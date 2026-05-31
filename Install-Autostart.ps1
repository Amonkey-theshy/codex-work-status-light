$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$startup = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startup "CodexWorkStatusLight.lnk"
$legacyShortcutPath = Join-Path $startup "Codex 工作状态指示灯.lnk"
$targetPath = Join-Path $root "Start-DesktopLight.ps1"

if (Test-Path $legacyShortcutPath) {
  Remove-Item -LiteralPath $legacyShortcutPath -Force
}

if (-not (Test-Path (Join-Path $root "DesktopLight.exe"))) {
  & (Join-Path $root "Build-DesktopLight.ps1")
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = "powershell.exe"
$shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$targetPath`""
$shortcut.WorkingDirectory = $root
$shortcut.IconLocation = Join-Path $root "DesktopLight.exe"
$shortcut.Description = "Codex work status light"
$shortcut.Save()

Write-Host "Installed autostart shortcut:"
Write-Host $shortcutPath
