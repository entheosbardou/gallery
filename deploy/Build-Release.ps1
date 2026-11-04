param([string]$Output = (Join-Path (Split-Path $PSScriptRoot) '.docker-stage\release'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $root '.docker-stage'))
$Output = [IO.Path]::GetFullPath($Output)
if (-not $Output.StartsWith($stagingRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Release output must be inside .docker-stage.' }
& (Join-Path $root 'iis-source\Build-IISGallery.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Gallery compilation failed.' }
$python = Get-Command python -ErrorAction Stop
& $python.Source -c "import pathlib; [compile(p.read_bytes(), str(p), 'exec') for p in pathlib.Path(r'$root\iis-helper').glob('*.py')]"
if ($LASTEXITCODE -ne 0) { throw 'Worker syntax check failed.' }
if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
foreach ($folder in @('iis-source','iis-helper','iis-site')) {
    Copy-Item -LiteralPath (Join-Path $root $folder) -Destination (Join-Path $Output $folder) -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $root 'requirements-worker.txt') -Destination $Output
Get-ChildItem -LiteralPath $Output -Directory -Filter __pycache__ -Recurse | Remove-Item -Recurse -Force
Write-Output "Release prepared: $Output"

