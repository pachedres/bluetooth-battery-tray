using Windows.Media.Control;
using MediaManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace BluetoothBatteryTray.Services;

public sealed record NowPlaying(string Title, string Artist, bool IsPlaying, string AppId)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} – {Title}";
}

/// <summary>
/// Reads "now playing" info through Windows' System Media Transport Controls —
/// the same source used by the volume flyout. Works with Spotify (desktop and Store)
/// and any other player, no Spotify API key or login needed.
/// </summary>
public sealed class MediaService : IDisposable
{
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly List<MediaSession> _hooked = [];
    private MediaManager? _manager;
    private MediaSession? _active;

    public string PreferredApp { get; set; } = "Spotify";
    public bool OnlyPreferredApp { get; set; }

    /// <summary>Raised (from a background thread) whenever the song or playback state changes. Null = nothing playing.</summary>
    public event Action<NowPlaying?>? Changed;

    public bool IsRunning => _manager is not null;

    public async Task StartAsync()
    {
        try
        {
            if (_manager is null)
            {
                _manager = await MediaManager.RequestAsync();
                _manager.SessionsChanged += OnSessionsChanged;
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                HookSessions();
            }
            await UpdateAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Could not start media service", ex);
        }
    }

    public void Stop()
    {
        UnhookSessions();
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
        }
        _active = null;
        Changed?.Invoke(null);
    }

    public Task TogglePlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync() => Run(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => Run(s => s.TrySkipPreviousAsync().AsTask());

    public void Dispose()
    {
        Stop();
        _updateLock.Dispose();
    }

    private async Task Run(Func<MediaSession, Task<bool>> action)
    {
        if (_active is null) return;
        try { await action(_active); }
        catch (Exception ex) { Log.Error("Media command failed", ex); }
    }

    private void OnSessionsChanged(MediaManager sender, SessionsChangedEventArgs args)
    {
        HookSessions();
        _ = UpdateAsync();
    }

    private void OnCurrentSessionChanged(MediaManager sender, CurrentSessionChangedEventArgs args) => _ = UpdateAsync();
    private void OnMediaPropertiesChanged(MediaSession sender, MediaPropertiesChangedEventArgs args) => _ = UpdateAsync();
    private void OnPlaybackInfoChanged(MediaSession sender, PlaybackInfoChangedEventArgs args) => _ = UpdateAsync();

    private void HookSessions()
    {
        UnhookSessions();
        if (_manager is null) return;
        foreach (var session in _manager.GetSessions())
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _hooked.Add(session);
        }
    }

    private void UnhookSessions()
    {
        foreach (var session in _hooked)
        {
            try
            {
                session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            catch { /* session may already be gone */ }
        }
        _hooked.Clear();
    }

    private async Task UpdateAsync()
    {
        await _updateLock.WaitAsync();
        try
        {
            var manager = _manager;
            if (manager is null) return;

            MediaSession? session = null;
            if (!string.IsNullOrWhiteSpace(PreferredApp))
            {
                session = manager.GetSessions().FirstOrDefault(s =>
                    s.SourceAppUserModelId?.Contains(PreferredApp, StringComparison.OrdinalIgnoreCase) == true);
            }
            if (session is null && !OnlyPreferredApp)
                session = manager.GetCurrentSession();

            _active = session;
            if (session is null)
            {
                Changed?.Invoke(null);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            if (props is null || string.IsNullOrWhiteSpace(props.Title))
            {
                Changed?.Invoke(null);
                return;
            }

            var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Changed?.Invoke(new NowPlaying(props.Title.Trim(), props.Artist?.Trim() ?? "", isPlaying, session.SourceAppUserModelId ?? ""));
        }
        catch (Exception ex)
        {
            // Sessions disappear all the time (app closed, tab closed...). Not worth crashing over.
            Log.Error("Media update failed", ex);
        }
        finally
        {
            _updateLock.Release();
        }
    }
}
