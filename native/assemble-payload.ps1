#Requires -Version 5.1
<#
  assemble-payload.ps1 — gather the four single-artifact native parental files LiteBox ships and deploys.

  Output: native\payload\ (staging), each file suffixed .api so nothing loads it from the shipped
  spot (the in-app Install button strips .api on deploy):
    winhttp.dll.api                  (Ultimate ASI Loader, from ExtendDB\thirdparty\winhttp.dll.api)
    litebox-parental.asi.api         (generic native trigger — sets DOTNET_STARTUP_HOOKS)
    litebox-parental.dll.api         (the managed dll — single net9, loads on both runtimes)
    litebox-parental-native.bin.api  (the native hooks — CreateFileW read filter + CopyFileExW write guard)

  The filter + guard live TOGETHER in the .bin, armed by the managed dll's startup hook; the .asi is a dumb
  trigger and winhttp is the generic loader. All runtime-agnostic — no per-TFM builds.

  LiteBox ships this folder as Core\litebox\parental-native\; the in-app Install button
  (ParentalNativeInstall) copies it into LB\Core + LB\Plugins\litebox-parental. Re-run after rebuilding the
  native projects. Pass -Deploy <LbRoot> to also drop it into a test install for the button.
#>
[CmdletBinding()]
param([string]$Deploy = '')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out  = Join-Path $here 'payload'
New-Item -ItemType Directory -Force -Path $out | Out-Null
# Retire retired payload names from a previous (two-artifact) run so they can't be shipped by accident.
foreach ($old in @('litebox-parentalcontrol.asi.api','litebox-parentalcontrol.dll.api',
                   'litebox-parentalcontrol.net9.dll.api','litebox-parentalcontrol.net10.dll.api','0Harmony.dll.api')) {
  Remove-Item (Join-Path $out $old) -Force -ErrorAction SilentlyContinue
}

$sources = @{
  'winhttp.dll'                 = [IO.Path]::GetFullPath((Join-Path $here '..\..\ExtendDB\thirdparty\winhttp.dll.api'))
  'litebox-parental.asi'        = Join-Path $here 'litebox-parental-trigger\bin\Release\litebox-parental.asi'
  'litebox-parental.dll'        = Join-Path $here 'litebox-parental-managed\bin\Release\litebox-parental.dll'
  'litebox-parental-native.bin' = Join-Path $here 'litebox-parental-native\bin\Release\litebox-parental-native.bin'
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

exit 0   # explicit success code so a caller (build-release.ps1) reading $LASTEXITCODE never sees a stale value
