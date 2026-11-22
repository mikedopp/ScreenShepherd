using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScreenShepherd;

internal sealed class MonitorInventoryService
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint GetSourceName = 1;
    private const uint GetTargetName = 2;
    private const uint GetTargetPreferredMode = 3;
    private const uint GetAdvancedColorInfo = 9;
    private const uint GetSdrWhiteLevel = 11;
    internal const uint MiracastTechnology = 15;

    private static readonly IReadOnlyDictionary<string, string> ManufacturerNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ACI"] = "ASUS",
            ["AOC"] = "AOC",
            ["APP"] = "Apple",
            ["AUO"] = "AU Optronics",
            ["BOE"] = "BOE",
            ["DEL"] = "Dell",
            ["EIZ"] = "EIZO",
            ["GSM"] = "LG Electronics",
            ["HWP"] = "HP",
            ["LEN"] = "Lenovo",
            ["LGD"] = "LG Display",
            ["NEC"] = "NEC",
            ["PHI"] = "Philips",
            ["SAM"] = "Samsung",
            ["SEC"] = "Samsung Display",
            ["SHP"] = "Sharp",
            ["SNY"] = "Sony",
            ["VSC"] = "ViewSonic"
        };

    internal IReadOnlyDictionary<string, MonitorTelemetry> GetActiveTelemetry()
    {
        var telemetry = new Dictionary<string, MonitorTelemetry>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
            return telemetry;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            var actualPathCount = pathCount;
            var actualModeCount = modeCount;
            var result = QueryDisplayConfig(
                QdcOnlyActivePaths,
                ref actualPathCount,
                paths,
                ref actualModeCount,
                modes,
                0);

            if (result == 122)
            {
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out modeCount) != 0) break;
                continue;
            }
            if (result != 0) break;

            foreach (var path in paths.Take((int)actualPathCount))
            {
                var sourceName = GetSourceDeviceName(path.SourceInfo).ViewGdiDeviceName;
                if (string.IsNullOrWhiteSpace(sourceName)) continue;

                var targetName = GetTargetDeviceName(path.TargetInfo);
                var preferredMode = GetPreferredMode(path.TargetInfo);
                var advancedColor = GetAdvancedColor(path.TargetInfo);
                var edid = ReadFullEdid(targetName.MonitorDevicePath, out var edidSource);
                var parsed = ParseEdid(edid);
                var edidDetails = EdidDetails.Parse(edid, edidSource);
                var signal = path.TargetInfo.ModeInfoIdx < actualModeCount ? ReadSignal(modes[path.TargetInfo.ModeInfoIdx]) : null;
                var sdrWhite = advancedColor.Enabled ? GetSdrWhiteNits(path.TargetInfo) : 0;
                var manufacturerCode = parsed.ManufacturerCode;
                if (string.IsNullOrWhiteSpace(manufacturerCode) && targetName.EdidIdsValid)
                    manufacturerCode = DecodeManufacturer(targetName.EdidManufactureId);

                var manufacturer = ManufacturerNames.TryGetValue(manufacturerCode, out var knownManufacturer)
                    ? knownManufacturer
                    : string.IsNullOrWhiteSpace(manufacturerCode) ? "Unknown" : manufacturerCode;
                var model = FirstMeaningful(parsed.Model, targetName.MonitorFriendlyDeviceName, "Unknown display");
                var currentRefresh = path.TargetInfo.RefreshRate.Denominator == 0
                    ? 0
                    : Math.Round((double)path.TargetInfo.RefreshRate.Numerator / path.TargetInfo.RefreshRate.Denominator, 2);
                var capabilities = new List<string>();
                if (parsed.DigitalInput) capabilities.Add("Digital input");
                if (parsed.HasPreferredTiming) capabilities.Add("Preferred timing");
                if (parsed.SrgbDefault) capabilities.Add("sRGB default");
                if (parsed.StandbySupported) capabilities.Add("DPMS standby");
                if (parsed.SuspendSupported) capabilities.Add("DPMS suspend");
                if (parsed.ActiveOffSupported) capabilities.Add("DPMS active-off");
                var reasons = new List<string>();
                if (!path.TargetInfo.TargetAvailable) reasons.Add("Windows reports the target unavailable.");
                if (!parsed.Valid) reasons.Add("EDID identity block is missing or invalid.");
                if (string.IsNullOrWhiteSpace(targetName.MonitorFriendlyDeviceName)) reasons.Add("Windows did not return a friendly monitor name.");
                if (currentRefresh <= 0) reasons.Add("Current refresh rate is unavailable.");

                telemetry[sourceName] = new MonitorTelemetry(
                    Manufacturer: manufacturer,
                    ManufacturerCode: manufacturerCode,
                    Model: model,
                    SerialNumber: parsed.SerialNumber,
                    ProductCode: parsed.ProductCode,
                    Connection: FormatConnection(path.TargetInfo.OutputTechnology),
                    ConnectorInstance: targetName.ConnectorInstance,
                    TargetAvailable: path.TargetInfo.TargetAvailable,
                    EdidValid: parsed.Valid,
                    PhysicalWidthCm: parsed.WidthCm,
                    PhysicalHeightCm: parsed.HeightCm,
                    ManufactureWeek: parsed.ManufactureWeek,
                    ManufactureYear: parsed.ManufactureYear,
                    PreferredWidth: preferredMode.Width,
                    PreferredHeight: preferredMode.Height,
                    RefreshRateHz: currentRefresh,
                    AdvancedColorSupported: advancedColor.Supported,
                    AdvancedColorEnabled: advancedColor.Enabled,
                    BitsPerColorChannel: advancedColor.BitsPerColorChannel,
                    Capabilities: capabilities,
                    Health: reasons.Count == 0 ? "Healthy" : "Review",
                    HealthLevel: reasons.Count == 0 ? "ok" : "warn",
                    HealthReasons: reasons,
                    IdentityKey: BuildIdentityKey(manufacturerCode, parsed.ProductCode, parsed.SerialNumber))
                {
                    OutputTechnology = path.TargetInfo.OutputTechnology,
                    DevicePath = targetName.MonitorDevicePath ?? string.Empty,
                    Edid = edidDetails,
                    Signal = signal,
                    SdrWhiteNits = sdrWhite
                };
            }

            MarkDuplicateActivePaths(telemetry);
            break;
        }

        return telemetry;
    }

    private static void MarkDuplicateActivePaths(Dictionary<string, MonitorTelemetry> telemetry)
    {
        var duplicateKeys = telemetry.Values
            .Where(item => !string.IsNullOrWhiteSpace(item.IdentityKey))
            .GroupBy(item => item.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var deviceName in telemetry.Keys.ToArray())
        {
            var item = telemetry[deviceName];
            if (!duplicateKeys.Contains(item.IdentityKey)) continue;
            telemetry[deviceName] = item with
            {
                MultipleActivePaths = true,
                Health = "Review",
                HealthLevel = "warn",
                HealthReasons = item.HealthReasons.Append("The same EDID identity appears on more than one active Windows path.").ToArray()
            };
        }
    }

    private static string BuildIdentityKey(string manufacturer, string productCode, string serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return string.Empty;
        return $"{manufacturer}|{productCode}|{serial}";
    }

    private static DisplayConfigSourceDeviceName GetSourceDeviceName(DisplayConfigPathSourceInfo source)
    {
        var request = new DisplayConfigSourceDeviceName
        {
            Header = CreateHeader(GetSourceName, Marshal.SizeOf<DisplayConfigSourceDeviceName>(), source.AdapterId, source.Id),
            ViewGdiDeviceName = string.Empty
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request : default;
    }

    private static DisplayConfigTargetDeviceName GetTargetDeviceName(DisplayConfigPathTargetInfo target)
    {
        var request = new DisplayConfigTargetDeviceName
        {
            Header = CreateHeader(GetTargetName, Marshal.SizeOf<DisplayConfigTargetDeviceName>(), target.AdapterId, target.Id),
            MonitorFriendlyDeviceName = string.Empty,
            MonitorDevicePath = string.Empty
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request : default;
    }

    private static (uint Width, uint Height) GetPreferredMode(DisplayConfigPathTargetInfo target)
    {
        var request = new DisplayConfigTargetPreferredMode
        {
            Header = CreateHeader(GetTargetPreferredMode, Marshal.SizeOf<DisplayConfigTargetPreferredMode>(), target.AdapterId, target.Id)
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? (request.Width, request.Height) : (0, 0);
    }

    private static (bool Supported, bool Enabled, uint BitsPerColorChannel) GetAdvancedColor(DisplayConfigPathTargetInfo target)
    {
        var request = new DisplayConfigGetAdvancedColorInfo
        {
            Header = CreateHeader(GetAdvancedColorInfo, Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>(), target.AdapterId, target.Id)
        };
        if (DisplayConfigGetDeviceInfo(ref request) != 0) return (false, false, 0);
        return ((request.Value & 0x1) != 0, (request.Value & 0x2) != 0, request.BitsPerColorChannel);
    }

    private static DisplayConfigDeviceInfoHeader CreateHeader(uint type, int size, Luid adapterId, uint id) =>
        new() { Type = type, Size = (uint)size, AdapterId = adapterId, Id = id };

    private static SignalInfo? ReadSignal(DisplayConfigModeInfo mode)
    {
        if (mode.InfoType != 2 || mode.PixelRate == 0) return null;
        var hsync = mode.HSyncDenominator == 0 ? 0 : (double)mode.HSyncNumerator / mode.HSyncDenominator / 1000;
        return new SignalInfo(Math.Round(mode.PixelRate / 1_000_000.0, 2), Math.Round(hsync, 2), mode.TotalWidth, mode.TotalHeight);
    }

    // Windows' SDR content brightness for an HDR display, in nits (the value is in units of 80/1000 nits).
    private static double GetSdrWhiteNits(DisplayConfigPathTargetInfo target)
    {
        var request = new DisplayConfigSdrWhiteLevel
        {
            Header = CreateHeader(GetSdrWhiteLevel, Marshal.SizeOf<DisplayConfigSdrWhiteLevel>(), target.AdapterId, target.Id)
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? Math.Round(request.SdrWhiteLevel * 80.0 / 1000) : 0;
    }

    // The registry often keeps only the 128-byte base block. When the base block says there are extension blocks the
    // registry lacks, they come from WMI (root\wmi WmiMonitorDescriptorMethods, readable without admin). Cached: EDIDs
    // don't change while a monitor stays on the same path, and the WMI read is slow.
    private static readonly object EdidGate = new();
    private static readonly Dictionary<string, (DateTime At, byte[]? Edid, string Source)> EdidCache = new(StringComparer.OrdinalIgnoreCase);

    internal static byte[]? ReadFullEdid(string? monitorDevicePath, out string source)
    {
        source = "none";
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return null;
        lock (EdidGate)
        {
            if (EdidCache.TryGetValue(monitorDevicePath, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10))
            {
                source = cached.Source;
                return cached.Edid;
            }
        }
        var edid = ReadEdid(monitorDevicePath);
        source = edid is null ? "none" : "registry";
        if (edid is { Length: >= 128 } && edid[126] > 0 && edid.Length < 128 * (1 + edid[126]))
        {
            var extended = ReadWmiExtensions(monitorDevicePath, edid);
            if (extended is not null) { edid = extended; source = "registry + WMI"; }
        }
        lock (EdidGate) EdidCache[monitorDevicePath] = (DateTime.UtcNow, edid, source);
        return edid;
    }

    private static byte[]? ReadWmiExtensions(string monitorDevicePath, byte[] baseBlock)
    {
        var parts = monitorDevicePath.Split('#');
        if (parts.Length < 3) return null;
        var prefix = $@"DISPLAY\{parts[1]}\{parts[2]}_";
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName FROM WmiMonitorDescriptorMethods");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (item["InstanceName"] is not string name || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var all = new List<byte>(baseBlock.AsSpan(0, 128).ToArray());
                    for (var block = 1; block <= Math.Min((int)baseBlock[126], 7); block++)
                    {
                        using var input = item.GetMethodParameters("WmiGetMonitorRawEEdidV1Block");
                        input["BlockId"] = (byte)block;
                        using var output = item.InvokeMethod("WmiGetMonitorRawEEdidV1Block", input, null);
                        if (output?["BlockContent"] is not byte[] content || content.Length < 128) break;
                        all.AddRange(content.AsSpan(0, 128).ToArray());
                    }
                    return all.Count > 128 ? all.ToArray() : null;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidCastException)
        {
            // The base block from the registry still gives identity, size and range limits.
        }
        return null;
    }

    private static byte[]? ReadEdid(string? monitorDevicePath)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return null;
        var parts = monitorDevicePath.Split('#');
        if (parts.Length < 3) return null;
        var registryPath = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(registryPath, writable: false);
            return key?.GetValue("EDID") as byte[];
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ParsedEdid ParseEdid(byte[]? edid)
    {
        if (edid is null || edid.Length < 128) return ParsedEdid.Empty;
        var headerValid = edid.AsSpan(0, 8).SequenceEqual(new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 });
        var checksumValid = edid.AsSpan(0, 128).ToArray().Sum(value => value) % 256 == 0;
        var manufacturer = DecodeManufacturer((ushort)((edid[8] << 8) | edid[9]));
        var product = $"{edid[11]:X2}{edid[10]:X2}";
        var model = ReadDescriptor(edid, 0xFC);
        var serial = ReadDescriptor(edid, 0xFF);
        if (string.IsNullOrWhiteSpace(serial))
        {
            var numericSerial = BitConverter.ToUInt32(edid, 12);
            if (numericSerial != 0) serial = numericSerial.ToString();
        }

        var features = edid[24];
        return new ParsedEdid(
            Valid: headerValid && checksumValid,
            ManufacturerCode: manufacturer,
            ProductCode: product,
            Model: model,
            SerialNumber: serial,
            WidthCm: edid[21],
            HeightCm: edid[22],
            ManufactureWeek: edid[16],
            ManufactureYear: edid[17] == 0 ? 0 : 1990 + edid[17],
            DigitalInput: (edid[20] & 0x80) != 0,
            StandbySupported: (features & 0x80) != 0,
            SuspendSupported: (features & 0x40) != 0,
            ActiveOffSupported: (features & 0x20) != 0,
            SrgbDefault: (features & 0x04) != 0,
            HasPreferredTiming: (features & 0x02) != 0);
    }

    private static string ReadDescriptor(byte[] edid, byte tag)
    {
        for (var offset = 54; offset + 18 <= 126; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 3] != tag) continue;
            return System.Text.Encoding.ASCII.GetString(edid, offset + 5, 13).Trim('\0', '\r', '\n', ' ');
        }
        return string.Empty;
    }

    private static string DecodeManufacturer(ushort value)
    {
        var first = (char)('A' + ((value >> 10) & 0x1F) - 1);
        var second = (char)('A' + ((value >> 5) & 0x1F) - 1);
        var third = (char)('A' + (value & 0x1F) - 1);
        return char.IsLetter(first) && char.IsLetter(second) && char.IsLetter(third)
            ? new string([first, second, third])
            : string.Empty;
    }

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY (wingdi.h). 7 is unused; 0xFFFFFFFF is OTHER.
    internal static string FormatConnection(uint technology) => technology switch
    {
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite video",
        3 => "Component video",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        8 => "D-JPN",
        9 => "SDI",
        10 => "DisplayPort",
        11 => "Embedded DisplayPort",
        12 => "UDI",
        13 => "Embedded UDI",
        14 => "SDTV dongle",
        15 => "Miracast",
        16 => "USB or indirect display",
        17 => "Virtual display",
        18 => "DisplayPort over USB-C",
        0xFFFFFFFF => "Other connector",
        0x80000000 => "Internal panel",
        _ => $"Connector {technology}"
    };

    private static string FirstMeaningful(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        nint currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetPreferredMode requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigGetAdvancedColorInfo requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSdrWhiteLevel requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSdrWhiteLevel
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint SdrWhiteLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    // DISPLAYCONFIG_MODE_INFO: infoType, id, adapterId, then a 48-byte union. For a target mode (infoType 2) the union
    // is DISPLAYCONFIG_VIDEO_SIGNAL_INFO: pixelRate, hSyncFreq, vSyncFreq, activeSize, totalSize, videoStandard, scanline.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(16)] public ulong PixelRate;
        [FieldOffset(24)] public uint HSyncNumerator;
        [FieldOffset(28)] public uint HSyncDenominator;
        [FieldOffset(48)] public uint TotalWidth;
        [FieldOffset(52)] public uint TotalHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
        public readonly bool EdidIdsValid => (Flags & 0x2) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigTargetPreferredMode
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Width;
        public uint Height;
        public DisplayConfigTargetMode TargetMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigTargetMode
    {
        public DisplayConfigVideoSignalInfo TargetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigVideoSignalInfo
    {
        public ulong PixelRate;
        public DisplayConfigRational HSyncFreq;
        public DisplayConfigRational VSyncFreq;
        public DisplayConfig2DRegion ActiveSize;
        public DisplayConfig2DRegion TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfig2DRegion
    {
        public uint Cx;
        public uint Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    private sealed record ParsedEdid(
        bool Valid,
        string ManufacturerCode,
        string ProductCode,
        string Model,
        string SerialNumber,
        int WidthCm,
        int HeightCm,
        int ManufactureWeek,
        int ManufactureYear,
        bool DigitalInput,
        bool StandbySupported,
        bool SuspendSupported,
        bool ActiveOffSupported,
        bool SrgbDefault,
        bool HasPreferredTiming)
    {
        internal static ParsedEdid Empty { get; } = new(false, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, 0, 0, false, false, false, false, false, false);
    }
}

internal sealed record MonitorTelemetry(
    string Manufacturer,
    string ManufacturerCode,
    string Model,
    string SerialNumber,
    string ProductCode,
    string Connection,
    uint ConnectorInstance,
    bool TargetAvailable,
    bool EdidValid,
    int PhysicalWidthCm,
    int PhysicalHeightCm,
    int ManufactureWeek,
    int ManufactureYear,
    uint PreferredWidth,
    uint PreferredHeight,
    double RefreshRateHz,
    bool AdvancedColorSupported,
    bool AdvancedColorEnabled,
    uint BitsPerColorChannel,
    IReadOnlyList<string> Capabilities,
    string Health,
    string HealthLevel,
    IReadOnlyList<string> HealthReasons,
    string IdentityKey,
    bool MultipleActivePaths = false)
{
    public uint OutputTechnology { get; init; }
    public string DevicePath { get; init; } = string.Empty;
    public EdidDetails Edid { get; init; } = EdidDetails.Empty;
    public SignalInfo? Signal { get; init; }
    public double SdrWhiteNits { get; init; }
}
