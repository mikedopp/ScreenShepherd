using System.Windows.Forms;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ScreenShepherd;

public sealed class WindowService
{
    private const int MinimumReachableTitleWidth = 64;
    private const int TitleBandHeight = 42;
    private readonly int _currentProcessId = Environment.ProcessId;
    private readonly MonitorInventoryService _monitorInventory = new();

    // Window inventory and rescue only need monitor areas; EDID and display-path telemetry
    // (registry and QueryDisplayConfig reads) is for the Displays view.
    public IReadOnlyList<MonitorSnapshot> GetMonitors(bool includeTelemetry = true)
    {
        var telemetry = includeTelemetry
            ? _monitorInventory.GetActiveTelemetry()
            : new Dictionary<string, MonitorTelemetry>();
        return Screen.AllScreens
            .OrderByDescending(screen => screen.Primary)
            .ThenBy(screen => screen.Bounds.X)
            .ThenBy(screen => screen.Bounds.Y)
            .Select((screen, index) =>
            {
                telemetry.TryGetValue(screen.DeviceName, out var details);
                var healthReasons = details?.HealthReasons ?? ["Display identity and capability telemetry is unavailable."];
                return new MonitorSnapshot(
                    Number: index + 1,
                    DeviceName: screen.DeviceName,
                    DisplayName: NormalizeDeviceName(screen.DeviceName),
                    Primary: screen.Primary,
                    Bounds: RectSnapshot.From(screen.Bounds),
                    WorkingArea: RectSnapshot.From(screen.WorkingArea),
                    BitsPerPixel: screen.BitsPerPixel,
                    Manufacturer: details?.Manufacturer ?? "Unknown",
                    ManufacturerCode: details?.ManufacturerCode ?? string.Empty,
                    Model: details?.Model ?? "Unknown display",
                    SerialNumber: details?.SerialNumber ?? string.Empty,
                    ProductCode: details?.ProductCode ?? string.Empty,
                    Connection: details?.Connection ?? "Unknown",
                    ConnectorInstance: details?.ConnectorInstance ?? 0,
                    TargetAvailable: details?.TargetAvailable ?? false,
                    EdidValid: details?.EdidValid ?? false,
                    PhysicalWidthCm: details?.PhysicalWidthCm ?? 0,
                    PhysicalHeightCm: details?.PhysicalHeightCm ?? 0,
                    ManufactureWeek: details?.ManufactureWeek ?? 0,
                    ManufactureYear: details?.ManufactureYear ?? 0,
                    PreferredWidth: details?.PreferredWidth ?? 0,
                    PreferredHeight: details?.PreferredHeight ?? 0,
                    RefreshRateHz: details?.RefreshRateHz ?? 0,
                    AdvancedColorSupported: details?.AdvancedColorSupported ?? false,
                    AdvancedColorEnabled: details?.AdvancedColorEnabled ?? false,
                    BitsPerColorChannel: details?.BitsPerColorChannel ?? 0,
                    Capabilities: details?.Capabilities ?? [],
                    Health: details?.Health ?? "Review",
                    HealthLevel: details?.HealthLevel ?? "warn",
                    HealthReasons: healthReasons,
                    MultipleActivePaths: details?.MultipleActivePaths ?? false);
            })
            .ToArray();
    }

