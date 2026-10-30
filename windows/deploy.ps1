#Requires -RunAsAdministrator
# Pulls the latest code, builds it, and deploys it to the installed service. Also pulls the iPad app
# (CLIENT_ROOT, a git clone of terra-tekla-client) so both update together.
# Usage (elevated PowerShell, from the repo root):  .\windows\deploy.ps1
# local.settings.json, photo-rules.json and the data folder in the install folder are never touched.
param(
    [string]$InstallDir = 'C:\TerraTeklaServer',
    [string]$ServiceName = 'TerraTeklaServer',
    [switch]$SkipPull,
    [switch]$SkipClient
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\TerraTeklaServer'
$publishDir = Join-Path $repo 'bin\publish'
$settingsFile = Join-Path $InstallDir 'local.settings.json'

if (-not (Test-Path $settingsFile)) { throw "$settingsFile not found. Copy local.settings.example.json there and fill it in." }
$settings = Get-Content $settingsFile -Raw | ConvertFrom-Json

# Find git even when it isn't on PATH (Git for Windows default install location).
$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git -and (Test-Path 'C:\Program Files\Git\cmd\git.exe')) { $git = 'C:\Program Files\Git\cmd\git.exe' }

# 0. Pull the latest code. Fast-forward only, so a diverged branch stops here instead of merging silently.
if ($git -and -not $SkipPull) {
    & $git -C $repo pull --ff-only
    if ($LASTEXITCODE -ne 0) { throw 'git pull failed (local changes or diverged branch). Nothing was deployed.' }
}

# 0b. The iPad app: a git clone at CLIENT_ROOT. It is served straight from there, so a pull is all it needs;
# the iPads pick it up the next time the app is opened.
$client = $settings.CLIENT_ROOT
if ($git -and -not $SkipPull -and -not $SkipClient -and $client -and (Test-Path (Join-Path $client '.git'))) {
    & $git -C $client pull --ff-only
    if ($LASTEXITCODE -ne 0) { Write-Warning "git pull of the iPad app in $client failed; it keeps the version it had." }
    else { Write-Host "iPad app: $(& $git -C $client log -1 --format='%h %s')" }
}

$commit = 'unknown commit'
$dirty = ''
if ($git) {
    $commit = & $git -C $repo rev-parse --short HEAD
    $dirty = & $git -C $repo status --porcelain
}
Write-Host "Building $commit$(if ($dirty) { ' (uncommitted changes present)' })"

# 1. Build and publish from the repo.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed; service left running on the previous build." }

# 2. Stop the service so its files can be replaced.
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

# 3. Copy the build over the install folder, leaving the settings, rules and database alone.
robocopy $publishDir $InstallDir /E /XF local.settings.json photo-rules.json /XD data /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

# 4. Start the service and check health on the port from local.settings.json.
Start-Service $ServiceName
Start-Sleep 3
$url = $settings.Urls -replace '0\.0\.0\.0|\*|\+', '127.0.0.1'
try {
    $health = Invoke-RestMethod "$url/health"
    Write-Host "Deployed $commit. Health: $($health | ConvertTo-Json -Compress)"
} catch {
    throw "Deployed $commit, but $url/health did not respond: $($_.Exception.Message)"
}
exit 0
