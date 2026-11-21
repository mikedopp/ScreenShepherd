// Chassis app shell kit: copied into the app by Chassis\tools\install.py. Do not edit this copy: change Chassis, then reinstall.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Chassis;

/// <summary>
/// Settings every Chassis app has (glass, opacity, blur, close to tray), plus the app's own values in <see cref="App"/>.
/// Saved to %LOCALAPPDATA%\AppName\settings.json. A broken file falls back to defaults and is kept as settings.json.bad.
/// </summary>
public sealed class ChassisSettings
{
    public const double DefaultOpacity = 0.62, DefaultBlur = 22;

    public bool Glass { get; set; } = true;
    public double Opacity { get; set; } = DefaultOpacity;
    public double Blur { get; set; } = DefaultBlur;
    public bool CloseToTray { get; set; } = true;
    /// <summary>The app's picture behind the glass and across the top of the pop-out (apps that pass Chassis.start({ art })).</summary>
    public bool Art { get; set; } = true;
    /// <summary>The app's own settings, edited by the page and read with Get/Set.</summary>
    public JsonObject App { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ChassisSettings Load(string? file = null)
    {
        file ??= ChassisPaths.SettingsFile;
        try
        {
            if (File.Exists(file))
            {
                var s = JsonSerializer.Deserialize<ChassisSettings>(File.ReadAllText(file), Json) ?? new ChassisSettings();
                s.Clamp();
                return s;
            }
        }
        catch (Exception ex)
        {
            ChassisLog.Write("settings unreadable, using defaults: " + ex.Message);
            try { File.Copy(file, file + ".bad", true); } catch { }
        }
        return new ChassisSettings();
    }

    public void Save(string? file = null)
    {
        file ??= ChassisPaths.SettingsFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, file, true);
        }
        catch (Exception ex) { ChassisLog.Write("settings not saved: " + ex.Message); }
    }

    public void ResetGlass() { Glass = true; Opacity = DefaultOpacity; Blur = DefaultBlur; Art = true; }

    void Clamp()
    {
        if (double.IsNaN(Opacity) || Opacity < 0.2 || Opacity > 1) Opacity = DefaultOpacity;
        if (double.IsNaN(Blur) || Blur < 0 || Blur > 60) Blur = DefaultBlur;
        App ??= new JsonObject();
    }

    /// <summary>Applies a "settings" message from the page: the shell's fields and the app bag.</summary>
    public void Apply(JsonElement m)
    {
        if (m.TryGetProperty("glass", out var g) && g.ValueKind is JsonValueKind.True or JsonValueKind.False) Glass = g.GetBoolean();
        if (m.TryGetProperty("opacity", out var o) && o.ValueKind == JsonValueKind.Number) Opacity = o.GetDouble();
        if (m.TryGetProperty("blur", out var b) && b.ValueKind == JsonValueKind.Number) Blur = b.GetDouble();
        if (m.TryGetProperty("closeToTray", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False) CloseToTray = c.GetBoolean();
        if (m.TryGetProperty("art", out var pic) && pic.ValueKind is JsonValueKind.True or JsonValueKind.False) Art = pic.GetBoolean();
        if (m.TryGetProperty("app", out var a) && a.ValueKind == JsonValueKind.Object)
            foreach (var p in a.EnumerateObject()) App[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        Clamp();
    }

    public T Get<T>(string key, T fallback)
    {
        try { return App[key] is JsonNode n ? n.Deserialize<T>(Json) ?? fallback : fallback; }
        catch { return fallback; }
    }

    public void Set<T>(string key, T value) => App[key] = JsonSerializer.SerializeToNode(value, Json);
}

/// <summary>A run log (%LOCALAPPDATA%\AppName\appname.log, restarted at 256 KB) shown on the Diagnostics tab.</summary>
public static class ChassisLog
{
    static readonly object Gate = new();
    static readonly List<string> Recent = new();

    public static void Write(string line)
    {
        var now = DateTime.Now;
        var stamped = now.ToString("HH:mm:ss") + "  " + line;
        lock (Gate)
        {
            Recent.Add(stamped);
            if (Recent.Count > 300) Recent.RemoveAt(0);
            try
            {
                if (string.IsNullOrEmpty(ChassisPaths.Folder)) return;
                Directory.CreateDirectory(ChassisPaths.Folder);
                var info = new FileInfo(ChassisPaths.LogFile);
                if (info.Exists && info.Length > 256 * 1024) File.Delete(ChassisPaths.LogFile);
                // the file spans days (and other runs of the app, such as scheduled ones), so its lines carry the date
                File.AppendAllText(ChassisPaths.LogFile, now.ToString("yyyy-MM-dd ") + stamped + Environment.NewLine);
            }
            catch { /* logging must never break the app */ }
        }
    }

    public static string[] Tail(int n = 120)
    {
        lock (Gate) return Recent.Count <= n ? Recent.ToArray() : Recent.GetRange(Recent.Count - n, n).ToArray();
    }
}

/// <summary>One line of the Dependencies tab: what it is, what kind, and where it lives.</summary>
/// <param name="Kind">runtime, package, kit, file, folder, service, host, variable, model or tool</param>
public sealed record ChassisDependency(string Name, string Kind, string Where, string Note = "");
