param([string]$Exe = (Join-Path $PSScriptRoot 'FindYou.Performance.exe'), [switch]$RequireNoExpect)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
public static class HttpStartupProbe {
    public static string Run(string path, bool requireNoExpect) {
        var asm = Assembly.LoadFile(path);
        var peer = asm.GetType("FindYou.PeerClient").GetMethod("Req");
        var native = asm.GetType("FindYou.NativeWorkspace").GetMethod("Request", BindingFlags.NonPublic | BindingFlags.Static);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var expect = new bool[3]; var delays = new double[3];
        var server = Task.Run(() => {
            using (var client = listener.AcceptTcpClient()) {
                client.ReceiveTimeout = 8000; client.NoDelay = true;
                using (var stream = client.GetStream()) for (int i=0; i<3; i++) {
                    string header = "";
                    while (!header.EndsWith("\r\n\r\n")) { int b=stream.ReadByte(); if(b<0) throw new IOException("Connection not reused"); header+=(char)b; }
                    expect[i] = header.IndexOf("Expect: 100-continue", StringComparison.OrdinalIgnoreCase)>=0;
                    int length=0;
                    foreach(string line in header.Split('\n')) if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)) length=int.Parse(line.Substring(15).Trim());
                    var clock=Stopwatch.StartNew();
                    for(int j=0;j<length;j++) if(stream.ReadByte()<0) throw new IOException("Short body");
                    delays[i]=clock.Elapsed.TotalMilliseconds;
                    byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nOK");
                    stream.Write(response,0,response.Length);
                }
            }
        });
        try {
            string url="http://127.0.0.1:"+port;
            for(int i=0;i<3;i++) {
                var r=(HttpWebRequest)(i==2 ? native.Invoke(null,new object[]{url+"/stream",(long)16}) : peer.Invoke(null,new object[]{url+(i==0?"/info":"/signal")}));
                r.Timeout=8000; r.ReadWriteTimeout=8000;
                if(i>0) { r.Method="POST"; r.ContentLength=16; using(var output=r.GetRequestStream()) output.Write(new byte[16],0,16); }
                using(var response=r.GetResponse()) using(var reader=new StreamReader(response.GetResponseStream())) reader.ReadToEnd();
            }
            server.GetAwaiter().GetResult();
            if(requireNoExpect && (expect[1] || expect[2])) throw new Exception("Unexpected 100-continue handshake");
            return "{\"version\":\""+asm.GetName().Version+"\",\"connections\":1,\"signalExpect\":"+expect[1].ToString().ToLowerInvariant()+",\"signalBodyWaitMs\":"+delays[1].ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)+",\"streamExpect\":"+expect[2].ToString().ToLowerInvariant()+",\"streamBodyWaitMs\":"+delays[2].ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)+"}";
        } finally { listener.Stop(); }
    }
}
'@
[HttpStartupProbe]::Run((Resolve-Path -LiteralPath $Exe).Path, $RequireNoExpect.IsPresent)
