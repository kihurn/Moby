[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet('start', 'camera', 'standby', 'stop', 'status')]
    [string]$Action,
    [string]$AgentPath,
    [string]$ConfigPath,
    [ValidateRange(1, 65535)]
    [int]$Port = 18765
)

$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot
$runtime = Join-Path $project '.moby-spike'
if (!$AgentPath) {
    foreach ($build in 'moby-presence-display-wake-win-x64', 'moby-presence-brightness-win-x64', 'moby-presence-standby-win-x64', 'moby-presence-wake-final-win-x64', 'moby-presence-wake-win-x64', 'moby-on-air-source-win-x64', 'moby-on-air-toggle-win-x64', 'moby-shortcut-alignment-win-x64', 'moby-phone-metrics-win-x64', 'moby-exposure-win-x64', 'moby-presence-mic-win-x64', 'moby-capture-status-win-x64', 'moby-capture-win-x64') {
        $AgentPath = Join-Path $runtime "$build\PhoneBoxAgent.exe"
        if (Test-Path -LiteralPath $AgentPath -PathType Leaf) { break }
    }
}
if (!$ConfigPath) { $ConfigPath = Join-Path $runtime 'baseline-config.json' }
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
$baseUrl = "http://127.0.0.1:$Port"

function Invoke-Moby([string]$Path, [string]$Method = 'Get') {
    Invoke-RestMethod -Uri "$baseUrl/api/$Path" -Method $Method -TimeoutSec 8
}

function Get-Agent {
    try { Invoke-Moby 'info' }
    catch [System.Net.WebException] {
        if ($_.Exception.Status -ne [System.Net.WebExceptionStatus]::ConnectFailure) { throw }
    }
    catch {
        if ($_.Exception.GetBaseException() -isnot [System.Net.Sockets.SocketException] -or
            $_.Exception.GetBaseException().SocketErrorCode -ne [System.Net.Sockets.SocketError]::ConnectionRefused) { throw }
    }
}

function Get-ProfileAgent {
    $current = Get-Agent
    if ($current -and $current.configPath -ine $ConfigPath) {
        throw "Port $Port is serving a different profile: $($current.configPath)"
    }
    return $current
}

function Find-UsbAgent {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $current = Get-ProfileAgent
        if ($current.connected -and $current.via -eq 'USB') { return $current }
        Start-Sleep -Milliseconds 500
    }
}

function Test-AgentMutexReleased {
    $name = if ($Port -eq 8765) { 'Local\PhoneBox.Agent' } else { "Local\PhoneBox.Agent.$Port" }
    try { $mutex = [Threading.Mutex]::OpenExisting($name) }
    catch [Threading.WaitHandleCannotBeOpenedException] { return $true }
    try {
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if ($acquired) { $mutex.ReleaseMutex() }
        return $acquired
    }
    finally { $mutex.Dispose() }
}

function Invoke-AdbProbe([string]$AdbPath, [string]$Command, [int]$TimeoutMs) {
    $process = Start-Process -FilePath $AdbPath -ArgumentList $Command -WindowStyle Hidden -PassThru
    try {
        if ($process.WaitForExit($TimeoutMs)) { return $process.ExitCode -eq 0 }
        if (!$process.HasExited) { $process.Kill() }
        return $false
    }
    finally { $process.Dispose() }
}

