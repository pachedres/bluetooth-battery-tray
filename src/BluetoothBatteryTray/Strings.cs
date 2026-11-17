using System.Globalization;

namespace BluetoothBatteryTray;

/// <summary>UI strings (Portuguese when the system language is pt-*, English otherwise).</summary>
internal static class L
{
    private static readonly bool Pt = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";

    public static string Refresh => Pt ? "Atualizar agora" : "Refresh now";
    public static string ShowMedia => Pt ? "Mostrar música tocando" : "Show now playing";
    public static string StartWithWindows => Pt ? "Iniciar com o Windows" : "Start with Windows";
    public static string OpenSettings => Pt ? "Abrir configurações..." : "Open settings...";
    public static string OpenLog => Pt ? "Abrir log..." : "Open log...";
    public static string Exit => Pt ? "Sair" : "Exit";
    public static string PlayPause => Pt ? "Tocar / Pausar" : "Play / Pause";
    public static string Next => Pt ? "Próxima" : "Next";
    public static string Previous => Pt ? "Anterior" : "Previous";
    public static string Paused => Pt ? "Pausado" : "Paused";
    public static string Charging => Pt ? "carregando" : "charging";
    public static string NoDevices => Pt
        ? "Nenhum dispositivo Bluetooth com bateria conectado"
        : "No connected Bluetooth device is reporting battery";
}
