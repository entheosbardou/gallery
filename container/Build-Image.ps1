$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$base = 'C:\downloads\gallery'
$installer = 'C:\python-installer.exe'
$url = "https://www.python.org/ftp/python/$env:PYTHON_VERSION/python-$env:PYTHON_VERSION-amd64.exe"
Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing
$install = Start-Process -FilePath $installer -ArgumentList '/quiet InstallAllUsers=1 TargetDir=C:\Python314 Include_pip=1 Include_test=0 PrependPath=0' -Wait -PassThru
if ($install.ExitCode -ne 0) { throw "Python installation failed: $($install.ExitCode)" }
Remove-Item -LiteralPath $installer
& 'C:\Python314\python.exe' -m pip install --disable-pip-version-check --no-cache-dir --target (Join-Path $base 'iis-helper\python-deps') -r (Join-Path $base 'requirements-worker.txt')
if ($LASTEXITCODE -ne 0) { throw 'Worker dependency installation failed.' }
& (Join-Path $base 'iis-source\Build-IISGallery.ps1')
if ($LASTEXITCODE -ne 0) { throw 'IIS gallery compilation failed.' }
& 'C:\Python314\python.exe' -m py_compile (Join-Path $base 'iis-helper\DatabaseWorker.py') (Join-Path $base 'iis-helper\FileManagerWorker.py') (Join-Path $base 'iis-helper\DownloaderLocations.py')
if ($LASTEXITCODE -ne 0) { throw 'Worker syntax check failed.' }
New-Item -ItemType Directory -Path (Join-Path $base 'iis-data') -Force | Out-Null

