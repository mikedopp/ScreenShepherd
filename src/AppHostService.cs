using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Chassis;

namespace ScreenShepherd;

public sealed class AppHostService
{
    private readonly WindowService _windows = new();
    private readonly GraphicsHealthService _graphics = new();
    private readonly BrowserUsageService _browsers = new();
    private readonly object _logGate = new();
    private readonly List<LogEntry> _logs = new();
    private nint _lastExternalForeground;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public AppHostService()
    {
        RememberExternalForeground(NativeMethods.GetForegroundWindow());
        Log("ready", "Screen Shepherd host initialized.");
    }

    // A copy of the app's settings that worker threads read; MainWindow replaces it when the page saves.
    public AppSettings Settings { get; private set; } = new();

    public void UseSettings(AppSettings settings) => Settings = settings;
    public bool HotkeyRegistered { get; set; }

    public AppState GetState()
    {
        var monitors = _windows.GetMonitors();
        var windows = _windows.GetWindows();
        var graphics = _graphics.GetSnapshot(monitors);
        var browsers = _browsers.GetSnapshot();
        var checks = GetChecks(monitors, graphics);
        return new AppState(
            App: "Screen Shepherd",
            Version: GetVersion(),
            Mode: "LOCAL WINDOW CONTROL",
            CapturedAt: DateTimeOffset.Now,
            Monitors: monitors,
            Windows: windows,
            Graphics: graphics,
            Browsers: browsers,
            OffScreenCount: windows.Count(window => window.RescueCandidate),
            Settings: Settings,
            Hotkey: new HotkeyState("Ctrl+Alt+Home", HotkeyRegistered ? "ready" : "unavailable", HotkeyRegistered ? "ok" : "warn"),
            Dependencies: new DependencyState(
                checks.All(item => item.Level == "ok") ? "ready" : "review",
                checks),
            Logs: GetLogs());
    }

    public SettingsLaunchResult OpenSystemSettings(JsonElement parameters)
    {
        var target = GetRequiredString(parameters, "target");
        var destinations = new Dictionary<string, (string Uri, string Label)>(StringComparer.OrdinalIgnoreCase)
        {
            ["advanced-display"] = ("ms-settings:display-advanced", "Advanced display"),
            ["graphics"] = ("ms-settings:display-advancedgraphics-default", "Graphics defaults"),
            ["power"] = ("ms-settings:powersleep", "Power & battery"),
            ["windows-update"] = ("ms-settings:windowsupdate", "Windows Update")
        };
        if (!destinations.TryGetValue(target, out var destination))
            throw new ArgumentException("Unsupported settings target: " + target);

        Process.Start(new ProcessStartInfo(destination.Uri) { UseShellExecute = true });
        Log("ready", $"Opened {destination.Label} settings.");
        return new SettingsLaunchResult("opened", target, $"Opened {destination.Label}.");
    }

    public DisplayWakeResult WakeDisplays()
    {
        var executionState = NativeMethods.SetThreadExecutionState(NativeMethods.ES_DISPLAY_REQUIRED);
        var signalSent = NativeMethods.SendNotifyMessage(
            NativeMethods.HWND_BROADCAST,
            NativeMethods.WM_SYSCOMMAND,
            new nint(NativeMethods.SC_MONITORPOWER),
            new nint(-1));
        var ok = executionState != 0 || signalSent;
        var message = ok
            ? "Windows display wake signal sent. A monitor left on another computer's input may still require input switching."
            : "Windows did not accept the display wake signal.";
        Log(ok ? "ok" : "warn", message);
        return new DisplayWakeResult(ok, message);
    }

    public MoveResult MoveWindow(JsonElement parameters)
    {
        // Optional x/y (physical pixels, the window's top-left) place it where it was dropped on the map.
        var x = GetOptionalInt(parameters, "x");
        var y = GetOptionalInt(parameters, "y");
        var result = _windows.MoveWindow(
            GetRequiredString(parameters, "handle"),
            GetRequiredString(parameters, "deviceName"),
            x is not null && y is not null ? new Point(x.Value, y.Value) : null);
        Log(result.Ok ? "ok" : "warn", result.Message);
        return result;
    }

