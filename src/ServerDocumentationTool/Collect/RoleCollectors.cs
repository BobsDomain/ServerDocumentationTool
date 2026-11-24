using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.DirectoryServices;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool.Collect
{
    /// <summary>AD DS, DNS, DHCP and IIS collectors. Same output fields as the PowerShell collectors; no WinRM or RSAT needed.</summary>
    internal static class RoleCollectors
    {
        private static Dictionary<string, object> Wrap(Target t, string method)
        {
            return N.Node("Success", false, "Error", null, "ComputerName", t.ComputerName, "TimedOut", false, "Method", method, "Notes", new List<object>());
        }

        // ------------------------------------------------------------------ AD DS (LDAP to the target domain controller)

        private static string Mode(object level, string kind, bool mixed)
        {
            int v; if (!N.TryInt(level, out v)) return N.S(level);
            switch (v)
            {
                case 0: return kind == "Forest" ? "Windows2000Forest" : mixed ? "Windows2000MixedDomain" : "Windows2000NativeDomain";
                case 1: return "Windows2003Interim" + kind;
                case 2: return "Windows2003" + kind;
                case 3: return "Windows2008" + kind;
                case 4: return "Windows2008R2" + kind;
                case 5: return "Windows2012" + kind;
                case 6: return "Windows2012R2" + kind;
                case 7: return "Windows2016" + kind;
                case 10: return "Windows2025" + kind;
                default: return N.S(level);
            }
        }

        /// <summary>RFC 4515 escaping of a value used in an LDAP filter.</summary>
        internal static string LdapEscape(string v)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in v ?? string.Empty)
            {
                if (ch == '\\' || ch == '*' || ch == '(' || ch == ')' || ch == '\0') sb.Append('\\').Append(((int)ch).ToString("x2"));
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string DnToDns(string dn)
        {
            return string.Join(".", Regex.Matches(dn ?? string.Empty, @"DC=([^,]+)", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value));
        }

        internal static Dictionary<string, object> Adds(Target t)
        {
            var r = Wrap(t, "LDAP to the domain controller");
            r["Forest"] = null; r["Domain"] = null; r["FSMORoles"] = null; r["DomainControllers"] = new List<object>();
            string host = t.HostName;
            Credential c = t.RemoteCredential;
            Func<string, DirectoryEntry> entry = dn => c != null
                ? new DirectoryEntry("LDAP://" + host + "/" + dn, c.FullUser, c.Password, AuthenticationTypes.Secure)
                : new DirectoryEntry("LDAP://" + host + "/" + dn, null, null, AuthenticationTypes.Secure);
            Func<string, string, string> attr = (dn, name) =>
            {
                using (DirectoryEntry e = entry(dn)) { object v = e.Properties[name].Value; return v == null ? string.Empty : Convert.ToString(v, CultureInfo.InvariantCulture); }
            };
            try
            {
                string defaultNc, configNc, schemaNc, rootNc; object domLevel, forLevel;
                using (DirectoryEntry dse = entry("RootDSE"))
                {
                    defaultNc = N.S(dse.Properties["defaultNamingContext"].Value);
                    configNc = N.S(dse.Properties["configurationNamingContext"].Value);
                    schemaNc = N.S(dse.Properties["schemaNamingContext"].Value);
                    rootNc = N.S(dse.Properties["rootDomainNamingContext"].Value);
                    domLevel = dse.Properties["domainFunctionality"].Value;
                    forLevel = dse.Properties["forestFunctionality"].Value;
                }
                bool mixed = attr(defaultNc, "nTMixedDomain") == "1";
                string netbios = string.Empty;
                using (DirectoryEntry parts = entry("CN=Partitions," + configNc))
                using (var ds = new DirectorySearcher(parts, "(&(objectClass=crossRef)(nCName=" + LdapEscape(defaultNc) + "))", new[] { "nETBIOSName" }, SearchScope.OneLevel))
                {
                    SearchResult sr = ds.FindOne();
                    if (sr != null && sr.Properties["nETBIOSName"].Count > 0) netbios = N.S(sr.Properties["nETBIOSName"][0]);
                }
                Func<string, string> owner = dn =>
                {
                    string ntds = attr(dn, "fSMORoleOwner");
                    if (string.IsNullOrEmpty(ntds)) return string.Empty;
                    string server = ntds.Substring(ntds.IndexOf(',') + 1);
                    string h = attr(server, "dNSHostName");
                    return string.IsNullOrEmpty(h) ? server : h;
                };
                r["Forest"] = N.Node("Name", DnToDns(rootNc), "ForestMode", Mode(forLevel, "Forest", false));
                r["Domain"] = N.Node("DNSRoot", DnToDns(defaultNc), "NetBIOSName", netbios, "DomainMode", Mode(domLevel, "Domain", mixed));
                r["FSMORoles"] = N.Node("SchemaMaster", owner(schemaNc), "DomainNamingMaster", owner("CN=Partitions," + configNc),
                    "RIDMaster", owner("CN=RID Manager$,CN=System," + defaultNc), "PDCEmulator", owner(defaultNc), "InfrastructureMaster", owner("CN=Infrastructure," + defaultNc));

                // Sites and global catalogs from the configuration partition, DCs from the domain.
                var servers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase); // server DN -> host, site
                var gcServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (DirectoryEntry sites = entry("CN=Sites," + configNc))
                {
                    using (var ds = new DirectorySearcher(sites, "(objectClass=server)", new[] { "dNSHostName", "distinguishedName" }, SearchScope.Subtree) { PageSize = 500 })
                    using (SearchResultCollection all = ds.FindAll())
                    {
                        foreach (SearchResult sr in all)
                        {
                            string dn = sr.Properties["distinguishedName"].Count > 0 ? N.S(sr.Properties["distinguishedName"][0]) : string.Empty;
                            string h = sr.Properties["dNSHostName"].Count > 0 ? N.S(sr.Properties["dNSHostName"][0]) : string.Empty;
                            string[] p = dn.Split(',');
                            string site = p.Length > 2 ? Regex.Replace(p[2], "^CN=", string.Empty, RegexOptions.IgnoreCase) : string.Empty;
                            servers[dn] = new[] { h, site };
                        }
                    }
                    using (var ds = new DirectorySearcher(sites, "(objectCategory=nTDSDSA)", new[] { "options", "distinguishedName" }, SearchScope.Subtree) { PageSize = 500 })
                    using (SearchResultCollection all = ds.FindAll())
                    {
                        foreach (SearchResult sr in all)
                        {
                            int opt = sr.Properties["options"].Count > 0 ? N.ToInt(sr.Properties["options"][0]) : 0;
                            string dn = sr.Properties["distinguishedName"].Count > 0 ? N.S(sr.Properties["distinguishedName"][0]) : string.Empty;
                            string server = dn.Substring(dn.IndexOf(',') + 1);
                            string[] info;
                            if ((opt & 1) == 1 && servers.TryGetValue(server, out info) && info[0].Length > 0) gcServers.Add(info[0]);
                        }
                    }
                }
                var dcs = new List<object>();
                using (DirectoryEntry dom = entry(defaultNc))
                using (var ds = new DirectorySearcher(dom, "(&(objectCategory=computer)(|(userAccountControl:1.2.840.113556.1.4.803:=8192)(userAccountControl:1.2.840.113556.1.4.803:=67108864)))",
                    new[] { "dNSHostName", "operatingSystem", "serverReferenceBL" }, SearchScope.Subtree) { PageSize = 500 })
                using (SearchResultCollection all = ds.FindAll())
                {
                    foreach (SearchResult sr in all)
                    {
                        string h = sr.Properties["dNSHostName"].Count > 0 ? N.S(sr.Properties["dNSHostName"][0]) : string.Empty;
                        string sref = sr.Properties["serverReferenceBL"].Count > 0 ? N.S(sr.Properties["serverReferenceBL"][0]) : string.Empty;
                        string[] info; string site = servers.TryGetValue(sref, out info) ? info[1] : string.Empty;
                        dcs.Add(N.Node("HostName", h, "Site", site, "OperatingSystem", sr.Properties["operatingSystem"].Count > 0 ? N.S(sr.Properties["operatingSystem"][0]) : string.Empty,
                            "IsGlobalCatalog", gcServers.Contains(h)));
                    }
                }
                r["DomainControllers"] = dcs.OrderBy(d => N.S(d, "HostName"), StringComparer.OrdinalIgnoreCase).ToList();
                r["Success"] = true;
            }
            catch (Exception ex) { r["Error"] = Describe(ex); }
            return r;
        }

        private static string Describe(Exception ex)
        {
            var ce = ex as COMException;
            if (ce != null) return ce.Message.Trim() + " (0x" + ce.ErrorCode.ToString("X8") + ")";
            return Wmi.Describe(ex);
        }

        // ------------------------------------------------------------------ DNS (WMI root\MicrosoftDNS, 120 s limit)

        internal static Dictionary<string, object> Dns(Target t, Wmi wmi)
        {
            const int T = 120;
            var r = Wrap(t, "WMI (root\\MicrosoftDNS)");
            r["Zones"] = new List<object>(); r["Forwarders"] = new List<object>(); r["Source"] = null;
            try
            {
                var zt = new Dictionary<string, string> { { "1", "Primary" }, { "2", "Secondary" }, { "3", "Stub" }, { "4", "Forwarder" } };
                var du = new Dictionary<string, string> { { "0", "None" }, { "1", "NonsecureAndSecure" }, { "2", "Secure" } };
                List<Dictionary<string, object>> zones;
                try { zones = wmi.Query("SELECT Name, ZoneType, DsIntegrated, Reverse, AllowUpdate FROM MicrosoftDNS_Zone", "root\\MicrosoftDNS", T); }
                catch (HardTimeoutException) { throw new TimeoutException("The DNS Server did not answer the zone query within " + T + " seconds (DNS WMI provider not responding)."); }
                catch (ManagementException me) when (me.ErrorCode == ManagementStatus.InvalidNamespace || me.ErrorCode == ManagementStatus.InvalidClass)
                { throw new InvalidOperationException("The DNS Server WMI provider (root\\MicrosoftDNS) is not available on this server."); }
                var cmp = StringComparer.Create(CultureInfo.InvariantCulture, true);
                r["Zones"] = zones.Where(z => N.S(z, "ZoneType") != "0" && !N.S(z, "Name").StartsWith("..", StringComparison.Ordinal)).OrderBy(z => N.S(z, "Name"), cmp).Select(z =>
                {
                    string ty = N.S(z, "ZoneType"), u = N.S(z, "AllowUpdate"), m1, m2;
                    return (object)N.Node("ZoneName", N.S(z, "Name"), "ZoneType", zt.TryGetValue(ty, out m1) ? m1 : ty, "IsDsIntegrated", N.IsTrue(N.Get(z, "DsIntegrated")),
                        "IsReverseLookupZone", N.IsTrue(N.Get(z, "Reverse")), "DynamicUpdate", du.TryGetValue(u, out m2) ? m2 : u);
                }).ToList();
                r["Source"] = "DNS WMI provider (root\\MicrosoftDNS)";
                try
                {
                    var srv = wmi.Query("SELECT Forwarders FROM MicrosoftDNS_Server", "root\\MicrosoftDNS", T).FirstOrDefault();
                    r["Forwarders"] = N.Items(srv, "Forwarders").Select(N.S).Where(x => x.Length > 0).Cast<object>().ToList();
                }
                catch { }
                r["Success"] = true;
            }
            catch (Exception ex) { r["Error"] = Wmi.Describe(ex); }
            return r;
        }

        // ------------------------------------------------------------------ DHCP (DHCP Server management API, dhcpsapi.dll)

        [StructLayout(LayoutKind.Sequential)] private struct DHCP_IP_ARRAY { public uint NumElements; public IntPtr Elements; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DHCP_HOST_INFO { public uint IpAddress; public string NetBiosName; public string HostName; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DHCP_SUBNET_INFO { public uint SubnetAddress; public uint SubnetMask; public string SubnetName; public string SubnetComment; public DHCP_HOST_INFO PrimaryHost; public int SubnetState; }
        [StructLayout(LayoutKind.Sequential)] private struct DHCP_SUBNET_ELEMENT_INFO_ARRAY { public uint NumElements; public IntPtr Elements; }
        [StructLayout(LayoutKind.Sequential)] private struct DHCP_SUBNET_ELEMENT_DATA { public int ElementType; public IntPtr Element; }
        [StructLayout(LayoutKind.Sequential)] private struct DHCP_IP_RANGE { public uint StartAddress; public uint EndAddress; }

        [DllImport("dhcpsapi.dll", CharSet = CharSet.Unicode)] private static extern uint DhcpEnumSubnets(string server, ref uint resume, uint preferredMax, out IntPtr enumInfo, out uint read, out uint total);
        [DllImport("dhcpsapi.dll", CharSet = CharSet.Unicode)] private static extern uint DhcpGetSubnetInfo(string server, uint subnet, out IntPtr info);
        [DllImport("dhcpsapi.dll", CharSet = CharSet.Unicode)] private static extern uint DhcpEnumSubnetElements(string server, uint subnet, int type, ref uint resume, uint preferredMax, out IntPtr info, out uint read, out uint total);
        [DllImport("dhcpsapi.dll")] private static extern void DhcpRpcFreeMemory(IntPtr p);

        private const uint ERROR_MORE_DATA = 234, ERROR_NO_MORE_ITEMS = 259;

        internal static string Ip(uint ip)
        {
            return ((ip >> 24) & 255) + "." + ((ip >> 16) & 255) + "." + ((ip >> 8) & 255) + "." + (ip & 255);
        }

        internal static Dictionary<string, object> Dhcp(Target t)
        {
            var r = Wrap(t, "DHCP Server management API (RPC)");
            r["Scopes"] = new List<object>(); r["Reservations"] = new List<object>(); r["ServerOptions"] = new List<object>(); r["Status"] = "Not collected"; r["Recommendation"] = null;
            try
            {
                string server = t.IsLocal ? null : t.ComputerName;
                var scopes = NetworkAccess.Run(t.RemoteCredential, () =>
                {
                    var subnets = new List<uint>();
                    uint resume = 0;
                    while (true)
                    {
                        IntPtr p; uint read, total;
                        uint rc = DhcpEnumSubnets(server, ref resume, 65536, out p, out read, out total);
                        if (rc == ERROR_NO_MORE_ITEMS) break;
                        if (rc != 0 && rc != ERROR_MORE_DATA) throw new Win32Exception((int)rc);
                        try
                        {
                            var arr = (DHCP_IP_ARRAY)Marshal.PtrToStructure(p, typeof(DHCP_IP_ARRAY));
                            for (int i = 0; i < arr.NumElements; i++) subnets.Add(unchecked((uint)Marshal.ReadInt32(arr.Elements, i * 4)));
                        }
                        finally { if (p != IntPtr.Zero) DhcpRpcFreeMemory(p); }
                        if (rc != ERROR_MORE_DATA) break;
                    }
                    var list = new List<object>();
                    foreach (uint sn in subnets)
                    {
                        IntPtr ip; string name = Ip(sn); string state = string.Empty;
                        uint rc = DhcpGetSubnetInfo(server, sn, out ip);
                        if (rc == 0 && ip != IntPtr.Zero)
                        {
                            try
                            {
                                var info = (DHCP_SUBNET_INFO)Marshal.PtrToStructure(ip, typeof(DHCP_SUBNET_INFO));
                                if (!string.IsNullOrEmpty(info.SubnetName)) name = info.SubnetName;
                                state = info.SubnetState == 0 || info.SubnetState == 2 ? "Active" : info.SubnetState == 1 || info.SubnetState == 3 ? "Inactive" : "Invalid";
                            }
                            finally { DhcpRpcFreeMemory(ip); }
                        }
                        string start = string.Empty, end = string.Empty;
                        uint res2 = 0; IntPtr ep; uint r2, t2;
                        uint rc2 = DhcpEnumSubnetElements(server, sn, 0, ref res2, 65536, out ep, out r2, out t2);
                        if ((rc2 == 0 || rc2 == ERROR_MORE_DATA) && ep != IntPtr.Zero)
                        {
                            try
                            {
                                var arr = (DHCP_SUBNET_ELEMENT_INFO_ARRAY)Marshal.PtrToStructure(ep, typeof(DHCP_SUBNET_ELEMENT_INFO_ARRAY));
                                int size = Marshal.SizeOf(typeof(DHCP_SUBNET_ELEMENT_DATA));
                                for (int i = 0; i < arr.NumElements; i++)
                                {
                                    var el = (DHCP_SUBNET_ELEMENT_DATA)Marshal.PtrToStructure(new IntPtr(arr.Elements.ToInt64() + i * size), typeof(DHCP_SUBNET_ELEMENT_DATA));
                                    if (el.ElementType != 0 || el.Element == IntPtr.Zero) continue;
                                    var range = (DHCP_IP_RANGE)Marshal.PtrToStructure(el.Element, typeof(DHCP_IP_RANGE));
                                    start = Ip(range.StartAddress); end = Ip(range.EndAddress);
                                    break;
                                }
                            }
                            finally { DhcpRpcFreeMemory(ep); }
                        }
                        list.Add(N.Node("ScopeId", Ip(sn), "Name", name, "StartRange", start, "EndRange", end, "State", state));
                    }
                    return list;
                });
                r["Scopes"] = scopes;
                r["Success"] = true; r["Status"] = "Collected";
            }
            catch (Exception ex)
            {
                r["Error"] = ex is DllNotFoundException ? "The DHCP Server management API (dhcpsapi.dll) is not available on the computer running the tool." : Wmi.Describe(ex);
                r["Recommendation"] = "Check that the account is a member of DHCP Administrators or local Administrators on the target and that RPC to the server is allowed.";
            }
            return r;
        }

        // ------------------------------------------------------------------ IIS (applicationHost.config + IIS WMI provider for state)

        private static string BindingHost(string info)
        {
            Match m = Regex.Match(info ?? string.Empty, @"^(.*):(\d+):(.*)$");
            return m.Success ? m.Groups[3].Value : string.Empty;
        }

        private static string A(XElement e, string name, string fallback)
        {
            if (e == null) return fallback;
            XAttribute a = e.Attribute(name);
            return a != null ? a.Value : fallback;
        }

        private static string StateText(object rv)
        {
            switch (N.S(rv))
            {
                case "0": return "Starting";
                case "1": return "Started";
                case "2": return "Stopping";
                case "3": return "Stopped";
                default: return "Unknown";
            }
        }

        internal static Dictionary<string, object> Iis(Target t, Wmi wmi, IRegistryReader reg)
        {
            var r = N.Node("Success", false, "Version", null, "Sites", new List<object>(), "AppPools", new List<object>(), "Bindings", new List<object>(),
                "VirtualDirectories", new List<object>(), "Error", null, "TimedOut", false, "Method", null, "Notes", new List<object>());
            try
            {
                try
                {
                    var v = reg.Values(@"SOFTWARE\Microsoft\InetStp");
                    object maj, min;
                    if (v.TryGetValue("MajorVersion", out maj)) r["Version"] = N.S(maj) + "." + (v.TryGetValue("MinorVersion", out min) ? N.S(min) : "0");
                }
                catch { }

                string path, text;
                if (t.IsLocal)
                {
                    string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    path = Path.Combine(win, Environment.Is64BitProcess ? "System32" : "Sysnative", @"inetsrv\config\applicationHost.config");
                    text = File.ReadAllText(path);
                    r["Method"] = "applicationHost.config (local)";
                }
                else
                {
                    path = "\\\\" + t.ComputerName + @"\ADMIN$\System32\inetsrv\config\applicationHost.config";
                    try { text = NetworkAccess.Run(t.RemoteCredential, () => File.ReadAllText(path)); }
                    catch (Exception ex) { throw new IOException("The IIS configuration could not be read from the admin share (" + path + "): " + ex.Message.Trim(), ex); }
                    r["Method"] = "applicationHost.config (admin share)";
                }

                Dictionary<string, object> siteStates = null, poolStates = null;
                try
                {
                    siteStates = wmi.InvokeEach("root\\WebAdministration", "SELECT Name FROM Site", "Name", "GetState", 60);
                    poolStates = wmi.InvokeEach("root\\WebAdministration", "SELECT Name FROM ApplicationPool", "Name", "GetState", 60);
                }
                catch { ((List<object>)r["Notes"]).Add("IIS site and application pool state not available (the IIS WMI provider is not installed on the server); state shows Unknown."); }
                ParseIisConfig(text, siteStates, poolStates, r);
                r["Success"] = true;
            }
            catch (Exception ex) { r["Error"] = Wmi.Describe(ex); }
            return r;
        }

        /// <summary>Sites, bindings, application pools and applications from applicationHost.config (states from the IIS WMI provider, if any).</summary>
        internal static void ParseIisConfig(string text, Dictionary<string, object> siteStates, Dictionary<string, object> poolStates, Dictionary<string, object> r)
        {
            XElement root = XDocument.Parse(text).Root;
            XElement ah = root.Element("system.applicationHost");
            XElement pools = ah != null ? ah.Element("applicationPools") : null;
            XElement poolDef = pools != null ? pools.Element("applicationPoolDefaults") : null;
            string defRuntime = A(poolDef, "managedRuntimeVersion", "v4.0"), defPipeline = A(poolDef, "managedPipelineMode", "Integrated");
            string defIdentity = A(poolDef != null ? poolDef.Element("processModel") : null, "identityType", "ApplicationPoolIdentity");
            var poolRows = new List<object>();
            if (pools != null)
            {
                foreach (XElement p in pools.Elements("add"))
                {
                    string n = A(p, "name", string.Empty); object st = null;
                    poolRows.Add(N.Node("Name", n, "State", poolStates != null && poolStates.TryGetValue(n, out st) ? StateText(st) : "Unknown",
                        "ManagedRuntimeVersion", A(p, "managedRuntimeVersion", defRuntime), "ManagedPipelineMode", A(p, "managedPipelineMode", defPipeline),
                        "Identity", A(p.Element("processModel"), "identityType", defIdentity)));
                }
            }
            XElement sites = ah != null ? ah.Element("sites") : null;
            string defPool = A(sites != null ? sites.Element("applicationDefaults") : null, "applicationPool", "DefaultAppPool");
            var siteRows = new List<object>(); var bindRows = new List<object>(); var vdirRows = new List<object>();
            if (sites != null)
            {
                foreach (XElement s in sites.Elements("site"))
                {
                    string sn = A(s, "name", string.Empty); object st = null;
                    string sitePool = A(s.Element("applicationDefaults"), "applicationPool", defPool);
                    var apps = s.Elements("application").ToList();
                    XElement rootApp = apps.FirstOrDefault(a => A(a, "path", string.Empty) == "/");
                    XElement rootDir = rootApp != null ? rootApp.Elements("virtualDirectory").FirstOrDefault(d => A(d, "path", string.Empty) == "/") : null;
                    siteRows.Add(N.Node("Name", sn, "ID", A(s, "id", string.Empty), "State", siteStates != null && siteStates.TryGetValue(sn, out st) ? StateText(st) : "Unknown",
                        "PhysicalPath", A(rootDir, "physicalPath", string.Empty), "AppPool", A(rootApp, "applicationPool", sitePool)));
                    XElement binds = s.Element("bindings");
                    if (binds != null)
                    {
                        foreach (XElement b in binds.Elements("binding"))
                        {
                            string bi = A(b, "bindingInformation", string.Empty);
                            bindRows.Add(N.Node("Site", sn, "Protocol", A(b, "protocol", string.Empty), "BindingInformation", bi, "Hostname", BindingHost(bi), "SslFlags", A(b, "sslFlags", "0")));
                        }
                    }
                    foreach (XElement a in apps.Where(x => A(x, "path", string.Empty) != "/"))
                    {
                        XElement d = a.Elements("virtualDirectory").FirstOrDefault(x => A(x, "path", string.Empty) == "/");
                        vdirRows.Add(N.Node("Site", sn, "Path", A(a, "path", string.Empty), "PhysicalPath", A(d, "physicalPath", string.Empty), "AppPool", A(a, "applicationPool", sitePool)));
                    }
                }
            }
            r["Sites"] = siteRows; r["Bindings"] = bindRows; r["AppPools"] = poolRows; r["VirtualDirectories"] = vdirRows;
        }
    }
}
