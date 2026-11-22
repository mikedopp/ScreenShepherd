using System.Text.Json;

namespace ScreenShepherd;

/// <summary>
/// Displays that come and go. Keeps:
/// <list type="bullet">
/// <item>the window layout per display, sampled every few seconds, so when a display goes away its windows' places are
/// known (Windows has already moved them by the time it tells anyone);</item>
/// <item>a parked layout per away display, put back when the same display (by EDID identity) returns;</item>
/// <item>a history of when each display came and went, kept 30 days in history.json;</item>
/// <item>the names and kinds the user gave displays, and which ones may be changed over DDC/CI, in displays.json.</item>
/// </list>
/// A snapshot run reads these files but never changes them.
/// </summary>
public sealed class DisplayWatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _bookFile;
    private readonly string _historyFile;
    private readonly bool _readOnly;
    private DisplayBook _book;
    private List<DisplayEvent> _events;
    private Dictionary<string, PresentDisplay>? _present;
    private Dictionary<string, List<ParkedWindow>> _layout = new(StringComparer.OrdinalIgnoreCase);
    private string _layoutDevices = string.Empty;

    public DisplayWatch(string folder, bool readOnly)
    {
        _bookFile = Path.Combine(folder, "displays.json");
        _historyFile = Path.Combine(folder, "history.json");
        _readOnly = readOnly;
        _book = Load<DisplayBook>(_bookFile) ?? new DisplayBook();
        _events = Load<List<DisplayEvent>>(_historyFile) ?? new List<DisplayEvent>();
    }

    public string BookFile => _bookFile;
    public string HistoryFile => _historyFile;

    /// <summary>The key a display is remembered by: its EDID identity (maker, product, serial), or without a serial its
    /// maker, product, connection and connector.</summary>
    public static string KeyOf(MonitorSnapshot m) =>
        !string.IsNullOrWhiteSpace(m.IdentityKey)
            ? m.IdentityKey
            : $"{m.ManufacturerCode}|{m.ProductCode}|{m.Connection}|c{m.ConnectorInstance}|{m.Model}";

    // ---------- names, kinds, DDC/CI permission ----------

    public string NameOf(string key) { lock (_gate) return _book.Names.GetValueOrDefault(key) ?? string.Empty; }
    public string KindOf(string key) { lock (_gate) return _book.Kinds.GetValueOrDefault(key) ?? string.Empty; }
    public bool DdcAllowed(string key) { lock (_gate) return _book.DdcAllow.GetValueOrDefault(key); }

    public void SetName(string key, string name, string kind)
    {
        name = name.Trim();
        if (name.Length > 40) name = name[..40];
        if (kind is not ("" or "wired" or "kit" or "dock" or "miracast")) throw new ArgumentException("Unknown display kind: " + kind);
        lock (_gate)
        {
            if (name.Length == 0) _book.Names.Remove(key); else _book.Names[key] = name;
            if (kind.Length == 0) _book.Kinds.Remove(key); else _book.Kinds[key] = kind;
            SaveBook();
        }
    }

    public void SetDdcAllowed(string key, bool allow)
    {
        lock (_gate)
        {
            if (allow) _book.DdcAllow[key] = true; else _book.DdcAllow.Remove(key);
            SaveBook();
        }
    }

    // ---------- layout sampling ----------

    /// <summary>Remembers where every window is, per display. A sample taken while the set of displays differs from
    /// the last one is not stored: Windows may already have moved windows off a display that just went away.</summary>
    /// <returns>True when the set of displays changed since the last sample.</returns>
    public bool Sample(IReadOnlyList<MonitorSnapshot> monitors, IReadOnlyList<WindowSnapshot> windows)
    {
        var devices = string.Join("|", monitors.Select(m => $"{m.DeviceName}@{m.Bounds.X},{m.Bounds.Y},{m.Bounds.Width},{m.Bounds.Height}").Order());
        lock (_gate)
        {
            if (_layoutDevices.Length > 0 && devices != _layoutDevices) return true;
            _layoutDevices = devices;
            _layout = windows.Where(w => w.MonitorDevice.Length > 0)
                .GroupBy(w => w.MonitorDevice, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(ParkedWindow.From).ToList(), StringComparer.OrdinalIgnoreCase);
            return false;
        }
    }

    // ---------- topology ----------

    /// <summary>Compares the displays now with the ones last seen. Records what went away (parking its windows) and
    /// what came back. <paramref name="windowsNow"/> is where Windows has put things after the change.</summary>
    public TopologyChange Observe(IReadOnlyList<MonitorSnapshot> monitors, IReadOnlyList<WindowSnapshot> windowsNow, AppSettings settings)
    {
        var now = DateTimeOffset.Now;
        var current = monitors.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.First());
        var gone = new List<GoneDisplay>();
        var back = new List<ReturnedDisplay>();
        lock (_gate)
        {
            if (_present is null)
            {
                // First look this run: everything present starts a session now.
                _present = current.ToDictionary(p => p.Key, p => new PresentDisplay(p.Value.DeviceName, now, Describe(p.Value), p.Value.Bounds));
                if (!_readOnly)
                {
                    Add(new DisplayEvent(now, "start", string.Empty, string.Empty, 0, 0, null, current.Keys.ToArray()));
                    foreach (var m in current.Values) Remember(m, now);
                    SaveBook();
                }
                _layoutDevices = string.Empty;
                return new TopologyChange(gone, back);
            }

            foreach (var (key, was) in _present.ToArray())
            {
                if (current.ContainsKey(key)) continue;
                _present.Remove(key);
                var minutes = (now - was.Since).TotalMinutes;
                var parked = _layout.GetValueOrDefault(was.Device) ?? new List<ParkedWindow>();
                // Where Windows put each one, so a window the user moves afterwards is left alone on the way back.
                var landed = windowsNow.ToDictionary(w => w.Handle, w => w.Bounds);
                parked = parked.Select(p => p with { Landed = landed.GetValueOrDefault(p.Handle) }).Where(p => p.Landed is not null).ToList();
                if (!_readOnly)
                {
                    Add(new DisplayEvent(now, "away", key, was.Name, minutes, parked.Count, null, null));
                    if (settings.PutBack && parked.Count > 0) _book.Parked[key] = new ParkedLayout(now, was.Bounds, parked);
                }
                gone.Add(new GoneDisplay(key, NameOrDescribe(key, was.Name), minutes, parked.Count));
            }

            foreach (var (key, m) in current)
            {
                if (_present.ContainsKey(key)) continue;
                _present[key] = new PresentDisplay(m.DeviceName, now, Describe(m), m.Bounds);
                var away = _events.LastOrDefault(e => e.Key == key && e.Kind is "away" or "back" or "on");
                double? gap = away is { Kind: "away" } ? (now - away.Time).TotalMinutes : null;
                bool? drop = gap is { } g ? g < settings.DropGapMinutes : null;
                if (!_readOnly)
                {
                    if (away is { Kind: "away" } && drop is { } d) Replace(away, away with { Drop = d });
                    Add(new DisplayEvent(now, gap is null ? "on" : "back", key, Describe(m), gap ?? 0, 0, drop, null));
                    Remember(m, now);
                }
                var parked = _book.Parked.GetValueOrDefault(key);
                if (parked is not null && now - parked.Since > TimeSpan.FromDays(settings.LayoutDays)) parked = null;
                back.Add(new ReturnedDisplay(key, NameOrDescribe(key, Describe(m)), m.DeviceName, m.Bounds, gap, drop, parked));
            }

            // Device names and positions can shift when a display comes or goes.
            foreach (var (key, m) in current) _present[key] = _present[key] with { Device = m.DeviceName, Bounds = m.Bounds };
            _layoutDevices = string.Empty;
            if (!_readOnly && (gone.Count > 0 || back.Count > 0)) { SaveBook(); SaveHistory(); }
        }
        return new TopologyChange(gone, back);
    }

    /// <summary>After a put-back: forgets the parked layout and notes how many went back.</summary>
    public void PutBackDone(string key, int moved)
    {
        if (_readOnly) return;
        lock (_gate)
        {
            _book.Parked.Remove(key);
            var last = _events.LastOrDefault(e => e.Key == key && e.Kind is "back" or "on");
            if (last is not null) Replace(last, last with { Windows = moved });
            SaveBook();
            SaveHistory();
        }
    }

    /// <summary>"Keep them where they are": forgets an away display's parked layout.</summary>
    public void Forget(string key)
    {
        lock (_gate)
        {
            if (_book.Parked.Remove(key)) SaveBook();
        }
    }

    public void Stop()
    {
        if (_readOnly) return;
        lock (_gate)
        {
            if (_present is null) return;
            Add(new DisplayEvent(DateTimeOffset.Now, "stop", string.Empty, string.Empty, 0, 0, null, null));
            SaveHistory();
        }
    }

    // ---------- what the page shows ----------

    /// <summary>Away displays that still have windows waiting for them, drawn as away pens on the map.</summary>
    public IReadOnlyList<AwayDisplay> GetAway(AppSettings settings)
    {
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            return _book.Parked
                .Where(p => (_present is null || !_present.ContainsKey(p.Key)) && now - p.Value.Since <= TimeSpan.FromDays(settings.LayoutDays))
                .Select(p =>
                {
                    var known = _book.Known.GetValueOrDefault(p.Key);
                    var away = _events.LastOrDefault(e => e.Key == p.Key && e.Kind == "away");
                    return new AwayDisplay(p.Key, NameOrDescribe(p.Key, known?.Name ?? "Display"), _book.Names.GetValueOrDefault(p.Key) ?? string.Empty,
                        EffectiveKind(p.Key, known?.Technology ?? 0), known?.Connection ?? string.Empty, p.Value.Bounds, p.Value.Since,
                        away?.Minutes ?? 0, p.Value.Windows);
                })
                .ToArray();
        }
    }

    /// <summary>The last 24 hours: on periods and drops per display, and the events newest first.</summary>
    public DisplayHistory GetHistory(AppSettings settings)
    {
        var now = DateTimeOffset.Now;
        var from = now.AddHours(-24);
        lock (_gate)
        {
            var open = new Dictionary<string, DateTimeOffset>();
            var spans = new Dictionary<string, List<HistorySpan>>();
            var drops = new Dictionary<string, List<DateTimeOffset>>();
            var names = new Dictionary<string, string>();
            DateTimeOffset? watchSince = null;
            void Close(string key, DateTimeOffset at)
            {
                if (!open.Remove(key, out var start)) return;
                if (at > from) (spans.TryGetValue(key, out var l) ? l : spans[key] = new()).Add(new HistorySpan(start < from ? from : start, at));
            }
            foreach (var e in _events)
            {
                if (e.Key.Length > 0 && e.Name.Length > 0) names[e.Key] = e.Name;
                switch (e.Kind)
                {
                    case "start":
                        if (e.Time >= from && watchSince is null) watchSince = e.Time;
                        foreach (var key in open.Keys.ToArray()) Close(key, e.Time);
                        foreach (var key in e.Present ?? Array.Empty<string>()) open[key] = e.Time;
                        break;
                    case "stop":
                        foreach (var key in open.Keys.ToArray()) Close(key, e.Time);
                        break;
                    case "away":
                        Close(e.Key, e.Time);
                        if (e.Drop == true && e.Time >= from) (drops.TryGetValue(e.Key, out var d) ? d : drops[e.Key] = new()).Add(e.Time);
                        break;
                    case "back" or "on":
                        open[e.Key] = e.Time;
                        break;
                }
            }
            if (watchSince is null && _events.Any(e => e.Kind == "start" && e.Time < from)) watchSince = from;
            foreach (var (key, start) in open) (spans.TryGetValue(key, out var l) ? l : spans[key] = new()).Add(new HistorySpan(start < from ? from : start, null));

            var keys = spans.Keys.Union(drops.Keys).ToArray();
            var rows = keys.Select(key => new HistoryRow(
                key, NameOrDescribe(key, names.GetValueOrDefault(key) ?? "Display"),
                spans.GetValueOrDefault(key)?.ToArray() ?? Array.Empty<HistorySpan>(),
                drops.GetValueOrDefault(key)?.ToArray() ?? Array.Empty<DateTimeOffset>(),
                open.TryGetValue(key, out var since) ? since : null)).ToArray();

            var events = _events.Where(e => e.Time >= from && e.Kind is "away" or "back" or "on")
                .Reverse().Take(60)
                .Select(e => new HistoryItem(e.Time, e.Key, NameOrDescribe(e.Key, e.Name), Describe(e, settings, now), WindowsText(e), e.Kind == "away" && e.Drop == true))
                .ToArray();
            var dropCount = _events.Count(e => e.Kind == "away" && e.Drop == true && e.Time >= from);
            return new DisplayHistory(watchSince ?? now, from, now, settings.DropGapMinutes, dropCount, rows, events);
        }
    }

    internal static string Describe(DisplayEvent e, AppSettings settings, DateTimeOffset now) => e.Kind switch
    {
        "away" when e.Drop == true => $"Dropped after {Span(e.Minutes)}",
        "away" when e.Drop == false => $"Turned off after {Span(e.Minutes)}",
        "away" when (now - e.Time).TotalMinutes < settings.DropGapMinutes => $"Went away after {Span(e.Minutes)}",
        "away" => $"Turned off after {Span(e.Minutes)}",
        "back" when e.Drop == true => $"Came back after {Span(e.Minutes)}",
        "back" => $"Turned on after {Span(e.Minutes)} away",
        _ => "Turned on"
    };

    private static string WindowsText(DisplayEvent e) => e.Kind switch
    {
        "away" when e.Windows > 0 => $"{e.Windows} moved by Windows",
        "back" or "on" when e.Windows > 0 => $"{e.Windows} put back",
        _ => string.Empty
    };

    /// <summary>"7 min", "1 h 48 m", "3 d 2 h".</summary>
    internal static string Span(double minutes)
    {
        if (minutes < 1) return "under a minute";
        var t = TimeSpan.FromMinutes(Math.Round(minutes));
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours} h {t.Minutes:00} m";
        return $"{(int)t.TotalDays} d {t.Hours} h";
    }

    /// <summary>The kind the page shows: Miracast is found on its own; the rest is what the user said (wired by default).</summary>
    public string EffectiveKind(string key, uint technology)
    {
        if (technology == MonitorInventoryService.MiracastTechnology) return "miracast";
        lock (_gate) return _book.Kinds.GetValueOrDefault(key) is { Length: > 0 } kind ? kind : "wired";
    }

    public DateTimeOffset? SessionStart(string key)
    {
        lock (_gate) return _present?.GetValueOrDefault(key)?.Since;
    }

    private string NameOrDescribe(string key, string fallback) =>
        _book.Names.GetValueOrDefault(key) is { Length: > 0 } name ? name : fallback;

    private static string Describe(MonitorSnapshot m)
    {
        var maker = m.Manufacturer.Trim();
        var model = m.Model.Trim();
        return model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) || maker is "" or "Unknown" ? model : $"{maker} {model}";
    }

    private void Remember(MonitorSnapshot m, DateTimeOffset now) =>
        _book.Known[KeyOf(m)] = new KnownDisplay(Describe(m), m.Connection, m.OutputTechnology, m.Bounds, now);

    private void Add(DisplayEvent e)
    {
        _events.Add(e);
        var cutoff = DateTimeOffset.Now.AddDays(-30);
        if (_events.Count > 4000 || _events[0].Time < cutoff)
            _events = _events.Where(x => x.Time >= cutoff).TakeLast(4000).ToList();
        SaveHistory();
    }

    private void Replace(DisplayEvent old, DisplayEvent updated)
    {
        var index = _events.LastIndexOf(old);
        if (index >= 0) _events[index] = updated;
    }

    private void SaveBook() { if (!_readOnly) Save(_bookFile, _book); }
    private void SaveHistory() { if (!_readOnly) Save(_historyFile, _events); }

    private static T? Load<T>(string file) where T : class
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            try { File.Copy(file, file + ".bad", true); } catch (IOException) { }
            return null;
        }
    }

    private static void Save<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; the app keeps working without the file.
        }
    }

    private sealed record PresentDisplay(string Device, DateTimeOffset Since, string Name, RectSnapshot Bounds);
}

