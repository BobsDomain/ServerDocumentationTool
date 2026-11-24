using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool.Collect
{
    /// <summary>Applications, certificates and SQL Server instances, read from the registry (no WinRM).</summary>
    internal static class Inventory
    {
        private static readonly StringComparer Cmp = StringComparer.Create(CultureInfo.InvariantCulture, true);

        private static Dictionary<string, object> Fail(Dictionary<string, object> r, Exception ex)
        {
            r["Success"] = false;
            r["Error"] = Wmi.Describe(ex);
            return r;
        }

        // ------------------------------------------------------------------ applications (port of Invoke-IABBApplicationCollection)

        internal static Dictionary<string, object> Applications(Target t, IRegistryReader reg)
        {
            var r = N.Node("Success", false, "ComputerName", t.ComputerName, "Applications", new List<object>(), "HiddenEntries", new List<object>(), "Summary", null,
                "Error", null, "TimedOut", false, "Method", reg.Method);
            try
            {
                var all = new List<Dictionary<string, object>>();
                var paths = new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
                // v2.0.1: only the values the report uses, DisplayName first (keys without one are skipped after one call),
                // several keys at a time.
                var names = new[] { "DisplayName", "Publisher", "DisplayVersion", "InstallDate", "SystemComponent", "ParentKeyName", "ReleaseType" };
                var keys = new List<string[]>();
                foreach (string p in paths)
                {
                    string arch = p.IndexOf("WOW6432Node", StringComparison.OrdinalIgnoreCase) >= 0 ? "32-bit" : "64-bit";
                    foreach (string sub in reg.SubKeys(p)) keys.Add(new[] { p + "\\" + sub, arch });
                }
                var errors = new List<Exception>();
                var values = RegistryAccess.ParallelMap(keys, reg.Parallelism, k => reg.Values(k[0], names, true), errors);
                if (keys.Count > 0 && errors.Count == keys.Count) throw errors[0];
                for (int i = 0; i < keys.Count; i++)
                {
                    var v = values[i];
                    if (v == null) continue;
                    string arch = keys[i][1];
                    object dnObj; v.TryGetValue("DisplayName", out dnObj);
                    string dn = N.S(dnObj);
                    if (string.IsNullOrEmpty(dn)) continue;
                    Func<string, string> val = n => { object o; return v.TryGetValue(n, out o) ? N.S(o) : string.Empty; };
                    string raw = val("InstallDate").Trim(), iso = raw;
                    Match m = Regex.Match(raw, @"^(\d{4})(\d{2})(\d{2})$");
                    if (m.Success) iso = m.Groups[1].Value + "-" + m.Groups[2].Value + "-" + m.Groups[3].Value;
                    bool hidden = val("SystemComponent") == "1" || !string.IsNullOrWhiteSpace(val("ParentKeyName")) ||
                                  Regex.IsMatch(val("ReleaseType"), "Update|Hotfix", RegexOptions.IgnoreCase) ||
                                  Regex.IsMatch(dn, "^(Security )?Update for |^Hotfix for ", RegexOptions.IgnoreCase);
                    all.Add(N.Node("Name", dn, "Publisher", val("Publisher"), "Version", val("DisplayVersion"), "InstallDate", iso, "Architecture", arch, "Hidden", hidden));
                }
                if (errors.Count > 0) r["Notes"] = new List<object> { errors.Count + " of " + keys.Count + " uninstall entries could not be read: " + errors[0].Message };
                Func<IEnumerable<Dictionary<string, object>>, List<object>> uniq = items => items
                    .OrderBy(a => N.S(a, "Name"), Cmp).ThenBy(a => N.S(a, "Version"), Cmp)
                    .GroupBy(a => N.S(a, "Name").ToLowerInvariant() + "\u0001" + N.S(a, "Version").ToLowerInvariant()).Select(g => g.First())
                    .Select(a => (object)N.Node("Name", a["Name"], "Publisher", a["Publisher"], "Version", a["Version"], "InstallDate", a["InstallDate"], "Architecture", a["Architecture"])).ToList();
                var apps = uniq(all.Where(a => !(bool)a["Hidden"]));
                var hiddenList = uniq(all.Where(a => (bool)a["Hidden"]));
                r["Applications"] = apps;
                r["HiddenEntries"] = hiddenList;
                r["Summary"] = N.Node("TotalApplications", apps.Count, "Publishers", apps.Select(a => N.S(a, "Publisher")).Where(x => x.Length > 0).Distinct().Count(), "HiddenEntries", hiddenList.Count);
                r["Success"] = true;
                return r;
            }
            catch (Exception ex) { return Fail(r, ex); }
        }

        // ------------------------------------------------------------------ certificates (port of Invoke-IABBCertificateCollection)

        private static readonly string[] Stores = { "My", "Root", "CA", "WebHosting" };

        internal static Dictionary<string, object> Certificates(Target t, IRegistryReader reg, Func<IRegistryReader> fallback = null)
        {
            var r = N.Node("Success", false, "ComputerName", t.ComputerName, "Certificates", new List<object>(), "Summary", null, "Error", null, "TimedOut", false, "Method", null, "Notes", new List<object>());
            try
            {
                DateTime now = DateTime.Now;
                var certs = new List<object>();
                if (t.IsLocal)
                {
                    r["Method"] = "Local certificate store";
                    foreach (string store in Stores)
                    {
                        X509Store st = new X509Store(store, StoreLocation.LocalMachine);
                        try { st.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
                        catch { continue; }
                        try
                        {
                            foreach (X509Certificate2 c in st.Certificates)
                                certs.Add(Row(store, c.FriendlyName, c, c.HasPrivateKey, now));
                        }
                        finally { st.Close(); }
                    }
                }
                else
                {
                    certs = RegistryCertificates(reg, now);
                    r["Method"] = "Registry (" + reg.Method + ")";
                    // v2.0.1: a server always has Root certificates. Nothing read means the read did not work (5 servers in the
                    // v2.0.0 field test): try again through the WMI registry provider before reporting no data.
                    if (certs.Count == 0 && fallback != null)
                    {
                        IRegistryReader alt = fallback();
                        if (alt != null && alt.Method != reg.Method)
                        {
                            certs = RegistryCertificates(alt, now);
                            r["Method"] = "Registry (" + alt.Method + ")";
                            ((List<object>)r["Notes"]).Add("The " + reg.Method + " returned no certificates; they were read through the " + alt.Method + ".");
                        }
                    }
                }
                var list = certs.Where(c => N.Truthy(N.Get(c, "Thumbprint"))).ToList();
                r["Certificates"] = list;
                r["Summary"] = N.Node("TotalCertificates", list.Count,
                    "ExpiringWithin60Days", list.Count(c => { double d = (double)N.Get(c, "DaysUntilExpiry"); return d >= 0 && d <= 60; }),
                    "Expired", list.Count(c => (double)N.Get(c, "DaysUntilExpiry") < 0));
                r["Success"] = true;
                return r;
            }
            catch (Exception ex) { return Fail(r, ex); }
        }

        /// <summary>The LocalMachine stores from the registry (the physical stores Windows combines into each logical store).
        /// Archived certificates (replaced by a renewal) are left out, as Windows does.</summary>
        private static List<object> RegistryCertificates(IRegistryReader reg, DateTime now)
        {
            var certs = new List<object>();
            var blobName = new[] { "Blob" };
            foreach (string store in Stores)
            {
                var roots = new List<string> { @"SOFTWARE\Microsoft\SystemCertificates\" + store, @"SOFTWARE\Policies\Microsoft\SystemCertificates\" + store, @"SOFTWARE\Microsoft\EnterpriseCertificates\" + store };
                if (store == "Root") roots.Add(@"SOFTWARE\Microsoft\SystemCertificates\AuthRoot");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var paths = new List<string>();
                foreach (string root in roots)
                {
                    string key = root + "\\Certificates";
                    foreach (string thumb in reg.SubKeys(key)) if (seen.Add(thumb)) paths.Add(key + "\\" + thumb);
                }
                var errors = new List<Exception>();
                var blobs = RegistryAccess.ParallelMap(paths, reg.Parallelism, p => reg.Values(p, blobName), errors);
                if (paths.Count > 0 && errors.Count == paths.Count) throw errors[0];
                foreach (var v in blobs)
                {
                    object o;
                    if (v == null || !v.TryGetValue("Blob", out o)) continue;
                    byte[] blob = o as byte[];
                    if (blob == null) continue;
                    string friendly; bool hasKey, archived;
                    byte[] der = ParseBlob(blob, out friendly, out hasKey, out archived);
                    if (der == null || archived) continue;
                    X509Certificate2 c;
                    try { c = new X509Certificate2(der); } catch { continue; }
                    certs.Add(Row(store, friendly, c, hasKey, now));
                }
            }
            return certs;
        }

        private static object Row(string store, string friendly, X509Certificate2 c, bool hasKey, DateTime now)
        {
            return N.Node("Store", store, "FriendlyName", friendly ?? string.Empty, "Subject", c.Subject ?? string.Empty, "Issuer", c.Issuer ?? string.Empty,
                "Thumbprint", c.Thumbprint ?? string.Empty, "NotAfter", c.NotAfter, "DaysUntilExpiry", Math.Round((c.NotAfter - now).TotalDays, 0), "HasPrivateKey", hasKey);
        }

        /// <summary>
        /// The "Blob" value of a certificate in the registry is a list of properties: id (uint32), reserved (uint32),
        /// length (uint32), data. Property 0x20 is the DER-encoded certificate, 11 the friendly name, 2 the private key link,
        /// 19 the "archived" mark (a certificate replaced by its renewal; hidden by Windows).
        /// </summary>
        internal static byte[] ParseBlob(byte[] blob, out string friendly, out bool hasKey, out bool archived)
        {
            friendly = string.Empty; hasKey = false; archived = false;
            byte[] der = null;
            int i = 0;
            while (i + 12 <= blob.Length)
            {
                uint id = BitConverter.ToUInt32(blob, i);
                int len = BitConverter.ToInt32(blob, i + 8);
                i += 12;
                if (len < 0 || i + len > blob.Length) break;
                if (id == 0x20) { der = new byte[len]; Buffer.BlockCopy(blob, i, der, 0, len); }
                else if (id == 11) friendly = Encoding.Unicode.GetString(blob, i, len).TrimEnd('\0');
                else if (id == 2) hasKey = true;
                else if (id == 19) archived = true;
                i += len;
            }
            return der;
        }

        // ------------------------------------------------------------------ SQL Server (port of Invoke-IABBSqlCollection)

        internal static Dictionary<string, object> Sql(Target t, IRegistryReader reg)
        {
            var r = N.Node("Success", false, "TimedOut", false, "Instances", new List<object>(), "Error", null, "Method", reg.Method);
            try
            {
                var roots = new[] { new[] { @"SOFTWARE\Microsoft\Microsoft SQL Server", "64-bit" }, new[] { @"SOFTWARE\WOW6432Node\Microsoft\Microsoft SQL Server", "32-bit" } };
                var rows = new List<object>(); var seen = new HashSet<string>();
                foreach (string[] rt in roots)
                {
                    string b = rt[0], arch = rt[1];
                    var names = reg.AllValues(b + @"\Instance Names\SQL");
                    foreach (var kv in names)
                    {
                        string inst = kv.Key, id = N.S(kv.Value);
                        if (inst.Length == 0 || N.Like(inst, "*MICROSOFT##*")) continue;
                        if (!seen.Add(inst.ToUpperInvariant())) continue;
                        var setup = reg.Values(b + "\\" + id + @"\Setup", new[] { "Version", "PatchLevel", "Edition", "SQLDataRoot" });
                        var tcp = reg.Values(b + "\\" + id + @"\MSSQLServer\SuperSocketNetLib\Tcp\IPAll", new[] { "TcpPort", "TcpDynamicPorts" });
                        Func<Dictionary<string, object>, string, string> val = (d, n) => { object o; return d.TryGetValue(n, out o) ? N.S(o) : string.Empty; };
                        string port = string.Empty;
                        if (tcp.Count > 0)
                        {
                            string st = val(tcp, "TcpPort").Trim(), dyn = val(tcp, "TcpDynamicPorts").Trim();
                            if (st != string.Empty) port = st;
                            else if (dyn != string.Empty) port = dyn == "0" ? "Dynamic" : "Dynamic (" + dyn + ")";
                        }
                        rows.Add(N.Node("Instance", inst, "InstanceId", id, "Version", val(setup, "Version"), "PatchLevel", val(setup, "PatchLevel"), "Edition", val(setup, "Edition"),
                            "DataRoot", val(setup, "SQLDataRoot"), "TcpPort", port, "ServiceName", inst == "MSSQLSERVER" ? "MSSQLSERVER" : "MSSQL$" + inst,
                            "AgentServiceName", inst == "MSSQLSERVER" ? "SQLSERVERAGENT" : "SQLAgent$" + inst, "Architecture", arch));
                    }
                }
                r["Instances"] = rows;
                r["Success"] = true;
                return r;
            }
            catch (Exception ex) { return Fail(r, ex); }
        }
    }
}
