using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IABB.Updater
{
    /// <summary>
    /// Started by the app from a temp copy:
    ///   IABB.Updater.exe --pid &lt;app pid&gt; --zip &lt;verified release zip&gt; --target &lt;app folder&gt; --start &lt;app exe&gt;
    /// Waits for the app to close, copies the new files over the app folder (keeping Engine\Config\appsettings.json),
    /// then restarts the app. Log: %TEMP%\IABB_Update.log
    /// </summary>
    internal static class Program
    {
        private const string ReleasesPage = "https://github.com/bobsdomain/ServerDocumentationTool/releases";
        private const string PreservedConfig = @"Engine\Config\appsettings.json";
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "IABB_Update.log");

        [STAThread]
        private static int Main(string[] args)
        {
            var a = ParseArgs(args);
            string zip = Arg(a, "zip"), target = Arg(a, "target"), start = Arg(a, "start");
            Log("Updater started. zip=" + zip + " target=" + target);
            try
            {
                if (string.IsNullOrEmpty(zip) || !File.Exists(zip)) throw new FileNotFoundException("Update package not found: " + zip);
                if (string.IsNullOrEmpty(target) || !Directory.Exists(target)) throw new DirectoryNotFoundException("Application folder not found: " + target);

                WaitForExit(Arg(a, "pid"));

                string staging = Path.Combine(Path.GetTempPath(), "IABB_Update_Staging_" + Guid.NewGuid().ToString("N"));
                ZipFile.ExtractToDirectory(zip, staging);
                if (!File.Exists(Path.Combine(staging, "InfrastructureAsBuiltBuilder.exe")) || !Directory.Exists(Path.Combine(staging, "Engine", "Modules")))
                    throw new InvalidOperationException("The update package does not contain InfrastructureAsBuiltBuilder.exe and Engine\\Modules.");

                int copied = 0;
                foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(staging.Length).TrimStart('\\', '/');
                    string dest = Path.Combine(target, rel);
                    if (string.Equals(rel, PreservedConfig, StringComparison.OrdinalIgnoreCase) && File.Exists(dest))
                    {
                        // Keep the user's settings; store the new defaults next to them for reference.
                        CopyWithRetry(file, dest.Substring(0, dest.Length - ".json".Length) + ".default.json");
                        Log("Kept existing settings: " + rel);
                        continue;
                    }
                    CopyWithRetry(file, dest);
                    copied++;
                }
                Log("Copied " + copied + " file(s).");
                try { Directory.Delete(staging, true); } catch { }

                if (!string.IsNullOrEmpty(start) && File.Exists(start))
                    Process.Start(new ProcessStartInfo(start) { UseShellExecute = true, WorkingDirectory = target });
                Log("Update finished.");
                return 0;
            }
            catch (Exception ex)
            {
                Log("FAILED: " + ex);
                MessageBox.Show("The update could not be completed:" + Environment.NewLine + ex.Message + Environment.NewLine + Environment.NewLine +
                    "Log: " + LogPath + Environment.NewLine + "You can download the release manually from:" + Environment.NewLine + ReleasesPage,
                    "Server Documentation Tool - Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
                try
                {
                    if (!string.IsNullOrEmpty(start) && File.Exists(start)) Process.Start(new ProcessStartInfo(start) { UseShellExecute = true });
                }
                catch { }
                return 1;
            }
        }

        private static void WaitForExit(string pidText)
        {
            int pid;
            if (!int.TryParse(pidText, out pid)) return;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    Log("Waiting for the application (pid " + pid + ") to close...");
                    if (!p.WaitForExit(60000)) throw new TimeoutException("The application did not close within 60 seconds.");
                }
            }
            catch (ArgumentException)
            {
                // Already closed.
            }
            Thread.Sleep(500);
        }

        private static void CopyWithRetry(string source, string dest)
        {
            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(source, dest, true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException) when (attempt < 10)
                {
                    Thread.Sleep(500);
                }
            }
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    d[args[i].Substring(2)] = args[i + 1];
                    i++;
                }
            }
            return d;
        }

        private static string Arg(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        private static void Log(string message)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, Encoding.UTF8); } catch { }
        }
    }
}
