$ErrorActionPreference='Stop'
Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
$manager=New-Object Microsoft.Web.Administration.ServerManager
$site=$manager.Sites['Wallpaper Gallery']
if(!$site -or $site.Applications['/'].VirtualDirectories['/'].PhysicalPath -ne 'C:\downloads\gallery\iis-site'){throw 'Unexpected site'}
if(!@($site.Bindings|Where-Object {$_.Protocol -eq 'https' -and $_.BindingInformation -eq '*:8792:'}).Count){$site.Bindings.Add('*:8792:','https')|Out-Null;$manager.CommitChanges()}
$cert=Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq 'CN=bens-laptop.thelinclan.com' | Sort-Object NotBefore -Descending | Select-Object -First 1
if(!$cert){$cert=New-SelfSignedCertificate -DnsName bens-laptop.thelinclan.com -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(2)}
foreach($ipport in @('0.0.0.0:8792','[::]:8792')){
 & netsh.exe http show sslcert ('ipport='+$ipport) | Out-Null
 if($LASTEXITCODE -ne 0){& netsh.exe http add sslcert ('ipport='+$ipport) ('certhash='+$cert.Thumbprint) 'appid={EEBE9E3B-57C2-4F54-8241-332D903E8173}' certstorename=MY | Out-Null;if($LASTEXITCODE -ne 0){throw 'TLS binding failed'}}
}
Export-Certificate -Cert $cert -FilePath C:\downloads\gallery\iis-data\gallery-backend.cer | Out-Null
if(!(Get-NetFirewallRule -DisplayName 'Wallpaper Gallery TLS Backend' -ErrorAction SilentlyContinue)){New-NetFirewallRule -DisplayName 'Wallpaper Gallery TLS Backend' -Direction Inbound -Protocol TCP -LocalPort 8792 -Action Allow -Profile Domain,Private | Out-Null}
$manager.Dispose()
