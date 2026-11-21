using System.Windows.Forms;
using System.Diagnostics;

namespace ScreenShepherd;

internal static class TestHarness
{
    internal static int RunSmoke(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var baseDirectory = AppContext.BaseDirectory;
        var timings = new Dictionary<string, long>();
        var stopwatch = Stopwatch.StartNew();
        T Time<T>(string name, Func<T> read)
        {
            stopwatch.Restart();
            var value = read();
            timings[name] = stopwatch.ElapsedMilliseconds;
            return value;
        }

        var service = new WindowService();
        var monitors = Time("monitors", () => service.GetMonitors());
        var windows = Time("windows", () => service.GetWindows(includeOwnProcess: true));
        var graphicsService = new GraphicsHealthService();
        var graphics = Time("graphics", () => graphicsService.GetSnapshot(monitors));
        var graphicsCached = Time("graphics-cached", () => graphicsService.GetSnapshot(monitors));
        var browsers = Time("browsers", () => new BrowserUsageService().GetSnapshot());
        var fileVersion = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? Application.ExecutablePath).ProductVersion ?? string.Empty;
        var checks = new Dictionary<string, bool>
        {
            ["wwwroot"] = Directory.Exists(Path.Combine(baseDirectory, "wwwroot")),
            ["index.html"] = File.Exists(Path.Combine(baseDirectory, "wwwroot", "index.html")),
            ["styles.css"] = File.Exists(Path.Combine(baseDirectory, "wwwroot", "styles.css")),
            ["app.js"] = File.Exists(Path.Combine(baseDirectory, "wwwroot", "app.js")),
            ["gleam-badge"] = File.Exists(Path.Combine(baseDirectory, "wwwroot", "gleam", "gleam-badge.js"))
                && File.Exists(Path.Combine(baseDirectory, "wwwroot", "gleam", "gleam-badge.css")),
            ["version-matches-file"] = fileVersion.Split('+')[0] == AppHostService.AppVersion,
            ["app-icon"] = File.Exists(Path.Combine(baseDirectory, "wwwroot", "icon.svg")) && IconLoads(),
            ["active-monitor"] = monitors.Count > 0,
            ["monitor-identity"] = monitors.Any(monitor => monitor.EdidValid && monitor.Model != "Unknown display"),
            ["monitor-path"] = monitors.All(monitor => !string.IsNullOrWhiteSpace(monitor.Connection)),
            ["graphics-telemetry"] = graphics.TelemetryStatus == "ready" && graphics.Adapters.Count > 0,
            ["physical-gpu-healthy"] = graphics.Adapters.Where(adapter => !adapter.Virtual).All(adapter => adapter.HealthLevel == "ok"),
            ["display-optimization-evaluated"] = graphics.Displays.Count == monitors.Count && graphics.Displays.All(display => display.OptimizationLevel is "ok" or "warn" or "info"),
            ["graphics-tuning-evaluated"] = graphics.Tuning.Recommendations.Count > 0
                && graphics.Tuning.TotalMemoryGiB > 0
                && !string.IsNullOrWhiteSpace(graphics.Tuning.PowerPlan),
            ["graphics-cache-reused"] = timings["graphics-cached"] < 500,
            ["browser-usage-evaluated"] = browsers.Browsers.Count == 2,
            ["enum-windows"] = windows.Count >= 0,
            ["handle-roundtrip"] = WindowService.ParseHandle(WindowService.ToHandleString(new nint(0x1234))) == new nint(0x1234)
        };
        var failed = checks.Where(item => !item.Value).Select(item => item.Key).ToArray();
        var receipt = new
        {
            test = "smoke",
            ok = failed.Length == 0,
            version = AppHostService.AppVersion,
            fileVersion,
            time = DateTimeOffset.Now,
            timingsMs = timings,
            monitorCount = monitors.Count,
            healthyMonitorCount = monitors.Count(monitor => monitor.HealthLevel == "ok"),
            graphics,
            browsers,
            monitors = monitors.Select(monitor => new
            {
                monitor.DisplayName,
                monitor.Manufacturer,
                monitor.Model,
                monitor.SerialNumber,
                monitor.Connection,
                currentMode = $"{monitor.Bounds.Width}x{monitor.Bounds.Height} @ {monitor.RefreshRateHz:0.##} Hz",
                preferredMode = monitor.PreferredWidth > 0 ? $"{monitor.PreferredWidth}x{monitor.PreferredHeight}" : "unavailable",
                monitor.PhysicalWidthCm,
                monitor.PhysicalHeightCm,
                monitor.AdvancedColorSupported,
                monitor.AdvancedColorEnabled,
                monitor.EdidValid,
                monitor.TargetAvailable,
                monitor.MultipleActivePaths,
                monitor.Health,
                monitor.HealthReasons
            }),
            enumeratedWindowCount = windows.Count,
            windowStatuses = windows.GroupBy(window => window.Status).ToDictionary(group => group.Key, group => group.Count()),
            checks,
            failed
        };
        Program.TryWriteReceipt(GetReceiptPath(args, "ScreenShepherd-smoke.json"), receipt);
        return failed.Length == 0 ? 0 : 1;
    }

    // The embedded icon loads at the window size and at the tray's small size.
    private static bool IconLoads()
    {
        try
        {
            using var window = IconBuilder.Window();
            using var tray = IconBuilder.Tray();
            return window.Width > 0 && tray.Size == SystemInformation.SmallIconSize;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Creates real windows and moves them with the same code the app uses. The test windows
    // are fully transparent, click-through and never activated, and the move target is a
    // monitor away from the mouse pointer, so the test stays out of the way of whatever is
    // being worked on.
    internal static int RunLiveMoveTest(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var receiptPath = GetReceiptPath(args, "ScreenShepherd-live-move.json");
        var service = new WindowService();
        var cursorScreen = Screen.FromPoint(Cursor.Position);
        var target = Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName != cursorScreen.DeviceName)
            ?? Screen.PrimaryScreen
            ?? Screen.AllScreens.FirstOrDefault();
        if (target is null)
        {
            Program.TryWriteReceipt(receiptPath, new { test = "live-move", ok = false, error = "No active monitor." });
            return 1;
        }

        var virtualRight = Screen.AllScreens.Max(screen => screen.Bounds.Right);
        var virtualBottom = Screen.AllScreens.Max(screen => screen.Bounds.Bottom);
        var offDesktop = new Point(virtualRight + 900, virtualBottom + 900);
        var cases = new List<LiveMoveCase>
        {
            RunNormalCase(service, target, offDesktop),
            RunMinimizedCase(service, target, offDesktop),
            RunPositionedCase(service, target, offDesktop),
            RunMaximizedCase(service, target)
        };

        var ok = cases.All(item => item.Status != "FAIL");
        Program.TryWriteReceipt(receiptPath, new
        {
            test = "live-move",
            ok,
            notRun = cases.Where(item => item.Status == "NOT_RUN").Select(item => item.Name),
            version = AppHostService.AppVersion,
            time = DateTimeOffset.Now,
            target = target.DeviceName,
            cases
        });
        return ok ? 0 : 1;
    }

    private static LiveMoveCase RunNormalCase(WindowService service, Screen target, Point offDesktop)
    {
        using var form = new TestWindow("Screen Shepherd Live Move Test", offDesktop);
        form.Show();
        Pump(100);
        var before = WaitForWindowSnapshot(service, form.Handle);
        var move = service.MoveWindow(form.Handle, target.DeviceName);
        Pump(250);
        var after = WaitForWindowSnapshot(service, form.Handle);
        var ok = before is { RescueCandidate: true }
                 && move.Ok
                 && after is { TitleBarReachable: true, RescueCandidate: false }
                 && after.MonitorDevice == target.DeviceName;
        form.Close();
        return new LiveMoveCase("normal-off-desktop", Verdict(ok), new
        {
            before = before is null ? null : new { before.Status, before.Bounds, before.RescueCandidate, before.NearestMonitorName },
            move,
            after = after is null ? null : new { after.Status, after.Bounds, after.RescueCandidate, after.MonitorName }
        });
    }

    // A minimized window must stay minimized, and its restore position must land on the target.
    private static LiveMoveCase RunMinimizedCase(WindowService service, Screen target, Point offDesktop)
    {
        using var form = new TestWindow("Screen Shepherd Live Move Test (minimized)", offDesktop);
        form.Show();
        NativeMethods.ShowWindowAsync(form.Handle, NativeMethods.SW_SHOWMINNOACTIVE);
        Pump(250);
        var minimizedBefore = NativeMethods.IsIconic(form.Handle);
        WindowService.TryGetRestoreRectangle(form.Handle, out var restoreBefore);
        var move = service.MoveWindow(form.Handle, target.DeviceName);
        Pump(400);
        var minimizedAfter = NativeMethods.IsIconic(form.Handle);
        WindowService.TryGetRestoreRectangle(form.Handle, out var restoreAfter);
        var restoreOnTarget = target.WorkingArea.Contains(restoreAfter);
        var ok = minimizedBefore && move.Ok && minimizedAfter && restoreOnTarget;
        form.Close();
        return new LiveMoveCase("minimized-restore-position", Verdict(ok), new
        {
            minimizedBefore,
            restoreBefore = RectSnapshot.From(restoreBefore),
            move,
            minimizedAfter,
            restoreAfter = RectSnapshot.From(restoreAfter),
            restoreOnTarget
        });
    }

    // A drop on the map: the window's visible frame lands at the requested point on the target.
    private static LiveMoveCase RunPositionedCase(WindowService service, Screen target, Point offDesktop)
    {
        using var form = new TestWindow("Screen Shepherd Live Move Test (positioned)", offDesktop);
        form.Show();
        Pump(100);
        var requested = new Point(target.WorkingArea.Left + 140, target.WorkingArea.Top + 96);
        var move = service.MoveWindow(form.Handle, target.DeviceName, topLeft: requested);
        Pump(250);
        var after = WaitForWindowSnapshot(service, form.Handle);
        var landed = after is null ? Point.Empty : new Point(after.Bounds.X, after.Bounds.Y);
        var ok = move.Ok && after is not null && Math.Abs(landed.X - requested.X) <= 2 && Math.Abs(landed.Y - requested.Y) <= 2;
        form.Close();
        return new LiveMoveCase("positioned-drop", Verdict(ok), new { requested = new { requested.X, requested.Y }, landed = new { landed.X, landed.Y }, move });
    }

    // A window maximized on one monitor must end up maximized on the target monitor.
    private static LiveMoveCase RunMaximizedCase(WindowService service, Screen target)
    {
        var source = Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName != target.DeviceName);
        if (source is null) return new LiveMoveCase("maximized-cross-monitor", "NOT_RUN", new { reason = "Needs two active monitors." });

        var start = new Point(source.WorkingArea.Left + 80, source.WorkingArea.Top + 80);
        using var form = new TestWindow("Screen Shepherd Live Move Test (maximized)", start);
        form.Show();
        NativeMethods.ShowWindowAsync(form.Handle, NativeMethods.SW_SHOWMAXIMIZED);
        Pump(300);
        var maximizedBefore = NativeMethods.IsZoomed(form.Handle);
        var monitorBefore = Screen.FromHandle(form.Handle).DeviceName;
        var move = service.MoveWindow(form.Handle, target.DeviceName);
        Pump(500);
        var maximizedAfter = NativeMethods.IsZoomed(form.Handle);
        var monitorAfter = Screen.FromHandle(form.Handle).DeviceName;
        var ok = maximizedBefore && monitorBefore == source.DeviceName && move.Ok && maximizedAfter && monitorAfter == target.DeviceName;
        form.Close();
        return new LiveMoveCase("maximized-cross-monitor", Verdict(ok), new { maximizedBefore, monitorBefore, move, maximizedAfter, monitorAfter });
    }

    private static WindowSnapshot? WaitForWindowSnapshot(WindowService service, nint handle)
    {
        var deadline = DateTimeOffset.Now.AddSeconds(2);
        do
        {
            Application.DoEvents();
            var snapshot = service.InspectWindow(handle, includeOwnProcess: true);
            if (snapshot is not null) return snapshot;
            Thread.Sleep(50);
        } while (DateTimeOffset.Now < deadline);
        return null;
    }

    private static void Pump(int milliseconds)
    {
        var until = DateTimeOffset.Now.AddMilliseconds(milliseconds);
        do
        {
            Application.DoEvents();
            Thread.Sleep(15);
        } while (DateTimeOffset.Now < until);
    }

    private static string GetReceiptPath(string[] args, string fallbackName)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals("--receipt", StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        }
        return Path.Combine(Path.GetTempPath(), fallbackName);
    }

    private static string Verdict(bool ok) => ok ? "PASS" : "FAIL";

    private sealed record LiveMoveCase(string Name, string Status, object Detail);

    private sealed class TestWindow : Form
    {
        public TestWindow(string title, Point location)
        {
            Text = title;
            StartPosition = FormStartPosition.Manual;
            Location = location;
            Size = new Size(620, 380);
            ShowInTaskbar = false;
            Opacity = 0;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TRANSPARENT = 0x00000020;
                const int WS_EX_NOACTIVATE = 0x08000000;
                var parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
                return parameters;
            }
        }
    }
}
