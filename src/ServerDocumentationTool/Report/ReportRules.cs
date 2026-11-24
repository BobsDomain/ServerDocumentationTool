using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security;
using System.Text.RegularExpressions;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool.Report
{
    /// <summary>Text escaping for DOCX (XML) and HTML. XML-illegal characters are removed first (v0.9.9h rule).</summary>
    internal static class Txt
    {
        private static readonly Regex Illegal = new Regex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\uFFFE\uFFFF]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]", RegexOptions.Compiled);

        internal static string Clean(object value)
        {
            if (value == null) return string.Empty;
            return Illegal.Replace(N.S(value), string.Empty);
        }

        internal static string Xml(object value)
        {
            if (value == null) return string.Empty;
            return SecurityElement.Escape(Clean(value)) ?? string.Empty;
        }

        internal static string Html(object value)
        {
            return WebUtility.HtmlEncode(Clean(value));
        }
    }

    /// <summary>
    /// The report rules of the PowerShell DocumentBuilder (v0.9.9-v1.1.0), ported one function at a time.
    /// Each method names the PowerShell function it replaces.
    /// </summary>
    internal static class Rules
    {
        internal static int GroupCap = 15;
        internal static int AppCap = 200;

        private static readonly StringComparer SortCmp = StringComparer.Create(CultureInfo.InvariantCulture, true);

        // ConvertTo-IABBReadableMode
        internal static string ReadableMode(object value)
        {
            string s = N.S(value).Trim();
            if (s.Length == 0) return "Not collected";
            var num = new Dictionary<string, string>
            {
                { "0", "Windows 2000" }, { "1", "Windows Server 2003 Interim" }, { "2", "Windows Server 2003" }, { "3", "Windows Server 2008" },
                { "4", "Windows Server 2008 R2" }, { "5", "Windows Server 2012" }, { "6", "Windows Server 2012 R2" }, { "7", "Windows Server 2016" },
                { "10", "Windows Server 2025" }
            };
            string v;
            if (num.TryGetValue(s, out v)) return v + " Functional Level";
            if (s == "WinThreshold") return "Windows Server 2016 Functional Level";
            if (s == "Windows2000MixedDomain") return "Windows 2000 Mixed Domain Functional Level";
            if (s == "Windows2000NativeDomain") return "Windows 2000 Native Domain Functional Level";
            if (s == "Windows2000Forest") return "Windows 2000 Forest Functional Level";
            Match m = Regex.Match(s, @"^Windows(\d{4})(R2)?(Interim)?(Forest|Domain)$");
            if (m.Success)
            {
                string t = "Windows Server " + m.Groups[1].Value;
                if (m.Groups[2].Success) t += " R2";
                if (m.Groups[3].Success) t += " Interim";
                return t + " " + m.Groups[4].Value + " Functional Level";
            }
            return s;
        }

        // Test-IABBWorkstationOS
        internal static bool IsWorkstationOS(object server)
        {
            return Regex.IsMatch(N.S(N.Path(server, "OperatingSystem", "Caption")), "Windows 10|Windows 11|Windows 7|Windows 8|Enterprise for Virtual Desktops", RegexOptions.IgnoreCase);
        }

        // Get-IABBDisplayRoles
        internal static List<string> DisplayRoles(object server)
        {
            return N.Items(server, "RolesDetected").Select(N.S).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        }

        // Convert-IABBShareType
        internal static string ShareType(object type)
        {
            long v;
            if (!long.TryParse(N.S(type), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) v = 0;
            switch (v)
            {
                case 0: return "Disk Drive";
                case 1: return "Printer Queue";
                case 2: return "Device";
                case 3: return "IPC";
                case 2147483648: return "Administrative Share";
                case 2147483651: return "Administrative IPC";
                default: return (v & 2147483648) != 0 ? "Administrative/Special" : N.S(type);
            }
        }

        // Format-IABBDNSForwarders
        internal static string DnsForwarders(object forwarders)
        {
            return string.Join(", ", N.Items(forwarders).Select(N.S).Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Select(ip => ip == "168.63.129.16" ? "Azure Recursive Resolver (168.63.129.16)" : ip));
        }

        // Get-IABBFSMOHolderSummary
        internal static string FsmoSummary(object fsmo)
        {
            var h = new List<string>();
            foreach (string n in new[] { "SchemaMaster", "DomainNamingMaster", "RIDMaster", "PDCEmulator", "InfrastructureMaster" })
            {
                string v = N.S(fsmo, n);
                if (!string.IsNullOrWhiteSpace(v)) h.Add(v);
            }
            var u = h.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, SortCmp).ToList();
            if (u.Count == 0) return "Not collected";
            return u.Count.ToString(CultureInfo.InvariantCulture) + " Unique FSMO Holder(s): " + string.Join(", ", u);
        }

        // Get-IABBGuestType
        internal static string GuestType(object server)
        {
            object virt = N.Get(server, "Virtualization");
            string pt = N.S(virt, "PlatformType");
            if (string.IsNullOrWhiteSpace(pt)) return "Unknown (not collected)";
            if (pt == "Virtual") return "Virtual Machine";
            if (N.Truthy(virt, "IsHyperVHost")) return "Physical Server (Hyper-V host)";
            if (IsWorkstationOS(server)) return "Physical Workstation";
            return "Physical Server";
        }

        // Get-IABBVirtualizationPlatform
        internal static string VirtualizationPlatform(object server)
        {
            string hp = N.S(N.Path(server, "Virtualization", "HostPlatform"));
            switch (hp)
            {
                case "Hyper-V": return "Microsoft Hyper-V";
                case "VMware": return "VMware vSphere/ESXi";
                case "Physical Server": return "Physical Hardware";
                case "Physical (Hyper-V host)": return "Physical Hardware (runs Hyper-V)";
                case "Azure": return "Microsoft Azure";
                default: return string.IsNullOrWhiteSpace(hp) ? "Unknown (not collected)" : hp;
            }
        }

        // Get-IABBIPv4String
        internal static string IPv4String(object server)
        {
            var ips = new List<string>();
            foreach (object n in N.Items(server, "NetworkAdapters"))
            {
                foreach (string ip in Regex.Split(N.S(n, "IPAddress"), @",\s*"))
                {
                    if (Regex.IsMatch(ip, @"^(\d{1,3}\.){3}\d{1,3}$")) ips.Add(ip);
                }
            }
            return string.Join(", ", ips.Distinct().OrderBy(x => x, SortCmp));
        }

        // Get-IABBDataVolumes
        internal static List<object> DataVolumes(object server)
        {
            return N.Items(server, "VolumeDetails").Where(v =>
            {
                string t = N.S(v, "VolumeType");
                return t == "Mounted Volume" || t == "Mount Point (folder)";
            }).ToList();
        }

        internal sealed class Storage
        {
            public double TotalGB, FreeGB, UsedGB;
            public object FreePct;
            public int VolumeCount, MountPointCount, LowVolumeCount;
        }

        // Get-IABBStorageSummary
        internal static Storage StorageSummary(object server)
        {
            var vols = DataVolumes(server);
            double t = 0, fr = 0; int low = 0;
            foreach (object v in vols)
            {
                double? c = N.Dbl(N.Get(v, "CapacityGB")), f = N.Dbl(N.Get(v, "FreeGB")), p = N.Dbl(N.Get(v, "FreePct"));
                if (c.HasValue) t += c.Value;
                if (f.HasValue) fr += f.Value;
                if (p.HasValue && p.Value < 15) low++;
            }
            return new Storage
            {
                TotalGB = Math.Round(t, 2),
                FreeGB = Math.Round(fr, 2),
                UsedGB = Math.Round(t - fr, 2),
                FreePct = t > 0 ? (object)Math.Round((fr / t) * 100, 2) : "N/A",
                VolumeCount = vols.Count,
                MountPointCount = vols.Count(v => N.S(v, "VolumeType") == "Mount Point (folder)"),
                LowVolumeCount = low
            };
        }

        // Get-IABBStorageTotalsRows
        internal static List<string[]> StorageTotalsRows(object server)
        {
            Storage x = StorageSummary(server);
            return new List<string[]>
            {
                new[] { "Total Storage GB", N.S(x.TotalGB) }, new[] { "Used Storage GB", N.S(x.UsedGB) }, new[] { "Free Storage GB", N.S(x.FreeGB) },
                new[] { "Free Percentage", N.S(x.FreePct) }, new[] { "Data Volumes", N.S(x.VolumeCount) }, new[] { "Folder Mount Points", N.S(x.MountPointCount) }
            };
        }

        // Get-IABBCollectionStatusRows
        internal static List<string[]> CollectionStatusRows(object server)
        {
            object st = N.Get(server, "CollectionStatus");
            if (st == null) return new List<string[]> { new[] { "Status", "Unknown" } };
            string err = N.Truthy(server, "Error") ? N.S(server, "Error") : "None";
            return new List<string[]>
            {
                new[] { "Status", N.S(st, "Status") }, new[] { "Transport", N.S(st, "Transport") },
                new[] { "Ping", N.Truthy(st, "PingOk") ? "OK" : "Failed" }, new[] { "Reachable", N.Truthy(st, "Reachable") ? "Yes" : "No" },
                new[] { "Authorized", N.Truthy(st, "Authorized") ? "Yes" : "No" }, new[] { "Classification", N.S(st, "Classification") },
                new[] { "Sections OK", N.S(st, "SectionsOk") }, new[] { "Sections Failed", N.S(st, "SectionsFailed") },
                new[] { "Steps Timed Out", N.S(st, "StepsTimedOut") }, new[] { "Error", err }
            };
        }

        // Get-IABBTimingRows (last column = cell fill)
        internal static List<string[]> TimingRows(object server)
        {
            var rows = new List<string[]>();
            foreach (object t in N.Items(server, "SectionTimings"))
            {
                string res = N.S(t, "Result");
                string fill = res == "Timed out" ? "FFF3CD" : (res == "Failed" || res == "No data") ? "F8D7DA" : string.Empty;
                rows.Add(new[] { N.S(t, "Section"), N.S(t, "Seconds"), res, fill });
            }
            return rows;
        }

        // Test-IABBCollected
        internal static bool Collected(object result)
        {
            return result != null && N.Truthy(result, "Success");
        }

        // Get-IABBNotCollectedText
        internal static string NotCollectedText(object result)
        {
            if (result == null) return "Not collected.";
            if (N.Truthy(result, "Error")) return "Not collected: " + N.S(result, "Error");
            return "Not collected.";
        }

        // Get-IABBAppCountText
        internal static string AppCountText(object server)
        {
            object apps = N.Get(server, "Applications");
            if (Collected(apps) && (N.Items(apps, "Applications").Count + N.Items(apps, "HiddenEntries").Count) > 0)
                return N.S(N.Path(apps, "Summary", "TotalApplications"));
            return "Not collected";
        }

        // Get-IABBCappedMembers
        internal static string CappedMembers(string members, int count)
        {
            int cap = GroupCap;
            var list = string.IsNullOrWhiteSpace(members) ? new List<string>() : Regex.Split(members, @",\s*").Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (count <= cap || list.Count <= cap) return string.Join(", ", list);
            return string.Join(", ", list.Take(cap)) + "  (+" + (count - cap) + " more of " + count + " total; full list in RawData JSON)";
        }

        // Get-IABBLocalGroupRows
        internal static List<string[]> LocalGroupRows(object server)
        {
            var rows = new List<string[]>();
            foreach (object g in N.Items(server, "LocalGroups"))
            {
                int mc;
                if (!N.TryInt(N.Get(g, "MemberCount"), out mc)) mc = 0;
                rows.Add(new[] { N.S(g, "Name"), N.S(g, "Description"), N.S(g, "MemberCount"), CappedMembers(N.S(g, "Members"), mc) });
            }
            return rows;
        }

        private static int CountOrZero(object v)
        {
            int i;
            return N.TryInt("0" + N.S(v), out i) ? i : 0;
        }

        // Get-IABBLargeGroups
        internal static List<object> LargeGroups(object server)
        {
            return N.Items(server, "LocalGroups").Where(g => CountOrZero(N.Get(g, "MemberCount")) > GroupCap)
                .OrderByDescending(g => CountOrZero(N.Get(g, "MemberCount"))).ToList();
        }

        // Get-IABBCertificateName
        internal static string CertificateName(object cert)
        {
            if (cert == null) return string.Empty;
            string fn = N.S(cert, "FriendlyName");
            if (!string.IsNullOrWhiteSpace(fn)) return fn;
            string subject = N.S(cert, "Subject");
            Match m = Regex.Match(subject, "CN=([^,]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string n = m.Groups[1].Value;
                if (Regex.IsMatch(n, "^[0-9a-fA-F-]{36}$")) return "Unnamed Application Certificate";
                return n;
            }
            if (!string.IsNullOrWhiteSpace(subject)) return subject;
            return N.S(cert, "Thumbprint");
        }

        internal sealed class UniqueCert
        {
            public string Name;
            public List<string> Stores = new List<string>();
            public object DaysUntilExpiry;
            public string Thumbprint;
            public int Days { get { return N.ToInt(DaysUntilExpiry); } }
        }

        // Get-IABBUniqueCertificates
        internal static List<UniqueCert> UniqueCertificates(object certResult)
        {
            var map = new Dictionary<string, UniqueCert>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (object c in N.Items(certResult, "Certificates"))
            {
                string key = N.S(c, "Thumbprint");
                if (string.IsNullOrWhiteSpace(key)) key = N.S(c, "Subject") + "|" + N.S(c, "NotAfter");
                UniqueCert u;
                if (!map.TryGetValue(key, out u))
                {
                    u = new UniqueCert { Name = CertificateName(c), DaysUntilExpiry = N.Get(c, "DaysUntilExpiry"), Thumbprint = N.S(c, "Thumbprint") };
                    map[key] = u;
                    order.Add(key);
                }
                string st = N.S(c, "Store");
                if (!string.IsNullOrWhiteSpace(st) && !u.Stores.Contains(st)) u.Stores.Add(st);
            }
            return order.Select(k => map[k]).ToList();
        }

        // Test-IABBActionableStore
        internal static bool ActionableStore(IEnumerable<string> stores)
        {
            return stores.Any(s => s == "My" || s == "WebHosting");
        }

        // Get-IABBCertStatus
        internal static string CertStatus(object days, IEnumerable<string> stores)
        {
            if (days == null || N.S(days) == string.Empty) return "Unknown";
            int d;
            if (!N.TryInt(days, out d)) return "Unknown";
            if (d < 0) return ActionableStore(stores) ? "EXPIRED" : "Expired (legacy root - informational)";
            if (d <= 30) return "Expiring <=30d";
            if (d <= 60) return "Expiring <=60d";
            return "OK";
        }

        // Get-IABBCertStatusFill
        internal static string CertStatusFill(string status)
        {
            switch (status)
            {
                case "EXPIRED": return "F8D7DA";
                case "Expiring <=30d": return "F8D7DA";
                case "Expiring <=60d": return "FFF3CD";
                case "OK": return "D4EDDA";
                case "Expired (legacy root - informational)": return "E7E6E6";
                default: return string.Empty;
            }
        }

        // Test-IABBListedCertificate (v0.9.9i)
        internal static bool ListedCertificate(UniqueCert c)
        {
            if (ActionableStore(c.Stores)) return true;
            int d;
            if (N.TryInt(c.DaysUntilExpiry, out d)) return d >= 0 && d <= 60;
            return false;
        }

        // Get-IABBCertificateRows (last column = fill)
        internal static List<string[]> CertificateRows(object certResult)
        {
            return UniqueCertificates(certResult).Where(ListedCertificate).OrderBy(c => c.Days).Select(c =>
            {
                string st = CertStatus(c.DaysUntilExpiry, c.Stores);
                return new[] { c.Name, string.Join(", ", c.Stores), N.S(c.DaysUntilExpiry), st, CertStatusFill(st) };
            }).ToList();
        }

        // Get-IABBCertificateSummaryRows
        internal static List<string[]> CertificateSummaryRows(object certResult)
        {
            var u = UniqueCertificates(certResult);
            int actExp = u.Count(c => c.Days < 0 && ActionableStore(c.Stores));
            int legExp = u.Count(c => c.Days < 0 && !ActionableStore(c.Stores));
            int soon = u.Count(c => c.Days >= 0 && c.Days <= 60);
            int own = u.Count(c => ActionableStore(c.Stores));
            int hidden = u.Count(c => !ListedCertificate(c));
            return new List<string[]>
            {
                new[] { "Server Certificates (Personal / Web Hosting)", N.S(own) }, new[] { "Expiring Within 60 Days (all stores)", N.S(soon) },
                new[] { "Expired (action required)", N.S(actExp) }, new[] { "Expired legacy roots (informational, not listed)", N.S(legExp) },
                new[] { "Built-in Root/CA certificates (not listed)", N.S(hidden) }, new[] { "Unique Certificates (all stores)", N.S(u.Count) }
            };
        }

        // Get-IABBPatchStatus
        internal static string PatchStatus(object days)
        {
            if (days == null || N.S(days) == string.Empty) return "Unknown";
            int d;
            if (!N.TryInt(days, out d)) return "Unknown";
            if (d <= 35) return "Current";
            if (d <= 90) return "Behind";
            return "Overdue";
        }

        // Get-IABBPatchStatusFill
        internal static string PatchStatusFill(string status)
        {
            switch (status)
            {
                case "Current": return "D4EDDA";
                case "Behind": return "FFF3CD";
                case "Overdue": return "F8D7DA";
                default: return string.Empty;
            }
        }

        // Get-IABBPatchSummaryRows (last column = fill)
        internal static List<string[]> PatchSummaryRows(object server)
        {
            object p = N.Get(server, "PatchSummary");
            if (!N.Truthy(p)) return new List<string[]> { new[] { "Patch data", "Not collected", string.Empty } };
            object days = N.Get(p, "DaysSinceLastPatch");
            string st = PatchStatus(days);
            return new List<string[]>
            {
                new[] { "Total Hotfixes", N.S(p, "HotfixCount"), string.Empty },
                new[] { "Latest Patch Date", N.Truthy(p, "LatestPatchDate") ? N.S(p, "LatestPatchDate") : "Unknown", string.Empty },
                new[] { "Latest KB(s)", N.Truthy(p, "LatestKB") ? N.S(p, "LatestKB") : "Unknown", string.Empty },
                new[] { "Days Since Last Patch", days != null ? N.S(days) : "Unknown", string.Empty },
                new[] { "Patch Status", st, PatchStatusFill(st) },
                new[] { "Hotfixes in Last 90 Days", N.S(p, "HotfixesLast90Days"), string.Empty },
                new[] { "Hotfixes Without Install Date", N.S(p, "UndatedCount"), string.Empty }
            };
        }

        // Get-IABBDaysSinceLastPatchText
        internal static string DaysSinceLastPatchText(object server)
        {
            object p = N.Get(server, "PatchSummary");
            object days = N.Get(p, "DaysSinceLastPatch");
            if (N.Truthy(p) && days != null) return N.S(days) + " (" + PatchStatus(days) + ")";
            return "Unknown";
        }

        // Get-IABBSqlProductName
        internal static string SqlProductName(string build)
        {
            if (string.IsNullOrWhiteSpace(build)) return "Unknown";
            string[] parts = build.Split('.');
            int maj, min = 0;
            int.TryParse(parts[0], out maj);
            if (parts.Length > 1) int.TryParse(parts[1], out min);
            switch (maj)
            {
                case 17: return "SQL Server 2025";
                case 16: return "SQL Server 2022";
                case 15: return "SQL Server 2019";
                case 14: return "SQL Server 2017";
                case 13: return "SQL Server 2016";
                case 12: return "SQL Server 2014";
                case 11: return "SQL Server 2012";
                case 10: return min >= 50 ? "SQL Server 2008 R2" : "SQL Server 2008";
                case 9: return "SQL Server 2005";
                default: return "SQL Server (" + build + ")";
            }
        }

        // Get-IABBSqlBuild
        internal static string SqlBuild(object inst)
        {
            string pl = N.S(inst, "PatchLevel");
            return !string.IsNullOrWhiteSpace(pl) ? pl : N.S(inst, "Version");
        }

        // Format-IABBSqlPort
        internal static string SqlPort(object port)
        {
            string p = N.S(port).Trim();
            return p == "Dynamic (0)" ? "Dynamic" : p;
        }

        // Get-IABBServiceByName
        internal static object ServiceByName(object server, string name)
        {
            return N.Items(server, "Services").FirstOrDefault(s => N.S(s, "Name") == name);
        }

        // Get-IABBServiceStateText
        internal static string ServiceStateText(object server, string name)
        {
            object svc = ServiceByName(server, name);
            if (svc == null) return "Not installed";
            string mode = N.S(svc, "StartMode");
            if (mode == "Disabled") return "Disabled";
            return N.S(svc, "State") + " / " + mode;
        }

        // Get-IABBServiceAccount
        internal static string ServiceAccount(object server, string name)
        {
            object svc = ServiceByName(server, name);
            return svc != null ? N.S(svc, "StartName") : string.Empty;
        }

        // Get-IABBRoleServiceMap
        internal static readonly Dictionary<string, string[]> RoleServiceMap = new Dictionary<string, string[]>
        {
            { "Domain Controller", new[] { "NTDS", "ADWS", "Netlogon", "Kdc", "IsmServ", "DFSR", "NtFrs", "Dfs", "W32Time", "Dnscache", "LanmanServer", "SamSs" } },
            { "DNS Server", new[] { "DNS" } },
            { "DHCP Server", new[] { "DHCPServer" } },
            { "AD CS Server", new[] { "CertSvc", "CertPropSvc" } },
            { "IIS Server", new[] { "W3SVC", "WAS", "IISADMIN", "AppHostSvc" } },
            { "SQL Server", new[] { "MSSQLSERVER", "MSSQL$*", "SQLSERVERAGENT", "SQLAgent$*", "SQLBrowser", "SQLWriter", "MSSQLFDLauncher*", "MsDtsServer*", "SSISTELEMETRY*", "SQLTELEMETRY*", "ReportServer*", "SQLServerReportingServices", "PowerBIReportServer", "MSSQLServerOLAPService", "MSOLAP$*" } },
            { "Hyper-V Host", new[] { "vmms", "vmcompute" } },
            { "Hyper-V Host (nested)", new[] { "vmms", "vmcompute" } },
            { "File Server", new[] { "LanmanServer" } },
            { "Print Server", new[] { "Spooler" } },
            { "DFS Namespace Server", new[] { "Dfs" } },
            { "DFS Replication Server", new[] { "DFSR" } },
            { "Remote Desktop Session Host", new[] { "TermService", "SessionEnv", "UmRdpService", "Tssdis", "TermServLicensing", "RDMS", "TScPubRPC" } }
        };

        // Get-IABBRoleServiceRows
        internal static List<string[]> RoleServiceRows(object server)
        {
            var rows = new List<string[]>();
            var seen = new HashSet<string>();
            var services = N.Items(server, "Services");
            foreach (string role in DisplayRoles(server))
            {
                string[] pats;
                if (!RoleServiceMap.TryGetValue(role, out pats)) continue;
                foreach (string pat in pats)
                {
                    var hits = services.Where(s => N.Like(N.S(s, "Name"), pat) && !N.Like(N.S(s, "Name"), "*MICROSOFT##*"))
                        .OrderBy(s => N.S(s, "Name"), SortCmp).ToList();
                    foreach (object svc in hits)
                    {
                        string dk = role + "|" + N.S(svc, "Name").ToLowerInvariant();
                        if (!seen.Add(dk)) continue;
                        string state = N.S(svc, "State"), startMode = N.S(svc, "StartMode");
                        string status = startMode == "Disabled" ? "Disabled" : state == "Running" ? "Running" : state == "Stopped" ? "Stopped" : state;
                        rows.Add(new[] { role, N.S(svc, "Name"), N.S(svc, "DisplayName"), status, startMode });
                    }
                }
            }
            return rows;
        }

        // Get-IABBExecutiveDashboardRows
        internal static List<string[]> ExecutiveDashboardRows(object server)
        {
            Storage st = StorageSummary(server);
            object cs = N.Get(server, "CollectionStatus");
            return new List<string[]>
            {
                new[] { "Server Name", N.S(server, "ComputerName") },
                new[] { "Collection Status", cs != null ? N.S(cs, "Status") : "Unknown" },
                new[] { "Server Type", GuestType(server) },
                new[] { "Operating System", N.S(N.Path(server, "OperatingSystem", "Caption")) },
                new[] { "Roles Count", N.S(DisplayRoles(server).Count) },
                new[] { "Memory GB", N.S(N.Path(server, "Hardware", "MemoryGB")) },
                new[] { "Total Storage GB", N.S(st.TotalGB) },
                new[] { "IPv4 Address", IPv4String(server) },
                new[] { "Applications Installed", AppCountText(server) },
                new[] { "Days Since Last Patch", DaysSinceLastPatchText(server) }
            };
        }
    }
}
