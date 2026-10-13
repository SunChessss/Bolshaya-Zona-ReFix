$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
$output = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location $projectRoot
try {
 & $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /out:dist\ReFixLauncher.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll src\Core.cs src\Program.cs
 if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed' }
 Copy-Item src\ReFixLauncher.exe.config dist
} finally { Pop-Location }
Write-Output "Built: $output/ReFixLauncher.exe"
