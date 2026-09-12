$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../FindYou.Speed.cs') -Raw
Add-Type -TypeDefinition ($source + @'

public static class SpeedChecks {
    static void Check(bool ok, string message) { if(!ok) throw new System.Exception(message); }
    public static string Run() {
        double now=0;
        var meter=new FindYou.TransferSpeed(() => now);
        Check(meter.BytesPerSecond==0,"Empty meter");
        now=20; Check(meter.BytesPerSecond==0,"Preparation must not create traffic");
        for(int i=1;i<=400;i++) { now=20+i*.01; meter.Add(100000); if(i>=25) Check(System.Math.Abs(meter.BytesPerSecond-10000000)<500000,"Constant throughput distorted by startup"); }
        now=24.5; Check(meter.BytesPerSecond<6500000,"Stall did not reduce rate");
        now=25.2; Check(meter.BytesPerSecond==0,"Stalled transfer retained stale rate");
        for(int i=1;i<=200;i++) { now=30+i*.01; meter.Add(200000); }
        Check(System.Math.Abs(meter.BytesPerSecond-20000000)<500000,"Old slow samples diluted resumed throughput");
        now=34; Check(meter.BytesPerSecond==0,"Resume then stall");
        double fixedTime=0;
        var concurrent=new FindYou.TransferSpeed(() => fixedTime);
        System.Threading.Tasks.Parallel.For(0,8, i => { for(int j=0;j<1000;j++) { concurrent.Add(100); long rate=concurrent.BytesPerSecond; } });
        fixedTime=.5;
        Check(concurrent.BytesPerSecond==1600000,"Concurrent byte updates lost");
        var samples=typeof(FindYou.TransferSpeed).GetField("samples",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
        Check(((System.Collections.ICollection)samples.GetValue(meter)).Count<=12,"Unbounded sample retention");
        return "PASS startup delay, steady speed, stall decay, resume, concurrent updates, bounded samples";
    }
}
'@)
[SpeedChecks]::Run()
