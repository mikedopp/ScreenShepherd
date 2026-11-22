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
        // Display keys can carry a serial number; swap each for a stable placeholder everywhere it appears.
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        string Key(string key) => key.Length == 0 ? key
            : keys.TryGetValue(key, out var k) ? k : keys[key] = $"display-{keys.Count + 1}";
        var awayCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string AwayTitle(ParkedWindow window)
        {
            awayCount[window.ProcessName] = awayCount.GetValueOrDefault(window.ProcessName) + 1;
            return $"{window.ProcessName} window {awayCount[window.ProcessName]}";
        }

        var monitors = state.Monitors
            .Select(monitor => monitor with
            {
                SerialNumber = monitor.SerialNumber.Length > 0 ? "redacted" : string.Empty,
                IdentityKey = Key(monitor.IdentityKey),
                Key = Key(monitor.Key)
            })
            .ToArray();
        var away = state.Away
            .Select(display => display with
            {
                Key = Key(display.Key),
                Windows = display.Windows
                    .Where(window => !hiddenProcesses.Contains(window.ProcessName, StringComparer.OrdinalIgnoreCase))
                    .Select(window => window with { Title = AwayTitle(window), Handle = "0" })
                    .ToArray()
            })
            .ToArray();
        var history = state.History is null ? null : state.History with
        {
            Rows = state.History.Rows.Select(row => row with { Key = Key(row.Key) }).ToArray(),
            Events = state.History.Events.Select(item => item with { Key = Key(item.Key) }).ToArray()
        };
        return state with
        {
            Windows = windows,
            OffScreenCount = windows.Count(window => window.RescueCandidate),
            Monitors = monitors,
            Away = away,
            History = history,
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
