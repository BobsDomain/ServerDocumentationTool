using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool
{
    internal sealed class ReleaseInfo
    {
        public string Tag;
        public Version Version;
        public string ExeUrl;
        public string ShaUrl;
        public string PageUrl;
    }

    /// <summary>
    /// Updates. A release has two files: ServerDocumentationTool.exe and ServerDocumentationTool.exe.sha256.
    /// The new exe is checked (SHA256, product name, version), the running exe is renamed to .old, the new one is copied
    /// in its place and started; the .old file is deleted on the next start. Settings in %ProgramData% are not touched.
    /// "Update from File..." does the same with the two files copied to the server (no internet needed).
    /// </summary>
    internal static class UpdateService
    {
        internal const string DefaultRepository = "bobsdomain/ServerDocumentationTool";
        private static readonly string[] RetiredRepositories = { "CPSDomain/ServerDocumentationTool" };

        internal static string ResolveRepository(string configured)
        {
            string repo = string.IsNullOrWhiteSpace(configured) ? DefaultRepository : configured.Trim().Trim('/');
            foreach (string old in RetiredRepositories)
                if (string.Equals(repo, old, StringComparison.OrdinalIgnoreCase)) return DefaultRepository;
            return repo;
        }

        internal static string ReleasesPage(string repo) { return "https://github.com/" + repo + "/releases"; }

        private static HttpWebRequest NewRequest(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "ServerDocumentationTool/" + AppInfo.Version.ToString(3);
            req.Accept = "application/vnd.github+json, application/octet-stream, */*";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            IWebProxy proxy = WebRequest.DefaultWebProxy;
            if (proxy != null) proxy.Credentials = CredentialCache.DefaultCredentials;
            req.Proxy = proxy;
            return req;
        }

        internal static ReleaseInfo GetLatestRelease(string repo)
        {
            string json;
            try
            {
                using (var resp = (HttpWebResponse)NewRequest("https://api.github.com/repos/" + repo + "/releases/latest", 20000).GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = reader.ReadToEnd();
            }
            catch (WebException wex)
            {
                var r = wex.Response as HttpWebResponse;
                if (r != null && r.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }
            var data = Json.Parse(json) as Dictionary<string, object>;
            if (data == null) throw new InvalidOperationException("Unexpected response from GitHub.");
            string tag = N.S(data, "tag_name");
            Version v;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out v)) throw new InvalidOperationException("Latest release tag '" + tag + "' is not a version number.");
            var info = new ReleaseInfo { Tag = tag, Version = Normalize(v), PageUrl = N.S(data, "html_url") };
            if (string.IsNullOrEmpty(info.PageUrl)) info.PageUrl = ReleasesPage(repo);
            foreach (object a in N.Items(data, "assets"))
            {
                string name = N.S(a, "name"), url = N.S(a, "browser_download_url");
                if (string.Equals(name, AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)) info.ExeUrl = url;
                else if (string.Equals(name, AppInfo.ExeName + ".sha256", StringComparison.OrdinalIgnoreCase)) info.ShaUrl = url;
            }
            if (info.ExeUrl == null) throw new InvalidOperationException("Release " + tag + " has no " + AppInfo.ExeName + " file.");
            if (info.ShaUrl == null) throw new InvalidOperationException("Release " + tag + " has no .sha256 checksum file, so it cannot be verified.");
            return info;
        }

        internal static bool IsNewer(ReleaseInfo release) { return release != null && release.Version > AppInfo.Version; }

        private static string NewStagingFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ServerDocumentationTool_Update_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Downloads the exe and its checksum, verifies them and returns the staged exe path.</summary>
        internal static string DownloadAndVerify(ReleaseInfo release, out Version version)
        {
            string dir = NewStagingFolder();
            string exe = Path.Combine(dir, AppInfo.ExeName);
            Download(release.ExeUrl, exe);
            Download(release.ShaUrl, exe + ".sha256");
            version = Verify(exe, exe + ".sha256");
            return exe;
        }

        /// <summary>Update from File: the chosen exe must have its .sha256 file next to it. Returns the staged, verified copy.</summary>
        internal static string PrepareLocalFile(string exePath, out Version version)
        {
            string sha = exePath + ".sha256";
            if (!File.Exists(sha))
                throw new FileNotFoundException("The checksum file " + Path.GetFileName(exePath) + ".sha256 must be in the same folder. Download both files from the Releases page and copy them together.", sha);
            string dir = NewStagingFolder();
            string exe = Path.Combine(dir, AppInfo.ExeName);
            File.Copy(exePath, exe, true);
            File.Copy(sha, exe + ".sha256", true);
            version = Verify(exe, exe + ".sha256");
            return exe;
        }

        private static Version Verify(string exe, string shaFile)
        {
            string[] parts = File.ReadAllText(shaFile).Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new InvalidOperationException("The checksum file is empty.");
            string actual = Sha256(exe);
            if (!string.Equals(parts[0], actual, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(exe); } catch { }
                throw new InvalidOperationException("The update file failed its SHA256 check (expected " + parts[0] + ", got " + actual + "). Nothing was changed.");
            }
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(exe);
            if (!string.Equals(fvi.ProductName, AppInfo.ProductName, StringComparison.Ordinal))
                throw new InvalidOperationException("The file is not the " + AppInfo.ProductName + " (product name: " + (fvi.ProductName ?? "none") + ").");
            Version v;
            if (!Version.TryParse(fvi.FileVersion ?? string.Empty, out v)) throw new InvalidOperationException("The file has no readable version number.");
            return Normalize(v);
        }

        internal static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void Download(string url, string path)
        {
            using (var resp = NewRequest(url, 120000).GetResponse())
            using (var input = resp.GetResponseStream())
            using (var output = File.Create(path))
                input.CopyTo(output);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFile(string path);

        /// <summary>Replaces the running exe with the staged one and starts it. The caller then closes the app.</summary>
        internal static void InstallAndRestart(string stagedExe)
        {
            string current = Process.GetCurrentProcess().MainModule.FileName;
            string old = current + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(current, old);
            try { File.Copy(stagedExe, current, false); }
            catch
            {
                try { File.Move(old, current); } catch { }
                throw;
            }
            DeleteFile(current + ":Zone.Identifier"); // no "downloaded from the internet" block on the new exe
            Process.Start(new ProcessStartInfo(current, "--updated") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(current) });
        }

        /// <summary>Deletes the previous exe (.old) left by an update. Runs in the background at start-up.</summary>
        internal static void CleanupOldExe()
        {
            try
            {
                string old = Process.GetCurrentProcess().MainModule.FileName + ".old";
                if (!File.Exists(old)) return;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    for (int i = 0; i < 20 && File.Exists(old); i++)
                    {
                        try { File.Delete(old); } catch { Thread.Sleep(500); }
                    }
                });
            }
            catch { }
        }

        private static Version Normalize(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
    }
}
