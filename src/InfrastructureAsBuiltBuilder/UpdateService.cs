using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace IABB.App
{
    internal sealed class ReleaseInfo
    {
        public string Tag;
        public Version Version;
        public string ZipName;
        public string ZipUrl;
        public string ShaUrl;
        public string PageUrl;
    }

    /// <summary>
    /// Update button: reads the latest GitHub Release, downloads its ZIP and the matching .sha256 file, verifies the hash,
    /// and hands over to IABB.Updater.exe, which replaces the files once this app has closed. Uses the system proxy.
    /// </summary>
    internal static class UpdateService
    {
        internal const string DefaultRepository = "CPSDomain/ServerDocumentationTool";
        internal const string AssetPrefix = "InfrastructureAsBuiltBuilder_";

        internal static string ReleasesPage(string repo)
        {
            return "https://github.com/" + repo + "/releases";
        }

        private static HttpWebRequest NewRequest(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "InfrastructureAsBuiltBuilder/" + AppInfo.Version.ToString(3);
            req.Accept = "application/vnd.github+json, application/octet-stream, */*";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            IWebProxy proxy = WebRequest.DefaultWebProxy;
            if (proxy != null) proxy.Credentials = CredentialCache.DefaultCredentials;
            req.Proxy = proxy;
            return req;
        }

        /// <summary>Returns the latest release, or null when the repository has no releases yet.</summary>
        internal static ReleaseInfo GetLatestRelease(string repo)
        {
            string url = "https://api.github.com/repos/" + repo + "/releases/latest";
            string json;
            try
            {
                using (var resp = (HttpWebResponse)NewRequest(url, 20000).GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }
            }
            catch (WebException wex)
            {
                var r = wex.Response as HttpWebResponse;
                if (r != null && r.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }

            var data = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (data == null) throw new InvalidOperationException("Unexpected response from GitHub.");
            string tag = Get(data, "tag_name");
            Version v;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out v)) throw new InvalidOperationException("Latest release tag '" + tag + "' is not a version number.");

            var info = new ReleaseInfo { Tag = tag, Version = Normalize(v), PageUrl = Get(data, "html_url") };
            if (string.IsNullOrEmpty(info.PageUrl)) info.PageUrl = ReleasesPage(repo);
            object assetsObj;
            var assets = data.TryGetValue("assets", out assetsObj) ? assetsObj as IEnumerable : null;
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (assets != null)
            {
                foreach (object a in assets)
                {
                    var ad = a as Dictionary<string, object>;
                    if (ad == null) continue;
                    string name = Get(ad, "name");
                    if (name.Length > 0) byName[name] = Get(ad, "browser_download_url");
                }
            }
            foreach (var kv in byName)
            {
                if (kv.Key.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) && kv.Key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    info.ZipName = kv.Key;
                    info.ZipUrl = kv.Value;
                    string sha;
                    if (byName.TryGetValue(kv.Key + ".sha256", out sha)) info.ShaUrl = sha;
                    break;
                }
            }
            if (info.ZipUrl == null) throw new InvalidOperationException("Release " + tag + " has no " + AssetPrefix + "*.zip file.");
            if (info.ShaUrl == null) throw new InvalidOperationException("Release " + tag + " has no .sha256 checksum file, so it cannot be verified.");
            return info;
        }

        internal static bool IsNewer(ReleaseInfo release)
        {
            return release != null && release.Version > AppInfo.Version;
        }

        /// <summary>Downloads the release ZIP and its checksum to a new temp folder and verifies SHA256. Returns the ZIP path.</summary>
        internal static string DownloadAndVerify(ReleaseInfo release)
        {
            string dir = Path.Combine(Path.GetTempPath(), "IABB_Update_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, release.ZipName);
            Download(release.ZipUrl, zip);
            string shaFile = zip + ".sha256";
            Download(release.ShaUrl, shaFile);

            string expected = File.ReadAllText(shaFile).Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
            string actual;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(zip))
            {
                actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", string.Empty);
            }
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zip); } catch { }
                throw new InvalidOperationException("The downloaded file failed its SHA256 check (expected " + expected + ", got " + actual + "). Nothing was changed.");
            }
            return zip;
        }

        private static void Download(string url, string path)
        {
            using (var resp = NewRequest(url, 120000).GetResponse())
            using (var input = resp.GetResponseStream())
            using (var output = File.Create(path))
            {
                input.CopyTo(output);
            }
        }

        /// <summary>Copies the updater to the temp folder (so it can replace its own file) and starts it.</summary>
        internal static void LaunchUpdater(string zipPath, string appDirectory, string exePath)
        {
            string source = Path.Combine(appDirectory, "IABB.Updater.exe");
            if (!File.Exists(source)) throw new FileNotFoundException("IABB.Updater.exe is missing from the application folder.", source);
            string tempDir = Path.GetDirectoryName(zipPath);
            string updater = Path.Combine(tempDir, "IABB.Updater.exe");
            File.Copy(source, updater, true);
            string cfg = source + ".config";
            if (File.Exists(cfg)) File.Copy(cfg, updater + ".config", true);

            string args = "--pid " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                + " --zip " + Quote(zipPath)
                + " --target " + Quote(appDirectory.TrimEnd('\\'))
                + " --start " + Quote(exePath);
            Process.Start(new ProcessStartInfo(updater, args) { UseShellExecute = true, WorkingDirectory = tempDir });
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\"", string.Empty) + "\"";
        }

        private static string Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : string.Empty;
        }

        private static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }
}
