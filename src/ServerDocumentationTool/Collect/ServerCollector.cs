using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool.Collect
{
    /// <summary>What a step produced. Merged into the server result only when the step finished in time, so a step
    /// abandoned at its time limit can never change the result while it is being saved.
    /// v2.0.1: thread-safe; when a step reaches its time limit the output is sealed and what it holds is kept
    /// (v2.0.0 dropped it, so a slow server lost its printers, groups, page file and environment).</summary>
    internal sealed class StepOut
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, object> set = N.New();
        private readonly List<string> warnings = new List<string>();
        private bool closed;
        public readonly SetView Set;
        public string Result; // null = OK unless set

        public StepOut() { Set = new SetView(this); }

        public void Warn(string w) { lock (gate) { if (!closed) warnings.Add(w); } }

        /// <summary>Stops further changes and returns what the step produced so far.</summary>
        public void Seal(out List<KeyValuePair<string, object>> values, out List<string> warns)
        {
            lock (gate)
            {
                closed = true;
                values = set.ToList();
                warns = warnings.ToList();
            }
        }

        public sealed class SetView
        {
            private readonly StepOut o;
            internal SetView(StepOut o) { this.o = o; }
            public object this[string key]
            {
                get { lock (o.gate) { object v; return o.set.TryGetValue(key, out v) ? v : null; } }
                set { lock (o.gate) { if (!o.closed) o.set[key] = value; } }
            }
        }
    }

    /// <summary>
    /// Collects one server. Port of Invoke-IABBServerCollection (PowerShell engine v1.1.0): same steps, step names,
    /// field names, warnings and status rules. Everything is read over WMI/DCOM, the Remote Registry service, ADSI and
    /// LDAP - no WinRM / PowerShell remoting is needed.
    /// </summary>
    internal sealed class ServerCollector
    {
        internal const string SchemaVersion = "2.0.1";
        private const double GB = 1073741824.0;

        private readonly Target t;
        private readonly Action<string> progress;
        private readonly CancellationToken token;
        private readonly Dictionary<string, object> r;
        private readonly Dictionary<string, object> status;
        private readonly List<object> timings = new List<object>();
        private readonly List<object> warnings = new List<object>();
        private readonly Wmi wmi;
        private IRegistryReader registry;
        private readonly List<string> registryNotes = new List<string>();
        private string netbiosName;

        private ServerCollector(Target t, Action<string> progress, CancellationToken token)
        {
            this.t = t; this.progress = progress; this.token = token;
            wmi = new Wmi(t);
            r = N.Node("SchemaVersion", SchemaVersion, "ComputerName", t.ComputerName, "CollectionStarted", Now(), "CollectionFinished", null,
                "Success", false, "Error", null, "CollectionStatus", null, "SectionTimings", timings, "Overview", null, "Hardware", null,
                "OperatingSystem", null, "Storage", new List<object>(), "Services", new List<object>(), "Hotfixes", new List<object>(),
                "Shares", new List<object>(), "ProcessorDetails", new List<object>(), "MemoryModules", new List<object>(),
                "PhysicalDisks", new List<object>(), "Partitions", new List<object>(), "DiskMap", new List<object>(),
                "VolumeDetails", new List<object>(), "NetworkAdapters", new List<object>(), "NetworkAdapterDetail", new List<object>(),
                "PatchSummary", null, "Virtualization", null, "RdsSessionHost", false, "RolesDetected", new List<object>(),
                "RoleDetection", null, "RoleData", N.New(), "Applications", null, "Certificates", null, "LocalUsers", new List<object>(),
                "LocalGroups", new List<object>(), "LocalAccountsNote", null, "PrintersNote", null, "RoleNotes", new List<object>(),
                "LocalAccountsCollected", false, "Printers", new List<object>(), "PageFiles", new List<object>(),
                "EnvironmentVariables", new List<object>(), "RolesFeatures", new List<object>(), "CollectionWarnings", warnings);
            status = N.Node("Status", "Failed", "Transport", "None", "Reachable", false, "PingOk", false, "Authorized", false,
                "Classification", "Unknown", "SectionsOk", 0, "SectionsFailed", 0, "StepsTimedOut", 0, "Detail", null);
        }

        internal static string Now() { return DateTime.Now.ToString("s", CultureInfo.InvariantCulture); }

        internal static Dictionary<string, object> Collect(Target t, Action<string> progress, CancellationToken token)
        {
            return new ServerCollector(t, progress, token).Run();
        }

        /// <summary>Readiness probe only (Test Reachability button).</summary>
        internal static Dictionary<string, object> Probe(Target t)
        {
            return new ServerCollector(t, null, CancellationToken.None).Readiness();
        }

        /// <summary>Port of New-IABBFailedResult.</summary>
        internal static Dictionary<string, object> FailedResult(string computerName, string message, string classification)
        {
            string now = Now();
            var st = N.Node("Status", "Failed", "Transport", "None", "Reachable", false, "PingOk", false, "Authorized", false,
                "Classification", classification ?? "Unexpected error during collection", "SectionsOk", 0, "SectionsFailed", 0, "StepsTimedOut", 0, "Detail", message);
            return N.Node("SchemaVersion", SchemaVersion, "ComputerName", computerName, "CollectionStarted", now, "CollectionFinished", now,
                "Success", false, "Error", message, "CollectionStatus", st, "SectionTimings", new List<object>(), "RolesDetected", new List<object>(),
                "RoleData", N.New(), "CollectionWarnings", new List<object> { "Collection stopped unexpectedly: " + message });
        }

        private void Progress(string m) { if (progress != null) { try { progress(m); } catch { } } }
        private void Warn(string w) { warnings.Add(w); }

        // ------------------------------------------------------------------ steps

        private void Step(string name, Action<StepOut> body)
        {
            token.ThrowIfCancellationRequested();
            Progress(name);
            var sw = Stopwatch.StartNew();
            string res = "OK";
            var o = new StepOut();
            bool merge = true;
            try
            {
                Hard.Run(() => body(o), t.SectionTimeoutSec, "Step '" + name + "'");
            }
            catch (HardTimeoutException)
            {
                res = "Timed out";
                merge = false;
                List<KeyValuePair<string, object>> partial; List<string> partialWarnings;
                o.Seal(out partial, out partialWarnings);
                Merge(partial, partialWarnings);
                Warn("Step '" + name + "' timed out after " + t.SectionTimeoutSec + " seconds." + (partial.Count > 0 ? " Data collected before the time limit is shown." : string.Empty));
            }
            catch (Exception ex)
            {
                res = "Failed";
                Warn("Step '" + name + "' failed: " + Wmi.Describe(ex));
            }
            sw.Stop();
            if (merge)
            {
                List<KeyValuePair<string, object>> values; List<string> warns;
                o.Seal(out values, out warns);
                Merge(values, warns);
                if (o.Result != null && res == "OK") res = o.Result;
            }
            timings.Add(N.Node("Section", name, "Seconds", Math.Round(sw.Elapsed.TotalSeconds, 1), "Result", res));
        }

        private void Merge(List<KeyValuePair<string, object>> values, List<string> warns)
        {
            foreach (var kv in values)
            {
                if (kv.Key.StartsWith("RoleData.", StringComparison.Ordinal)) ((Dictionary<string, object>)r["RoleData"])[kv.Key.Substring(9)] = kv.Value;
                else r[kv.Key] = kv.Value;
            }
            foreach (string w in warns) warnings.Add(w);
        }

        private static void AddNotes(StepOut o, Dictionary<string, object> x)
        {
            foreach (object n in N.Items(x, "Notes")) if (!string.IsNullOrWhiteSpace(N.S(n))) o.Warn(N.S(n));
        }

        /// <summary>Port of Set-IABBStepOutcome.</summary>
        private void Outcome(StepOut o, Dictionary<string, object> result, string label)
        {
            if (result == null) { o.Result = "Failed"; o.Warn(label + ": no data returned."); return; }
            if (N.IsTrue(N.Get(result, "TimedOut"))) { o.Result = "Timed out"; o.Warn(label + " timed out after " + t.SectionTimeoutSec + " seconds."); return; }
            if (!N.IsTrue(N.Get(result, "Success"))) { o.Result = "Failed"; o.Warn(label + " not collected: " + N.S(result, "Error")); }
        }

        /// <summary>Port of Test-IABBStepData (v0.9.9h zero-row rule).</summary>
        private static void StepData(StepOut o, string label, IEnumerable items)
        {
            bool any = items != null && items.Cast<object>().Any(x => x != null);
            if (!any)
            {
                if (o.Result == null) o.Result = "No data";
                o.Warn(label + " returned no data. A working server always has at least one entry, so this section is treated as not collected.");
            }
        }

        // ------------------------------------------------------------------ main

        private Dictionary<string, object> Run()
        {
            var swProbe = Stopwatch.StartNew();
            Progress("Readiness probe");
            Dictionary<string, object> ready = Readiness();
            swProbe.Stop();
            bool reachable = N.IsTrue(ready["Reachable"]);
            timings.Add(N.Node("Section", "Readiness probe", "Seconds", Math.Round(swProbe.Elapsed.TotalSeconds, 1), "Result", reachable ? "OK" : "Failed"));
            foreach (string k in new[] { "Reachable", "PingOk", "Transport", "Authorized", "Classification", "Detail" }) status[k] = ready[k];
            if (!reachable)
            {
                r["Error"] = "Collection could not start: " + N.S(ready, "Classification") + ". " + N.S(ready, "Detail");
                Warn("Collection could not start: " + N.S(ready, "Classification"));
                status["Status"] = "Failed";
                r["CollectionStatus"] = status;
                r["CollectionFinished"] = Now();
                return r;
            }

            try
            {
                if (t.IsLocal && t.Credential != null)
                    Warn("This is the server running the tool, so it was collected as the signed-in user; the alternate credentials are not used for local collection.");
                Progress("System information");
                var cs0 = wmi.Query("SELECT * FROM Win32_ComputerSystem").FirstOrDefault();
                if (cs0 == null) throw new InvalidOperationException("Win32_ComputerSystem returned no data.");
                var bios = wmi.TryQuery("SELECT * FROM Win32_BIOS").FirstOrDefault();
                var enc = wmi.TryQuery("SELECT * FROM Win32_SystemEnclosure").FirstOrDefault();
                var os = wmi.Query("SELECT * FROM Win32_OperatingSystem").FirstOrDefault();
                if (os == null) throw new InvalidOperationException("Win32_OperatingSystem returned no data.");
                netbiosName = N.S(cs0, "Name");
                bool isDC = N.ToInt(N.Get(cs0, "DomainRole")) >= 4;

                Step("Processors/Memory", o => ProcessorsMemory(o, cs0));
                Step("Disks/Volumes", DisksVolumes);
                Step("Network", Network);
                Step("Services/Hotfixes/Shares", ServicesHotfixesShares);
                Step("Time zone/OS", o => TimeZoneOs(o, os));

                bool hasVmms = N.Items(r, "Services").Any(s => N.S(s, "Name") == "vmms");
                var pi = Platform.Info(N.S(cs0, "Manufacturer"), N.S(cs0, "Model"), hasVmms, N.S(enc, "SMBIOSAssetTag"));
                string note = N.IsTrue(pi["IsHyperVHost"]) ? "Physical server running the Hyper-V role (virtualization host)." : N.IsTrue(pi["IsVirtual"]) ? "Server is virtual." : "Server appears physical.";
                r["Virtualization"] = N.Node("PlatformType", pi["PlatformType"], "HostPlatform", pi["HostPlatform"], "IsVirtual", pi["IsVirtual"], "IsHyperVHost", pi["IsHyperVHost"],
                    "Manufacturer", N.Get(cs0, "Manufacturer"), "Model", N.Get(cs0, "Model"), "ReportingNote", note);
                r["Overview"] = N.Node("Name", N.Get(cs0, "Name"), "Domain", N.Get(cs0, "Domain"), "SerialNumber", N.Get(bios, "SerialNumber"), "DomainRole", N.ToInt(N.Get(cs0, "DomainRole")));

                Step("Local accounts, printers, page file, features", o => Extras(o, isDC));

                var rd = RoleDetection.Detect(r);
                r["RoleDetection"] = rd;
                r["RolesDetected"] = N.Items(rd, "Roles");
                r["RoleNotes"] = N.Items(rd, "Notes").Where(x => !string.IsNullOrWhiteSpace(N.S(x))).ToList();
                var roles = N.Items(r, "RolesDetected").Select(N.S).ToList();

                if (roles.Contains("Domain Controller")) RoleStep("Active Directory", "ADDS", () => RoleCollectors.Adds(t));
                if (roles.Contains("DNS Server")) RoleStep("DNS Server", "DNS", () => RoleCollectors.Dns(t, wmi));
                if (roles.Contains("DHCP Server")) RoleStep("DHCP Server", "DHCP", () => RoleCollectors.Dhcp(t));
                if (roles.Contains("IIS Server")) Step("IIS", o => { var x = RoleCollectors.Iis(t, wmi, Registry()); o.Set["RoleData.IIS"] = x; Outcome(o, x, "IIS"); AddNotes(o, x); });
                if (roles.Contains("SQL Server")) Step("SQL Server", SqlStep);
                Step("Installed applications", o =>
                {
                    var x = Inventory.Applications(t, Registry());
                    o.Set["Applications"] = x;
                    Outcome(o, x, "Applications inventory");
                    AddNotes(o, x);
                    if (N.IsTrue(x["Success"])) StepData(o, "Applications inventory", N.Items(x, "Applications").Concat(N.Items(x, "HiddenEntries")).ToList());
                });
                Step("Certificates", o =>
                {
                    var x = Inventory.Certificates(t, Registry(), () => Registry() is WmiReader ? null : new WmiReader(wmi, t.OperationTimeoutSec));
                    o.Set["Certificates"] = x;
                    Outcome(o, x, "Certificates inventory");
                    AddNotes(o, x);
                    if (N.IsTrue(x["Success"])) StepData(o, "Certificates inventory", N.Items(x, "Certificates"));
                });
                foreach (string n in registryNotes.Distinct()) Warn(n);
                r["Success"] = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string m = Wmi.Describe(ex);
                r["Error"] = m;
                Warn("Core collection failed: " + m);
            }
            finally
            {
                r["CollectionFinished"] = Now();
            }

            var steps = timings.Where(x => N.S(x, "Section") != "Readiness probe").ToList();
            status["SectionsOk"] = steps.Count(x => N.S(x, "Result") == "OK");
            status["SectionsFailed"] = steps.Count(x => N.S(x, "Result") == "Failed" || N.S(x, "Result") == "No data");
            status["StepsTimedOut"] = steps.Count(x => N.S(x, "Result") == "Timed out");
            bool ok = N.IsTrue(r["Success"]);
            status["Status"] = ok && (int)status["SectionsFailed"] == 0 && (int)status["StepsTimedOut"] == 0 ? "Complete" : ok ? "Partial" : "Failed";
            r["CollectionStatus"] = status;
            Progress("Done - " + status["Status"]);
            return r;
        }

        private IRegistryReader Registry()
        {
            lock (registryNotes)
            {
                if (registry == null) registry = RegistryAccess.Open(t, wmi, registryNotes);
                return registry;
            }
        }

        /// <summary>AD DS / DNS / DHCP step (port of the role step loop and Invoke-IABBRoleCollectorTimed).</summary>
        private void RoleStep(string label, string key, Func<Dictionary<string, object>> collect)
        {
            Step(label, o =>
            {
                Dictionary<string, object> x = null; string err = null;
                try { x = collect(); } catch (Exception ex) { err = Wmi.Describe(ex); }
                if (x != null)
                {
                    o.Set["RoleData." + key] = x;
                    foreach (object nt in N.Items(x, "Notes")) if (!string.IsNullOrWhiteSpace(N.S(nt))) o.Warn(N.S(nt));
                    if (!N.IsTrue(N.Get(x, "Success"))) err = N.S(x, "Error");
                }
                else
                {
                    o.Set["RoleData." + key] = N.Node("Success", false, "Error", err ?? "No data returned");
                    if (err == null) err = "No data returned";
                }
                if (!string.IsNullOrEmpty(err)) { o.Result = "Failed"; o.Warn(label + " not collected: " + err); }
            });
            // A role step that hit the step time limit still needs its RoleData entry, so the report shows the reason.
            var last = timings.LastOrDefault();
            var rdata = (Dictionary<string, object>)r["RoleData"];
            if (last != null && N.S(last, "Result") == "Timed out" && !rdata.ContainsKey(key))
            {
                rdata[key] = N.Node("Success", false, "Error", "Timed out after " + t.SectionTimeoutSec + " seconds");
            }
        }

        // ------------------------------------------------------------------ readiness (port of Test-IABBCollectionReadiness)

        private Dictionary<string, object> Readiness()
        {
            var x = N.Node("Reachable", false, "PingOk", false, "Transport", "None", "Authorized", false, "Classification", "Unknown", "Detail", null);
            if (t.IsLocal)
            {
                x["Reachable"] = true; x["PingOk"] = true; x["Transport"] = "Local"; x["Authorized"] = true; x["Classification"] = "OK (Local)";
                return x;
            }
            x["PingOk"] = PingOk(t.ComputerName);
            try
            {
                var os = wmi.Query("SELECT Caption FROM Win32_OperatingSystem", "root\\cimv2", Math.Max(30, t.ConnectTimeoutSec));
                if (os.Count == 0) throw new InvalidOperationException("Win32_OperatingSystem returned no data");
                x["Reachable"] = true; x["Transport"] = "DCOM"; x["Authorized"] = true; x["Classification"] = "OK (DCOM)";
            }
            catch (Exception ex)
            {
                string e = Wmi.Describe(ex);
                x["Detail"] = "WMI (DCOM) connection failed: " + e;
                x["Classification"] = Classify(N.IsTrue(x["PingOk"]), e);
            }
            return x;
        }

        internal static string Classify(bool pingOk, string e)
        {
            if (!pingOk && !Regex.IsMatch(e, "Access is denied|0x80070005|password|logon", RegexOptions.IgnoreCase)) return "Unreachable (no ping)";
            if (Regex.IsMatch(e, "0x8009030e|0x80090322|0x8009030c|0x80090311|0x8007052e|0x80070569|logon session does not exist|Kerberos|Service Principal Name|no trust|user name or password is incorrect|logon failure", RegexOptions.IgnoreCase))
                return "Authentication failed (Kerberos/credentials) - run with alternate credentials for the target domain";
            if (Regex.IsMatch(e, "Access is denied|0x80070005", RegexOptions.IgnoreCase)) return "Access denied (account is not a local administrator on the target)";
            if (Regex.IsMatch(e, "RPC server|0x800706ba|0x800706bf|0x80070776|timeout|timed out|did not finish|not responding|cannot connect", RegexOptions.IgnoreCase))
                return "WMI (DCOM/RPC) blocked or not responding (firewall, service off or timeout)";
            return "WMI connection failed";
        }

        internal static bool PingOk(string host)
        {
            try
            {
                using (var p = new Ping())
                {
                    return p.Send(host, 3000).Status == IPStatus.Success;
                }
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ core WMI steps

        private static object RoundGB(object bytes, int digits, object whenZero)
        {
            double? b = N.Dbl(bytes);
            if (!b.HasValue || b.Value == 0) return whenZero;
            return Math.Round(b.Value / GB, digits);
        }

        private void ProcessorsMemory(StepOut o, Dictionary<string, object> cs0)
        {
            var procs = wmi.Query("SELECT * FROM Win32_Processor");
            var mem = wmi.TryQuery("SELECT * FROM Win32_PhysicalMemory");
            var names = new List<string>();
            foreach (var p in procs) { string n = N.S(p, "Name"); if (!names.Contains(n)) names.Add(n); }
            o.Set["Hardware"] = N.Node("ProcessorModel", string.Join(", ", names), "MemoryGB", RoundGB(N.Get(cs0, "TotalPhysicalMemory"), 2, null), "MemoryModuleCount", mem.Count);
            var pd = procs.Select(p => (object)N.Node("SocketDesignation", N.Get(p, "SocketDesignation"), "Name", N.Get(p, "Name"), "Cores", N.Get(p, "NumberOfCores"),
                "LogicalProcessors", N.Get(p, "NumberOfLogicalProcessors"), "MaxClockSpeedMHz", N.Get(p, "MaxClockSpeed"))).ToList();
            o.Set["ProcessorDetails"] = pd;
            o.Set["MemoryModules"] = mem.Select(m => (object)N.Node("BankLabel", N.Get(m, "BankLabel"), "DeviceLocator", N.Get(m, "DeviceLocator"), "CapacityGB", RoundGB(N.Get(m, "Capacity"), 2, null),
                "SpeedMHz", N.Get(m, "Speed"), "Manufacturer", N.Get(m, "Manufacturer"), "PartNumber", N.S(m, "PartNumber").Trim())).ToList();
            StepData(o, "Processors", pd);
        }

        /// <summary>Port of Get-IABBAssocDeviceId: DeviceID from an association reference path.</summary>
        internal static string AssocDeviceId(object reference)
        {
            if (reference == null) return string.Empty;
            Match m = Regex.Match(N.S(reference), "DeviceID\\s*=\\s*\"([^\"]+)\"");
            return m.Success ? Regex.Replace(m.Groups[1].Value, @"\\\\", "\\") : string.Empty;
        }

        /// <summary>Port of Get-IABBVolumeClassification.</summary>
        internal static string[] VolumeClass(string driveLetter, string name, string caption)
        {
            string dl = (driveLetter ?? string.Empty).Trim();
            string path = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(path)) path = (caption ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(dl)) return new[] { "Mounted Volume", dl, "True" };
            if (Regex.IsMatch(path, @"^[A-Za-z]:\\.+")) return new[] { "Mount Point (folder)", path, "True" };
            return new[] { "System/Recovery", path, "False" };
        }

        private static readonly StringComparer Sort = StringComparer.Create(CultureInfo.InvariantCulture, true);

        private void DisksVolumes(StepOut o)
        {
            var disks = wmi.Query("SELECT * FROM Win32_DiskDrive");
            var parts = wmi.TryQuery("SELECT * FROM Win32_DiskPartition");
            var logical = wmi.TryQuery("SELECT * FROM Win32_LogicalDisk WHERE DriveType=3");
            var vols = wmi.TryQuery("SELECT * FROM Win32_Volume WHERE DriveType=3");
            var diskToPart = wmi.TryQuery("SELECT * FROM Win32_DiskDriveToDiskPartition");
            var partToLogical = wmi.TryQuery("SELECT * FROM Win32_LogicalDiskToPartition");
            var pd = disks.OrderBy(d => N.ToInt(N.Get(d, "Index"))).Select(d => (object)N.Node("Index", N.Get(d, "Index"), "Model", N.Get(d, "Model"),
                "DiskClass", Regex.IsMatch(N.S(d, "Model"), "Virtual|VMware", RegexOptions.IgnoreCase) ? "Virtual Disk" : "Physical Disk",
                "InterfaceType", N.Get(d, "InterfaceType"), "MediaType", N.Get(d, "MediaType"), "SizeGB", RoundGB(N.Get(d, "Size"), 2, null), "Partitions", N.Get(d, "Partitions"))).ToList();
            o.Set["PhysicalDisks"] = pd;
            o.Set["Partitions"] = parts.OrderBy(p => N.ToInt(N.Get(p, "DiskIndex"))).ThenBy(p => N.ToInt(N.Get(p, "Index"))).Select(p => (object)N.Node("DiskIndex", N.Get(p, "DiskIndex"),
                "Name", N.Get(p, "Name"), "Type", N.Get(p, "Type"), "SizeGB", RoundGB(N.Get(p, "Size"), 2, null), "Bootable", N.Get(p, "Bootable"), "BootPartition", N.Get(p, "BootPartition"),
                "Primary", N.Get(p, "PrimaryPartition"), "StartingOffset", N.Get(p, "StartingOffset"))).ToList();
            var ptl = new Dictionary<string, string>();
            foreach (var pl in partToLogical)
            {
                string pN = AssocDeviceId(N.Get(pl, "Antecedent")), lN = AssocDeviceId(N.Get(pl, "Dependent"));
                if (!string.IsNullOrWhiteSpace(pN)) ptl[pN] = lN;
            }
            o.Set["DiskMap"] = diskToPart.Select(dp =>
            {
                string dD = AssocDeviceId(N.Get(dp, "Antecedent")), pD = AssocDeviceId(N.Get(dp, "Dependent"));
                string lD; if (!ptl.TryGetValue(pD, out lD)) lD = string.Empty;
                return (object)N.Node("PhysicalDisk", dD, "Partition", pD, "LogicalDisk", lD);
            }).ToList();
            o.Set["Storage"] = logical.OrderBy(l => N.S(l, "DeviceID"), Sort).Select(l => (object)N.Node("Drive", N.Get(l, "DeviceID"), "FileSystem", N.Get(l, "FileSystem"),
                "SizeGB", RoundGB(N.Get(l, "Size"), 2, 0), "FreeGB", RoundGB(N.Get(l, "FreeSpace"), 2, 0))).ToList();
            var vd = vols.OrderBy(v => N.S(v, "DriveLetter"), Sort).ThenBy(v => N.S(v, "Name"), Sort).ThenBy(v => N.S(v, "Label"), Sort).Select(v =>
            {
                double? cap = N.Dbl(N.Get(v, "Capacity")), free = N.Dbl(N.Get(v, "FreeSpace"));
                object fp = cap.HasValue && cap.Value != 0 ? (object)Math.Round(((free ?? 0) / cap.Value) * 100, 2) : null;
                string[] cls = VolumeClass(N.S(v, "DriveLetter"), N.S(v, "Name"), N.S(v, "Caption"));
                return (object)N.Node("DriveLetter", N.Get(v, "DriveLetter"), "MountPath", cls[1], "VolumeType", cls[0], "IsData", cls[2] == "True", "Label", N.Get(v, "Label"),
                    "FileSystem", N.Get(v, "FileSystem"), "CapacityGB", RoundGB(N.Get(v, "Capacity"), 2, null), "FreeGB", RoundGB(N.Get(v, "FreeSpace"), 2, null), "FreePct", fp,
                    "HealthFlag", fp != null && (double)fp < 15 ? "LOW SPACE" : "OK", "BlockSize", N.Get(v, "BlockSize"));
            }).ToList();
            o.Set["VolumeDetails"] = vd;
            StepData(o, "Physical disks", pd);
            StepData(o, "Volumes", vd);
        }

        private static List<string> IPv4(object values)
        {
            return N.Items(values).Select(N.S).Where(v => Regex.IsMatch(v, @"^(\d{1,3}\.){3}\d{1,3}$")).ToList();
        }

        private static string NetBios(object v)
        {
            string s = N.S(v);
            switch (s)
            {
                case "0": return "Default (from DHCP)";
                case "1": return "Enabled";
                case "2": return "Disabled";
                default: return string.IsNullOrWhiteSpace(s) ? "Not set" : s;
            }
        }

        private void Network(StepOut o)
        {
            var cfgs = wmi.Query("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=True").OrderBy(c => N.S(c, "Description"), Sort).ToList();
            var nics = wmi.TryQuery("SELECT Index, NetConnectionID, Speed, NetConnectionStatus FROM Win32_NetworkAdapter");
            Func<Dictionary<string, object>, Dictionary<string, object>> adapter = c => nics.FirstOrDefault(a => N.S(a, "Index") == N.S(c, "Index"));
            var na = cfgs.Select(c =>
            {
                var ad = adapter(c);
                double? sp = N.Dbl(N.Get(ad, "Speed"));
                return (object)N.Node("Name", ad != null ? N.S(ad, "NetConnectionID") : null, "Description", N.S(c, "Description"),
                    "SpeedMbps", ad != null && sp.HasValue && sp.Value != 0 ? (object)Math.Round(sp.Value / 1000000, 0) : null, "MACAddress", N.S(c, "MACAddress"),
                    "IPAddress", string.Join(", ", IPv4(N.Get(c, "IPAddress"))), "Gateway", string.Join(", ", N.Items(c, "DefaultIPGateway").Select(N.S)),
                    "DNSServers", string.Join(", ", IPv4(N.Get(c, "DNSServerSearchOrder"))), "DHCPEnabled", N.Get(c, "DHCPEnabled"));
            }).ToList();
            o.Set["NetworkAdapters"] = na;
            o.Set["NetworkAdapterDetail"] = cfgs.Select(c =>
            {
                var ad = adapter(c);
                var masks = N.Items(c, "IPSubnet").Select(N.S).Where(m => Regex.IsMatch(m, @"^(\d{1,3}\.){3}\d{1,3}$"));
                var wins = new List<string>();
                if (N.Truthy(N.Get(c, "WINSPrimaryServer"))) wins.Add(N.S(c, "WINSPrimaryServer"));
                if (N.Truthy(N.Get(c, "WINSSecondaryServer"))) wins.Add(N.S(c, "WINSSecondaryServer"));
                string stText = "Unknown";
                if (ad != null)
                {
                    string ns = N.S(ad, "NetConnectionStatus");
                    stText = ns == "2" ? "Connected" : ns == "7" ? "Media disconnected" : ns == "0" ? "Disconnected" : ns;
                }
                return (object)N.Node("Name", ad != null ? N.S(ad, "NetConnectionID") : N.S(c, "Description"), "Status", stText,
                    "IPv4Address", string.Join(", ", IPv4(N.Get(c, "IPAddress"))), "SubnetMask", string.Join(", ", masks),
                    "DefaultGateway", string.Join(", ", N.Items(c, "DefaultIPGateway").Select(N.S)), "DNSServers", string.Join(", ", IPv4(N.Get(c, "DNSServerSearchOrder"))),
                    "ConnSpecificSuffix", N.S(c, "DNSDomain"), "DNSDomainSuffixSearchOrder", string.Join(", ", N.Items(c, "DNSDomainSuffixSearchOrder").Select(N.S)),
                    "DHCPEnabled", N.S(c, "DHCPEnabled"), "DHCPServer", N.S(c, "DHCPServer"), "DHCPLeaseObtained", N.S(c, "DHCPLeaseObtained"),
                    "DHCPLeaseExpires", N.S(c, "DHCPLeaseExpires"), "WINSServers", string.Join(", ", wins), "NetBIOSSetting", NetBios(N.Get(c, "TcpipNetbiosOptions")));
            }).ToList();
            StepData(o, "Network adapters", na);
        }

        /// <summary>Port of Convert-IABBHotfixDate.</summary>
        internal static DateTime? HotfixDate(object value)
        {
            if (value == null) return null;
            if (value is DateTime) return (DateTime)value;
            string s = N.S(value).Trim();
            if (s.Length == 0) return null;
            DateTime d;
            string[] fmts = { "M/d/yyyy", "MM/dd/yyyy", "M/d/yyyy h:mm:ss tt", "M/d/yyyy HH:mm:ss", "yyyy-MM-dd", "yyyyMMdd" };
            if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            if (Regex.IsMatch(s, "^[0-9a-fA-F]{15,16}$"))
            {
                try { return DateTime.FromFileTimeUtc(Convert.ToInt64(s, 16)).ToLocalTime(); } catch { }
            }
            if (DateTime.TryParse(s, out d)) return d;
            return null;
        }

        private void ServicesHotfixesShares(StepOut o)
        {
            var svcs = wmi.Query("SELECT Name, DisplayName, State, StartMode, StartName FROM Win32_Service");
            var hot = wmi.TryQuery("SELECT HotFixID, Description, InstalledOn, InstalledBy FROM Win32_QuickFixEngineering");
            var shares = wmi.TryQuery("SELECT Name, Path, Description, Type FROM Win32_Share");
            var services = svcs.OrderBy(s => N.S(s, "DisplayName"), Sort).Select(s => (object)N.Node("Name", N.Get(s, "Name"), "DisplayName", N.Get(s, "DisplayName"),
                "State", N.Get(s, "State"), "StartMode", N.Get(s, "StartMode"), "StartName", N.Get(s, "StartName"))).ToList();
            o.Set["Services"] = services;
            var hf = hot.Select(h => new { Id = N.S(h, "HotFixID"), Desc = N.S(h, "Description"), Date = HotfixDate(N.Get(h, "InstalledOn")), Raw = N.S(h, "InstalledOn"), By = N.S(h, "InstalledBy") }).ToList();
            o.Set["Hotfixes"] = hf.OrderByDescending(h => h.Date ?? DateTime.MinValue).ThenBy(h => h.Id, Sort).Select(h => (object)N.Node("HotFixID", h.Id, "Description", h.Desc,
                "InstalledOn", h.Date.HasValue ? h.Date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty, "InstalledOnRaw", h.Raw, "InstalledBy", h.By)).ToList();
            var dated = hf.Where(h => h.Date.HasValue).ToList();
            DateTime now = DateTime.Now;
            var latest = dated.OrderByDescending(h => h.Date.Value).FirstOrDefault();
            string latestKbs = latest == null ? string.Empty : string.Join(", ", dated.Where(h => h.Date.Value.Date == latest.Date.Value.Date).Select(h => h.Id).OrderBy(x => x, Sort));
            o.Set["PatchSummary"] = N.Node("HotfixCount", hot.Count, "DatedCount", dated.Count, "UndatedCount", hot.Count - dated.Count,
                "LatestPatchDate", latest != null ? latest.Date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null, "LatestKB", latestKbs,
                "DaysSinceLastPatch", latest != null ? (object)(int)Math.Floor((now - latest.Date.Value).TotalDays) : null,
                "HotfixesLast90Days", dated.Count(h => (now - h.Date.Value).TotalDays <= 90));
            o.Set["Shares"] = shares.OrderBy(s => N.S(s, "Name"), Sort).Select(s => (object)N.Node("Name", N.Get(s, "Name"), "Path", N.Get(s, "Path"),
                "Description", N.Get(s, "Description"), "Type", N.Get(s, "Type"))).ToList();
            StepData(o, "Services", services);
        }

        private void TimeZoneOs(StepOut o, Dictionary<string, object> os)
        {
            var tz = wmi.TryQuery("SELECT Caption FROM Win32_TimeZone").FirstOrDefault();
            object boot = N.Get(os, "LastBootUpTime");
            o.Set["OperatingSystem"] = N.Node("Caption", N.Get(os, "Caption"), "Version", N.Get(os, "Version"), "BuildNumber", N.Get(os, "BuildNumber"),
                "InstallDate", N.Get(os, "InstallDate"), "LastBootTime", boot,
                "UptimeDays", boot is DateTime ? (object)Math.Round((DateTime.Now - (DateTime)boot).TotalDays, 2) : null,
                "Architecture", N.Get(os, "OSArchitecture"), "TimeZone", tz != null ? N.Get(tz, "Caption") : null);
            try
            {
                var ts = wmi.Query("SELECT TerminalServerMode FROM Win32_TerminalServiceSetting", "root\\cimv2\\TerminalServices").FirstOrDefault();
                if (ts != null && N.ToInt(N.Get(ts, "TerminalServerMode")) == 1) o.Set["RdsSessionHost"] = true;
            }
            catch { }
        }

        // ------------------------------------------------------------------ extras (port of IABBExtrasBlock)

        private void Extras(StepOut o, bool isDC)
        {
            int budget = (int)(t.SectionTimeoutSec * 0.8);
            var sw = Stopwatch.StartNew();
            Dictionary<string, object> spool = null; bool spoolKnown = false;
            try { spool = wmi.Query("SELECT State, StartMode FROM Win32_Service WHERE Name='Spooler'").FirstOrDefault(); spoolKnown = true; } catch { }
            if (spoolKnown && spool == null) o.Set["PrintersNote"] = "The Print Spooler service is not installed, so no printers are available on this server.";
            else if (spoolKnown && N.S(spool, "State") != "Running") o.Set["PrintersNote"] = "The Print Spooler service is " + N.S(spool, "State") + " (" + N.S(spool, "StartMode") + "), so no printers are available on this server.";
            else
            {
                // v2.0.1: Win32_Printer is slow over DCOM on print servers with many queues (seen in the v2.0.0 field
                // test). After 60 s the list is read from the spooler's registry key instead.
                try
                {
                    o.Set["Printers"] = wmi.Query("SELECT * FROM Win32_Printer", "root\\cimv2", 60).Select(p => (object)N.Node("Name", N.S(p, "Name"),
                        "DriverName", N.S(p, "DriverName"), "PortName", N.S(p, "PortName"), "Shared", N.S(p, "Shared"), "ShareName", N.S(p, "ShareName"),
                        "Location", N.S(p, "Location"), "Default", N.S(p, "Default"))).ToList();
                }
                catch (Exception ex)
                {
                    try
                    {
                        o.Set["Printers"] = PrintersFromRegistry(Registry());
                        o.Warn("Printers: the WMI printer query did not complete (" + Wmi.Describe(ex) + "); the printer list was read from the registry.");
                    }
                    catch (Exception ex2) { o.Warn("Printers: " + Wmi.Describe(ex) + "; registry: " + Wmi.Describe(ex2)); }
                }
            }
            try
            {
                o.Set["PageFiles"] = wmi.Query("SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage").Select(p => (object)N.Node("Name", N.S(p, "Name"),
                    "AllocatedBaseSizeMB", N.Get(p, "AllocatedBaseSize"), "CurrentUsageMB", N.Get(p, "CurrentUsage"), "PeakUsageMB", N.Get(p, "PeakUsage"))).ToList();
            }
            catch (Exception ex) { o.Warn("Page file: " + Wmi.Describe(ex)); }
            try
            {
                o.Set["EnvironmentVariables"] = wmi.Query("SELECT Name, VariableValue FROM Win32_Environment WHERE SystemVariable=True").Select(e => (object)N.Node("Name", N.S(e, "Name"),
                    "Value", N.S(e, "VariableValue"))).ToList();
            }
            catch (Exception ex) { o.Warn("Environment variables: " + Wmi.Describe(ex)); }
            try { o.Set["RolesFeatures"] = Features.Collect(wmi); }
            catch (ManagementException me) when (me.ErrorCode == ManagementStatus.InvalidClass || me.ErrorCode == ManagementStatus.InvalidNamespace) { }
            catch (Exception ex) { o.Warn("Roles and features: " + Wmi.Describe(ex)); }

            string nb = netbiosName ?? t.HostName;
            string q = "'" + nb.Replace("'", "''") + "'";
            if (isDC)
            {
                o.Set["LocalUsers"] = new List<object>();
                o.Set["LocalAccountsCollected"] = true;
                o.Set["LocalAccountsNote"] = "Domain controller: there are no local (SAM) user accounts. Only the built-in domain groups (S-1-5-32-*) are listed below; full domain group membership belongs in Active Directory documentation.";
            }
            else
            {
                try
                {
                    o.Set["LocalUsers"] = wmi.Query("SELECT Name, FullName, Disabled, Lockout, PasswordExpires, PasswordRequired, SID FROM Win32_UserAccount WHERE LocalAccount=True AND Domain=" + q)
                        .Select(u => (object)N.Node("Name", N.S(u, "Name"), "FullName", N.S(u, "FullName"), "Disabled", N.S(u, "Disabled"), "Lockout", N.S(u, "Lockout"),
                        "PasswordExpires", N.S(u, "PasswordExpires"), "PasswordRequired", N.S(u, "PasswordRequired"), "SID", N.S(u, "SID"))).ToList();
                    o.Set["LocalAccountsCollected"] = true;
                }
                catch (Exception ex) { o.Warn("Local users: " + Wmi.Describe(ex)); }
            }
            try
            {
                string filter = isDC ? "LocalAccount=True AND SID LIKE 'S-1-5-32-%'" : "LocalAccount=True AND Domain=" + q;
                var groups = wmi.Query("SELECT Name, Description, SID FROM Win32_Group WHERE " + filter).OrderBy(g => N.S(g, "Name"), Sort).ToList();
                // Listed straight away, so a step that reaches its time limit still shows the groups.
                o.Set["LocalGroups"] = groups.Select(g => (object)N.Node("Name", N.S(g, "Name"), "Description", N.S(g, "Description"), "SID", N.S(g, "SID"),
                    "MemberCount", string.Empty, "Members", "(member lookup did not finish - time limit reached)")).ToList();
                o.Set["LocalAccountsCollected"] = true;
                // v2.0.1: members are looked up 6 groups at a time (v2.0.0: one at a time, median 36 s, up to 300 s per server).
                int skipped = 0;
                var rowsArr = RegistryAccess.ParallelMap(groups, 6, g =>
                {
                    if (sw.Elapsed.TotalSeconds >= budget)
                    {
                        Interlocked.Increment(ref skipped);
                        return (object)N.Node("Name", N.S(g, "Name"), "Description", N.S(g, "Description"), "SID", N.S(g, "SID"), "MemberCount", string.Empty, "Members", "(member lookup skipped - time limit reached)");
                    }
                    List<string> members;
                    try { members = GroupMembers(N.S(g, "Name"), nb); }
                    catch (Exception ex) { members = new List<string> { "(member lookup failed: " + ex.Message.Trim() + ")" }; }
                    return (object)N.Node("Name", N.S(g, "Name"), "Description", N.S(g, "Description"), "SID", N.S(g, "SID"), "MemberCount", members.Count, "Members", string.Join(", ", members));
                }, null);
                var rows = rowsArr.ToList();
                o.Set["LocalGroups"] = rows;
                o.Set["LocalAccountsCollected"] = true;
                if (skipped > 0) o.Warn("Local groups: member lookup skipped for " + skipped + " group(s) after reaching the time limit.");
            }
            catch (Exception ex) { o.Warn("Local groups: " + Wmi.Describe(ex)); }
        }

        /// <summary>Printers from HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers (Attributes: 0x8 shared, 0x4 default).</summary>
        internal static List<object> PrintersFromRegistry(IRegistryReader reg)
        {
            const string root = @"SYSTEM\CurrentControlSet\Control\Print\Printers";
            var names = new[] { "Name", "Printer Driver", "Port", "Share Name", "Location", "Attributes" };
            var keys = reg.SubKeys(root).OrderBy(x => x, Sort).ToList();
            var vals = RegistryAccess.ParallelMap(keys, reg.Parallelism, k => reg.Values(root + "\\" + k, names), null);
            var list = new List<object>();
            for (int i = 0; i < keys.Count; i++)
            {
                var v = vals[i] ?? new Dictionary<string, object>();
                Func<string, string> val = n => { object x; return v.TryGetValue(n, out x) ? N.S(x) : string.Empty; };
                int attr = N.ToInt(val("Attributes"));
                list.Add(N.Node("Name", val("Name").Length > 0 ? val("Name") : keys[i], "DriverName", val("Printer Driver"), "PortName", val("Port"),
                    "Shared", (attr & 8) != 0 ? "True" : "False", "ShareName", val("Share Name"), "Location", val("Location"), "Default", (attr & 4) != 0 ? "True" : "False"));
            }
            return list;
        }

        /// <summary>Members of a local group via ADSI WinNT (port of the [ADSI] lookup and Get-MemberName).</summary>
        private List<string> GroupMembers(string group, string netbios)
        {
            string host = t.IsLocal ? Environment.MachineName : t.ComputerName;
            Credential c = t.RemoteCredential;
            int limit = 60;
            return Hard.Run(() =>
            {
                var list = new List<string>();
                using (var g = c != null ? new DirectoryEntry("WinNT://" + host + "/" + group + ",group", c.FullUser, c.Password, AuthenticationTypes.Secure)
                                         : new DirectoryEntry("WinNT://" + host + "/" + group + ",group"))
                {
                    var members = g.Invoke("Members") as IEnumerable;
                    if (members == null) return list;
                    foreach (object m in members)
                    {
                        string p;
                        try { p = Convert.ToString(m.GetType().InvokeMember("ADsPath", System.Reflection.BindingFlags.GetProperty, null, m, null), CultureInfo.InvariantCulture) ?? string.Empty; }
                        catch { continue; }
                        string name = MemberName(p, netbios);
                        if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
                    }
                }
                return list;
            }, limit, "Members of local group " + group);
        }

        internal static string MemberName(string adsPath, string netbios)
        {
            string p = Regex.Replace(adsPath ?? string.Empty, "^WinNT://", string.Empty, RegexOptions.IgnoreCase);
            string[] parts = p.Split('/');
            if (parts.Length >= 3 && string.Equals(parts[1], netbios, StringComparison.OrdinalIgnoreCase)) return parts[2];
            // v2.0.1: read remotely, a local account's path has no domain part (WinNT://SERVER/user); show "user" as v1.x did.
            if (parts.Length == 2 && string.Equals(parts[0], netbios, StringComparison.OrdinalIgnoreCase)) return parts[1];
            if (parts.Length == 2) return parts[0] + "\\" + parts[1];
            if (parts.Length >= 3) return parts[1] + "\\" + parts[2];
            return p;
        }

        // ------------------------------------------------------------------ SQL step

        private void SqlStep(StepOut o)
        {
            var x = Inventory.Sql(t, Registry());
            Outcome(o, x, "SQL Server");
            var insts = N.Items(x, "Instances");
            var known = new HashSet<string>(insts.Select(i => N.S(i, "Instance").ToUpperInvariant()));
            foreach (object svc in N.Items(r, "Services"))
            {
                string nm = N.S(svc, "Name");
                if (!((nm == "MSSQLSERVER" || N.Like(nm, "MSSQL$*")) && !N.Like(nm, "*MICROSOFT##*"))) continue;
                string inst = nm == "MSSQLSERVER" ? "MSSQLSERVER" : nm.Substring(6);
                if (known.Contains(inst.ToUpperInvariant())) continue;
                insts.Add(N.Node("Instance", inst, "InstanceId", string.Empty, "Version", string.Empty, "PatchLevel", string.Empty, "Edition", "(details not found in registry)",
                    "DataRoot", string.Empty, "TcpPort", string.Empty, "ServiceName", nm, "AgentServiceName", inst == "MSSQLSERVER" ? "SQLSERVERAGENT" : "SQLAgent$" + inst, "Architecture", string.Empty));
            }
            x["Instances"] = insts;
            o.Set["RoleData.SQL"] = x;
        }
    }
}
