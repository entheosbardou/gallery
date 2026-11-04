$ErrorActionPreference='Stop'
$resultPath='C:\downloads\gallery\iis-data\ip-binding.json'
try {
 Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
 $manager=New-Object Microsoft.Web.Administration.ServerManager
 $site=$manager.Sites['Wallpaper Gallery']
 if(!$site -or $site.Applications['/'].VirtualDirectories['/'].PhysicalPath -ne 'C:\downloads\gallery\iis-site'){throw 'Gallery site not found at expected path'}
 $binding='*:8791:'
 foreach($other in $manager.Sites){if($other.Name -ne $site.Name -and @($other.Bindings|Where-Object {$_.Protocol -eq 'http' -and $_.BindingInformation -eq $binding}).Count){throw 'Another site already owns this port without a hostname'}}
 $before=@($site.Bindings|ForEach-Object {$_.BindingInformation})
 $backup='C:\downloads\gallery\iis-data\bindings-before-ip-'+(Get-Date -Format yyyyMMdd-HHmmss)+'.json'
 @{site=$site.Name;bindings=@($site.Bindings|ForEach-Object {@{protocol=$_.Protocol;binding=$_.BindingInformation}})}|ConvertTo-Json -Depth 5|Set-Content $backup
 if(!@($site.Bindings|Where-Object {$_.Protocol -eq 'http' -and $_.BindingInformation -eq $binding}).Count){$site.Bindings.Add($binding,'http')|Out-Null;$manager.CommitChanges()}
 $manager.Dispose()
 $manager=New-Object Microsoft.Web.Administration.ServerManager
 @{status='complete';before=$before;bindings=@($manager.Sites['Wallpaper Gallery'].Bindings|ForEach-Object {$_.BindingInformation});backup=$backup}|ConvertTo-Json -Depth 5|Set-Content $resultPath
 $manager.Dispose()
}catch{@{status='failed';error=$_.Exception.Message}|ConvertTo-Json|Set-Content $resultPath;exit 1}
