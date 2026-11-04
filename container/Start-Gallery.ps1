$ErrorActionPreference = 'Stop'
$base = 'C:\downloads\gallery'
$storage = if ($env:GALLERY_WALLPAPER_ROOT) { $env:GALLERY_WALLPAPER_ROOT } else { 'C:\wallpapers' }
if (-not (Test-Path -LiteralPath $storage -PathType Container)) { throw "Wallpaper storage is unavailable: $storage" }
foreach ($name in @('mysql.json','oidc.json','mysql-ca.pem')) {
    if (-not (Test-Path -LiteralPath (Join-Path $base "iis-data\$name") -PathType Leaf)) { throw "Missing mounted configuration: $name" }
}
[xml]$config = Get-Content -LiteralPath (Join-Path $base 'iis-site\web.config') -Raw
foreach ($entry in $config.configuration.appSettings.add) {
    if ($entry.key -eq 'WallpaperRoot') { $entry.value = $storage }
    if ($entry.key -eq 'WallhavenKeyFile') { $entry.value = Join-Path $storage 'wallhaven-api-key.txt' }
}
$config.Save((Join-Path $base 'iis-site\web.config'))
Import-Module WebAdministration
Set-ItemProperty 'IIS:\Sites\Default Web Site' -Name physicalPath -Value (Join-Path $base 'iis-site')
& icacls.exe (Join-Path $base 'iis-data') /grant 'IIS_IUSRS:(OI)(CI)(M)' /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'IIS data access setup failed.' }
$worker = Start-Process -FilePath 'C:\Python314\python.exe' -ArgumentList (Join-Path $base 'iis-helper\DatabaseWorker.py') -PassThru -WindowStyle Hidden
$iis = Start-Process -FilePath 'C:\ServiceMonitor.exe' -ArgumentList 'w3svc' -PassThru -NoNewWindow
try {
    while (-not $worker.HasExited -and -not $iis.HasExited) { Start-Sleep -Seconds 5; $worker.Refresh(); $iis.Refresh() }
    throw "Gallery process exited (worker=$($worker.HasExited), IIS=$($iis.HasExited))."
} finally {
    foreach ($process in @($worker,$iis)) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force } }
}
