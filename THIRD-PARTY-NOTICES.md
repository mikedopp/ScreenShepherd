# Third-party notices

Screen Shepherd is MIT licensed (see `LICENSE`). It uses the components below. The license texts travel with the source and with every release build.

## Shipped with the app

| Component | License | Where in a release | License text | Used for |
|---|---|---|---|---|
| Microsoft.Web.WebView2 SDK 1.0.4314.35 (`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Wpf.dll`, `WebView2Loader.dll`) | Microsoft BSD-style license | app folder | `licenses\Microsoft.Web.WebView2-LICENSE.txt`, `licenses\Microsoft.Web.WebView2-NOTICE.txt` | hosting the window's page |
| System.Management 10.0.15 (`System.Management.dll`) | MIT, © .NET Foundation and Contributors | app folder | `licenses\System.Management-LICENSE.txt`, `licenses\System.Management-THIRD-PARTY-NOTICES.txt` | WMI queries (adapters, modes, host, page file, power plan) |
| Gleam 0.9.1 badge and orb editor | MIT, © 2026 mikedopp | `wwwroot\gleam\` | `wwwroot\gleam\LICENSE`, `wwwroot\gleam\NOTICE.md` | the version button and its orb editor |
| orb by LerSent001 (inside Gleam's editor and presets) | MIT | `wwwroot\gleam\` | `wwwroot\gleam\LICENSE-orb-LerSent001` | orb shader, presets and editor app |
| Toolcraft UI by Pixel Point (inside Gleam's editor) | MIT, © 2026 Pixel Point | `wwwroot\gleam\editor\` | `wwwroot\gleam\LICENSE-toolcraft-PixelPoint.md` | the orb editor's controls |

## Required on the PC, not shipped

| Component | License | Used for |
|---|---|---|
| .NET 10 Desktop Runtime | MIT | running the app (WPF window, Windows Forms tray icon, UI Automation, event log, performance data) |
| Microsoft Edge WebView2 Runtime | Microsoft | rendering the window's page |

## Data and references

Monitor refresh limits for recognized models (Dell P2425H, P2425HE, P2426HE) and the Surface Laptop 5 external-display limit are facts taken from the makers' published specifications; the app names each source next to the value. Microsoft documentation pages are referenced by name and link only.

## Network

Screen Shepherd makes no network connections. The Settings shortcuts open local `ms-settings:` pages.
