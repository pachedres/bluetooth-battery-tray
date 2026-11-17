using BluetoothBatteryTray.Interop;

namespace BluetoothBatteryTray.Services;

/// <summary>
/// Battery level of devices connected through a proprietary 2.4 GHz USB receiver (not Bluetooth).
///
/// Each supported receiver model has its own report format, found with tools/hid-probe.ps1.
/// Receivers are only LISTENED to: no command is ever sent to the device.
/// </summary>
public sealed class ReceiverBatteryService : IDisposable
{
    /// <summary>A device is considered gone if its receiver stops reporting for this long.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    /// <summary>How often to look for newly plugged receivers and expire stale ones.</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    private readonly record struct Status(bool Online, bool Charging, int Percent);

    private sealed record Model(string Name, DeviceKind Kind, ushort VendorId, ushort ProductId, ushort UsagePage,
        Func<byte[], int, Status?> Parse);

    private static readonly Model[] Models =
    [
        // Attack Shark X11. The receiver's vendor collection (usage page 0x000A) sends every ~2 s:
        //   03 55 40 <status> <battery %>     status bit 0 = mouse online, bit 1 = charging (on the dock)
        new("Attack Shark X11", DeviceKind.Mouse, 0x1D57, 0xFA60, 0x000A, (b, n) =>
            n >= 5 && b[0] == 0x03 && b[1] == 0x55 && b[2] == 0x40 && b[4] <= 100
                ? new Status((b[3] & 0x01) != 0, (b[3] & 0x02) != 0, b[4])
                : null),
    ];

    private sealed class Listener(string path, Model model, FileStream stream)
    {
        public string Path { get; } = path;
        public Model Model { get; } = model;
        public FileStream Stream { get; } = stream;
        public CancellationTokenSource Cts { get; } = new();
        public Status? Last { get; set; }
        public DateTime LastAt { get; set; }
        public bool Ended { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Listener> _listeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private string _lastSnapshot = "";
    private bool _disposed;

    /// <summary>Raised (from a background thread) when a device appears, disappears or its level/charging state changes.</summary>
    public event Action? Changed;

    public ReceiverBatteryService()
    {
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, ScanInterval);
    }

    public IReadOnlyList<DeviceBattery> GetDevices()
    {
        lock (_gate) return Snapshot();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var listener in _listeners.Values)
            {
                listener.Cts.Cancel();
                listener.Stream.Dispose();
            }
            _listeners.Clear();
        }
        _timer.Dispose();
    }

    private void Tick()
    {
        try { Scan(); }
        catch (Exception ex) { Log.Error("Receiver scan failed", ex); }
        RaiseIfChanged();
    }

    private void Scan()
    {
        foreach (var collection in Hid.Enumerate(Models.Select(m => (m.VendorId, m.ProductId))))
        {
            var model = Models.FirstOrDefault(m =>
                m.VendorId == collection.VendorId && m.ProductId == collection.ProductId && m.UsagePage == collection.UsagePage);
            if (model is null || collection.InputReportLength <= 0) continue;

            lock (_gate)
            {
                if (_disposed) return;
                if (_listeners.TryGetValue(collection.Path, out var existing) && !existing.Ended) continue;
            }

            var handle = Hid.OpenForRead(collection.Path);
            if (handle is null) continue;

            var listener = new Listener(collection.Path, model, new FileStream(handle, FileAccess.Read, collection.InputReportLength, isAsync: true));
            lock (_gate) _listeners[collection.Path] = listener;
            Log.Info($"Listening to {model.Name} receiver");
            _ = Task.Run(() => ListenAsync(listener, collection.InputReportLength));
        }

        lock (_gate)
        {
            foreach (var key in _listeners.Where(kv => kv.Value.Ended).Select(kv => kv.Key).ToList())
                _listeners.Remove(key);
        }
    }

    private async Task ListenAsync(Listener listener, int length)
    {
        var buffer = new byte[length];
        try
        {
            while (!listener.Cts.IsCancellationRequested)
            {
                var read = await listener.Stream.ReadAsync(buffer.AsMemory(0, length), listener.Cts.Token);
                if (read <= 0) break;
                if (listener.Model.Parse(buffer, read) is not { } status) continue;

                lock (_gate)
                {
                    listener.Last = status;
                    listener.LastAt = DateTime.UtcNow;
                }
                RaiseIfChanged();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            // Typically the receiver was unplugged.
            Log.Info($"{listener.Model.Name} receiver stopped: {ex.Message}");
        }
        finally
        {
            lock (_gate) listener.Ended = true;
            listener.Stream.Dispose();
            RaiseIfChanged();
        }
    }

    /// <summary>Raises <see cref="Changed"/> only when what the UI shows would actually change.</summary>
    private void RaiseIfChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var snapshot = string.Join(";", Snapshot());
            if (snapshot == _lastSnapshot) return;
            _lastSnapshot = snapshot;
        }
        Changed?.Invoke();
    }

    // Caller must hold _gate.
    private List<DeviceBattery> Snapshot()
    {
        var now = DateTime.UtcNow;
        return _listeners.Values
            .Where(l => !l.Ended && l.Last is { Online: true } && now - l.LastAt < StaleAfter)
            .Select(l => new DeviceBattery(l.Path, l.Model.Name, l.Model.Kind, l.Last!.Value.Percent, l.Last.Value.Charging))
            .ToList();
    }
}
