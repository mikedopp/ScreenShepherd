// Chassis app shell kit: copied into the app by Chassis\tools\install.py. Do not edit this copy: change Chassis, then reinstall.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chassis;

/// <summary>
/// The --selftest runner. Chassis checks the shell (version, settings, page files, no CDN, request filter, tray menu),
/// then the app adds its own with ChassisOptions.SelfTest. Prints one line per failure and "selftest: n/m passed".
/// </summary>
public sealed class ChassisCheck
{
    int _count;
    readonly List<string> _fails = new();
    public string Section { get; set; } = "";

    public void That(bool condition, string name)
    {
        _count++;
        if (condition) return;
        _fails.Add(Section + name);
        Console.WriteLine("FAIL " + Section + name);
    }

    public void Equal<T>(T expected, T actual, string name) =>
        That(EqualityComparer<T>.Default.Equals(expected, actual), $"{name} (expected {expected}, got {actual})");

    /// <summary>Runs an action that must not throw.</summary>
    public void NoThrow(Action a, string name)
    {
        try { a(); That(true, name); }
        catch (Exception ex) { That(false, $"{name} threw {ex.GetType().Name}: {ex.Message}"); }
    }

    public int Count => _count;
    public IReadOnlyList<string> Failures => _fails;

    public static int RunAll(ChassisOptions o)
    {
        var c = new ChassisCheck();
        c.Section = "chassis: ";
        Shell(c, o);
        if (o.SelfTest != null)
        {
            c.Section = o.AppName.ToLowerInvariant() + ": ";
            try { o.SelfTest(c); }
            catch (Exception ex) { c.That(false, "app self-test threw: " + ex); }
        }
        Console.WriteLine($"selftest: {c.Count - c.Failures.Count}/{c.Count} passed");
        return c.Failures.Count == 0 ? 0 : 1;
    }

    static readonly Regex External = new(@"(?:src|href)\s*=\s*[""']\s*(?:https?:)?//|@import\s+url\(\s*[""']?https?:|\bfetch\(\s*[""']https?:|\bimport\s*\(?\s*[""']https?:", RegexOptions.IgnoreCase);

