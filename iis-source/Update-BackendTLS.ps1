$ErrorActionPreference='Stop'
$cert=New-SelfSignedCertificate -DnsName bens-laptop.thelinclan.com,wallpapers.thelinclan.com -FriendlyName 'Wallpaper Gallery TLS Backend' -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(2)
foreach($ipport in @('0.0.0.0:8792','[::]:8792')){& netsh.exe http update sslcert ('ipport='+$ipport) ('certhash='+$cert.Thumbprint) 'appid={EEBE9E3B-57C2-4F54-8241-332D903E8173}' certstorename=MY | Out-Null;if($LASTEXITCODE -ne 0){throw 'TLS update failed'}}
Export-Certificate -Cert $cert -FilePath C:\downloads\gallery\iis-data\gallery-backend.cer -Force | Out-Null
