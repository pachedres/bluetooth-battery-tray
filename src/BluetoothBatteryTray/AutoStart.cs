using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace BluetoothBatteryTray;

/// <summary>
/// Starts the app with Windows through a per-user Task Scheduler task with a logon trigger.
///
/// A scheduled task is used instead of the classic HKCU\...\Run entry because the task can be
/// delayed a few seconds after logon. Right at logon, endpoint security products may block unknown
/// executables and the Bluetooth stack is still starting, so a Run entry could silently fail.
/// No administrator rights are needed: the task runs as the current user, unelevated.
/// </summary>
internal static class AutoStart
{
    private const string TaskName = "BluetoothBatteryTray";
    private const string StartDelay = "PT10S"; // ISO 8601 duration

    // Previous versions used the Run key; it is migrated/cleaned up automatically.
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "BluetoothBatteryTray";

    public static bool IsEnabled => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static void Set(bool enabled)
    {
        RemoveLegacyRunEntry();
        if (enabled)
            CreateTask();
        else if (IsEnabled)
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
    }

    /// <summary>Converts a Run-key entry from older versions into the scheduled task.</summary>
    public static void MigrateLegacyRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey);
        if (key?.GetValue(LegacyValueName) is not string) return;

        Log.Info("Migrating auto start from Run key to scheduled task");
        Set(true);
    }

    private static void CreateTask()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path"));

        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Bluetooth Battery Tray when you sign in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>{StartDelay}</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                </Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), $"BluetoothBatteryTray-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            var exit = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (exit != 0) throw new InvalidOperationException($"schtasks /Create failed with exit code {exit}");
        }
        finally
        {
            try { File.Delete(file); } catch { /* ignore */ }
        }
    }

    private static void RemoveLegacyRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
        key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    private static int RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
