# Changelog

## 1.7.1 — 2026-11-21

### Fixed
- **Connection types from Windows' number 14 on were one off.** A Miracast display showed as "Indirect wired", an SDTV dongle as "Miracast", a USB or DisplayLink display as "Indirect virtual", a virtual display as "USB4", and DisplayPort over USB-C as "Connector 18". They now read Miracast, SDTV dongle, USB or indirect display, Virtual display and DisplayPort over USB-C. The self-test checks the numbering.

## 1.7.0 — 2026-11-21

Screen Shepherd now runs on Chassis, the shared app shell, instead of its own window, tray, single-instance and pop-out code. What the app does (the desk map, moves, rescues, Health) is unchanged.

### Changed
- **Version pop-out.** It is now Chassis's panel on the right edge, in Trial Field's colours: **Settings** (glass, Close to tray, rescue behaviour, start hidden, data folder), **Dependencies**, **Help**, **Diagnostics**. Appearance is part of Settings. Dependencies lists where everything lives; what each Windows part answered on the last read moved to Diagnostics, next to the run log.
- **Settings and data moved** to `%LOCALAPPDATA%\Screen Shepherd\` (`settings.json`, `screen shepherd.log`, the `webview` profile). Settings from 1.6 are copied over once on the first run; the old `%LOCALAPPDATA%\ScreenShepherd\` folder is left as it is. A custom orb look saved in 1.6 is not carried over.
- **Run log on disk.** The run log is now also a file (restarted at 256 KB), not only in memory.
- **Glass defaults** are Chassis's (62% opacity, 22 px blur) for a fresh install; Reset glass returns to them.
- **Tray menu.** Close to tray is set in Settings only; the tray menu keeps Open, Hide, the two rescue actions and Exit. The one-time "Still running" notification on first close is gone.
- The window is WPF (it was Windows Forms); the tray icon and the live move test still use Windows Forms.
- The version lives in `VERSION` (it was `Directory.Build.props`).
- The C# source moved into `src\`.
- README: a short summary, Requirements, Quick start and How to use at the top, captions on every picture, and new screenshots of the 1.7 window.

### Added
- `--selftest` (Chassis's shell checks and the app's own) and `--qa <dir>` (self-test, then glass, solid, every pop-out tab, a narrow window, Health and a selected window, each rendered off-screen and read-only).
- `--hidden` starts in the notification area (`--minimized` still works).
- Check manifests for the release build.

### Changed for scripts
- `--snapshot` now takes a PNG file and Chassis's switches (`--panel`, `--solid`, `--width`, `--height`, `--query`). The old `--tabs`, `--glass`, `--size`, `--redact`, `--hide` and `--sample-stray` became `--query` options (`view=health`, `select=1`, `redact=1`, `hide=a,b`, `stray=1`) and `--solid`. What the page shows is written to `<file>.png.json`.

## 1.6.2 — 2026-11-21

### Changed
- Gleam version orb and its editor updated from 0.4.0 to 0.9.1 (adds the Contour and Rings presets).
- Microsoft.Web.WebView2 SDK 1.0.4078.44 → 1.0.4314.35.
- System.Management 10.0.12 → 10.0.15.

## 1.6.1 — 2026-10-10

### Changed
- The version pop-out no longer has a Credentials section. Screen Shepherd uses no sign-in, passwords, tokens or keys, so the section only ever said "None required". That fact is now one row in Dependencies ("Credentials: none needed").
- Diagnostics JSON drops the empty credentials block.

## 1.6.0 — 2026-10-10

A redesign: the window is now a utility, not a report.

### Changed
- **Desk view.** The window opens on the whole desk: every monitor and window drawn to scale with run numbers, a fence around the desk, and windows grouped by display in a filterable list beside it. Health verdicts for monitors, graphics, display modes and Windows checks sit along the bottom and open the Health view.
- **Move by dragging.** Drag a window on the map and drop it where it should land on any display (it stays inside the work area; Grid snaps drops to 120 pixels), or select it on the map or in the list and use Send to or Center. Maximized and minimized windows keep their state.
- **Stranded windows** sit beyond the fence with their true coordinates and a drive line to their nearest display. **Rescue stranded** (and display-change rescue) now sends each one to that nearest display instead of everything to the primary.
- **Health view** replaces the Displays and Graphics tabs with cards instead of wide tables. Each monitor card has its run number, a panel silhouette (all drawn to one centimetre scale), identity, path, mode, and its refresh verdict (path max and panel max). The graphics card has driver, Plug and Play, mode and recovered resets. Ranked Windows checks and browser load follow.
- **Version pop-out.** Settings, Appearance, Dependencies, Credentials, Help and Diagnostics moved out of tabs into a pop-out under the version orb. Diagnostics adds Open data folder and the app, runtime, WebView2 and Windows versions with the app and data folders.
- **Dependencies** now say where each one lives (DLL and API, registry path, WMI classes, event log channel, .NET and WebView2 install folders, profile, UI and settings paths); what was read moved to the status column.
- **Look.** The Trial Field visual system: night-pasture greens on one tonal ramp, wool-white windows, ochre only for where things go and the primary action, red only for stranded windows. Drawn icons replace text-glyph icons.
- **Glass.** Floating panels (the pop-out, map toolbar, selection bar, notices) use frosted glass; the map, lists and tables stay solid. Appearance has glass on/off, opacity and blur, saved with the other settings, and Reset. It falls back to solid panels when Windows transparency effects are off, a high-contrast theme is on, or forced colours are active.
- **Icon.** Same design (a stray screen driven home to the active screens), with more pop: a moss tile with soft top light, wool-white screens with depth, and a bolder ochre arrow that stays readable at every size. A simplified version is used at 16-24 px (tray, small taskbar). The window and tray now use the multi-size icon file instead of an icon drawn at run time.

### Licensing
- Screen Shepherd is now MIT licensed (it was proprietary in 1.5.0). Third-party notices are unchanged.

### Added
- `windows.move` accepts an exact drop position; the live move test covers it.
- Snapshot options: views `desk`, `desk-select`, `health` and each pop-out section; `--size`, `--glass on|off`, `--sample-stray`.
- `build.cmd` proves glass on the rendered app (forced on renders glass, forced off renders solid) and checks the embedded icon.
- `DESIGN.md` and the design brief.

## 1.5.0 — 2026-10-10

### Fixed
- Moving a **minimized** window reported success but left its restore position off-desktop, so restoring it brought it back out of reach. Moving a **maximized** window to another monitor left it maximized where it was. The move restored with `ShowWindowAsync` and then called `SetWindowPos`; the posted restore ran after the move and undid it. Minimized windows now move through one synchronous `SetWindowPlacement` (they stay minimized and restore onto the target); maximized windows are restored, moved and re-maximized synchronously. Both cases are covered by the live move test.
- Maximized and edge-snapped windows were judged by `GetWindowRect`, which includes invisible resize borders, and maximized windows by their restore rectangle. A window maximized on a monitor could be reported off-screen and pulled to the primary display by display-change rescue. Visibility now uses the frame DWM draws.
- The window could freeze for seconds: every 4-second refresh ran WMI, performance-counter and UI Automation queries on the UI thread, and the tuning reads were not cached at all. State reads, moves and rescues now run off the UI thread with one refresh in flight at a time, and the slow readings are cached for 60 seconds. On the test PC the smoke test's full read went from about 14.5 s to about 1.7 s, and a cached graphics read takes under 10 ms.
- Window-show events triggered EDID and display-path reads for every child control. They are now filtered to top-level windows, and window inventory uses monitor areas only.
- Commit charge showed 0% and GPU memory was blank when the WMI performance classes are not registered. Memory and commit now come from `GetPerformanceInfo` and GPU memory from the PDH `GPU Adapter Memory` counters, and the GPU figure appears in the memory evidence.
- Graphics advice assumed a Surface Laptop with Intel graphics on every PC (for example "Check the Surface graphics package before testing Intel generic drivers" on an NVIDIA desktop). Driver advice now follows the PC maker or GPU vendor (Surface, NVIDIA, AMD, Intel), Surface-only notes appear only on a Surface, and the integrated-GPU wording appears only for integrated GPUs.
- Board-maker placeholders such as "To Be Filled By O.E.M." in the device name are replaced with the motherboard maker and model.
- A second launch showed a message box; it now brings the running window back.
- Task Manager's End task only hid the window (so Task Manager then killed it and left a ghost tray icon); it now exits cleanly.
- The tray icon handle leaked once per launch.
- The help text said stranding used the normal placement rectangle; it now describes the visible frame and the restore rectangle for minimized windows.

### Added
- **Close to tray** setting (on by default, explained in Settings and the tray menu). Off: closing the window exits. Exit always quits.
- Tray menu: version line, Hide.
- `ScreenShepherd.exe --exit` asks a running copy to quit, like the tray's Exit (for scripts and updates).
- `ScreenShepherd.exe --snapshot <dir> [--tabs readiness,graphics]` renders the real UI off-screen with live host state and saves each tab with WebView2's own capture, plus a receipt of what the page shows. No screen capture, and it refuses any command that would change something. `--redact` replaces window titles, monitor serials and profile paths for images meant to be shared; `--hide a,b` leaves processes out.
- README summary, redacted screenshots of four tabs (`docs\screenshots\`), and `build.cmd -Screenshots` to refresh them.
- Legal: proprietary `LICENSE`, `THIRD-PARTY-NOTICES.md`, and the WebView2 SDK and System.Management license texts in `licenses\`. All of them ship in every release build.
- Gleam orb version button that shows the build's version and reflects busy, success and error states.
- Dependencies list with where each dependency lives: settings file, WebView2 profile, UI assets, registry keys, WMI classes, event log, performance counters, and the fact that no network host or environment variable is used.
- Smoke test checks the Gleam kit, the version against the exe file version, and the graphics cache, and records read timings and window status counts.
- Live move test covers minimized and maximized windows. Test windows are transparent, click-through and never activated, and target a monitor away from the mouse pointer.

### Build
- .NET 10 (`net10.0-windows`), framework-dependent instead of a 156 MB self-contained single file; no PDB or package XML docs in the output.
- `build.cmd` / `build.ps1` publish to `dist\ScreenShepherd-<version>-win-x64\`, smoke-test the published exe, check its ProductVersion, and write a zip and SHA256 sums.
- The version lives only in `Directory.Build.props`; the hard-coded copies in the code, page and manifest are gone.
- Removed the stale `publish\` output, the work-PC validator and wiki publishing instructions, and tracked diagnostic receipts from another PC. `reports\` is now git-ignored.

## 1.4.0 and earlier

Initial development: window inventory and rescue, display topology map, monitor identity and health, graphics adapter health, display-mode optimization, Windows tuning checks, browser load, and the wake signal. See the git history.
