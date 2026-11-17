# Infrastructure As-Built Builder (ServerDocumentationTool)

A Windows app that produces SYDI-style as-built documentation for Windows servers:

- one Word document per server (`SERVER_AsBuilt.docx`, native OpenXML; Word is not needed),
- an HTML run summary,
- one RawData JSON file per server with everything collected,
- a run log (`IABB_Run.log`).

Servers only. v1.0.0 is the first app release, built on the field-tested PowerShell engine v0.9.9i.

## Download and install

1. Open **[Releases](https://github.com/CPSDomain/ServerDocumentationTool/releases)** and download `InfrastructureAsBuiltBuilder_v<version>.zip`. The `.sha256` file next to it is its checksum.
2. Right-click the ZIP, choose **Properties**, tick **Unblock** and click OK. This stops Windows marking every extracted file as "from the internet".
3. Extract it to a folder you can write to, for example `C:\Tools\InfrastructureAsBuiltBuilder`. Avoid `C:\Program Files`, because the update button needs write access to the folder.
4. Run **InfrastructureAsBuiltBuilder.exe**. It asks for administrator rights.

**First run:** the app is not code-signed, so Windows SmartScreen may show "Windows protected your PC". Click **More info**, then **Run anyway**. Some corporate antivirus/EDR products block unsigned applications; if that happens, ask for an allow-list entry, or use the PowerShell launcher below.

## Requirements

**PC running the app**
- Windows 10/11 or Windows Server 2016 or later (2012 R2 with WMF 5.1 also works).
- .NET Framework 4.8 and Windows PowerShell 5.1 (both built into current Windows).
- Recommended: RSAT for Active Directory, DNS Server and DHCP Server. These are only used when a target has no WinRM, or lacks the role's PowerShell module.

**Each target server**
- The account used (your logon, or the alternate credentials) must be a **local administrator**.
- **WinRM** (PowerShell remoting) for full collection. Without it, the app falls back to DCOM/WMI, and local accounts, page file, applications, certificates, IIS and SQL show "Not collected" (the report is Partial).
- Firewall: WinRM (TCP 5985), or RPC/WMI for DCOM; AD Web Services (TCP 9389) on domain controllers for the RSAT fallback.

## Using it

1. Enter the **Client Name** and **Output Path** (**Browse...**).
2. Add servers:
   - type names and click **Add Servers**;
   - **Import from File** (`Engine\Samples\servers.txt` / `servers.csv` show the format);
   - **Add Local Server**;
   - **Discover Domain**.
3. Tick **Use alternate credentials** when the targets are in another domain (`DOMAIN\adminuser`). Use **Change...** to re-enter them.
4. Set **Servers at once** (default 4).
5. Optionally click **Test Reachability**.
6. Click **Start Assessment**.
   - Rows turn blue while running, then green (Complete), amber (Partial), red (Failed) or grey (Cancelled).
   - **Cancel** stops the servers that are running and skips the rest. Finished servers keep their reports.
7. **Open Output Folder** opens `<Output Path>\<Client>_AsBuilt_<yyyyMMdd_HHmmss>\`.

The server running the app is collected locally as the signed-in user, whether you enter it by short name or full name.

## Output

| File | Content |
|---|---|
| `<SERVER>_AsBuilt.docx` | Per-server report with Collection Status banner, warnings and timings |
| `<Client>_Server_Summary.html` | Run summary: status, transport, classification, issues per server |
| `RawData\<SERVER>.json` | Everything collected, including the full certificate and application lists |
| `IABB_Run.log` | The run log |

**Collection status**
- **Complete:** every step succeeded and returned data.
- **Partial:** at least one step failed, timed out or returned no data.
- **Failed:** the server could not be reached, collection could not start, or it exceeded the per-server time limit.

## Updates

On start-up the app checks GitHub for a newer release. You can also click **Check for Updates**. If one is available, **Update available: vX.Y.Z** appears at the top right. Click it, and the app:

1. downloads the release and checks its SHA256,
2. closes,
3. replaces its files (your `Engine\Config\appsettings.json` is kept; the new defaults are saved as `appsettings.default.json`),
4. restarts.

The update uses the system proxy. If GitHub cannot be reached, download the ZIP from Releases manually. The updater's log is `%TEMP%\IABB_Update.log`.

## Settings: `Engine\Config\appsettings.json`

| Key | Default | Meaning |
|---|---|---|
| `ParallelServers` | 4 | Servers collected at once (1-16; also on the main window) |
| `ServerTimeLimitMinutes` | 30 | Hard limit per server; a server that runs longer is stopped and reported as Failed |
| `SectionTimeoutSec` | 300 | Limit per collection step |
| `CimOperationTimeoutSec` | 180 | Limit per WMI/CIM query |
| `TimeoutSeconds` | 30 | Readiness probe / connection timeout |
| `EnableDcomFallback` | true | Use DCOM when WinRM fails |
| `GroupMemberDisplayCap` / `ApplicationDisplayCap` | 15 / 200 | Caps in the report (full lists stay in the JSON) |
| `ServerOperatingSystemFilter` | Windows Server | Discover Domain filter |
| `UpdateRepository` | CPSDomain/ServerDocumentationTool | Where the update button looks |
| `CheckForUpdatesOnStart` | true | Check for updates when the app starts |

## PowerShell launcher (fallback)

If the app is blocked, the same engine still runs as before: `Engine\Start-InfrastructureAsBuiltBuilder.ps1`, from an elevated Windows PowerShell after unblocking the files. It has no Cancel, parallel collection or update button.

## Repository layout and building

| Path | Content |
|---|---|
| `engine/` | PowerShell engine (modules, config, template, samples, legacy launcher) |
| `src/InfrastructureAsBuiltBuilder/` | The app (C#, .NET Framework 4.8, WinForms) |
| `src/IABB.Updater/` | The updater |
| `tests/` | Syntax check and engine smoke test (Windows PowerShell 5.1) |
| `build/Package.ps1` | Builds the release ZIP and its SHA256 |
| `.github/workflows/build.yml` | GitHub Actions: syntax check, analyzer, smoke test (real local collection), build, package; publishes a Release for `v*` tags |
| `Docs/` | Architecture, roadmap, release notes |

**To release a version:**
1. Set `VERSION.txt` (for example `1.0.1`) and add `Docs/v1.0.1-ReleaseNotes.md`.
2. Commit and push.
3. When the build is green, create and push the tag `v1.0.1` (GitHub Desktop: *History*, right-click the commit, *Create Tag...*, then *Push*).

The tag build publishes the Release, and the update button picks it up.

**This repository is public. Never commit client output** (DOCX, HTML, RawData JSON, run logs). `.gitignore` blocks these file types.