    public IReadOnlyList<WindowSnapshot> GetWindows(bool includeOwnProcess = false)
    {
        var monitors = GetMonitors(includeTelemetry: false);
        var windows = new List<WindowSnapshot>();
        var shell = NativeMethods.GetShellWindow();

        NativeMethods.EnumWindows((handle, _) =>
        {
            if (handle == shell) return true;
            var snapshot = InspectWindow(handle, monitors, includeOwnProcess);
            if (snapshot is not null) windows.Add(snapshot);
            return true;
        }, 0);

        return windows
            .OrderByDescending(window => window.RescueCandidate)
            .ThenBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public WindowSnapshot? InspectWindow(nint handle, bool includeOwnProcess = false)
    {
        return InspectWindow(handle, GetMonitors(includeTelemetry: false), includeOwnProcess);
    }

    private WindowSnapshot? InspectWindow(
        nint handle,
        IReadOnlyList<MonitorSnapshot> monitors,
        bool includeOwnProcess)
    {
        if (handle == 0 || !NativeMethods.IsWindow(handle) || !NativeMethods.IsWindowVisible(handle)) return null;

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (!includeOwnProcess && processId == _currentProcessId) return null;

        var titleLength = NativeMethods.GetWindowTextLength(handle);
        if (titleLength <= 0) return null;
        var titleBuffer = new System.Text.StringBuilder(titleLength + 1);
        NativeMethods.GetWindowText(handle, titleBuffer, titleBuffer.Capacity);
        var title = titleBuffer.ToString().Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0) return null;

        try
        {
            if (NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                return null;
        }
        catch (DllNotFoundException)
        {
            // DWM is present on supported Windows versions; continue if an unusual host omits it.
        }

        // A minimized window is judged by where it will restore to; anything else (maximized
        // included) by the frame it shows now.
        var minimized = NativeMethods.IsIconic(handle);
        var maximized = NativeMethods.IsZoomed(handle);
        var rectangle = Rectangle.Empty;
        if (minimized ? !TryGetRestoreRectangle(handle, out rectangle) : !TryGetVisibleFrame(handle, out rectangle)) return null;
        if (rectangle.Width <= 0 || rectangle.Height <= 0) return null;

        var windowArea = Math.Max(1L, (long)rectangle.Width * rectangle.Height);
        long visibleArea = 0;
        var bestArea = 0L;
        MonitorSnapshot? bestMonitor = null;
        foreach (var monitor in monitors)
        {
            var intersection = Rectangle.Intersect(rectangle, monitor.WorkingArea.ToRectangle());
            var area = Math.Max(0L, (long)intersection.Width * intersection.Height);
            visibleArea += area;
            if (area > bestArea)
            {
                bestArea = area;
                bestMonitor = monitor;
            }
        }

        var titleBand = new Rectangle(rectangle.Left, rectangle.Top, rectangle.Width, Math.Min(TitleBandHeight, rectangle.Height));
        var titleReachable = monitors.Any(monitor =>
        {
            var intersection = Rectangle.Intersect(titleBand, monitor.WorkingArea.ToRectangle());
            return intersection.Width >= MinimumReachableTitleWidth && intersection.Height >= 8;
        });

        var visiblePercent = Math.Min(100d, visibleArea * 100d / windowArea);
        var status = minimized
            ? "minimized"
            : visibleArea == 0
                ? "off-screen"
                : !titleReachable
                    ? "stranded"
                    : visiblePercent < 99.5d
                        ? "partial"
                        : "visible";

        var nearestMonitor = bestMonitor ?? GetNearestMonitor(rectangle, monitors);
        return new WindowSnapshot(
            Handle: ToHandleString(handle),
            ProcessId: processId,
            ProcessName: GetProcessName(processId),
            Title: title,
            Bounds: RectSnapshot.From(rectangle),
            MonitorDevice: bestMonitor?.DeviceName ?? string.Empty,
            MonitorName: bestMonitor?.DisplayName ?? "OFF-DESKTOP",
            NearestMonitorDevice: nearestMonitor?.DeviceName ?? string.Empty,
            NearestMonitorName: nearestMonitor?.DisplayName ?? "unknown",
            Status: status,
            VisiblePercent: Math.Round(visiblePercent, 1),
            Minimized: minimized,
            Maximized: maximized,
            TitleBarReachable: titleReachable,
            RescueCandidate: !minimized && !titleReachable);
    }

    public MoveResult MoveWindow(string handleText, string deviceName, int cascadeIndex = 0)
    {
        var handle = ParseHandle(handleText);
        return MoveWindow(handle, deviceName, cascadeIndex);
    }

    public MoveResult MoveWindow(string handleText, string deviceName, Point? topLeft)
    {
        return MoveWindow(ParseHandle(handleText), deviceName, 0, topLeft);
    }

    // topLeft (physical pixels, visible frame) places the window where it was dropped on the
    // map, kept inside the target's work area; without it the window is centred (cascaded).
    public MoveResult MoveWindow(nint handle, string deviceName, int cascadeIndex = 0, Point? topLeft = null)
    {
        if (!NativeMethods.IsWindow(handle)) return new MoveResult(false, handle.ToString(), deviceName, "Window no longer exists.");
        var screen = Screen.AllScreens.FirstOrDefault(item => item.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
        if (screen is null) return new MoveResult(false, ToHandleString(handle), deviceName, "Target monitor is not active.");

        var wasMinimized = NativeMethods.IsIconic(handle);
        var wasMaximized = NativeMethods.IsZoomed(handle);
        var current = Rectangle.Empty;
        var readable = wasMinimized || wasMaximized
            ? TryGetRestoreRectangle(handle, out current)
            : TryGetWindowRectangle(handle, out current);
        if (!readable)
            return new MoveResult(false, ToHandleString(handle), deviceName, "Could not read window placement.");

        var work = screen.WorkingArea;
        var width = Math.Clamp(current.Width, Math.Min(320, work.Width), work.Width);
        var height = Math.Clamp(current.Height, Math.Min(180, work.Height), work.Height);
        var cascade = Math.Max(0, cascadeIndex % 8) * 28;
        var centeredX = work.Left + Math.Max(0, (work.Width - width) / 2);
        var centeredY = work.Top + Math.Max(0, (work.Height - height) / 2);
        var x = Math.Min(work.Right - width, centeredX + cascade);
        var y = Math.Min(work.Bottom - height, centeredY + cascade);
        if (topLeft is { } requested)
        {
            // The map shows the visible frame; the window rectangle also has invisible borders.
            var border = !wasMinimized && !wasMaximized && TryGetVisibleFrame(handle, out var frame)
                ? new Point(frame.Left - current.Left, frame.Top - current.Top)
                : Point.Empty;
            x = Math.Clamp(requested.X - border.X, work.Left, Math.Max(work.Left, work.Right - width));
            y = Math.Clamp(requested.Y - border.Y, work.Top, Math.Max(work.Top, work.Bottom - height));
        }

        var targetRectangle = new Rectangle(x, y, width, height);
        var moved = wasMinimized
            ? MoveMinimized(handle, targetRectangle)
            : wasMaximized
                ? MoveMaximized(handle, targetRectangle)
                : NativeMethods.SetWindowPos(
                    handle,
                    0,
                    x,
                    y,
                    width,
                    height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        return new MoveResult(
            moved,
            ToHandleString(handle),
            deviceName,
            moved ? $"Moved to {NormalizeDeviceName(deviceName)}." : "Windows rejected the move request.");
    }

    // Without a target, each stranded window goes to its nearest active display (the one the
    // map's drive line points at); with one, they all go there.
    public BatchMoveResult RescueAll(string? targetDevice = null)
    {
        var fallback = ResolveTarget(targetDevice);
        if (fallback is null) return new BatchMoveResult(0, 0, "No active target monitor.", string.Empty);
        var candidates = GetWindows().Where(window => window.RescueCandidate).ToArray();
        var moved = 0;
        var perTarget = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var device = !string.IsNullOrWhiteSpace(targetDevice) || string.IsNullOrWhiteSpace(candidate.NearestMonitorDevice)
                ? fallback.DeviceName
                : candidate.NearestMonitorDevice;
            var cascade = perTarget.GetValueOrDefault(device);
            perTarget[device] = cascade + 1;
            if (MoveWindow(candidate.Handle, device, cascade).Ok) moved++;
        }
        var message = candidates.Length == 0
            ? "No stranded windows."
            : $"Rescued {moved} of {candidates.Length} stranded window{(candidates.Length == 1 ? "" : "s")}.";
        return new BatchMoveResult(candidates.Length, moved, message, string.IsNullOrWhiteSpace(targetDevice) ? string.Empty : fallback.DeviceName);
    }

    public BatchMoveResult GatherAll(string targetDevice)
    {
        var target = ResolveTarget(targetDevice);
        if (target is null) return new BatchMoveResult(0, 0, "No active target monitor.", string.Empty);
        var windows = GetWindows().Where(window => !window.Minimized).ToArray();
        var moved = 0;
        for (var index = 0; index < windows.Length; index++)
        {
            if (MoveWindow(windows[index].Handle, target.DeviceName, index).Ok) moved++;
        }
        return new BatchMoveResult(windows.Length, moved, $"Gathered {moved} of {windows.Length} windows.", target.DeviceName);
    }

    public MoveResult MoveForegroundToCursor()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == 0) return new MoveResult(false, string.Empty, string.Empty, "No foreground window found.");
        return MoveWindowToCursor(handle);
    }

