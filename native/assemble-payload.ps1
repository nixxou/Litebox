#Requires -Version 5.1
<#
  assemble-payload.ps1 — gather the four native parental files LiteBox ships and deploys.

  Output: native\payload\ (staging), each file suffixed .api so nothing loads it from the shipped
  spot (the in-app Install button strips .api on deploy):
    litebox-parentalcontrol.asi.api   (WS5.1 build)
    litebox-parentalcontrol.dll.api   (WS5.2 build)
    0Harmony.dll.api                  (Harmony, from the ExtendDB checkout)
    winhttp.dll.api                   (Ultimate ASI Loader, from ExtendDB\thirdparty\winhttp.dll.api)

  LiteBox ships this folder as Core\litebox\parental-native\; the in-app Install button
  (ParentalNativeInstall) copies it into LB\Core + LB\Plugins. Re-run after rebuilding either
  native project. Pass -Deploy <LbRoot> to also drop it into a test install for the button.
#>
[CmdletBinding()]
param([string]$Deploy = '')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out  = Join-Path $here 'payload'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$sources = @{
  'litebox-parentalcontrol.asi' = Join-Path $here 'litebox-parentalcontrol-asi\bin\Release\litebox-parentalcontrol.asi'
  'litebox-parentalcontrol.dll' = Join-Path $here 'litebox-parentalcontrol-dll\bin\Release\litebox-parentalcontrol.dll'
  '0Harmony.dll'                = [IO.Path]::GetFullPath((Join-Path $here '..\..\ExtendDB\0Harmony.dll'))
  'winhttp.dll'                 = [IO.Path]::GetFullPath((Join-Path $here '..\..\ExtendDB\thirdparty\winhttp.dll.api'))
}

$missing = @()
foreach ($name in $sources.Keys) {
  $src = $sources[$name]
  if (-not (Test-Path $src)) { $missing += "$name  <-  $src"; continue }
  Copy-Item $src (Join-Path $out "$name.api") -Force
  Write-Host "  staged $name.api" -ForegroundColor Green
}
if ($missing.Count -gt 0) {
  Write-Warning "missing sources (build the native projects first):`n  $($missing -join "`n  ")"
  exit 1
}
Write-Host "payload assembled at $out" -ForegroundColor Cyan

if ($Deploy) {
  $dst = Join-Path $Deploy 'Core\litebox\parental-native'
  if (-not (Test-Path (Join-Path $Deploy 'Core\LaunchBox.exe'))) { Write-Warning "skip deploy: $Deploy is not a LaunchBox install"; exit 0 }
  New-Item -ItemType Directory -Force -Path $dst | Out-Null
  Copy-Item (Join-Path $out '*') $dst -Force
  Write-Host "deployed payload -> $dst  (inert until the in-app Install button runs)" -ForegroundColor Green
}
