# Server Documentation Tool: Architecture (v2.0.0)

## Overview
One native exe, `ServerDocumentationTool.exe` (C# 7.3, .NET Framework 4.8, WinForms). It replaces the v1.x design (C# app hosting the PowerShell engine, plus an updater and an Engine folder). The collection steps, field names, warnings, status rules and the report are ported one to one from the PowerShell engine v1.1.0, which is kept under `legacy/powershell-engine-v1.1.0` for reference.

| Folder | Content |
|---|---|
| `Common/` | `N` (the data model helpers: dictionary nodes with the same keys as the RawData JSON, PowerShell-style string and truth rules) and `Json` (reader/writer; dates are stored as `/Date(ms)/` like Windows PowerShell) |
| `Collect/` | `ServerCollector` (steps and status), `Wmi` (DCOM), `RegistryAccess`, `NetworkAccess`, `Inventory` (applications, certificates, SQL), `RoleCollectors` (AD DS, DNS, DHCP, IIS), `RoleDetection` / `Platform` / `Features`, `Discovery`, `Target` / `Hard` (time limits) |
| `Report/` | `DocxReport` (per-server DOCX, port of New-IABBOpenXmlDocx), `HtmlSummary` (port of New-IABBHtmlSummary), `Rules` (every report helper of the DocumentBuilder module) |
| `App/` | `MainForm`, `Runner` (parallel servers, hard per-server limit, Cancel), `RunFiles` (run folder, server lists, JSON + DOCX per server, HTML summary), `Settings`, `UpdateService`, `SelfTest`, `Program` (`--selftest`, `--render`) |

## How a server is reached
| Data | Method | Credentials |
|---|---|---|
| Hardware, OS, disks, network, services, hotfixes, shares, printers, page file, environment, local users/groups, roles and features (`Win32_ServerFeature`), RDS mode | WMI `root\cimv2` over DCOM, `PacketPrivacy`, `Impersonate` | `ConnectionOptions` user/password |
| Local group members | ADSI `WinNT://server/group,group` | `DirectoryEntry` user/password |
| Applications, SQL instances, IIS version, certificates (remote) | Remote Registry service; fallback WMI `StdRegProv` (64-bit view) over DCOM | network logon (`LogonUser` NEW_CREDENTIALS) / WMI credentials |
| Certificates (local) | `X509Store` LocalMachine My, Root, CA, WebHosting | signed-in user |
| Certificates (remote) | registry `Blob` values of SystemCertificates, Policies and EnterpriseCertificates (plus AuthRoot for Root); property 0x20 = certificate, 11 = friendly name, 2 = private key | as registry |
| IIS sites, bindings, pools, applications | `\\server\ADMIN$\System32\inetsrv\config\applicationHost.config` (local file for the local server); state from WMI `root\WebAdministration` when installed | network logon |
| AD DS | LDAP to the target DC: RootDSE (functional levels), fSMORoleOwner of the five role objects, crossRef (NetBIOS), DCs from the domain, sites and GCs from the configuration partition | `DirectoryEntry` user/password |
| DNS | WMI `root\MicrosoftDNS` (`MicrosoftDNS_Zone`, `MicrosoftDNS_Server`), hard 120 s limit | WMI credentials |
| DHCP | DHCP Server API (`dhcpsapi.dll`: DhcpEnumSubnets, DhcpGetSubnetInfo, DhcpEnumSubnetElements) | network logon |
| Discover Domain | LDAP search of the signed-in user's domain | signed-in user |

The local server is always collected as the signed-in user (Windows refuses alternate credentials for local WMI). It is recognised by short name, FQDN and DNS host name.

## Collection flow (per server)
1. **Readiness probe:** ping, then a WMI query of `Win32_OperatingSystem`. Classification: OK (Local/DCOM), Unreachable (no ping), Authentication failed, Access denied, WMI (DCOM/RPC) blocked or not responding, WMI connection failed. A failed probe makes the server **Failed**.
2. **System information:** `Win32_ComputerSystem`, `Win32_BIOS`, `Win32_SystemEnclosure`, `Win32_OperatingSystem` (failure here = core collection failed).
3. **Steps** (each recorded in `SectionTimings` with OK / Failed / Timed out / No data): Processors/Memory, Disks/Volumes, Network, Services/Hotfixes/Shares, Time zone/OS, then platform classification, then *Local accounts, printers, page file, features*.
4. **Role detection,** then the role steps: Active Directory, DNS Server, DHCP Server, IIS, SQL Server.
5. Installed applications, Certificates.
6. **Status:** Complete = every step OK; Partial = any step Failed, No data or Timed out; Failed = the probe or the core collection failed. The zero-row rule (v0.9.9h) marks a step that returned no rows as No data.

## Time limits
- Every WMI query, registry connection and group-member lookup runs under a hard limit (`Hard.Run`): the caller continues when the limit is reached and the hung call is abandoned.
- Every step runs under `SectionTimeoutSec`. A step writes its data into its own buffer, which is merged into the server result only when the step finished, so an abandoned step can never change a result that is being saved.
- Every server runs under `ServerTimeLimitMinutes`. On timeout or Cancel the runner stops waiting, records the server as Failed (JSON + DOCX), and the collection thread stops at its next step.

## Reporting
- The runner saves `RawData\<server>.json`, then the DOCX (each in its own try/catch), and the HTML summary after all servers.
- DOCX tables use a fixed column grid (`tblLayout fixed`); every row must have exactly one cell per column, otherwise a visible warning row replaces the data (the role of Normalize-IABBTableRows in v1.x).
- Sections tied to a step print "Not collected: ..." instead of an empty table; "Complete" is never paired with empty tables.
- All text passes through `Txt.Clean` (XML-illegal characters removed) before XML/HTML escaping.
- Verified for v2.0.0: the 153 RawData files of the v1.0.2 field run render to DOCX files whose XML is byte-for-byte identical to the PowerShell engine's (apart from the Generated line and version strings).

## Updates
- Online: GitHub `releases/latest` of `UpdateRepository` (system proxy, TLS 1.2). Assets `ServerDocumentationTool.exe` and `ServerDocumentationTool.exe.sha256`.
- From file: the chosen exe with its `.sha256` next to it.
- Both: the exe is copied to `%TEMP%`, SHA256, product name and version are checked; the running exe is renamed to `.old`, the new exe is copied in its place and started with `--updated`; `.old` is deleted on the next start. Settings in `%ProgramData%` are untouched.

## Build and release (GitHub Actions, windows-latest)
1. `VERSION.txt` is checked; it must match the tag on tag builds.
2. `dotnet build` of the app and the unit tests with `-p:Version=<VERSION.txt>`.
3. Unit tests (`tests/UnitTests`, fixtures in `tests/UnitTests/fixtures`).
4. The runner installs the DNS, DHCP and IIS roles (zone `ci.test`, forwarder `192.0.2.53`, scope `192.0.2.0`).
5. `ServerDocumentationTool.exe --selftest` collects the runner through the app's own code path and checks: Complete status, local transport, DOCX and banner, JSON re-render, HTML summary, and the DNS, DHCP and IIS data.
6. `build/Package.ps1` creates the exe and its SHA256 (version and product name checked).
7. A `v*` tag runs `gh release create` with both files and `Docs/v<ver>-ReleaseNotes.md`.

## Rules that must not regress
- `DocxReport.Write` and `HtmlSummary.Write` (the v1.x exported New-IABBOpenXmlDocx / New-IABBHtmlSummary) produce the same documents from the same data.
- Fixed column-grid tables with the row/column check; per-step error handling; the Collection Status banner.
- Hyper-V host = physical; VMs show the hypervisor; Hyper-V inside a VM = nested.
- "Complete" is never paired with empty tables.
- Removed on purpose, do not restore: Scheduled Tasks, AD DS DC/FSMO tables, DNS zone table.
