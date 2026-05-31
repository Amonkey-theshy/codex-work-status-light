$startup = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startup "CodexWorkStatusLight.lnk"
$legacyShortcutPath = Join-Path $startup "Codex 工作状态指示灯.lnk"

if (Test-Path $shortcutPath) {
  Remove-Item -LiteralPath $shortcutPath -Force
  Write-Host "Removed autostart shortcut:"
  Write-Host $shortcutPath
} elseif (Test-Path $legacyShortcutPath) {
  Remove-Item -LiteralPath $legacyShortcutPath -Force
  Write-Host "Removed legacy autostart shortcut:"
  Write-Host $legacyShortcutPath
} else {
  Write-Host "Autostart shortcut was not found."
}
