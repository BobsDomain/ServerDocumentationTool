using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace IABB.App
{
    /// <summary>
    /// Hosts the PowerShell engine (Engine\Modules) inside the app: one Windows PowerShell 5.1 runspace pool with every
    /// engine module imported. Each server runs in its own pipeline, so servers are collected in parallel and a stuck
    /// server can be stopped without holding up the others.
    /// </summary>
    internal sealed class EngineHost : IDisposable
    {
        // Same order as Start-InfrastructureAsBuiltBuilder.ps1 (without the legacy UI module), plus the app entry points.
        internal static readonly string[] ModuleOrder =
        {
            "IABB.Core.psm1", "IABB.Discovery.psm1", "IABB.RoleDetection.psm1",
            "IABB.Collector.ADDS.psm1", "IABB.Collector.DNS.psm1", "IABB.Collector.DHCP.psm1",
            "IABB.Collector.IIS.psm1", "IABB.Collector.SQL.psm1", "IABB.Collector.Applications.psm1",
            "IABB.Collector.Certificates.psm1", "IABB.Collector.Server.psm1", "IABB.DocumentBuilder.psm1",
            "IABB.AppHost.psm1"
        };

        internal static readonly string[] RequiredCommands =
        {
            "Invoke-IABBAppServer", "Invoke-IABBAppFailedServer", "New-IABBAppSummary", "Invoke-IABBServerCollection",
            "Test-IABBCollectionReadiness", "New-IABBHtmlSummary", "New-IABBOpenXmlDocx", "Get-IABBDomainServers",
            "Import-IABBServerList", "ConvertFrom-IABBServerText", "New-IABBRunFolder"
        };

        private readonly RunspacePool pool;

        public EngineHost(string engineRoot, int maxRunspaces)
        {
            EngineRoot = engineRoot;
            var iss = InitialSessionState.CreateDefault();
            // Process-scope policy for the engine only (same effect as starting powershell.exe -ExecutionPolicy Bypass).
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            var paths = new List<string>();
            foreach (string m in ModuleOrder)
            {
                string p = Path.Combine(engineRoot, "Modules", m);
                if (!File.Exists(p)) throw new FileNotFoundException("Engine module is missing: " + p, p);
                paths.Add(p);
            }
            iss.ImportPSModule(paths.ToArray());
            pool = RunspaceFactory.CreateRunspacePool(1, Math.Max(2, maxRunspaces), iss, null);
            pool.ThreadOptions = PSThreadOptions.UseNewThread;
            pool.Open();
        }

        public string EngineRoot { get; private set; }

        public PowerShell Create()
        {
            var ps = PowerShell.Create();
            ps.RunspacePool = pool;
            return ps;
        }

        /// <summary>Checks that every engine function the app calls was loaded. Returns the missing names.</summary>
        public IList<string> FindMissingCommands()
        {
            var missing = new List<string>();
            foreach (string name in RequiredCommands)
            {
                using (var ps = Create())
                {
                    ps.AddCommand("Get-Command").AddParameter("Name", name).AddParameter("ErrorAction", "SilentlyContinue");
                    if (ps.Invoke().Count == 0) missing.Add(name);
                }
            }
            return missing;
        }

        /// <summary>Runs one engine command synchronously (call from a background thread).</summary>
        public Collection<PSObject> Run(string command, IDictionary<string, object> parameters)
        {
            using (var ps = Create())
            {
                ps.AddCommand(command);
                if (parameters != null)
                {
                    foreach (var kv in parameters)
                    {
                        if (kv.Value != null) ps.AddParameter(kv.Key, kv.Value);
                    }
                }
                Collection<PSObject> result = ps.Invoke();
                if (ps.HadErrors && result.Count == 0 && ps.Streams.Error.Count > 0)
                {
                    ErrorRecord er = ps.Streams.Error[0];
                    throw new InvalidOperationException(command + ": " + (er.Exception != null ? er.Exception.Message : er.ToString()));
                }
                return result;
            }
        }

        public void Dispose()
        {
            try { pool.Close(); } catch { }
            try { pool.Dispose(); } catch { }
        }

        // ---- PSObject helpers ----

        internal static object Unwrap(object value)
        {
            var pso = value as PSObject;
            return pso != null ? pso.BaseObject : value;
        }

        internal static object Prop(PSObject obj, string name)
        {
            if (obj == null) return null;
            PSPropertyInfo p = obj.Properties[name];
            return p == null ? null : p.Value;
        }

        internal static string Str(PSObject obj, string name)
        {
            object v = Unwrap(Prop(obj, name));
            return v == null ? string.Empty : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        internal static bool Bool(PSObject obj, string name)
        {
            object v = Unwrap(Prop(obj, name));
            if (v is bool) return (bool)v;
            bool b;
            return v != null && bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b) && b;
        }

        internal static string[] StrArray(PSObject obj, string name)
        {
            object v = Unwrap(Prop(obj, name));
            if (v == null) return new string[0];
            var s = v as string;
            if (s != null) return new[] { s };
            var e = v as IEnumerable;
            if (e == null) return new[] { Convert.ToString(v, CultureInfo.InvariantCulture) };
            var list = new List<string>();
            foreach (object item in e)
            {
                object u = Unwrap(item);
                if (u != null) list.Add(Convert.ToString(u, CultureInfo.InvariantCulture));
            }
            return list.ToArray();
        }

        internal static PSObject AsPSObject(object value)
        {
            if (value == null) return null;
            return value as PSObject ?? PSObject.AsPSObject(value);
        }

        internal static IList<PSObject> NonNull(IEnumerable<PSObject> items)
        {
            return items == null ? new List<PSObject>() : items.Where(i => i != null).ToList();
        }
    }
}
