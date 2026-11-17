using BluetoothBatteryTray.Services;

namespace BluetoothBatteryTray;

/// <summary>Icon code points from Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10).</summary>
internal static class Glyphs
{
    public const string Bluetooth = "";
    public const string Headphones = "";
    public const string Headset = "";
    public const string Speaker = "";
    public const string Mouse = "";
    public const string Keyboard = "";
    public const string Gamepad = "";
    public const string Phone = "";
    public const string Watch = "";
    public const string Music = "";
    public const string Pause = "";

    public static string For(DeviceKind kind) => kind switch
    {
        DeviceKind.Headphones => Headphones,
        DeviceKind.Headset => Headset,
        DeviceKind.Speaker => Speaker,
        DeviceKind.Mouse => Mouse,
        DeviceKind.Keyboard => Keyboard,
        DeviceKind.Gamepad => Gamepad,
        DeviceKind.Phone => Phone,
        DeviceKind.Watch => Watch,
        _ => Bluetooth,
    };
}