    public BatchMoveResult RescueAll(JsonElement parameters)
    {
        var target = GetString(parameters, "deviceName", string.Empty);
        var result = _windows.RescueAll(string.IsNullOrWhiteSpace(target) ? null : target);
        Log(result.Moved > 0 ? "ok" : "ready", result.Message);
        return result;
    }

    public BatchMoveResult GatherAll(JsonElement parameters)
    {
        var result = _windows.GatherAll(GetRequiredString(parameters, "deviceName"));
        Log(result.Moved > 0 ? "ok" : "warn", result.Message);
        return result;
    }

    public MoveResult MoveForegroundToCursor()
    {
        var result = _windows.MoveForegroundToCursor();
        Log(result.Ok ? "ok" : "warn", result.Message);
        return result;
    }

    public MoveResult MoveLastExternalToCursor()
    {
        var result = _windows.MoveWindowToCursor(_lastExternalForeground);
        Log(result.Ok ? "ok" : "warn", result.Message);
        return result;
    }

    public void RememberExternalForeground(nint handle)
    {
        if (handle == 0 || handle == NativeMethods.GetShellWindow()) return;
        var snapshot = _windows.InspectWindow(handle);
        if (snapshot is not null) _lastExternalForeground = handle;
    }

    public bool TryAutoRescueWindow(nint handle)
    {
        if (!Settings.AutoRescueNewWindows) return false;
        var snapshot = _windows.InspectWindow(handle);
        if (snapshot is null || !snapshot.RescueCandidate) return false;
        var result = _windows.MoveWindow(handle, snapshot.NearestMonitorDevice);
        Log(result.Ok ? "ok" : "warn", $"Launch rescue: {snapshot.ProcessName} — {result.Message}");
        return result.Ok;
    }

    public BatchMoveResult HandleDisplayTopologyChange()
    {
        if (!Settings.AutoRescueDisplayChanges)
        {
            var skipped = new BatchMoveResult(0, 0, "Display topology changed; automatic rescue is disabled.", string.Empty);
            Log("warn", skipped.Message);
            return skipped;
        }
        var result = _windows.RescueAll();
        Log(result.Moved > 0 ? "ok" : "ready", "Display topology changed. " + result.Message);
        return result;
    }

    // Built off the UI thread; the caller puts the text on the clipboard (which needs the UI thread).
    public string BuildDiagnosticsText()
    {
        var state = GetState();
        var diagnostics = new
        {
            state.App,
            state.Version,
            state.Mode,
            state.CapturedAt,
            MonitorCount = state.Monitors.Count,
            WindowCount = state.Windows.Count,
            state.OffScreenCount,
            state.Settings,
            state.Hotkey,
            state.Dependencies,
            state.Graphics,
            state.Browsers,
            Monitors = state.Monitors.Select(monitor => new
            {
                monitor.DisplayName,
                monitor.Primary,
                monitor.Manufacturer,
                monitor.ManufacturerCode,
                monitor.Model,
                monitor.SerialNumber,
                monitor.ProductCode,
                monitor.Connection,
                monitor.TargetAvailable,
                monitor.EdidValid,
                monitor.Bounds,
                monitor.WorkingArea,
                monitor.RefreshRateHz,
                monitor.PreferredWidth,
                monitor.PreferredHeight,
                monitor.PhysicalWidthCm,
                monitor.PhysicalHeightCm,
                monitor.AdvancedColorSupported,
                monitor.AdvancedColorEnabled,
                monitor.BitsPerColorChannel,
                monitor.Capabilities,
                monitor.Health,
                monitor.HealthReasons,
                monitor.MultipleActivePaths
            }),
            Windows = state.Windows.Select(window => new
            {
                window.ProcessName,
                window.ProcessId,
                window.Title,
                window.MonitorName,
                window.Status,
                window.Bounds,
                window.VisiblePercent
            }),
            state.Logs
        };
        return JsonSerializer.Serialize(diagnostics, JsonOptions);
    }

