using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BluetoothBatteryTray.Interop;
using BluetoothBatteryTray.Services;
using static BluetoothBatteryTray.Interop.NativeMethods;

namespace BluetoothBatteryTray;

/// <summary>
/// Borderless, transparent, always-on-top strip that sits on the taskbar, just left of
/// the notification area (Windows 11 no longer supports real taskbar toolbars/deskbands).
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly BluetoothBatteryService _battery = new();
    private readonly MediaService _media = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _deviceDebounce;
    private readonly DispatcherTimer _settingsDebounce;
    private FileSystemWatcher? _settingsWatcher;

    private AppSettings _settings;
    private IReadOnlyList<DeviceBattery> _devices = [];
    private NowPlaying? _nowPlaying;
    private IntPtr _hwnd;
    private bool _lightTheme;
    private bool _refreshing;
    private bool _refreshPending;
    private string _lastLoggedDevices = "";
    private string _lastRenderKey = "";
    private IntPtr _foregroundHook;
    private WinEventProc? _foregroundProc; // kept in a field so the GC doesn't collect the callback

    public OverlayWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _lightTheme = Taskbar.UsesLightTheme();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _positionTimer.Tick += (_, _) => { CheckTheme(); Reposition(); };

        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += async (_, _) => await RefreshDevicesAsync();

        _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _deviceDebounce.Tick += async (_, _) => { _deviceDebounce.Stop(); await RefreshDevicesAsync(); };

        _settingsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _settingsDebounce.Tick += (_, _) => { _settingsDebounce.Stop(); ReloadSettings(); };

        _battery.DevicesChanged += () => Dispatcher.InvokeAsync(() => Restart(_deviceDebounce));
        _media.Changed += np => Dispatcher.InvokeAsync(() => { _nowPlaying = np; Render(); });

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => Reposition();
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
        Closed += (_, _) => Cleanup();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: no Alt+Tab entry. No-activate: clicking it never steals focus.
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Render();
        Reposition();
        _positionTimer.Start();

        // React immediately when another window comes to the front (the taskbar may have been
        // raised above us), instead of waiting for the next timer tick.
        _foregroundProc = (_, _, _, _, _, _, _) => Reposition();
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        ApplySettings();
        WatchSettingsFile();

        try { _battery.StartWatching(); }
        catch (Exception ex) { Log.Error("Could not start device watcher", ex); }

        await RefreshDevicesAsync();
    }

    // ---------------------------------------------------------------- data

    private async Task RefreshDevicesAsync()
    {
        if (_refreshing) { _refreshPending = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                var all = await _battery.GetConnectedDevicesAsync();

                var summary = string.Join(", ", all.Select(d => $"{d.Name} [{d.Kind}] {d.Percent?.ToString() ?? "n/a"}"));
                if (summary != _lastLoggedDevices)
                {
                    Log.Info($"Devices: {(summary.Length == 0 ? "(none)" : summary)}");
                    _lastLoggedDevices = summary;
                }

                _devices = all
                    .Where(d => d.Percent is not null || _settings.ShowDevicesWithoutBattery)
                    .Where(d => !_settings.HiddenDevices.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                Render();
            } while (_refreshPending);
        }
        catch (Exception ex)
        {
            Log.Error("Device refresh failed", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplySettings()
    {
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(10, _settings.RefreshIntervalSeconds));
        _refreshTimer.Start();

        _media.PreferredApp = _settings.PreferredMediaApp ?? "";
        _media.OnlyPreferredApp = _settings.OnlyPreferredMediaApp;
        if (_settings.ShowMedia) _ = _media.StartAsync();
        else if (_media.IsRunning) _media.Stop();
    }

    private void ReloadSettings()
    {
        _settings = AppSettings.Load();
        ApplySettings();
        _ = RefreshDevicesAsync();
    }

    private void WatchSettingsFile()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DirectoryPath);
            _settingsWatcher = new FileSystemWatcher(AppSettings.DirectoryPath, Path.GetFileName(AppSettings.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            FileSystemEventHandler onChange = (_, _) => Dispatcher.InvokeAsync(() => Restart(_settingsDebounce));
            _settingsWatcher.Changed += onChange;
            _settingsWatcher.Created += onChange;
            _settingsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not watch settings file", ex);
        }
    }

    // ---------------------------------------------------------------- UI

    private void Render()
    {
        // Rebuilding the UI is visible (hover state resets, layout jumps), so only do it when something changed.
        var key = string.Join("|",
            _lightTheme, _settings.ShowMedia, _settings.HideMediaWhenPaused, _settings.MaxMediaChars, _settings.LowBatteryThreshold,
            string.Join(";", _devices), _nowPlaying);
        if (key == _lastRenderKey) return;
        _lastRenderKey = key;

        Foreground = _lightTheme ? Brushes.Black : Brushes.White;
        var lowBrush = new SolidColorBrush(_lightTheme ? Color.FromRgb(0xC4, 0x2B, 0x1C) : Color.FromRgb(0xFF, 0x6B, 0x6B));

        Items.Children.Clear();

        foreach (var device in _devices)
        {
            var text = device.Percent is int p ? $"{p}%" : "—";
            var isLow = device.Percent is int pct && pct <= _settings.LowBatteryThreshold;
            Items.Children.Add(CreateItem(Glyphs.For(device.Kind), text, $"{device.Name}: {text}", isLow ? lowBrush : null));
        }

        if (_settings.ShowMedia && _nowPlaying is { } np && (np.IsPlaying || !_settings.HideMediaWhenPaused))
        {
            var tooltip = np.IsPlaying ? np.DisplayText : $"{np.DisplayText} ({L.Paused})";
            var item = CreateItem(np.IsPlaying ? Glyphs.Music : Glyphs.Pause,
                Truncate(np.DisplayText, _settings.MaxMediaChars), tooltip, null);
            if (!np.IsPlaying) item.Opacity = 0.6;
            item.Cursor = Cursors.Hand;
            item.MouseLeftButtonUp += async (_, _) => await _media.TogglePlayPauseAsync();
            Items.Children.Add(item);
        }

        if (Items.Children.Count == 0)
        {
            var empty = CreateItem(Glyphs.Bluetooth, "", L.NoDevices, null);
            empty.Opacity = 0.5;
            Items.Children.Add(empty);
        }
    }

    private Border CreateItem(string glyph, string text, string tooltip, Brush? textBrush)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(text))
        {
            var label = new TextBlock
            {
                Text = text,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (textBrush is not null) label.Foreground = textBrush;
            panel.Children.Add(label);
        }

        var hover = new SolidColorBrush(_lightTheme ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            ToolTip = tooltip,
        };
        border.MouseEnter += (_, _) => border.Background = hover;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        return border;
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu();

        if (_nowPlaying is not null)
        {
            menu.Items.Add(MenuItem(L.PlayPause, () => _ = _media.TogglePlayPauseAsync()));
            menu.Items.Add(MenuItem(L.Next, () => _ = _media.NextAsync()));
            menu.Items.Add(MenuItem(L.Previous, () => _ = _media.PreviousAsync()));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(MenuItem(L.Refresh, () => _ = RefreshDevicesAsync()));
        menu.Items.Add(MenuItem(L.ShowMedia, () =>
        {
            _settings.ShowMedia = !_settings.ShowMedia;
            _settings.Save();
            ApplySettings();
            Render();
        }, _settings.ShowMedia));
        menu.Items.Add(MenuItem(L.StartWithWindows, () =>
        {
            try { AutoStart.Set(!AutoStart.IsEnabled); }
            catch (Exception ex) { Log.Error("Could not change auto start", ex); }
        }, AutoStart.IsEnabled));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L.OpenSettings, () => OpenFile(AppSettings.FilePath)));
        menu.Items.Add(MenuItem(L.OpenLog, () => OpenFile(Log.FilePath)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L.Exit, Close));

        // Required so the menu closes when clicking elsewhere (our window never gets activated otherwise).
        SetForegroundWindow(_hwnd);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, Action onClick, bool? isChecked = null)
    {
        var item = new MenuItem { Header = header };
        if (isChecked is bool c)
        {
            item.IsCheckable = true;
            item.IsChecked = c;
        }
        item.Click += (_, _) => onClick();
        return item;
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }

    // ---------------------------------------------------------------- positioning

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;

        // Get out of the way of full-screen games, videos and presentations.
        if (Taskbar.IsFullScreenAppRunning())
        {
            if (Visibility == Visibility.Visible) Visibility = Visibility.Hidden;
            return;
        }
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;

        if (!Taskbar.TryGetLayout(out var taskbar, out var notifyLeft)) return;
        if (taskbar.Height > taskbar.Width) return; // vertical taskbar: not supported

        var dpi = VisualTreeHelper.GetDpi(this);
        var heightDip = taskbar.Height / dpi.DpiScaleY;
        if (Math.Abs(Height - heightDip) > 0.5) Height = heightDip;

        if (!GetWindowRect(_hwnd, out var self)) return;

        var right = _settings.RightOffsetPx is int offset ? taskbar.Right - offset : notifyLeft - _settings.MarginPx;
        var x = right - self.Width;
        var y = taskbar.Top + (taskbar.Height - self.Height) / 2;

        // Re-assert topmost every tick: clicking the taskbar raises it above us.
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void CheckTheme()
    {
        var light = Taskbar.UsesLightTheme();
        if (light == _lightTheme) return;
        _lightTheme = light;
        Render();
    }

    // ---------------------------------------------------------------- helpers

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private static string Truncate(string text, int max) =>
        max > 1 && text.Length > max ? text[..(max - 1)].TrimEnd() + "…" : text;

    private void Cleanup()
    {
        _positionTimer.Stop();
        _refreshTimer.Stop();
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _settingsWatcher?.Dispose();
        _battery.Dispose();
        _media.Dispose();
    }
}
