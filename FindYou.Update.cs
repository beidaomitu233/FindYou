using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Text.RegularExpressions;
[assembly: AssemblyVersion("5.1.0.0")]
[assembly: AssemblyFileVersion("5.1.0.0")]
[assembly: AssemblyProduct("FindYou")]
namespace FindYou
{
    sealed class UpdateRelease { public Version Version; public string Url, HashUrl; }
    static class AppUpdate
    {
        public const string Repository = "beidaomitu233/FindYou";
        public static readonly Version Current = new Version(5,1,0);
        internal static UpdateRelease Parse(string json)
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            if (Convert.ToBoolean(data["draft"]) || Convert.ToBoolean(data["prerelease"])) return null;
            string tag = Convert.ToString(data["tag_name"]); Version version;
            if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag.Substring(1), out version)) throw new IOException("版本号无效");
            if (version <= Current) return null;
            var release = new UpdateRelease { Version = version };
            foreach (Dictionary<string, object> asset in (System.Collections.IEnumerable)data["assets"])
            {
                string name = Convert.ToString(asset["name"]), url = Convert.ToString(asset["browser_download_url"]);
                if (name != "FindYou.exe" && name != "FindYou.exe.sha256") continue;
                if (url != "https://github.com/" + Repository + "/releases/download/" + tag + "/" + name) throw new IOException("更新地址无效");
                if (name == "FindYou.exe") release.Url = url; else release.HashUrl = url;
            }
            if (release.Url == null || release.HashUrl == null) throw new IOException("更新包尚未发布完整");
            return release;
        }
        static HttpWebResponse Open(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "FindYou/" + Current; request.Timeout = 20000; request.ReadWriteTimeout = 30000;
            var response = (HttpWebResponse)request.GetResponse();
            if (response.ResponseUri.Scheme != "https") { response.Dispose(); throw new IOException("更新需要 HTTPS"); }
            return response;
        }
        static string Read(string url, int limit)
        {
            using (var response = Open(url)) using (var reader = new StreamReader(response.GetResponseStream()))
            {
                char[] buffer = new char[limit + 1]; int count = 0, n;
                while (count < buffer.Length && (n = reader.Read(buffer, count, buffer.Length-count)) > 0) count += n;
                if (count > limit) throw new IOException("更新响应过大");
                return new string(buffer,0,count);
            }
        }
        public static UpdateRelease Check()
        {
            try { return Parse(Read("https://api.github.com/repos/" + Repository + "/releases/latest", 1048576)); }
            catch (WebException ex) { if (ex.Response != null) ex.Response.Dispose(); throw new IOException("检查失败，请检查网络或稍后重试"); }
        }
        internal static void Verify(string path, string hash)
        {
            if (!Regex.IsMatch(hash,"^[0-9a-fA-F]{64}$")) throw new IOException("校验信息无效");
            using (var sha = SHA256.Create()) using (var file = File.OpenRead(path))
                if (!string.Equals(BitConverter.ToString(sha.ComputeHash(file)).Replace("-",""),hash,StringComparison.OrdinalIgnoreCase)) throw new IOException("更新校验失败");
        }
        public static string Download(UpdateRelease release, string directory, Action<int> progress)
        {
            string hash = Read(release.HashUrl,1024).Trim();
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory,"FindYou-"+release.Version+".exe"), partial = Path.Combine(directory,".findyou-update-"+Guid.NewGuid().ToString("N")+".part");
            try
            {
                using (var response = Open(release.Url)) using (var input = response.GetResponseStream()) using (var output = new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    byte[] buffer = new byte[131072]; long total = 0; int n;
                    while ((n=input.Read(buffer,0,buffer.Length))>0)
                    {
                        total += n; if (total > 268435456) throw new IOException("更新包过大");
                        output.Write(buffer,0,n); progress(response.ContentLength>0 ? (int)Math.Min(99,total*100/response.ContentLength):0);
                    }
                    if (response.ContentLength>=0 && total!=response.ContentLength) throw new IOException("下载不完整");
                }
                Verify(partial,hash);
                if (File.Exists(target)) Verify(target,hash); else File.Move(partial,target);
                return target;
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
    }
}
