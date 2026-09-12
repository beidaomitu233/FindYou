param(
    [Parameter(Mandatory = $true)][string]$CertificateThumbprint,
    [string]$FilePath = (Join-Path $PSScriptRoot 'FindYou.exe'),
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [string]$SignToolPath = 'signtool.exe'
)
$ErrorActionPreference = 'Stop'
$target = (Resolve-Path -LiteralPath $FilePath).Path
$thumbprint = $CertificateThumbprint.Replace(' ', '')
if ($thumbprint -notmatch '^[0-9a-fA-F]{40}$') { throw 'A certificate thumbprint is required.' }
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint"
if (!$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'The certificate needs an accessible private key and must not be expired.' }
if (!(@($certificate.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId.Value }) -contains '1.3.6.1.5.5.7.3.3')) { throw 'The certificate must support code signing.' }
$chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
try {
    if (!$chain.Build($certificate)) { throw 'The certificate does not chain to a trusted authority.' }
} finally { $chain.Dispose() }
$signTool = (Get-Command $SignToolPath -ErrorAction Stop).Source
& $signTool sign /sha1 $thumbprint /s My /fd SHA256 /tr $TimestampServer /td SHA256 $target
if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
& $signTool verify /pa /all $target
if ($LASTEXITCODE -ne 0) { throw 'Signature verification failed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $target
if ($signature.Status -ne 'Valid' -or !$signature.TimeStamperCertificate) { throw 'A valid, timestamped signature is required.' }
Get-FileHash -LiteralPath $target -Algorithm SHA256
Write-Output 'Signature verified. Distribute this file without rebuilding or modifying it.'
