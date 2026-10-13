param([Parameter(Mandatory=$true)][string]$GameRoot)
$ErrorActionPreference = 'Stop'
# This test installs the published patch and cycles all add-ons in a disposable test copy.
$testRoot = (Resolve-Path -LiteralPath $GameRoot).Path
Push-Location $PSScriptRoot
try {
 New-Item -ItemType Directory -Path dist -Force | Out-Null
 & "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /target:exe /utf8output /out:dist\ReleaseSmoke.exe /r:System.dll /r:System.Core.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /resource:src\InstallProfiles.json,ReFix.InstallProfiles.json src\GameFonts.cs src\Installation.cs src\Core.cs tests\ReleaseSmoke.cs
 if ($LASTEXITCODE -ne 0) { throw 'Release test compilation failed' }
 & .\dist\ReleaseSmoke.exe $testRoot
 if ($LASTEXITCODE -ne 0) { throw 'Release test failed' }
} finally { Pop-Location }
