$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
  $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
  throw "Could not find csc.exe from .NET Framework."
}

$out = Join-Path $root "DesktopLight.exe"
$source = Join-Path $root "DesktopLightApp.cs"
$argsList = @(
  "/nologo",
  "/target:winexe",
  "/out:$out",
  "/reference:System.Windows.Forms.dll",
  "/reference:System.Drawing.dll",
  "/reference:System.Web.Extensions.dll",
  $source
)

& $csc @argsList

if ($LASTEXITCODE -ne 0) {
  throw "DesktopLight.exe build failed."
}

Write-Host "Built DesktopLight.exe"
