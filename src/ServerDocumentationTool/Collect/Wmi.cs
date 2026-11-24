using System;
using System.Collections;
using System.Collections.Generic;
using System.Management;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool.Collect
{
    /// <summary>
    /// WMI over DCOM (System.Management). Replaces the CIM sessions (WSMan, then DCOM) of the PowerShell engine: no WinRM
    /// is needed. Every query has a hard time limit. Results are converted to nodes: CIM dates become local DateTime
    /// values, arrays stay arrays, references stay path strings.
    /// </summary>
    internal sealed class Wmi
    {
        private readonly Target target;
        private readonly Dictionary<string, ManagementScope> scopes = new Dictionary<string, ManagementScope>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();

        internal Wmi(Target target)
        {
            this.target = target;
        }

        internal ConnectionOptions Options(bool registry64)
        {
            var o = new ConnectionOptions
            {
                Authentication = AuthenticationLevel.PacketPrivacy,
                Impersonation = ImpersonationLevel.Impersonate,
                EnablePrivileges = true,
                Timeout = TimeSpan.FromSeconds(Math.Max(10, target.ConnectTimeoutSec))
            };
            Credential c = target.RemoteCredential;
            if (c != null)
            {
                o.Username = c.FullUser;
                o.Password = c.Password;
            }
            if (registry64)
            {
                o.Context.Add("__ProviderArchitecture", 64);
                o.Context.Add("__RequiredArchitecture", true);
            }
            return o;
        }

        internal ManagementScope Scope(string ns, bool registry64 = false)
        {
            string key = ns + (registry64 ? "|64" : string.Empty);
            lock (gate)
            {
                ManagementScope s;
                if (scopes.TryGetValue(key, out s)) return s;
                string host = target.IsLocal ? "." : target.ComputerName;
                s = new ManagementScope("\\\\" + host + "\\" + ns, Options(registry64));
                Hard.Run(() => s.Connect(), Math.Max(10, target.ConnectTimeoutSec) + 5, "Connecting to WMI (" + ns + ")");
                scopes[key] = s;
                return s;
            }
        }

        /// <summary>Runs a WQL query and returns the rows. Throws on error or when the time limit is reached.</summary>
        internal List<Dictionary<string, object>> Query(string wql, string ns = "root\\cimv2", int timeoutSec = 0)
        {
            int t = timeoutSec > 0 ? timeoutSec : target.OperationTimeoutSec;
            return Hard.Run(() =>
            {
                ManagementScope scope = Scope(ns);
                var opts = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(t), DirectRead = false };
                var rows = new List<Dictionary<string, object>>();
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), opts))
                using (ManagementObjectCollection col = searcher.Get())
                {
                    foreach (ManagementBaseObject mo in col)
                    {
                        using (mo) rows.Add(ToNode(mo));
                    }
                }
                return rows;
            }, t + 5, "WMI query '" + wql + "'");
        }

        /// <summary>Query that returns an empty list instead of throwing (PowerShell -ErrorAction SilentlyContinue).</summary>
        internal List<Dictionary<string, object>> TryQuery(string wql, string ns = "root\\cimv2", int timeoutSec = 0)
        {
            try { return Query(wql, ns, timeoutSec); }
            catch { return new List<Dictionary<string, object>>(); }
        }

        internal static Dictionary<string, object> ToNode(ManagementBaseObject mo)
        {
            var d = N.New();
            foreach (PropertyData p in mo.Properties) d[p.Name] = Convert(p.Value, p.Type, p.IsArray);
            return d;
        }

        private static object Convert(object v, CimType type, bool isArray)
        {
            if (v == null) return null;
            if (type == CimType.DateTime)
            {
                if (isArray)
                {
                    var list = new List<object>();
                    foreach (object o in (IEnumerable)v) list.Add(Date(o as string));
                    return list;
                }
                return Date(v as string);
            }
            if (type == CimType.Object)
            {
                var mo = v as ManagementBaseObject;
                if (mo != null) return ToNode(mo);
                var arr = v as ManagementBaseObject[];
                if (arr != null)
                {
                    var list = new List<object>();
                    foreach (ManagementBaseObject o in arr) list.Add(ToNode(o));
                    return list;
                }
            }
            return v;
        }

        private static object Date(string cim)
        {
            if (string.IsNullOrEmpty(cim)) return null;
            try
            {
                // Intervals (ddddddddHHMMSS.mmmmmm:000) are not dates; keep them as text.
                if (cim.Length >= 22 && cim[21] == ':') return cim;
                return ManagementDateTimeConverter.ToDateTime(cim);
            }
            catch { return cim; }
        }

        /// <summary>Calls a static method of a WMI class (used for StdRegProv). Returns the out parameters.</summary>
        internal ManagementBaseObject InvokeStatic(string ns, string className, string method, IDictionary<string, object> args, bool registry64, int timeoutSec)
        {
            return Hard.Run(() =>
            {
                ManagementScope scope = Scope(ns, registry64);
                using (var mc = new ManagementClass(scope, new ManagementPath(className), null))
                {
                    ManagementBaseObject inp = mc.GetMethodParameters(method);
                    if (args != null) foreach (var kv in args) inp[kv.Key] = kv.Value;
                    var io = new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(timeoutSec) };
                    if (registry64)
                    {
                        io.Context.Add("__ProviderArchitecture", 64);
                        io.Context.Add("__RequiredArchitecture", true);
                    }
                    return mc.InvokeMethod(method, inp, io);
                }
            }, timeoutSec + 5, "WMI " + className + "." + method);
        }

        /// <summary>Calls a no-argument method on each instance a query returns; result: key property -> ReturnValue.</summary>
        internal Dictionary<string, object> InvokeEach(string ns, string wql, string keyProperty, string method, int timeoutSec)
        {
            return Hard.Run(() =>
            {
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                ManagementScope scope = Scope(ns);
                var opts = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(timeoutSec) };
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), opts))
                using (ManagementObjectCollection col = searcher.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        using (mo)
                        {
                            string key = System.Convert.ToString(mo[keyProperty], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                            try
                            {
                                ManagementBaseObject outp = mo.InvokeMethod(method, null, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(timeoutSec) });
                                d[key] = outp == null ? null : outp["ReturnValue"];
                            }
                            catch { d[key] = null; }
                        }
                    }
                }
                return d;
            }, timeoutSec + 5, "WMI " + wql + " " + method);
        }

        /// <summary>Text used to classify a failed connection (HRESULT plus message).</summary>
        internal static string Describe(Exception ex)
        {
            if (ex == null) return string.Empty;
            var me = ex as ManagementException;
            if (me != null) return me.Message.Trim() + " (WMI " + me.ErrorCode + ")";
            var ce = ex as System.Runtime.InteropServices.COMException;
            if (ce != null) return ce.Message.Trim() + " (0x" + ce.ErrorCode.ToString("X8") + ")";
            var ua = ex as UnauthorizedAccessException;
            if (ua != null) return ua.Message.Trim() + " (0x80070005)";
            return ex.Message.Trim();
        }
    }
}
