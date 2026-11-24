# Server Documentation Tool

A Windows app that produces as-built documentation for Windows servers:

- one Word document per server (`SERVER_AsBuilt.docx`, native OpenXML; Word is not needed),
- an HTML run summary,
- one RawData JSON file per server with everything collected,
- a run log (`ServerDocumentation_Run.log`).

Servers only. From v2.0.0 the tool is **one native exe** (`ServerDocumentationTool.exe`, C# / .NET Framework 4.8). It needs **no PowerShell engine, no WinRM and no RSAT**: everything is read over WMI (DCOM), the Remote Registry service, the admin share, LDAP and the DHCP Server API.

## Download and install

1. Open **[Releases](https://github.com/bobsdomain/ServerDocumentationTool/releases)** and download **`ServerDocumentationTool.exe`** (and `ServerDocumentationTool.exe.sha256` if you want to check it, or to install it later with *Update from File...*).
2. Right-click the exe, choose **Properties**, tick **Unblock** and click OK.
3. Put it in a folder you can write to, for example `C:\Tools\ServerDocumentationTool`. Avoid `C:\Program Files` unless you always run it as administrator, because the update replaces the exe in its folder.
4. Run **ServerDocumentationTool.exe**. It asks for administrator rights.

That is the whole installation: there is no Engine folder, no updater and no .config file.

**First run:** the app is not code-signed, so Windows SmartScreen may show "Windows protected your PC". Click **More info**, then **Run anyway**. Some corporate antivirus/EDR products block unsigned applications; if that happens, ask for an allow-list entry for the exe.

**Upgrading from v1.x:** v1.x cannot update itself to v2.0.0 (the release format changed from a ZIP to a single exe). Install v2.0.0 once by hand as above; you can delete the old v1.x folder (`InfrastructureAsBuiltBuilder.exe`, `IABB.Updater.exe`, `Engine\`) afterwards. v2.0.0 has its own settings file (see Settings).

## Requirements

**PC running the app**
- Windows 10/11 or Windows Server 2012 R2 or later, with .NET Framework 4.8 (built into current Windows).
- Nothing else: PowerShell, RSAT and WinRM are not used.

**Each target server**
- The account used (your logon, or the alternate credentials) must be a **local administrator**.
- **WMI over DCOM** reachable: TCP 135 (RPC endpoint mapper) and the dynamic RPC ports, or the fixed WMI port if you set one. This is the "Windows Management Instrumentation" firewall rule group.
- **SMB (TCP 445)** for the Remote Registry service (applications, certificates, SQL) and the admin share `ADMIN$` (IIS configuration). If the Remote Registry service is not available, the registry is read through WMI instead (slower, same data).
- Domain controllers: **LDAP (TCP 389)**. DHCP servers: RPC to the DHCP Server service.

## Using it

1. Enter the **Client Name** and **Output Path** (**Browse...**).
2. Add servers:
   - type names and click **Add Servers**;
   - **Import from File**: a `.txt` file with one name per line, or a `.csv` file with a `ServerName`, `Server`, `ComputerName` or `Name` column (`Docs/Samples` shows both);
   - **Add Local Server**;
   - **Discover Domain**. It skips disabled computer accounts, cluster name objects, and accounts with no domain logon for 90 days (`DiscoveryStaleDays`); each skipped object is listed in the log with its reason.
3. Tick **Use alternate credentials** when the targets are in another domain (`DOMAIN\adminuser`). Use **Change...** to re-enter them. They are kept in memory only.
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
| `ServerDocumentation_Run.log` | The run log |

**Collection status**
- **Complete:** every step succeeded and returned data.
- **Partial:** at least one step failed, timed out or returned no data.
- **Failed:** the server could not be reached, collection could not start, or it exceeded the per-server time limit.

**Rebuild reports without collecting again:** `ServerDocumentationTool.exe --render <RawData folder> <output folder> [client name]` builds the DOCX files and HTML summary from saved RawData JSON, including JSON from v1.x runs.

## Updates

On start-up the app checks GitHub for a newer release. You can also click **Check for Updates**. If one is available, **Update available: vX.Y.Z** appears at the top right. Click it, and the app:

1. downloads `ServerDocumentationTool.exe` and its `.sha256`, and checks the SHA256, the product name and the version;
2. asks before installing;
3. renames itself to `ServerDocumentationTool.exe.old`, puts the new exe in its place and starts it (the `.old` file is deleted on the next start).

Your settings are not touched. The update uses the system proxy.

**Servers without internet: Update from File...**
1. On a PC with internet, download `ServerDocumentationTool.exe` **and** `ServerDocumentationTool.exe.sha256` from Releases.
2. Copy both to the server, into the same folder (not the folder the app runs from).
3. Click **Update from File...** and choose the exe.

The checksum is verified before anything changes; the update then runs exactly like the online one.

## Settings: `%ProgramData%\ServerDocumentationTool\appsettings.json`

Created with the defaults on first start. **Open Settings** opens it in Notepad; changes apply the next time the app starts.

| Key | Default | Meaning |
|---|---|---|
| `DefaultOutputRoot` | C:\ServerDocumentation | Output Path shown at start-up |
| `DefaultClientName` | Client | Client Name shown at start-up |
| `ParallelServers` | 4 | Servers collected at once (1-16; also on the main window) |
| `ServerTimeLimitMinutes` | 30 | Hard limit per server; a server that runs longer is stopped and reported as Failed |
| `SectionTimeoutSec` | 300 | Hard limit per collection step |
| `WmiQueryTimeoutSec` | 180 | Hard limit per WMI query |
| `ConnectTimeoutSec` | 30 | Readiness probe / connection limit |
| `GroupMemberDisplayCap` / `ApplicationDisplayCap` | 15 / 200 | Caps in the report (full lists stay in the JSON) |
| `IncludeWorkstations` | false | Also write reports for workstation operating systems |
| `ServerOperatingSystemFilter` | Windows Server | Discover Domain filter |
| `DiscoveryStaleDays` | 90 | Discover Domain skips accounts with no domain logon for this many days (0 = off); disabled accounts and cluster name objects are always skipped |
| `UpdateRepository` | bobsdomain/ServerDocumentationTool | Where the update button looks |
| `CheckForUpdatesOnStart` | true | Check for updates when the app starts |

## Repository layout and building

| Path | Content |
|---|---|
| `src/ServerDocumentationTool/` | The app (C# 7.3, .NET Framework 4.8, WinForms): `Collect/` collectors, `Report/` DOCX and HTML, `Common/` data model and JSON, `App/` window, runner, updates, self-test |
| `tests/UnitTests/` | Unit tests for the collector logic and report rules (run by CI) |
| `build/Package.ps1` | Creates `dist/ServerDocumentationTool.exe` and its SHA256 file |
| `.github/workflows/build.yml` | GitHub Actions: build, unit tests, DNS/DHCP/IIS role fixtures, app self-test (real local collection), package; publishes a Release for `v*` tags |
| `Docs/` | Architecture, roadmap, release notes, sample server lists |
| `legacy/powershell-engine-v1.1.0/` | The v1.x PowerShell engine, kept for reference only (not built or shipped) |

**To release a version:**
1. Set `VERSION.txt` (for example `2.0.1`) and add `Docs/v2.0.1-ReleaseNotes.md`.
2. Commit and push.
3. When the build is green, create and push the tag `v2.0.1` (GitHub Desktop: *History*, right-click the commit, *Create Tag...*, then *Push*).

The tag build publishes the Release, and the update button picks it up.

**This repository is public. Never commit client output** (DOCX, HTML, RawData JSON, run logs). `.gitignore` blocks these file types.
