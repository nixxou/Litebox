#Requires -Version 5.1
<#
  deploy-dev.ps1 - DEV deployment of LiteBox (and optionally the ExtendDB plugin) to the two test installs.

  What it does (the exact sequence used by hand during dev sessions):
    1. dotnet publish LiteBox light MULTI-FILE (-p:PublishSingleFile=false -p:LiteBoxDist=light) per TFM:
         net9  -> <Lb9Root>\Core   (LB 13.27)          default G:\LB
         net10 -> <Lb10Root>\Core  (LB 13.28+)         default <repo>\..\..\..\LB
       Copies the 4 host files (LiteBox.exe/.dll/.deps.json/.runtimeconfig.json), retrying while a running
       LiteBox keeps them locked (use -Kill to terminate LiteBox first).
    2. Hot-deploys the web assets (web-assets\{litebox,bigbox,vendor}) into BOTH the staging folder
       (Core\litebox\web-assets\<site>) AND the SERVED folder (Core\litebox\web\<site>) of each install -
       needed because WebAssets.EnsureDeployed only re-copies staging->served on a VERSION change (stamp file).
    3. -Plugin: builds ..\ExtendDB\ExtendDB.csproj and deploys ExtendDB.dll (net9) + BigBoxWeb\web assets to
       every install that actually has Plugins\ExtendDB\ExtendDB.dll (currently G:\LB only).

  Usage:
    powershell -ExecutionPolicy Bypass -File deploy-dev.ps1                 # LiteBox -> both installs
    powershell -ExecutionPolicy Bypass -File deploy-dev.ps1 -Plugin        # + ExtendDB plugin
    powershell -ExecutionPolicy Bypass -File deploy-dev.ps1 -Kill          # kill running LiteBox first
    powershell -ExecutionPolicy Bypass -File deploy-dev.ps1 -SkipNet9      # net10 install only
#>
[CmdletBinding()]
param(
  [string]$Lb9Root  = 'G:\LB',
  [string]$Lb10Root = '',
  [switch]$Plugin,
  [switch]$Kill,
  [switch]$SkipNet9,
  [switch]$SkipNet10
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not $Lb10Root) { $Lb10Root = [IO.Path]::GetFullPath((Join-Path $here '..\..\..\LB')) }
$proj = Join-Path $here 'LiteBox.csproj'
# Mirror LightPayload.Files (the installer's Core set): forgetting a loose app dll here means the dev
# installs silently lose the feature it backs (Magick absent = thumb generation no-ops, for one).
$hostFiles = @('LiteBox.exe', 'LiteBox.dll', 'LiteBox.deps.json', 'LiteBox.runtimeconfig.json',
               'LibVLCSharp.dll', 'ZstdSharp.dll', 'Magick.NET-Q16-AnyCPU.dll', 'Magick.NET.Core.dll',
               'Microsoft.ML.OnnxRuntime.dll')
$sites = @('litebox', 'bigbox', 'vendor')
$fail = $false

