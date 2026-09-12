param([string]$Exe = (Join-Path $PSScriptRoot 'FindYou.Native.exe'), [switch]$NoUi)
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('findyou-memory-' + [Guid]::NewGuid().ToString('N'))
$token = 'd' * 32
$port = 54168
$before = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in @('msedge', 'msedgewebview2') } | Select-Object -ExpandProperty Id)
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
[IO.File]::WriteAllText((Join-Path $testRoot 'config.txt'), "FindYouConfig2`ncloud=0`nrelay=`nfw=1", [Text.UTF8Encoding]::new($false))
$process = $null
try {
    $arguments = @("/data=$testRoot", "/port=$port", "/udp=$($port + 1)", "/apitoken=$token")
    if ($NoUi) { $arguments += '/noui' }
    $process = Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList $arguments -PassThru -WindowStyle Minimized
    $origin = "http://127.0.0.1:$port"
    $ready = $false
    for ($i = 0; $i -lt 100 -and !$ready; $i++) {
        Start-Sleep -Milliseconds 100
        try { Invoke-RestMethod -Uri "$origin/api/info" -TimeoutSec 1 | Out-Null; $ready = $true } catch { }
    }
    if (!$ready) { throw 'FindYou did not start' }
    Start-Sleep -Seconds 3
    $logPath = Join-Path $testRoot 'findyou.log'
    if (!$NoUi -and (Test-Path $logPath) -and [IO.File]::ReadAllText($logPath).Contains('打开界面失败')) { throw 'Native window failed to initialize' }
    $sample = Get-Process -Id $process.Id
    $after = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in @('msedge', 'msedgewebview2') -and $_.Id -notin $before } | Select-Object -ExpandProperty Id)
    Invoke-RestMethod -Method Post -Uri "$origin/api/local/quit?k=$token" | Out-Null
    if (!$process.WaitForExit(10000)) { throw 'FindYou did not exit within 10 seconds' }
    [pscustomobject]@{
        WorkingSetMiB = [math]::Round($sample.WorkingSet64 / 1MB, 1)
        PrivateMiB = [math]::Round($sample.PrivateMemorySize64 / 1MB, 1)
        NewBrowserProcesses = $after.Count
        ExitCode = $process.ExitCode
        ExitedCleanly = $process.HasExited
    } | ConvertTo-Json
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
