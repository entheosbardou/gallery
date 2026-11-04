$ErrorActionPreference='Stop'
$galleryPath=Split-Path $PSScriptRoot
$compiler=Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runtimePath=Join-Path $galleryPath 'iis-helper\runtime-lib'
if(!(Test-Path $runtimePath)){throw 'The shared IIS runtime dependencies are missing.'}
New-Item -ItemType Directory -Path (Join-Path $galleryPath 'iis-site\bin') -Force | Out-Null
Copy-Item (Join-Path $runtimePath '*.dll') (Join-Path $galleryPath 'iis-site\bin') -Force
$references=@('/r:System.Web.dll','/r:System.Web.Extensions.dll','/r:System.Configuration.dll','/r:System.Drawing.dll','/r:System.Data.dll','/r:System.Net.Http.dll')+@(Get-ChildItem $runtimePath -Filter *.dll|ForEach-Object {'/r:'+$_.FullName})
$sources=@('Gallery.cs','GalleryAuth.cs','GalleryAccess.cs','DuplicateScan.cs','FileReferences.cs','DuplicateCleanup.cs','RedditPosts.cs','CategoryFolders.cs','DownloadQueue.cs','ManualImages.cs','Database.cs','DatabasePaging.cs')|ForEach-Object {Join-Path $PSScriptRoot $_}
& $compiler /nologo /target:library ('/out:'+(Join-Path $galleryPath 'iis-site\bin\WallpaperGallery.dll')) $references $sources
if($LASTEXITCODE -ne 0){throw 'Gallery build failed.'}
& $compiler /nologo /target:winexe ('/out:'+(Join-Path $galleryPath 'iis-helper\CookieBridge.exe')) /r:System.Web.Extensions.dll /r:System.Security.dll (Join-Path $PSScriptRoot 'CookieBridge.cs')
if($LASTEXITCODE -ne 0){throw 'Cookie helper build failed.'}
& $compiler /nologo /target:winexe ('/out:'+(Join-Path $galleryPath 'iis-helper\QueueGuardian.exe')) /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'QueueGuardian.cs')
if($LASTEXITCODE -ne 0){throw 'Queue guardian build failed.'}
