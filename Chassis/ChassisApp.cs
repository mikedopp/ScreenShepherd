// Chassis app shell kit: copied into the app by Chassis\tools\install.py. Do not edit this copy: change Chassis, then reinstall.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace Chassis;

/// <summary>What an app tells Chassis about itself.</summary>
public sealed class ChassisOptions
{
    /// <summary>Display name, also the data folder (%LOCALAPPDATA%\Name) and the page host (name.app).</summary>
    public required string AppName { get; init; }
    /// <summary>
    /// Gleam preset pinned on the version orb. Null or "auto" gives the orb seeded from <see cref="Seed"/> (Gleam 0.10.0+),
    /// which is what an app with its own orb look uses.
    /// </summary>
    public string? Preset { get; init; } = "aurora";
    /// <summary>What the seeded orb is generated from (Preset null or "auto"); null seeds it from the app name.</summary>
    public string? Seed { get; init; }
    /// <summary>What each kit in wwwroot does in this app (key: the kit's folder, such as "charts"), shown on the Dependencies tab.</summary>
    public Dictionary<string, string> KitNotes { get; init; } = new();
    public int Width { get; init; } = 1100;
    public int Height { get; init; } = 760;
    public int MinWidth { get; init; } = 720;
    public int MinHeight { get; init; } = 500;
    /// <summary>Page background behind the WebView while it loads (match --page in the app's CSS).</summary>
    public string PageColor { get; init; } = "#07080a";
    /// <summary>The app's own dependencies (Chassis adds .NET, WebView2, kits, settings, log and page cache).</summary>
    public List<ChassisDependency> Dependencies { get; init; } = new();
    /// <summary>The app's self-test checks, run after Chassis's own by --selftest.</summary>
    public Action<ChassisCheck>? SelfTest { get; init; }
    /// <summary>
    /// Address prefixes on the app's own origin that the app answers itself through ChassisWindow.Serve, such as
    /// "/api/" or "/media/" (each starts and ends with /). The page reaches them with a same-origin fetch, so its CSP uses
    /// connect-src 'self'. Nothing listens on a port. Empty (the default): the page is only the files in wwwroot.
    /// </summary>
    public List<string> Routes { get; init; } = new();
    /// <summary>
    /// Other hosts the page may fetch from over https, by exact name ("tiles.example.org"), for an app whose page shows
    /// live data such as map tiles. The page's CSP must name the same hosts in connect-src (and img-src when it shows
    /// their pictures); the self-test checks that. Scripts and styles still come only from wwwroot, and the page still
    /// can't navigate away. Each host is listed on the Dependencies tab. Empty (the default): no network at all.
    /// </summary>
    public List<string> Hosts { get; init; } = new();
    /// <summary>
    /// Right-click on the page shows the edit menu (Copy, Cut, Paste, Select all, Undo, Redo) and nothing else. For apps whose
    /// page is text people read and copy. Off (the default): no context menu.
    /// </summary>
    public bool EditMenu { get; init; }
    /// <summary>Extra --qa snapshots: (file name, extra args). Chassis always takes glass, solid and every panel.</summary>
    public List<(string Name, string[] Args)> QaShots { get; init; } = new();
}

/// <summary>Command-line switches every Chassis app understands.</summary>
public sealed class ChassisLaunch
{
    public string? Snapshot;
    public bool ForceSolid;
    public string? Panel;
    public int? Width, Height;
    public string? DataFolder;
    /// <summary>--exec CMD: the page sends this command right after start (QA shots of busy states).</summary>
    public string? Exec;
    /// <summary>--query Q: options for the page, put on its address (index.html?Q) and in hello.query. A leading ? is dropped.</summary>
    public string? Query;
    /// <summary>--hidden: start in the tray with no window on screen (a second launch or the tray icon shows it).</summary>
    public bool Hidden;
    public readonly List<string> Rest = new();

    /// <summary>The page the window opens: index.html, with the --query options when there are any.</summary>
    public string StartPage(string origin) => origin + "index.html" + (string.IsNullOrEmpty(Query) ? "" : "?" + Query);

