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
        c.That(WindowsParts.ForChassis().Count == 16, "every Windows part the app reads is on the Dependencies tab");

        // 1.8: displays that come and go, full EDID, DDC/CI.
        c.That(defaults is { PutBack: true, PutBackDelaySeconds: 3, NotifyDrops: true, DropGapMinutes: 10, LayoutDays: 7, ReadDdc: true },
            "put-back, drop notices and DDC/CI reads are on by default; a gap under 10 minutes is a drop");
        var parsed = EdidDetails.Parse(SyntheticEdid(), "test");
        c.That(parsed is { ExtensionBlocks: 1, MaxPixelClockMHz: 600, MinVerticalHz: 48, MaxVerticalHz: 144, HdmiMaxTmdsMHz: 300 } && Math.Abs(parsed.PreferredPixelClockMHz - 148.5) < 0.01,
            "the EDID's range limits, preferred pixel clock and HDMI TMDS limit are read");
        c.That(parsed.HdrTransfer.Contains("HDR10") && parsed.HdrMaxNits == 400 && parsed.HdrMaxAverageNits > 0, "the CTA-861 HDR block is read");
        c.That(parsed.SrgbCoveragePercent >= 99 && parsed.DciP3CoveragePercent is > 70 and < 78, "an sRGB panel covers all of sRGB and about three quarters of DCI-P3");
        c.That(Math.Abs(EdidDetails.Coverage(EdidDetails.Srgb, EdidDetails.Srgb) - 1) < 1e-9 && EdidDetails.Parse(new byte[10], "x") == EdidDetails.Empty,
            "gamut coverage is exact for identical triangles, and a short EDID reads as empty");
        c.That(MonitorControlService.ParseInputChoices("(prot(monitor)type(lcd)vcp(02 04 10 12 14(05 08 0B) 60(0F 11 12) D6(01 04)))").SequenceEqual(new[] { 0x0F, 0x11, 0x12 })
               && MonitorControlService.ParseInputChoices("vcp(10 12)").Count == 0 && MonitorControlService.InputName(0x11) == "HDMI 1",
            "a monitor's input list is read from its DDC/CI capabilities string");
        c.That(DisplayWatch.Span(0.4) == "under a minute" && DisplayWatch.Span(7) == "7 min" && DisplayWatch.Span(123) == "2 h 03 m" && DisplayWatch.Span(1500) == "1 d 1 h",
            "durations read as words");
        c.That(AppHostService.Near(new RectSnapshot(0, 0, 800, 600), new RectSnapshot(20, -20, 810, 590)) && !AppHostService.Near(new RectSnapshot(0, 0, 800, 600), new RectSnapshot(100, 0, 800, 600)),
            "a window within 24 pixels of where Windows put it counts as not moved since");
        c.NoThrow(WatchRoundTrip, "a display that drops and returns is recorded as a drop, and its window is parked for the return");
    }

    private static void WatchRoundTrip()
    {
        var folder = Path.Combine(Path.GetTempPath(), "screenshepherd-watch-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        try
        {
            var settings = new AppSettings();
            var a = TestMonitor(@"\\.\DISPLAY1", 0, "A1");
            var b = TestMonitor(@"\\.\DISPLAY2", 1920, "B2");
            var onB = new WindowSnapshot("0x10", 1, "player", "Film", new RectSnapshot(2000, 100, 800, 600), b.DeviceName, b.DisplayName, b.DeviceName, b.DisplayName, "visible", 100, false, false, true, false);
            var landed = onB with { Bounds = new RectSnapshot(80, 100, 800, 600), MonitorDevice = a.DeviceName, NearestMonitorDevice = a.DeviceName };
            var watch = new DisplayWatch(folder, readOnly: false);
            watch.Observe(new[] { a, b }, new[] { onB }, settings);
            if (watch.Sample(new[] { a, b }, new[] { onB })) throw new InvalidOperationException("an unchanged desk reported a change");
            if (!watch.Sample(new[] { a }, new[] { landed })) throw new InvalidOperationException("a display going away was not noticed");
            var gone = watch.Observe(new[] { a }, new[] { landed }, settings);
            if (gone.Gone is not [{ Windows: 1 }]) throw new InvalidOperationException("the window on the display that went away was not parked");
            if (watch.GetAway(settings) is not [{ Windows.Count: 1 }]) throw new InvalidOperationException("the away pen is missing");
            var back = watch.Observe(new[] { a, b }, new[] { landed }, settings);
            if (back.Back is not [{ Drop: true, Parked.Windows.Count: 1 }]) throw new InvalidOperationException("the return was not a drop with a parked layout");
            watch.PutBackDone(back.Back[0].Key, 1);
            if (watch.GetHistory(settings).DropsToday != 1 || watch.GetAway(settings).Count != 0) throw new InvalidOperationException("the history or away list is wrong");
            if (new DisplayWatch(folder, readOnly: true).GetHistory(settings).Events.Count != 2) throw new InvalidOperationException("history.json did not round-trip");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static MonitorSnapshot TestMonitor(string device, int x, string serial) =>
        new MonitorSnapshot(1, device, device, x == 0, new RectSnapshot(x, 0, 1920, 1080), new RectSnapshot(x, 0, 1920, 1040), 32,
            "Test", "TST", "Panel " + serial, serial, "0001", "HDMI", 1, true, true, 53, 30, 1, 2024, 1920, 1080, 60, false, false, 8,
            Array.Empty<string>(), "Healthy", "ok", Array.Empty<string>(), false) { IdentityKey = "TST|0001|" + serial };

    // A 1080p sRGB monitor with a range-limits descriptor, an HDMI block (300 MHz TMDS) and an HDR10 block.
    private static byte[] SyntheticEdid()
    {
        var e = new byte[256];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(e, 0);
        e[54] = 0x02; e[55] = 0x3A;                                          // 148.50 MHz preferred mode
        new byte[] { 0, 0, 0, 0xFD, 0, 48, 144, 30, 160, 60 }.CopyTo(e, 72); // 48-144 Hz, 600 MHz
        void Primary(int high, int low, int shift, double value)
        {
            var v = (int)Math.Round(value * 1024);
            e[high] = (byte)(v >> 2);
            e[low] |= (byte)((v & 3) << shift);
        }
        Primary(27, 25, 6, 0.640); Primary(28, 25, 4, 0.330);
        Primary(29, 25, 2, 0.300); Primary(30, 25, 0, 0.600);
        Primary(31, 26, 6, 0.150); Primary(32, 26, 4, 0.060);
        e[126] = 1;
        new byte[] { 0x02, 0x03, 19, 0, 0x67, 0x03, 0x0C, 0x00, 0x10, 0x00, 0x00, 60, 0xE6, 0x06, 0x05, 0x01, 96, 82, 40 }.CopyTo(e, 128);
        return e;
    }
}
