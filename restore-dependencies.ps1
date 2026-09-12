$ErrorActionPreference = 'Stop'
$toolsDir = Join-Path $PSScriptRoot 'output\tools'
$packagesDir = Join-Path $PSScriptRoot 'output\packages'
[IO.Directory]::CreateDirectory($toolsDir) | Out-Null
$nugetPath = Join-Path $toolsDir 'nuget.exe'
if (!(Test-Path -LiteralPath $nugetPath)) {
    Invoke-WebRequest -UseBasicParsing 'https://dist.nuget.org/win-x86-commandline/v6.14.0/nuget.exe' -OutFile $nugetPath
}
& $nugetPath install SIPSorcery -Version 10.0.16 -Framework net48 -OutputDirectory $packagesDir -Source 'https://api.nuget.org/v3/index.json' -NonInteractive -Verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Native P2P dependency restore failed' }
