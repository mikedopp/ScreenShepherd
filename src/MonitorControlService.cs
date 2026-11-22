using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ScreenShepherd;

/// <summary>
/// Each monitor's own settings over DDC/CI (dxva2.dll, the MCCS codes): brightness, contrast, input, colour preset,
/// power mode, the monitor's count of hours on, and the MCCS version. DDC/CI is slow (tens of milliseconds a code, a
/// second or more for the capabilities string) and a few monitors flicker when asked, so reads run in the background at
/// most once a minute and the state read only ever sees the cached result. Writes happen only when the page asks, and
/// only for a monitor the user allowed (AppHostService checks that).
/// </summary>
public sealed class MonitorControlService
{
    internal const byte Brightness = 0x10, Contrast = 0x12, ColorPreset = 0x14, InputSource = 0x60, PowerMode = 0xD6, UsageHours = 0xC0, VcpVersion = 0xDF;
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly Dictionary<string, DdcReading> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<int>> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _readAt = DateTime.MinValue;
    private int _reading;

    /// <summary>Cached readings by device name. Starts a background read when they are older than a minute.</summary>
    public IReadOnlyDictionary<string, DdcReading> GetCached(IReadOnlyList<MonitorSnapshot> monitors, bool enabled)
    {
        if (!enabled) return new Dictionary<string, DdcReading>();
        if (DateTime.UtcNow - _readAt > MaxAge && Interlocked.Exchange(ref _reading, 1) == 0)
        {
            var targets = monitors.Select(m => (m.DeviceName, m.Bounds, m.OutputTechnology)).ToArray();
            Task.Run(() =>
            {
                try { ReadAll(targets); }
                finally { _readAt = DateTime.UtcNow; Volatile.Write(ref _reading, 0); }
            });
        }
        lock (_gate) return new Dictionary<string, DdcReading>(_cache, StringComparer.OrdinalIgnoreCase);
    }

    public void Invalidate() => _readAt = DateTime.MinValue;