    public MoveResult MoveWindowToCursor(nint handle)
    {
        if (handle == 0 || !NativeMethods.IsWindow(handle))
            return new MoveResult(false, string.Empty, string.Empty, "The last application window is no longer available.");
        if (!NativeMethods.GetCursorPos(out var point)) return new MoveResult(false, ToHandleString(handle), string.Empty, "Could not read cursor position.");
        var screen = Screen.FromPoint(new Point(point.X, point.Y));
        return MoveWindow(handle, screen.DeviceName);
    }

    private static MonitorSnapshot? GetNearestMonitor(Rectangle rectangle, IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (monitors.Count == 0) return null;
        var centerX = rectangle.Left + rectangle.Width / 2d;
        var centerY = rectangle.Top + rectangle.Height / 2d;
        return monitors.OrderBy(monitor =>
        {
            var bounds = monitor.Bounds;
            var monitorX = bounds.X + bounds.Width / 2d;
            var monitorY = bounds.Y + bounds.Height / 2d;
            return Math.Pow(centerX - monitorX, 2) + Math.Pow(centerY - monitorY, 2);
        }).First();
    }

    private static Screen? ResolveTarget(string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var requested = Screen.AllScreens.FirstOrDefault(item => item.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            if (requested is not null) return requested;
        }
        return Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
    }