public sealed class DisplayBook
{
    public Dictionary<string, string> Names { get; set; } = new();
    public Dictionary<string, string> Kinds { get; set; } = new();
    public Dictionary<string, bool> DdcAllow { get; set; } = new();
    public Dictionary<string, ParkedLayout> Parked { get; set; } = new();
    public Dictionary<string, KnownDisplay> Known { get; set; } = new();
}

public sealed record KnownDisplay(string Name, string Connection, uint Technology, RectSnapshot Bounds, DateTimeOffset LastSeen);
public sealed record ParkedLayout(DateTimeOffset Since, RectSnapshot Bounds, List<ParkedWindow> Windows);

public sealed record ParkedWindow(string Handle, string ProcessName, string Title, RectSnapshot Bounds, bool Maximized, bool Minimized, RectSnapshot? Landed)
{
    public static ParkedWindow From(WindowSnapshot w) => new(w.Handle, w.ProcessName, w.Title, w.Bounds, w.Maximized, w.Minimized, null);
}

/// <param name="Kind">start (Screen Shepherd started; Present lists the displays), stop, away, back (after an away this
/// run or an earlier one), on (seen for the first time)</param>
/// <param name="Minutes">for away: how long it had been on; for back: how long it was gone</param>
/// <param name="Drop">for away and back: whether the gap was under the drop threshold (null while still away)</param>
public sealed record DisplayEvent(DateTimeOffset Time, string Kind, string Key, string Name, double Minutes, int Windows, bool? Drop, string[]? Present);

public sealed record GoneDisplay(string Key, string Name, double Minutes, int Windows);
public sealed record ReturnedDisplay(string Key, string Name, string DeviceName, RectSnapshot Bounds, double? GapMinutes, bool? Drop, ParkedLayout? Parked);
public sealed record TopologyChange(IReadOnlyList<GoneDisplay> Gone, IReadOnlyList<ReturnedDisplay> Back);

public sealed record AwayDisplay(string Key, string Name, string Label, string Kind, string Connection, RectSnapshot Bounds, DateTimeOffset Since, double OnMinutes, IReadOnlyList<ParkedWindow> Windows);
public sealed record HistorySpan(DateTimeOffset Start, DateTimeOffset? End);
public sealed record HistoryRow(string Key, string Name, IReadOnlyList<HistorySpan> Spans, IReadOnlyList<DateTimeOffset> Drops, DateTimeOffset? OnSince);
public sealed record HistoryItem(DateTimeOffset Time, string Key, string Name, string What, string Windows, bool Drop);
public sealed record DisplayHistory(DateTimeOffset WatchSince, DateTimeOffset From, DateTimeOffset To, int DropGapMinutes, int DropsToday, IReadOnlyList<HistoryRow> Rows, IReadOnlyList<HistoryItem> Events);
