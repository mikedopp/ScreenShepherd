using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScreenShepherd;

internal sealed class GraphicsTuningService
{
    private const string SurfaceDisplaySource = "Microsoft Surface external display support";
    private const string SurfaceDisplayUrl = "https://support.microsoft.com/en-US/surface/external-display/supported-external-display-surface-laptop";
    private const string SurfaceUpdateSource = "Microsoft Surface Laptop 5 update history";
    private const string SurfaceUpdateUrl = "https://support.microsoft.com/en-us/surface/updates/surface-laptop-5-update-history";
    private const string NvidiaDriverSource = "NVIDIA driver downloads";
    private const string NvidiaDriverUrl = "https://www.nvidia.com/en-us/drivers/";
    private const string AmdDriverSource = "AMD drivers and support";
    private const string AmdDriverUrl = "https://www.amd.com/en/support/download/drivers.html";
    private const string IntelDriverSource = "Intel Driver & Support Assistant";
    private const string IntelDriverUrl = "https://www.intel.com/content/www/us/en/support/detect.html";
    private const string MemoryCompressionSource = "Microsoft Enable-MMAgent documentation";
    private const string MemoryCompressionUrl = "https://learn.microsoft.com/en-us/powershell/module/mmagent/enable-mmagent";
    private const string WindowsGraphicsSource = "Microsoft Windows 11 graphics settings";
    private const string WindowsGraphicsUrl = "https://support.microsoft.com/en-us/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11";
    private const string SurfacePerformanceSource = "Microsoft Surface performance guidance";
    private const string SurfacePerformanceUrl = "https://support.microsoft.com/en-us/surface/performance/what-to-do-if-surface-is-running-slower";
    private const string GpuMemoryCategory = "GPU Adapter Memory";

    // Board makers ship these instead of a real system name; the motherboard name is better.
    private static readonly string[] PlaceholderNames =
    [
        "To Be Filled By O.E.M.", "System manufacturer", "System Product Name", "Default string", "O.E.M.", "Not Applicable"
    ];

    private static readonly TimeSpan SlowReadingLifetime = TimeSpan.FromSeconds(60);
    private readonly object _slowGate = new();
    private SlowReadings? _slow;

    internal static HostGraphicsProfile ReadHostProfile()
    {
        try
        {
            using var systemSearcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Manufacturer,Model,TotalPhysicalMemory FROM Win32_ComputerSystem");
            using var systemResults = systemSearcher.Get();
            using var system = systemResults.Cast<ManagementObject>().FirstOrDefault();

            using var osSearcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Caption,BuildNumber,FreePhysicalMemory FROM Win32_OperatingSystem");
            using var osResults = osSearcher.Get();
            using var os = osResults.Cast<ManagementObject>().FirstOrDefault();

            var manufacturer = ReadString(system, "Manufacturer", "Unknown manufacturer");
            var model = ReadString(system, "Model", "Unknown model");
            if (IsPlaceholder(manufacturer) || IsPlaceholder(model))
            {
                var board = ReadBaseBoard();
                if (IsPlaceholder(manufacturer) && board.Manufacturer.Length > 0) manufacturer = board.Manufacturer;
                if (IsPlaceholder(model) && board.Product.Length > 0) model = board.Product;
            }

            var isSurface = manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                && model.StartsWith("Surface", StringComparison.OrdinalIgnoreCase);
            var isSurfaceLaptop5 = isSurface && model.Equals("Surface Laptop 5", StringComparison.OrdinalIgnoreCase);
            return new HostGraphicsProfile(
                Manufacturer: manufacturer,
                Model: model,
                OsCaption: ReadString(os, "Caption", "Windows"),
                OsBuild: ReadString(os, "BuildNumber", "Unknown"),
                TotalMemoryBytes: ReadUInt64(system, "TotalPhysicalMemory"),
                AvailableMemoryBytes: ReadUInt64(os, "FreePhysicalMemory") * 1024,
                IsSurface: isSurface,
                ExternalDisplayMaxHz: isSurfaceLaptop5 ? 60 : 0,
                ExternalDisplayLimitSource: isSurfaceLaptop5 ? SurfaceDisplaySource : string.Empty,
                ExternalDisplayLimitUrl: isSurfaceLaptop5 ? SurfaceDisplayUrl : string.Empty);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new HostGraphicsProfile(
                "Unknown manufacturer",
                "Unknown model",
                "Windows",
                "Unknown",
                0,
                0,
                false,
                0,
                string.Empty,
                string.Empty);
        }
    }