    private void ReadAll((string Device, RectSnapshot Bounds, uint Technology)[] targets)
    {
        var fresh = new Dictionary<string, DdcReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var (device, bounds, technology) in targets)
        {
            if (technology == MonitorInventoryService.MiracastTechnology)
            {
                fresh[device] = DdcReading.NotAvailable("Not available over Miracast.");
                continue;
            }
            fresh[device] = WithMonitor(bounds, handle => Read(device, handle)) ?? DdcReading.NotAvailable("No answer. Wireless HDMI kits, docks, TVs and most projectors don't pass monitor settings through, and some monitors have DDC/CI turned off in their own menu.");
        }
        lock (_gate)
        {
            _cache.Clear();
            foreach (var pair in fresh) _cache[pair.Key] = pair.Value;
        }
    }

    private DdcReading? Read(string device, nint handle)
    {
        var values = new Dictionary<byte, (uint Current, uint Max)>();
        foreach (var code in new[] { Brightness, Contrast, InputSource, ColorPreset, PowerMode, UsageHours, VcpVersion })
            if (GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out var current, out var max)) values[code] = (current, max);
        if (values.Count == 0) return null;

        IReadOnlyList<int> inputs;
        lock (_gate) _inputs.TryGetValue(device, out inputs!);
        if (inputs is null)
        {
            inputs = ReadInputChoices(handle);
            lock (_gate) _inputs[device] = inputs;
        }
        int? Value(byte code) => values.TryGetValue(code, out var v) ? (int)v.Current : null;
        int? Max(byte code) => values.TryGetValue(code, out var v) ? (int)v.Max : null;
        var input = Value(InputSource) is { } raw ? raw & 0xFF : (int?)null;
        var version = Value(VcpVersion) is { } ver and > 0 ? $"MCCS {(ver >> 8) & 0xFF}.{ver & 0xFF}" : string.Empty;
        return new DdcReading(
            Available: true,
            Note: string.Empty,
            Brightness: Value(Brightness),
            BrightnessMax: Max(Brightness),
            Contrast: Value(Contrast),
            ContrastMax: Max(Contrast),
            Input: input,
            InputName: input is { } i ? InputName(i) : string.Empty,
            Inputs: inputs.Select(i => new DdcChoice(i, InputName(i))).ToArray(),
            ColorPreset: Value(ColorPreset) is { } preset ? PresetName(preset & 0xFF) : string.Empty,
            PowerMode: Value(PowerMode) is { } power ? PowerName(power & 0xFF) : string.Empty,
            UsageHours: Value(UsageHours) is { } hours and > 0 ? hours : null,
            Version: version,
            ReadAt: DateTimeOffset.Now);
    }

    // The input codes the monitor lists for VCP 60 in its capabilities string, e.g. "60(0F 11 12)".
    private static IReadOnlyList<int> ReadInputChoices(nint handle)
    {
        try
        {
            if (!GetCapabilitiesStringLength(handle, out var length) || length == 0 || length > 16384) return Array.Empty<int>();
            var buffer = new byte[length];
            if (!CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length)) return Array.Empty<int>();
            return ParseInputChoices(System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0'));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Array.Empty<int>();
        }
    }

    internal static IReadOnlyList<int> ParseInputChoices(string capabilities)
    {
        var match = Regex.Match(capabilities, @"(?:^|[\s(])60\s*\(([0-9A-Fa-f\s]+)\)");
        if (!match.Success) return Array.Empty<int>();
        return match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(hex => int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : -1)
            .Where(v => v > 0).Distinct().ToArray();
    }

    /// <summary>Sends one setting to the monitor at <paramref name="bounds"/>. The caller has checked the user allowed it.</summary>
    public bool Write(RectSnapshot bounds, byte code, uint value)
    {
        var ok = WithMonitor<bool?>(bounds, handle => SetVCPFeature(handle, code, value)) ?? false;
        Invalidate();
        return ok;
    }

    private static T? WithMonitor<T>(RectSnapshot bounds, Func<nint, T?> action)
    {
        var center = new NativePoint { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
        try
        {
            var hmonitor = MonitorFromPoint(center, 0);
            if (hmonitor == 0 || !GetNumberOfPhysicalMonitorsFromHMONITOR(hmonitor, out var count) || count == 0) return default;
            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hmonitor, count, physical)) return default;
            try { return action(physical[0].Handle); }
            finally { DestroyPhysicalMonitors(count, physical); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return default;
        }
    }

    internal static string InputName(int code) => code switch
    {
        0x01 => "VGA 1", 0x02 => "VGA 2", 0x03 => "DVI 1", 0x04 => "DVI 2", 0x05 => "Composite 1", 0x06 => "Composite 2",
        0x07 => "S-Video 1", 0x08 => "S-Video 2", 0x09 => "Tuner 1", 0x0A => "Tuner 2", 0x0B => "Tuner 3",
        0x0C => "Component 1", 0x0D => "Component 2", 0x0E => "Component 3", 0x0F => "DisplayPort 1", 0x10 => "DisplayPort 2",
        0x11 => "HDMI 1", 0x12 => "HDMI 2", 0x13 => "HDMI 3", 0x14 => "HDMI 4",
        _ => $"Input {code:X2} (maker's own code)"
    };

    internal static string PresetName(int code) => code switch
    {
        0x01 => "sRGB", 0x02 => "Native", 0x03 => "4000 K", 0x04 => "5000 K", 0x05 => "6500 K", 0x06 => "7500 K",
        0x07 => "8200 K", 0x08 => "9300 K", 0x09 => "10000 K", 0x0A => "11500 K", 0x0B => "User 1", 0x0C => "User 2", 0x0D => "User 3",
        _ => $"Preset {code:X2}"
    };

    internal static string PowerName(int code) => code switch
    {
        0x01 => "On", 0x02 => "Standby", 0x03 => "Suspend", 0x04 => "Off", 0x05 => "Off (power button)", _ => $"Mode {code}"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public nint Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint hMonitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(nint monitor, byte code, out uint type, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVCPFeature(nint monitor, byte code, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCapabilitiesStringLength(nint monitor, out uint length);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(nint monitor, byte[] capabilities, uint length);
}

public sealed record DdcChoice(int Code, string Name);

public sealed record DdcReading(
    bool Available,
    string Note,
    int? Brightness,
    int? BrightnessMax,
    int? Contrast,
    int? ContrastMax,
    int? Input,
    string InputName,
    IReadOnlyList<DdcChoice> Inputs,
    string ColorPreset,
    string PowerMode,
    int? UsageHours,
    string Version,
    DateTimeOffset? ReadAt)
{
    public static DdcReading NotAvailable(string note) =>
        new(false, note, null, null, null, null, null, string.Empty, Array.Empty<DdcChoice>(), string.Empty, string.Empty, null, string.Empty, DateTimeOffset.Now);
}
