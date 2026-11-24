using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using ServerDocumentationTool.Collect;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool
{
    /// <summary>
    /// ServerDocumentationTool.exe --selftest &lt;output folder&gt; [--expect name=value ...]
    /// Runs the app's own code path without the window: collects this machine through the Runner (progress, JSON, DOCX),
    /// renders the saved JSON again, and writes the HTML summary. Expectations used by GitHub Actions with role fixtures:
    ///   dns-zone=ci.test  dns-forwarder=192.0.2.53  dhcp-scope=192.0.2.0  iis-site=Default Web Site  role=DNS Server
    /// Result: &lt;output folder&gt;\selftest.log; exit code 0 = passed.
    /// </summary>
    internal static class SelfTest
    {
        internal static int Run(string outDir, IList<KeyValuePair<string, string>> expect)
        {
            var log = new StringBuilder();
            int fails = 0;
            Action<string> write = s => { string l = DateTime.Now.ToString("HH:mm:ss") + " " + s; log.AppendLine(l); Console.WriteLine(l); };
            Action<bool, string> check = (ok, what) => { if (ok) write("PASS " + what); else { fails++; write("FAIL " + what); } };
            try
            {
                if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.Combine(Path.GetTempPath(), "ServerDocumentationTool_SelfTest");
                Directory.CreateDirectory(outDir);
                write(AppInfo.ProductName + " " + AppInfo.VersionText + " self-test");
                write("OS " + Environment.OSVersion.VersionString + ", 64-bit process: " + Environment.Is64BitProcess + ", CLR " + Environment.Version);

                var s = new RunSettings { ClientName = "SelfTest", RunRoot = outDir, RawDataPath = Path.Combine(outDir, "RawData"), ParallelServers = 1, ServerTimeLimitMinutes = 15, SectionTimeoutSec = 240 };
                Directory.CreateDirectory(s.RawDataPath);
                var runner = new Runner();
                int progress = 0;
                runner.ServerProgress += (name, msg) => { Interlocked.Increment(ref progress); write("  ... " + msg); };
                ServerOutcome o = runner.RunAsync(new[] { Environment.MachineName }, s, CancellationToken.None).GetAwaiter().GetResult().FirstOrDefault();
                if (o == null) { check(false, "an outcome was returned"); }
                else
                {
                    write(string.Format("Local collection: {0} [{1}] {2}; roles: {3}; DOCX: {4}", o.Status, o.Transport, o.Classification, o.Roles, o.DocName ?? "(none)"));
                    foreach (string step in o.NonOkSteps) write("  step: " + step);
                    foreach (string w in o.Warnings) write("  warning: " + w);
                    foreach (object t in N.Items(o.Result, "SectionTimings")) write("  timing: " + N.S(t, "Section") + " " + N.S(t, "Seconds") + " s " + N.S(t, "Result"));
                    check(o.Status != "Failed" && !o.Cancelled, "local collection succeeded (" + o.Status + ")");
                    check(o.Transport == "Local", "collected locally");
                    check(o.Status == "Complete", "status is Complete (no step failed, timed out or returned no data)");
                    string docPath = o.DocName == null ? null : Path.Combine(outDir, o.DocName);
                    check(docPath != null && File.Exists(docPath), "DOCX written " + (o.ReportError ?? string.Empty));
                    if (docPath != null && File.Exists(docPath))
                    {
                        string text = DocxText(docPath);
                        check(text.Contains("Server Details - " + Environment.MachineName), "DOCX has the server heading");
                        check(text.Contains("Collection Status: " + o.Status), "DOCX has the Collection Status banner");
                    }
                    check(progress > 0, progress + " progress messages reached the app");

                    string json = Path.Combine(s.RawDataPath, RunFiles.SafeFileName(Environment.MachineName) + ".json");
                    check(File.Exists(json), "RawData JSON written");
                    if (File.Exists(json))
                    {
                        var back = Json.Parse(File.ReadAllText(json));
                        string again = Path.Combine(outDir, "rerender.docx");
                        Report.DocxReport.Write(new List<object> { back }, again, "SelfTest", 15, 200);
                        check(File.Exists(again) && DocxText(again) == DocxText(docPath), "JSON renders to the same document (--render path)");
                    }

                    foreach (var e in expect ?? new List<KeyValuePair<string, string>>()) check(Expect(o.Result, e.Key, e.Value), "expected " + e.Key + " = " + e.Value);

                    string html = Path.Combine(outDir, "SelfTest_Server_Summary.html");
                    RunFiles.WriteSummary(new[] { o }, html, "SelfTest");
                    check(File.Exists(html), "HTML summary written");
                }
            }
            catch (Exception ex)
            {
                fails++;
                write("FAIL " + ex.GetType().Name + ": " + ex.Message);
            }
            write(fails == 0 ? "SELFTEST PASSED" : "SELFTEST FAILED (" + fails + ")");
            try { File.WriteAllText(Path.Combine(outDir, "selftest.log"), log.ToString()); } catch { }
            return fails == 0 ? 0 : 1;
        }

        private static bool Expect(Dictionary<string, object> r, string key, string value)
        {
            object rd = N.Get(r, "RoleData");
            switch (key.ToLowerInvariant())
            {
                case "role": return N.Items(r, "RolesDetected").Select(N.S).Contains(value);
                case "dns-zone": return N.IsTrue(N.Path(rd, "DNS", "Success")) && N.Items(N.Path(rd, "DNS", "Zones")).Any(z => string.Equals(N.S(z, "ZoneName"), value, StringComparison.OrdinalIgnoreCase));
                case "dns-forwarder": return N.Items(N.Path(rd, "DNS", "Forwarders")).Select(N.S).Contains(value);
                case "dhcp-scope": return N.IsTrue(N.Path(rd, "DHCP", "Success")) && N.Items(N.Path(rd, "DHCP", "Scopes")).Any(x => N.S(x, "ScopeId") == value);
                case "iis-site": return N.IsTrue(N.Path(rd, "IIS", "Success")) && N.Items(N.Path(rd, "IIS", "Sites")).Any(x => N.S(x, "Name") == value);
                default: return false;
            }
        }

        internal static string DocxText(string path)
        {
            using (ZipArchive z = ZipFile.OpenRead(path))
            using (var rd = new StreamReader(z.GetEntry("word/document.xml").Open(), Encoding.UTF8))
            {
                string xml = rd.ReadToEnd();
                // Ignore the "Generated:" timestamp line.
                return System.Text.RegularExpressions.Regex.Replace(xml, "Generated: [^<]*", "Generated:");
            }
        }
    }
}
