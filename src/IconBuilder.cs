using System.Windows.Forms;
namespace ScreenShepherd;

// The app icon, ScreenShepherd.ico, embedded in the assembly. assets\icon\build-icon.ps1 builds
// it from two SVG masters: the full icon for 32 px and up, a flatter, heavier one for 16-24 px.
internal static class IconBuilder
{
    private const string ResourceName = "ScreenShepherd.ico";

    // Every size, for the window (Windows picks the title-bar and Alt+Tab sizes from it).
    internal static Icon Window() => Load(null);

    // The tray's small size at the current DPI (16 px at 100%, 20 or 24 px when scaled).
    internal static Icon Tray() => Load(SystemInformation.SmallIconSize);

    private static Icon Load(Size? size)
    {
        using var stream = typeof(IconBuilder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return size is { } requested ? new Icon(stream, requested) : new Icon(stream);
    }
}
