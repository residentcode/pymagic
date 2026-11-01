# A check of the built setup (desktop\out\PyMagic-Setup-0.1.0.exe): silent installs into folders of the temp folder, the shortcut on the desktop and its removal, a folder with other files refused,
# a parent folder with other files redirected to its PyMagic sub folder. It makes and removes a shortcut on the real desktop (and stops if one is there). Run: powershell -File desktop\installer\check-installer.ps1
$ErrorActionPreference = 'Stop'
$setup = 'C:\Users\latch\pymagic\desktop\out\PyMagic-Setup-0.1.0.exe'
$desk = [Environment]::GetFolderPath('Desktop')
$lnk = Join-Path $desk 'PyMagic.lnk'
"desktop shortcut before: $(Test-Path $lnk)"
if (Test-Path $lnk) { 'a PyMagic shortcut is already on the desktop; stopping so it is not touched'; exit 3 }
$base = Join-Path $env:TEMP ('pm_inst_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $base | Out-Null

# 1. a clean folder
$dir1 = Join-Path $base 'one\PyMagic'
$p = Start-Process $setup -ArgumentList '/S', '/currentuser', "/D=$dir1" -Wait -PassThru
"1 install exit=$($p.ExitCode) exe=$(Test-Path (Join-Path $dir1 'PyMagic.exe')) uninstaller=$(Test-Path (Join-Path $dir1 'Uninstall PyMagic.exe')) desktop-shortcut=$(Test-Path $lnk)"

# 2. uninstall it
$un = Join-Path $dir1 'Uninstall PyMagic.exe'
if (Test-Path $un) {
  $p = Start-Process $un -ArgumentList '/S', '/currentuser', "_?=$dir1" -Wait -PassThru
  "2 uninstall exit=$($p.ExitCode) desktop-shortcut=$(Test-Path $lnk)"
  Start-Sleep -Seconds 2
  if (Test-Path $dir1) { Remove-Item (Join-Path $dir1 'Uninstall PyMagic.exe') -Force -ErrorAction SilentlyContinue; Remove-Item $dir1 -Force -ErrorAction SilentlyContinue }
}
if (Test-Path $lnk) { Remove-Item $lnk -Force; '2b removed a shortcut the uninstaller left' }

# 3. a folder that already holds other files: must be refused, nothing added, nothing deleted
$dir2 = Join-Path $base 'two\PyMagic'
New-Item -ItemType Directory -Path $dir2 | Out-Null
Set-Content -Path (Join-Path $dir2 'keep.txt') -Value 'mine'
$p = Start-Process $setup -ArgumentList '/S', '/currentuser', "/D=$dir2" -Wait -PassThru
$left = (Get-ChildItem $dir2 -Force | ForEach-Object Name) -join ','
"3 refused exit=$($p.ExitCode) files-in-folder=$left desktop-shortcut=$(Test-Path $lnk)"

# 4. a parent folder with other files: the install goes to its PyMagic sub folder
$dir3 = Join-Path $base 'three'
New-Item -ItemType Directory -Path $dir3 | Out-Null
Set-Content -Path (Join-Path $dir3 'other.txt') -Value 'mine'
$p = Start-Process $setup -ArgumentList '/S', '/currentuser', "/D=$dir3" -Wait -PassThru
"4 parent-with-files exit=$($p.ExitCode) exe-in-sub=$(Test-Path (Join-Path $dir3 'PyMagic\PyMagic.exe')) exe-in-parent=$(Test-Path (Join-Path $dir3 'PyMagic.exe')) other-kept=$(Test-Path (Join-Path $dir3 'other.txt'))"
$un = Join-Path $dir3 'PyMagic\Uninstall PyMagic.exe'
if (Test-Path $un) { Start-Process $un -ArgumentList '/S', '/currentuser', "_?=$(Join-Path $dir3 'PyMagic')" -Wait | Out-Null; Start-Sleep -Seconds 2 }
if (Test-Path $lnk) { Remove-Item $lnk -Force; '4b removed the shortcut of test 4' }
"base kept at $base"