    public DiagnosticsResult DiagnosticsCopied(int characters)
    {
        Log("ok", "Diagnostics copied to clipboard.");
        return new DiagnosticsResult("copied", characters, "Diagnostics copied without credentials or window contents.");
    }

    public void Log(string level, string message)
    {
        lock (_logGate)
        {
            _logs.Insert(0, new LogEntry(DateTimeOffset.Now, level, message));
            if (_logs.Count > 80) _logs.RemoveRange(80, _logs.Count - 80);
        }
        // The same line goes to Chassis's run log (the Diagnostics tab and the log file).
        ChassisLog.Write(level is "warn" or "bad" ? level + ": " + message : message);
    }

    private IReadOnlyList<LogEntry> GetLogs()
    {
        lock (_logGate) return _logs.ToArray();
    }

    // What was actually read from each Windows part just now (the Diagnostics tab). Where each part lives is on the
    // Dependencies tab, from WindowsParts.ForChassis.
    private IReadOnlyList<DependencyItem> GetChecks(IReadOnlyList<MonitorSnapshot> monitors, GraphicsHealthSnapshot graphics)
    {
        var gpuCounters = graphics.Tuning.GpuSharedUsageMiB is not null;
        var validEdid = monitors.Count(monitor => monitor.EdidValid);
        var adapters = graphics.Adapters.Count;
        var resets = graphics.DisplayResetEvents30Days;
        var wmiReady = graphics.TelemetryStatus == "ready";
        return new[]
        {
            new DependencyItem("Display configuration", WindowsParts.DisplayConfig,
                Plural(monitors.Count, "display"), monitors.Count > 0 ? "ok" : "bad"),
            new DependencyItem("Monitor EDID", WindowsParts.Edid,
                $"{validEdid} of {monitors.Count} valid", validEdid == monitors.Count ? "ok" : "warn"),
            new DependencyItem("Window placement", WindowsParts.Placement, "ready", "ok"),
            new DependencyItem("Desktop Window Manager", WindowsParts.Dwm, "ready", "ok"),
            new DependencyItem("Global rescue hotkey", WindowsParts.Hotkey,
                HotkeyRegistered ? "registered" : "taken by another app", HotkeyRegistered ? "ok" : "warn"),
            new DependencyItem("WMI", WindowsParts.Wmi,
                wmiReady ? Plural(adapters, "adapter") : "unavailable", wmiReady ? "ok" : "warn"),
            new DependencyItem("System event log", WindowsParts.EventLog,
                resets == 0 ? "no resets in 30 days" : $"{Plural(resets, "reset")} in 30 days", "ok"),
            new DependencyItem("Performance data", WindowsParts.Performance,
                gpuCounters ? "ready" : "GPU counters unavailable", gpuCounters ? "ok" : "warn"),
            new DependencyItem("Registry settings", WindowsParts.Registry, "ready", "ok"),
            new DependencyItem("UI Automation", WindowsParts.UiAutomation, "ready", "ok")
        };
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    // The one version string the app shows anywhere; it comes from the VERSION file via the assembly.
    public static string AppVersion { get; } =
        typeof(AppHostService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppHostService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    private static string GetVersion() => AppVersion;

    private static string GetRequiredString(JsonElement parameters, string name)
    {
        var value = GetString(parameters, name, string.Empty);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Missing required parameter: {name}");
        return value;
    }

    private static string GetString(JsonElement parameters, string name, string fallback)
    {
        return parameters.ValueKind == JsonValueKind.Object &&
               parameters.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static double GetNumber(JsonElement parameters, string name, double fallback)
    {
        return parameters.ValueKind == JsonValueKind.Object &&
               parameters.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : fallback;
    }

    private static int? GetOptionalInt(JsonElement parameters, string name)
    {
        return parameters.ValueKind == JsonValueKind.Object &&
               parameters.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(value.GetDouble())
            : null;
    }

    private static bool GetBoolean(JsonElement parameters, string name, bool fallback)
    {
        return parameters.ValueKind == JsonValueKind.Object &&
               parameters.TryGetProperty(name, out var value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    }
}

public sealed record AppState(
    string App,
    string Version,
    string Mode,
    DateTimeOffset CapturedAt,
    IReadOnlyList<MonitorSnapshot> Monitors,
    IReadOnlyList<WindowSnapshot> Windows,
    GraphicsHealthSnapshot Graphics,
    BrowserUsageSnapshot Browsers,
    int OffScreenCount,
    AppSettings Settings,
    HotkeyState Hotkey,
    DependencyState Dependencies,
    IReadOnlyList<LogEntry> Logs);

/// <summary>
/// The Windows parts Screen Shepherd reads or calls, and where each lives. One list feeds the Dependencies tab (through
/// Chassis) and the live checks on the Diagnostics tab.
/// </summary>
public static class WindowsParts
{
    public const string DisplayConfig = "user32.dll: QueryDisplayConfig, DisplayConfigGetDeviceInfo, EnumDisplayMonitors";
    public const string Edid = @"HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<model>\<instance>\Device Parameters\EDID";
    public const string Placement = "user32.dll: EnumWindows, Get/SetWindowPlacement, SetWindowPos, ShowWindow, SetWinEventHook";
    public const string Dwm = "dwmapi.dll: DwmGetWindowAttribute (cloaked state, visible frame bounds)";
    public const string Hotkey = "user32.dll RegisterHotKey on the main window: Ctrl+Alt+Home";
    public const string Wmi = @"root\CIMV2: Win32_VideoController, Win32_ComputerSystem, Win32_BaseBoard, Win32_OperatingSystem, Win32_PageFileUsage; root\wmi: WmiMonitorListedSupportedSourceModes; root\CIMV2\power: Win32_PowerPlan";
    public const string EventLog = "Windows Logs > System, provider Display, event 4101";
    public const string Performance = @"psapi.dll GetPerformanceInfo; pdh.dll \GPU Adapter Memory(*)\Shared Usage and Total Committed";
    public const string Registry = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers (HwSchMode); HKCU\System\GameConfigStore (GameDVR_Enabled); HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    public const string UiAutomation = "UIAutomationClient in the .NET desktop runtime: Chrome and Edge tab strips";

    public static List<ChassisDependency> ForChassis() => new()
    {
        new("Display configuration", "service", DisplayConfig, "Which displays are attached, how they are arranged and what they are."),
        new("Monitor EDID", "service", Edid, "Read only. Maker, model, serial number, size and build date of each monitor."),
        new("Window placement", "service", Placement, "Finds app windows and moves them. The only part that changes anything."),
        new("Desktop Window Manager", "service", Dwm, "Read only. Tells hidden (cloaked) windows from real ones and gives true window edges."),
        new("Global rescue hotkey", "service", Hotkey, "Moves the foreground window to the display under the mouse, from anywhere."),
        new("WMI", "service", Wmi, "Read only. Graphics adapters, drivers, memory, power plan and each monitor's advertised modes."),
        new("System event log", "service", EventLog, "Read only. Counts recovered display driver resets in the last 30 days."),
        new("Performance data", "service", Performance, "Read only. Free memory and shared GPU memory."),
        new("Registry settings", "service", Registry, "Read only. Hardware GPU scheduling, Game DVR and the active power scheme."),
        new("UI Automation", "service", UiAutomation, "Read only. Counts open browser tabs when the browser exposes its tab strip."),
        new("Settings shortcuts", "service", "ms-settings: pages (Advanced display, Graphics, Power, Windows Update)", "Opened in Windows Settings when you click Open settings on a Health check. Local pages, not the network."),
        new("Settings from 1.6 and older", "file", LegacySettings.LegacyFile, "Copied into the settings file above once, on the first run of 1.7. Never changed or deleted.")
    };
}

public sealed record HotkeyState(string Chord, string Status, string Level);
public sealed record DependencyState(string Status, IReadOnlyList<DependencyItem> Items);
public sealed record DependencyItem(string Name, string Target, string Status, string Level);
public sealed record LogEntry(DateTimeOffset Time, string Level, string Message);
public sealed record DiagnosticsResult(string Status, int Characters, string Message);
public sealed record SettingsLaunchResult(string Status, string Target, string Message);
public sealed record DisplayWakeResult(bool Ok, string Message);
