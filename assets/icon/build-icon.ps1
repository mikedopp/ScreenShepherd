# Rebuilds ScreenShepherd.ico and wwwroot\icon.svg from the two SVG masters in this folder.
#   screenshepherd.svg        32 px and up (taskbar, Alt+Tab, file icon, header)
#   screenshepherd-small.svg  16-24 px (tray, small taskbar), flat and heavier so it stays crisp
# Needs Microsoft Edge (headless render) and ffmpeg (Lanczos downscale) on PATH or in the usual place.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$root = Resolve-Path '..\..'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found.' }
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
$work = Join-Path $env:TEMP 'ScreenShepherd-icon'
New-Item -ItemType Directory -Force $work | Out-Null

function Render([string]$svg, [string]$png) {
    $page = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($png) + '.html')
    $markup = Get-Content $svg -Raw
    Set-Content $page "<!doctype html><html><body style='margin:0;background:transparent'>$markup</body></html>" -Encoding utf8
    $args = '--headless=new', '--hide-scrollbars', '--force-device-scale-factor=1', '--default-background-color=00000000',
            "--user-data-dir=$work\edge", '--window-size=256,256', "--screenshot=$png", ([Uri]$page).AbsoluteUri
    $p = Start-Process $edge -ArgumentList $args -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(30000)) { $p.Kill(); throw "Render timed out: $svg" }
    if (-not (Test-Path $png)) { throw "Render failed: $svg" }
}

function Scale([string]$source, [int]$size, [string]$out) {
    & $ffmpeg -v error -y -i $source -vf "scale=${size}:${size}:flags=lanczos" -pix_fmt rgba $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed at $size px" }
}

Render (Resolve-Path 'screenshepherd.svg') "$work\large-256.png"
Render (Resolve-Path 'screenshepherd-small.svg') "$work\small-256.png"

$entries = @()
foreach ($size in 256, 128, 64, 48, 40, 32, 24, 20, 16) {
    $source = if ($size -le 24) { "$work\small-256.png" } else { "$work\large-256.png" }
    $png = "$work\icon-$size.png"
    if ($size -eq 256) { Copy-Item $source $png -Force } else { Scale $source $size $png }
    $entries += [pscustomobject]@{ Size = $size; Bytes = [IO.File]::ReadAllBytes($png) }
}

# ICO with PNG-compressed entries (supported by Windows Vista and later at every size).
$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($entry in $entries) {
    $dimension = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$entry.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $entry.Bytes.Length
}
foreach ($entry in $entries) { $writer.Write($entry.Bytes) }
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $root 'ScreenShepherd.ico'), $stream.ToArray())
Copy-Item 'screenshepherd.svg' (Join-Path $root 'wwwroot\icon.svg') -Force
Write-Host ("ScreenShepherd.ico: {0} sizes, {1:N0} bytes; size previews in {2}" -f $entries.Count, $stream.Length, $work)
