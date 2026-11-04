$ErrorActionPreference='Stop'
$resultPath='C:\downloads\gallery\iis-data\persistent-download-setup.json'
try {
 $feature=Get-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit
 if($feature.State -ne 'Enabled'){$enabled=Enable-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit -All -NoRestart;if($enabled.RestartNeeded){throw 'Application initialization needs a Windows restart'}}
 Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
 $manager=New-Object Microsoft.Web.Administration.ServerManager
 $pool=$manager.ApplicationPools['WallpaperGallery'];$site=$manager.Sites['Wallpaper Gallery']
 $before=@{startMode=$pool.StartMode.ToString();idleTimeout=$pool.ProcessModel.IdleTimeout.ToString();preload=$site.Applications['/'].GetAttributeValue('preloadEnabled')}
 $before|ConvertTo-Json|Set-Content C:\downloads\gallery\iis-data\before-persistent-download-settings.json
 $pool.StartMode=[Microsoft.Web.Administration.StartMode]::AlwaysRunning
 $pool.ProcessModel.IdleTimeout=[TimeSpan]::Zero
 $site.Applications['/'].SetAttributeValue('preloadEnabled',$true)
 $manager.CommitChanges();$manager.Dispose()
 & icacls.exe C:\downloads\wallhaven-api-key.txt /grant 'IIS AppPool\WallpaperGallery:(R)' /Q|Out-Null
 if($LASTEXITCODE -ne 0){throw 'Key read permission failed'}
 @{status='complete';applicationInitialization=$true;alwaysRunning=$true;idleTimeout=0;preload=$true}|ConvertTo-Json|Set-Content $resultPath
}catch{@{status='failed';error=$_.Exception.Message}|ConvertTo-Json|Set-Content $resultPath;exit 1}