    static void Shell(ChassisCheck c, ChassisOptions o)
    {
        c.That(Regex.IsMatch(ChassisApp.Version, @"^\d+\.\d+\.\d+$") && ChassisApp.Version != "0.0.0", "version comes from the build: " + ChassisApp.Version);
        c.That(!string.IsNullOrWhiteSpace(o.AppName) && !o.AppName.Contains('\\') && !o.AppName.Contains('/'), "app name is a plain name");
        c.That(Regex.IsMatch(ChassisPaths.HostName, @"^[a-z0-9-]+\.app$"), "page host is a plain name.app: " + ChassisPaths.HostName);

        // settings: round trip, clamping, a broken file
        var tmp = Path.Combine(Path.GetTempPath(), "chassis-selftest-" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        try
        {
            var f = Path.Combine(tmp, "settings.json");
            var s = new ChassisSettings { Glass = false, Opacity = 0.4, Blur = 9, CloseToTray = false };
            s.Set("answer", 42);
            s.Set("name", "x");
            s.Save(f);
            var r = ChassisSettings.Load(f);
            c.That(!r.Glass && Math.Abs(r.Opacity - 0.4) < 1e-9 && r.Blur == 9 && !r.CloseToTray, "settings round trip");
            c.Equal(42, r.Get("answer", 0), "app setting round trip");
            c.Equal("fallback", r.Get("missing", "fallback"), "missing app setting falls back");
            File.WriteAllText(f, "{ \"opacity\": 9, \"blur\": -3 }");
            r = ChassisSettings.Load(f);
            c.That(r.Opacity == ChassisSettings.DefaultOpacity && r.Blur == ChassisSettings.DefaultBlur, "out-of-range glass values are clamped");
            File.WriteAllText(f, "{ not json");
            r = ChassisSettings.Load(f);
            c.That(r.Glass && r.CloseToTray && File.Exists(f + ".bad"), "a broken settings file gives defaults and is kept as .bad");
            using (var doc = JsonDocument.Parse("{\"cmd\":\"settings\",\"glass\":false,\"opacity\":0.5,\"app\":{\"k\":\"v\"}}"))
                r.Apply(doc.RootElement);
            c.That(!r.Glass && r.Opacity == 0.5 && r.Get("k", "") == "v", "page settings message applies");
            using (var doc = JsonDocument.Parse("{\"cmd\":\"settings\",\"art\":false}"))
                r.Apply(doc.RootElement);
            c.That(!r.Art && new ChassisSettings().Art, "app picture is on by default and the page can turn it off");
            r.ResetGlass();
            c.That(r.Glass && r.Opacity == ChassisSettings.DefaultOpacity && r.Blur == ChassisSettings.DefaultBlur && r.Art, "reset glass");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        // the page: present, local only, carries the kits
        var web = ChassisPaths.Web;
        c.That(File.Exists(Path.Combine(web, "index.html")), "wwwroot\\index.html is next to the exe");
        c.That(File.Exists(Path.Combine(web, "chassis", "chassis.js")) && File.Exists(Path.Combine(web, "chassis", "chassis.css")), "Chassis page kit is installed (wwwroot\\chassis)");
        c.That(ChassisPaths.KitStamp("chassis") is string hs && hs.StartsWith("Chassis "), "Chassis page kit has its stamp");
        c.That(File.Exists(Path.Combine(web, "gleam", "gleam-badge.js")), "Gleam badge is installed (wwwroot\\gleam)");
        c.That(ChassisPaths.ChassisSource() is string cs && cs.StartsWith("Chassis "), "Chassis C# kit stamp is embedded");
        c.That(ChassisPaths.KitStamp("..") == null && ChassisPaths.KitStamp("../x") == null && ChassisPaths.KitStamp("a\b") == null, "kit stamp names can't walk out of wwwroot");
        c.Equal("drive cell bar", ChassisWindow.KitTitle("cellbar"), "our kits go by their own names on the Dependencies tab");
        c.That(o.KitNotes.Keys.All(k => k.Length > 0 && Path.GetFileName(k) == k), "kit notes are keyed by a kit folder name");
        // an app that asks for the seeded orb needs a Gleam badge that seeds (0.10.0 and later)
        var badgeJs = Path.Combine(web, "gleam", "gleam-badge.js");
        c.That(o.Preset is not (null or "auto") || (File.Exists(badgeJs) && File.ReadAllText(badgeJs).Contains("seedBadge")), "a seeded version orb has a Gleam badge that can seed it");
        if (Directory.Exists(web))
        {
            var offenders = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".html") || p.EndsWith(".css") || (p.EndsWith(".js") && !p.Contains(Path.DirectorySeparatorChar + "gleam" + Path.DirectorySeparatorChar)))
                .Where(p => External.IsMatch(File.ReadAllText(p)))
                .Select(p => Path.GetRelativePath(web, p)).ToArray();
            c.That(offenders.Length == 0, "the page loads nothing from the network (no CDN): " + string.Join(", ", offenders));
            var index = Path.Combine(web, "index.html");
            if (File.Exists(index))
            {
                var html = File.ReadAllText(index);
                if (o.Hosts.Count == 0)
                    c.That(html.Contains("Content-Security-Policy") && (html.Contains("connect-src 'none'") || (o.Routes.Count > 0 && html.Contains("connect-src 'self'"))),
                        "index.html has a CSP that blocks network calls (connect-src 'none', or 'self' for an app with routes)");
                else
                {
                    var connect = Regex.Match(html, @"connect-src([^;""]*)").Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var extra = connect.Where(s => s.StartsWith("https://")).Select(s => s["https://".Length..]).ToArray();
                    c.That(html.Contains("Content-Security-Policy") && extra.Length > 0 && !connect.Any(s => s is "*" or "https:" or "http:" || s.Contains('*')),
                        "index.html has a CSP whose connect-src names each host (no wildcards)");
                    c.That(extra.All(h => o.Hosts.Contains(h, StringComparer.OrdinalIgnoreCase)) && o.Hosts.All(h => extra.Contains(h, StringComparer.OrdinalIgnoreCase)),
                        "the CSP's connect-src and the app's host list name the same hosts");
                }
                c.That(html.Contains("chassis/chassis.js") && html.Contains("gleam/gleam-badge.js"), "index.html loads chassis.js and the Gleam badge");
            }
        }

        // request filter
        c.That(ChassisWindow.IsAllowed(ChassisPaths.Origin + "app.js"), "own origin is allowed");
        c.That(ChassisWindow.IsAllowed("data:image/png;base64,AA"), "data: is allowed");
        c.That(!ChassisWindow.IsAllowed("https://cdn.example.com/x.js"), "another host is blocked");
        c.That(!ChassisWindow.IsAllowed("https://" + ChassisPaths.HostName + ".evil.com/"), "a look-alike host is blocked");
        c.That(!ChassisWindow.IsAllowed("file:///C:/Windows/win.ini"), "file: is blocked");
        var hosts = new[] { "tiles.example.org" };
        c.That(ChassisWindow.HostAllowed("https://tiles.example.org/1/2/3.png", hosts) && ChassisWindow.HostAllowed("https://TILES.example.org/x", hosts), "a listed host is allowed over https");
        c.That(!ChassisWindow.HostAllowed("http://tiles.example.org/x", hosts) && !ChassisWindow.HostAllowed("https://a.tiles.example.org/x", hosts)
            && !ChassisWindow.HostAllowed("https://tiles.example.org.evil.com/x", hosts) && !ChassisWindow.HostAllowed("https://evil.com/tiles.example.org", hosts),
            "plain http, sub-domains and look-alikes of a listed host are blocked");
        c.That(!ChassisWindow.HostAllowed("https://tiles.example.org/x", Array.Empty<string>()) && !ChassisWindow.IsAllowed("https://tiles.example.org/x"),
            "an app with no host list reaches no host, and a listed host is never a page to navigate to");
        c.That(o.Hosts.All(h => h.Length > 0 && Uri.CheckHostName(h) == UriHostNameType.Dns && !h.Contains('*')), "the app's hosts are plain host names");
        var q = ChassisLaunch.Parse(new[] { "--query", "?demo=1&kind=temp", "--other" });
        c.That(q.Query == "demo=1&kind=temp" && q.Rest.SequenceEqual(new[] { "--other" }), "--query is read without its leading ?, other switches stay for the app");
        c.That(q.StartPage(ChassisPaths.Origin) == ChassisPaths.Origin + "index.html?demo=1&kind=temp" && ChassisLaunch.Parse(Array.Empty<string>()).StartPage(ChassisPaths.Origin) == ChassisPaths.Origin + "index.html", "the page opens with the --query options on its address");
        c.That(ChassisLaunch.Parse(new[] { "--query", "?" }).Query == null, "an empty --query adds nothing");
        c.That(ChassisLaunch.Parse(new[] { "--hidden" }).Hidden && !ChassisLaunch.Parse(new[] { "--other" }).Hidden, "--hidden asks for a start in the tray, and only when given");
        c.That(ChassisWindow.IsAllowed(q.StartPage(ChassisPaths.Origin)), "the start page with options is on the app's own origin");

        // app routes: only the app's own origin, only under a declared prefix
        c.That(o.Routes.All(r => r.StartsWith('/') && r.EndsWith('/') && r.Length > 1), "every app route starts and ends with /");
        var routes = new[] { "/api/" };
        c.Equal("/api/x", ChassisWindow.RouteOf(ChassisPaths.Origin + "api/x?a=1", routes), "a route request is the app's");
        c.That(ChassisWindow.RouteOf(ChassisPaths.Origin + "app.js", routes) == null, "page files are not routes");
        c.That(ChassisWindow.RouteOf(ChassisPaths.Origin + "chassis/../api/x", routes) == "/api/x" && ChassisWindow.RouteOf(ChassisPaths.Origin + "api/../app.js", routes) == null, "dot segments can't walk into or out of a route");
        c.That(ChassisWindow.RouteOf("https://evil.com/api/x", routes) == null && ChassisWindow.RouteOf(ChassisPaths.Origin + "api/x", Array.Empty<string>()) == null, "no route for another host or an app without routes");
        var hr = new ChassisRequest("GET", "/api/x", "id=a%2Fb&q=two+words&empty", Array.Empty<byte>());
        c.That(hr.Arg("id") == "a/b" && hr.Arg("q") == "two words" && hr.Arg("empty") == "" && hr.Arg("none") == "", "route query values are unescaped");
        c.That(ChassisWindow.WebFileOf(ChassisPaths.Origin + "app.js") == Path.Combine(ChassisPaths.Web, "app.js")
            && ChassisWindow.WebFileOf(ChassisPaths.Origin) == Path.Combine(ChassisPaths.Web, "index.html"), "a self-served page file maps into wwwroot");
        bool inside(string? f) => f == null || f.StartsWith(Path.GetFullPath(ChassisPaths.Web) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        c.That(ChassisWindow.WebFileOf(ChassisPaths.Origin + "..%2F..%2Fsecret.txt") == null && inside(ChassisWindow.WebFileOf(ChassisPaths.Origin + "%2e%2e/%2e%2e/x"))
            && inside(ChassisWindow.WebFileOf(ChassisPaths.Origin + "a/..%5C..%5C..%5Cwin.ini")) && ChassisWindow.WebFileOf("https://evil.com/app.js") == null, "a self-served page file can't leave wwwroot");
        c.Equal("text/javascript; charset=utf-8", ChassisWindow.MimeOf("x/app.mjs"), "page scripts are served as JavaScript");
        c.That(ChassisWindow.IsEditMenuItem("copy") && ChassisWindow.IsEditMenuItem("selectAll") && !ChassisWindow.IsEditMenuItem("inspectElement") && !ChassisWindow.IsEditMenuItem("back"), "the edit menu keeps only edit commands");

        // single instance: the name must be the same in every process (a second launch has to find the first)
        c.Equal(ChassisApp.InstanceId("A", @"C:\x\Data"), ChassisApp.InstanceId("A", @"c:\X\data\"), "instance name ignores case and a trailing slash");
        c.That(ChassisApp.InstanceId("A", @"C:\x") != ChassisApp.InstanceId("A", @"C:\y"), "a different --data folder is a different instance");
        c.Equal(@"Local\Chassis.A.6E6EF9E1F001", ChassisApp.InstanceId("A", @"C:\x"), "instance name is a stable hash (not string.GetHashCode)");

        // system and log
        c.NoThrow(() => ChassisWindow.ReducedTransparency(), "reading Windows transparency settings");
        ChassisLog.Write("selftest log line");
        c.That(ChassisLog.Tail().Any(l => l.EndsWith("selftest log line")), "log keeps recent lines");
        c.That(o.Dependencies.All(d => !string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(d.Where)), "every app dependency says where it lives");
    }
}
