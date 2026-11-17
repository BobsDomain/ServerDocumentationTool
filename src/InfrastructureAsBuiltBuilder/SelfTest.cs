using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace IABB.App
{
    /// <summary>
    /// InfrastructureAsBuiltBuilder.exe --selftest &lt;output folder&gt;
    /// Runs the app's own code path without the window: loads the engine in the runspace pool, checks the engine functions,
    /// collects this machine through AssessmentRunner (progress, JSON, DOCX) and writes the HTML summary.
    /// Result: &lt;output folder&gt;\selftest.log, exit code 0 = passed. Used by GitHub Actions after every build (added in v1.0.1,
    /// after v1.0.0 failed at start-up in a way the PowerShell-only smoke test could not see).
    /// </summary>
    internal static class SelfTest
    {
        internal static int Run(string outDir)
        {
            var log = new StringBuilder();
            int fails = 0;
            Action<string> write = s => log.AppendLine(DateTime.Now.ToString("HH:mm:ss") + " " + s);
            try
            {
                if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.Combine(Path.GetTempPath(), "ServerDocumentationTool_SelfTest");
                Directory.CreateDirectory(outDir);
                write(AppInfo.ProductName + " " + AppInfo.VersionText + " self-test, engine folder " + AppInfo.EngineDirectory);

                using (var engine = new EngineHost(AppInfo.EngineDirectory, 4))
                {
                    write("PASS engine loaded in the runspace pool");
                    IList<string> missing = engine.FindMissingCommands();
                    if (missing.Count > 0) { fails++; write("FAIL engine functions missing: " + string.Join(", ", missing)); }
                    else write("PASS all " + EngineHost.RequiredCommands.Length + " engine functions present");

                    string raw = Path.Combine(outDir, "RawData");
                    Directory.CreateDirectory(raw);
                    var settings = new RunSettings
                    {
                        ClientName = "SelfTest",
                        RunRoot = outDir,
                        RawDataPath = raw,
                        ParallelServers = 1,
                        ServerTimeLimitMinutes = 15,
                        SectionTimeoutSec = 240
                    };
                    var runner = new AssessmentRunner(engine);
                    int progress = 0;
                    runner.ServerProgress += (name, msg) => Interlocked.Increment(ref progress);
                    List<ServerOutcome> outcomes = runner.RunAsync(new[] { Environment.MachineName }, settings, CancellationToken.None).GetAwaiter().GetResult();
                    ServerOutcome o = outcomes.FirstOrDefault();
                    if (o == null) { fails++; write("FAIL no outcome returned"); }
                    else
                    {
                        write(string.Format("Local collection: {0} [{1}] {2}; roles: {3}; DOCX: {4}", o.Status, o.Transport, o.Classification, o.Roles, o.DocName ?? "(none)"));
                        foreach (string step in o.NonOkSteps) write("  step: " + step);
                        foreach (string w in o.Warnings) write("  warning: " + w);
                        if (o.Status == "Failed" || o.Cancelled) { fails++; write("FAIL local collection did not succeed"); } else write("PASS local collection " + o.Status);
                        if (o.Transport != "Local") { fails++; write("FAIL local server not collected locally"); } else write("PASS collected locally");
                        if (o.DocName == null || !File.Exists(Path.Combine(outDir, o.DocName))) { fails++; write("FAIL DOCX missing " + (o.ReportError ?? string.Empty)); } else write("PASS DOCX written");
                        if (progress == 0) { fails++; write("FAIL no progress messages reached the app"); } else write("PASS " + progress + " progress messages reached the app");

                        string html = Path.Combine(outDir, "SelfTest_Server_Summary.html");
                        engine.Run("New-IABBAppSummary", new Dictionary<string, object>
                        {
                            { "Results", new[] { o.Result } }, { "CancelledServers", new string[0] }, { "OutputPath", html }, { "ClientName", "SelfTest" }
                        });
                        if (File.Exists(html)) write("PASS HTML summary written"); else { fails++; write("FAIL HTML summary missing"); }
                    }
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
    }
}
