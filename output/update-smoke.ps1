param([string]$Exe = (Join-Path $PSScriptRoot 'FindYou.exe'))
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Exe).Path)
$type = $assembly.GetType('FindYou.AppUpdate', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$parse = $type.GetMethod('Parse',$flags)
function Parse-Release($version, $assets = @(), $preview = $false) {
    $json = @{tag_name=$version; assets=$assets; draft=$false; prerelease=$preview} | ConvertTo-Json -Depth 6 -Compress
    return $parse.Invoke($null,@([string]$json))
}
if ($null -ne (Parse-Release 'v5.1.0')) { throw 'Current version offered' }
if ($null -ne (Parse-Release 'v5.0.0')) { throw 'Downgrade offered' }
if ($null -ne (Parse-Release 'v6.0.0' @() $true)) { throw 'Preview offered' }
$assets = @('FindYou.exe','FindYou.exe.sha256') | ForEach-Object { @{name=$_; browser_download_url=('https://github.com/beidaomitu233/FindYou/releases/download/v5.2.0/'+$_)} }
if ((Parse-Release 'v5.2.0' $assets).Version.ToString() -ne '5.2.0') { throw 'New version missing' }
$assets[0].browser_download_url = 'https://example.com/FindYou.exe'
$rejected = $false
try { Parse-Release 'v5.2.0' $assets } catch { $rejected = $true }
if (!$rejected) { throw 'Unexpected host accepted' }
$rejected = $false
try { Parse-Release 'v5.2.0' } catch { $rejected = $true }
if (!$rejected) { throw 'Incomplete release accepted' }
$verify = $type.GetMethod('Verify',$flags)
$path = (Resolve-Path -LiteralPath $Exe).Path
$hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
$verify.Invoke($null,@([string]$path,[string]$hash))
$rejected = $false
try { $verify.Invoke($null,@($path,('0'*64))) } catch { $rejected = $true }
if (!$rejected) { throw 'Corrupted package accepted' }
'PASS current/old/new/preview versions, host restriction, incomplete release, SHA-256 validation'
