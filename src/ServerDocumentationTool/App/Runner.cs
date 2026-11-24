using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ServerDocumentationTool.Collect;
using ServerDocumentationTool.Common;
using ServerDocumentationTool.Report;

namespace ServerDocumentationTool
{
    internal sealed class RunSettings
    {
        public string ClientName = "Client";
        public Credential Credential;
        public int ParallelServers = 4;
        public int ServerTimeLimitMinutes = 30;
        public int ConnectTimeoutSec = 30;
        public int WmiQueryTimeoutSec = 180;
        public int SectionTimeoutSec = 300;
        public int GroupMemberDisplayCap = 15;
        public int ApplicationDisplayCap = 200;
        public bool IncludeWorkstations;
        public string RunRoot;
        public string RawDataPath;

        internal static RunSettings From(Settings c)
        {
            return new RunSettings
            {
                ParallelServers = c.Int("ParallelServers", 1, 16),
                ServerTimeLimitMinutes = c.Int("ServerTimeLimitMinutes", 1, 600),
                ConnectTimeoutSec = c.Int("ConnectTimeoutSec", 5, 600),
                WmiQueryTimeoutSec = c.Int("WmiQueryTimeoutSec", 10, 3600),
                SectionTimeoutSec = c.Int("SectionTimeoutSec", 30, 3600),
                GroupMemberDisplayCap = c.Int("GroupMemberDisplayCap", 1, 100000),
                ApplicationDisplayCap = c.Int("ApplicationDisplayCap", 1, 100000),
                IncludeWorkstations = c.Bool("IncludeWorkstations")
            };
        }

        internal Target TargetFor(string name)
        {
            return new Target
            {
                ComputerName = name, IsLocal = LocalNames.IsLocal(name), Credential = Credential,
                ConnectTimeoutSec = ConnectTimeoutSec, OperationTimeoutSec = WmiQueryTimeoutSec, SectionTimeoutSec = SectionTimeoutSec
            };
        }
    }

    internal sealed class ServerOutcome
    {
        public string Name;
        public string Status = "Unknown";
        public bool Success;
        public string Transport = string.Empty;
        public string Classification = string.Empty;
        public string Roles = string.Empty;
        public List<string> NonOkSteps = new List<string>();
        public List<string> Warnings = new List<string>();
        public string DocName;
        public string ReportError;
        public string JsonError;
        public bool SkippedWorkstation;
        public bool Cancelled;
        public bool TimeLimitExceeded;
        public Dictionary<string, object> Result;
    }

    /// <summary>Output folder, server lists and per-server output files (ports of the IABB.Core / IABB.AppHost helpers).</summary>
    internal static class RunFiles
    {
        internal static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unknown";
            return Regex.Replace(Regex.Replace(name, "[\\\\/:*?\"<>|]", "_"), "\\s+", "_");
        }

        /// <summary>Port of New-IABBRunFolder: &lt;root&gt;\&lt;Client&gt;_AsBuilt_yyyyMMdd_HHmmss\RawData.</summary>
        internal static void CreateRunFolder(RunSettings s, string outputRoot, string clientName)
        {
            if (string.IsNullOrWhiteSpace(outputRoot)) outputRoot = @"C:\ServerDocumentation";
            if (string.IsNullOrWhiteSpace(clientName)) clientName = "Client";
            s.ClientName = SafeFileName(clientName);
            s.RunRoot = Path.Combine(outputRoot, s.ClientName + "_AsBuilt_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            s.RawDataPath = Path.Combine(s.RunRoot, "RawData");
            Directory.CreateDirectory(s.RawDataPath);
        }

        /// <summary>Port of ConvertFrom-IABBServerText.</summary>
        internal static List<string> ParseText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return Regex.Split(text, "[\\r\\n,;\\t ]+").Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
        }

