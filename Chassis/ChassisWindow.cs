// Chassis app shell kit: copied into the app by Chassis\tools\install.py. Do not edit this copy: change Chassis, then reinstall.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Chassis;

/// <summary>A request the page made to one of the app's routes (ChassisOptions.Routes). Path is unescaped; Query has no leading ?.</summary>
public sealed record ChassisRequest(string Method, string Path, string Query, byte[] Body)
{
    /// <summary>One query value, unescaped ("" when missing).</summary>
    public string Arg(string key)
    {
        foreach (var part in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string k = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            if (k == key) return eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return "";
    }
    public string BodyText => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>The app's answer to a route request.</summary>
public sealed record ChassisResponse(int Status, string ContentType, byte[] Body)
{
    public static ChassisResponse Json(object? value, int status = 200) =>
        new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value));
    public static ChassisResponse Bytes(byte[] body, string contentType) => new(200, contentType, body);
    public static ChassisResponse NotFound() => new(404, "text/plain", System.Text.Encoding.UTF8.GetBytes("not found"));
}

/// <summary>A credential the app uses. Only its name, where it is kept and whether it is set: never the value.</summary>
public sealed record ChassisCredential(string Name, string Where, bool IsSet, string Note = "");

/// <summary>
/// The app window: the native Windows frame (Windows owns move, snap, resize, minimise, maximise, close) holding one
/// WebView2 page served from wwwroot as https://appname.app/, a tray icon, close to tray, and the page bridge.
/// Subclass it: override OnCommand for the page's commands, Hello for data the page needs at start,
/// AddTrayItems for tray menu entries and Credentials when the app keeps any.
/// </summary>
public abstract class ChassisWindow : Window
{
    readonly WebView2 _web = new();
    readonly List<string> _pageErrors = new();
    Forms.NotifyIcon? _tray;
    bool _exiting, _snapping;

    protected ChassisSettings Settings => ChassisApp.Settings;
    protected ChassisLaunch Launch => ChassisApp.Launch;
    protected ChassisOptions Options => ChassisApp.Options;
    protected bool IsSnapshot => Launch.Snapshot != null;
    public bool PageReady { get; private set; }
    public int ExitCode { get; private set; }

