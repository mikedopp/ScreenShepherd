using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Chassis;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ScreenShepherd;

/// <summary>
/// The app. Chassis owns the frame, tray icon, single instance, settings file, log, version pop-out and snapshots; this
/// class answers the page's calls and runs the watchers: the global hotkey, new-window rescue and display-change rescue.
/// </summary>
public sealed class MainWindow : ChassisWindow
{
    private const int RescueHotkeyId = 0x5348;
    private readonly AppHostService _service = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly NativeMethods.WinEventDelegate _winEventCallback;
    private readonly SnapshotOptions _shot;
    private nint _handle;
    private nint _winEventHook;
    private nint _foregroundHook;
    private int _pushingState;
    private int _checkingDisplays;
    private System.Threading.Timer? _displayTimer;
    private bool _closing;

    public MainWindow()
    {
        _winEventCallback = OnWindowEvent;
        _shot = SnapshotOptions.From(Launch.Snapshot, Launch.Query);
        _service.UseSettings(AppSettings.From(Settings));
        // Display names and history live beside settings.json; a snapshot run reads them and changes nothing.
        _service.StartWatch(ChassisPaths.Folder, readOnly: IsSnapshot);
        if (Program.ImportedLegacySettings) _service.Log("ok", "Settings from Screen Shepherd 1.6 were copied over.");

        Title = "Screen Shepherd";
        SetTrayText("Screen Shepherd: monitor-aware window rescue");
        SourceInitialized += (_, _) => OnHandleReady();
        if (IsSnapshot)
        {
            // Really shown (a hidden page never paints), but far beyond every monitor, and a tool window so a running
            // Screen Shepherd doesn't take it for a stranded app window and rescue it.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2000;
            Top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 2000;
            ShowInTaskbar = false;
            WindowStyle = WindowStyle.ToolWindow;
            return;
        }

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible && PageReady) _ = PushStateAsync(); };
        _refreshTimer.Tick += async (_, _) => { if (IsVisible && PageReady) await PushStateAsync(); };
        _refreshTimer.Start();
        // Every 3 seconds, visible or not: where windows are, and whether a display came or went. (A snapshot run
        // returned above, so it only reads the saved history and never puts a window back.)
        _displayTimer = new System.Threading.Timer(_ => CheckDisplays(false), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    // ---------- Chassis hooks ----------

    // --hidden (Chassis), the older --minimized, or the app's own "Start hidden" setting.
    protected override bool StartHidden =>
        Launch.Hidden || Program.Has(Launch.Rest.ToArray(), "--minimized") || Settings.Get(AppSettings.StartMinimizedKey, false);

    // The icon has a separately drawn small size for the tray.
    protected override System.Drawing.Icon? TrayIcon() => IconBuilder.Tray();

    protected override void AddTrayItems(Forms.ContextMenuStrip menu)
    {
        menu.Items.Add("Rescue off-screen windows", null, (_, _) => RunTrayRescue());
        menu.Items.Add("Move last app here", null, (_, _) => RunLastAppRescue());
    }

    protected override void OnSettingsChanged()
    {
        _service.UseSettings(AppSettings.From(Settings));
        _service.Log("ok", "Settings saved.");
    }

    protected override void OnCommand(string cmd, JsonElement message)
    {
        switch (cmd)
        {
            // The page's request/response calls: { cmd: "call", id, method, params }.
            case "call":
                // The message belongs to a document Chassis disposes when this returns.
                _ = AnswerAsync(message.Clone());
                break;
            // A snapshot run: what the page says it is showing, saved beside the picture.
            case "facts" when IsSnapshot && message.TryGetProperty("facts", out var facts):
                Program.TryWriteReceipt(Launch.Snapshot + ".json", new
                {
                    test = "snapshot",
                    version = AppHostService.AppVersion,
                    time = DateTimeOffset.Now,
                    redacted = _shot.Redact,
                    facts = facts.Clone()
                });
                break;
        }
    }

    protected override void OnExiting()
    {
        _closing = true;
        _refreshTimer.Stop();
        _displayTimer?.Dispose();
        _service.StopWatch();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_handle != 0 && !IsSnapshot) NativeMethods.UnregisterHotKey(_handle, RescueHotkeyId);
        if (_winEventHook != 0) NativeMethods.UnhookWinEvent(_winEventHook);
        if (_foregroundHook != 0) NativeMethods.UnhookWinEvent(_foregroundHook);
    }

    // ---------- hotkey and watchers ----------

    private void OnHandleReady()
    {
        _handle = new WindowInteropHelper(this).Handle;
        const uint chord = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        if (IsSnapshot)
        {
            // Snapshots must not hold the hotkey (it would move windows). Report whether the real app could register
            // it: probe, then release it straight away.
            _service.HotkeyRegistered = NativeMethods.RegisterHotKey(_handle, RescueHotkeyId, chord, NativeMethods.VK_HOME)
                && NativeMethods.UnregisterHotKey(_handle, RescueHotkeyId);
            return;
        }

        _service.HotkeyRegistered = NativeMethods.RegisterHotKey(_handle, RescueHotkeyId, chord, NativeMethods.VK_HOME);
        HwndSource.FromHwnd(_handle)?.AddHook(OnWindowMessage);
        _service.Log(_service.HotkeyRegistered ? "ok" : "warn",
            _service.HotkeyRegistered ? "Global rescue hotkey registered." : "Ctrl+Alt+Home is already owned by another application.");

        const uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;
        _winEventHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW, 0, _winEventCallback, 0, 0, flags);
        _foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, 0, _winEventCallback, 0, 0, flags);
        _service.Log(_winEventHook != 0 ? "ok" : "warn",
            _winEventHook != 0 ? "New-window watcher active." : "New-window watcher could not start.");
    }

    private nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == RescueHotkeyId)
        {
            RunForegroundRescue();
            handled = true;
        }
        return 0;
    }

    private void OnWindowEvent(nint hook, uint eventType, nint handle, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (handle == 0 || _closing) return;
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            _service.RememberExternalForeground(handle);
            return;
        }
        // Show events fire for every child control and popup; only top-level windows can be stranded.
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF) return;
        if (NativeMethods.GetAncestor(handle, NativeMethods.GA_ROOT) != handle) return;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(650);
            try
            {
                if (await Task.Run(() => _service.TryAutoRescueWindow(handle)) && PageReady) await PushStateAsync();
            }
            catch (Exception ex)
            {
                _service.Log("bad", "Launch rescue failed: " + ex.Message);
            }
        }));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(1500);
            try
            {
                var result = await Task.Run(_service.HandleDisplayTopologyChange);
                if (result.Moved > 0) Notify("Display changed", result.Message);
                await Task.Run(() => CheckDisplays(false));
            }
            catch (Exception ex)
            {
                _service.Log("bad", "Display-change rescue failed: " + ex.Message);
            }
            if (PageReady) await PushStateAsync();
        }));
    }

    // Runs on a pool thread. One check at a time; a put-back can wait a few seconds for the display to settle.
    private void CheckDisplays(bool force)
    {
        if (_closing || Interlocked.Exchange(ref _checkingDisplays, 1) == 1) return;
        try
        {
            var check = _service.CheckDisplays(force);
            if (check.Gone.Count == 0 && check.Back.Count == 0) return;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var settings = _service.Settings;
                foreach (var gone in check.Gone.Where(_ => settings.NotifyDrops))
                    Notify($"{gone.Name} went away", $"After {DisplayWatch.Span(gone.Minutes)} on."
                        + (gone.Windows > 0 && settings.PutBack ? $" Its {Plural(gone.Windows, "window")} go back when it returns." : string.Empty), true);
                foreach (var back in check.Back)
                {
                    var done = check.PutBack.FirstOrDefault(p => p.Key == back.Key);
                    var text = (back.GapMinutes is { } gap ? $"Back after {DisplayWatch.Span(gap)}." : "Turned on.")
                        + (done is { Moved: > 0 } ? $" Put {Plural(done.Moved, "window")} back." : string.Empty);
                    if (settings.NotifyDrops || done is { Moved: > 0 }) Notify($"{back.Name} is back", text);
                }
                if (PageReady)
                {
                    Post(new { type = "displays", gone = check.Gone, back = check.Back.Select(b => new { b.Key, b.Name, b.GapMinutes }), putBack = check.PutBack });
                    await PushStateAsync();
                }
            }));
        }
        catch (Exception ex)
        {
            _service.Log("bad", "Display check failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _checkingDisplays, 0);
        }
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private async void RunTrayRescue()
    {
        var result = await Task.Run(() => _service.RescueAll(default));
        Notify("Rescue complete", result.Message);
        if (PageReady) await PushStateAsync();
    }

    private async void RunForegroundRescue()
    {
        var result = await Task.Run(_service.MoveForegroundToCursor);
        Notify(result.Ok ? "Window moved" : "Window not moved", result.Message, !result.Ok);
        if (PageReady) await PushStateAsync();
    }

    private async void RunLastAppRescue()
    {
        var result = await Task.Run(_service.MoveLastExternalToCursor);
        Notify(result.Ok ? "Window moved" : "Window not moved", result.Message, !result.Ok);
        if (PageReady) await PushStateAsync();
    }

    // ---------- the page's calls ----------

    private async Task AnswerAsync(JsonElement call)
    {
        var id = call.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? string.Empty : string.Empty;
        try
        {
            var method = call.TryGetProperty("method", out var methodValue) ? methodValue.GetString() ?? string.Empty : string.Empty;
            var parameters = call.TryGetProperty("params", out var paramsValue) ? paramsValue : default;
            if (method.Length == 0) return;
            var result = await DispatchAsync(method, parameters);
            Post(new { type = "response", id, ok = true, result = ToElement(result) });
        }
        catch (Exception ex)
        {
            _service.Log("bad", ex.Message);
            Post(new { type = "response", id, ok = false, error = new { message = ex.Message } });
        }
    }

    // Inventory reads (WMI, event log, UI Automation of browser windows) and cross-process window moves can take
    // seconds, so they run off the UI thread. The clipboard stays on it.
    private async Task<object?> DispatchAsync(string method, JsonElement parameters)
    {
        if (IsSnapshot && method is not ("app.getState" or "windows.refresh"))
            throw new InvalidOperationException("Snapshot mode is read-only.");

        if (method == "app.copyDiagnostics")
        {
            var text = await Task.Run(_service.BuildDiagnosticsText);
            if (!CopyText(text)) throw new InvalidOperationException("The clipboard is busy. Try again.");
            return _service.DiagnosticsCopied(text.Length);
        }

        return await Task.Run<object?>(() => method switch
        {
            "app.getState" => ReadState(),
            "windows.refresh" => ReadState(),
            "system.openSettings" => _service.OpenSystemSettings(parameters),
            "displays.wake" => _service.WakeDisplays(),
            "windows.move" => _service.MoveWindow(parameters),
            "windows.rescueAll" => _service.RescueAll(parameters),
            "windows.gatherAll" => _service.GatherAll(parameters),
            "windows.moveForegroundToCursor" => _service.MoveForegroundToCursor(),
            "windows.moveLastExternalToCursor" => _service.MoveLastExternalToCursor(),
            "displays.setName" => _service.SetDisplayName(parameters),
            "displays.forget" => _service.ForgetAway(parameters),
            "displays.connect" => _service.OpenConnect(),
            "displays.allowChanges" => _service.SetDdcAllowed(parameters),
            "monitor.set" => _service.WriteMonitorSetting(parameters),
            _ => throw new InvalidOperationException("Unknown command: " + method)
        });
    }

    private AppState ReadState()
    {
        var state = _service.GetState();
        if (_shot.SampleStray && state.Monitors.Count > 0)
        {
            // A labelled, made-up stranded window so captures can show the drive line.
            var pen = state.Monitors.FirstOrDefault(monitor => !monitor.Primary) ?? state.Monitors[0];
            var right = state.Monitors.Max(monitor => monitor.Bounds.Right);
            var bottom = state.Monitors.Max(monitor => monitor.Bounds.Bottom);
            var sample = new WindowSnapshot("0x0", 0, "sample", "sample stranded window (not a real window)",
                new RectSnapshot(right + 900, bottom + 900, 620, 380), string.Empty, "OFF-DESKTOP",
                pen.DeviceName, pen.DisplayName, "off-screen", 0, false, false, false, true);
            state = state with { Windows = state.Windows.Prepend(sample).ToArray(), OffScreenCount = state.OffScreenCount + 1 };
        }
        return _shot.Redact ? StateRedactor.Apply(state, _shot.Hidden) : state;
    }

    // The app's records keep their own JSON shape (nulls included); Chassis then sends the element as it is.
    private static JsonElement? ToElement(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType(), AppHostService.JsonOptions);

    // At most one state read in flight: the 4-second timer skips a tick rather than queueing reads behind a slow one.
    private async Task PushStateAsync()
    {
        if (Interlocked.Exchange(ref _pushingState, 1) == 1) return;
        try
        {
            var state = await Task.Run(ReadState);
            Post(new { type = "state", ok = true, result = ToElement(state) });
        }
        catch (Exception ex)
        {
            _service.Log("bad", "State refresh failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _pushingState, 0);
        }
    }
}

/// <summary>
/// Options for a --snapshot run, read from --query: redact=1 replaces window titles, monitor serials and profile paths
/// (for pictures meant to be shared), hide=a,b also leaves those processes' windows out, stray=1 adds a labelled made-up
/// stranded window. A normal run ignores all of them, so the real window never shows made-up state.
/// </summary>
internal sealed record SnapshotOptions(bool Redact, IReadOnlyList<string> Hidden, bool SampleStray)
{
    internal static SnapshotOptions From(string? snapshotFile, string? query)
    {
        if (snapshotFile is null || string.IsNullOrEmpty(query)) return new SnapshotOptions(false, Array.Empty<string>(), false);
        var options = new ChassisRequest("GET", "/", query, Array.Empty<byte>());
        var hidden = options.Arg("hide").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new SnapshotOptions(options.Arg("redact") == "1" || hidden.Length > 0, hidden, options.Arg("stray") == "1");
    }
}
