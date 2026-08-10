#Requires -Version 5.1
<#
  deploy-probe.ps1 — set up the single-managed-DLL TIMING experiment on a test LaunchBox install.

  It does two things:
   1) CLEANS any real native parental control out of the install first (winhttp.dll + the ASI in Core,
      the Plugins\litebox-parentalcontrol\ folder). This is mandatory — otherwise the REAL ASI also hooks
      CreateFileW and the probe's measurement is meaningless. (Source files are untouched; only the
      DEPLOYED copies in this test LB are removed.)
   2) Deploys the probe: Plugins\filterprobe\{filterprobe-plugin.dll (matching Core's TFM) + filterprobe.dll}.

  Then launch LaunchBox/BigBox and read Core\filterprobe.log — see the printed instructions.

  Build the two probe projects first:
    msbuild native\filterprobe\filterprobe.vcxproj /p:Configuration=Release /p:Platform=x64
    dotnet build native\filterprobe-plugin\filterprobe-plugin.csproj -c Release
#>
[CmdletBinding()]
param([string]$LbRoot = '')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not $LbRoot) { $LbRoot = [IO.Path]::GetFullPath((Join-Path $here '..\..\..\..\..\LB')) }
$core = Join-Path $LbRoot 'Core'
if (-not (Test-Path (Join-Path $core 'LaunchBox.exe'))) { throw "not a LaunchBox install (no Core\LaunchBox.exe): $LbRoot" }

Write-Host "== Test LaunchBox: $LbRoot" -ForegroundColor Cyan

# --- 1) Clean the REAL native parental so only the probe hooks CreateFileW -------------------------
Write-Host "-- cleaning any deployed native parental control (winhttp + ASI + guard plugin)" -ForegroundColor Yellow
foreach ($f in @('winhttp.dll','litebox-parentalcontrol.asi')) {
  $p = Join-Path $core $f
  if (Test-Path $p) { Remove-Item $p -Force; Write-Host "   removed Core\$f" }
}
$guardDir = Join-Path $LbRoot 'Plugins\litebox-parentalcontrol'
if (Test-Path $guardDir) { Remove-Item $guardDir -Recurse -Force; Write-Host "   removed Plugins\litebox-parentalcontrol\" }
# Old probe logs, so the run is fresh.
foreach ($lg in @((Join-Path $core 'filterprobe.log'))) { if (Test-Path $lg) { Remove-Item $lg -Force } }

# --- 2) Detect Core runtime and pick the matching managed probe plugin -----------------------------
function Core-Tfm([string]$coreDir) {
  foreach ($n in @('coreclr.dll','System.Private.CoreLib.dll','hostpolicy.dll')) {
    $f = Join-Path $coreDir $n
    if (Test-Path $f) { $maj = (Get-Item $f).VersionInfo.ProductMajorPart; if ($maj -gt 0) { return ($(if ($maj -le 9) {'net9.0-windows'} else {'net10.0-windows'})) } }
  }
  return 'net10.0-windows'
}
$tfm = Core-Tfm $core
Write-Host "-- Core runtime -> $tfm" -ForegroundColor Cyan

$pluginBin = Join-Path $here "..\filterprobe-plugin\bin\Release\$tfm"
$nativeDll = Join-Path $here 'bin\Release\filterprobe.dll'
foreach ($need in @((Join-Path $pluginBin 'filterprobe-plugin.dll'), $nativeDll)) {
  if (-not (Test-Path $need)) { throw "build output missing: $need  (build the two probe projects first)" }
}

$dst = Join-Path $LbRoot 'Plugins\filterprobe'
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item (Join-Path $pluginBin 'filterprobe-plugin.dll') $dst -Force
if (Test-Path (Join-Path $pluginBin 'filterprobe-plugin.deps.json')) { Copy-Item (Join-Path $pluginBin 'filterprobe-plugin.deps.json') $dst -Force }
Copy-Item $nativeDll $dst -Force
Write-Host "-- deployed probe -> Plugins\filterprobe\ (filterprobe-plugin.dll + filterprobe.dll)" -ForegroundColor Green

Write-Host ""
Write-Host "NEXT:" -ForegroundColor Cyan
Write-Host "  1. Launch LaunchBox (and/or BigBox) from this install."
Write-Host "  2. Open Tools menu -> 'Filter probe: N platform-xml reads caught'."
Write-Host "  3. Read Core\filterprobe.log and Plugins\filterprobe\filterprobe-managed.log."
Write-Host ""
Write-Host "READING THE RESULT:" -ForegroundColor Cyan
Write-Host "  - managed log shows WHEN the plugin's ModuleInitializer ran + that Probe_Install returned 0."
Write-Host "  - filterprobe.log '[ProbeInstall] hook ARMED' then '[Open] Platforms xml READ ...' lines."
Write-Host "  * If the [Open] lines appear at STARTUP (before you click anything) -> EARLY ENOUGH: Option B viable."
Write-Host "  * If NO [Open] lines at startup but they appear only after you browse platforms -> TOO LATE."
