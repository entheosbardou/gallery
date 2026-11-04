$ErrorActionPreference='Stop'
$resultPath='C:\downloads\gallery\iis-data\anonymous-access.json'
try {
 Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
 $manager=New-Object Microsoft.Web.Administration.ServerManager
 $site=$manager.Sites['Wallpaper Gallery']
 if(!$site -or $site.Applications['/'].VirtualDirectories['/'].PhysicalPath -ne 'C:\downloads\gallery\iis-site'){throw 'Unexpected gallery site path'}
 $config=$manager.GetApplicationHostConfiguration()
 $anonymous=$config.GetSection('system.webServer/security/authentication/anonymousAuthentication','Wallpaper Gallery')
 $windows=$config.GetSection('system.webServer/security/authentication/windowsAuthentication','Wallpaper Gallery')
 $authorization=$config.GetSection('system.webServer/security/authorization','Wallpaper Gallery').GetCollection()
 $before=@{anonymous=$anonymous.GetAttributeValue('enabled');anonymousUser=$anonymous.GetAttributeValue('userName');windows=$windows.GetAttributeValue('enabled');authorization=@($authorization|ForEach-Object {@{accessType=$_.GetAttributeValue('accessType').ToString();users=$_.GetAttributeValue('users');roles=$_.GetAttributeValue('roles');verbs=$_.GetAttributeValue('verbs')}})}
 $backupName='WallpaperGallery-Anonymous-'+(Get-Date -Format yyyyMMdd-HHmmss)
 & "$env:windir\System32\inetsrv\appcmd.exe" add backup $backupName | Out-Null
 if($LASTEXITCODE -ne 0){throw 'IIS configuration backup failed'}
 $anonymous.SetAttributeValue('enabled',$true)
 $anonymous.SetAttributeValue('userName','')
 $windows.SetAttributeValue('enabled',$false)
 $authorization.Clear()
 $allow=$authorization.CreateElement('add');$allow.SetAttributeValue('accessType','Allow');$allow.SetAttributeValue('users','*');$authorization.Add($allow)
 $manager.CommitChanges();$manager.Dispose()
 $manager=New-Object Microsoft.Web.Administration.ServerManager
 $config=$manager.GetApplicationHostConfiguration()
 $result=@{status='complete';site='Wallpaper Gallery';backup=$backupName;before=$before;anonymous=$config.GetSection('system.webServer/security/authentication/anonymousAuthentication','Wallpaper Gallery').GetAttributeValue('enabled');anonymousUser=$config.GetSection('system.webServer/security/authentication/anonymousAuthentication','Wallpaper Gallery').GetAttributeValue('userName');windows=$config.GetSection('system.webServer/security/authentication/windowsAuthentication','Wallpaper Gallery').GetAttributeValue('enabled');bindings=@($manager.Sites['Wallpaper Gallery'].Bindings|ForEach-Object {$_.BindingInformation})}
 $manager.Dispose()
 $result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $resultPath -Encoding UTF8
}catch{@{status='failed';error=$_.Exception.Message}|ConvertTo-Json|Set-Content -LiteralPath $resultPath -Encoding UTF8;exit 1}
