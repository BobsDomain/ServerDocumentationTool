using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool.Collect
{
    /// <summary>Port of Get-IABBPlatformInfo (physical vs virtual; a Hyper-V host is physical).</summary>
    internal static class Platform
    {
        internal static Dictionary<string, object> Info(string manufacturer, string model, bool hasVmms, string assetTag)
        {
            string mf = manufacturer ?? string.Empty, md = model ?? string.Empty;
            string platform = "Physical", hostp = "Physical Server";
            Func<string, string, bool> m = (v, p) => Regex.IsMatch(v, p, RegexOptions.IgnoreCase);
            if (m(mf, "VMware") || m(md, "VMware")) { platform = "Virtual"; hostp = "VMware"; }
            else if (m(mf, "Microsoft") && m(md, "Virtual Machine"))
            {
                platform = "Virtual"; hostp = "Hyper-V";
                if ((assetTag ?? string.Empty).Trim() == "7783-7084-3265-9085-8269-3286-77") hostp = "Azure";
            }
            else if (m(md, "VirtualBox") || m(mf, "innotek")) { platform = "Virtual"; hostp = "Oracle VirtualBox"; }
            else if (m(mf, "QEMU|Red Hat|Nutanix") || m(md, "KVM|QEMU|AHV")) { platform = "Virtual"; hostp = "KVM/QEMU"; }
            else if (m(mf, "^Xen") || m(md, "HVM domU")) { platform = "Virtual"; hostp = "Xen"; }
            else if (m(md, "^Virtual")) { platform = "Virtual"; hostp = "Unknown Hypervisor"; }
            bool isHvHost = platform == "Physical" && hasVmms;
            if (isHvHost) hostp = "Physical (Hyper-V host)";
            return N.Node("PlatformType", platform, "HostPlatform", hostp, "IsVirtual", platform == "Virtual", "IsHyperVHost", isHvHost);
        }
    }

    /// <summary>
    /// Installed roles and features from Win32_ServerFeature (WMI, works over DCOM; Get-WindowsFeature needed WinRM).
    /// The class returns display names and numeric IDs. The IDs used by role detection are mapped to their short names.
    /// </summary>
    internal static class Features
    {
        private static readonly Dictionary<long, string> ShortNames = new Dictionary<long, string>
        {
            { 101, "FS-DFS-Namespace" }, { 102, "FS-DFS-Replication" }, { 130, "RDS-RD-Server" }
        };

        internal static List<object> Collect(Wmi wmi)
        {
            var rows = wmi.Query("SELECT ID, ParentID, Name FROM Win32_ServerFeature");
            var byId = new Dictionary<long, Dictionary<string, object>>();
            foreach (var x in rows) { long id; if (long.TryParse(N.S(x, "ID"), out id)) byId[id] = x; }
            Func<Dictionary<string, object>, string> path = null;
            path = x =>
            {
                var parts = new List<string>(); var seen = new HashSet<long>(); var cur = x;
                while (cur != null)
                {
                    parts.Insert(0, N.S(cur, "Name"));
                    long pid; if (!long.TryParse(N.S(cur, "ParentID"), out pid) || pid == 0 || !seen.Add(pid)) break;
                    byId.TryGetValue(pid, out cur);
                }
                return string.Join(" > ", parts);
            };
            var cmp = StringComparer.Create(CultureInfo.InvariantCulture, true);
            return rows.OrderBy(path, cmp).Select(x =>
            {
                long id; long.TryParse(N.S(x, "ID"), out id);
                string sn; if (!ShortNames.TryGetValue(id, out sn)) sn = N.S(x, "Name");
                bool top = N.S(x, "ParentID") == "0" || N.S(x, "ParentID") == string.Empty;
                return (object)N.Node("Name", sn, "DisplayName", N.S(x, "Name"), "FeatureType", top ? "Role or Feature" : "Role Service", "ID", id, "ParentID", N.Get(x, "ParentID"));
            }).ToList();
        }
    }

    /// <summary>Port of Invoke-IABBRoleDetection (v0.9.9i rules: File/Print/DFS, nested Hyper-V, RDS, demoted DC note).</summary>
    internal static class RoleDetection
    {
        internal static Dictionary<string, object> Detect(object s)
        {
            var roles = new List<string>(); var notes = new List<object>();
            var svcs = N.Items(s, "Services").Select(x => N.S(x, "Name")).ToList();
            string os = N.S(N.Path(s, "OperatingSystem", "Caption"));
            var features = N.Items(s, "RolesFeatures").Select(x => N.S(x, "Name")).ToList();
            int domainRole = -1;
            object dr = N.Path(s, "Overview", "DomainRole");
            if (dr != null && N.S(dr) != string.Empty) { int v; if (N.TryInt(dr, out v)) domainRole = v; }
            bool isDC = domainRole >= 4;
            if (isDC) roles.Add("Domain Controller");
            else if (svcs.Contains("NTDS"))
            {
                object ntds = N.Items(s, "Services").FirstOrDefault(x => N.S(x, "Name") == "NTDS");
                notes.Add("AD DS role files are still installed (NTDS service " + N.S(ntds, "State") + " / " + N.S(ntds, "StartMode") + ") but this server is a member server, not a domain controller (DomainRole " + domainRole + "). It was probably a domain controller that has been demoted.");
            }
            if (svcs.Contains("DNS")) roles.Add("DNS Server");
            if (svcs.Contains("DHCPServer")) roles.Add("DHCP Server");
            if (svcs.Contains("CertSvc")) roles.Add("AD CS Server");
            if (svcs.Contains("W3SVC")) roles.Add("IIS Server");
            if (svcs.Any(n => (n == "MSSQLSERVER" || N.Like(n, "MSSQL$*")) && !N.Like(n, "*MICROSOFT##*"))) roles.Add("SQL Server");
            bool isVirtual = N.Truthy(N.Get(s, "Virtualization")) && N.Truthy(N.Path(s, "Virtualization", "IsVirtual"));
            if (svcs.Contains("vmms")) roles.Add(isVirtual ? "Hyper-V Host (nested)" : "Hyper-V Host");
            var runSvc = N.Items(s, "Services").Where(x => N.S(x, "State") == "Running").Select(x => N.S(x, "Name")).ToList();
            if (!isDC)
            {
                if (features.Contains("FS-DFS-Namespace") || (features.Count == 0 && runSvc.Contains("Dfs"))) roles.Add("DFS Namespace Server");
                if (features.Contains("FS-DFS-Replication") || (features.Count == 0 && runSvc.Contains("DFSR"))) roles.Add("DFS Replication Server");
            }
            bool isRdsh = features.Contains("RDS-RD-Server") || N.IsTrue(N.Get(s, "RdsSessionHost"));
            if (isRdsh) roles.Add("Remote Desktop Session Host");
            int workloadCount = roles.Count(x => !N.Like(x, "DFS *"));
            int userShares = N.Items(s, "Shares").Count(x => N.S(x, "Type") == "0" && !N.S(x, "Name").EndsWith("$") && N.S(x, "Name") != "SYSVOL" && N.S(x, "Name") != "NETLOGON");
            if (userShares > 0 && workloadCount == 0) roles.Add("File Server");
            if (N.Items(s, "Printers").Any(x => N.S(x, "Shared") == "True")) roles.Add("Print Server");
            if (roles.Count == 0) roles.Add(Regex.IsMatch(os, "Windows 10|Windows 11|Enterprise for Virtual Desktops") ? "Workstation" : "General Purpose Server");
            var cmp = StringComparer.Create(CultureInfo.InvariantCulture, true);
            var unique = roles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, cmp).Cast<object>().ToList();
            return N.Node("Roles", unique, "IsDomainController", isDC, "DomainRole", domainRole, "IsRdsSessionHost", isRdsh, "Notes", notes);
        }
    }
}