    internal GraphicsTuningSnapshot GetSnapshot(
        HostGraphicsProfile host,
        IReadOnlyList<GpuAdapterSnapshot> adapters,
        IReadOnlyList<DisplayOptimizationSnapshot> displays)
    {
        var memory = ReadMemory(host);
        var slow = GetSlowReadings();

        var recommendations = BuildRecommendations(host, adapters, displays, memory, slow);
        var overallLevel = recommendations.Any(item => item.Level == "warn") ? "warn" : "ok";
        var overall = overallLevel == "warn" ? "Review" : "Tuned";
        return new GraphicsTuningSnapshot(
            CapturedAt: DateTimeOffset.Now,
            Overall: overall,
            OverallLevel: overallLevel,
            Device: $"{host.Manufacturer} {host.Model}".Trim(),
            Windows: $"{host.OsCaption} build {host.OsBuild}",
            HostDisplayLimit: host.ExternalDisplayMaxHz > 0 ? $"{host.ExternalDisplayMaxHz:0} Hz external" : "Not cataloged",
            PowerPlan: slow.PowerPlan,
            TotalMemoryGiB: ToGiB(memory.TotalBytes),
            AvailableMemoryGiB: ToGiB(memory.AvailableBytes),
            CommittedPercent: memory.CommittedPercent,
            PageFileCurrentGiB: slow.PageFileCurrentGiB,
            PageFilePeakGiB: slow.PageFilePeakGiB,
            GpuSharedUsageMiB: slow.GpuSharedUsageMiB,
            GpuCommittedMiB: slow.GpuCommittedMiB,
            HardwareScheduling: slow.HardwareScheduling,
            GameDvr: slow.GameDvr,
            Recommendations: recommendations);
    }

    // WMI, performance-counter and page-file reads take from milliseconds to a couple of
    // seconds (much longer when a WMI provider is broken), so they are read at most once a
    // minute. Physical memory and commit come straight from the kernel on every refresh.
    private SlowReadings GetSlowReadings()
    {
        lock (_slowGate)
        {
            if (_slow is not null && DateTimeOffset.Now - _slow.CapturedAt <= SlowReadingLifetime) return _slow;
            var pageFile = ReadPageFile();
            var gpuMemory = ReadGpuMemory();
            _slow = new SlowReadings(
                CapturedAt: DateTimeOffset.Now,
                PowerPlan: ReadActivePowerPlan(),
                PageFileCurrentGiB: pageFile.CurrentGiB,
                PageFilePeakGiB: pageFile.PeakGiB,
                GpuSharedUsageMiB: gpuMemory.SharedUsageMiB,
                GpuCommittedMiB: gpuMemory.TotalCommittedMiB,
                HardwareScheduling: ReadRegistrySetting(
                    Registry.LocalMachine,
                    @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                    "HwSchMode",
                    value => value switch { 2 => "Enabled override", 1 => "Disabled override", _ => $"Override {value}" },
                    "Windows default"),
                GameDvr: ReadRegistrySetting(
                    Registry.CurrentUser,
                    @"System\GameConfigStore",
                    "GameDVR_Enabled",
                    value => value == 0 ? "Disabled" : "Enabled",
                    "Windows default"));
            return _slow;
        }
    }

