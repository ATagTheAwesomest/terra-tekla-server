# Gets the latest iPad app (terra-tekla-client) into the folder the server serves it from
# (CLIENT_ROOT). No restart needed: the iPads pick it up the next time the app is opened or
# Update is pressed.
#
# deploy.ps1 runs this after updating the server, and the server runs it when Update iPad app is
# pressed on the admin page or in the app's Server settings / Admin (it ships next to the exe).
#
# Usage:  .\update-client.ps1                      (CLIENT_ROOT from local.settings.json next to it)
#         .\update-client.ps1 -ClientRoot C:\TerraTeklaClient
#
# A git clone is pulled (fast-forward only; first time: cloned). Without git, the branch is
# downloaded from GitHub as a zip and copied over the folder.
param(
    [string]$ClientRoot,
    [string]$RepoUrl = 'https://github.com/ATagTheAwesomest/terra-tekla-client',
    [string]$Branch = 'main'
)
$ErrorActionPreference = 'Stop'

if (-not $ClientRoot) {
    $settingsFile = Join-Path $PSScriptRoot 'local.settings.json'
    if (Test-Path $settingsFile) { $ClientRoot = (Get-Content $settingsFile -Raw | ConvertFrom-Json).CLIENT_ROOT }
}
if (-not $ClientRoot) { throw 'No -ClientRoot given and no CLIENT_ROOT in local.settings.json.' }
if (-not [System.IO.Path]::IsPathRooted($ClientRoot)) { $ClientRoot = Join-Path $PSScriptRoot $ClientRoot }

function Get-AppVersion([string]$dir) {
    $file = Join-Path $dir 'version.js'
    if (-not (Test-Path $file)) { return $null }
    if ((Get-Content $file -Raw) -match "TERRA_VERSION\s*=\s*['""]([^'""]+)") { return $Matches[1] }
    return $null
}

$before = Get-AppVersion $ClientRoot
Write-Host "iPad app folder: $ClientRoot (now $(if ($before) { "v$before" } else { 'empty' }))"

# Find git even when it isn't on PATH (Git for Windows default install location).
$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git -and (Test-Path 'C:\Program Files\Git\cmd\git.exe')) { $git = 'C:\Program Files\Git\cmd\git.exe' }
# The service runs as another Windows user than the one who cloned the folder; git refuses such a
# folder ("dubious ownership") unless told it is fine.
$safe = @('-c', 'safe.directory=*')

$isClone = Test-Path (Join-Path $ClientRoot '.git')
$isEmpty = -not (Test-Path $ClientRoot) -or -not (Get-ChildItem $ClientRoot -Force)

if ($git -and $isClone) {
    & $git @safe -C $ClientRoot pull --ff-only
    if ($LASTEXITCODE -ne 0) { throw "git pull in $ClientRoot failed (local changes, a diverged branch, or no access to the repo). The app keeps the version it had." }
    Write-Host "Now at $(& $git @safe -C $ClientRoot log -1 --format='%h %s')"
}
elseif ($git -and $isEmpty) {
    & $git @safe clone --branch $Branch $RepoUrl $ClientRoot
    if ($LASTEXITCODE -ne 0) { throw "git clone of $RepoUrl failed (no access to the repo?)." }
}
else {
    # No git (or a folder that isn't a clone): download the branch as a zip and copy it over.
    $zip = Join-Path ([System.IO.Path]::GetTempPath()) "terra-client-$([guid]::NewGuid().ToString('N')).zip"
    $unpacked = "$zip.d"
    $repoPath = ([uri]$RepoUrl).AbsolutePath.Trim('/')
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest "https://codeload.github.com/$repoPath/zip/refs/heads/$Branch" -OutFile $zip -UseBasicParsing
        Expand-Archive $zip $unpacked
        $source = (Get-ChildItem $unpacked -Directory | Select-Object -First 1).FullName
        if (-not (Test-Path (Join-Path $source 'index.html'))) { throw 'The download has no index.html; nothing was changed.' }
        New-Item -ItemType Directory -Force $ClientRoot | Out-Null
        robocopy $source $ClientRoot /MIR /XD .git /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
    }
    finally {
        Remove-Item $zip, $unpacked -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$after = Get-AppVersion $ClientRoot
if (-not $after) { throw "$ClientRoot has no version.js after the update." }
Write-Host $(if ($after -eq $before) { "iPad app already up to date: v$after" } else { "iPad app updated: v$before -> v$after" })
exit 0
