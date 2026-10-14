$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
 New-Item -ItemType Directory -Path dist -Force | Out-Null
 & "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /target:exe /utf8output /out:dist\ReFixTests.exe /r:System.dll /r:System.Core.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /resource:src\InstallProfiles.json,ReFix.InstallProfiles.json src\SelfUpdate.cs src\GameFonts.cs src\Installation.cs src\Core.cs tests\Tests.cs
 if ($LASTEXITCODE -ne 0) { throw 'Tests compilation failed' }
 & .\dist\ReFixTests.exe
 if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
} finally { Pop-Location }