    private static IReadOnlyList<GraphicsTuningRecommendation> BuildRecommendations(
        HostGraphicsProfile host,
        IReadOnlyList<GpuAdapterSnapshot> adapters,
        IReadOnlyList<DisplayOptimizationSnapshot> displays,
        MemoryReading memory,
        SlowReadings slow)
    {
        var recommendations = new List<GraphicsTuningRecommendation>();
        var platformLimited = displays.Where(display => display.PlatformLimited).ToArray();
        if (platformLimited.Length > 0)
        {
            recommendations.Add(new GraphicsTuningRecommendation(
                Priority: 1,
                Status: "Hardware limit",
                Level: "info",
                Title: $"The external displays are already at the {host.Model} ceiling",
                Evidence: $"{platformLimited.Length} external display path(s) run at {host.ExternalDisplayMaxHz:0} Hz. Their panels support more, but the maker documents {host.ExternalDisplayMaxHz:0} Hz as the maximum external refresh rate for this model.",
                NextStep: "No RAM, registry, cable, or Windows graphics toggle raises this documented ceiling. A newer host or discrete GPU is the meaningful high-refresh upgrade.",
                SettingsTarget: "advanced-display",
                Source: host.ExternalDisplayLimitSource,
                SourceUrl: host.ExternalDisplayLimitUrl));
        }
        else
        {
            var pathLimited = displays.Where(display => display.PathLimited).ToArray();
            if (pathLimited.Length > 0)
            {
                recommendations.Add(new GraphicsTuningRecommendation(
                    1,
                    "Path review",
                    "warn",
                    "The monitor path exposes less refresh rate than the panel supports",
                    $"{pathLimited.Length} path(s) are below the recognized panel limit.",
                    "Check dock, MST chain, cable, port, monitor OSD bandwidth priority, then re-open Advanced display.",
                    "advanced-display",
                    WindowsGraphicsSource,
                    WindowsGraphicsUrl));
            }
        }

        var physical = adapters.Where(adapter => !adapter.Virtual).OrderByDescending(adapter => adapter.DriverDate).FirstOrDefault();
        if (physical is not null)
        {
            var driverAgeDays = physical.DriverDate is null ? 0 : Math.Max(0, (DateTimeOffset.Now - physical.DriverDate.Value).Days);
            var stale = physical.DriverDate is not null && driverAgeDays >= 365;
            var advice = DriverAdvice(host, physical);
            recommendations.Add(new GraphicsTuningRecommendation(
                Priority: 2,
                Status: stale ? "Review" : "Current evidence",
                Level: stale ? "warn" : "ok",
                Title: advice.Title,
                Evidence: $"{physical.Name} is healthy on driver {physical.DriverVersion}" +
                          (physical.DriverDate is null ? "; its driver date is unavailable." : $" dated {physical.DriverDate:yyyy-MM-dd} ({driverAgeDays} days old)."),
                NextStep: advice.NextStep,
                SettingsTarget: "windows-update",
                Source: advice.Source,
                SourceUrl: advice.SourceUrl));
        }

        var availablePercent = memory.TotalBytes == 0 ? 0 : memory.AvailableBytes * 100d / memory.TotalBytes;
        var memoryPressure = availablePercent > 0 && (availablePercent < 15 || memory.CommittedPercent >= 85);
        var integrated = physical is not null && IsIntegratedGpu(physical);
        var commitText = memory.CommittedPercent is { } committed ? $"commit is {committed:0}%" : "commit charge is unavailable";
        var gpuText = slow.GpuSharedUsageMiB is { } shared
            ? $" GPU shared memory in use: {shared / 1024d:0.0} GiB."
            : string.Empty;
        recommendations.Add(new GraphicsTuningRecommendation(
            Priority: 3,
            Status: memoryPressure ? "Pressure" : "Headroom",
            Level: memoryPressure ? "warn" : "ok",
            Title: memoryPressure
                ? integrated ? "Memory pressure can feed integrated-GPU stutter" : "Memory pressure can cause stutter and paging"
                : "RAM compression is not the current graphics bottleneck",
            Evidence: memory.TotalBytes == 0
                ? "Windows did not return physical-memory telemetry."
                : $"{ToGiB(memory.AvailableBytes):0.0} of {ToGiB(memory.TotalBytes):0.0} GiB is available; {commitText} and page-file usage is {slow.PageFileCurrentGiB:0.0} GiB.{gpuText}",
            NextStep: memoryPressure
                ? "Keep Windows memory compression enabled and close the processes driving memory use before changing graphics settings."
                : "Leave memory compression Windows-managed. Recheck during the slowdown; compression helps avoid paging but does not add GPU execution capacity.",
            SettingsTarget: string.Empty,
            Source: MemoryCompressionSource,
            SourceUrl: MemoryCompressionUrl));

        var performancePlan = slow.PowerPlan.Contains("High performance", StringComparison.OrdinalIgnoreCase)
            || slow.PowerPlan.Contains("Ultimate performance", StringComparison.OrdinalIgnoreCase);
        recommendations.Add(new GraphicsTuningRecommendation(
            Priority: 4,
            Status: performancePlan ? "Set" : "Review",
            Level: performancePlan ? "ok" : "warn",
            Title: performancePlan ? "The active power plan already favors performance" : "Use a performance power mode for this workload",
            Evidence: $"Active power plan: {slow.PowerPlan}.",
            NextStep: performancePlan
                ? host.IsSurface
                    ? "No power-plan change is indicated. Watch heat: a thin Surface can trade sustained speed for temperature."
                    : "No power-plan change is indicated."
                : "Open Power settings, choose Best performance (on a laptop, while plugged in), then compare the same workload.",
            SettingsTarget: "power",
            Source: host.IsSurface ? SurfacePerformanceSource : "Windows power settings",
            SourceUrl: host.IsSurface ? SurfacePerformanceUrl : string.Empty));

        var staleVirtual = adapters
            .Where(adapter => adapter.Virtual && adapter.CurrentMode == "No active desktop mode"
                && adapter.DriverDate is not null
                && DateTimeOffset.Now - adapter.DriverDate.Value > TimeSpan.FromDays(365 * 3))
            .OrderBy(adapter => adapter.DriverDate)
            .FirstOrDefault();
        if (staleVirtual is not null)
        {
            recommendations.Add(new GraphicsTuningRecommendation(
                Priority: 5,
                Status: "Optional test",
                Level: "warn",
                Title: $"Retest without the inactive {staleVirtual.Name} if it is no longer used",
                Evidence: $"The virtual display adapter has no active desktop mode and uses driver {staleVirtual.DriverVersion} dated {staleVirtual.DriverDate:yyyy-MM-dd}. This is not proof that it causes the slowdown.",
                NextStep: "Only if the associated display-sharing software is unused, disable or uninstall it as a reversible A/B test, restart, and compare. Keep it if the workflow depends on it.",
                SettingsTarget: string.Empty,
                Source: "Local Plug and Play evidence",
                SourceUrl: string.Empty));
        }

        recommendations.Add(new GraphicsTuningRecommendation(
            Priority: 6,
            Status: "Benchmark only",
            Level: "info",
            Title: "Do not force hardware scheduling through the registry",
            Evidence: $"Hardware scheduling registry state: {slow.HardwareScheduling}. Game DVR: {slow.GameDvr}.",
            NextStep: "If Hardware-accelerated GPU scheduling appears in Graphics settings, compare it on and off with the same workload. Windows 11's windowed-game optimization helps DX10/11 games, not ordinary desktop video.",
            SettingsTarget: "graphics",
            Source: WindowsGraphicsSource,
            SourceUrl: WindowsGraphicsUrl));

        return recommendations.OrderBy(item => item.Priority).ToArray();
    }

