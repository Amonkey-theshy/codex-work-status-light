$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$packageName = "CodexWorkStatusLight"
$stage = Join-Path $dist $packageName
$zip = Join-Path $dist "$packageName.zip"

if (Test-Path $stage) {
  $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
  $resolvedDist = if (Test-Path $dist) { (Resolve-Path -LiteralPath $dist).Path } else { $dist }
  if (-not $resolvedStage.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected staging path: $resolvedStage"
  }
  Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}

New-Item -ItemType Directory -Path $stage | Out-Null

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
  $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
  throw "Could not find csc.exe from .NET Framework."
}

& $csc @(
  "/nologo",
  "/target:winexe",
  "/out:$(Join-Path $stage "DesktopLight.exe")",
  "/reference:System.Windows.Forms.dll",
  "/reference:System.Drawing.dll",
  "/reference:System.Web.Extensions.dll",
  (Join-Path $root "DesktopLightApp.cs")
)

if ($LASTEXITCODE -ne 0) {
  throw "DesktopLight.exe release build failed."
}

$files = @(
  "DesktopLightApp.cs",
  "Build-DesktopLight.ps1",
  "Start-DesktopLight.ps1",
  "Install-Autostart.ps1",
  "Uninstall-Autostart.ps1",
  "Set-Status.ps1",
  "DesktopLight.ps1",
  "server.js",
  "package.json",
  "README.md",
  "启动桌面指示灯.bat",
  "install-autostart.bat",
  "uninstall-autostart.bat"
)

foreach ($file in $files) {
  $source = Join-Path $root $file
  if (Test-Path -LiteralPath $source) {
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage $file) -Force
  }
}

if (Test-Path (Join-Path $root "public")) {
  Copy-Item -LiteralPath (Join-Path $root "public") -Destination (Join-Path $stage "public") -Recurse -Force
}

if (Test-Path $zip) {
  Remove-Item -LiteralPath $zip -Force
}

Compress-Archive -LiteralPath $stage -DestinationPath $zip -Force
Write-Host "Created release package:"
Write-Host $zip
