using Chassis;

namespace ScreenShepherd;

internal static class Program
{
    // The display name: the tray menu, the pop-out title, the data folder (%LOCALAPPDATA%\Screen Shepherd) and the page host.
    internal const string AppName = "Screen Shepherd";

    /// <summary>
    /// Chassis runs the shell: --selftest, --qa DIR, --snapshot PNG [--solid] [--panel settings|deps|help|diag] [--width/--height]
    /// [--query "view=health&amp;select=1&amp;stray=1&amp;redact=1&amp;hide=a,b"] [--data DIR], --exit (ask the running copy to quit),
    /// otherwise the single-instance window with its tray icon (--hidden or --minimized starts it in the tray).
    /// Screen Shepherd's own: --smoke and --integration-test, each with --receipt FILE.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (Has(args, "--smoke")) return TestHarness.RunSmoke(args);
        if (Has(args, "--integration-test")) return TestHarness.RunLiveMoveTest(args);

        // Settings from 1.6 and older move over once. Test runs (--data) never touch the real files.
        if (!Has(args, "--data") && LegacySettings.ImportOnce(LegacySettings.NewFile(AppName), LegacySettings.LegacyFile))
            _importedLegacySettings = true;

        return ChassisApp.Run(args, new ChassisOptions
        {
            AppName = AppName,
            Preset = "aurora",
            Width = 1380,
            Height = 900,
            MinWidth = 1020,
            MinHeight = 680,
            PageColor = "#0d1410",
            KitNotes = { ["gleam"] = "The version button's orb and its editor (right-click the button)." },
            Dependencies = WindowsParts.ForChassis(),
            SelfTest = AppChecks.Run,
            // extra --qa shots: the Health view, and a selected window with a labelled made-up stray
            QaShots =
            {
                ("health", new[] { "--query", "view=health" }),
                ("desk-select", new[] { "--query", "select=1&stray=1" })
            }
        }, () => new MainWindow());
    }

    private static bool _importedLegacySettings;
    /// <summary>True when this run copied the 1.6 settings over (logged once the log is open).</summary>
    internal static bool ImportedLegacySettings => _importedLegacySettings;

    internal static bool Has(string[] args, string name) => args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));

    internal static void TryWriteReceipt(string path, object value)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(value, AppHostService.JsonOptions));
        }
        catch
        {
            // A receipt failure must not hide the actual application or test result.
        }
    }
}

/// <summary>Screen Shepherd's own self-test checks (run after Chassis's by --selftest). No window is moved.</summary>
internal static class AppChecks
{
    internal static void Run(ChassisCheck c)
    {
        c.That(WindowService.ParseHandle(WindowService.ToHandleString(new nint(0x1234))) == new nint(0x1234), "window handles survive the trip to the page and back");
        c.NoThrow(() =>
        {
            using var window = IconBuilder.Window();
            using var tray = IconBuilder.Tray();
            if (window.Width <= 0 || tray.Size != System.Windows.Forms.SystemInformation.SmallIconSize) throw new InvalidOperationException("wrong icon size");
        }, "the embedded icon loads at the window size and the tray's small size");
        c.That(new WindowService().GetMonitors().Count > 0, "Windows reports at least one display");

        var defaults = AppSettings.From(new ChassisSettings());
        c.That(defaults is { AutoRescueNewWindows: true, AutoRescueDisplayChanges: true, StartMinimized: false }, "both rescues are on and start hidden is off by default");

        var moved = LegacySettings.ToChassis("""{"autoRescueNewWindows":false,"autoRescueDisplayChanges":true,"startMinimized":true,"closeToTray":false,"glassEnabled":false,"glassOpacity":0.52,"glassBlur":16}""");
        var temp = Path.Combine(Path.GetTempPath(), "screenshepherd-selftest-" + Environment.ProcessId);
        Directory.CreateDirectory(temp);
        try
        {
            var legacy = Path.Combine(temp, "old.json");
            var target = Path.Combine(temp, "new", "settings.json");
            File.WriteAllText(legacy, """{"autoRescueNewWindows":false,"startMinimized":true,"closeToTray":false,"glassEnabled":false,"glassOpacity":0.52,"glassBlur":16}""");
            c.That(moved is not null && LegacySettings.ImportOnce(target, legacy), "settings from 1.6 are copied over");
            var loaded = ChassisSettings.Load(target);
            var app = AppSettings.From(loaded);
            c.That(!loaded.Glass && !loaded.CloseToTray && Math.Abs(loaded.Opacity - 0.52) < 1e-9 && loaded.Blur == 16
                   && app is { AutoRescueNewWindows: false, AutoRescueDisplayChanges: true, StartMinimized: true }, "copied settings keep their values");
            File.WriteAllText(legacy, """{"autoRescueNewWindows":true}""");
            c.That(!LegacySettings.ImportOnce(target, legacy) && !AppSettings.From(ChassisSettings.Load(target)).AutoRescueNewWindows, "the copy happens once and never overwrites newer settings");
            c.That(LegacySettings.ToChassis("{ not json") is null && LegacySettings.ToChassis("[1]") is null, "unreadable old settings are left alone");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }

        var plain = SnapshotOptions.From("C:\\x.png", null);
        var shared = SnapshotOptions.From("C:\\x.png", "view=health&hide=alpha,%20beta&stray=1");
        c.That(plain is { Redact: false, SampleStray: false } && plain.Hidden.Count == 0, "a plain snapshot shows the real state");
        c.That(shared is { Redact: true, SampleStray: true } && shared.Hidden.SequenceEqual(new[] { "alpha", "beta" }), "hiding a process also redacts titles and serials");
        c.That(SnapshotOptions.From(null, "redact=1&stray=1") is { Redact: false, SampleStray: false }, "the made-up stray and redaction exist only in snapshot runs");
        c.That(MonitorInventoryService.FormatConnection(15) == "Miracast" && MonitorInventoryService.FormatConnection(14) == "SDTV dongle"
               && MonitorInventoryService.FormatConnection(16) == "USB or indirect display" && MonitorInventoryService.FormatConnection(17) == "Virtual display"
               && MonitorInventoryService.FormatConnection(18) == "DisplayPort over USB-C" && MonitorInventoryService.FormatConnection(5) == "HDMI",
               "connector types use Windows' numbering (15 is Miracast)");
        c.That(WindowsParts.ForChassis().Count == 12, "every Windows part the app reads is on the Dependencies tab");
    }
}