    // Where to look for a newer driver depends on who supplies it: the PC maker's package
    // comes first on a Surface, the GPU vendor's tool on a desktop card.
    private static (string Title, string NextStep, string Source, string SourceUrl) DriverAdvice(HostGraphicsProfile host, GpuAdapterSnapshot adapter)
    {
        var vendor = $"{adapter.DriverProvider} {adapter.Name}";
        if (host.IsSurface)
            return ("Check the Surface graphics package before testing generic drivers",
                "Use the Surface app and Windows Update first. If the issue remains, treat a generic GPU-vendor driver as a controlled A/B test with the Surface package ready for rollback.",
                SurfaceUpdateSource,
                SurfaceUpdateUrl);
        if (vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return ("Compare the NVIDIA driver with the current release",
                "Check the NVIDIA app or NVIDIA's driver page for a newer Game Ready or Studio driver. Note the installed version first so a rollback is one step.",
                NvidiaDriverSource,
                NvidiaDriverUrl);
        if (vendor.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || vendor.Contains("AMD", StringComparison.Ordinal) || vendor.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return ("Compare the AMD driver with the current release",
                "Check AMD Software: Adrenalin Edition or AMD's driver page for a newer release. Note the installed version first so a rollback is one step.",
                AmdDriverSource,
                AmdDriverUrl);
        if (vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return ("Compare the Intel graphics driver with the current release",
                "On a laptop, check the PC maker's driver package and Windows Update first; otherwise use Intel Driver & Support Assistant. Keep the current package for rollback.",
                IntelDriverSource,
                IntelDriverUrl);
        return ("Compare the graphics driver with the vendor's current release",
            "Check Windows Update and the adapter vendor's support page for a newer driver. Keep the current package for rollback.",
            "Windows Update",
            string.Empty);
    }

    // Integrated GPUs borrow system RAM, so memory pressure hits them directly.
    private static bool IsIntegratedGpu(GpuAdapterSnapshot adapter)
    {
        var name = adapter.Name;
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return !name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
        return name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Vega", StringComparison.OrdinalIgnoreCase);
    }

    // GetPerformanceInfo is a kernel call: always present, microseconds, and unaffected by
    // broken WMI performance classes (which returned a false 0% commit on one test PC).
    private static MemoryReading ReadMemory(HostGraphicsProfile host)
    {
        if (NativeMethods.GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<NativeMethods.PerformanceInformation>()) && info.CommitLimit > 0)
        {
            var pageSize = (ulong)info.PageSize;
            return new MemoryReading(
                TotalBytes: (ulong)info.PhysicalTotal * pageSize,
                AvailableBytes: (ulong)info.PhysicalAvailable * pageSize,
                CommittedPercent: Math.Round((double)info.CommitTotal * 100d / info.CommitLimit, 1));
        }
        return new MemoryReading(host.TotalMemoryBytes, host.AvailableMemoryBytes, null);
    }

    private static (double CurrentGiB, double PeakGiB) ReadPageFile()
    {
        double currentGiB = 0;
        double peakGiB = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT CurrentUsage,PeakUsage FROM Win32_PageFileUsage");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    currentGiB += ReadUInt64(item, "CurrentUsage") / 1024d;
                    peakGiB += ReadUInt64(item, "PeakUsage") / 1024d;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException)
        {
            // Page-file telemetry is optional.
        }
        return (currentGiB, peakGiB);
    }

    // The "GPU Adapter Memory" counters, read through PDH. The WMI formatted-data class and
    // .NET's PerformanceCounter (which needs the classic counter registry) both failed on a PC
    // where PDH still returned the values.
    private static (double? SharedUsageMiB, double? TotalCommittedMiB) ReadGpuMemory()
    {
        var shared = Pdh.SumLarge($@"\{GpuMemoryCategory}(*)\Shared Usage");
        var committed = Pdh.SumLarge($@"\{GpuMemoryCategory}(*)\Total Committed");
        return (shared / 1024d / 1024d, committed / 1024d / 1024d);
    }

    private static string ReadActivePowerPlan()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2\\power",
                "SELECT ElementName FROM Win32_PowerPlan WHERE IsActive=True");
            using var results = searcher.Get();
            using var item = results.Cast<ManagementObject>().FirstOrDefault();
            var name = ReadString(item, "ElementName", string.Empty);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // Fall through to the registry-backed active scheme identifier.
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes",
                writable: false);
            var scheme = key?.GetValue("ActivePowerScheme")?.ToString()?.Trim();
            return scheme?.ToLowerInvariant() switch
            {
                "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High performance",
                "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
                "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power saver",
                "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Ultimate performance",
                { Length: > 0 } => $"Custom ({scheme})",
                _ => "Unknown"
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return "Unknown";
        }
    }

    private static (string Manufacturer, string Product) ReadBaseBoard()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Manufacturer,Product FROM Win32_BaseBoard");
            using var results = searcher.Get();
            using var board = results.Cast<ManagementObject>().FirstOrDefault();
            var manufacturer = ReadString(board, "Manufacturer", string.Empty);
            var product = ReadString(board, "Product", string.Empty);
            return (IsPlaceholder(manufacturer) ? string.Empty : manufacturer, IsPlaceholder(product) ? string.Empty : product);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return (string.Empty, string.Empty);
        }
    }

    private static bool IsPlaceholder(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)
        || PlaceholderNames.Any(name => value.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string ReadRegistrySetting(
        RegistryKey hive,
        string path,
        string valueName,
        Func<int, string> formatter,
        string fallback)
    {
        try
        {
            using var key = hive.OpenSubKey(path, writable: false);
            return key?.GetValue(valueName) is null
                ? fallback
                : formatter(Convert.ToInt32(key.GetValue(valueName), System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or FormatException or InvalidCastException or OverflowException)
        {
            return "Unavailable";
        }
    }

    private static string ReadString(ManagementBaseObject? item, string property, string fallback)
    {
        if (item is null) return fallback;
        try { return item[property]?.ToString()?.Trim() is { Length: > 0 } value ? value : fallback; }
        catch (ManagementException) { return fallback; }
    }

    private static ulong ReadUInt64(ManagementBaseObject? item, string property)
    {
        if (item is null) return 0;
        try { return item[property] is null ? 0 : Convert.ToUInt64(item[property], System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is ManagementException or FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    private static double ToGiB(ulong bytes) => bytes / 1024d / 1024d / 1024d;

    private sealed record MemoryReading(ulong TotalBytes, ulong AvailableBytes, double? CommittedPercent);

    private static class Pdh
    {
        private const uint PdhFmtLarge = 0x00000400;
        private const uint PdhFmtNoScale = 0x00001000;
        private const uint PdhMoreData = 0x800007D2;

        // Sums every instance of a wildcard counter path. Raw-count counters such as the GPU
        // memory ones are valid after a single collection. Null when the counter is missing.
        internal static double? SumLarge(string path)
        {
            if (PdhOpenQuery(null, 0, out var query) != 0) return null;
            try
            {
                if (PdhAddEnglishCounter(query, path, 0, out var counter) != 0) return null;
                if (PdhCollectQueryData(query) != 0) return null;
                uint bufferSize = 0;
                var status = PdhGetFormattedCounterArray(counter, PdhFmtLarge | PdhFmtNoScale, ref bufferSize, out _, 0);
                if (status != PdhMoreData || bufferSize == 0) return null;
                var buffer = Marshal.AllocHGlobal((int)bufferSize);
                try
                {
                    if (PdhGetFormattedCounterArray(counter, PdhFmtLarge | PdhFmtNoScale, ref bufferSize, out var itemCount, buffer) != 0) return null;
                    // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer, status (padded to 8), 64-bit value.
                    var itemSize = IntPtr.Size + 8 + 8;
                    double total = 0;
                    for (var index = 0; index < itemCount; index++)
                    {
                        var item = buffer + index * itemSize;
                        if (Marshal.ReadInt32(item, IntPtr.Size) != 0) continue;
                        total += Marshal.ReadInt64(item, IntPtr.Size + 8);
                    }
                    return total;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                PdhCloseQuery(query);
            }
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(nint query, string counterPath, nint userData, out nint counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(nint query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(nint query);
    }

    private sealed record SlowReadings(
        DateTimeOffset CapturedAt,
        string PowerPlan,
        double PageFileCurrentGiB,
        double PageFilePeakGiB,
        double? GpuSharedUsageMiB,
        double? GpuCommittedMiB,
        string HardwareScheduling,
        string GameDvr);
}

internal sealed record HostGraphicsProfile(
    string Manufacturer,
    string Model,
    string OsCaption,
    string OsBuild,
    ulong TotalMemoryBytes,
    ulong AvailableMemoryBytes,
    bool IsSurface,
    double ExternalDisplayMaxHz,
    string ExternalDisplayLimitSource,
    string ExternalDisplayLimitUrl);

public sealed record GraphicsTuningSnapshot(
    DateTimeOffset CapturedAt,
    string Overall,
    string OverallLevel,
    string Device,
    string Windows,
    string HostDisplayLimit,
    string PowerPlan,
    double TotalMemoryGiB,
    double AvailableMemoryGiB,
    double? CommittedPercent,
    double PageFileCurrentGiB,
    double PageFilePeakGiB,
    double? GpuSharedUsageMiB,
    double? GpuCommittedMiB,
    string HardwareScheduling,
    string GameDvr,
    IReadOnlyList<GraphicsTuningRecommendation> Recommendations);

public sealed record GraphicsTuningRecommendation(
    int Priority,
    string Status,
    string Level,
    string Title,
    string Evidence,
    string NextStep,
    string SettingsTarget,
    string Source,
    string SourceUrl);