    // The earlier code restored with ShowWindowAsync and then called SetWindowPos. The posted
    // restore ran after the move and put the window back, so minimized and maximized windows
    // reported "moved" but stayed where they were. Every step below is synchronous.

    // A minimized window stays minimized; only its restore rectangle moves, so restoring it
    // later lands on the target (maximized there if it was maximized before minimizing).
    private static bool MoveMinimized(nint handle, Rectangle target)
    {
        var placement = NativeMethods.WindowPlacement.Create();
        if (!NativeMethods.GetWindowPlacement(handle, ref placement)) return false;
        placement.NormalPosition = ScreenToWorkspace(handle, target);
        placement.Flags &= ~NativeMethods.WPF_SETMINPOSITION;
        placement.ShowCommand = NativeMethods.SW_SHOWMINNOACTIVE;
        return NativeMethods.SetWindowPlacement(handle, ref placement);
    }

    // SetWindowPlacement can't move a maximized window: it restores to the old normal position
    // and ignores the new one (measured on Windows 10 22H2). Restore, move, then maximize again
    // on the target monitor.
    private static bool MoveMaximized(nint handle, Rectangle target)
    {
        NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOWNOACTIVATE);
        var moved = NativeMethods.SetWindowPos(
            handle,
            0,
            target.X,
            target.Y,
            target.Width,
            target.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOWMAXIMIZED);
        return moved;
    }

    private static bool TryGetWindowRectangle(nint handle, out Rectangle rectangle)
    {
        if (NativeMethods.GetWindowRect(handle, out var rect))
        {
            rectangle = rect.ToRectangle();
            return true;
        }
        rectangle = Rectangle.Empty;
        return false;
    }

    // The frame DWM draws, without the invisible resize borders GetWindowRect includes. Those
    // borders made edge-snapped and maximized windows look partly off-screen.
    private static bool TryGetVisibleFrame(nint handle, out Rectangle rectangle)
    {
        try
        {
            if (NativeMethods.DwmGetWindowRectAttribute(handle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<NativeMethods.Rect>()) == 0
                && frame.Width > 0 && frame.Height > 0)
            {
                rectangle = frame.ToRectangle();
                return true;
            }
        }
        catch (DllNotFoundException)
        {
            // Fall back to the window rectangle.
        }
        return TryGetWindowRectangle(handle, out rectangle);
    }

    // The restore (normal) rectangle in screen coordinates. That is where a minimized or
    // maximized window returns to, and what the move code places.
    internal static bool TryGetRestoreRectangle(nint handle, out Rectangle rectangle)
    {
        var placement = NativeMethods.WindowPlacement.Create();
        if (NativeMethods.GetWindowPlacement(handle, ref placement))
        {
            rectangle = WorkspaceToScreen(handle, placement.NormalPosition.ToRectangle());
            return rectangle.Width > 0 && rectangle.Height > 0;
        }
        rectangle = Rectangle.Empty;
        return false;
    }

    // WINDOWPLACEMENT uses workspace coordinates for normal top-level windows: screen
    // coordinates shifted by any taskbar or app bar docked at the top or left of the primary
    // monitor. Tool windows use plain screen coordinates.
    private static Rectangle WorkspaceToScreen(nint handle, Rectangle rectangle)
    {
        var offset = WorkspaceOffset(handle);
        return new Rectangle(rectangle.X + offset.X, rectangle.Y + offset.Y, rectangle.Width, rectangle.Height);
    }

    private static NativeMethods.Rect ScreenToWorkspace(nint handle, Rectangle rectangle)
    {
        var offset = WorkspaceOffset(handle);
        return new NativeMethods.Rect
        {
            Left = rectangle.Left - offset.X,
            Top = rectangle.Top - offset.Y,
            Right = rectangle.Right - offset.X,
            Bottom = rectangle.Bottom - offset.Y
        };
    }

    private static Point WorkspaceOffset(nint handle)
    {
        var exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        var primary = Screen.PrimaryScreen;
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 || primary is null) return Point.Empty;
        return new Point(primary.WorkingArea.Left - primary.Bounds.Left, primary.WorkingArea.Top - primary.Bounds.Top);
    }

    internal static string ToHandleString(nint handle) => "0x" + handle.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    internal static nint ParseHandle(string value)
    {
        var clean = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (!long.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException("Invalid window handle.");
        return new nint(parsed);
    }

    private static string GetProcessName(uint processId)
    {
        try { return Process.GetProcessById((int)processId).ProcessName; }
        catch { return "unknown"; }
    }

    private static string NormalizeDeviceName(string deviceName) => deviceName.Replace(@"\\.\", string.Empty, StringComparison.OrdinalIgnoreCase);
}

public sealed record RectSnapshot(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public Rectangle ToRectangle() => new(X, Y, Width, Height);
    public static RectSnapshot From(Rectangle rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}

public sealed record MonitorSnapshot(
    int Number,
    string DeviceName,
    string DisplayName,
    bool Primary,
    RectSnapshot Bounds,
    RectSnapshot WorkingArea,
    int BitsPerPixel,
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
    bool MultipleActivePaths);

public sealed record WindowSnapshot(
    string Handle,
    uint ProcessId,
    string ProcessName,
    string Title,
    RectSnapshot Bounds,
    string MonitorDevice,
    string MonitorName,
    string NearestMonitorDevice,
    string NearestMonitorName,
    string Status,
    double VisiblePercent,
    bool Minimized,
    bool Maximized,
    bool TitleBarReachable,
    bool RescueCandidate);

public sealed record MoveResult(bool Ok, string Handle, string TargetDevice, string Message);
public sealed record BatchMoveResult(int Found, int Moved, string Message, string TargetDevice);
