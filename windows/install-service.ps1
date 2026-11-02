#Requires -RunAsAdministrator
# Installs (or updates) the Windows service from the folder this script sits in.
# Usage: copy local.settings.example.json to local.settings.json, fill it in, then run this script.
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$exe = Join-Path $dir 'TerraTeklaServer.exe'
$name = 'TerraTeklaServer'

if (-not (Test-Path $exe)) { throw "TerraTeklaServer.exe not found next to this script." }
$settingsFile = Join-Path $dir 'local.settings.json'
if (-not (Test-Path $settingsFile)) {
    throw "Create local.settings.json first (copy local.settings.example.json and fill in ACCESS_KEY, CONVERSION_URL, CONVERSION_KEY and CLIENT_ROOT)."
}

function Stop-TerraService([string]$Name, [int]$Seconds = 30) {
    # Stop-Service waits forever if the process doesn't exit; ask, wait, then end the process.
    $svc = Get-Service $Name -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -eq 'Stopped') { return }
    Stop-Service $Name -NoWait -ErrorAction SilentlyContinue
    try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($Seconds)) }
    catch {
        $id = (Get-CimInstance Win32_Service -Filter "Name='$Name'").ProcessId
        if ($id) {
            Write-Warning "$Name didn't stop within $Seconds s; ending its process ($id)."
            Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
        }
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }
}

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-TerraService $name
    sc.exe delete $name | Out-Null
    Start-Sleep 2
}
New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'TERRA Server' `
    -Description 'Shared stacks, devices, history and photos for the TERRA iPad app; hosts the app itself' -StartupType Automatic | Out-Null
# Restart automatically after a crash.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

# Let the iPads in: open the port from Urls in the firewall (private/domain networks only).
$url = (Get-Content $settingsFile -Raw | ConvertFrom-Json).Urls
$port = if ($url -match ':(\d+)\s*$') { [int]$Matches[1] } else { 8095 }
if (-not (Get-NetFirewallRule -DisplayName 'TERRA Server' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'TERRA Server' -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Domain,Private | Out-Null
}

Start-Service $name
Start-Sleep 3
$settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
# The admin code in use (the server writes it there whenever it is made up or changed).
$dataDir = if ($settings.DATA_DIR) { $settings.DATA_DIR } else { 'data' }
if (-not [IO.Path]::IsPathRooted($dataDir)) { $dataDir = Join-Path $dir $dataDir }
$codeFile = Join-Path $dataDir 'admin-code.txt'
if (Test-Path $codeFile) { Write-Host "Admin code (also in $codeFile): $((Get-Content $codeFile -TotalCount 1).Trim())" }
Write-Host "Service started. Open http://<this computer>:$port/admin/ in a browser, or check: Invoke-RestMethod http://127.0.0.1:$port/health"
