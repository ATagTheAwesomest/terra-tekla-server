#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Moves the TERRA setup from "conversion layer on the internet + app on GitHub Pages" to
  terra-tekla-server. Run once on the server, elevated, from a clone of terra-tekla-server.

.DESCRIPTION
  Before: Cloudflare Tunnel -> tekla-conversion-layer (port 8090), iPad app from GitHub Pages,
          photos saved by the conversion layer (FILES_*), stacks kept only on each iPad.
  After:  Cloudflare Tunnel -> terra-tekla-server (port 8095), which serves the iPad app at /,
          the admin board at /admin/, live sync at /ws, photos (PHOTOS_*), and passes PowerFab
          calls on to the conversion layer on 127.0.0.1 only.

  The conversion layer is NOT removed: the new server needs it to reach PowerFab.

  What it does, asking before every change (-Yes answers yes to all of them):
    1. Checks: elevated, git, .NET SDK, the conversion layer's service and settings.
    2. Backs up every file it may change into C:\TerraMigrationBackup\<time>, plus a rollback script.
    3. Gets the code: this repo (SERVER_REPO) and the iPad app (CLIENT_ROOT, git clone).
    4. Writes the new server's local.settings.json: CONVERSION_KEY = the conversion layer's
       API_TOKEN, photo settings carried over from FILES_*, a new ACCESS_KEY for the iPads.
       An existing file is kept; only empty keys are filled in. file-rules.json -> photo-rules.json.
    5. Installs the TerraTeklaServer service the first time; after that runs deploy.ps1.
    6. Makes the conversion layer listen on 127.0.0.1 only (if it doesn't already).
    7. Repoints a locally managed Cloudflare Tunnel (config.yml) from :8090 to :8095. A tunnel
       managed in the Cloudflare dashboard is changed there: the script says exactly what to click.
    8. Checks it all: /health, the app, the admin board, PowerFab through the new server, and (with
       -PublicHostname) the same plus the live connection over the public address.

  Run it again at any time: every step checks first and skips what is already done.
  After this, updates are deploy.ps1 / the admin board's Updates, as usual.

.EXAMPLE
  cd C:\src\terra-tekla-server
  git pull
  .\windows\migrate-from-conversion-layer.ps1 -PublicHostname terra.example.com
#>
param(
    # The address the iPads will use, e.g. terra.example.com (no https://). Only used for checks and
    # instructions; never written into a repo.
    [string]$PublicHostname = '',
    [string]$InstallDir = 'C:\TerraTeklaServer',
    [string]$ServiceName = 'TerraTeklaServer',
    [string]$ClientRoot = 'C:\TerraTeklaClient',
    [string]$ClientRepoUrl = 'https://github.com/ATagTheAwesomest/terra-tekla-client',
    [int]$Port = 8095,
    [string]$OldInstallDir = 'C:\TeklaConversionLayer',
    [string]$OldServiceName = 'TeklaConversionLayer',
    [string]$BackupRoot = 'C:\TerraMigrationBackup',
    # Answer yes to every question (still prints everything it does).
    [switch]$Yes,
    # Leave the Cloudflare Tunnel alone (do that part by hand).
    [switch]$SkipTunnel
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $BackupRoot $stamp
$todo = New-Object System.Collections.Generic.List[string]
$utf8 = New-Object System.Text.UTF8Encoding $false

function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Info($text) { Write-Host "   $text" }
function Good($text) { Write-Host "   OK  $text" -ForegroundColor Green }
function Warn($text) { Write-Host "   !!  $text" -ForegroundColor Yellow }
function Todo($text) { $todo.Add($text); Write-Host "   TODO $text" -ForegroundColor Yellow }
function Ask($question) {
    if ($Yes) { Write-Host "   $question [y/N] y (-Yes)"; return $true }
    $answer = Read-Host "   $question [y/N]"
    return $answer -match '^(y|yes)$'
}
# Written as it goes, so a run that stops half way can still be undone.
function Add-Rollback($line) {
    $file = Join-Path $backup 'rollback.ps1'
    if (-not (Test-Path $file)) {
        [IO.File]::WriteAllLines($file, [string[]]@('#Requires -RunAsAdministrator', '# Puts back the files the migration changed, then restarts what it restarted.'), $utf8)
    }
    [IO.File]::AppendAllText($file, $line + "`r`n", $utf8)
}
function Backup-File($path) {
    if (-not (Test-Path $path)) { return }
    $name = ($path -replace '[:\\/]+', '_').Trim('_')
    $target = Join-Path $backup $name
    Copy-Item $path $target -Force
    Add-Rollback "Copy-Item -LiteralPath '$target' -Destination '$path' -Force"
    Info "backed up $path"
}
function Read-Json($path) { Get-Content $path -Raw | ConvertFrom-Json }
function Write-Json($path, $object) { [IO.File]::WriteAllText($path, ($object | ConvertTo-Json -Depth 10), $utf8) }
function Get-Prop($object, $name) {
    if ($null -eq $object) { return $null }
    $p = $object.PSObject.Properties[$name]
    if ($p) { return $p.Value } else { return $null }
}
function Set-Prop($object, $name, $value) {
    if ($object.PSObject.Properties[$name]) { $object.$name = $value }
    else { $object | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}
function New-Key {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return [Convert]::ToBase64String($bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')
}
# Same as the app's encodeSetup(): "TERRACFG:" + base64url of {"u": url, "k": key}. A blank url means
# "the server this app was opened from", which is right for iPads that open the server's address.
function New-SetupCode($key) {
    $json = '{"u":"","k":' + (ConvertTo-Json $key) + '}'
    $b64 = [Convert]::ToBase64String($utf8.GetBytes($json)).Replace('+', '-').Replace('/', '_').TrimEnd('=')
    return "TERRACFG:$b64"
}
function Wait-Health($url, $seconds) {
    $until = (Get-Date).AddSeconds($seconds)
    do {
        try { return Invoke-RestMethod "$url/health" -TimeoutSec 5 } catch { Start-Sleep 2 }
    } while ((Get-Date) -lt $until)
    return $null
}

Write-Host 'TERRA migration: conversion layer on the internet -> terra-tekla-server' -ForegroundColor Cyan
Write-Host "Backups go to $backup. Nothing is deleted."

# ---- 1. Checks -----------------------------------------------------------------------------------
Step '1. Checks'
$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git -and (Test-Path 'C:\Program Files\Git\cmd\git.exe')) { $git = 'C:\Program Files\Git\cmd\git.exe' }
if (-not $git) { throw 'git is not installed (https://git-scm.com/download/win). Install it and run this again.' }
Good "git: $git"

$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks -match '^(1\d|[2-9]\d)\.')) { throw '.NET 10 SDK not found (dotnet --list-sdks). Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.' }
Good ".NET SDK: $(($sdks | Select-Object -Last 1))"

if (-not (Test-Path (Join-Path $repo 'src\TerraTeklaServer'))) { throw "Run this from a clone of terra-tekla-server (expected $repo\src\TerraTeklaServer)." }
Good "terra-tekla-server repo: $repo"

$oldService = Get-Service $OldServiceName -ErrorAction SilentlyContinue
$oldSettingsFile = Join-Path $OldInstallDir 'local.settings.json'
if (-not (Test-Path $oldSettingsFile)) {
    throw "Can't find the conversion layer's settings at $oldSettingsFile. Pass -OldInstallDir <folder with TeklaConversionLayer.exe>."
}
$old = Read-Json $oldSettingsFile
$oldUrls = [string](Get-Prop $old 'Urls')
$oldPort = 8090
if ($oldUrls -match ':(\d+)\s*/?\s*$') { $oldPort = [int]$Matches[1] }
$apiToken = [string](Get-Prop $old 'API_TOKEN')
if (-not $oldService) { Warn "No '$OldServiceName' service. The new server still needs the conversion layer running to reach PowerFab." }
else { Good "conversion layer service: $($oldService.Status), Urls $oldUrls" }
if (-not $apiToken) { Warn 'The conversion layer has no API_TOKEN. CONVERSION_KEY will be left empty; set both.' }
try { $null = Invoke-RestMethod "http://127.0.0.1:$oldPort/health" -TimeoutSec 5; Good "conversion layer answers on 127.0.0.1:$oldPort" }
catch { Warn "conversion layer doesn't answer on http://127.0.0.1:$oldPort/health ($($_.Exception.Message))" }

$newService = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($newService) { Info "TerraTeklaServer is already installed ($($newService.Status)): this run updates it." }
if ($oldPort -eq $Port) { throw "The conversion layer and the new server would both use port $Port. Pass -Port <another>." }

if (-not (Ask 'Go ahead? Every change below asks again.')) { Write-Host 'Stopped. Nothing changed.'; exit 0 }
New-Item -ItemType Directory -Force $backup | Out-Null

# ---- 2. Backups ----------------------------------------------------------------------------------
Step '2. Backups'
Backup-File $oldSettingsFile
Backup-File (Join-Path $OldInstallDir 'file-rules.json')
Backup-File (Join-Path $InstallDir 'local.settings.json')
Backup-File (Join-Path $InstallDir 'photo-rules.json')
$db = Join-Path $InstallDir 'data\terra.db'
if (Test-Path $db) {
    # Copy the database with the service stopped so the copy is whole; deploy starts it again.
    if ($newService -and $newService.Status -eq 'Running') {
        Stop-Service $ServiceName
        (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Get-ChildItem (Join-Path $InstallDir 'data') -Filter 'terra.db*' | ForEach-Object { Copy-Item $_.FullName $backup -Force }
    Info "backed up the database ($db)"
    if ($newService) { Start-Service $ServiceName }
}

# ---- 3. Code ------------------------------------------------------------------------------------
Step '3. Code'
& $git -C $repo pull --ff-only
if ($LASTEXITCODE -ne 0) { Warn 'git pull of terra-tekla-server failed; using the code as it is.' }
Info "terra-tekla-server: $(& $git -C $repo log -1 --format='%h %s')"

if (Test-Path (Join-Path $ClientRoot 'index.html')) {
    Good "iPad app already at $ClientRoot (deploy keeps it up to date)"
} elseif (Test-Path $ClientRoot) {
    Warn "$ClientRoot exists but has no index.html. Fix or empty it, or pass -ClientRoot <folder>."
} elseif (Ask "Clone the iPad app ($ClientRepoUrl) to ${ClientRoot}?") {
    & $git clone $ClientRepoUrl $ClientRoot
    if ($LASTEXITCODE -ne 0) { throw "git clone of the iPad app failed (private repo? sign in to git first, or clone it by hand to $ClientRoot)." }
    Good "iPad app: $(& $git -C $ClientRoot log -1 --format='%h %s')"
} else { Todo "Put a clone of terra-tekla-client in $ClientRoot (CLIENT_ROOT)." }

# ---- 4. The new server's settings ----------------------------------------------------------------
Step '4. Settings'
New-Item -ItemType Directory -Force $InstallDir | Out-Null
$settingsFile = Join-Path $InstallDir 'local.settings.json'
if (Test-Path $settingsFile) { $s = Read-Json $settingsFile; Info "keeping $settingsFile, filling in only empty keys" }
else { $s = Read-Json (Join-Path $repo 'windows\local.settings.example.json'); Info "new $settingsFile from the example" }

# What each key should be, from the conversion layer's setup.
$wanted = [ordered]@{
    'Urls'           = "http://0.0.0.0:$Port"
    'CONVERSION_URL' = "http://127.0.0.1:$oldPort"
    'CONVERSION_KEY' = $apiToken
    'CLIENT_ROOT'    = $ClientRoot
    'DATA_DIR'       = 'data'
    'SERVER_REPO'    = $repo
}
# The conversion layer's old photo settings (FILES_*), if it still has them.
$photoMap = [ordered]@{ 'FILES_ENABLED' = 'PHOTOS_ENABLED'; 'FILES_ROOT' = 'PHOTOS_ROOT'; 'FILES_MAX_MB' = 'PHOTOS_MAX_MB'; 'FILES_MAX_PER_UPLOAD' = 'PHOTOS_MAX_PER_UPLOAD'; 'FILES_TYPES' = 'PHOTOS_TYPES' }
$hadPhotos = $false
foreach ($k in $photoMap.Keys) {
    $v = Get-Prop $old $k
    if ($null -ne $v -and "$v" -ne '') { $wanted[$photoMap[$k]] = "$v"; $hadPhotos = $true }
}
$fresh = -not (Test-Path $settingsFile)
$changes = @()
foreach ($k in $wanted.Keys) {
    $current = [string](Get-Prop $s $k)
    $value = [string]$wanted[$k]
    if ($value -eq '' -or $current -eq $value) { continue }
    # A fresh file takes everything; an existing one only gets keys that are empty.
    if ($fresh -or $current -eq '') {
        $changes += [pscustomobject]@{ Key = $k; From = $current; To = $value }
    } elseif ($k -like '*KEY*') { Warn "$k is already set and differs from the conversion layer's API_TOKEN; kept. (The PowerFab check below shows if it is wrong.)" }
    else { Info "$k stays '$current' (the conversion layer suggests '$value')" }
}
$newKey = $null
if (-not [string](Get-Prop $s 'ACCESS_KEY')) {
    $newKey = New-Key
    $changes += [pscustomobject]@{ Key = 'ACCESS_KEY'; From = ''; To = '(new random key, shown at the end)' }
}
if ($changes.Count -eq 0) { Good 'settings already complete' }
else {
    $changes | ForEach-Object { Info ("{0,-22} {1}" -f $_.Key, $(if ($_.Key -like '*KEY*' -and $_.Key -ne 'ACCESS_KEY') { '(the conversion layer''s API_TOKEN)' } else { $_.To })) }
    if (Ask "Write these to ${settingsFile}?") {
        foreach ($c in $changes) { if ($c.Key -ne 'ACCESS_KEY') { Set-Prop $s $c.Key $c.To } }
        if ($newKey) { Set-Prop $s 'ACCESS_KEY' $newKey }
        Write-Json $settingsFile $s
        Good 'settings written'
    } else { Todo "Fill in $settingsFile by hand (see SETUP.md)." ; $newKey = $null }
}

$oldRules = Join-Path $OldInstallDir 'file-rules.json'
$newRules = Join-Path $InstallDir 'photo-rules.json'
if ((Test-Path $oldRules) -and -not (Test-Path $newRules)) {
    if (Ask "Copy the conversion layer's photo folder rules ($oldRules) to ${newRules}? (Same format.)") {
        Copy-Item $oldRules $newRules
        Add-Rollback "Remove-Item -LiteralPath '$newRules'"
        Good 'photo rules copied'
    }
} elseif (Test-Path $newRules) { Good 'photo-rules.json already there' }
if ($hadPhotos) { Info 'Photos keep going to the same folders, so the photos already taken show in the app and admin board.' }

# ---- 5. Install or update the service ------------------------------------------------------------
Step '5. TerraTeklaServer service'
$newService = Get-Service $ServiceName -ErrorAction SilentlyContinue
if (-not $newService) {
    if (Ask "Build and install the TerraTeklaServer service in $InstallDir (port $Port)?") {
        dotnet publish (Join-Path $repo 'src\TerraTeklaServer') -c Release -o $InstallDir
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed. Nothing is installed; the old setup is untouched.' }
        Copy-Item (Join-Path $repo 'windows\install-service.ps1') $InstallDir -Force
        & (Join-Path $InstallDir 'install-service.ps1')
        Add-Rollback "Stop-Service '$ServiceName' -ErrorAction SilentlyContinue  # the new server stays installed, only stopped"
    } else { Todo 'Install the service: see SETUP.md, "Installing on Windows".' }
} elseif (Ask 'Update it now with windows\deploy.ps1 (pull, build, restart)?') {
    & (Join-Path $repo 'windows\deploy.ps1') -InstallDir $InstallDir -ServiceName $ServiceName -SkipPull
}
$health = Wait-Health "http://127.0.0.1:$Port" 30
if ($health) { Good "new server: $($health | ConvertTo-Json -Compress)" }
else { Warn "http://127.0.0.1:$Port/health doesn't answer. Event Viewer > Windows Logs > Application, source TerraTeklaServer." }

# ---- 6. Conversion layer: local only -------------------------------------------------------------
Step '6. Conversion layer: only this computer'
if ($oldUrls -match '://(127\.0\.0\.1|localhost|\[::1\])[:/]') {
    Good "already local only ($oldUrls)"
} elseif (Ask "It listens on '$oldUrls'. Change to http://127.0.0.1:$oldPort so only this server can reach it, and restart it?") {
    Set-Prop $old 'Urls' "http://127.0.0.1:$oldPort"
    Write-Json $oldSettingsFile $old
    if ($oldService) { Restart-Service $OldServiceName; Add-Rollback "Restart-Service '$OldServiceName'" }
    Good 'conversion layer now local only'
}
if ((Get-Prop $old 'FILES_ENABLED') -or (Get-Prop $old 'FILES_ROOT')) { Info 'Its FILES_* settings are ignored now (photos moved); you can delete those lines from its local.settings.json.' }

# ---- 7. Cloudflare Tunnel ------------------------------------------------------------------------
Step '7. Cloudflare Tunnel'
$tunnelDone = $false
if ($SkipTunnel) { Info 'skipped (-SkipTunnel)' }
else {
    $cf = Get-CimInstance Win32_Service -Filter "Name='Cloudflared'" -ErrorAction SilentlyContinue
    if (-not $cf) { $cf = Get-CimInstance Win32_Service | Where-Object { $_.PathName -match 'cloudflared' } | Select-Object -First 1 }
    if (-not $cf) {
        Warn 'No cloudflared service on this computer.'
        Todo "Point the tunnel's public hostname at http://localhost:$Port (MIGRATION.md, 'Cloudflare')."
    } elseif ($cf.PathName -match '--token') {
        # Never print PathName: it holds the tunnel token.
        Info "cloudflared ($($cf.Name)) runs a tunnel managed in the Cloudflare dashboard. Change it there:"
        Info '  Cloudflare One (one.dash.cloudflare.com) > Networks > Tunnels > your tunnel > Edit'
        Info '  > Published application routes (older: Public Hostname) > the hostname that points at'
        Info "    http://localhost:$oldPort > Edit > Service: HTTP, URL: localhost:$Port > Save."
        Info '  It takes effect in seconds; no restart here.'
        Todo "In the Cloudflare dashboard, repoint the hostname from localhost:$oldPort to localhost:$Port."
    } else {
        $candidates = @()
        if ($cf.PathName -match '--config[ =]+"?([^"]+?\.ya?ml)"?(\s|$)') { $candidates += $Matches[1] }
        $candidates += 'C:\Windows\System32\config\systemprofile\.cloudflared\config.yml',
                       'C:\Windows\System32\config\systemprofile\.cloudflared\config.yaml',
                       (Join-Path $env:USERPROFILE '.cloudflared\config.yml'),
                       (Join-Path $env:USERPROFILE '.cloudflared\config.yaml'),
                       'C:\ProgramData\cloudflared\config.yml'
        $config = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $config) {
            Warn 'cloudflared runs, but its config.yml was not found.'
            Todo "Point the tunnel's hostname at http://localhost:$Port (MIGRATION.md, 'Cloudflare')."
        } else {
            Info "tunnel config: $config"
            $lines = @(Get-Content $config)
            $pattern = "^(\s*-?\s*service:\s*)https?://(localhost|127\.0\.0\.1|\[::1\]|0\.0\.0\.0):$oldPort/?\s*$"
            $hits = @()
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match $pattern) {
                    $hostLine = ($lines[0..$i] | Where-Object { $_ -match 'hostname:' } | Select-Object -Last 1)
                    $hits += $i
                    Info ("  line {0}: {1}   ({2})" -f ($i + 1), $lines[$i].Trim(), $(if ($hostLine) { $hostLine.Trim() } else { 'no hostname' }))
                }
            }
            if (Select-String -Path $config -Pattern ":$Port\b" -Quiet) { Good "something in it already points at :$Port" }
            if ($hits.Count -eq 0) {
                Info "nothing points at :$oldPort."
                if (-not (Select-String -Path $config -Pattern ":$Port\b" -Quiet)) { Todo "Add/point a hostname to http://localhost:$Port in $config." }
                else { $tunnelDone = $true }
            } elseif (Ask "Point those line(s) at http://localhost:$Port and restart cloudflared?") {
                Backup-File $config
                foreach ($i in $hits) { $lines[$i] = $lines[$i] -replace $pattern, "`${1}http://localhost:$Port" }
                [IO.File]::WriteAllLines($config, [string[]]$lines, $utf8)
                $cfExe = if ($cf.PathName -match '^"?([^"]+cloudflared\.exe)') { $Matches[1] } else { 'cloudflared' }
                & $cfExe tunnel --config $config ingress validate
                if ($LASTEXITCODE -ne 0) {
                    Copy-Item (Join-Path $backup (($config -replace '[:\\/]+', '_').Trim('_'))) $config -Force
                    Warn 'cloudflared says the new config is invalid; the old one is back. Nothing changed.'
                } else {
                    Restart-Service $cf.Name
                    Add-Rollback "Restart-Service '$($cf.Name)'"
                    Good "tunnel now goes to :$Port"
                    $tunnelDone = $true
                }
            }
        }
    }
}

