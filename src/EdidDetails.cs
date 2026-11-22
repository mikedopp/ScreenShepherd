namespace ScreenShepherd;

/// <summary>
/// What a monitor's full EDID says beyond identity: the bandwidth and refresh limits from the range-limits descriptor,
/// HDMI's TMDS limits and variable refresh range from the CTA-861 extension, the HDR block, and how much of DCI-P3 and
/// sRGB its colour primaries cover. Every value is what the monitor advertises, not a measurement.
/// </summary>
public sealed record EdidDetails(
    int ExtensionBlocks,
    string Source,
    double MaxPixelClockMHz,
    int MinVerticalHz,
    int MaxVerticalHz,
    double PreferredPixelClockMHz,
    double HdmiMaxTmdsMHz,
    int HdmiVrrMinHz,
    int HdmiVrrMaxHz,
    bool FreeSyncBlock,
    IReadOnlyList<string> HdrTransfer,
    double HdrMaxNits,
    double HdrMaxAverageNits,
    double HdrMinNits,
    double DciP3CoveragePercent,
    double SrgbCoveragePercent)
{
    public static EdidDetails Empty { get; } = new(0, "none", 0, 0, 0, 0, 0, 0, 0, false, Array.Empty<string>(), 0, 0, 0, 0, 0);

    /// <summary>Parses a full EDID (the 128-byte base block and any 128-byte extension blocks).</summary>
    public static EdidDetails Parse(byte[]? edid, string source)
    {
        if (edid is null || edid.Length < 128) return Empty;
        var details = Empty with { Source = source, ExtensionBlocks = Math.Max(0, edid.Length / 128 - 1) };

        // Detailed timing 1 (bytes 54-71) is the preferred mode; its pixel clock is in 10 kHz units.
        var preferredClock = (edid[54] | edid[55] << 8) / 100.0;
        if (preferredClock > 0) details = details with { PreferredPixelClockMHz = preferredClock };

        // Display range limits descriptor (tag 0xFD): vertical rate range and the maximum pixel clock.
        for (var offset = 54; offset + 18 <= 126; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 3] != 0xFD) continue;
            var flags = edid[offset + 4];
            var minV = edid[offset + 5] + ((flags & 0x01) != 0 ? 255 : 0);
            var maxV = edid[offset + 6] + ((flags & 0x02) != 0 ? 255 : 0);
            var maxClock = edid[offset + 9] * 10.0;
            details = details with { MinVerticalHz = minV, MaxVerticalHz = maxV, MaxPixelClockMHz = maxClock };
        }

        var primaries = ReadPrimaries(edid);
        if (primaries is not null)
        {
            details = details with
            {
                DciP3CoveragePercent = Math.Round(Coverage(primaries, DciP3) * 100),
                SrgbCoveragePercent = Math.Round(Coverage(primaries, Srgb) * 100)
            };
        }

        for (var block = 1; (block + 1) * 128 <= edid.Length; block++)
        {
            var start = block * 128;
            if (edid[start] != 0x02) continue; // only CTA-861 extensions carry these data blocks
            details = ParseCta(edid, start, details);
        }
        return details;
    }

    private static EdidDetails ParseCta(byte[] e, int start, EdidDetails details)
    {
        var dtdOffset = e[start + 2];
        if (dtdOffset < 4 || dtdOffset > 127) return details;
        var i = start + 4;
        var end = start + dtdOffset;
        while (i < end)
        {
            var tag = e[i] >> 5;
            var length = e[i] & 0x1F;
            if (i + length >= start + 128) break;
            if (tag == 3 && length >= 3)
            {
                var oui = e[i + 1] | e[i + 2] << 8 | e[i + 3] << 16;
                if (oui == 0x000C03 && length >= 7 && e[i + 7] > 0)
                    details = details with { HdmiMaxTmdsMHz = Math.Max(details.HdmiMaxTmdsMHz, e[i + 7] * 5.0) };
                else if (oui == 0xC45DD8)
                {
                    if (length >= 5 && e[i + 5] > 0)
                        details = details with { HdmiMaxTmdsMHz = Math.Max(details.HdmiMaxTmdsMHz, e[i + 5] * 5.0) };
                    if (length >= 10)
                    {
                        var vrrMin = e[i + 9] & 0x3F;
                        var vrrMax = (e[i + 9] & 0xC0) << 2 | e[i + 10];
                        if (vrrMin > 0 && vrrMax > vrrMin) details = details with { HdmiVrrMinHz = vrrMin, HdmiVrrMaxHz = vrrMax };
                    }
                }
                else if (oui == 0x00001A)
                    details = details with { FreeSyncBlock = true };
            }
            else if (tag == 7 && length >= 3 && e[i + 1] == 0x06)
            {
                // HDR static metadata: supported transfer functions, then optional luminance code values.
                var eotf = e[i + 2];
                var transfer = new List<string>();
                if ((eotf & 0x04) != 0) transfer.Add("HDR10");
                if ((eotf & 0x08) != 0) transfer.Add("HLG");
                double max = 0, average = 0, min = 0;
                if (length >= 4 && e[i + 4] > 0) max = 50 * Math.Pow(2, e[i + 4] / 32.0);
                if (length >= 5 && e[i + 5] > 0) average = 50 * Math.Pow(2, e[i + 5] / 32.0);
                if (length >= 6 && max > 0) min = max * Math.Pow(e[i + 6] / 255.0, 2) / 100;
                details = details with
                {
                    HdrTransfer = transfer,
                    HdrMaxNits = Math.Round(max),
                    HdrMaxAverageNits = Math.Round(average),
                    HdrMinNits = Math.Round(min, 3)
                };
            }
            i += length + 1;
        }
        return details;
    }

    // CIE 1931 xy of red, green and blue from bytes 25-34 (10-bit values split across a low-bits byte and a high byte).
    private static (double X, double Y)[]? ReadPrimaries(byte[] e)
    {
        double Value(int high, int lowByte, int shift) => ((e[high] << 2) | ((e[lowByte] >> shift) & 0x03)) / 1024.0;
        var red = (Value(27, 25, 6), Value(28, 25, 4));
        var green = (Value(29, 25, 2), Value(30, 25, 0));
        var blue = (Value(31, 26, 6), Value(32, 26, 4));
        var all = new[] { red, green, blue };
        if (all.Any(p => p.Item1 <= 0 || p.Item2 <= 0) || Math.Abs(Area(all)) < 0.01) return null;
        return all;
    }

    internal static readonly (double X, double Y)[] DciP3 = { (0.680, 0.320), (0.265, 0.690), (0.150, 0.060) };
    internal static readonly (double X, double Y)[] Srgb = { (0.640, 0.330), (0.300, 0.600), (0.150, 0.060) };

    /// <summary>Share of the reference triangle that the panel's triangle covers, in CIE 1931 xy.</summary>
    internal static double Coverage((double X, double Y)[] panel, (double X, double Y)[] reference)
    {
        var clipped = Clip(Ccw(reference), Ccw(panel));
        return clipped.Count < 3 ? 0 : Math.Min(1, Math.Abs(Area(clipped)) / Math.Abs(Area(reference)));
    }

    private static (double X, double Y)[] Ccw((double X, double Y)[] p) => Area(p) < 0 ? p.Reverse().ToArray() : p;

    private static double Area(IReadOnlyList<(double X, double Y)> p)
    {
        double sum = 0;
        for (var i = 0; i < p.Count; i++)
        {
            var (x1, y1) = p[i];
            var (x2, y2) = p[(i + 1) % p.Count];
            sum += x1 * y2 - x2 * y1;
        }
        return sum / 2;
    }

    // Sutherland-Hodgman: the subject polygon clipped by a convex counter-clockwise clip polygon.
    private static List<(double X, double Y)> Clip((double X, double Y)[] subject, (double X, double Y)[] clip)
    {
        var output = subject.ToList();
        for (var i = 0; i < clip.Length && output.Count > 0; i++)
        {
            var a = clip[i];
            var b = clip[(i + 1) % clip.Length];
            bool Inside((double X, double Y) p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X) >= 0;
            (double X, double Y) Cross((double X, double Y) p, (double X, double Y) q)
            {
                var a1 = b.Y - a.Y; var b1 = a.X - b.X; var c1 = a1 * a.X + b1 * a.Y;
                var a2 = q.Y - p.Y; var b2 = p.X - q.X; var c2 = a2 * p.X + b2 * p.Y;
                var det = a1 * b2 - a2 * b1;
                return Math.Abs(det) < 1e-12 ? p : ((b2 * c1 - b1 * c2) / det, (a1 * c2 - a2 * c1) / det);
            }
            var input = output;
            output = new List<(double X, double Y)>();
            for (var j = 0; j < input.Count; j++)
            {
                var current = input[j];
                var previous = input[(j + input.Count - 1) % input.Count];
                if (Inside(current))
                {
                    if (!Inside(previous)) output.Add(Cross(previous, current));
                    output.Add(current);
                }
                else if (Inside(previous)) output.Add(Cross(previous, current));
            }
        }
        return output;
    }
}

/// <summary>The signal on the cable right now: the pixel clock of the current mode, from Windows' display paths.</summary>
public sealed record SignalInfo(double PixelClockMHz, double HorizontalKHz, uint TotalWidth, uint TotalHeight);