function Repair-SharedAdb {
    $profile = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if (!$profile.mobyCameraPath -or !$profile.adbPath) { return $false }
    $bundledAdb = Join-Path (Split-Path $profile.mobyCameraPath) 'resources\adb.exe'
    if ($profile.mobyOnDemandCameraDll -or (Test-Path -LiteralPath (Join-Path (Split-Path $profile.mobyCameraPath) 'moby-exposure.txt') -PathType Leaf)) {
        $bundledAdb = Join-Path $runtime 'plugcam\resources\adb.exe'
    }
    if (!(Test-Path -LiteralPath $bundledAdb -PathType Leaf) -or
        [IO.Path]::GetFullPath($profile.adbPath) -ine [IO.Path]::GetFullPath($bundledAdb)) { return $false }
    if ($env:ADB_SERVER_SOCKET -or $env:ANDROID_ADB_SERVER_ADDRESS -or $env:ANDROID_ADB_SERVER_PORT) { return $false }
    if (Invoke-AdbProbe $bundledAdb 'devices' 4500) { return $false }

    $current = Get-Agent
    if ($current) {
        if ($current.configPath -ine $ConfigPath) { return $false }
        $state = Invoke-Moby 'state'
        if ($state.phoneMic.live -or $state.mobyCall.camera -or $state.mobyCall.state -eq 'starting') { return $false }
    }
    if (Get-Process -Name 'Plugcam' -ErrorAction SilentlyContinue) { return $false }

    $owners = @(Get-NetTCPConnection -LocalPort 5037 -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($owners.Count -gt 1) { return $false }
    if ($owners.Count -eq 1) {
        $server = Get-CimInstance Win32_Process -Filter "ProcessId=$($owners[0])"
        if (!$server -or $server.ExecutablePath -ine $bundledAdb -or $server.CommandLine -notmatch '\bfork-server\s+server\b') { return $false }
        if (Invoke-AdbProbe $bundledAdb 'devices' 4500) { return $true }

        Write-Host 'The shared Moby ADB server stopped responding. Restarting it once.'
        Stop-Process -Id $server.ProcessId
        Wait-Process -Id $server.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
    } else {
        Write-Host 'The shared Moby ADB server is down. Starting it once.'
    }
    if (!(Invoke-AdbProbe $bundledAdb 'start-server' 12000)) { return $false }
    return (Invoke-AdbProbe $bundledAdb 'devices' 6500)
}

function Start-MobyAgent {
    if (!(Test-Path -LiteralPath $AgentPath -PathType Leaf)) { throw "Moby agent not found: $AgentPath" }
    if (!(Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw "Moby profile not found: $ConfigPath" }
    $executable = (Resolve-Path -LiteralPath $AgentPath).Path
    for ($launch = 0; $launch -lt 2; $launch++) {
        $current = Get-ProfileAgent
        if ($current) { return $current }
        $process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru -ArgumentList "--restart --port $Port --config `"$ConfigPath`""
        try {
            # The agent can spend up to 15 seconds waiting for the previous process to release its mutex.
            for ($attempt = 0; $attempt -lt 40; $attempt++) {
                $current = Get-ProfileAgent
                if ($current) { return $current }
                if ($process.HasExited) { break }
                Start-Sleep -Milliseconds 500
            }
            if (!$process.HasExited) { break }
        }
        finally { $process.Dispose() }
    }
    $current = Get-ProfileAgent
    if ($current) { return $current }
    throw "Moby did not start on port $Port. Check the Windows agent log."
}

$agent = Get-ProfileAgent

if ($Action -eq 'status') {
    if (!$agent) {
        if (Test-AgentMutexReleased) { Write-Host 'Moby is stopped.' }
        else { Write-Host "Moby is running but HTTP is unavailable on port $Port." }
        return
    }
    $state = Invoke-Moby 'state'
    [pscustomobject]@{
        Settings = $baseUrl
        USB = $agent.connected -and $agent.via -eq 'USB'
        Capture = $state.mobyCall.state
        Camera = [bool]$state.mobyCall.camera
        Mic = [bool]$state.phoneMic.live
        Muted = [bool]$state.phoneMic.muted
        Message = $state.mobyCall.message
        MicMessage = $state.mobyCall.micMessage
    }
    return
}

if ($Action -eq 'stop') {
    $stopRequested = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($agent -and !$stopRequested) {
            Invoke-Moby 'moby/stop' 'Post' | Out-Null
            $stopRequested = $true
        }
        if (!$agent -and (Test-AgentMutexReleased)) {
            if ($attempt -eq 0) { Write-Host 'Moby is already stopped.' }
            else { Write-Host 'Moby stopped.' }
            return
        }
        Start-Sleep -Milliseconds 400
        $agent = Get-ProfileAgent
    }
    throw "Moby has not finished stopping on port $Port."
}

if ($Action -in 'start', 'standby' -and !(Get-Process -Name 'Virtual Cables' -ErrorAction SilentlyContinue)) {
    $cables = Join-Path $env:LOCALAPPDATA 'Programs\Virtual Cables\Virtual Cables.exe'
    if (Test-Path -LiteralPath $cables -PathType Leaf) { Start-Process -FilePath $cables -WindowStyle Hidden }
}

if (!$agent) { $null = Start-MobyAgent }

$agent = Find-UsbAgent
if (!$agent -and (Repair-SharedAdb)) {
    if (!(Get-ProfileAgent)) { $null = Start-MobyAgent }
    $agent = Find-UsbAgent
}
if (!$agent) {
    if (!(Get-ProfileAgent)) { throw "Moby stopped responding on port $Port. Check the Windows agent log." }
    throw 'Moby could not reach the phone over USB. Check the data cable and unlock the phone to see any USB debugging prompt.'
}

if ($Action -eq 'standby') {
    $capture = Invoke-Moby 'moby/call/stop' 'Post'
    for ($attempt = 0; $attempt -lt 20 -and $capture.state -eq 'stopping'; $attempt++) {
        Start-Sleep -Milliseconds 400
        $capture = (Invoke-Moby 'state').mobyCall
    }
    if ($capture.state -ne 'off') {
        if ($capture.message) { throw $capture.message }
        throw 'Capture is still stopping.'
    }
    $profile = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if ($profile.mobyOnDemandCameraDll) {
        Invoke-Moby 'moby/call/start' 'Post' | Out-Null
        Write-Host 'Moby is on standby. The camera wakes when a Windows app requests video.'
        return
    }
    Write-Host 'Moby is on standby. Start capture from the Call widget or run moby start.'
    return
}

$capturePath = 'moby/call/start'
if ($Action -eq 'camera') { $capturePath += '?cameraOnly=true' }
$capture = Invoke-Moby $capturePath 'Post'
for ($attempt = 0; $attempt -lt 48; $attempt++) {
    if ($capture.state -eq 'error') { throw $capture.message }
    if ($Action -eq 'start' -and $capture.automatic) {
        if ((Invoke-Moby 'phone-mic').mic.state -eq 'live') {
            Write-Host 'Moby mic is live. The camera wakes when a Windows app requests video.'
            return
        }
    }
    if ($capture.camera -and ($Action -eq 'camera' -or $capture.mic)) {
        if ($Action -eq 'camera') { Write-Host 'Moby camera is live.' }
        else { Write-Host 'Moby camera and mic are live.' }
        return
    }
    Start-Sleep -Milliseconds 500
    $capture = (Invoke-Moby 'state').mobyCall
}
if ($Action -eq 'start' -and $capture.camera) {
    $problem = if ($capture.micMessage) { $capture.micMessage } else { 'The mic did not become live.' }
    throw "Moby camera is live. $problem"
}
throw "Capture did not become live. Run status to check the feeds."
