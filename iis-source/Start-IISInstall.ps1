$ErrorActionPreference='Stop'
try {
    $action=New-ScheduledTaskAction -Execute "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\downloads\gallery\iis-source\Install-IISGallery.ps1'
    $principal=New-ScheduledTaskPrincipal -UserId 'THELINCLAN\ben' -LogonType Interactive -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName 'Wallpaper Gallery IIS Setup' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName 'Wallpaper Gallery IIS Setup'
} catch {
    @{status='bootstrap-failed';error=$_.Exception.Message} | ConvertTo-Json | Set-Content C:\downloads\gallery\iis-data\deployment.json -Encoding UTF8
}
