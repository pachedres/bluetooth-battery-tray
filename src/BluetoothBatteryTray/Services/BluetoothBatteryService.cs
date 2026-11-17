using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BluetoothBatteryTray.Services;

public enum DeviceKind { Generic, Headphones, Headset, Speaker, Mouse, Keyboard, Gamepad, Phone, Watch }

public sealed record DeviceBattery(string Id, string Name, DeviceKind Kind, int? Percent);

/// <summary>
/// Reads battery levels of connected Bluetooth devices.
///
/// 1. Classic Bluetooth (headphones, headsets...): Windows stores the level reported via
///    HFP in a PnP device property — the same value shown in Settings &gt; Bluetooth.
/// 2. Bluetooth LE (mice, keyboards...): same PnP property when available, otherwise
///    reads the standard GATT Battery Service (0x180F / 0x2A19).
/// </summary>
public sealed class BluetoothBatteryService : IDisposable
{
    // DEVPKEY_Bluetooth_Battery
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private const string ContainerIdKey = "System.Devices.Aep.ContainerId";
    private const string CodMajorKey = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string CodMinorKey = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string LeCategoryKey = "System.Devices.Aep.Bluetooth.Le.Appearance.Category";
    private const string LeSubcategoryKey = "System.Devices.Aep.Bluetooth.Le.Appearance.Subcategory";

    private static readonly string[] ClassicProps = [IsConnectedKey, ContainerIdKey, CodMajorKey, CodMinorKey];
    private static readonly string[] LeProps = [IsConnectedKey, ContainerIdKey, LeCategoryKey, LeSubcategoryKey];

    private static readonly TimeSpan GattTimeout = TimeSpan.FromSeconds(5);

    private readonly List<DeviceWatcher> _watchers = [];

    /// <summary>Raised (from a background thread) when a device connects, disconnects or is (un)paired.</summary>
    public event Action? DevicesChanged;

    public async Task<IReadOnlyList<DeviceBattery>> GetConnectedDevicesAsync()
    {
        // Keyed by container id so dual-mode devices (classic + LE) appear only once.
        var byContainer = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);

