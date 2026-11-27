[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PlugcamExecutable,
    [Parameter(Mandatory)] [string]$CameraDllX64,
    [Parameter(Mandatory)] [string]$CameraDllX86
)

$ErrorActionPreference = 'Stop'
$checkout = Split-Path (Split-Path $PSScriptRoot)
$runtime = Join-Path (Split-Path $checkout) '.moby-spike'
$original = Join-Path $runtime 'plugcam'
$prototype = Join-Path $runtime 'plugcam-on-demand'
$resources = Join-Path $prototype 'resources'
$x86 = Join-Path $resources 'x86'
$profilePath = Join-Path $runtime 'moby-on-demand-config.json'

$CameraDllX64 = (Resolve-Path -LiteralPath $CameraDllX64).Path
$CameraDllX86 = (Resolve-Path -LiteralPath $CameraDllX86).Path
$PlugcamExecutable = (Resolve-Path -LiteralPath $PlugcamExecutable).Path
if (Test-Path -LiteralPath $profilePath) {
    throw "The prototype profile already exists at $profilePath."
}

$profile = Get-Content -LiteralPath (Join-Path $runtime 'baseline-config.json') -Raw | ConvertFrom-Json
$sharedAdb = (Resolve-Path -LiteralPath (Join-Path $original 'resources\adb.exe')).Path
if ($profile.adbPath -ine $sharedAdb) { throw 'The saved profile is not using the expected shared ADB server.' }

New-Item -ItemType Directory -Path $x86 -Force | Out-Null
Copy-Item -LiteralPath $PlugcamExecutable -Destination (Join-Path $prototype 'Plugcam.exe')
foreach ($file in 'portable.txt', 'LICENSE.txt', 'THIRD_PARTY_NOTICES.md', 'licenses') {
    Copy-Item -LiteralPath (Join-Path $original $file) -Destination $prototype -Recurse
}
foreach ($file in 'scrcpy-server', 'scrcpy-server.sha256') {
    Copy-Item -LiteralPath (Join-Path $original "resources\$file") -Destination $resources
}
Copy-Item -LiteralPath $CameraDllX64 -Destination (Join-Path $resources 'plugcam_cam.dll')
Copy-Item -LiteralPath $CameraDllX86 -Destination (Join-Path $x86 'plugcam_cam.dll')

$profile | Add-Member -NotePropertyName mobyCameraPath -NotePropertyValue (Join-Path $prototype 'Plugcam.exe') -Force
$profile | Add-Member -NotePropertyName mobyOnDemandCameraDll -NotePropertyValue (Join-Path $resources 'plugcam_cam.dll') -Force
$profile.phoneMic.enabled = $true
$profile | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $profilePath -Encoding UTF8
Write-Output $profilePath
