using System.Management;
using System.Diagnostics.Eventing.Reader;

namespace ScreenShepherd;

internal sealed class GraphicsHealthService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private readonly GraphicsTuningService _tuning = new();
    private readonly object _cacheGate = new();
    private BaseTelemetry? _cache;
    private static readonly IReadOnlyDictionary<string, KnownDisplayCapability> KnownDisplayCapabilities =
        new Dictionary<string, KnownDisplayCapability>(StringComparer.OrdinalIgnoreCase)
        {
            ["DELL P2425H"] = new(100, "Dell P2425H published specification", "https://www.dell.com/support/kbdoc/000223854"),
            ["DELL P2425HE"] = new(100, "Dell P2425HE published specification", "https://www.dell.com/support/kbdoc/000223851"),
            ["DELL P2426HE"] = new(120, "Dell P2426HE published specification", "https://www.dell.com/en-us/shop/dell-pro-p-24-usb-c-hub-monitor-p2426he/apd/210-bvsp")
        };

    internal GraphicsHealthSnapshot GetSnapshot(IReadOnlyList<MonitorSnapshot> monitors)
    {
        BaseTelemetry telemetry;
        lock (_cacheGate)
        {
            if (_cache is null || DateTimeOffset.Now - _cache.CapturedAt > CacheLifetime)
                _cache = ReadBaseTelemetry();
            telemetry = _cache;
        }

        var displays = monitors.Select(monitor => EvaluateDisplayMode(monitor, telemetry.SupportedModes, telemetry.HostProfile)).ToArray();
        var physicalAdapters = telemetry.Adapters.Where(adapter => !adapter.Virtual).ToArray();
        var healthReasons = new List<string>();
        if (physicalAdapters.Length == 0) healthReasons.Add("No physical display adapter was returned by Windows Management Instrumentation.");
        healthReasons.AddRange(physicalAdapters.SelectMany(adapter => adapter.HealthReasons));
        if (telemetry.DisplayResetEvents30Days > 0)
            healthReasons.Add($"Windows logged {telemetry.DisplayResetEvents30Days} recovered display-driver reset event(s) in the last 30 days.");

        var optimizationReviews = displays.Where(display => display.Optimization == "Review").ToArray();
        var pathLimited = displays.Where(display => display.PathLimited).ToArray();
        var platformLimited = displays.Where(display => display.PlatformLimited).ToArray();
        var optimizationUnknown = displays.Where(display => display.OptimizationLevel == "info").ToArray();
        var optimization = optimizationReviews.Length > 0
            ? "Review"
            : pathLimited.Length > 0
                ? "Path limited"
            : platformLimited.Length > 0
                ? "Platform limited"
            : optimizationUnknown.Length > 0
                ? "Partially verified"
                : "Optimized";
        var optimizationLevel = optimizationReviews.Length > 0 || pathLimited.Length > 0
            ? "warn"
            : platformLimited.Length > 0 || optimizationUnknown.Length > 0 ? "info" : "ok";
        var tuning = _tuning.GetSnapshot(telemetry.HostProfile, telemetry.Adapters, displays);

        return new GraphicsHealthSnapshot(
            CapturedAt: DateTimeOffset.Now,
            Health: healthReasons.Count == 0 ? "Healthy" : "Review",
            HealthLevel: healthReasons.Count == 0 ? "ok" : "warn",
            HealthReasons: healthReasons,
            Optimization: optimization,
            OptimizationLevel: optimizationLevel,
            DisplayResetEvents30Days: telemetry.DisplayResetEvents30Days,
            DriverCurrency: "Installed driver health verified; latest vendor version not checked.",
            Adapters: telemetry.Adapters,
            Displays: displays,
            Tuning: tuning,
            TelemetryStatus: telemetry.TelemetryStatus);
    }

    private static BaseTelemetry ReadBaseTelemetry()
    {
        try
        {
            var adapters = ReadAdapters();
            var supportedModes = ReadSupportedModes();
            var resets = ReadDisplayResetCount();
            var hostProfile = GraphicsTuningService.ReadHostProfile();
            return new BaseTelemetry(DateTimeOffset.Now, adapters, supportedModes, resets, hostProfile, "ready");
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new BaseTelemetry(
                DateTimeOffset.Now,
                [],
                new Dictionary<string, IReadOnlyList<SupportedDisplayMode>>(StringComparer.OrdinalIgnoreCase),
                0,
                GraphicsTuningService.ReadHostProfile(),
                "unavailable");
        }
    }

    private static IReadOnlyList<GpuAdapterSnapshot> ReadAdapters()
    {
        var adapters = new List<GpuAdapterSnapshot>();
        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2",
            "SELECT Name,PNPDeviceID,Status,ConfigManagerErrorCode,AdapterCompatibility,DriverVersion,DriverDate,VideoProcessor,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate,CurrentBitsPerPixel FROM Win32_VideoController");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            var name = ReadString(item, "Name", "Unknown adapter");
            var pnpDeviceId = ReadString(item, "PNPDeviceID", string.Empty);
            var isVirtual = pnpDeviceId.StartsWith("ROOT\\DISPLAY", StringComparison.OrdinalIgnoreCase)
                || name.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                || name.Contains("mirror", StringComparison.OrdinalIgnoreCase);
            var status = ReadString(item, "Status", "Unknown");
            var problemCode = ReadUInt32(item, "ConfigManagerErrorCode");
            var width = ReadUInt32(item, "CurrentHorizontalResolution");
            var height = ReadUInt32(item, "CurrentVerticalResolution");
            var refresh = ReadUInt32(item, "CurrentRefreshRate");
            var reasons = new List<string>();
            if (!status.Equals("OK", StringComparison.OrdinalIgnoreCase)) reasons.Add($"Windows adapter status is {status}.");
            if (problemCode != 0) reasons.Add($"Plug and Play problem code is {problemCode}.");
            if (!isVirtual && (width == 0 || height == 0)) reasons.Add("The physical adapter has no readable current display mode.");

            adapters.Add(new GpuAdapterSnapshot(
                Name: name,
                Role: isVirtual ? "Virtual display adapter" : "Physical display adapter",
                Virtual: isVirtual,
                Status: status,
                ProblemCode: problemCode,
                DriverProvider: ReadString(item, "AdapterCompatibility", "Unknown"),
                DriverVersion: ReadString(item, "DriverVersion", "Unknown"),
                DriverDate: ReadManagementDate(item, "DriverDate"),
                VideoProcessor: ReadString(item, "VideoProcessor", string.Empty),
                CurrentMode: width > 0 && height > 0 ? $"{width} × {height} @ {refresh} Hz" : "No active desktop mode",
                BitsPerPixel: ReadUInt32(item, "CurrentBitsPerPixel"),
                Health: reasons.Count == 0 ? "Healthy" : "Review",
                HealthLevel: reasons.Count == 0 ? "ok" : "warn",
                HealthReasons: reasons));
        }
        return adapters.OrderBy(adapter => adapter.Virtual).ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<SupportedDisplayMode>> ReadSupportedModes()
    {
        var modesByHardwareId = new Dictionary<string, IReadOnlyList<SupportedDisplayMode>>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(
            "root\\wmi",
            "SELECT InstanceName,Active,MonitorSourceModes FROM WmiMonitorListedSupportedSourceModes WHERE Active=True");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            var instanceName = ReadString(item, "InstanceName", string.Empty);
            var parts = instanceName.Split('\\');
            if (parts.Length < 2) continue;
            var hardwareId = parts[1];
            var modes = new List<SupportedDisplayMode>();
            if (item["MonitorSourceModes"] is Array modeObjects)
            {
                foreach (var value in modeObjects)
                {
                    if (value is not ManagementBaseObject mode) continue;
                    var denominator = ReadUInt32(mode, "VerticalRefreshRateDenominator");
                    var numerator = ReadUInt32(mode, "VerticalRefreshRateNumerator");
                    modes.Add(new SupportedDisplayMode(
                        Width: ReadUInt32(mode, "HorizontalActivePixels"),
                        Height: ReadUInt32(mode, "VerticalActivePixels"),
                        RefreshRateHz: denominator == 0 ? 0 : Math.Round((double)numerator / denominator, 2)));
                }
            }
            modesByHardwareId[hardwareId] = modes;
        }
        return modesByHardwareId;
    }

    private static int ReadDisplayResetCount()
    {
        try
        {
            const string query = "*[System[(EventID=4101) and Provider[@Name='Display'] and TimeCreated[timediff(@SystemTime) <= 2592000000]]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true });
            var count = 0;
            while (reader.ReadEvent() is { } entry)
            {
                using (entry) count++;
                if (count >= 1000) break;
            }
            return count;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DisplayOptimizationSnapshot EvaluateDisplayMode(
        MonitorSnapshot monitor,
        IReadOnlyDictionary<string, IReadOnlyList<SupportedDisplayMode>> modesByHardwareId,
        HostGraphicsProfile hostProfile)
    {
        var hardwareId = (monitor.ManufacturerCode + monitor.ProductCode).ToUpperInvariant();
        modesByHardwareId.TryGetValue(hardwareId, out var modes);
        var maxRefresh = modes?
            .Where(mode => mode.Width == monitor.Bounds.Width && mode.Height == monitor.Bounds.Height)
            .Select(mode => mode.RefreshRateHz)
            .DefaultIfEmpty(0)
            .Max() ?? 0;
        var reasons = new List<string>();
        KnownDisplayCapabilities.TryGetValue(monitor.Model, out var knownCapability);
        if (monitor.PreferredWidth == 0 || monitor.PreferredHeight == 0)
            reasons.Add("Preferred resolution is unavailable.");
        else if (monitor.Bounds.Width != monitor.PreferredWidth || monitor.Bounds.Height != monitor.PreferredHeight)
            reasons.Add($"Current resolution differs from preferred {monitor.PreferredWidth} × {monitor.PreferredHeight}.");
        if (maxRefresh > 0 && monitor.RefreshRateHz + 0.5 < maxRefresh)
            reasons.Add($"Current refresh {monitor.RefreshRateHz:0.##} Hz is below the advertised {maxRefresh:0.##} Hz maximum at this resolution.");
        if (monitor.BitsPerPixel < 32) reasons.Add($"Desktop color depth is {monitor.BitsPerPixel}-bit instead of 32-bit.");
        if (monitor.BitsPerColorChannel > 0 && monitor.BitsPerColorChannel < 8)
            reasons.Add($"Active path reports {monitor.BitsPerColorChannel} bits per color channel.");
        var panelExceedsPath = knownCapability is not null
            && maxRefresh > 0
            && knownCapability.MaxNativeRefreshHz > maxRefresh + 0.5;
        var externalDisplay = !monitor.Connection.Equals("Internal panel", StringComparison.OrdinalIgnoreCase);
        var platformLimited = panelExceedsPath
            && externalDisplay
            && hostProfile.ExternalDisplayMaxHz > 0
            && maxRefresh <= hostProfile.ExternalDisplayMaxHz + 0.5;
        var pathLimited = panelExceedsPath && !platformLimited;
        if (platformLimited)
            reasons.Add($"The panel supports up to {knownCapability!.MaxNativeRefreshHz:0.##} Hz, while {hostProfile.Model} is documented for up to {hostProfile.ExternalDisplayMaxHz:0.##} Hz on external displays.");
        if (pathLimited)
            reasons.Add($"The panel is rated for up to {knownCapability!.MaxNativeRefreshHz:0.##} Hz, but the current Windows path advertises only {maxRefresh:0.##} Hz at native resolution.");

        var modeEvidenceAvailable = monitor.PreferredWidth > 0 && monitor.PreferredHeight > 0 && maxRefresh > 0;
        var limitReasonCount = (pathLimited ? 1 : 0) + (platformLimited ? 1 : 0);
        var pathProblemCount = reasons.Count - limitReasonCount;
        var level = pathProblemCount > 0 || pathLimited ? "warn" : platformLimited ? "info" : modeEvidenceAvailable ? "ok" : "info";
        var optimization = pathProblemCount > 0
            ? "Review"
            : pathLimited
                ? "Path limited"
                : platformLimited
                    ? "Platform limited"
                    : modeEvidenceAvailable ? "Optimal" : "Partially verified";
        return new DisplayOptimizationSnapshot(
            DisplayName: monitor.DisplayName,
            Model: monitor.Model,
            Connection: monitor.Connection,
            CurrentMode: $"{monitor.Bounds.Width} × {monitor.Bounds.Height} @ {monitor.RefreshRateHz:0.##} Hz",
            PreferredMode: monitor.PreferredWidth > 0 ? $"{monitor.PreferredWidth} × {monitor.PreferredHeight}" : "Unknown",
            MaxRefreshAtCurrentResolutionHz: maxRefresh,
            VendorMaxRefreshAtNativeHz: knownCapability?.MaxNativeRefreshHz ?? 0,
            CapabilitySource: knownCapability?.Source ?? "Monitor descriptor only",
            CapabilitySourceUrl: knownCapability?.SourceUrl ?? string.Empty,
            PathLimited: pathLimited,
            PlatformLimited: platformLimited,
            PlatformLimitSource: platformLimited ? hostProfile.ExternalDisplayLimitSource : string.Empty,
            PlatformLimitUrl: platformLimited ? hostProfile.ExternalDisplayLimitUrl : string.Empty,
            BitsPerPixel: monitor.BitsPerPixel,
            BitsPerColorChannel: monitor.BitsPerColorChannel,
            Optimization: optimization,
            OptimizationLevel: level,
            Reasons: reasons.Count > 0 ? reasons : modeEvidenceAvailable
                ? ["Current resolution, refresh rate, and color depth match the advertised path capabilities."]
                : ["Current mode is readable, but the full supported-mode list is unavailable."]);
    }

    private static string ReadString(ManagementBaseObject item, string property, string fallback)
    {
        try { return item[property]?.ToString()?.Trim() is { Length: > 0 } value ? value : fallback; }
        catch (ManagementException) { return fallback; }
    }

    private static uint ReadUInt32(ManagementBaseObject item, string property)
    {
        try { return item[property] is null ? 0 : Convert.ToUInt32(item[property], System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is ManagementException or FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    private static DateTimeOffset? ReadManagementDate(ManagementBaseObject item, string property)
    {
        var value = ReadString(item, property, string.Empty);
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private sealed record BaseTelemetry(
        DateTimeOffset CapturedAt,
        IReadOnlyList<GpuAdapterSnapshot> Adapters,
        IReadOnlyDictionary<string, IReadOnlyList<SupportedDisplayMode>> SupportedModes,
        int DisplayResetEvents30Days,
        HostGraphicsProfile HostProfile,
        string TelemetryStatus);

    private sealed record SupportedDisplayMode(uint Width, uint Height, double RefreshRateHz);
    private sealed record KnownDisplayCapability(double MaxNativeRefreshHz, string Source, string SourceUrl);
}

public sealed record GraphicsHealthSnapshot(
    DateTimeOffset CapturedAt,
    string Health,
    string HealthLevel,
    IReadOnlyList<string> HealthReasons,
    string Optimization,
    string OptimizationLevel,
    int DisplayResetEvents30Days,
    string DriverCurrency,
    IReadOnlyList<GpuAdapterSnapshot> Adapters,
    IReadOnlyList<DisplayOptimizationSnapshot> Displays,
    GraphicsTuningSnapshot Tuning,
    string TelemetryStatus);

public sealed record GpuAdapterSnapshot(
    string Name,
    string Role,
    bool Virtual,
    string Status,
    uint ProblemCode,
    string DriverProvider,
    string DriverVersion,
    DateTimeOffset? DriverDate,
    string VideoProcessor,
    string CurrentMode,
    uint BitsPerPixel,
    string Health,
    string HealthLevel,
    IReadOnlyList<string> HealthReasons);

public sealed record DisplayOptimizationSnapshot(
    string DisplayName,
    string Model,
    string Connection,
    string CurrentMode,
    string PreferredMode,
    double MaxRefreshAtCurrentResolutionHz,
    double VendorMaxRefreshAtNativeHz,
    string CapabilitySource,
    string CapabilitySourceUrl,
    bool PathLimited,
    bool PlatformLimited,
    string PlatformLimitSource,
    string PlatformLimitUrl,
    int BitsPerPixel,
    uint BitsPerColorChannel,
    string Optimization,
    string OptimizationLevel,
    IReadOnlyList<string> Reasons);
