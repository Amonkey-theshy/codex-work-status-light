param(
  [switch]$Manual,
  [switch]$NoSnap
)

$root = $PSScriptRoot
$exe = Join-Path $root "DesktopLight.exe"

if (-not (Test-Path $exe)) {
  & (Join-Path $root "Build-DesktopLight.ps1")
}

if (Get-Process DesktopLight -ErrorAction SilentlyContinue) {
  exit 0
}

$argsList = @()
if ($Manual) { $argsList += "--manual" }
if ($NoSnap) { $argsList += "--no-snap" }

if ($argsList.Count -gt 0) {
  Start-Process -FilePath $exe -ArgumentList $argsList
} else {
  Start-Process -FilePath $exe
}
