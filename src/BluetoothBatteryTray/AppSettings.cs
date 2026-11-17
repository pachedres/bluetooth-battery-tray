using System.Text.Json;
using System.Text.Json.Serialization;

namespace BluetoothBatteryTray;

/// <summary>User settings stored as JSON in %AppData%\BluetoothBatteryTray\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>How often battery levels are polled (seconds, minimum 10).</summary>
    public int RefreshIntervalSeconds { get; set; } = 60;

    /// <summary>Percentages at or below this value are shown in red.</summary>
    public int LowBatteryThreshold { get; set; } = 20;

    /// <summary>Show connected devices that don't report a battery level (shown as "—").</summary>
    public bool ShowDevicesWithoutBattery { get; set; }

    /// <summary>Device names to hide (case-insensitive, exact match).</summary>
    public List<string> HiddenDevices { get; set; } = [];

    /// <summary>Show the song currently playing.</summary>
    public bool ShowMedia { get; set; } = true;

    /// <summary>Media app to prefer (matched against the app id). Empty = whatever Windows considers current.</summary>
    public string PreferredMediaApp { get; set; } = "Spotify";

    /// <summary>If true, only the preferred app is shown; other players are ignored.</summary>
    public bool OnlyPreferredMediaApp { get; set; }

    /// <summary>Hide the song when playback is paused.</summary>
    public bool HideMediaWhenPaused { get; set; }

    /// <summary>Maximum number of characters for the "Artist – Title" label.</summary>
    public int MaxMediaChars { get; set; } = 40;

    /// <summary>
    /// Optional manual position: distance in physical pixels from the right edge of the taskbar.
    /// Leave null to place the overlay automatically next to the notification area.
    /// </summary>
    public int? RightOffsetPx { get; set; }

    /// <summary>Gap in pixels between the overlay and the notification area.</summary>
    public int MarginPx { get; set; } = 4;

    [JsonIgnore]
    public static string DirectoryPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BluetoothBatteryTray");

    [JsonIgnore]
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            // Don't overwrite a file the user is editing: just fall back to defaults in memory.
            Log.Error("Could not read settings, using defaults", ex);
            return new AppSettings();
        }

        var settings = new AppSettings();
        settings.Save();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}
