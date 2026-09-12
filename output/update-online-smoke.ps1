param([string]$Exe = (Join-Path $PSScriptRoot 'FindYou.exe'), [string]$PreviousExe)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Exe).Path)
$type = $assembly.GetType('FindYou.AppUpdate',$true)
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
if ($null -ne $type.GetMethod('Check',$flags).Invoke($null,@())) { throw 'Published version should match installed version' }
$releaseType = $assembly.GetType('FindYou.UpdateRelease',$true)
$release = [Activator]::CreateInstance($releaseType,$true)
$version = $type.GetField('Current',$flags).GetValue($null)
$latest = Invoke-RestMethod 'https://api.github.com/repos/beidaomitu233/FindYou/releases/latest' -Headers @{'User-Agent'='FindYou-release-validation'}
if ($latest.tag_name -ne ('v'+$version)) { throw 'Latest public release does not match executable' }
$releaseType.GetField('Version').SetValue($release, $version)
$releaseType.GetField('Url').SetValue($release,('https://github.com/beidaomitu233/FindYou/releases/download/v'+$version+'/FindYou.exe'))
$releaseType.GetField('HashUrl').SetValue($release,('https://github.com/beidaomitu233/FindYou/releases/download/v'+$version+'/FindYou.exe.sha256'))
if ($PreviousExe) {
    $oldAssembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $PreviousExe).Path)
    $oldUpdate = $oldAssembly.GetType('FindYou.AppUpdate',$true).GetMethod('Check',$flags).Invoke($null,@())
    if ($null -eq $oldUpdate -or $oldUpdate.Version -ne $version) { throw 'Previous version cannot discover update' }
}
$directory = Join-Path $PSScriptRoot 'update-validation'
Add-Type 'public static class UpdateTestProgress { public static void Report(int value) {} }'
$progress = [Delegate]::CreateDelegate([Action[int]], [UpdateTestProgress].GetMethod('Report'))
$result = $type.GetMethod('Download',$flags).Invoke($null,@($release,[string]$directory,$progress))
if ((Get-FileHash -LiteralPath $result).Hash -ne (Get-FileHash -LiteralPath $Exe).Hash) { throw 'Downloaded release differs from tested executable' }
'PASS real GitHub latest-version check, EXE download and SHA-256 equality'
