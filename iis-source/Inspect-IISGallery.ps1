$ErrorActionPreference='Stop'
$result=@{}
try {
    Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
    $manager=New-Object Microsoft.Web.Administration.ServerManager
    $site=$manager.Sites['Wallpaper Gallery']
    $config=$manager.GetApplicationHostConfiguration()
    $result.siteState=$site.State.ToString()
    $result.poolState=$manager.ApplicationPools['WallpaperGallery'].State.ToString()
    $result.bindings=@($site.Bindings | ForEach-Object {$_.BindingInformation})
    $result.authentication=$config.GetSection('system.webServer/security/authentication/windowsAuthentication','Wallpaper Gallery').GetAttributeValue('enabled')
    $result.user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    try {$response=Invoke-WebRequest 'http://bens-laptop.thelinclan.com:8791/api/health' -UseDefaultCredentials -UseBasicParsing -TimeoutSec 15;$result.healthStatus=$response.StatusCode;$result.health=$response.Content} catch {$result.healthError=$_.Exception.Message}
    $result.helperTask=(Get-ScheduledTask -TaskName 'Wallpaper Gallery Browser Session').State.ToString()
    $logDirectory='C:\inetpub\logs\LogFiles\W3SVC'+$site.Id
    $log=Get-ChildItem $logDirectory -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if($log) {
        $fieldLine=(Get-Content $log.FullName | Where-Object {$_ -like '#Fields:*'} | Select-Object -Last 1)
        $fields=$fieldLine.Substring(9) -split ' '
        $result.recentRequests=@(Get-Content $log.FullName -Tail 12 | Where-Object {$_ -notlike '#*'} | ForEach-Object {
            $parts=$_ -split ' ';$entry=@{}
            foreach($field in @('sc-status','sc-substatus','sc-win32-status','cs-uri-stem')) {$position=[Array]::IndexOf($fields,$field);if($position -ge 0) {$entry[$field]=$parts[$position]}}
            $entry
        })
    }
    $manager.Dispose()
} catch {$result.error=$_.Exception.Message}
$result | ConvertTo-Json -Depth 6 | Set-Content C:\downloads\gallery\iis-data\inspection.json -Encoding UTF8
