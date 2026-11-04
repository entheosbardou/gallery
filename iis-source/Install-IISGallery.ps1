param(
    [string]$SiteName='Wallpaper Gallery',
    [string]$PoolName='WallpaperGallery',
    [string]$HostName='bens-laptop.thelinclan.com',
    [int]$Port=8791,
    [string]$UserName='THELINCLAN\ben'
)
$ErrorActionPreference='Stop'
if(Test-Path 'C:\downloads\gallery\iis-data\cleanup.request') {
    Remove-Item -LiteralPath 'C:\downloads\gallery\iis-data\cleanup.request'
    Unregister-ScheduledTask -TaskName 'Wallpaper Gallery IIS Setup' -Confirm:$false
    @{setupTaskRemoved=$true} | ConvertTo-Json | Set-Content C:\downloads\gallery\iis-data\cleanup.json -Encoding UTF8
    exit
}
if(Test-Path 'C:\downloads\gallery\iis-data\inspect.request') {
    Remove-Item -LiteralPath 'C:\downloads\gallery\iis-data\inspect.request'
    & 'C:\downloads\gallery\iis-source\Inspect-IISGallery.ps1'
    exit
}
$galleryPath='C:\downloads\gallery'
$sitePath=Join-Path $galleryPath 'iis-site'
$statePath=Join-Path $galleryPath 'iis-data'
$resultFile=Join-Path $statePath 'deployment.json'
[xml]$galleryConfig=Get-Content -LiteralPath (Join-Path $sitePath 'web.config')
$wallpaperRoot=($galleryConfig.configuration.appSettings.add | Where-Object key -eq 'WallpaperRoot').value
$backupName='WallpaperGallery-'+(Get-Date -Format yyyyMMdd-HHmmss)
try {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {throw 'Run this installer with administrator rights.'}
    if(-not (Test-Path -LiteralPath $wallpaperRoot)) {throw 'Wallpaper root not found.'}
    if(-not (Test-Path (Join-Path $sitePath 'bin\WallpaperGallery.dll'))) {throw 'The compiled gallery is missing.'}
    $backupOutput=& "$env:windir\System32\inetsrv\appcmd.exe" add backup $backupName 2>&1
    if($LASTEXITCODE -ne 0) {throw ('IIS configuration backup failed: '+($backupOutput -join ' '))}
    $features=@('IIS-StaticContent','IIS-DefaultDocument','IIS-ASPNET45','IIS-NetFxExtensibility45','IIS-ISAPIExtensions','IIS-ISAPIFilter','IIS-WindowsAuthentication','IIS-URLAuthorization')
    $missing=@($features | Where-Object { (Get-WindowsOptionalFeature -Online -FeatureName $_).State -ne 'Enabled' })
    if($missing.Count) {
        @{status='enabling-features';features=$missing;backup=$backupName} | ConvertTo-Json -Depth 5 | Set-Content $resultFile -Encoding UTF8
        $enabled=Enable-WindowsOptionalFeature -Online -FeatureName $missing -All -NoRestart
        if($enabled.RestartNeeded) {throw 'Windows enabled the required IIS features but requires a reboot. Configuration backup is preserved; rerun after reboot.'}
    }
    Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
    $manager=New-Object Microsoft.Web.Administration.ServerManager
    $existing=$manager.Sites[$SiteName]
    if($existing -and $existing.Applications['/'].VirtualDirectories['/'].PhysicalPath -ne $sitePath) {throw 'An unrelated IIS site already uses this name.'}
    $pool=$manager.ApplicationPools[$PoolName]
    if(-not $pool) {$pool=$manager.ApplicationPools.Add($PoolName)}
    $pool.ManagedRuntimeVersion='v4.0'
    $pool.ManagedPipelineMode=[Microsoft.Web.Administration.ManagedPipelineMode]::Integrated
    $pool.ProcessModel.IdentityType=[Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity
    $pool.ProcessModel.LoadUserProfile=$false
    $pool.ProcessModel.IdleTimeout=[TimeSpan]::FromMinutes(20)
    $binding='*:'+ $Port + ':'
    foreach($otherSite in $manager.Sites) {
        if($otherSite.Name -ne $SiteName -and ($otherSite.Bindings | Where-Object { $_.Protocol -eq 'http' -and $_.BindingInformation -eq $binding })) {throw 'The requested IIS binding is already in use.'}
    }
    if(-not $existing) {$existing=$manager.Sites.Add($SiteName,'http',$binding,$sitePath)}
    elseif(-not @($existing.Bindings | Where-Object {$_.Protocol -eq 'http' -and $_.BindingInformation -eq $binding}).Count) {$existing.Bindings.Add($binding,'http') | Out-Null}
    $existing.Applications['/'].ApplicationPoolName=$PoolName
    $manager.CommitChanges()
    $manager.Dispose()

    $manager=New-Object Microsoft.Web.Administration.ServerManager
    $configuration=$manager.GetApplicationHostConfiguration()
    $configuration.GetSection('system.webServer/security/authentication/anonymousAuthentication',$SiteName).SetAttributeValue('enabled',$true)
    $configuration.GetSection('system.webServer/security/authentication/anonymousAuthentication',$SiteName).SetAttributeValue('userName','')
    $configuration.GetSection('system.webServer/security/authentication/windowsAuthentication',$SiteName).SetAttributeValue('enabled',$false)
    $authorization=$configuration.GetSection('system.webServer/security/authorization',$SiteName).GetCollection()
    $authorization.Clear()
    $allow=$authorization.CreateElement('add');$allow.SetAttributeValue('accessType','Allow');$allow.SetAttributeValue('users','*');$authorization.Add($allow)
    # The gallery allows anonymous access; unrelated IIS sites are unchanged.
    $manager.CommitChanges()
    $manager.Dispose()

    $poolAccount='IIS AppPool\'+$PoolName
    $aclEntries=@(@($sitePath,'(OI)(CI)(RX)'),@($statePath,'(OI)(CI)(M)'))
    # SMB access uses the server's network identity; never grant a local pool SID on a NAS.
    if(-not $wallpaperRoot.StartsWith('\\')) {$aclEntries+=,@($wallpaperRoot,'(OI)(CI)(M)')}
    foreach($aclEntry in $aclEntries) {
        & icacls.exe $aclEntry[0] /grant ($poolAccount+':'+$aclEntry[1]) /Q | Out-Null
        if($LASTEXITCODE -ne 0) {throw 'Could not grant the gallery access to '+$aclEntry[0]}
    }
    $poolSid=([Security.Principal.NTAccount]$poolAccount).Translate([Security.Principal.SecurityIdentifier]).Value
    $helper=Join-Path $galleryPath 'iis-helper\CookieBridge.exe'
    $taskName='Wallpaper Gallery Browser Session'
    $action=New-ScheduledTaskAction -Execute $helper -Argument $poolSid -WorkingDirectory (Split-Path $helper)
    $trigger=New-ScheduledTaskTrigger -AtLogOn -User $UserName
    $principal=New-ScheduledTaskPrincipal -UserId $UserName -LogonType Interactive -RunLevel Limited
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Supplies only Reddit browser cookies to the local wallpaper IIS application through a restricted private pipe. No HTTP server or cookie files.' -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    $queueAction=New-ScheduledTaskAction -Execute (Join-Path $galleryPath 'iis-helper\QueueGuardian.exe') -WorkingDirectory (Join-Path $galleryPath 'iis-helper')
    Register-ScheduledTask -TaskName 'Wallpaper Gallery Download Queue' -Action $queueAction -Trigger $trigger -Principal $principal -Settings $settings -Description 'Keeps the persisted wallpaper queue running without an open browser.' -Force | Out-Null
    Start-ScheduledTask -TaskName 'Wallpaper Gallery Download Queue'
    $manager=New-Object Microsoft.Web.Administration.ServerManager
    if($manager.Sites[$SiteName].State -ne 'Started') {$manager.Sites[$SiteName].Start() | Out-Null}
    $manager.Dispose()
    $launcher=Join-Path $galleryPath 'Start wallpaper gallery.cmd'
    if(-not (Test-Path ($launcher+'.pre-iis.bak'))) {Copy-Item $launcher ($launcher+'.pre-iis.bak')}
    @('@echo off',('start "" "http://'+$HostName+':'+$Port+'/"')) | Set-Content $launcher -Encoding ASCII
    @{status='installed';site=$SiteName;pool=$PoolName;url=('http://'+$HostName+':'+$Port+'/');root=$wallpaperRoot;backup=$backupName;helperTask=$taskName;user=$UserName;installedUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 5 | Set-Content $resultFile -Encoding UTF8
} catch {
    @{status='failed';error=$_.Exception.Message;backup=$backupName;timeUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 5 | Set-Content $resultFile -Encoding UTF8
    exit 1
}
