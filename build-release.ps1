#Requires -Version 5.1
<#
  build-release.ps1 - builds the LiteBox release artifacts. See BUILD-RELEASE.md for the full explanation.

  WHY multi-file: LaunchBox's core assemblies use a method-body-encryption obfuscator (a runtime JIT-hook
  decryptor). Its native reads (Marshal.ReadInt64 on JIT'd code) AccessViolation-crash under a .NET
  SINGLE-FILE bundle, so the HOST must be the LIGHT multi-file form. A single-file exe can therefore only be
  an INSTALLER, never the host. (See LightPayload.cs / the reference-lb-obfuscator memory.)

  Forms (all self-contained -> 'includedFrameworks' runtimeconfig):
    - light      : NON-single-file, STRIPPED of the .NET runtime DLLs so it borrows LaunchBox\Core's runtime
                   (like LaunchBox.exe). This is THE HOST. Ships LiteBox.exe + LiteBox.dll + the two .json.
                   Runs ONLY from Core. Built per TFM (net9 for LB 13.27, net10 for LB 13.28+).
    - installer  : ONE universal self-contained SINGLE-FILE exe (~86 MB) that EMBEDS both lights (net9+net10)
                   AND the native payload. Never runs as the host: at install it detects Core's .NET
                   (LightPayload.DetectCoreTfm), extracts the matching light into Core + deploys the natives,
                   then launches Core\LiteBox.exe. Drop it anywhere / at the LaunchBox root and double-click.

  Usage:
    powershell -ExecutionPolicy Bypass -File build-release.ps1
    powershell -ExecutionPolicy Bypass -File build-release.ps1 -Lb9Root "D:\LaunchBox-net9"

  Params:
    -Lb9Root  <path>  .NET 9  LaunchBox: net9 SDK compile ref (CS1705 otherwise) AND net9 version label. Default G:\LB.
    -Lb10Root <path>  .NET 10 LaunchBox: net10 SDK compile ref AND net10 version label. Default ..\..\..\LB.
    -Rid      <rid>   runtime identifier. Default win-x64.

  Output layout (release\, git-ignored):
    LiteBox-Setup-<ver10>.exe                  (+ README.txt)   the ONE universal installer (net9+net10)
    light\<ver>_<label>\LiteBox-<ver>.zip      (+ README.txt)   manual "extract into Core" alternative, per TFM
  where <ver> = Major.Minor of the referenced LaunchBox, <label> = net9 / net10, <ver10> = the net10 LB version.
  The exe INSIDE each zip stays "LiteBox.exe" (it lands in Core as-is; the uninstaller + ExtendDB host-detection
  key on that name).
#>
[CmdletBinding()]
param(
  [string]$Lb9Root  = 'G:\LB',
  [string]$Lb10Root = "$PSScriptRoot\..\..\..\LB",
  [string]$Rid      = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$here       = $PSScriptRoot
$proj       = Join-Path $here 'LiteBox.csproj'
$thirdparty = Join-Path $here 'thirdparty'
$readme     = Join-Path $here 'release-README.txt'
$out        = Join-Path $here 'release'
$tmp        = Join-Path $env:TEMP 'litebox-pub'
$Lb10Root   = [IO.Path]::GetFullPath($Lb10Root)

# The 8 native payload files shipped LOOSE in each light zip (under litebox\thirdparty\). Same list as the
# csproj EmbeddedResource block and NativeInstaller.Payload - keep the three in sync.
$payload = @(
  'Everything64.dll.api','Magick.Native-Q16-x64.dll.api','RahasherExtendDB.exe','7z.dll.api',
  'MSVCP140.dll.api','VCRUNTIME140.dll.api','VCRUNTIME140_1.dll.api','steam_api64.dll.api'
)

# The ONLY files the light build ships (everything else the publish produced is the .NET runtime, which
# LaunchBox\Core already provides). deps.json + runtimeconfig.json make it self-contained-flat. These four
# are BOTH the zip contents AND what the universal installer embeds (per TFM) and extracts into Core.
$appFiles = @('LiteBox.exe','LiteBox.dll','LiteBox.deps.json','LiteBox.runtimeconfig.json')

# net9.0-windows -> "net9" (uses Lb9Root), net10.0-windows -> "net10" (uses Lb10Root)
$targets = @(
  @{ Tfm = 'net9.0-windows';  Label = 'net9';  LbRoot = $Lb9Root  },
  @{ Tfm = 'net10.0-windows'; Label = 'net10'; LbRoot = $Lb10Root }
)

# Major.Minor of the LaunchBox this target compiles against (from LaunchBox.exe, fallback to the SDK dll).
function LbVersion([string]$lbRoot) {
  $exe = Join-Path $lbRoot 'Core\LaunchBox.exe'
  $dll = Join-Path $lbRoot 'Core\Unbroken.LaunchBox.Plugins.dll'
  $src = if (Test-Path $exe) { $exe } elseif (Test-Path $dll) { $dll } else { throw "no LaunchBox.exe / SDK dll under $lbRoot\Core" }
  $p = (Get-Item $src).VersionInfo.FileVersion.Split('.')
  return "$($p[0]).$($p[1])"
}

# Publish (always self-contained) into $Dir with the extra props; capture stdout so it doesn't leak; verify.
function Publish([string]$Tfm, [string]$Dir, [string[]]$Extra) {
  if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force }
  $a = @('publish', $proj, '-c', 'Release', '-r', $Rid, '-f', $Tfm, '-p:SelfContained=true',
         "-p:Lb9Root=$Lb9Root", "-p:Lb10Root=$Lb10Root", '-o', $Dir, '-v', 'q') + $Extra
  $log = & dotnet @a
  if ($LASTEXITCODE -ne 0) { $log | Write-Host; throw "publish failed: $Tfm ($($Extra -join ' '))" }
  if (-not (Test-Path (Join-Path $Dir 'LiteBox.exe'))) { throw "LiteBox.exe missing after publish: $Dir" }
}

# fresh output + temp. Tolerate a locked dir (release folder open in Explorer): we overwrite in place anyway.
foreach ($d in @($out, $tmp)) {
  if (Test-Path $d) {
    try { Remove-Item $d -Recurse -Force -ErrorAction Stop }
    catch { Write-Host "  (note: could not fully clear '$d' - overwriting in place. Close any Explorer window on it for a clean wipe.)" }
  }
}
New-Item -ItemType Directory -Force $out | Out-Null

$lightPayload = Join-Path $tmp 'light-payload'   # <label>\{LiteBox.exe,dll,deps,runtimeconfig} → embedded by the installer
$verByLabel   = @{}

# ---- 1) Build BOTH lights: stage for the installer + ship each as a manual "extract into Core" zip ----
foreach ($t in $targets) {
  $tfm = $t.Tfm; $label = $t.Label
  $ver = LbVersion $t.LbRoot           # e.g. 13.27
  $verByLabel[$label] = $ver
  Write-Host "== light $label (LaunchBox $ver, $tfm from $($t.LbRoot)) =="

  $liDir = Join-Path $tmp "$label-light"
  Publish $tfm $liDir @('-p:PublishSingleFile=false', '-p:LiteBoxDist=light')

  # a) stage the 4 app files for the universal installer to embed
  $stageEmbed = Join-Path $lightPayload $label
  New-Item -ItemType Directory -Force $stageEmbed | Out-Null
  foreach ($f in $appFiles) {
    $src = Join-Path $liDir $f
    if (-not (Test-Path $src)) { throw "light build missing expected file: $f ($label)" }
    Copy-Item $src (Join-Path $stageEmbed $f)
  }

  # b) manual zip: the 4 app files + the loose native payload under litebox\thirdparty\ (extract into Core)
  $stageZip = Join-Path $tmp "$label-zip"
  if (Test-Path $stageZip) { Remove-Item $stageZip -Recurse -Force }
  $tpDir = Join-Path $stageZip 'litebox\thirdparty'
  New-Item -ItemType Directory -Force $tpDir | Out-Null
  foreach ($f in $appFiles) { Copy-Item (Join-Path $liDir $f) (Join-Path $stageZip $f) }
  foreach ($p in $payload) {
    $src = Join-Path $thirdparty $p
    if (-not (Test-Path $src)) { throw "payload file missing: $src" }
    Copy-Item $src (Join-Path $tpDir $p)
  }
  $zipRel = Join-Path $out "light\${ver}_${label}"
  New-Item -ItemType Directory -Force $zipRel | Out-Null
  Compress-Archive -Path (Join-Path $stageZip '*') -DestinationPath (Join-Path $zipRel "LiteBox-$ver.zip") -Force
  Copy-Item $readme (Join-Path $zipRel 'README.txt')
}

# ---- 2) Build the ONE universal installer (net10 single-file) embedding BOTH lights + the native payload ----
$ver10 = $verByLabel['net10']
Write-Host "== universal installer (net10 single-file, embeds net9+net10 lights) =="
$instDir = Join-Path $tmp 'installer'
Publish 'net10.0-windows' $instDir @('-p:PublishSingleFile=true', '-p:LiteBoxDist=standalone', "-p:LightPayloadDir=$lightPayload")
Copy-Item (Join-Path $instDir 'LiteBox.exe') (Join-Path $out "LiteBox-Setup-$ver10.exe")
Copy-Item $readme (Join-Path $out 'README.txt')

# ---- summary ----
Write-Host "`n== release tree ($out) =="
Get-ChildItem $out -Recurse -File | Sort-Object FullName | ForEach-Object {
  $size = '{0,9:N2} MB' -f ($_.Length / 1MB)
  $rel  = $_.FullName.Substring($out.Length + 1)
  Write-Host ("  {0}  {1}" -f $size, $rel)
}
Write-Host "`nDone."
