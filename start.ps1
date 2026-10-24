# Builds PyMagic (only what changed) and opens the editor in the browser.
#   .\start.ps1                 asks for a project folder (Cancel: examples\demo)
#   .\start.ps1 C:\my\project   opens that folder
#   .\start.ps1 -Rebuild        builds everything again
param(
    [string]$Project,
    [int]$Port = 5180,
    [switch]$Rebuild,
    [switch]$NoBrowser
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
Set-Location $repo

function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }

foreach ($tool in 'dotnet', 'node', 'npm') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Host "'$tool' was not found. Install the .NET 9 SDK and Node.js, then run this again." -ForegroundColor Red
        exit 1
    }
}

# ---- the web page (Monaco bundle) ----
$web = Join-Path $repo 'web'
$dist = Join-Path $web 'dist\main.js'
if (-not (Test-Path (Join-Path $web 'node_modules\monaco-editor'))) { Step 'Installing the web packages (first run only)'; Push-Location $web; npm install; Pop-Location }
$newest = Get-ChildItem (Join-Path $web 'src') -Recurse -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($Rebuild -or -not (Test-Path $dist) -or $newest.LastWriteTime -gt (Get-Item $dist).LastWriteTime) {
    Step 'Building the web page'
    Push-Location $web; node build.mjs; Pop-Location
    if ($LASTEXITCODE -ne 0) { Write-Host 'The web build failed.' -ForegroundColor Red; exit 1 }
}

# ---- the host ----
Step 'Building the host'
dotnet build (Join-Path $repo 'src\PyMagic.Web\PyMagic.Web.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Host 'The host build failed.' -ForegroundColor Red; exit 1 }

# ---- the project folder ----
if (-not $Project) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Choose the Python project to open (Cancel opens the demo project)'
    $dialog.SelectedPath = $repo
    if ($dialog.ShowDialog() -eq 'OK') { $Project = $dialog.SelectedPath }
}
if (-not $Project) { $Project = Join-Path $repo 'examples\demo' }
$Project = (Resolve-Path $Project).Path

# ---- typeshed: PyCharm's copy when it is installed, else the stubs inside PyMagic ----
$typeshed = $env:PYMAGIC_TYPESHED
if (-not $typeshed) {
    $found = Get-ChildItem 'C:\Program Files\JetBrains' -Directory -Filter 'PyCharm*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'plugins\python-ce\helpers\typeshed' } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) { $typeshed = $found }
}

# ---- a free port ----
function PortIsFree($p) {
    try { $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $p); $l.Start(); $l.Stop(); $true } catch { $false }
}
while (-not (PortIsFree $Port)) { $Port++ }

$hostArgs = @('--root', $Project, '--www', (Join-Path $web 'dist'), '--port', $Port)
if ($typeshed) { $hostArgs += @('--typeshed', $typeshed) }

$exe = Get-ChildItem (Join-Path $repo 'src\PyMagic.Web\bin\Release') -Recurse -Filter 'pymagic-web.dll' | Select-Object -First 1
if (-not $exe) { $exe = Get-ChildItem (Join-Path $repo 'src\PyMagic.Web\bin\Release') -Recurse -Filter 'PyMagic.Web.dll' | Select-Object -First 1 }

Step "Opening $Project on http://127.0.0.1:$Port/  (close this window to stop)"
if ($typeshed) { Write-Host "typeshed: $typeshed" }
if (-not $NoBrowser) {
    Start-Job -ScriptBlock { param($u) Start-Sleep 2; Start-Process $u } -ArgumentList "http://127.0.0.1:$Port/" | Out-Null
}
dotnet $exe.FullName @hostArgs