# ---- 8. Checks -----------------------------------------------------------------------------------
Step '8. Checks'
$s = Read-Json $settingsFile
$key = [string](Get-Prop $s 'ACCESS_KEY')
$headers = @{ Authorization = "Bearer $key" }
function Check-Page($url, $label) {
    try {
        $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 15
        # Cloudflare Access answers its own sign-in page instead of ours: the iPads would get that too.
        if ($r.Content -match 'cloudflareaccess\.com|CF_Authorization') { Warn "$label Cloudflare Access sign-in page instead of the server (MIGRATION.md, 'Cloudflare Access')" }
        else { Good $label }
    } catch { Warn "${label}: $($_.Exception.Message)" }
}
function Check-Base($base, $label) {
    Check-Page "$base/" "$label iPad app (/)"
    Check-Page "$base/admin/" "$label admin board (/admin/)"
    try {
        $st = Invoke-RestMethod "$base/api/status" -Headers $headers -TimeoutSec 15
        if ($st -is [string]) { Warn "$label /api/status answered a web page, not the server (Cloudflare Access?)" }
        elseif ($st.conversion.ok) { Good "$label PowerFab bridge ok, app version $($st.client.version)" } else { Warn "$label PowerFab bridge: $($st.conversion.error)" }
    } catch { Warn "$label /api/status: $($_.Exception.Message)" }
}
Check-Base "http://127.0.0.1:$Port" 'local:'
try {
    # A read-only PowerFab call through the new server proves CONVERSION_KEY is right.
    $null = Invoke-RestMethod "http://127.0.0.1:$Port/api/powerfab/GetProductionControlJobs" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}' -TimeoutSec 60
    Good 'local: PowerFab answers through the new server (CONVERSION_KEY ok)'
} catch { Warn "local: PowerFab through the new server: $($_.Exception.Message)" }

