using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ServerDocumentationTool;
using ServerDocumentationTool.Collect;
using ServerDocumentationTool.Common;
using ServerDocumentationTool.Report;

internal sealed class FakeReg : IRegistryReader
{
    public readonly Dictionary<string, Dictionary<string, object>> Keys = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
    public string Method { get { return "Fake"; } }
    public void Put(string path, params object[] kv)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        Keys[path] = d;
        // make parents exist
        string p = path;
        while (p.Contains("\\")) { p = p.Substring(0, p.LastIndexOf('\\')); if (!Keys.ContainsKey(p)) Keys[p] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }
    }
    public bool KeyExists(string path) { return Keys.ContainsKey(path); }
    public string[] SubKeys(string path)
    {
        string pre = path + "\\";
        return Keys.Keys.Where(k => k.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', pre.Length) < 0).Select(k => k.Substring(pre.Length)).ToArray();
    }
    public string[] ValueNames(string path) { Dictionary<string, object> d; return Keys.TryGetValue(path, out d) ? d.Keys.ToArray() : new string[0]; }
    public object Value(string path, string name) { Dictionary<string, object> d; object v; return Keys.TryGetValue(path, out d) && d.TryGetValue(name, out v) ? v : null; }
    public Dictionary<string, object> Values(string path) { Dictionary<string, object> d; return Keys.TryGetValue(path, out d) ? new Dictionary<string, object>(d, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }
}

internal static class UnitTests
{
    static int fails, passes;
    static void Eq(object expected, object actual, string what)
    {
        if (Equals(N.S(expected), N.S(actual))) passes++;
        else { fails++; Console.WriteLine("FAIL " + what + ": expected [" + N.S(expected) + "] got [" + N.S(actual) + "]"); }
    }
    static void True(bool b, string what) { Eq(true, b, what); }

    private static int Main(string[] args)
    {
        string dir = args[0];
        // readiness classification (DCOM error texts)
        Eq("Unreachable (no ping)", ServerCollector.Classify(false, "The RPC server is unavailable. (0x800706BA)"), "classify no ping");
        Eq("WMI (DCOM/RPC) blocked or not responding (firewall, service off or timeout)", ServerCollector.Classify(true, "The RPC server is unavailable. (0x800706BA)"), "classify rpc");
        Eq("Access denied (account is not a local administrator on the target)", ServerCollector.Classify(true, "Access is denied. (0x80070005)"), "classify denied");
        Eq("Access denied (account is not a local administrator on the target)", ServerCollector.Classify(false, "Access is denied. (0x80070005)"), "classify denied without ping (ICMP blocked)");
        Eq("Authentication failed (Kerberos/credentials) - run with alternate credentials for the target domain", ServerCollector.Classify(true, "The user name or password is incorrect. (0x8007052E)"), "classify bad password");
        Eq("WMI (DCOM/RPC) blocked or not responding (firewall, service off or timeout)", ServerCollector.Classify(true, "WMI query 'SELECT Caption FROM Win32_OperatingSystem' did not finish within 35 seconds."), "classify hard timeout");
        // group members (ADSI paths)
        Eq("Administrator", ServerCollector.MemberName("WinNT://CONTOSO/SRV01/Administrator", "SRV01"), "member local");
        Eq("CONTOSO\\Domain Admins", ServerCollector.MemberName("WinNT://CONTOSO/Domain Admins", "SRV01"), "member domain");
        Eq("NT AUTHORITY\\INTERACTIVE", ServerCollector.MemberName("WinNT://NT AUTHORITY/INTERACTIVE", "SRV01"), "member well-known");
        Eq("OTHER\\SRV02", ServerCollector.MemberName("WinNT://OTHER/SRV02", "SRV01"), "member computer account");
        // volumes and associations
        Eq("Mounted Volume|C:", string.Join("|", ServerCollector.VolumeClass("C:", "C:\\", "C:\\").Take(2)), "volume letter");
        Eq("Mount Point (folder)|D:\\Mounts\\Data\\", string.Join("|", ServerCollector.VolumeClass(null, "D:\\Mounts\\Data\\", null).Take(2)), "volume mount point");
        Eq("System/Recovery", ServerCollector.VolumeClass(null, "\\\\?\\Volume{abc}\\", null)[0], "volume recovery");
        Eq("\\\\.\\PHYSICALDRIVE0", ServerCollector.AssocDeviceId("\\\\SRV\\root\\cimv2:Win32_DiskDrive.DeviceID=\"\\\\\\\\.\\\\PHYSICALDRIVE0\""), "assoc disk");
        Eq("Disk #0, Partition #1", ServerCollector.AssocDeviceId("\\\\SRV\\root\\cimv2:Win32_DiskPartition.DeviceID=\"Disk #0, Partition #1\""), "assoc partition");
        // hotfix dates
        Eq("2023-04-16", ServerCollector.HotfixDate("4/16/2023").Value.ToString("yyyy-MM-dd"), "hotfix M/d/yyyy");
        Eq("2023-04-16", ServerCollector.HotfixDate("20230416").Value.ToString("yyyy-MM-dd"), "hotfix yyyyMMdd");
        True(ServerCollector.HotfixDate("01d9707ab1c2d000").HasValue, "hotfix FILETIME hex");
        True(!ServerCollector.HotfixDate("").HasValue, "hotfix empty");
        // LDAP filter escaping
        Eq("DC=contoso\\28eu\\29,DC=local", RoleCollectors.LdapEscape("DC=contoso(eu),DC=local"), "ldap escape");
        // DHCP address conversion
        Eq("192.0.2.0", RoleCollectors.Ip(0xC0000200), "dhcp ip");
        Eq("10.1.2.254", RoleCollectors.Ip(0x0A0102FE), "dhcp ip 2");

        // certificates from a registry blob
        string fn; bool key;
        byte[] der = Inventory.ParseBlob(File.ReadAllBytes(Path.Combine(dir, "cert-blob.bin")), out fn, out key);
        True(der != null, "blob has certificate");
        Eq("Web cert", fn, "blob friendly name"); True(key, "blob private key flag");
        var reg = new FakeReg();
        reg.Put(@"SOFTWARE\Microsoft\SystemCertificates\My\Certificates\9E65A33533C4F6F687757EA1E21E5D368330D50F", "Blob", File.ReadAllBytes(Path.Combine(dir, "cert-blob.bin")));
        reg.Put(@"SOFTWARE\Policies\Microsoft\SystemCertificates\My\Certificates\9E65A33533C4F6F687757EA1E21E5D368330D50F", "Blob", File.ReadAllBytes(Path.Combine(dir, "cert-blob.bin")));
        var remote = new Target { ComputerName = "SRV01", IsLocal = false };
        var certs = Inventory.Certificates(remote, reg);
        True(N.IsTrue(certs["Success"]), "certs success");
        Eq(1, N.Items(certs, "Certificates").Count, "certs deduplicated across physical stores");
        var c0 = N.Items(certs, "Certificates")[0];
        Eq("9E65A33533C4F6F687757EA1E21E5D368330D50F", N.Get(c0, "Thumbprint"), "cert thumbprint");
        Eq("CN=web01.contoso.local", N.Get(c0, "Subject"), "cert subject");
        var nb = new System.Security.Cryptography.X509Certificates.X509Certificate2(der).NotAfter;
        Eq(Math.Round((nb - DateTime.Now).TotalDays, 0), N.Get(c0, "DaysUntilExpiry"), "cert days until expiry");
        var rows = Rules.CertificateRows(certs);
        Eq("Web cert|My|OK", rows[0][0] + "|" + rows[0][1] + "|" + rows[0][3], "cert report row");

        // applications from the uninstall keys
        reg = new FakeReg();
        string u64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", u32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        reg.Put(u64 + "\\7-Zip", "DisplayName", "7-Zip 23.01 (x64)", "Publisher", "Igor Pavlov", "DisplayVersion", "23.01", "InstallDate", "20240105");
        reg.Put(u64 + "\\{KB1}", "DisplayName", "Security Update for Windows (KB123)", "Publisher", "Microsoft");
        reg.Put(u64 + "\\{SYS}", "DisplayName", "Microsoft Visual C++ Runtime", "SystemComponent", 1);
        reg.Put(u64 + "\\{CHILD}", "DisplayName", "Office Proofing", "ParentKeyName", "Office16");
        reg.Put(u64 + "\\NoName", "Publisher", "Nobody");
        reg.Put(u32 + "\\Notepad++", "DisplayName", "Notepad++ (32-bit x86)", "Publisher", "Notepad++ Team", "DisplayVersion", "8.6", "InstallDate", "2024-02-01");
        reg.Put(u32 + "\\7zipdup", "DisplayName", "7-Zip 23.01 (x64)", "Publisher", "Igor Pavlov", "DisplayVersion", "23.01");
        var apps = Inventory.Applications(remote, reg);
        Eq("7-Zip 23.01 (x64)|Notepad++ (32-bit x86)", string.Join("|", N.Items(apps, "Applications").Select(a => N.S(a, "Name"))), "apps listed, sorted, unique");
        Eq("2024-01-05", N.Get(N.Items(apps, "Applications")[0], "InstallDate"), "apps install date ISO");
        Eq("32-bit", N.Get(N.Items(apps, "Applications")[1], "Architecture"), "apps architecture");
        Eq(3, N.Items(apps, "HiddenEntries").Count, "apps hidden (system component, update, child)");
        Eq(2, N.Path(apps, "Summary", "Publishers"), "apps publishers");

        // SQL from the registry
        reg = new FakeReg();
        string sq = @"SOFTWARE\Microsoft\Microsoft SQL Server";
        reg.Put(sq + @"\Instance Names\SQL", "MSSQLSERVER", "MSSQL15.MSSQLSERVER", "SAGE", "MSSQL15.SAGE", "MICROSOFT##WID", "x");
        reg.Put(sq + @"\MSSQL15.MSSQLSERVER\Setup", "Version", "15.0.2000.5", "PatchLevel", "15.0.4355.3", "Edition", "Standard Edition", "SQLDataRoot", @"D:\SQL");
        reg.Put(sq + @"\MSSQL15.MSSQLSERVER\MSSQLServer\SuperSocketNetLib\Tcp\IPAll", "TcpPort", "1433", "TcpDynamicPorts", "");
        reg.Put(sq + @"\MSSQL15.SAGE\MSSQLServer\SuperSocketNetLib\Tcp\IPAll", "TcpPort", "", "TcpDynamicPorts", "0");
        var sql = Inventory.Sql(remote, reg);
        var insts = N.Items(sql, "Instances");
        Eq(2, insts.Count, "sql instances (WID skipped)");
        Eq("MSSQLSERVER|15.0.4355.3|1433|SQLSERVERAGENT", N.S(insts[0], "Instance") + "|" + N.S(insts[0], "PatchLevel") + "|" + N.S(insts[0], "TcpPort") + "|" + N.S(insts[0], "AgentServiceName"), "sql default instance");
        Eq("SAGE|Dynamic|MSSQL$SAGE|SQLAgent$SAGE", N.S(insts[1], "Instance") + "|" + N.S(insts[1], "TcpPort") + "|" + N.S(insts[1], "ServiceName") + "|" + N.S(insts[1], "AgentServiceName"), "sql named instance");
        Eq("SQL Server 2019", Rules.SqlProductName(Rules.SqlBuild(insts[0])), "sql product name");

        // IIS from applicationHost.config
        string cfg = File.ReadAllText(Path.Combine(dir, "applicationHost.config"));
        var iis = N.New();
        RoleCollectors.ParseIisConfig(cfg, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "Default Web Site", 1u } },
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "DefaultAppPool", 1u }, { "Classic .NET AppPool", 3u } }, iis);
        var site = N.Items(iis, "Sites")[0];
        Eq("Default Web Site|1|Started|%SystemDrive%\\inetpub\\wwwroot|DefaultAppPool", string.Join("|", new[] { "Name", "ID", "State", "PhysicalPath", "AppPool" }.Select(k => N.S(site, k))), "iis site");
        Eq("Intranet|2|Unknown|D:\\Web\\Intranet|IntranetPool", string.Join("|", new[] { "Name", "ID", "State", "PhysicalPath", "AppPool" }.Select(k => N.S(N.Items(iis, "Sites")[1], k))), "iis second site");
        Eq("http|*:80:|", string.Join("|", new[] { "Protocol", "BindingInformation", "Hostname" }.Select(k => N.S(N.Items(iis, "Bindings")[0], k))), "iis binding");
        Eq("https|*:443:intranet.contoso.local|intranet.contoso.local|1", string.Join("|", new[] { "Protocol", "BindingInformation", "Hostname", "SslFlags" }.Select(k => N.S(N.Items(iis, "Bindings")[2], k))), "iis https binding");
        Eq("DefaultAppPool|Started|v4.0|Integrated|ApplicationPoolIdentity", string.Join("|", new[] { "Name", "State", "ManagedRuntimeVersion", "ManagedPipelineMode", "Identity" }.Select(k => N.S(N.Items(iis, "AppPools")[0], k))), "iis default pool");
        Eq("Classic .NET AppPool|Stopped|v2.0|Classic|NetworkService", string.Join("|", new[] { "Name", "State", "ManagedRuntimeVersion", "ManagedPipelineMode", "Identity" }.Select(k => N.S(N.Items(iis, "AppPools")[1], k))), "iis classic pool");
        Eq("Default Web Site|/ArdentWebServices|C:\\Ardent\\WebSites\\ArdentWebServices|ArdentWebServices", string.Join("|", new[] { "Site", "Path", "PhysicalPath", "AppPool" }.Select(k => N.S(N.Items(iis, "VirtualDirectories")[0], k))), "iis application");
        Eq(1, N.Items(iis, "VirtualDirectories").Count, "iis applications (root apps excluded)");

        // server lists
        Eq("SRV01|SRV02|srv03.contoso.local", string.Join("|", RunFiles.ParseText("SRV01, SRV02;\r\nsrv03.contoso.local  SRV01")), "parse server text");
        File.WriteAllText(Path.Combine(dir, "list.csv"), "Name,Notes\r\nSRV10,\"web, front\"\r\n,empty\r\nSRV11,x\r\n");
        Eq("SRV10|SRV11", string.Join("|", RunFiles.ImportList(Path.Combine(dir, "list.csv"))), "import csv");
        Eq("Client_A_B", RunFiles.SafeFileName("Client A:B"), "safe file name");

        // JSON
        var j = N.Node("a", "line1\nline2 \"q\" \\ \u0001", "b", 1.5, "c", 42L, "d", true, "e", null, "f", new List<object> { 1L, "x" }, "g", new DateTime(2023, 4, 16, 12, 26, 22, DateTimeKind.Local));
        var back = (Dictionary<string, object>)Json.Parse(Json.Serialize(j, true));
        Eq(N.S(j, "a"), N.S(back, "a"), "json string escapes");
        Eq("1.5|42|True||x", N.S(back, "b") + "|" + N.S(back, "c") + "|" + N.S(back, "d") + "|" + N.S(back, "e") + "|" + N.S(N.Items(back, "f")[1]), "json values");
        Eq("04/16/2023 12:26:22", N.DateText(back["g"]), "json date round trip (local time)");
        Eq("04/16/2023 12:26:22", N.DateText(j["g"]), "date text from DateTime");

        // settings
        string sp = Path.Combine(dir, "settings", "appsettings.json");
        if (File.Exists(sp)) File.Delete(sp);
        var st = Settings.Load(sp);
        True(File.Exists(sp), "settings file created with defaults");
        Eq(4, st.Int("ParallelServers", 1, 16), "settings default");
        File.WriteAllText(sp, "{ \"ParallelServers\": 99, \"DefaultOutputRoot\": \"E:\\\\Docs\", \"CheckForUpdatesOnStart\": false }");
        st = Settings.Load(sp);
        Eq(16, st.Int("ParallelServers", 1, 16), "settings clamp");
        Eq("E:\\Docs", st.Str("DefaultOutputRoot"), "settings string");
        Eq(false, st.Bool("CheckForUpdatesOnStart"), "settings bool");
        Eq(300, st.Int("SectionTimeoutSec", 30, 3600), "settings missing key default");
        Eq("bobsdomain/ServerDocumentationTool", UpdateService.ResolveRepository("CPSDomain/ServerDocumentationTool"), "retired repository");

        // credentials
        var cr = Credential.Parse("CONTOSO\\admin", "x");
        Eq("CONTOSO|admin|CONTOSO\\admin", cr.Domain + "|" + cr.User + "|" + cr.FullUser, "credential domain\\user");
        cr = Credential.Parse("admin@contoso.local", "x");
        Eq("|admin@contoso.local|admin@contoso.local", cr.Domain + "|" + cr.User + "|" + cr.FullUser, "credential UPN");

        // failed result renders with the Failed banner
        var fr = ServerCollector.FailedResult("SRV99", "Time limit exceeded", "Time limit exceeded");
        var rs = new RunSettings { ClientName = "UT", RunRoot = Path.Combine(dir, "run"), RawDataPath = Path.Combine(dir, "run", "RawData") };
        Directory.CreateDirectory(rs.RawDataPath);
        var o = RunFiles.Complete(fr, rs, true);
        Eq("Failed|SRV99_AsBuilt.docx", o.Status + "|" + o.DocName, "failed server outcome");
        True(SelfTest.DocxText(Path.Combine(rs.RunRoot, o.DocName)).Contains("Collection Status: Failed - no inventory data was collected"), "failed DOCX banner");
        RunFiles.WriteSummary(new[] { o, new ServerOutcome { Name = "SRV98", Cancelled = true } }, Path.Combine(rs.RunRoot, "s.html"), "UT");
        True(File.ReadAllText(Path.Combine(rs.RunRoot, "s.html")).Contains("Cancelled by user"), "summary lists cancelled server");

        // hard time limit
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { Hard.Run(() => System.Threading.Thread.Sleep(10000), 1, "Sleep"); fails++; Console.WriteLine("FAIL hard limit did not trigger"); }
        catch (HardTimeoutException) { True(sw.Elapsed.TotalSeconds < 3, "hard limit returns on time"); }
        try { Hard.Run(() => { throw new InvalidOperationException("boom"); }, 5, "Throw"); }
        catch (InvalidOperationException ex) { Eq("boom", ex.Message, "hard limit passes the original exception"); }

        Console.WriteLine(passes + " passed, " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
