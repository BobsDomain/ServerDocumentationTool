# Infrastructure As-Built Builder: Architecture (v1.0.0)

## Components
| Component | Technology | Role |
|---|---|---|
| `InfrastructureAsBuiltBuilder.exe` | C# 7.3, .NET Framework 4.8, WinForms (`src/InfrastructureAsBuiltBuilder`) | GUI, run orchestration, parallelism, Cancel, hard per-server time limit, run log, update check |
| PowerShell engine | Windows PowerShell 5.1 modules (`engine/Modules`, shipped as `Engine\Modules`) | All collection and report generation (unchanged rules from v0.9.9i) |
| `IABB.Updater.exe` | C# (`src/IABB.Updater`) | Applies a downloaded, SHA256-verified release after the app closes, keeps `Engine\Config\appsettings.json`, restarts the app |
| `Engine\Start-InfrastructureAsBuiltBuilder.ps1` | PowerShell + `IABB.UI.psm1` | Legacy launcher / fallback when the exe is blocked |

## How the app runs the engine
- `EngineHost` opens one runspace pool (Windows PowerShell 5.1 from the OS, process-scope execution policy Bypass). Every engine module in `EngineHost.ModuleOrder` is imported, and the functions the app needs are verified at start-up.
- `AssessmentRunner` starts one pipeline per server, `Invoke-IABBAppServer` (IABB.AppHost):
  - collection, then RawData JSON, then DOCX, then a summary object back to the app;
  - a `SemaphoreSlim` limits servers at once (`ParallelServers`);
  - each pipeline races a timer (`ServerTimeLimitMinutes`). On timeout or Cancel the pipeline is stopped asynchronously (`BeginStop`), so it never holds up other servers;
  - a timed-out server is written as Failed (`Invoke-IABBAppFailedServer`).
- Progress: the engine's `-OnProgress` callback writes `Write-Information` records (tag `IABBProgress`). The app reads them from the pipeline's Information stream and shows them in the grid and the log.
- After all servers: `New-IABBAppSummary` writes the HTML summary (cancelled servers listed as Failed "Cancelled by user").
- `RunLogger` writes the log pane and `IABB_Run.log` (thread-safe).

## Update flow
1. `UpdateService.GetLatestRelease` reads `https://api.github.com/repos/<UpdateRepository>/releases/latest` through the system proxy, over TLS 1.2.
2. It needs the assets `InfrastructureAsBuiltBuilder_v<ver>.zip` and `<zip>.sha256`.
3. If the release is newer than the exe's version, the ZIP and checksum are downloaded to `%TEMP%` and SHA256 is verified.
4. `IABB.Updater.exe` is copied to `%TEMP%` and started with `--pid --zip --target --start`; the app closes.
5. The updater:
   - waits for the app to exit;
   - extracts the ZIP;
   - copies the files over the app folder (keeping `Engine\Config\appsettings.json`, saving new defaults as `appsettings.default.json`);
   - restarts the app.

   Log: `%TEMP%\IABB_Update.log`.

## Build and release (GitHub Actions, windows-latest)
1. `VERSION.txt` is checked; it must match the tag on tag builds.
2. `tests/Test-EngineSyntax.ps1` parses every PowerShell file with Windows PowerShell 5.1.
3. PSScriptAnalyzer runs (report only).
4. `tests/Test-EngineSmoke.ps1`:
   - imports the modules and checks the exported functions;
   - checks local-name recognition;
   - builds a control-character DOCX;
   - runs a **real local collection** through `Invoke-IABBAppServer` and validates the JSON, DOCX and HTML.
5. `dotnet build` builds the app and updater with `-p:Version=<VERSION.txt>`.
6. `build/Package.ps1` creates the ZIP (root = app folder) and its SHA256 (`System.Management.Automation.dll` is never shipped) and uploads them as an artifact.
7. A `v*` tag runs `gh release create` with the ZIP, its SHA256, and `Docs/v<ver>-ReleaseNotes.md`.

## Engine (PowerShell)

### Runtime
- Long-running work runs in jobs (`Invoke-IABBTimed`). Local targets use `Start-Job`.
- Remote targets use `Invoke-Command -AsJob`, on the server's **reusable PSSession** when one is registered (v1.0.0).
- Every step has a time limit.

