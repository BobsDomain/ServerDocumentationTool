using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Globalization;
using System.Linq;

namespace ServerDocumentationTool.Collect
{
    internal sealed class DiscoveredServer
    {
        public string ServerName;
        public string Source;
        public bool Skipped;
        public string SkipReason;
        public string LastLogon;
        public string OperatingSystem;
    }

    /// <summary>
    /// Port of Get-IABBDomainServers (v1.1.0 skip rules): computer objects of the signed-in user's domain whose OS matches
    /// the filter. Disabled accounts, failover cluster name objects and accounts with no logon for more than StaleDays
    /// are returned as skipped, with the reason. Uses LDAP directly; the ActiveDirectory PowerShell module is not needed.
    /// </summary>
    internal static class Discovery
    {
        internal static List<DiscoveredServer> DomainServers(string osFilter, int staleDays)
        {
            var list = new List<DiscoveredServer>();
            string f = (osFilter ?? "Windows Server").Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29");
            DateTime cut = DateTime.Now.AddDays(-Math.Abs(staleDays));
            using (var searcher = new DirectorySearcher("(&(objectCategory=computer)(operatingSystem=*" + f + "*))",
                new[] { "name", "dNSHostName", "userAccountControl", "lastLogonTimestamp", "servicePrincipalName", "operatingSystem" }) { PageSize = 1000 })
            using (SearchResultCollection all = searcher.FindAll())
            {
                foreach (SearchResult sr in all)
                {
                    Func<string, object> one = n => sr.Properties[n].Count > 0 ? sr.Properties[n][0] : null;
                    string name = Convert.ToString(one("dNSHostName") ?? one("name"), CultureInfo.InvariantCulture) ?? string.Empty;
                    if (name.Length == 0) continue;
                    int uac = one("userAccountControl") == null ? 0 : Convert.ToInt32(one("userAccountControl"), CultureInfo.InvariantCulture);
                    DateTime? last = null;
                    object llt = one("lastLogonTimestamp");
                    if (llt != null) { try { long v = Convert.ToInt64(llt, CultureInfo.InvariantCulture); if (v > 0) last = DateTime.FromFileTime(v); } catch { } }
                    var spns = sr.Properties["servicePrincipalName"].Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? string.Empty);
                    string reason = null;
                    if ((uac & 2) == 2) reason = "Disabled computer account";
                    else if (spns.Any(x => x.StartsWith("MSClusterVirtualServer/", StringComparison.OrdinalIgnoreCase))) reason = "Failover cluster name object (not a server)";
                    else if (staleDays > 0 && (!last.HasValue || last.Value < cut))
                        reason = "No logon to the domain for more than " + staleDays + " days (last logon: " + (last.HasValue ? last.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "never") + ")";
                    list.Add(new DiscoveredServer
                    {
                        ServerName = name, Source = "Active Directory", Skipped = reason != null, SkipReason = reason,
                        LastLogon = last.HasValue ? last.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty,
                        OperatingSystem = Convert.ToString(one("operatingSystem"), CultureInfo.InvariantCulture) ?? string.Empty
                    });
                }
            }
            return list.OrderBy(x => x.ServerName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
