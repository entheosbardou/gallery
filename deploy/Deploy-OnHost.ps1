param(
    [Parameter(Mandatory=$true)][string]$ReleasePath,
    [string]$LivePath = 'C:\downloads\gallery',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$release = (Resolve-Path -LiteralPath $ReleasePath).Path
$live = (Resolve-Path -LiteralPath $LivePath).Path
foreach ($required in @('iis-site\bin\WallpaperGallery.dll','iis-site\gallery.js','iis-helper\DatabaseWorker.py')) {
    if (-not (Test-Path -LiteralPath (Join-Path $release $required) -PathType Leaf)) { throw "Release is missing $required" }
}
foreach ($required in @('iis-data\mysql.json','iis-data\oidc.json','iis-data\mysql-ca.pem','iis-helper\python-deps\pymysql')) {
    if (-not (Test-Path -LiteralPath (Join-Path $live $required))) { throw "Live dependency is missing: $required" }
}
Import-Module WebAdministration
function Wait-WebAppPoolState([string]$Name,[string]$Expected,[int]$TimeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $state = (Get-WebAppPoolState -Name $Name -ErrorAction Stop).Value
        if ($state -eq $Expected) { return }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "App pool '$Name' did not reach '$Expected' within $TimeoutSeconds seconds (current state: $state)."
}
$site = Get-Website -Name 'Wallpaper Gallery' -ErrorAction Stop
$pool = Get-WebAppPoolState -Name 'WallpaperGallery' -ErrorAction Stop
$task = Get-ScheduledTask -TaskName 'Wallpaper Gallery Database Worker' -ErrorAction Stop
if ($ValidateOnly) { Write-Output 'Release and live dependencies are ready for deployment.'; return }
$backup = Join-Path $live ('change-backups\github-release-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$files = Get-ChildItem -LiteralPath $release -File -Recurse | Where-Object {
    $_.FullName.Substring($release.Length).TrimStart('\') -ne 'iis-site\web.config' -and
    $_.FullName.Substring($release.Length).TrimStart('\') -ne 'requirements-worker.txt'
}
$added = New-Object System.Collections.Generic.List[string]
foreach ($file in $files) {
    $relative = $file.FullName.Substring($release.Length).TrimStart('\')
    $target = Join-Path $live $relative
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        $saved = Join-Path $backup $relative
        New-Item -ItemType Directory -Path (Split-Path $saved) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $saved -Force
    } else { $added.Add($target) }
}
try {
    Stop-ScheduledTask -TaskName $task.TaskName -ErrorAction SilentlyContinue
    Stop-WebAppPool -Name 'WallpaperGallery'
    Wait-WebAppPoolState -Name 'WallpaperGallery' -Expected 'Stopped'
    Get-CimInstance Win32_Process -Filter "Name='CookieBridge.exe' OR Name='QueueGuardian.exe'" | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($live + '\',[StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($release.Length).TrimStart('\')
        $target = Join-Path $live $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    Start-WebAppPool -Name 'WallpaperGallery'
    Wait-WebAppPoolState -Name 'WallpaperGallery' -Expected 'Started'
    Start-ScheduledTask -TaskName $task.TaskName
    $running = (Get-ScheduledTask -TaskName $task.TaskName).State
    if ($running -ne 'Running') { throw "Database worker did not start: $running" }
    Write-Output "Deployed release. Backup: $backup"
} catch {
    foreach ($target in $added) { if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force } }
    foreach ($saved in Get-ChildItem -LiteralPath $backup -File -Recurse) {
        $relative = $saved.FullName.Substring($backup.Length).TrimStart('\')
        Copy-Item -LiteralPath $saved.FullName -Destination (Join-Path $live $relative) -Force
    }
    try {
        $poolState = (Get-WebAppPoolState -Name 'WallpaperGallery' -ErrorAction Stop).Value
        if ($poolState -eq 'Stopping') {
            Wait-WebAppPoolState -Name 'WallpaperGallery' -Expected 'Stopped'
            $poolState = 'Stopped'
        }
        if ($poolState -eq 'Starting') {
            Wait-WebAppPoolState -Name 'WallpaperGallery' -Expected 'Started'
            $poolState = 'Started'
        }
        if ($poolState -eq 'Stopped') { Start-WebAppPool -Name 'WallpaperGallery' }
        Wait-WebAppPoolState -Name 'WallpaperGallery' -Expected 'Started'
    } catch {
        Write-Warning "Could not restore the Gallery app pool automatically: $($_.Exception.Message)"
    }
    Start-ScheduledTask -TaskName $task.TaskName -ErrorAction SilentlyContinue
    throw
}