    public static ChassisLaunch Parse(string[] a)
    {
        var o = new ChassisLaunch();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--snapshot" when i + 1 < a.Length: o.Snapshot = Path.GetFullPath(a[++i]); break;
                case "--solid": o.ForceSolid = true; break;
                case "--panel" when i + 1 < a.Length: o.Panel = a[++i]; break;
                case "--width" when i + 1 < a.Length && int.TryParse(a[i + 1], out var w): o.Width = w; i++; break;
                case "--height" when i + 1 < a.Length && int.TryParse(a[i + 1], out var h): o.Height = h; i++; break;
                case "--data" when i + 1 < a.Length: o.DataFolder = Path.GetFullPath(a[++i]); break;
                case "--exec" when i + 1 < a.Length: o.Exec = a[++i]; break;
                case "--hidden": o.Hidden = true; break;
                case "--query" when i + 1 < a.Length: o.Query = a[++i].TrimStart('?'); if (o.Query.Length == 0) o.Query = null; break;
                default: o.Rest.Add(a[i]); break;
            }
        }
        return o;
    }
}

public static class ChassisApp
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    /// <summary>The build's version (from VERSION via the csproj), without the +commit suffix.</summary>
    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>The full informational version, with the commit when the build stamped one.</summary>
    public static string BuildVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            int plus = v.IndexOf('+');
            return plus > 0 && v.Length > plus + 8 ? v[..(plus + 8)] : v;
        }
    }

    public static ChassisOptions Options { get; private set; } = null!;
    public static ChassisLaunch Launch { get; private set; } = null!;
    public static ChassisSettings Settings { get; private set; } = null!;

    /// <summary>
    /// Runs the app: --selftest, --qa DIR, --snapshot PNG, --exit (ask the running copy to quit), or the normal
    /// single-instance window (--hidden starts it in the tray).
    /// Exit codes: 0 ok, 1 self-test or capture failed (or --exit found nothing running), 3 snapshot saved but the page
    /// reported errors, 4 page never ready.
    /// </summary>
    public static int Run(string[] args, ChassisOptions options, Func<ChassisWindow> createWindow)
    {
        Options = options;
        Launch = ChassisLaunch.Parse(args);
        ChassisPaths.Init(options.AppName, Launch.DataFolder);
        Settings = ChassisSettings.Load();

        if (args.Contains("--selftest"))
        {
            if (!Console.IsOutputRedirected) AttachConsole(-1);
            return ChassisCheck.RunAll(options);
        }
        int qa = Array.IndexOf(args, "--qa");
        if (qa >= 0)
        {
            if (!Console.IsOutputRedirected) AttachConsole(-1);
            return RunQa(qa + 1 < args.Length ? args[qa + 1] : "qa-out", options);
        }

        string id = InstanceId(options.AppName, Launch.DataFolder);
        // --exit: ask the running copy to quit, the same as Exit in its tray menu (for scripts and updates)
        if (args.Contains("--exit")) return SignalExit(id) ? 0 : 1;
        using var mutex = new Mutex(true, id, out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, id + ".Show");
        // a second launch restores the running window; a snapshot run is its own throwaway process
        if (!first && Launch.Snapshot == null) { show.Set(); return 0; }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { ChassisLog.Write("unhandled: " + e.Exception); e.Handled = true; };
        var window = createWindow();
        // only the real running copy listens for --exit: a snapshot run must never answer for it
        using var quit = first && Launch.Snapshot == null ? new EventWaitHandle(false, EventResetMode.AutoReset, id + ".Exit") : null;
        if (quit != null)
        {
            new Thread(() => { while (show.WaitOne()) window.Dispatcher.BeginInvoke(window.RestoreFromTray); }) { IsBackground = true, Name = "chassis-show" }.Start();
            new Thread(() => { while (quit.WaitOne()) window.Dispatcher.BeginInvoke(() => window.Exit()); }) { IsBackground = true, Name = "chassis-exit" }.Start();
        }
        app.Run(window);
        return window.ExitCode;
    }

    /// <summary>Sets the running copy's exit signal. False when no copy is running (for this --data folder).</summary>
    static bool SignalExit(string id)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(id + ".Exit", out var signal)) return false;
            using (signal) return signal.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) { return false; }
    }

    /// <summary>
    /// The single-instance name: per user session, per app, and per --data folder (so a test copy never grabs the real
    /// window). Uses a stable hash: string.GetHashCode differs in every process and would break the hand-off.
    /// </summary>
    public static string InstanceId(string app, string? dataFolder)
    {
        string id = "Local\\Chassis." + app;
        if (dataFolder == null) return id;
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataFolder).TrimEnd('\\').ToLowerInvariant()));
        return id + "." + Convert.ToHexString(bytes, 0, 6);
    }

    /// <summary>--qa DIR: self-test, then glass/solid/panel snapshots, each in its own process with a throwaway data folder.</summary>
    static int RunQa(string dir, ChassisOptions o)
    {
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);
        string exe = Environment.ProcessPath!;
        string data = Path.Combine(dir, "data");
        var results = new List<object>();
        int worst = 0;

        int Step(string name, params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(data);
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } }
            int code = p.HasExited ? p.ExitCode : 4;
            results.Add(new { step = name, code, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) });
            File.WriteAllText(Path.Combine(dir, name + ".txt"), output);
            Console.WriteLine($"{name}: exit {code} ({sw.Elapsed.TotalSeconds:0.0} s)");
            if (code != 0) worst = 1;
            return code;
        }

        Step("selftest", "--selftest");
        Step("glass", "--snapshot", Path.Combine(dir, "glass.png"));
        Step("solid", "--snapshot", Path.Combine(dir, "solid.png"), "--solid");
        Step("solid-settings", "--snapshot", Path.Combine(dir, "solid-settings.png"), "--solid", "--panel", "settings");
        foreach (var panel in new[] { "settings", "deps", "help", "diag", "creds" })
            Step("panel-" + panel, "--snapshot", Path.Combine(dir, "panel-" + panel + ".png"), "--panel", panel);
        Step("narrow", "--snapshot", Path.Combine(dir, "narrow.png"), "--width", "520", "--height", "720");
        foreach (var (name, args) in o.QaShots)
            Step(name, new[] { "--snapshot", Path.Combine(dir, name + ".png") }.Concat(args).ToArray());

        File.WriteAllText(Path.Combine(dir, "qa.json"), JsonSerializer.Serialize(new
        {
            app = o.AppName, version = BuildVersion, at = DateTimeOffset.Now, exe, pass = worst == 0, steps = results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(worst == 0 ? "qa: PASS" : "qa: FAIL");
        return worst;
    }
}

/// <summary>Where an app keeps its files: %LOCALAPPDATA%\AppName (or --data DIR for tests).</summary>
public static class ChassisPaths
{
    public static string App { get; private set; } = "App";
    public static string Folder { get; private set; } = "";
    public static string SettingsFile => Path.Combine(Folder, "settings.json");
    public static string LogFile => Path.Combine(Folder, App.ToLowerInvariant() + ".log");
    public static string WebViewData => Path.Combine(Folder, "webview");
    public static string Web => Path.Combine(AppContext.BaseDirectory, "wwwroot");
    public static string HostName => App.ToLowerInvariant().Replace(' ', '-') + ".app";
    public static string Origin => "https://" + HostName + "/";

    public static void Init(string app, string? overrideFolder)
    {
        App = app;
        Folder = overrideFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), app);
    }

    /// <summary>Reads the stamp of a kit copied into wwwroot (VERSION.txt or SOURCE.txt), first line only.</summary>
    public static string? KitStamp(string kit)
    {
        // a kit is a folder name directly under wwwroot, never a path
        if (string.IsNullOrEmpty(kit) || Path.GetFileName(kit) != kit || kit is "." or "..") return null;
        foreach (var f in new[] { "VERSION.txt", "SOURCE.txt" })
        {
            var p = Path.Combine(Web, Path.GetFileName(kit), f);
            if (File.Exists(p)) return File.ReadLines(p).FirstOrDefault()?.Trim();
        }
        return null;
    }

    /// <summary>The Chassis C# kit stamp compiled into this app (Chassis\SOURCE.txt is embedded at build time by the csproj).</summary>
    public static string? ChassisSource()
    {
        try
        {
            using var s = Assembly.GetEntryAssembly()?.GetManifestResourceStream("Chassis.SOURCE.txt");
            return s == null ? null : new StreamReader(s).ReadLine()?.Trim();
        }
        catch { return null; }
    }
}