        /// <summary>Port of Import-IABBServerList: CSV with a ServerName / Server / ComputerName / Name column, or one name per line.</summary>
        internal static List<string> ImportList(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Server list file not found: " + path, path);
            var lines = File.ReadAllLines(path);
            if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
                return lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var result = new List<string>();
            if (lines.Length == 0) return result;
            var header = CsvSplit(lines[0]);
            var cols = new[] { "ServerName", "Server", "ComputerName", "Name" }.Select(c => header.FindIndex(h => string.Equals(h.Trim(), c, StringComparison.OrdinalIgnoreCase))).Where(i => i >= 0).ToList();
            foreach (string line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cells = CsvSplit(line);
                string v = cols.Select(i => i < cells.Count ? cells[i].Trim() : string.Empty).FirstOrDefault(x => x.Length > 0);
                if (!string.IsNullOrEmpty(v)) result.Add(v);
            }
            return result;
        }

        private static List<string> CsvSplit(string line)
        {
            var cells = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') q = false;
                    else sb.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            cells.Add(sb.ToString());
            return cells;
        }

        internal static void WriteText(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content ?? string.Empty, new UTF8Encoding(false));
        }

        /// <summary>Port of Complete-IABBAppServer: saves the RawData JSON and the DOCX for one server.</summary>
        internal static ServerOutcome Complete(Dictionary<string, object> result, RunSettings s, bool includeWorkstations)
        {
            string name = N.S(result, "ComputerName");
            var o = new ServerOutcome { Name = name, Result = result };
            try { WriteText(Path.Combine(s.RawDataPath, SafeFileName(name) + ".json"), Json.Serialize(result, true)); }
            catch (Exception ex) { o.JsonError = ex.Message; }
            object st = N.Get(result, "CollectionStatus");
            o.Status = st != null ? N.S(st, "Status") : "Unknown";
            if (!includeWorkstations && o.Status != "Failed" && Rules.IsWorkstationOS(result)) o.SkippedWorkstation = true;
            else
            {
                string doc = SafeFileName(name) + "_AsBuilt.docx";
                try
                {
                    DocxReport.Write(new List<object> { result }, Path.Combine(s.RunRoot, doc), s.ClientName, s.GroupMemberDisplayCap, s.ApplicationDisplayCap);
                    o.DocName = doc;
                }
                catch (Exception ex) { o.ReportError = ex.Message; result["ReportError"] = ex.Message; }
            }
            o.Success = N.IsTrue(N.Get(result, "Success"));
            o.Transport = N.S(st, "Transport");
            o.Classification = N.S(st, "Classification");
            o.Roles = string.Join(", ", N.Items(result, "RolesDetected").Select(N.S).Where(x => x.Length > 0));
            o.NonOkSteps = N.Items(result, "SectionTimings").Where(x => N.S(x, "Result") != "OK").Select(x => N.S(x, "Section") + " - " + N.S(x, "Result") + " (" + N.S(x, "Seconds") + " s)").ToList();
            o.Warnings = N.Items(result, "CollectionWarnings").Select(N.S).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return o;
        }

        /// <summary>Port of New-IABBAppSummary: HTML run summary; cancelled servers are listed as Failed ("Cancelled by user").</summary>
        internal static void WriteSummary(IEnumerable<ServerOutcome> outcomes, string path, string clientName)
        {
            var all = new List<object>();
            foreach (ServerOutcome o in outcomes)
            {
                if (o.Cancelled) all.Add(ServerCollector.FailedResult(o.Name, "Cancelled by user before collection finished.", "Cancelled by user"));
                else if (o.Result != null && !o.SkippedWorkstation) all.Add(o.Result);
            }
            HtmlSummary.Write(all, path, clientName);
        }
    }

    /// <summary>
    /// Runs the assessment: up to ParallelServers servers at once, each with a hard time limit. Cancel stops waiting for
    /// running servers immediately and skips queued ones. A server that is stopped is recorded as Failed (JSON + DOCX).
    /// </summary>
    internal sealed class Runner
    {
        public event Action<string, string> ServerProgress;   // server, message
        public event Action<ServerOutcome> ServerFinished;

        public async Task<List<ServerOutcome>> RunAsync(IList<string> servers, RunSettings s, CancellationToken token)
        {
            var gate = new SemaphoreSlim(Math.Max(1, s.ParallelServers));
            var tasks = servers.Select(name => RunOneAsync(name, s, gate, token)).ToList();
            ServerOutcome[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.ToList();
        }

        private async Task<ServerOutcome> RunOneAsync(string name, RunSettings s, SemaphoreSlim gate, CancellationToken token)
        {
            try { await gate.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return Finish(Cancelled(name, "Cancelled before it started")); }
            ServerOutcome outcome;
            try
            {
                outcome = token.IsCancellationRequested ? Cancelled(name, "Cancelled before it started") : await CollectAsync(name, s, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = RecordFailed(name, "Unexpected error: " + ex.Message, s);
            }
            finally { gate.Release(); }
            return Finish(outcome);
        }

        private static ServerOutcome Cancelled(string name, string why)
        {
            return new ServerOutcome { Name = name, Status = "Cancelled", Cancelled = true, Classification = why };
        }

        private ServerOutcome Finish(ServerOutcome o)
        {
            var h = ServerFinished;
            if (h != null) { try { h(o); } catch { } }
            return o;
        }

        private void Progress(string name, string message)
        {
            var h = ServerProgress;
            if (h != null) { try { h(name, message); } catch { } }
        }

        private async Task<ServerOutcome> CollectAsync(string name, RunSettings s, CancellationToken token)
        {
            Progress(name, "Starting");
            Target t = s.TargetFor(name);
            var collectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            bool stopped = false;
            Task<Dictionary<string, object>> collect = Task.Factory.StartNew(() =>
            {
                try { return ServerCollector.Collect(t, m => { if (!stopped) Progress(name, m); }, collectCts.Token); }
                catch (OperationCanceledException) { return null; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task delay = Task.Delay(TimeSpan.FromMinutes(Math.Max(1, s.ServerTimeLimitMinutes)), delayCts.Token);
            Task first = await Task.WhenAny(collect, delay).ConfigureAwait(false);
            if (first == collect)
            {
                delayCts.Cancel();
                Dictionary<string, object> result = await collect.ConfigureAwait(false);
                if (result == null)
                {
                    if (token.IsCancellationRequested) return Cancelled(name, "Cancelled by user while collecting");
                    return RecordFailed(name, "Collection returned no result.", s);
                }
                Progress(name, "Writing reports");
                return RunFiles.Complete(result, s, s.IncludeWorkstations);
            }
            // Time limit or Cancel: stop waiting. The collection thread stops at its next step; its result is discarded.
            stopped = true;
            collectCts.Cancel();
            _ = collect.ContinueWith(x => { var e = x.Exception; collectCts.Dispose(); }, TaskScheduler.Default);
            if (token.IsCancellationRequested) return Cancelled(name, "Cancelled by user while collecting");
            Progress(name, "Time limit of " + s.ServerTimeLimitMinutes + " minutes reached - stopped");
            ServerOutcome failed = RecordFailed(name, "Time limit exceeded: collection did not finish within " + s.ServerTimeLimitMinutes + " minutes and was stopped.", s);
            failed.TimeLimitExceeded = true;
            return failed;
        }

        /// <summary>Port of Invoke-IABBAppFailedServer: records a server as Failed, with JSON and DOCX, without collecting.</summary>
        internal static ServerOutcome RecordFailed(string name, string message, RunSettings s)
        {
            try
            {
                var r = ServerCollector.FailedResult(name, message, message);
                return RunFiles.Complete(r, s, true);
            }
            catch
            {
                return new ServerOutcome { Name = name, Status = "Failed", Classification = message, Warnings = new List<string> { message } };
            }
        }
    }
}