if ($PublicHostname) {
    $publicHost = $PublicHostname -replace '^https?://', '' -replace '/.*$', ''
    Check-Base "https://$publicHost" 'public:'
    try {
        $ws = New-Object System.Net.WebSockets.ClientWebSocket
        $ws.ConnectAsync([Uri]"wss://$publicHost/ws", [Threading.CancellationToken]::None).Wait(15000) | Out-Null
        $hello = $utf8.GetBytes('{"type":"hello","kind":"admin","key":' + (ConvertTo-Json $key) + '}')
        $ws.SendAsync((New-Object 'ArraySegment[byte]' (,$hello)), 'Text', $true, [Threading.CancellationToken]::None).Wait(5000) | Out-Null
        $buffer = New-Object byte[] 65536
        $got = $ws.ReceiveAsync((New-Object 'ArraySegment[byte]' (,$buffer)), [Threading.CancellationToken]::None)
        if ($got.Wait(10000) -and $utf8.GetString($buffer, 0, $got.Result.Count) -match '"welcome"') { Good 'public: live sync (/ws) connects' }
        else { Warn 'public: live sync (/ws) connected but got no welcome (Cloudflare WebSockets off, or Access in the way?)' }
        $ws.Dispose()
    } catch { Warn "public: live sync (/ws): $($_.Exception.InnerException.Message) $($_.Exception.Message)" }
} else { Info 'Pass -PublicHostname terra.example.com to check the public address too.' }

