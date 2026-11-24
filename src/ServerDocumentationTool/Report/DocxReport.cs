using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool.Report
{
    /// <summary>
    /// Per-server DOCX (native OpenXML, no Word). Port of New-IABBOpenXmlDocx (PowerShell engine v1.1.0):
    /// same sections, same order, same texts, fixed column-grid tables, "Not collected" instead of empty tables,
    /// visible warning row when a row does not fit the grid.
    /// </summary>
    internal sealed class DocxReport
    {
        private const int UsableTwips = 9026;
        private readonly StringBuilder body = new StringBuilder();
        private object s; // current server (used by the step helpers, as in the PowerShell version)

        internal static void Write(IList<object> servers, string outputPath, string clientName, int groupCap, int appCap)
        {
            Rules.GroupCap = groupCap;
            Rules.AppCap = appCap > 0 ? appCap : 200;
            var r = new DocxReport();
            r.Build(servers, clientName);
            r.Save(outputPath);
        }

        // ------------------------------------------------------------------ OpenXML primitives

        private static string P(string text, string style)
        {
            string pPr = !string.IsNullOrEmpty(style)
                ? "<w:pPr><w:pStyle w:val=\"" + style + "\"/><w:keepNext/><w:keepLines/><w:spacing w:before=\"160\" w:after=\"100\"/></w:pPr>"
                : "<w:pPr><w:spacing w:after=\"80\"/></w:pPr>";
            return "<w:p>" + pPr + "<w:r><w:t xml:space=\"preserve\">" + Txt.Xml(text) + "</w:t></w:r></w:p>";
        }

        private static string Banner(string text, string fill)
        {
            return "<w:p><w:pPr><w:keepNext/><w:shd w:fill=\"" + fill + "\"/><w:spacing w:before=\"120\" w:after=\"120\"/></w:pPr><w:r><w:rPr><w:b/><w:color w:val=\"FFFFFF\"/><w:sz w:val=\"26\"/></w:rPr><w:t xml:space=\"preserve\">" + Txt.Xml(text) + "</w:t></w:r></w:p>";
        }

        private static string TC(string text, int width, bool header, string fill)
        {
            string sh = header ? "<w:shd w:fill=\"0F172A\"/>" : !string.IsNullOrEmpty(fill) ? "<w:shd w:fill=\"" + fill + "\"/>" : string.Empty;
            string rp = header ? "<w:rPr><w:b/><w:color w:val=\"FFFFFF\"/></w:rPr>" : string.Empty;
            return "<w:tc><w:tcPr><w:tcW w:w=\"" + width + "\" w:type=\"dxa\"/>" + sh + "</w:tcPr><w:p><w:pPr><w:spacing w:after=\"20\"/></w:pPr><w:r>" + rp + "<w:t xml:space=\"preserve\">" + Txt.Xml(text) + "</w:t></w:r></w:p></w:tc>";
        }

        private static int[] CW(int count, int[] weights)
        {
            var w = new int[count];
            if (weights != null && weights.Length == count)
            {
                int sum = weights.Sum(), acc = 0;
                for (int i = 0; i < count; i++)
                {
                    if (i == count - 1) w[i] = UsableTwips - acc;
                    else { w[i] = (int)Math.Floor(UsableTwips * ((double)weights[i] / sum)); acc += w[i]; }
                }
            }
            else
            {
                int b = UsableTwips / count;
                for (int i = 0; i < count; i++) w[i] = i == count - 1 ? UsableTwips - b * (count - 1) : b;
            }
            return w;
        }

        private static string TblHead(int[] wd, string[] hd)
        {
            var x = new StringBuilder("<w:tbl><w:tblPr><w:tblW w:w=\"" + UsableTwips + "\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/><w:right w:val=\"single\" w:sz=\"4\"/><w:insideH w:val=\"single\" w:sz=\"4\"/><w:insideV w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr><w:tblGrid>");
            foreach (int c in wd) x.Append("<w:gridCol w:w=\"" + c + "\"/>");
            x.Append("</w:tblGrid><w:tr><w:trPr><w:tblHeader/><w:cantSplit/></w:trPr>");
            for (int j = 0; j < hd.Length; j++) x.Append(TC(hd[j], wd[j], true, string.Empty));
            x.Append("</w:tr>");
            return x.ToString();
        }

        private static string MismatchRow(int[] wd, int cells, int cols)
        {
            string msg = "Table data could not be laid out (" + cells + " values for " + cols + " columns). The full data is in the RawData JSON for this server.";
            var y = new StringBuilder("<w:tr><w:trPr><w:cantSplit/></w:trPr>");
            for (int j = 0; j < wd.Length; j++) y.Append(TC(j == 0 ? msg : string.Empty, wd[j], false, "FFF3CD"));
            return y.Append("</w:tr>").ToString();
        }

        /// <summary>Table; each row must have exactly hd.Length cells, otherwise a warning row replaces the data.</summary>
        private static string Tbl(string[] hd, IList<string[]> rows, int[] weights)
        {
            int hc = hd.Length;
            if (hc == 0) return string.Empty;
            int[] wd = CW(hc, weights);
            var x = new StringBuilder(TblHead(wd, hd));
            int cells = rows.Sum(r => r.Length);
            if (rows.Count > 0 && rows.All(r => r.Length == hc))
            {
                foreach (string[] r in rows)
                {
                    x.Append("<w:tr><w:trPr><w:cantSplit/></w:trPr>");
                    for (int j = 0; j < hc; j++) x.Append(TC(r[j], wd[j], false, string.Empty));
                    x.Append("</w:tr>");
                }
            }
            else if (cells > 0) x.Append(MismatchRow(wd, cells, hc));
            return x.Append("</w:tbl>").ToString();
        }

        /// <summary>Table whose rows carry one extra last cell: the fill colour for the columns from shadeFrom on.</summary>
        private static string TblFill(string[] hd, IList<string[]> rows, int[] weights, int shadeFrom)
        {
            int hc = hd.Length, rc = hc + 1;
            int[] wd = CW(hc, weights);
            var x = new StringBuilder(TblHead(wd, hd));
            int cells = rows.Sum(r => r.Length);
            if (rows.Count > 0 && rows.All(r => r.Length == rc))
            {
                foreach (string[] r in rows)
                {
                    string fill = r[hc];
                    x.Append("<w:tr><w:trPr><w:cantSplit/></w:trPr>");
                    for (int j = 0; j < hc; j++) x.Append(TC(r[j], wd[j], false, j >= shadeFrom ? fill : string.Empty));
                    x.Append("</w:tr>");
                }
            }
            else if (cells > 0) x.Append(MismatchRow(wd, cells, rc));
            return x.Append("</w:tbl>").ToString();
        }

        private static string Gap() { return "<w:p><w:pPr><w:spacing w:after=\"160\"/></w:pPr></w:p>"; }

        private static string[] H(params string[] headers) { return headers; }
        private static int[] W(params int[] weights) { return weights; }
        private static List<string[]> Rows(params string[][] rows) { return rows.ToList(); }
        private static string[] R(params object[] cells) { return cells.Select(N.Cell).ToArray(); }

        // ------------------------------------------------------------------ step helpers (v0.9.9h)

        private string StepRes(string name)
        {
            object tm = N.Items(s, "SectionTimings").FirstOrDefault(t => N.S(t, "Section") == name);
            return tm != null ? N.S(tm, "Result") : null;
        }

        private string NotCol(string name)
        {
            string res = StepRes(name);
            if (!string.IsNullOrEmpty(res) && res != "OK") return "Not collected: the \"" + name + "\" collection step result was " + res + ". See Collection Warnings.";
            if (string.IsNullOrEmpty(res)) return "Not collected: the \"" + name + "\" collection step did not run. See Collection Warnings.";
            return null;
        }

        private string SecTbl(string step, string[] hd, IList<string[]> rows, int[] weights, string emptyText)
        {
            string nc = NotCol(step);
            if (rows.Count == 0) return P(nc ?? emptyText, null);
            string y = string.Empty;
            if (nc != null) y += P("Partial data: the \"" + step + "\" collection step result was " + StepRes(step) + ". See Collection Warnings.", null);
            return y + Tbl(hd, rows, weights);
        }

        private void Add(string xml) { body.Append(xml).Append('\n'); }

        // ------------------------------------------------------------------ document

        private void Build(IList<object> servers, string clientName)
        {
            Add(P("Server As-Built Documentation", "Title"));
            Add(P("Client: " + clientName, null));
            Add(P("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), null));
            Add(P("Generated By: " + AppInfo.ProductName + " " + AppInfo.VersionText, null));
            Add(P("Document Version: " + AppInfo.Version.ToString(3), null));
            foreach (object server in servers) BuildServer(server);
        }

        private void BuildServer(object server)
        {
            s = server;
            Add(P("Server Details - " + N.S(s, "ComputerName"), "Heading1"));
            object st = N.Get(s, "CollectionStatus");
            string statusText = st != null ? N.S(st, "Status") : "Unknown";
            string fill = statusText == "Complete" ? "137333" : statusText == "Partial" ? "B06000" : statusText == "Failed" ? "B00020" : "555555";
            if (statusText == "Failed") Add(Banner("Collection Status: Failed - no inventory data was collected. See details below.", fill));
            else if (statusText != "Complete") Add(Banner("Collection Status: " + statusText + " - some data could not be collected. See details below.", fill));
            else Add(Banner("Collection Status: Complete", fill));
            Add(P("Collection Status", "Heading2")); Add(Tbl(H("Item", "Value"), Rules.CollectionStatusRows(s), W(30, 70)));
            var warns = N.Items(s, "CollectionWarnings").Select(N.S).Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
            if (warns.Count > 0) { Add(P("Collection Warnings", "Heading2")); Add(Tbl(H("Warning"), warns.Select(w => new[] { w }).ToList(), W(100))); }
            var timing = Rules.TimingRows(s);
            if (timing.Count > 0) { Add(P("Collection Timings", "Heading2")); Add(TblFill(H("Step", "Seconds", "Result"), timing, W(60, 20, 20), 1)); }
            if (statusText == "Failed")
            {
                Add(P("No inventory data was collected for this server, so the inventory sections are omitted. Resolve the issue shown under Classification and Error, then run the assessment again.", null));
                if (N.Like(N.S(st, "Classification"), "Authentication failed*"))
                    Add(P("Tip: tick \"Use alternate credentials\" and supply an administrator account from the target server's domain (for example DOMAIN\\adminuser).", null));
                Add(Gap());
                return;
            }
            Add(Gap());

            object os = N.Get(s, "OperatingSystem"), hw = N.Get(s, "Hardware"), virt = N.Get(s, "Virtualization");
            Add(P("Executive Dashboard", "Heading2")); Add(Tbl(H("Metric", "Value"), Rules.ExecutiveDashboardRows(s), W(35, 65))); Add(Gap());
            Add(P("Executive Server Facts", "Heading2"));
            Add(Tbl(H("Item", "Value"), Rows(R("Operating System", N.Get(os, "Caption")), R("Memory GB", N.Get(hw, "MemoryGB")), R("CPU", N.Get(hw, "ProcessorModel")),
                R("Total Storage GB", Rules.StorageSummary(s).TotalGB), R("IPv4 Address", Rules.IPv4String(s)), R("Roles", string.Join(", ", Rules.DisplayRoles(s)))), W(35, 65)));
            Add(Gap());
            string hvText = N.Truthy(virt, "IsHyperVHost") ? "Yes" : N.Items(s, "RolesDetected").Select(N.S).Contains("Hyper-V Host (nested)") ? "Nested (virtual machine running Hyper-V)" : "No";
            Add(P("Platform Classification", "Heading2"));
            Add(Tbl(H("Item", "Value"), Rows(R("Platform Type", N.Get(virt, "PlatformType")), R("Host Platform", N.Get(virt, "HostPlatform")), R("Guest Type", Rules.GuestType(s)),
                R("Virtualization Platform", Rules.VirtualizationPlatform(s)), R("Hyper-V Host", hvText), R("Manufacturer", N.Get(virt, "Manufacturer")),
                R("Model", N.Get(virt, "Model")), R("Serial Number", N.Path(s, "Overview", "SerialNumber"))), W(35, 65)));
            Add(Gap());
            Add(P("Operating System", "Heading2"));
            if (os == null) Add(P(NotCol("Time zone/OS"), null));
            else Add(Tbl(H("Item", "Value"), Rows(R("OS", N.Get(os, "Caption")), R("Version", N.Get(os, "Version")), R("Build", N.Get(os, "BuildNumber")),
                R("Architecture", N.Get(os, "Architecture")), R("Install Date", N.DateText(N.Get(os, "InstallDate"))), R("Last Boot", N.DateText(N.Get(os, "LastBootTime"))),
                R("Uptime Days", N.Get(os, "UptimeDays")), R("Timezone", N.Get(os, "TimeZone"))), W(35, 65)));
            Add(Gap());

            Add(P("Processors", "Heading2"));
            Add(SecTbl("Processors/Memory", H("Socket", "Name", "Cores", "Logical CPUs", "Max MHz"),
                N.Items(s, "ProcessorDetails").Select(p => R(N.Get(p, "SocketDesignation"), N.Get(p, "Name"), N.Get(p, "Cores"), N.Get(p, "LogicalProcessors"), N.Get(p, "MaxClockSpeedMHz"))).ToList(),
                W(12, 52, 12, 12, 12), "No processor information returned."));
            Add(Gap());
            Add(P("Storage Summary", "Heading2"));
            var stRows = N.Items(s, "VolumeDetails").Count > 0 ? Rules.StorageTotalsRows(s) : new List<string[]>();
            Add(SecTbl("Disks/Volumes", H("Item", "Value"), stRows, W(40, 60), "No volumes returned."));
            Add(P("Physical Disks", "Heading2"));
            Add(SecTbl("Disks/Volumes", H("Index", "Model", "Disk Type", "Interface", "Media", "Size GB", "Partitions"),
                N.Items(s, "PhysicalDisks").Select(d => R(N.Get(d, "Index"), N.Get(d, "Model"), N.Get(d, "DiskClass"), N.Get(d, "InterfaceType"), N.Get(d, "MediaType"), N.Get(d, "SizeGB"), N.Get(d, "Partitions"))).ToList(),
                W(9, 25, 15, 12, 20, 10, 9), "No physical disks returned."));
            var diskMap = N.Items(s, "DiskMap");
            if (diskMap.Count > 0)
            {
                Add(P("Disk to Volume Mapping", "Heading2"));
                Add(Tbl(H("Physical Disk", "Partition", "Logical Disk"), diskMap.Select(m => R(N.Get(m, "PhysicalDisk"), N.Get(m, "Partition"), N.Get(m, "LogicalDisk"))).ToList(), W(40, 40, 20)));
                Add(Gap());
            }
            var parts = N.Items(s, "Partitions");
            if (parts.Count > 0)
            {
                Add(P("Partitions", "Heading2"));
                Add(Tbl(H("Disk", "Partition", "Type", "Size GB", "Bootable", "Boot Partition", "Primary", "Starting Offset"),
                    parts.Select(p => R(N.Get(p, "DiskIndex"), N.Get(p, "Name"), N.Get(p, "Type"), N.Get(p, "SizeGB"), N.Get(p, "Bootable"), N.Get(p, "BootPartition"), N.Get(p, "Primary"), N.Get(p, "StartingOffset"))).ToList(),
                    W(8, 20, 24, 12, 10, 10, 8, 8)));
                Add(Gap());
            }
            Add(P("Volumes", "Heading2"));
            Add(SecTbl("Disks/Volumes", H("Mount Path", "Label", "Type", "File System", "Capacity GB", "Free GB", "Free %", "Health"),
                Rules.DataVolumes(s).Select(v => R(N.Get(v, "MountPath"), N.Get(v, "Label"), N.Get(v, "VolumeType"), N.Get(v, "FileSystem"), N.Get(v, "CapacityGB"), N.Get(v, "FreeGB"), N.Get(v, "FreePct"), N.Get(v, "HealthFlag"))).ToList(),
                W(18, 16, 16, 12, 11, 10, 9, 8), "No data volumes returned."));
            Add(Gap());

            Add(P("Network Adapters", "Heading2"));
            Add(SecTbl("Network", H("Name", "Description", "Speed Mbps", "MAC", "IPv4 Address", "Gateway", "DNS Servers", "DHCP"),
                N.Items(s, "NetworkAdapters").Select(n => R(N.Get(n, "Name"), N.Get(n, "Description"), N.Get(n, "SpeedMbps"), N.Get(n, "MACAddress"), N.Get(n, "IPAddress"), N.Get(n, "Gateway"), N.Get(n, "DNSServers"), N.Get(n, "DHCPEnabled"))).ToList(),
                W(12, 22, 10, 15, 13, 10, 12, 6), "No IP-enabled network adapters returned."));
            Add(Gap());
            var nicDetail = N.Items(s, "NetworkAdapterDetail");
            if (nicDetail.Count > 0)
            {
                Add(P("Network Adapter Detail", "Heading2"));
                foreach (object n in nicDetail)
                {
                    Add(Tbl(H("Setting", "Value"), Rows(R("Adapter", N.Get(n, "Name")), R("Status", N.Get(n, "Status")), R("IPv4 Address", N.Get(n, "IPv4Address")),
                        R("Subnet Mask", N.Get(n, "SubnetMask")), R("Default Gateway", N.Get(n, "DefaultGateway")), R("DNS Servers", N.Get(n, "DNSServers")),
                        R("Connection-specific Suffix", N.Get(n, "ConnSpecificSuffix")), R("DNS Suffix Search Order", N.Get(n, "DNSDomainSuffixSearchOrder")),
                        R("DHCP Enabled", N.Get(n, "DHCPEnabled")), R("DHCP Server", N.Get(n, "DHCPServer")), R("DHCP Lease Obtained", N.Get(n, "DHCPLeaseObtained")),
                        R("DHCP Lease Expires", N.Get(n, "DHCPLeaseExpires")), R("WINS Servers", N.Get(n, "WINSServers")), R("NetBIOS over TCP/IP", N.Get(n, "NetBIOSSetting"))), W(35, 65)));
                    Add(Gap());
                }
            }
            var features = N.Items(s, "RolesFeatures");
            if (features.Count > 0)
            {
                Add(P("Installed Roles and Features", "Heading2"));
                Add(Tbl(H("Name", "Display Name", "Type"), features.Select(f => R(N.Get(f, "Name"), N.Get(f, "DisplayName"), N.Get(f, "FeatureType"))).ToList(), W(30, 50, 20)));
                Add(Gap());
            }
            Add(P("Role Services", "Heading2"));
            var roleSvc = Rules.RoleServiceRows(s);
            if (roleSvc.Count > 0) Add(Tbl(H("Role", "Service Name", "Display Name", "State", "Start Mode"), roleSvc, W(20, 18, 32, 15, 15)));
            else if (NotCol("Services/Hotfixes/Shares") != null) Add(P(NotCol("Services/Hotfixes/Shares"), null));
            else Add(P("No role-specific services detected for this server.", null));
            foreach (string rn in N.Items(s, "RoleNotes").Select(N.S).Where(x => !string.IsNullOrWhiteSpace(x))) Add(P("Note: " + rn, null));
            Add(Gap());

            BuildApplications();
            Add(Gap());
            Add(P("Patch Summary", "Heading2")); Add(TblFill(H("Item", "Value"), Rules.PatchSummaryRows(s), W(45, 55), 1));
            Add(P("Patch Status: Current = last hotfix installed within 35 days; Behind = 36-90 days; Overdue = more than 90 days. Based on Windows hotfix install dates (Win32_QuickFixEngineering); application updates such as SQL Server CUs are not included.", null));
            Add(P("Hotfixes", "Heading2"));
            Add(SecTbl("Services/Hotfixes/Shares", H("KB", "Description", "Installed On", "Installed By"),
                N.Items(s, "Hotfixes").Select(h => R(N.Get(h, "HotFixID"), N.Get(h, "Description"), N.Truthy(h, "InstalledOn") ? N.Get(h, "InstalledOn") : N.Get(h, "InstalledOnRaw"), N.Get(h, "InstalledBy"))).ToList(),
                W(16, 24, 22, 38), "No hotfixes recorded by Windows (Win32_QuickFixEngineering)."));
            Add(Gap());
            Add(P("Shares", "Heading2"));
            Add(SecTbl("Services/Hotfixes/Shares", H("Name", "Path", "Description", "Type"),
                N.Items(s, "Shares").Select(x => R(N.Get(x, "Name"), N.Get(x, "Path"), N.Get(x, "Description"), Rules.ShareType(N.Get(x, "Type")))).ToList(),
                W(16, 34, 28, 22), "No shares returned."));
            Add(Gap());

            string acctNote = N.S(s, "LocalAccountsNote");
            bool extrasFailed = N.Like(acctNote, "Not collected*");
            Add(P("Printers", "Heading2"));
            var printers = N.Items(s, "Printers");
            if (printers.Count > 0) Add(Tbl(H("Name", "Driver", "Port", "Shared", "Share Name", "Location", "Default"),
                printers.Select(p => R(N.Get(p, "Name"), N.Get(p, "DriverName"), N.Get(p, "PortName"), N.Get(p, "Shared"), N.Get(p, "ShareName"), N.Get(p, "Location"), N.Get(p, "Default"))).ToList(),
                W(22, 22, 14, 9, 12, 12, 9)));
            else if (extrasFailed) Add(P(acctNote, null));
            else if (!string.IsNullOrWhiteSpace(N.S(s, "PrintersNote"))) Add(P(N.S(s, "PrintersNote"), null));
            else Add(P("No printers installed.", null));
            Add(Gap()); Add(P("Local Users", "Heading2"));
            var users = N.Items(s, "LocalUsers");
            if (users.Count > 0) Add(Tbl(H("Name", "Full Name", "Disabled", "Locked", "Password Expires", "Password Required", "SID"),
                users.Select(u => R(N.Get(u, "Name"), N.Get(u, "FullName"), N.Get(u, "Disabled"), N.Get(u, "Lockout"), N.Get(u, "PasswordExpires"), N.Get(u, "PasswordRequired"), N.Get(u, "SID"))).ToList(),
                W(14, 16, 9, 9, 13, 13, 26)));
            else if (!string.IsNullOrWhiteSpace(acctNote)) Add(P(acctNote, null));
            else Add(P("No local user accounts collected.", null));
            Add(Gap());
            var large = Rules.LargeGroups(s);
            if (large.Count > 0)
            {
                Add(P("Large Groups (membership exceeds display cap)", "Heading2"));
                Add(Tbl(H("Group", "Member Count"), large.Select(g => R(N.Get(g, "Name"), N.Get(g, "MemberCount"))).ToList(), W(70, 30)));
                Add(Gap());
            }
            string groupHeading = N.Truthy(N.Get(s, "RoleDetection")) && N.Truthy(N.Get(s, "RoleDetection"), "IsDomainController") ? "Built-in Groups (Domain Controller)" : "Local Groups";
            Add(P(groupHeading, "Heading2"));
            if (N.Items(s, "LocalGroups").Count > 0) Add(Tbl(H("Group", "Description", "Members", "Members List (capped)"), Rules.LocalGroupRows(s), W(20, 30, 10, 40)));
            else if (extrasFailed) Add(P(acctNote, null));
            else Add(P("No local groups collected.", null));
            Add(Gap()); Add(P("Page File", "Heading2"));
            var pf = N.Items(s, "PageFiles");
            if (pf.Count > 0) Add(Tbl(H("Name", "Allocated MB", "Current Usage MB", "Peak Usage MB"),
                pf.Select(p => R(N.Get(p, "Name"), N.Get(p, "AllocatedBaseSizeMB"), N.Get(p, "CurrentUsageMB"), N.Get(p, "PeakUsageMB"))).ToList(), W(40, 20, 20, 20)));
            else if (extrasFailed) Add(P(acctNote, null));
            else Add(P("No page file information collected.", null));
            Add(Gap()); Add(P("Environment Variables (System)", "Heading2"));
            var env = N.Items(s, "EnvironmentVariables");
            if (env.Count > 0) Add(Tbl(H("Name", "Value"), env.Select(e => R(N.Get(e, "Name"), N.Get(e, "Value"))).ToList(), W(24, 76)));
            else if (extrasFailed) Add(P(acctNote, null));
            else Add(P("No system environment variables collected.", null));

            Add(Gap()); Add(P("Certificate Summary", "Heading2"));
            object certs = N.Get(s, "Certificates");
            if (Rules.Collected(certs) && N.Items(certs, "Certificates").Count > 0)
            {
                Add(Tbl(H("Item", "Count"), Rules.CertificateSummaryRows(certs), W(60, 40)));
                Add(P("Certificates", "Heading2"));
                var certRows = Rules.CertificateRows(certs);
                if (certRows.Count > 0) Add(TblFill(H("Certificate Name", "Store(s)", "Days Until Expiry", "Status"), certRows, W(44, 16, 14, 26), 2));
                else Add(P("No certificates in the Personal or Web Hosting stores, and no Root/CA certificates expiring within 60 days.", null));
                Add(P("Listed: certificates in the server's Personal (My) and Web Hosting stores, plus Root/CA certificates expiring within 60 days. Other built-in Root/CA certificates are counted in the summary and kept in the RawData JSON. Red EXPIRED applies to Personal/Web Hosting certificates that require action.", null));
            }
            else if (Rules.Collected(certs)) Add(P("Not collected: the certificate query returned no data. See Collection Warnings.", null));
            else Add(P(Rules.NotCollectedText(certs), null));
            Add(Gap());

            BuildRoleSections();
        }

        private void BuildApplications()
        {
            Add(P("Application Summary", "Heading2"));
            object a = N.Get(s, "Applications");
            var apps = N.Items(a, "Applications");
            var hidden = N.Items(a, "HiddenEntries");
            if (Rules.Collected(a) && apps.Count > 0)
            {
                int cap = Rules.AppCap;
                var shown = apps.Take(cap).ToList();
                string shownText = apps.Count > cap ? cap + " of " + apps.Count : "All (" + apps.Count + ")";
                object summary = N.Get(a, "Summary");
                object hiddenCount = N.Get(summary, "HiddenEntries");
                Add(Tbl(H("Item", "Count"), Rows(R("Total Applications (Programs and Features)", N.Get(summary, "TotalApplications")), R("Unique Publishers", N.Get(summary, "Publishers")),
                    R("Shown in This Report", shownText), R("System Components / Updates (not listed)", hiddenCount ?? 0)), W(50, 50)));
                Add(P("Installed Applications", "Heading2"));
                Add(Tbl(H("Name", "Publisher", "Version", "Install Date", "Architecture"),
                    shown.Select(x => R(N.Get(x, "Name"), N.Get(x, "Publisher"), N.Get(x, "Version"), N.Get(x, "InstallDate"), N.Get(x, "Architecture"))).ToList(), W(40, 24, 14, 12, 10)));
                if (apps.Count > cap) Add(P("Showing the first " + cap + " of " + apps.Count + " applications (alphabetical). The full list is in the RawData JSON for this server.", null));
            }
            else if (Rules.Collected(a) && hidden.Count > 0) Add(P("No applications are listed in Programs and Features. " + hidden.Count + " system components / updates are not listed; see the RawData JSON.", null));
            else if (Rules.Collected(a)) Add(P("Not collected: the applications query returned no data. See Collection Warnings.", null));
            else Add(P(Rules.NotCollectedText(a), null));
        }

        private void BuildRoleSections()
        {
            object roleData = N.Get(s, "RoleData");
            object adds = N.Get(roleData, "ADDS");
            if (adds != null)
            {
                Add(P("Active Directory Domain Services", "Heading1"));
                if (Rules.Collected(adds))
                {
                    var dcs = N.Items(adds, "DomainControllers");
                    Add(Tbl(H("Metric", "Value"), Rows(R("Forest Name", N.Path(adds, "Forest", "Name")), R("Domain Name", N.Path(adds, "Domain", "DNSRoot")),
                        R("Forest Functional Level", Rules.ReadableMode(N.Path(adds, "Forest", "ForestMode"))), R("Domain Functional Level", Rules.ReadableMode(N.Path(adds, "Domain", "DomainMode"))),
                        R("Domain Controllers", dcs.Count), R("Global Catalogs", dcs.Count(d => N.IsTrue(N.Get(d, "IsGlobalCatalog")))),
                        R("FSMO Role Holders", Rules.FsmoSummary(N.Get(adds, "FSMORoles"))), R("Collected Via", N.Truthy(adds, "Method") ? N.S(adds, "Method") : "Not recorded")), W(35, 65)));
                }
                else Add(P(Rules.NotCollectedText(adds), null));
                Add(Gap());
            }
            object dns = N.Get(roleData, "DNS");
            if (dns != null)
            {
                Add(P("DNS Infrastructure", "Heading1"));
                if (Rules.Collected(dns))
                {
                    var zones = N.L(N.Get(dns, "Zones"));
                    Add(Tbl(H("DNS Item", "Value"), Rows(R("Total Zones", zones.Count), R("Reverse Lookup Zones", zones.Count(z => N.IsTrue(N.Get(z, "IsReverseLookupZone")))),
                        R("AD-Integrated Zones", zones.Count(z => N.IsTrue(N.Get(z, "IsDsIntegrated")))), R("DNS Forwarders", Rules.DnsForwarders(N.Get(dns, "Forwarders"))),
                        R("Collected Via", N.Truthy(dns, "Method") ? N.S(dns, "Method") : "Not recorded")), W(35, 65)));
                }
                else Add(P(Rules.NotCollectedText(dns), null));
                Add(Gap());
            }
            object dhcp = N.Get(roleData, "DHCP");
            if (dhcp != null)
            {
                Add(P("DHCP Services", "Heading1"));
                if (Rules.Collected(dhcp))
                {
                    if (N.Truthy(dhcp, "Method")) Add(P("Collected via: " + N.S(dhcp, "Method"), null));
                    var scopes = N.Items(dhcp, "Scopes");
                    if (scopes.Count > 0) Add(Tbl(H("Scope ID", "Name", "Start Range", "End Range", "State"),
                        scopes.Select(x => R(N.Get(x, "ScopeId"), N.Get(x, "Name"), N.Get(x, "StartRange"), N.Get(x, "EndRange"), N.Get(x, "State"))).ToList(), W(18, 28, 18, 18, 18)));
                    else Add(P("No IPv4 DHCP scopes are configured on this server.", null));
                }
                else Add(P(Rules.NotCollectedText(dhcp), null));
                Add(Gap());
            }
            object iis = N.Get(roleData, "IIS");
            if (iis != null)
            {
                Add(P("Internet Information Services (IIS)", "Heading1"));
                if (Rules.Collected(iis))
                {
                    var sites = N.Items(iis, "Sites"); var pools = N.Items(iis, "AppPools"); var binds = N.Items(iis, "Bindings"); var vdirs = N.Items(iis, "VirtualDirectories");
                    Add(Tbl(H("Item", "Value"), Rows(R("IIS Version", N.Get(iis, "Version")), R("Sites", sites.Count), R("Application Pools", pools.Count), R("Bindings", binds.Count)), W(35, 65)));
                    if (sites.Count > 0) { Add(P("IIS Sites", "Heading2")); Add(Tbl(H("Name", "ID", "State", "Physical Path", "App Pool"), sites.Select(x => R(N.Get(x, "Name"), N.Get(x, "ID"), N.Get(x, "State"), N.Get(x, "PhysicalPath"), N.Get(x, "AppPool"))).ToList(), W(20, 8, 12, 40, 20))); }
                    if (binds.Count > 0) { Add(P("IIS Bindings", "Heading2")); Add(Tbl(H("Site", "Protocol", "Binding Information", "Hostname", "SSL Flags"), binds.Select(x => R(N.Get(x, "Site"), N.Get(x, "Protocol"), N.Get(x, "BindingInformation"), N.Get(x, "Hostname"), N.Get(x, "SslFlags"))).ToList(), W(22, 12, 34, 22, 10))); }
                    if (pools.Count > 0) { Add(P("IIS Application Pools", "Heading2")); Add(Tbl(H("Name", "State", "Runtime", "Pipeline", "Identity"), pools.Select(x => R(N.Get(x, "Name"), N.Get(x, "State"), N.Get(x, "ManagedRuntimeVersion"), N.Get(x, "ManagedPipelineMode"), N.Get(x, "Identity"))).ToList(), W(30, 12, 16, 16, 26))); }
                    if (vdirs.Count > 0) { Add(P("IIS Applications / Virtual Directories", "Heading2")); Add(Tbl(H("Site", "Path", "Physical Path", "App Pool"), vdirs.Select(x => R(N.Get(x, "Site"), N.Get(x, "Path"), N.Get(x, "PhysicalPath"), N.Get(x, "AppPool"))).ToList(), W(20, 20, 40, 20))); }
                }
                else Add(P(Rules.NotCollectedText(iis), null));
                Add(Gap());
            }
            object sql = N.Get(roleData, "SQL");
            if (sql != null)
            {
                var insts = N.Items(sql, "Instances");
                Add(P("SQL Server", "Heading1"));
                if (insts.Count > 0)
                {
                    Add(P("SQL Server Instances", "Heading2"));
                    Add(Tbl(H("Instance", "Product", "Build", "Edition", "Architecture", "TCP Port"), insts.Select(i =>
                    {
                        string b = Rules.SqlBuild(i);
                        return R(N.Get(i, "Instance"), !string.IsNullOrEmpty(b) ? Rules.SqlProductName(b) : "Unknown", b, N.Get(i, "Edition"), N.Get(i, "Architecture"), Rules.SqlPort(N.Get(i, "TcpPort")));
                    }).ToList(), W(16, 16, 14, 28, 12, 14)));
                    Add(P("SQL Server Instance Services", "Heading2"));
                    Add(Tbl(H("Instance", "Data Root", "Engine Service", "Agent Service", "Engine Account"), insts.Select(i => R(N.Get(i, "Instance"), N.Get(i, "DataRoot"),
                        Rules.ServiceStateText(s, N.S(i, "ServiceName")), Rules.ServiceStateText(s, N.S(i, "AgentServiceName")), Rules.ServiceAccount(s, N.S(i, "ServiceName")))).ToList(), W(14, 30, 18, 18, 20)));
                }
                else Add(P(Rules.NotCollectedText(sql), null));
                Add(Gap());
            }
        }

        // ------------------------------------------------------------------ package

        private void Save(string outputPath)
        {
            string documentXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr></w:body></w:document>";
            const string stylesXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:rPr><w:rFonts w:ascii=\"Segoe UI\" w:hAnsi=\"Segoe UI\"/><w:sz w:val=\"19\"/></w:rPr></w:style><w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:pPr><w:keepNext/><w:keepLines/></w:pPr><w:rPr><w:b/><w:sz w:val=\"40\"/><w:color w:val=\"0F172A\"/></w:rPr></w:style><w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:pPr><w:keepNext/><w:keepLines/></w:pPr><w:rPr><w:b/><w:sz w:val=\"32\"/><w:color w:val=\"1E3A8A\"/></w:rPr></w:style><w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:pPr><w:keepNext/><w:keepLines/></w:pPr><w:rPr><w:b/><w:sz w:val=\"26\"/><w:color w:val=\"334155\"/></w:rPr></w:style></w:styles>";
            const string ct = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>";
            const string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
            const string docRels = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
            if (File.Exists(outputPath)) File.Delete(outputPath);
            using (var fs = new FileStream(outputPath, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddEntry(zip, "[Content_Types].xml", ct);
                AddEntry(zip, "_rels/.rels", rels);
                AddEntry(zip, "word/document.xml", documentXml);
                AddEntry(zip, "word/styles.xml", stylesXml);
                AddEntry(zip, "word/_rels/document.xml.rels", docRels);
            }
        }

        private static void AddEntry(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(content);
        }
    }

    /// <summary>HTML run summary. Port of New-IABBHtmlSummary.</summary>
    internal static class HtmlSummary
    {
        internal static void Write(IList<object> servers, string outputPath, string clientName)
        {
            var rows = new StringBuilder();
            foreach (object s in servers)
            {
                object st = N.Get(s, "CollectionStatus");
                string status = st != null ? N.S(st, "Status") : "Unknown";
                string color = status == "Complete" ? "#137333" : status == "Partial" ? "#b06000" : status == "Failed" ? "#b00020" : "#555";
                string issues = string.Join("; ", N.Items(s, "SectionTimings").Where(t => N.S(t, "Result") != "OK").Select(t => N.S(t, "Section") + ": " + N.S(t, "Result")));
                string err = N.Truthy(s, "Error") ? N.S(s, "Error") : issues;
                if (N.Truthy(s, "ReportError")) err = string.Join("; ", new[] { err, "DOCX generation failed: " + N.S(s, "ReportError") }.Where(x => !string.IsNullOrEmpty(x)));
                rows.Append("<tr><td>" + Txt.Html(N.S(s, "ComputerName")) + "</td><td style=\"color:" + color + ";font-weight:bold\">" + Txt.Html(status) + "</td><td>" + Txt.Html(N.S(st, "Transport")) + "</td><td>" + Txt.Html(N.S(st, "Classification")) + "</td><td>" + Txt.Html(err) + "</td></tr>");
            }
            string html = "<html><head><meta charset=\"utf-8\"><style>body{font-family:Segoe UI}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:6px}</style></head><body><h1>Server As-Built Documentation</h1><div>Client: " + Txt.Html(clientName) + "<br>Generated By: " + AppInfo.ProductName + " " + AppInfo.VersionText + "</div><h2>Run Summary</h2><table><tr><th>Server</th><th>Status</th><th>Transport</th><th>Classification</th><th>Error / Issues</th></tr>" + rows + "</table></body></html>";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
            File.WriteAllText(outputPath, html, new UTF8Encoding(false));
        }
    }
}
