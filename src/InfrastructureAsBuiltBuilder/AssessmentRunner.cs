using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;

namespace IABB.App
{
    internal sealed class RunSettings
    {
        public string ClientName;
        public PSCredential Credential;
        public bool EnableDcomFallback = true;
        public int ParallelServers = 4;
        public int ServerTimeLimitMinutes = 30;
        public int TimeoutSeconds = 30;
        public int CimOperationTimeoutSec = 180;
        public int SectionTimeoutSec = 300;
        public int GroupMemberDisplayCap = 15;
        public int ApplicationDisplayCap = 200;
        public bool IncludeWorkstations;
        public string RunRoot;
        public string RawDataPath;
    }

    internal sealed class ServerOutcome
    {
        public string Name;
        public string Status = "Unknown";
        public bool Success;
        public string Transport = string.Empty;
        public string Classification = string.Empty;
        public string Roles = string.Empty;
        public string[] NonOkSteps = new string[0];
        public string[] Warnings = new string[0];
        public string DocName;
        public string ReportError;
        public string JsonError;
        public bool SkippedWorkstation;
        public bool Cancelled;
        public bool TimeLimitExceeded;
        public object Result; // the engine's full result object (PSObject), used for the HTML summary

        public static ServerOutcome FromEngine(string name, PSObject o)
        {
            var r = new ServerOutcome { Name = name };
            if (o == null) { r.Status = "Failed"; r.Classification = "No result returned by the engine"; return r; }
            r.Status = EngineHost.Str(o, "Status");
            r.Success = EngineHost.Bool(o, "Success");
            r.Transport = EngineHost.Str(o, "Transport");
            r.Classification = EngineHost.Str(o, "Classification");
            r.Roles = EngineHost.Str(o, "Roles");
            r.NonOkSteps = EngineHost.StrArray(o, "NonOkSteps");
            r.Warnings = EngineHost.StrArray(o, "Warnings");
            r.DocName = EngineHost.Str(o, "DocName");
            r.ReportError = EngineHost.Str(o, "ReportError");
            r.JsonError = EngineHost.Str(o, "JsonError");
            r.SkippedWorkstation = EngineHost.Bool(o, "SkippedWorkstation");
            r.Result = EngineHost.Prop(o, "Result");
            if (string.IsNullOrEmpty(r.DocName)) r.DocName = null;
            if (string.IsNullOrEmpty(r.ReportError)) r.ReportError = null;
            if (string.IsNullOrEmpty(r.JsonError)) r.JsonError = null;
            return r;
        }
    }

    /// <summary>
    /// Runs the assessment: up to ParallelServers servers at once, each in its own engine pipeline with a hard time limit.
    /// Cancel stops running servers immediately and skips queued ones.
    /// </summary>
    internal sealed class AssessmentRunner
    {
        private readonly EngineHost engine;

        public AssessmentRunner(EngineHost engine)
        {
            this.engine = engine;
        }

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
            try
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Finish(new ServerOutcome { Name = name, Status = "Cancelled", Cancelled = true, Classification = "Cancelled before it started" });
            }

