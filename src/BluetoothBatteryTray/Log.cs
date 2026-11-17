namespace BluetoothBatteryTray;

/// <summary>Tiny file logger (%AppData%\BluetoothBatteryTray\log.txt).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxSizeBytes = 1_000_000;

    public static string FilePath => Path.Combine(AppSettings.DirectoryPath, "log.txt");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DirectoryPath);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxSizeBytes) file.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