# ---- Done -----------------------------------------------------------------------------------------
Step 'Done'
Info "Backups and rollback script: $backup (run rollback.ps1 there to undo)."
if ($newKey) {
    Write-Host ''
    Write-Host '   New ACCESS_KEY for the iPads and admin board (also in local.settings.json):' -ForegroundColor Cyan
    Write-Host "     $newKey"
    Write-Host '   Setup code (type or scan it into Server settings on each iPad; address left blank = this server):' -ForegroundColor Cyan
    Write-Host "     $(New-SetupCode $newKey)"
    Write-Host '   Keep both out of photos, chats and GitHub.'
}
Write-Host ''
Write-Host '   Then, on each iPad:' -ForegroundColor Cyan
$where = if ($PublicHostname) { "https://$($PublicHostname -replace '^https?://', '' -replace '/.*$', '')/" } else { 'https://<your hostname>/' }
Info "1. Safari > $where > Server settings / Admin > leave Server address blank, enter the key (or the setup code) > Save."
Info '2. Set its name and colour there. 3. Share > Add to Home Screen. 4. Delete the old GitHub Pages TERRA icon.'
Info "Admin board: $($where)admin/ (Add to Home Screen / Install app the same way). First visit sets the admin code."
if ($todo.Count) {
    Write-Host ''
    Write-Host '   Still to do by hand:' -ForegroundColor Yellow
    $todo | ForEach-Object { Write-Host "   - $_" -ForegroundColor Yellow }
}
Write-Host ''
Info 'From now on: windows\deploy.ps1 (or the admin board > Settings > Updates) keeps the server and the app up to date.'
