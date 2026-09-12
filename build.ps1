param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'FindYou.exe')
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compilerPath)) { $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$packagesDir = Join-Path $PSScriptRoot 'output\packages'
if (!(Test-Path (Join-Path $packagesDir 'SIPSorcery.10.0.16'))) { throw 'Run restore-dependencies.ps1 first.' }
$resources = @('/resource:FindYou.Native.xaml,FindYou.Native.xaml')
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll', '/r:System.Xaml.dll')
$wpf = Join-Path (Split-Path $compilerPath) 'WPF'
foreach ($name in @('WindowsBase', 'PresentationCore', 'PresentationFramework', 'WindowsFormsIntegration')) { $references += "/r:$(Join-Path $wpf ($name + '.dll'))" }
foreach ($package in Get-ChildItem -LiteralPath $packagesDir -Directory) {
    $selected = $null
    foreach ($framework in @('net48','net472','net47','net463','net462','net461','net46','net452','net45','net40','net35','netstandard2.0')) {
        $candidate = Join-Path $package.FullName "lib\$framework"
        if (Test-Path -LiteralPath $candidate) { $dlls = @(Get-ChildItem -LiteralPath $candidate -Filter '*.dll'); if ($dlls.Count) { $selected = $dlls; break } }
    }
    foreach ($dll in $selected) {
        if ($dll.Name -in @(
            'SIPSorcery.dll','SIPSorceryMedia.Abstractions.dll','BouncyCastle.Cryptography.dll','DnsClient.dll',
            'Microsoft.Bcl.HashCode.dll','Microsoft.Extensions.Logging.Abstractions.dll','System.Memory.dll','System.Buffers.dll',
            'System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Threading.Tasks.Extensions.dll')) {
            $resources += "/resource:$($dll.FullName),FindYou.Runtime.$($dll.Name)"
            $references += "/r:$($dll.FullName)"
        }
    }
}
$netstandard = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\netstandard') -Recurse -Filter netstandard.dll | Select-Object -First 1
if (!$netstandard) { throw '.NET Framework 4.8 is required.' }
$references += "/r:$($netstandard.FullName)"
& $compilerPath /nologo /target:winexe /optimize+ /platform:x64 "/out:$OutputPath" $references $resources FindYou.cs FindYou.Workspace.cs FindYou.Direct.cs FindYou.Native.cs FindYou.Rtc.cs FindYou.Window.cs FindYou.Update.cs FindYou.Speed.cs
if ($LASTEXITCODE -ne 0) { throw 'FindYou build failed' }
Write-Output "Built $OutputPath (native WPF UI and managed P2P; no browser runtime)"