            ServerOutcome outcome;
            try
            {
                if (token.IsCancellationRequested)
                {
                    outcome = new ServerOutcome { Name = name, Status = "Cancelled", Cancelled = true, Classification = "Cancelled before it started" };
                }
                else
                {
                    outcome = await CollectAsync(name, s, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                outcome = RecordFailed(name, "Unexpected error: " + ex.Message, s);
            }
            finally
            {
                gate.Release();
            }
            return Finish(outcome);
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
            PowerShell ps = engine.Create();
            ps.AddCommand("Invoke-IABBAppServer")
              .AddParameter("ComputerName", name)
              .AddParameter("EnableDcomFallback", s.EnableDcomFallback)
              .AddParameter("TimeoutSeconds", s.TimeoutSeconds)
              .AddParameter("CimOperationTimeoutSec", s.CimOperationTimeoutSec)
              .AddParameter("SectionTimeoutSec", s.SectionTimeoutSec)
              .AddParameter("RawDataPath", s.RawDataPath)
              .AddParameter("ReportFolder", s.RunRoot)
              .AddParameter("ClientName", s.ClientName)
              .AddParameter("GroupMemberDisplayCap", s.GroupMemberDisplayCap)
              .AddParameter("ApplicationDisplayCap", s.ApplicationDisplayCap)
              .AddParameter("IncludeWorkstations", s.IncludeWorkstations);
            if (s.Credential != null) ps.AddParameter("Credential", s.Credential);

            ps.Streams.Information.DataAdded += (sender, e) =>
            {
                try
                {
                    var col = (PSDataCollection<InformationRecord>)sender;
                    InformationRecord rec = col[e.Index];
                    if (rec != null && rec.MessageData != null) Progress(name, Convert.ToString(rec.MessageData));
                }
                catch { }
            };

            Task<PSDataCollection<PSObject>> invoke = Task.Factory.FromAsync(ps.BeginInvoke(), ar => ps.EndInvoke(ar));
            var timeLimit = TimeSpan.FromMinutes(Math.Max(1, s.ServerTimeLimitMinutes));
            var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task delay = Task.Delay(timeLimit, delayCts.Token);
            Task first = await Task.WhenAny(invoke, delay).ConfigureAwait(false);

            if (first == invoke)
            {
                delayCts.Cancel();
                delayCts.Dispose();
                PSDataCollection<PSObject> output;
                try
                {
                    output = await invoke.ConfigureAwait(false); // engine exceptions are rethrown -> RunOneAsync records the server as Failed
                }
                catch
                {
                    try { ps.Dispose(); } catch { }
                    throw;
                }
                PSObject result = output.FirstOrDefault(o => o != null);
                string err = ps.Streams.Error.Count > 0 ? ps.Streams.Error[0].ToString() : "The engine returned no result.";
                ps.Dispose();
                if (result == null) return RecordFailed(name, err, s);
                return ServerOutcome.FromEngine(name, result);
            }
            delayCts.Dispose();

            // Time limit reached or Cancel pressed: stop this server without waiting for it (hard limit).
            try { ps.BeginStop(null, null); } catch { }
            invoke.ContinueWith(t => { var ignored = t.Exception; try { ps.Dispose(); } catch { } }, TaskScheduler.Default);

            if (token.IsCancellationRequested)
            {
                return new ServerOutcome { Name = name, Status = "Cancelled", Cancelled = true, Classification = "Cancelled by user while collecting" };
            }
            Progress(name, "Time limit of " + s.ServerTimeLimitMinutes + " minutes reached - stopped");
            ServerOutcome failed = RecordFailed(name, "Time limit exceeded: collection did not finish within " + s.ServerTimeLimitMinutes + " minutes and was stopped.", s);
            failed.TimeLimitExceeded = true;
            return failed;
        }

        /// <summary>Writes a Failed JSON + DOCX for a server through the engine. Falls back to an in-memory outcome if that also fails.</summary>
        private ServerOutcome RecordFailed(string name, string message, RunSettings s)
        {
            try
            {
                var p = new Dictionary<string, object>
                {
                    { "ComputerName", name }, { "Message", message }, { "RawDataPath", s.RawDataPath }, { "ReportFolder", s.RunRoot },
                    { "ClientName", s.ClientName }, { "GroupMemberDisplayCap", s.GroupMemberDisplayCap }, { "ApplicationDisplayCap", s.ApplicationDisplayCap }
                };
                PSObject o = engine.Run("Invoke-IABBAppFailedServer", p).FirstOrDefault(x => x != null);
                if (o != null) return ServerOutcome.FromEngine(name, o);
            }
            catch { }
            return new ServerOutcome { Name = name, Status = "Failed", Classification = message, Warnings = new[] { message } };
        }
    }
}
