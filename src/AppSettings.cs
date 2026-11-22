using System.Text.Json;
using System.Text.Json.Nodes;
using Chassis;

namespace ScreenShepherd;

/// <summary>
/// Screen Shepherd's own settings, kept in Chassis's settings file (the "app" part of settings.json). This record is a copy
/// the worker threads can read safely; MainWindow refreshes it whenever the page saves.
/// </summary>
public sealed record AppSettings(
    bool AutoRescueNewWindows = true,
    bool AutoRescueDisplayChanges = true,
    bool StartMinimized = false,
    bool PutBack = true,
    int PutBackDelaySeconds = 3,
    bool NotifyDrops = true,
    int DropGapMinutes = 10,
    int LayoutDays = 7,
    bool ReadDdc = true)
{
    public const string NewWindowsKey = "autoRescueNewWindows";
    public const string DisplayChangesKey = "autoRescueDisplayChanges";
    public const string StartMinimizedKey = "startMinimized";
    public const string PutBackKey = "putBack";
    public const string PutBackDelayKey = "putBackDelaySeconds";
    public const string NotifyDropsKey = "notifyDrops";
    public const string DropGapKey = "dropGapMinutes";
    public const string LayoutDaysKey = "layoutDays";
    public const string ReadDdcKey = "readDdc";

    public static AppSettings From(ChassisSettings settings) => new(
        settings.Get(NewWindowsKey, true),
        settings.Get(DisplayChangesKey, true),
        settings.Get(StartMinimizedKey, false),
        settings.Get(PutBackKey, true),
        Math.Clamp(settings.Get(PutBackDelayKey, 3), 0, 30),
        settings.Get(NotifyDropsKey, true),
        Math.Clamp(settings.Get(DropGapKey, 10), 1, 120),
        Math.Clamp(settings.Get(LayoutDaysKey, 7), 1, 90),
        settings.Get(ReadDdcKey, true));
}

/// <summary>
/// Screen Shepherd 1.6 and older kept settings in %LOCALAPPDATA%\ScreenShepherd\settings.json. The first run on
/// Chassis copies them into the new file once; the old folder is left as it is.
/// </summary>
internal static class LegacySettings
{
    internal static string LegacyFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenShepherd", "settings.json");

    internal static string NewFile(string appName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName, "settings.json");

    /// <summary>The old settings as Chassis settings JSON, or null when the old text can't be read.</summary>
    internal static string? ToChassis(string legacyJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(legacyJson);
            var old = doc.RootElement;
            if (old.ValueKind != JsonValueKind.Object) return null;
            bool Flag(string name, bool fallback) =>
                old.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
            double Number(string name, double fallback) =>
                old.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
            var chassis = new JsonObject
            {
                ["glass"] = Flag("glassEnabled", true),
                ["opacity"] = Math.Clamp(Number("glassOpacity", ChassisSettings.DefaultOpacity), 0.25, 0.95),
                ["blur"] = Math.Clamp(Number("glassBlur", ChassisSettings.DefaultBlur), 0, 40),
                ["closeToTray"] = Flag("closeToTray", true),
                ["app"] = new JsonObject
                {
                    [AppSettings.NewWindowsKey] = Flag("autoRescueNewWindows", true),
                    [AppSettings.DisplayChangesKey] = Flag("autoRescueDisplayChanges", true),
                    [AppSettings.StartMinimizedKey] = Flag("startMinimized", false)
                }
            };
            return chassis.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Copies the old settings into the new file when the new one doesn't exist yet. True when it did.</summary>
    internal static bool ImportOnce(string newFile, string legacyFile)
    {
        try
        {
            if (File.Exists(newFile) || !File.Exists(legacyFile)) return false;
            var converted = ToChassis(File.ReadAllText(legacyFile));
            if (converted is null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
            File.WriteAllText(newFile, converted);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Defaults are fine; the old file is still there.
            return false;
        }
    }
}
