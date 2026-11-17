# Server Documentation Tool: Roadmap

Source: v0.9.9g code review (2026-11-16). Each version is built only after the owner approves its scope.

## Done
- **v0.9.9f:** stable baseline (patch summary, SQL incl. 32-bit, PS 4.0, time limits, ADSI groups, Hyper-V host physical, demoted-DC warning).
- **v0.9.9g:** readable functional levels, page file/environment "Not collected", SQL "Dynamic", RDS Session Host.
- **v0.9.9h:** fixes only. AD DS/DNS/DHCP from the target with credentials; per-server isolation of collection and DOCX; XML-illegal character stripping; "Not collected" in place of empty tables plus the zero-row rule; IIS multi-binding/hostname; run log; packaging and README. **Field-tested 2026-11-17 on 153 servers.**
- **v0.9.9i:** from the field test. Certificates table limited to server certificates and expiring Root/CA certificates; applications match Programs and Features; Print Spooler disabled shown as a note; demoted-DC moved to role notes; File/Print/DFS role detection; nested Hyper-V and Azure labels.

## Removed on purpose (owner decision 2026-11-16): do not restore
- **Scheduled Tasks** section and collector.
- **AD DS Domain Controllers / FSMO tables** (the AD DS section keeps its summary metrics: forest, domain, functional levels, DC/GC counts, FSMO holder summary).
- **DNS zone table** (the DNS section keeps its summary: zone counts, AD-integrated, reverse, forwarders).

Reason: they added a lot of length to every DC/DNS report without adding value to a per-server as-built document.

## Product goal
One concise, accurate as-built document per server: clear collection status, honest "Not collected" reporting, physical/virtual clarity, and no padding.

## Proposed: v0.9.9i, report content (needs owner decision on each item)
Candidates that are still open (data already collected unless noted):
- Memory Modules for physical servers (bank, size, speed, part number); Memory Allocation / "N/A" for VMs. *Data already collected.*
- System and Recovery volumes. *Data already collected.*
- Critical Services (WinRM, Winmgmt, EventLog, BFE, mpssvc, WinDefend, W32Time, CryptSvc, Schedule). *Data already collected.*
- Platform detection: AWS EC2, GCP (Azure done in v0.9.9i); virtual disks on any VM.
- Role detection: Failover Cluster, WSUS, NPS, AD FS, RD Gateway/Broker/Licensing, Exchange, WDS (File/Print/DFS done in v0.9.9i); "Unknown" when services were not collected.
- Report size: Installed Roles and Features (7% of an average report in the field test).
- ISO dates for OS install/last boot.

- **v0.9.9i:** locked as baseline 2026-11-17 after the field test on 153 servers.
- **v1.0.2:** fixes the app's engine start-up ('host' is null): the runspace pool now gets the app's own PowerShell host.
- **v1.0.1:** renamed to Server Documentation Tool (visible names; file names kept for update compatibility).
- **v1.0.0:** the Windows app (C# .NET Framework 4.8, built by GitHub Actions): Cancel, parallel collection, hard per-server time limit, update button, reusable remoting session per server, local server recognised by FQDN.

## Candidates after v1.0.0 (owner approval needed)
- Skip WinRM-only steps immediately on DCOM-only servers; reuse the probe's protocol (one connection attempt per server). (Reusable WinRM session done in v1.0.0.)
- Domain discovery: skip disabled, stale and cluster-name objects. (The field test had 14 unreachable servers; the owner is to confirm whether they are decommissioned.)
- Rebuild Reports from RawData (no re-collection).
- HTML fleet summary (OS, platform, roles, patch status, low space, expiring certificates per server).
- Config clean-up: wire up or remove the unused keys (DefaultParallelLimit, DefaultDocTitle, IncludeRawJson, IncludeHtmlSummary, IncludeDocx, IncludeApplications, IncludeCertificates, SkipEmptyReports, CertificateExpiringDays, DocumentTemplate, ReachabilityPingCount) and the template file.

## Later
- DOCX polish: cover page, table of contents, header/footer with page numbers, page breaks, landscape wide tables, document properties.
- IIS HTTPS binding -> certificate thumbprint and expiry.

## Pending tests
- Large-group cap against a real large group (e.g. 2,778 members).

## Excluded by the owner
- DNS/DHCP depth (per-zone record counts, reservations, scope utilisation %), workstation fixes, any cloud/M365/Azure/Intune collection, any Office/Word COM dependency.
