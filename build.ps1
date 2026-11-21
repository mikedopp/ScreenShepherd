# Builds a versioned Screen Shepherd release in dist\ScreenShepherd-<ver>-win-x64\:
# framework-dependent win-x64 (needs the .NET 10 Desktop Runtime and the WebView2 Runtime), smoke-tested,
# run through Chassis's --qa (self-test plus glass, solid, every pop-out tab, the Health view and a selected window,
# all rendered off-screen from the published exe), zipped, with SHA256 sums. The version comes from VERSION.
# Receipts and QA pictures go to reports\ (git-ignored: they hold this PC's monitor serial numbers and window titles).
#   -LiveMove     also run the live window-move test. It creates fully transparent,
#                 click-through test windows and moves them (normal, minimized, maximized).
#   -Screenshots  refresh docs\screenshots\ from the published exe, redacted (window titles,
#                 monitor serials and profile paths replaced) and with a labelled sample stray.
#   -Hide a,b     with -Screenshots: leave these processes' windows out of the screenshots.
param([switch]$LiveMove, [switch]$Screenshots, [string]$Hide = '')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = (Get-Content VERSION -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must be major.minor.patch, got '$version'" }
$name = "ScreenShepherd-$version-win-x64"
$out = Join-Path 'dist' $name
Write-Host "Screen Shepherd $version" -ForegroundColor Cyan

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish ScreenShepherd.csproj -c Release -r win-x64 --self-contained false -o $out -m:2 -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
foreach ($f in 'ScreenShepherd.exe', 'wwwroot\index.html', 'wwwroot\app.js', 'wwwroot\styles.css', 'wwwroot\icon.svg',
               'wwwroot\chassis\chassis.js', 'wwwroot\chassis\chassis.css', 'wwwroot\chassis\VERSION.txt',
               'wwwroot\gleam\gleam-badge.js', 'wwwroot\gleam\gleam-badge.css', 'wwwroot\gleam\NOTICE.md') {
    if (-not (Test-Path (Join-Path $out $f))) { throw "Release is missing $f" }
}

$exe = (Resolve-Path (Join-Path $out 'ScreenShepherd.exe')).Path
$exeVersion = (Get-Item $exe).VersionInfo.ProductVersion
if ($exeVersion -ne $version) { throw "Exe ProductVersion $exeVersion does not match $version" }

$reports = Join-Path $PSScriptRoot 'reports'
New-Item -ItemType Directory -Force $reports | Out-Null

function Invoke-Receipt([string]$flag, [string]$receipt, [string[]]$extra = @()) {
    $arguments = @($flag, '--receipt', "`"$receipt`"") + $extra
    $process = Start-Process $exe -ArgumentList $arguments -Wait -PassThru
    $result = Get-Content $receipt -Raw | ConvertFrom-Json
    return [pscustomobject]@{ Code = $process.ExitCode; Result = $result }
}

$smoke = Invoke-Receipt '--smoke' (Join-Path $reports "smoke-$version.json")
if ($smoke.Code -ne 0) { throw "Smoke test failed: $($smoke.Result.failed -join ', ')" }
$timings = ($smoke.Result.timingsMs.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value) ms" }) -join ', '
Write-Host "Smoke: $(@($smoke.Result.checks.PSObject.Properties).Count) checks passed ($timings)" -ForegroundColor Green

# Chassis's QA on the published exe: the self-test, then the real window rendered off-screen and captured by WebView2
# itself, once per look and pop-out tab. Each run is its own process with a throwaway data folder, read-only
# (no tray, hotkey, hooks or rescue), and a page error fails it.
$qa = Join-Path $reports "qa-$version"
if (Test-Path $qa) { Remove-Item $qa -Recurse -Force }
$qaRun = Start-Process $exe -ArgumentList '--qa', "`"$qa`"" -Wait -PassThru
$receipt = Get-Content (Join-Path $qa 'qa.json') -Raw | ConvertFrom-Json
if ($qaRun.ExitCode -ne 0 -or -not $receipt.pass) {
    $failed = ($receipt.steps | Where-Object { $_.code -ne 0 } | ForEach-Object { "$($_.step) (exit $($_.code))" }) -join ', '
    throw "QA failed: $failed. See $qa"
}
$selftest = (Get-Content (Join-Path $qa 'selftest.txt') | Select-Object -Last 1)
Write-Host "QA: $selftest; $(@($receipt.steps).Count - 1) off-screen captures in $qa" -ForegroundColor Green

# What the page said it was showing when each picture was taken.
function Read-Facts([string]$shot) { (Get-Content (Join-Path $qa "$shot.png.json") -Raw | ConvertFrom-Json).facts }
$glass = Read-Facts 'glass'
$solid = Read-Facts 'solid'
if ($glass.versionLabel -ne "v$version") { throw "The version button shows '$($glass.versionLabel)', expected 'v$version'." }
if (-not $glass.gleamLoaded) { throw 'The Gleam orb did not load.' }
if ($glass.mapMonitors -lt 1) { throw 'The desk map drew no monitors.' }
if ((Read-Facts 'health').view -ne 'health') { throw 'The Health capture is not showing the Health view.' }
Write-Host "UI: $($glass.versionLabel) rendered, orb $($glass.orbPreset)/$($glass.orbState), $($glass.mapMonitors) monitors and $($glass.mapWindows) windows on the map" -ForegroundColor Green

# Glass proof on the rendered app: asked for solid it must render solid; left alone it must render glass unless
# Windows itself asks for solid panels. The two captures (glass.png, solid.png) are kept side by side.
if (-not $solid.solidPanels) { throw 'Forced solid still rendered glass panels.' }
$windowsWantsSolid = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction Ignore).EnableTransparency -eq 0
if ($glass.solidPanels -and -not $windowsWantsSolid) { throw 'Glass on rendered solid panels without a Windows reason.' }
Write-Host "Glass: on -> $(if ($glass.solidPanels) { 'solid (Windows transparency effects are off)' } else { 'glass' }), off -> solid" -ForegroundColor Green

if ($LiveMove) {
    $live = Invoke-Receipt '--integration-test' (Join-Path $reports "live-move-$version.json")
    $live.Result.cases | ForEach-Object { Write-Host ("  {0,-28} {1}" -f $_.name, $_.status) }
    if ($live.Code -ne 0) { throw 'Live move test failed.' }
    Write-Host 'Live move: passed' -ForegroundColor Green
}

if ($Screenshots) {
    $shots = Join-Path $reports "screenshots-$version"
    if (Test-Path $shots) { Remove-Item $shots -Recurse -Force }
    New-Item -ItemType Directory -Force $shots | Out-Null
    # Every capture is redacted; -Hide also leaves those processes' windows out.
    $share = 'redact=1&stray=1' + $(if ($Hide) { '&hide=' + [uri]::EscapeDataString($Hide) } else { '' })
    $views = [ordered]@{
        'desk-select'  = @('--query', "select=1&$share")
        'health'       = @('--query', "view=health&$share")
        'settings'     = @('--query', $share, '--panel', 'settings')
        'dependencies' = @('--query', $share, '--panel', 'deps')
    }
    foreach ($view in $views.Keys) {
        $file = Join-Path $shots "screenshepherd-$view.png"
        $shotArgs = @('--snapshot', "`"$file`"", '--data', "`"$(Join-Path $shots 'data')`"") + $views[$view]
        $shotRun = Start-Process $exe -ArgumentList $shotArgs -Wait -PassThru
        if ($shotRun.ExitCode -ne 0) { throw "Screenshot '$view' failed (exit $($shotRun.ExitCode)); see $shots" }
    }
    $docs = Join-Path $PSScriptRoot 'docs\screenshots'
    New-Item -ItemType Directory -Force $docs | Out-Null
    Get-ChildItem $docs -Filter *.png | Remove-Item
    Get-ChildItem $shots -Filter *.png | Copy-Item -Destination $docs -Force
    Write-Host "Screenshots: $((Get-ChildItem $docs -Filter *.png).Count) redacted captures in docs\screenshots (review before committing)" -ForegroundColor Green
}

Copy-Item README.md, CHANGELOG.md, LICENSE, THIRD-PARTY-NOTICES.md -Destination $out
Copy-Item licenses -Destination $out -Recurse
$zip = Join-Path 'dist' "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip

$sums = @()
$sums += "{0}  {1}" -f (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant(), "$name.zip"
$sums += "{0}  {1}" -f (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant(), "$name/ScreenShepherd.exe"
# The exe is the .NET launcher stub; the app's code is in the dll.
$sums += "{0}  {1}" -f (Get-FileHash (Join-Path $out 'ScreenShepherd.dll') -Algorithm SHA256).Hash.ToLowerInvariant(), "$name/ScreenShepherd.dll"
Set-Content (Join-Path 'dist' "SHA256SUMS-$version.txt") $sums

Write-Host "Built $out (exe ProductVersion $exeVersion)" -ForegroundColor Green
Get-Content (Join-Path 'dist' "SHA256SUMS-$version.txt")