## Modules and import order
| # | Module | Responsibility | Key functions |
|---|---|---|---|
| 1 | IABB.Core | Config, file helpers, server lists, local-name detection, reusable remoting sessions, timed jobs, role-query runner, failed-result record | `Get-IABBConfig`, `Import-IABBServerList`, `ConvertFrom-IABBServerText`, `Test-IABBIsLocal`, `Get-IABBLocalNames`, `Register-/Get-/Unregister-IABBSession`, `Invoke-IABBTimed`, `Invoke-IABBRoleQuery`, `New-IABBFailedResult`, `New-IABBRunFolder`, `Save-IABBTextFile` |
| 2 | IABB.Discovery | AD server discovery | `Get-IABBDomainServers` |
| 3 | IABB.RoleDetection | Roles from services, features, shares, printers, DomainRole and platform (DC, DNS, DHCP, AD CS, IIS, SQL, Hyper-V Host / nested, RDS Session Host, File, Print, DFS Namespace, DFS Replication); role notes (e.g. demoted DC) | `Invoke-IABBRoleDetection` |
| 4 | IABB.Collector.ADDS | Forest/domain, functional levels, FSMO, DCs | `Invoke-IABBADDSCollection` |
| 5 | IABB.Collector.DNS | Zones, forwarders | `Invoke-IABBDNSCollection` |
| 6 | IABB.Collector.DHCP | IPv4 scopes | `Invoke-IABBDHCPCollection` |
| 7 | IABB.Collector.IIS | Version, sites, bindings, app pools, applications (WebAdministration, appcmd fallback) | `Invoke-IABBIISCollection` |
| 8 | IABB.Collector.SQL | SQL instances from the registry (64/32-bit) | `Invoke-IABBSqlCollection` |
| 9 | IABB.Collector.Applications | Uninstall registry (64/32-bit) | `Invoke-IABBApplicationCollection` |
| 10 | IABB.Collector.Certificates | LocalMachine My/Root/CA/WebHosting | `Invoke-IABBCertificateCollection` |
| 11 | IABB.Collector.Server | Readiness probe, CIM session (WSMan -> DCOM), core inventory steps, orchestration of all collectors, status | `Invoke-IABBServerCollection`, `Test-IABBCollectionReadiness`, `New-IABBCimSession`, `Get-IABBPlatformInfo`, `Invoke-IABBRoleCollectorTimed` |
| 12 | IABB.DocumentBuilder | HTML summary, per-server DOCX | **exported:** `New-IABBHtmlSummary`, `New-IABBOpenXmlDocx`, `Test-IABBWorkstationOS` |
| 13 | IABB.AppHost | App entry points: one server end to end (collect, JSON, DOCX), failed record, HTML summary | `Invoke-IABBAppServer`, `Invoke-IABBAppFailedServer`, `Complete-IABBAppServer`, `New-IABBAppSummary` |
| 14 | IABB.UI | Legacy PowerShell GUI (fallback launcher only) | `Start-IABBApplication` |

## Collection flow (per server)
0. **Local check (v1.0.0):** the target is local when it matches the computer name, `COMPUTERNAME.<DNS domain>` or the DNS host name. Local targets are collected as the signed-in user, without CIM/WinRM sessions.
1. **Readiness probe:** ping, then a CIM session over WSMan, falling back to DCOM if enabled. Classifies the target as OK (WSMan/DCOM/Local), Unreachable, Authentication failed, Access denied, WMI/WinRM blocked or CIM failed. If the probe fails, the server is **Failed** and its report shows only status, warnings and timings.
2. **CIM session** (same protocol logic), plus one **reusable PSSession** when the transport is WSMan (v1.0.0), for the core steps:
   - Processors/Memory
   - Disks/Volumes
   - Network
   - Services/Hotfixes/Shares
   - Time zone/OS (+ RDS Session Host mode)
3. **Platform classification:** from manufacturer/model, the Azure SMBIOS asset tag (v0.9.9i) and the `vmms` service (Hyper-V host = physical; Hyper-V inside a VM = "nested").
4. **Remoting step** (WinRM): local users/groups (fast ADSI), printers (only when the Print Spooler is running; otherwise a note), page file, environment variables, roles and features.
5. **Role detection,** then the role collectors:
   - **AD DS / DNS / DHCP** (v0.9.9h) via `Invoke-IABBRoleQuery`:
     - local target: run locally;
     - WinRM available: run **on the target** (`Server=$null`);
     - DCOM-only, or module missing on the target: run on this PC with RSAT, `-Server` / CIM session with the supplied credentials.

     The result carries `Method` ("Collected Via") and `Notes`.
   - **IIS, SQL:** run on the target over WinRM.
6. **Applications, Certificates:** run on the target over WinRM.
7. **Status:**
   - every step is recorded in `SectionTimings` with a result of OK, Failed, Timed out or No data;
   - **No data** (v0.9.9h) is set when a step that always returns rows on a working server returned none;
   - Complete = all steps OK; Partial = any step Failed, No data or Timed out; Failed = core collection could not run.

## Reporting
- The UI saves `RawData\<server>.json`, then generates one DOCX per server (each in its own try/catch), then the HTML summary.
- DOCX tables use a fixed column grid (`tblLayout fixed`). Rows pass through `New-IABBRowsFlat` / `Get-IABBFlatCells` so single-row tables never flatten into a vertical list. A row/column mismatch writes a visible warning row.
- Sections tied to a collection step use `SecTbl`. A failed or empty step prints "Not collected: ..." instead of an empty table.
- Report-size rules (v0.9.9i): Certificates lists My/WebHosting plus Root/CA certificates expiring within 60 days; Applications lists what Programs and Features shows. Everything else is counted in the summaries and kept in the RawData JSON.
- All text passes through `ConvertTo-IABBCleanText` (XML-illegal characters removed) before XML/HTML escaping.

## Rules that must not regress
- Exported `New-IABBHtmlSummary`, `New-IABBOpenXmlDocx`; check the export list and file sizes after every change.
- Fixed column-grid tables and row flattening (`,@(...)` rows).
- WSMan -> DCOM fallback; per-step try/catch; Collection Status banner.
- Hyper-V host = physical; VMs show the hypervisor.
- "Complete" is never paired with empty tables.
- Use `$(if ...)`, never a bare inline `if`, inside array expressions.
