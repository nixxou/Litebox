#Requires -Version 5.1
<#
  build-installer.ps1 — build the standalone parental plugin installer (litebox-parental-plugin.exe).

  Steps:
    1. (unless -SkipPayload) run assemble-payload.ps1 to stage native\payload\*.api — the 4 files the
       installer EMBEDS. The native projects (trigger .asi, native .bin, managed dll) must be built first.
    2. dotnet publish the installer SELF-CONTAINED single-file (RID/SelfContained/PublishSingleFile are in
       the csproj) -> native\litebox-parental-installer\dist\litebox-parental-plugin.exe.

  Distribute that one exe: drop it at the LaunchBox ROOT and run it (or pick the root LaunchBox.exe).
#>
[CmdletBinding()]
param([switch]$SkipPayload)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

if (-not $SkipPayload) {
  Write-Host '-- staging payload' -ForegroundColor Cyan
  & (Join-Path $here 'assemble-payload.ps1')
  if ($LASTEXITCODE -ne 0) { throw 'assemble-payload failed (build the native projects first)' }
}

$proj = Join-Path $here 'litebox-parental-installer\litebox-parental-installer.csproj'
$out  = Join-Path $here 'litebox-parental-installer\dist'
Write-Host '-- publish installer (self-contained single-file)' -ForegroundColor Cyan
& dotnet publish $proj -c Release -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$exe = Join-Path $out 'litebox-parental-plugin.exe'
if (Test-Path $exe) {
  $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
  Write-Host "installer -> $exe  ($mb MB)" -ForegroundColor Green
} else {
  Write-Warning "expected exe not found at $exe"
}