    protected ChassisWindow()
    {
        Title = Options.AppName;
        Width = Launch.Width ?? Options.Width;
        Height = Launch.Height ?? Options.Height;
        MinWidth = Math.Min(Options.MinWidth, Width);
        MinHeight = Math.Min(Options.MinHeight, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        var page = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Options.PageColor);
        Background = new System.Windows.Media.SolidColorBrush(page);
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(page.R, page.G, page.B);
        Content = _web;
        Loaded += async (_, _) => await StartAsync();
        Closing += OnClosing;
        if (IsSnapshot)
        {
            ShowActivated = false;
            // a page that breaks before it is ready must fail the snapshot, not hang it
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            watchdog.Tick += (_, _) => { if (!_snapping) { ChassisLog.Write("snapshot: the page never became ready"); EndNow(4); } };
            watchdog.Start();
        }
        else
        {
            MakeTray();
            SystemEvents.UserPreferenceChanged += OnSystemChanged;
            if (StartHidden)
            {
                // The page needs a window that was really shown once, so it opens far off every screen, hides, and is put
                // back in the middle of the main screen for when the tray icon or a second launch shows it.
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -32000; Top = -32000;
                ShowActivated = false;
                ShowInTaskbar = false;
                Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    Hide();
                    var area = SystemParameters.WorkArea;
                    Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
                    Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
                    ShowInTaskbar = true;
                    ShowActivated = true;
                    ChassisLog.Write("started hidden in the tray");
                });
            }
        }
    }

    // ---------- the app's hooks ----------

    /// <summary>A command from the page that Chassis doesn't handle itself.</summary>
    protected virtual void OnCommand(string cmd, JsonElement message) { }
    /// <summary>Extra fields for the hello message (the page gets them as hello.app).</summary>
    protected virtual object? Hello() => null;
    /// <summary>Called once the page has said ready and has its hello.</summary>
    protected virtual void OnPageReady() { }
    /// <summary>Entries between the version line and Exit in the tray menu.</summary>
    protected virtual void AddTrayItems(Forms.ContextMenuStrip menu) { }
    /// <summary>Credentials the app uses. Null (the default) hides the Credentials tab.</summary>
    protected virtual IEnumerable<ChassisCredential>? Credentials() => null;
    /// <summary>Called once before the process ends: tray Exit, close with close-to-tray off, and every snapshot exit.</summary>
    protected virtual void OnExiting() { }
    /// <summary>
    /// Asked before tray Exit (or close with close-to-tray off) ends the app; false keeps it running. Use it to ask about
    /// unsaved work. Not asked on snapshot exits.
    /// </summary>
    protected virtual bool ConfirmExit() => true;
    /// <summary>
    /// Answers a page request under one of ChassisOptions.Routes. Runs on a worker thread, not the UI thread. Null gives 404;
    /// an exception gives 500 and is logged.
    /// </summary>
    protected virtual ChassisResponse? Serve(ChassisRequest request) => null;
    /// <summary>
    /// True starts the app in the tray with no window on screen. The default is the --hidden switch; override it to add
    /// the app's own "start hidden" setting. Read while the window is being built: use only Launch and Settings.
    /// </summary>
    protected virtual bool StartHidden => Launch.Hidden;
    /// <summary>
    /// The tray icon, for an app that draws a separate small one. Null (the default) uses the exe's icon. Called while the
    /// window is being built: load it from a file or resource, don't touch the window's own fields.
    /// </summary>
    protected virtual System.Drawing.Icon? TrayIcon() => null;
    /// <summary>Called after the page changed settings (the shell's or the app's own) and they were saved.</summary>
    protected virtual void OnSettingsChanged() { }

    // ---------- start ----------

    async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(ChassisPaths.WebViewData);
            var env = await CoreWebView2Environment.CreateAsync(null, ChassisPaths.WebViewData);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = Options.EditMenu;
            if (Options.EditMenu) core.ContextMenuRequested += (_, e) => TrimToEditMenu(e);
            core.Settings.IsZoomControlEnabled = false;
            // the page only ever loads from wwwroot: no network, no CDN (an app may list hosts its page fetches data from)
            // A folder mapping is served inside WebView2 and never raises WebResourceRequested, so an app with routes has
            // Chassis serve wwwroot itself (ServeFile): page and routes stay one origin.
            bool selfServed = Options.Routes.Count > 0;
            if (!selfServed)
                core.SetVirtualHostNameToFolderMapping(ChassisPaths.HostName, ChassisPaths.Web, CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationStarting += (_, e) => { if (!IsAllowed(e.Uri)) { e.Cancel = true; ChassisLog.Write("blocked navigation away from the app"); } };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!IsAllowed(e.Request.Uri))
                {
                    // a host the app listed: only data (fetch, XHR, pictures), never a script, style, frame or page
                    if (HostAllowed(e.Request.Uri, Options.Hosts) && e.ResourceContext is CoreWebView2WebResourceContext.Fetch
                        or CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Image) return;
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                    ChassisLog.Write("blocked a request to another host");
                    return;
                }
                if (RouteOf(e.Request.Uri, Options.Routes) != null) Route(core, e);
                else if (selfServed) ServeFile(core, e);
            };
            core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
            core.ProcessFailed += (_, e) => ChassisLog.Write("WebView2 process failed: " + e.ProcessFailedKind);
            core.Navigate(Launch.StartPage(ChassisPaths.Origin));
            ChassisLog.Write($"{Options.AppName} {ChassisApp.BuildVersion} opened; WebView2 {core.Environment.BrowserVersionString}; data {ChassisPaths.Folder}");
        }
        catch (Exception ex)
        {
            ChassisLog.Write("start failed: " + ex);
            if (IsSnapshot) EndNow(1);
            MessageBox.Show(Options.AppName + " needs the Microsoft Edge WebView2 Runtime.\n\n" + ex.Message, Options.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            Exit();
        }
    }

    /// <summary>Only the app's own origin and inline data may load.</summary>
    public static bool IsAllowed(string uri) =>
        uri.StartsWith(ChassisPaths.Origin, StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
        || uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);

    /// <summary>An https address on one of the hosts the app listed (ChassisOptions.Hosts), by exact host name.</summary>
    public static bool HostAllowed(string uri, IReadOnlyCollection<string> hosts) =>
        hosts.Count > 0 && Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && hosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The request path when the address is on the app's own origin under one of the routes, else null.</summary>
    public static string? RouteOf(string uri, IReadOnlyCollection<string> routes)
    {
        if (routes.Count == 0 || !uri.StartsWith(ChassisPaths.Origin, StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return null;
        string path = u.AbsolutePath;   // dot segments are already resolved here
        return routes.Any(r => path.StartsWith(r, StringComparison.Ordinal)) ? path : null;
    }

    /// <summary>File types the page may load when Chassis serves wwwroot itself.</summary>
    public static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".wasm" => "application/wasm",
        ".glb" => "model/gltf-binary",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => "application/octet-stream",
    };

    /// <summary>The file in wwwroot an address on the app's origin names, or null when it would leave wwwroot.</summary>
    public static string? WebFileOf(string uri)
    {
        if (!uri.StartsWith(ChassisPaths.Origin, StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(uri, UriKind.Absolute, out var u)) return null;
        string rel = Uri.UnescapeDataString(u.AbsolutePath).TrimStart('/');
        if (rel.Length == 0) rel = "index.html";
        string root = Path.GetFullPath(ChassisPaths.Web).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    void ServeFile(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string? file = WebFileOf(e.Request.Uri);
        if (file == null || !File.Exists(file) || e.Request.Method is not ("GET" or "HEAD"))
        {
            e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
            return;
        }
        try
        {
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(file)), 200, "OK",
                "Content-Type: " + MimeOf(file) + "\r\nCache-Control: no-cache\r\nX-Content-Type-Options: nosniff");
        }
        catch (Exception ex)
        {
            ChassisLog.Write("page file unreadable: " + ex.Message);
            e.Response = core.Environment.CreateWebResourceResponse(null, 500, "Error", "");
        }
    }

    /// <summary>Hands a route request to the app on a worker thread and answers the page when it is done.</summary>
    void Route(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        var u = new Uri(e.Request.Uri);
        byte[] body = Array.Empty<byte>();
        try
        {
            if (e.Request.Content is Stream s) { using var ms = new MemoryStream(); s.CopyTo(ms); body = ms.ToArray(); }
        }
        catch (Exception ex) { ChassisLog.Write("route body unreadable: " + ex.Message); }
        var request = new ChassisRequest(e.Request.Method, Uri.UnescapeDataString(u.AbsolutePath), u.Query.TrimStart('?'), body);
        Task.Run(() =>
        {
            try { return Serve(request) ?? ChassisResponse.NotFound(); }
            catch (Exception ex)
            {
                ChassisLog.Write($"route {request.Method} {request.Path} failed: {ex.Message}");
                return ChassisResponse.Json(new { error = ex.Message }, 500);
            }
        }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var r = t.Result;
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(r.Body), r.Status, r.Status < 400 ? "OK" : "Error",
                    "Content-Type: " + r.ContentType + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            }
            catch (Exception ex) { ChassisLog.Write("route answer failed: " + ex.Message); }
            finally { deferral.Complete(); }
        }));
    }

    /// <summary>The context menu entries kept by ChassisOptions.EditMenu (WebView2 item names).</summary>
    public static bool IsEditMenuItem(string name) =>
        name is "copy" or "cut" or "paste" or "pasteAndMatchStyle" or "selectAll" or "undo" or "redo";

    static void TrimToEditMenu(CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var items = e.MenuItems;
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i].Kind != CoreWebView2ContextMenuItemKind.Separator && !IsEditMenuItem(items[i].Name)) items.RemoveAt(i);
        // no separator at either end or twice in a row
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i].Kind == CoreWebView2ContextMenuItemKind.Separator
                && (i == 0 || i == items.Count - 1 || items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator))
                items.RemoveAt(i);
        if (items.Count == 0) e.Handled = true;
    }

    /// <summary>Puts text on the clipboard, retrying while another program holds it open.</summary>
    public static bool CopyText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(40); }
        }
        ChassisLog.Write("copy failed: the clipboard stayed busy");
        return false;
    }

    // ---------- page -> host ----------

    void OnMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            string cmd = m.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
            switch (cmd)
            {
                case "ready":
                    SendHello();
                    PageReady = true;
                    OnPageReady();
                    break;
                case "settings":
                    Settings.Apply(m);
                    if (!IsSnapshot) Settings.Save();
                    try { OnSettingsChanged(); } catch (Exception ex) { ChassisLog.Write("settings hook failed: " + ex.Message); }
                    break;
                case "copy":
                    var text = m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (text.Length is > 0 and <= 2_000_000 && !CopyText(text)) Done("Couldn't reach the clipboard. Try again.", true);
                    break;
                case "diag": SendDiag(); break;
                case "log": ChassisLog.Write("page: " + (m.TryGetProperty("text", out var l) ? l.GetString() : "")); break;
                case "pageError":
                    var err = m.TryGetProperty("text", out var et) ? et.GetString() ?? "" : "";
                    lock (_pageErrors) _pageErrors.Add(err);
                    ChassisLog.Write("page error: " + err);
                    break;
                case "openDataFolder":
                    Directory.CreateDirectory(ChassisPaths.Folder);
                    Process.Start(new ProcessStartInfo("explorer.exe", ChassisPaths.Folder) { UseShellExecute = true });
                    break;
                case "hide": Hide(); break;
                case "exit": Exit(); break;
                case "snapshotReady": if (IsSnapshot && !_snapping) { _snapping = true; _ = Snapshot(); } break;
                default: OnCommand(cmd, m); break;
            }
        }
        catch (Exception ex) { ChassisLog.Write("message failed: " + ex.Message); }
    }

    // ---------- host -> page ----------

    /// <summary>Messages to the page: camelCase, nulls left out, not indented (large payloads stay small).</summary>
    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Sends any object to the page as JSON (camelCase).</summary>
    public void Post(object message)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Wire)); }
        catch (Exception ex) { ChassisLog.Write("post failed: " + ex.Message); }
    }

    /// <summary>Shows a busy line and turns the orb to "thinking".</summary>
    public void Busy(string text) => Post(new { type = "chassis.busy", text });
    /// <summary>Clears busy; an error turns the orb red for a moment.</summary>
    public void Done(string text, bool error = false) => Post(new { type = "chassis.done", text, error });

    void SendHello()
    {
        var creds = Credentials()?.Select(x => new { x.Name, x.Where, x.IsSet, x.Note }).ToArray();
        Post(new
        {
            type = "chassis.hello",
            app = Options.AppName,
            version = ChassisApp.Version,
            build = ChassisApp.BuildVersion,
            preset = Options.Preset,
            seed = Options.Seed,
            settings = Settings,
            reducedTransparency = ReducedTransparency() || Launch.ForceSolid,
            highContrast = SystemParameters.HighContrast,
            snapshot = IsSnapshot,
            panel = Launch.Panel,
            exec = Launch.Exec,
            query = Launch.Query,
            deps = Dependencies(),
            credentials = creds,
            paths = new { data = ChassisPaths.Folder, settings = ChassisPaths.SettingsFile, log = ChassisPaths.LogFile, exe = Environment.ProcessPath },
            extra = Hello(),
        });
    }

    void SendDiag() => Post(new
    {
        type = "chassis.diag",
        log = ChassisLog.Tail(),
        versions = new Dictionary<string, string?>
        {
            [Options.AppName] = ChassisApp.BuildVersion,
            [".NET"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["WebView2 runtime"] = _web.CoreWebView2?.Environment.BrowserVersionString,
            ["WebView2 SDK"] = typeof(CoreWebView2).Assembly.GetName().Version?.ToString(),
            ["Windows"] = Environment.OSVersion.VersionString,
            ["Chassis"] = ChassisPaths.ChassisSource() ?? ChassisPaths.KitStamp("chassis"),
            ["Gleam"] = ChassisPaths.KitStamp("gleam"),
        },
        process = new { path = Environment.ProcessPath, memoryMb = Environment.WorkingSet / 1048576, started = Process.GetCurrentProcess().StartTime },
    });

    /// <summary>The Dependencies tab: Chassis's own list, the kits found in wwwroot, then the app's.</summary>
    List<ChassisDependency> Dependencies()
    {
        var list = new List<ChassisDependency>
        {
            new(".NET 10 desktop runtime", "runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "Runs the window, tray icon and the app's work."),
            new("Microsoft Edge WebView2 runtime", "runtime", _web.CoreWebView2?.Environment.BrowserVersionString ?? "not loaded", "Shows the page. Comes with Windows 11 and Edge."),
            new("Microsoft.Web.WebView2", "package", "NuGet " + typeof(CoreWebView2).Assembly.GetName().Version, "Lets the window host the page."),
            new("Chassis app shell", "kit", ChassisPaths.ChassisSource() ?? "copy in Chassis\\", "Window, tray, single instance, settings, log, self-test. Fix the kit, then reinstall; never edit the copy."),
        };
        foreach (var dir in Directory.Exists(ChassisPaths.Web) ? Directory.GetDirectories(ChassisPaths.Web) : Array.Empty<string>())
        {
            var name = Path.GetFileName(dir);
            var stamp = ChassisPaths.KitStamp(name);
            if (stamp == null) continue;
            string generic = "Local copy in wwwroot\\" + name + ". Fix the kit, then reinstall; never edit the copy.";
            list.Add(new(KitTitle(name), "kit", stamp, Options.KitNotes.TryGetValue(name, out var note) ? note + " " + generic : generic));
        }
        list.Add(new("Settings", "file", ChassisPaths.SettingsFile));
        list.Add(new("Run log", "file", ChassisPaths.LogFile, "Restarted at 256 KB."));
        list.Add(new("Page cache", "folder", ChassisPaths.WebViewData));
        list.Add(new("The page", "folder", ChassisPaths.Web, "Served as " + ChassisPaths.Origin + " inside the window only."));
        list.AddRange(Options.Dependencies);
        foreach (var h in Options.Hosts.Where(h => !Options.Dependencies.Any(d => d.Kind == "host" && d.Where.Contains(h, StringComparison.OrdinalIgnoreCase))))
            list.Add(new(h, "host", "https://" + h, "The page fetches data from here."));
        if (Options.Hosts.Count == 0 && !Options.Dependencies.Any(d => d.Kind == "host"))
            list.Add(new("External hosts", "host", "none", "The page can't reach the network or navigate away."));
        return list;
    }

    /// <summary>The Dependencies tab's name for a kit folder in wwwroot (our kits by their own names).</summary>
    public static string KitTitle(string folder) => folder switch
    {
        "chassis" => "Chassis page kit",
        "gleam" => "Gleam version orb",
        "charts" => "graphs and heat",
        "cellbar" => "drive cell bar",
        "map3d" => "3D map",
        "weather" => "weather and disasters",
        "drive-activity" => "drive activity",
        _ => folder.Length == 0 ? folder : char.ToUpperInvariant(folder[0]) + folder[1..],
    };

    /// <summary>True when Windows "Transparency effects" is off or a high-contrast (forced colors) theme is on.</summary>
    public static bool ReducedTransparency()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("EnableTransparency") is int v && v == 0) return true;
        }
        catch { /* assume transparency is fine */ }
        return SystemParameters.HighContrast;
    }

    void OnSystemChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Dispatcher.BeginInvoke(() => Post(new { type = "chassis.system", reducedTransparency = ReducedTransparency(), highContrast = SystemParameters.HighContrast }));
    }

    // ---------- --snapshot: paint the page, save a PNG, quit (proof of the real window without screen capture) ----------

    async Task Snapshot()
    {
        await Task.Delay(1200);
        int code = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Launch.Snapshot!)!);
            using (var fs = File.Create(Launch.Snapshot!))
                await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            ChassisLog.Write("snapshot saved: " + Launch.Snapshot);
        }
        catch (Exception ex) { ChassisLog.Write("snapshot failed: " + ex.Message); code = 1; }
        string[] errors;
        lock (_pageErrors) errors = _pageErrors.ToArray();
        if (errors.Length > 0)
        {
            File.WriteAllLines(Launch.Snapshot + ".errors.txt", errors);
            if (code == 0) code = 3;
        }
        EndNow(code);
    }

    /// <summary>
    /// Ends a snapshot run at once with its exit code. The app's OnExiting still runs first (once), so child processes,
    /// timers and files are cleaned up the same way as on a normal exit.
    /// </summary>
    void EndNow(int code)
    {
        if (!_exiting)
        {
            _exiting = true;
            ExitCode = code;
            try { OnExiting(); } catch (Exception ex) { ChassisLog.Write("exit hook failed: " + ex.Message); }
        }
        ChassisLog.Write("exit " + code);
        Environment.Exit(code);
    }

    // ---------- tray, close, exit ----------

    /// <summary>Builds the tray menu: a disabled "App vX.Y.Z" line first, then Open/Hide, the app's items, Exit.</summary>
    public Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem($"{Options.AppName} v{ChassisApp.Version}") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Hide", null, (_, _) => Hide());
        int before = menu.Items.Count;
        AddTrayItems(menu);
        if (menu.Items.Count > before) menu.Items.Insert(before, new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        return menu;
    }

    void MakeTray()
    {
        System.Drawing.Icon? icon = null;
        try { icon = TrayIcon() ?? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        _tray = new Forms.NotifyIcon { Icon = icon ?? System.Drawing.SystemIcons.Application, Text = Options.AppName, ContextMenuStrip = BuildTrayMenu(), Visible = true };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    /// <summary>Tray tooltip (Windows allows 63 characters).</summary>
    public void SetTrayText(string text)
    {
        if (_tray != null) _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>
    /// A Windows notification from the tray icon, for results the user can't see because the window is hidden (a hotkey
    /// or tray action). Does nothing in a snapshot run.
    /// </summary>
    public void Notify(string title, string text, bool warning = false)
    {
        if (_tray == null) return;
        try { _tray.ShowBalloonTip(4000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info); }
        catch (Exception ex) { ChassisLog.Write("notify failed: " + ex.Message); }
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Settings.CloseToTray && !_exiting && !IsSnapshot) { e.Cancel = true; Hide(); return; }
        if (!_exiting) { e.Cancel = true; Exit(); }
    }

    /// <summary>Really ends the process: bypasses close to tray, removes the tray icon.</summary>
    public void Exit(int code = 0)
    {
        if (_exiting) return;
        if (!IsSnapshot)
        {
            bool ok = true;
            try { ok = ConfirmExit(); } catch (Exception ex) { ChassisLog.Write("exit check failed: " + ex.Message); }
            if (!ok) { ChassisLog.Write("exit cancelled by the app"); return; }
        }
        _exiting = true;
        ExitCode = code;
        try { OnExiting(); } catch (Exception ex) { ChassisLog.Write("exit hook failed: " + ex.Message); }
        SystemEvents.UserPreferenceChanged -= OnSystemChanged;
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        ChassisLog.Write("exit");
        Application.Current.Shutdown(code);
    }
}
