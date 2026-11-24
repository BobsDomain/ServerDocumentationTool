using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;
using ServerDocumentationTool.Common;
using ServerDocumentationTool.Report;

namespace ServerDocumentationTool
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // GitHub requires TLS 1.2; older Windows builds do not enable it by default for .NET Framework apps.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            UpdateService.CleanupOldExe();
            args = args ?? new string[0];
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

            if (mode == "--selftest")
            {
                var expect = new List<KeyValuePair<string, string>>();
                string dir = null;
                for (int i = 1; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--expect", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        string e = args[++i]; int eq = e.IndexOf('=');
                        if (eq > 0) expect.Add(new KeyValuePair<string, string>(e.Substring(0, eq).Trim(), e.Substring(eq + 1).Trim()));
                    }
                    else if (dir == null) dir = args[i];
                }
                return SelfTest.Run(dir, expect);
            }
            if (mode == "--render") return Render(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);
            Application.Run(new MainForm());
            return 0;
        }

        /// <summary>
        /// ServerDocumentationTool.exe --render &lt;RawData folder or .json file&gt; &lt;output folder&gt; [client name]
        /// Builds the DOCX reports and the HTML summary again from saved RawData JSON (also JSON from the v1.x PowerShell engine).
        /// </summary>
        private static int Render(string[] args)
        {
            try
            {
                if (args.Length < 3) { Console.Error.WriteLine("Usage: " + AppInfo.ExeName + " --render <RawData folder or .json file> <output folder> [client name]"); return 2; }
                string src = args[1], outDir = args[2], client = args.Length > 3 ? args[3] : "Client";
                var files = Directory.Exists(src) ? Directory.GetFiles(src, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string> { src };
                Directory.CreateDirectory(outDir);
                var all = new List<object>();
                foreach (string f in files)
                {
                    var r = Json.Parse(File.ReadAllText(f));
                    all.Add(r);
                    string doc = Path.Combine(outDir, RunFiles.SafeFileName(N.S(r, "ComputerName")) + "_AsBuilt.docx");
                    DocxReport.Write(new List<object> { r }, doc, client, 15, 200);
                    Console.WriteLine("Created " + doc);
                }
                HtmlSummary.Write(all, Path.Combine(outDir, RunFiles.SafeFileName(client) + "_Server_Summary.html"), client);
                Console.WriteLine("Rendered " + files.Count + " server(s).");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Render failed: " + ex.Message);
                return 1;
            }
        }

        internal static void ShowError(Exception ex)
        {
            string text = ex == null ? "Unknown error." : ex.Message;
            try
            {
                MessageBox.Show("An unexpected error occurred:" + Environment.NewLine + Environment.NewLine + text,
                    AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
