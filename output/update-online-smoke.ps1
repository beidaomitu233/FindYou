param([string]$Exe = (Join-Path $PSScriptRoot 'FindYou.exe'))
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Exe).Path)
$type = $assembly.GetType('FindYou.AppUpdate',$true)
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
if ($null -ne $type.GetMethod('Check',$flags).Invoke($null,@())) { throw 'Published version should match installed version' }
$releaseType = $assembly.GetType('FindYou.UpdateRelease',$true)
$release = [Activator]::CreateInstance($releaseType,$true)
$releaseType.GetField('Version').SetValue($release, [Version]'5.1.0')
$releaseType.GetField('Url').SetValue($release,'https://github.com/beidaomitu233/FindYou/releases/download/v5.1.0/FindYou.exe')
$releaseType.GetField('HashUrl').SetValue($release,'https://github.com/beidaomitu233/FindYou/releases/download/v5.1.0/FindYou.exe.sha256')
$directory = Join-Path $PSScriptRoot 'update-validation'
Add-Type 'public static class UpdateTestProgress { public static void Report(int value) {} }'
$progress = [Delegate]::CreateDelegate([Action[int]], [UpdateTestProgress].GetMethod('Report'))
$result = $type.GetMethod('Download',$flags).Invoke($null,@($release,[string]$directory,$progress))
if ((Get-FileHash -LiteralPath $result).Hash -ne (Get-FileHash -LiteralPath $Exe).Hash) { throw 'Downloaded release differs from tested executable' }
'PASS real GitHub latest-version check, EXE download and SHA-256 equality'
