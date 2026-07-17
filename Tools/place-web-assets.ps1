# Populate the LiteBox web frontends' static assets from your private ExtendDB theme, pruned of demo media.
#
# Run this YOURSELF (it is a private->product copy, which the assistant is not allowed to perform for you).
# It copies straight into the runtime location LB\Core\litebox\web\ so the themes render immediately, no rebuild.
#
#   powershell -ExecutionPolicy Bypass -File tools\place-web-assets.ps1
#
# Layout produced (served by the S1 web server):
#   Core\litebox\web\bigbox\   = ExtendDB BigBoxWeb/web/  minus launchbox/, vendor/, and demo dirs
#   Core\litebox\web\litebox\  = ExtendDB BigBoxWeb/web/launchbox/  minus demo dirs   (the "LiteBox Web" theme)
#   Core\litebox\web\vendor\   = ExtendDB BigBoxWeb/web/vendor/
# The database site is server-rendered (slice S3), so web\database\ stays empty.
#
# NOTE: this is the TEST placement (runtime). To have it survive a clean redeploy, also copy the same three
# folders into a gitignored  LbApiHost\web-assets\{bigbox,litebox,vendor}\  — the build copies that to output
# and WebAssets.EnsureDeployed re-installs it on boot. web-assets\ is gitignored: nothing reaches the repo.

param(
    [string]$Src = 'c:\Users\mehdi\source\repos\scrapper-project\project\ExtendDB\ExtendDB\BigBoxWeb\web',
    [string]$WebRoot = 'c:\Users\mehdi\source\repos\scrapper-project\LB\Core\litebox\web'
)

$demo = @('data','images','videos','renders','screens','test-elements')  # demo/dummy media to drop (keep sounds/)
if (-not (Test-Path $Src)) { Write-Error "source theme not found: $Src"; exit 1 }

function Copy-Pruned([string]$from, [string]$to, [string[]]$excludeTop) {
    if (Test-Path $to) { Remove-Item -Recurse -Force $to }
    New-Item -ItemType Directory -Force $to | Out-Null
    Get-ChildItem -LiteralPath $from -Force | ForEach-Object {
        if ($_.PSIsContainer -and ($excludeTop -contains $_.Name)) { return }
        Copy-Item -LiteralPath $_.FullName -Destination $to -Recurse -Force
    }
}

Copy-Pruned $Src                       (Join-Path $WebRoot 'bigbox')  (@('launchbox','vendor') + $demo)
Copy-Pruned (Join-Path $Src 'launchbox') (Join-Path $WebRoot 'litebox') $demo
Copy-Pruned (Join-Path $Src 'vendor')    (Join-Path $WebRoot 'vendor')  @()
New-Item -ItemType Directory -Force (Join-Path $WebRoot 'database') | Out-Null

$files = Get-ChildItem -Recurse -File $WebRoot
Write-Output ("web assets placed under {0}" -f $WebRoot)
Write-Output ("  {0} files, {1:N1} MB" -f $files.Count, (($files | Measure-Object -Property Length -Sum).Sum/1MB))
Get-ChildItem $WebRoot -Directory | ForEach-Object { Write-Output ("  " + $_.Name + "/") }
Write-Output "Now start LiteBox with the Web module ON, and open http://127.0.0.1:8080/bigbox/"