        var classic = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true), ClassicProps, DeviceInformationKind.AssociationEndpoint);

        foreach (var info in classic)
        {
            if (!IsConnected(info)) continue;
            var container = GetGuid(info, ContainerIdKey);
            var kind = KindFromClassOfDevice(GetInt(info, CodMajorKey), GetInt(info, CodMinorKey));
            var percent = container is Guid g ? await ReadPnpBatteryAsync(g) : null;
            Merge(byContainer, container?.ToString() ?? info.Id, new DeviceBattery(info.Id, info.Name, kind, percent));
        }

        var le = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), LeProps, DeviceInformationKind.AssociationEndpoint);

        foreach (var info in le)
        {
            if (!IsConnected(info)) continue;
            var container = GetGuid(info, ContainerIdKey);
            var key = container?.ToString() ?? info.Id;
            if (byContainer.TryGetValue(key, out var existing) && existing.Percent is not null) continue;

            var kind = KindFromAppearance(GetInt(info, LeCategoryKey), GetInt(info, LeSubcategoryKey));
            var percent = (container is Guid g ? await ReadPnpBatteryAsync(g) : null)
                          ?? await ReadGattBatteryAsync(info.Id);
            Merge(byContainer, key, new DeviceBattery(info.Id, info.Name, kind, percent));
        }

        return byContainer.Values.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Starts watching for connect/disconnect events (see <see cref="DevicesChanged"/>).</summary>
    public void StartWatching()
    {
        if (_watchers.Count > 0) return;
        _watchers.Add(CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true)));
        _watchers.Add(CreateWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)));
        foreach (var w in _watchers) w.Start();
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try
            {
                if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop();
            }
            catch { /* ignore */ }
        }
        _watchers.Clear();
    }

    private DeviceWatcher CreateWatcher(string selector)
    {
        var watcher = DeviceInformation.CreateWatcher(selector, [IsConnectedKey], DeviceInformationKind.AssociationEndpoint);
        watcher.Added += (_, _) => DevicesChanged?.Invoke();
        watcher.Removed += (_, _) => DevicesChanged?.Invoke();
        watcher.Updated += (_, update) =>
        {
            if (update.Properties.ContainsKey(IsConnectedKey)) DevicesChanged?.Invoke();
        };
        return watcher;
    }

    private static void Merge(Dictionary<string, DeviceBattery> map, string key, DeviceBattery device)
    {
        if (!map.TryGetValue(key, out var existing))
        {
            map[key] = device;
        }
        else if (existing.Percent is null && device.Percent is not null)
        {
            // Prefer the entry that has a battery level, but keep the more specific icon.
            map[key] = device.Kind == DeviceKind.Generic ? device with { Kind = existing.Kind } : device;
        }
    }

    private static async Task<int?> ReadPnpBatteryAsync(Guid containerId)
    {
        try
        {
            var aqs = $"System.Devices.ContainerId:=\"{{{containerId}}}\"";
            var nodes = await DeviceInformation.FindAllAsync(aqs, [BatteryKey], DeviceInformationKind.Device);
            foreach (var node in nodes)
            {
                if (node.Properties.TryGetValue(BatteryKey, out var value) && value is not null)
                {
                    var percent = Convert.ToInt32(value);
                    if (percent is >= 0 and <= 100) return percent;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"PnP battery read failed for container {containerId}", ex);
        }
        return null;
    }

    private static async Task<int?> ReadGattBatteryAsync(string deviceId)
    {
        using var cts = new CancellationTokenSource(GattTimeout);
        try
        {
            using var device = await BluetoothLEDevice.FromIdAsync(deviceId).AsTask(cts.Token);
            if (device is null) return null;

            var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(cts.Token);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) return null;

            try
            {
                var service = services.Services[0];
                var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached).AsTask(cts.Token);
                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return null;

                var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cts.Token);
                if (read.Status != GattCommunicationStatus.Success || read.Value.Length == 0) return null;

                int percent = DataReader.FromBuffer(read.Value).ReadByte();
                return percent is >= 0 and <= 100 ? percent : null;
            }
            finally
            {
                foreach (var s in services.Services) s.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info($"GATT battery read timed out for {deviceId}");
        }
        catch (Exception ex)
        {
            Log.Error($"GATT battery read failed for {deviceId}", ex);
        }
        return null;
    }

    // Bluetooth Class of Device (major / minor), see Bluetooth Assigned Numbers 2.8
    private static DeviceKind KindFromClassOfDevice(int? major, int? minor) => (major, minor) switch
    {
        (2, _) => DeviceKind.Phone,
        (4, 1 or 2) => DeviceKind.Headset,
        (4, 5 or 7) => DeviceKind.Speaker,
        (4, 6) => DeviceKind.Headphones,
        (4, _) => DeviceKind.Headphones,
        (5, int m) when (m & 0x0F) is 1 or 2 => DeviceKind.Gamepad,
        (5, int m) when (m >> 4) == 2 => DeviceKind.Mouse,
        (5, int m) when (m >> 4) is 1 or 3 => DeviceKind.Keyboard,
        (7, 1) => DeviceKind.Watch,
        _ => DeviceKind.Generic,
    };

    // Bluetooth LE GAP Appearance (category / subcategory), see Bluetooth Assigned Numbers 2.6
    private static DeviceKind KindFromAppearance(int? category, int? subcategory) => (category, subcategory) switch
    {
        (0x01, _) => DeviceKind.Phone,
        (0x03, _) => DeviceKind.Watch,
        (0x0F, 1) => DeviceKind.Keyboard,
        (0x0F, 2) => DeviceKind.Mouse,
        (0x0F, 3 or 4) => DeviceKind.Gamepad,
        (0x21, _) => DeviceKind.Speaker,
        (0x25, 2) => DeviceKind.Headset,
        (0x25, _) => DeviceKind.Headphones,
        _ => DeviceKind.Generic,
    };

    private static bool IsConnected(DeviceInformation info) =>
        info.Properties.TryGetValue(IsConnectedKey, out var v) && v is bool b && b;

    private static Guid? GetGuid(DeviceInformation info, string key) =>
        info.Properties.TryGetValue(key, out var v) && v is Guid g && g != Guid.Empty ? g : null;

    private static int? GetInt(DeviceInformation info, string key)
    {
        if (!info.Properties.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToInt32(v); } catch { return null; }
    }
}