function Publish-Light([string]$tfm, [string]$outDir) {
  Write-Host "-- publish $tfm (light multi-file)" -ForegroundColor Cyan
  & dotnet publish $proj -c Release -f $tfm -r win-x64 --self-contained `
      -p:PublishSingleFile=false -p:LiteBoxDist=light -o $outDir --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { throw "publish $tfm failed" }
}

# Copies one file with a lock-retry loop (running LiteBox holds the exe/dll). Returns $true on success.
function Copy-Retry([string]$src, [string]$dstDir, [int]$tries = 8, [int]$delaySec = 10) {
  for ($i = 1; $i -le $tries; $i++) {
    try { Copy-Item $src (Join-Path $dstDir (Split-Path $src -Leaf)) -Force; return $true }
    catch {
      if ($i -eq $tries) { Write-Warning "LOCKED after $tries tries: $dstDir\$(Split-Path $src -Leaf) - close LiteBox and re-run (or use -Kill)"; return $false }
      Write-Host "  locked ($dstDir) - retry $i/$tries in ${delaySec}s..."
      Start-Sleep -Seconds $delaySec
    }
  }
}

function Deploy-Host([string]$outDir, [string]$lbRoot) {
  $core = Join-Path $lbRoot 'Core'
  if (-not (Test-Path (Join-Path $core 'LaunchBox.exe'))) { Write-Warning "skip: $core is not a LaunchBox Core"; return }
  $ok = $true
  foreach ($f in $hostFiles) { if (-not (Copy-Retry (Join-Path $outDir $f) $core)) { $ok = $false } }
  if ($ok) { Write-Host "  host -> $core  OK" -ForegroundColor Green } else { $script:fail = $true }
}

function Deploy-WebAssets([string]$lbRoot) {
  $core = Join-Path $lbRoot 'Core'
  foreach ($site in $sites) {
    $src = Join-Path $here "web-assets\$site"
    if (-not (Test-Path $src)) { continue }
    foreach ($dst in @((Join-Path $core "litebox\web-assets\$site"), (Join-Path $core "litebox\web\$site"))) {
      # Only refresh folders LiteBox has already created - never invent the layout on a fresh install
      # (WebAssets.EnsureDeployed does the initial deploy itself).
      if (Test-Path $dst) {
        Copy-Item (Join-Path $src '*') $dst -Recurse -Force
        Write-Host "  web '$site' -> $dst" -ForegroundColor Green
      }
    }
  }
}

function Deploy-Plugin([string]$lbRoot) {
  $dstDir = Join-Path $lbRoot 'Plugins\ExtendDB'
  if (-not (Test-Path (Join-Path $dstDir 'ExtendDB.dll'))) { return }   # install doesn't run the plugin
  $dll = Join-Path $here '..\ExtendDB\bin\Release\net9.0-windows\ExtendDB.dll'
  if (-not (Copy-Retry $dll $dstDir)) { $script:fail = $true; return }
  $webSrc = Join-Path $here '..\ExtendDB\BigBoxWeb\web'
  $webDst = Join-Path $dstDir 'BigBoxWeb\web'
  if ((Test-Path $webSrc) -and (Test-Path $webDst)) { Copy-Item (Join-Path $webSrc '*') $webDst -Recurse -Force }
  Write-Host "  plugin -> $dstDir  OK (restart LaunchBox/BigBox/LiteBox to reload the dll)" -ForegroundColor Green
}

# -- go --
if ($Kill) {
  Get-Process LiteBox -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Seconds 1
}

$t9  = Join-Path $env:TEMP 'litebox-dev9'
$t10 = Join-Path $env:TEMP 'litebox-dev10'

if (-not $SkipNet9)  { Publish-Light 'net9.0-windows'  $t9 }
if (-not $SkipNet10) { Publish-Light 'net10.0-windows' $t10 }

# Native parental payload (.api) - shipped so the in-app Install button can deploy it on demand.
# net10 ONLY: the write-guard plugin is net10 (LB 13.28); on a net9 host it wouldn't load, which would
# let the ASI filter reads with no write-guard = data risk. Assemble first if native/payload is stale.
function Deploy-ParentalNative([string]$lbRoot) {
  $payload = Join-Path $here 'native\payload'
  if (-not (Test-Path (Join-Path $payload 'litebox-parentalcontrol.asi.api'))) {
    Write-Warning "  parental payload not staged (run native\assemble-payload.ps1) - skipping native-parental ship"
    return
  }
  $dst = Join-Path $lbRoot 'Core\litebox\parental-native'
  New-Item -ItemType Directory -Force -Path $dst | Out-Null
  # Mirror the staged payload exactly — clear stale .api first so a renamed file (e.g. the retired
  # single-TFM guard name) never lingers in the dev install.
  Remove-Item (Join-Path $dst '*.api') -Force -ErrorAction SilentlyContinue
  Copy-Item (Join-Path $payload '*.api') $dst -Force
  Write-Host "  parental-native (.api) -> $dst" -ForegroundColor Green
}

if (-not $SkipNet9)  { Write-Host "-- deploy net9 -> $Lb9Root" -ForegroundColor Cyan;  Deploy-Host $t9  $Lb9Root;  Deploy-WebAssets $Lb9Root }
if (-not $SkipNet10) { Write-Host "-- deploy net10 -> $Lb10Root" -ForegroundColor Cyan; Deploy-Host $t10 $Lb10Root; Deploy-WebAssets $Lb10Root; Deploy-ParentalNative $Lb10Root }

if ($Plugin) {
  Write-Host '-- build ExtendDB plugin' -ForegroundColor Cyan
  & dotnet build (Join-Path $here '..\ExtendDB\ExtendDB.csproj') -c Release --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { throw 'plugin build failed' }
  Deploy-Plugin $Lb9Root
  Deploy-Plugin $Lb10Root
}

if ($fail) { Write-Warning 'deployment INCOMPLETE (locked files) - see warnings above'; exit 1 }
Write-Host 'Deployment complete.' -ForegroundColor Green
