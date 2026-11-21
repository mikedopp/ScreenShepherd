using System.Diagnostics;
using System.Windows.Automation;

namespace ScreenShepherd;

internal sealed class BrowserUsageService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private readonly object _cacheGate = new();
    private BrowserUsageSnapshot? _cache;

    internal BrowserUsageSnapshot GetSnapshot()
    {
        lock (_cacheGate)
        {
            if (_cache is not null && DateTimeOffset.Now - _cache.CapturedAt <= CacheLifetime) return _cache;
            _cache = new BrowserUsageSnapshot(
                DateTimeOffset.Now,
                [
                    ReadBrowser("Google Chrome", "chrome"),
                    ReadBrowser("Microsoft Edge", "msedge")
                ]);
            return _cache;
        }
    }

    private static BrowserUsageItem ReadBrowser(string displayName, string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            if (processes.Length == 0)
                return new BrowserUsageItem(displayName, "Not running", "info", 0, "not-running", 0, 0, 0, 0);

            long workingSet = 0;
            long privateMemory = 0;
            var windowHandles = new List<nint>();
            foreach (var process in processes)
            {
                try
                {
                    workingSet += process.WorkingSet64;
                    privateMemory += process.PrivateMemorySize64;
                    if (process.MainWindowHandle != 0) windowHandles.Add(process.MainWindowHandle);
                }
                catch (InvalidOperationException)
                {
                    // Chromium processes can exit while the snapshot is being collected.
                }
            }

            var tabRead = ReadVisibleTabStrips(windowHandles);
            return new BrowserUsageItem(
                Name: displayName,
                Status: "Running",
                Level: tabRead.Status == "ready" ? "ok" : "info",
                TabCount: tabRead.Count,
                TabCountStatus: tabRead.Status,
                ProcessCount: processes.Length,
                WindowCount: windowHandles.Count,
                WorkingSetMiB: Math.Round(workingSet / 1024d / 1024d, 0),
                PrivateMemoryMiB: Math.Round(privateMemory / 1024d / 1024d, 0));
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static TabReadResult ReadVisibleTabStrips(IReadOnlyList<nint> windowHandles)
    {
        var tabControlCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab);
        var tabItemCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
        var count = 0;
        var foundBrowserStrip = false;
        foreach (var handle in windowHandles)
        {
            try
            {
                var window = AutomationElement.FromHandle(handle);
                var windowBounds = window.Current.BoundingRectangle;
                var controls = window.FindAll(TreeScope.Descendants, tabControlCondition);
                foreach (AutomationElement control in controls)
                {
                    var bounds = control.Current.BoundingRectangle;
                    var nearWindowTop = bounds.Top >= windowBounds.Top - 2 && bounds.Top <= windowBounds.Top + 120;
                    if (!nearWindowTop || bounds.Height is <= 0 or > 80 || bounds.Width < 200) continue;
                    var items = control.FindAll(TreeScope.Children, tabItemCondition);
                    if (items.Count == 0) continue;
                    foundBrowserStrip = true;
                    count += items.Count;
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Memory totals remain exact when browser chrome does not expose UI Automation.
            }
        }

        return foundBrowserStrip
            ? new TabReadResult(count, "ready")
            : new TabReadResult(0, "unavailable");
    }

    private sealed record TabReadResult(int Count, string Status);
}

public sealed record BrowserUsageSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<BrowserUsageItem> Browsers);

public sealed record BrowserUsageItem(
    string Name,
    string Status,
    string Level,
    int TabCount,
    string TabCountStatus,
    int ProcessCount,
    int WindowCount,
    double WorkingSetMiB,
    double PrivateMemoryMiB);
