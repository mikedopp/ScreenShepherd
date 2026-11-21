namespace ScreenShepherd;

// For screenshots meant to be shared (--snapshot --redact): keeps the real layout and window
// geometry but drops what identifies the person or the specific hardware units: window
// titles, monitor serial numbers, and the user-profile part of paths.
internal static class StateRedactor
{
    // hiddenProcesses (--hide) leaves those processes' windows out entirely.
    internal static AppState Apply(AppState state, IReadOnlyCollection<string> hiddenProcesses)
    {
        var perProcess = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string Title(WindowSnapshot window)
        {
            perProcess[window.ProcessName] = perProcess.GetValueOrDefault(window.ProcessName) + 1;
            return $"{window.ProcessName} window {perProcess[window.ProcessName]}";
        }

        var windows = state.Windows
            .Where(window => !hiddenProcesses.Contains(window.ProcessName, StringComparer.OrdinalIgnoreCase))
            .Select(window => window with { Title = Title(window) })
            .ToArray();
        return state with
        {
            Windows = windows,
            OffScreenCount = windows.Count(window => window.RescueCandidate),
            Monitors = state.Monitors
                .Select(monitor => monitor with { SerialNumber = monitor.SerialNumber.Length > 0 ? "redacted" : string.Empty })
                .ToArray(),
            Dependencies = state.Dependencies with
            {
                Items = state.Dependencies.Items.Select(item => item with { Target = Paths(item.Target) }).ToArray()
            },
            Logs = state.Logs.Select(entry => entry with { Message = Paths(entry.Message) }).ToArray()
        };
    }

    private static string Paths(string value)
    {
        var appFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return value
            .Replace(appFolder, "<app folder>", StringComparison.OrdinalIgnoreCase)
            .Replace(localAppData, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase)
            .Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }
}
