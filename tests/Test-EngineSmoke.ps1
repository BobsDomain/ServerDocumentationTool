# Engine smoke test on Windows PowerShell 5.1 (GitHub Actions windows runner, or any Windows PC):
#   1. imports every engine module in the app's order and checks the functions the app and the legacy launcher need;
#   2. checks local-server recognition (short name, FQDN, DNS host name, trailing dot);
#   3. builds a DOCX from a result containing XML-illegal control characters and checks the XML is valid;
#   4. runs a REAL collection of this machine through the app entry point (Invoke-IABBAppServer) and validates the JSON, DOCX and HTML.
# Usage:  powershell -File .\tests\Test-EngineSmoke.ps1 [-OutputPath C:\Temp\IABB_Smoke]
param([string]$OutputPath = (Join-Path $env:TEMP 'IABB_Smoke'))

$ErrorActionPreference = 'Stop'
$script:failures = 0
function Pass([string]$m) { Write-Host ("PASS  " + $m) }
function Fail([string]$m) { Write-Host ("FAIL  " + $m); $script:failures++ }
function Test-DocxXml([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $z.GetEntry('word/document.xml')
        if (-not $entry) { return 'word/document.xml missing' }
        $sr = New-Object System.IO.StreamReader($entry.Open())
        try { $x = $sr.ReadToEnd() } finally { $sr.Dispose() }
        try { [void]([xml]$x); return $null } catch { return $_.Exception.Message }
    } finally { $z.Dispose() }
}

$root = Split-Path -Parent $PSScriptRoot
$mods = Join-Path $root 'engine\Modules'
$order = @('IABB.Core.psm1','IABB.Discovery.psm1','IABB.RoleDetection.psm1','IABB.Collector.ADDS.psm1','IABB.Collector.DNS.psm1','IABB.Collector.DHCP.psm1','IABB.Collector.IIS.psm1','IABB.Collector.SQL.psm1','IABB.Collector.Applications.psm1','IABB.Collector.Certificates.psm1','IABB.Collector.Server.psm1','IABB.DocumentBuilder.psm1','IABB.AppHost.psm1','IABB.UI.psm1')
Write-Host ("PowerShell " + $PSVersionTable.PSVersion + " on " + [Environment]::OSVersion.VersionString)

# 1. modules and functions
foreach ($m in $order) {
    try { Import-Module (Join-Path $mods $m) -Force -WarningAction SilentlyContinue; Pass ("import " + $m) } catch { Fail ("import " + $m + ": " + $_.Exception.Message) }
}
$required = @('New-IABBHtmlSummary','New-IABBOpenXmlDocx','Test-IABBWorkstationOS','Invoke-IABBServerCollection','Test-IABBCollectionReadiness','New-IABBCimSession','Invoke-IABBRoleCollectorTimed','Get-IABBPlatformInfo','Invoke-IABBRoleQuery','New-IABBFailedResult','Invoke-IABBTimed','Test-IABBIsLocal','Register-IABBSession','Get-IABBSession','Unregister-IABBSession','Get-IABBDomainServers','Import-IABBServerList','ConvertFrom-IABBServerText','New-IABBRunFolder','Invoke-IABBADDSCollection','Invoke-IABBDNSCollection','Invoke-IABBDHCPCollection','Invoke-IABBIISCollection','Invoke-IABBSqlCollection','Invoke-IABBApplicationCollection','Invoke-IABBCertificateCollection','Invoke-IABBRoleDetection','Invoke-IABBAppServer','Invoke-IABBAppFailedServer','New-IABBAppSummary','Complete-IABBAppServer','Start-IABBApplication')
foreach ($f in $required) { if (Get-Command $f -ErrorAction SilentlyContinue) { Pass ("function " + $f) } else { Fail ("function missing: " + $f) } }

# 2. local-server recognition
$cn = $env:COMPUTERNAME
$dom = ''; try { $dom = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().DomainName } catch {}
$localNames = @('', '.', 'localhost', $cn, $cn.ToLowerInvariant())
if ($dom) { $localNames += ($cn + '.' + $dom); $localNames += ($cn + '.' + $dom).ToUpperInvariant() } else { Write-Host '  (no DNS domain on this machine - FQDN form tested through the DNS host name below)' }
$localNames += ($cn + '.')   # trailing dot
try { $hn = [System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName; if ($hn) { $localNames += $hn } } catch {}
foreach ($n in $localNames) { if (Test-IABBIsLocal $n) { Pass ("local: '" + $n + "'") } else { Fail ("not recognised as local: '" + $n + "'") } }
foreach ($n in @('NOT-A-REAL-HOST-1234', 'server01.contoso.local')) { if (-not (Test-IABBIsLocal $n)) { Pass ("remote: '" + $n + "'") } else { Fail ("wrongly treated as local: '" + $n + "'") } }

# 2b. DNS collector (v1.1.0: DNS WMI provider with a hard per-query limit). The workflow installs the DNS role with zone ci.test and forwarder 192.0.2.53.
if (Get-Service DNS -ErrorAction SilentlyContinue) {
    $d = Invoke-IABBDNSCollection -ComputerName $cn -TimeoutSec 240
    if ($d.Success) { Pass ("DNS collected via " + $d.Source + " (" + @($d.Zones).Count + " zones, method " + $d.Method + ")") } else { Fail ("DNS collection failed: " + $d.Error) }
    if ([string]$d.Source -like 'DNS WMI provider*') { Pass 'DNS read through the WMI provider' } else { Fail ("DNS not read through the WMI provider: " + $d.Source) }
    if (@($d.Zones | Where-Object { $_.ZoneName -eq 'ci.test' -and $_.ZoneType -eq 'Primary' }).Count -eq 1) { Pass 'DNS test zone ci.test found (Primary)' } else { Fail 'DNS test zone ci.test not found' }
    if (@($d.Forwarders) -contains '192.0.2.53') { Pass 'DNS forwarder 192.0.2.53 found' } else { Fail ("DNS forwarder not found: " + (@($d.Forwarders) -join ', ')) }
} else { Write-Host 'SKIP  DNS Server role not installed on this machine' }

# 2c. discovery returns the skip fields (no AD on the build machine: the local fallback is returned)
$disc = @(Get-IABBDomainServers -StaleDays 90)
if ($disc.Count -ge 1 -and $disc[0].PSObject.Properties['Skipped'] -and $disc[0].PSObject.Properties['SkipReason']) { Pass 'discovery returns Skipped/SkipReason' } else { Fail 'discovery result has no Skipped/SkipReason' }

New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null
$raw = Join-Path $OutputPath 'RawData'; New-Item -Path $raw -ItemType Directory -Force | Out-Null

# 3. control characters must not corrupt the DOCX
$bad = New-IABBFailedResult -ComputerName ('CTRL' + [char]1 + 'TEST') -Message ('control ' + [char]0x1F + ' chars ' + [char]0x0B)
$docBad = Join-Path $OutputPath 'ControlChars_AsBuilt.docx'
New-IABBOpenXmlDocx -ServerResults @($bad) -OutputPath $docBad -ClientName ('CI' + [char]2)
$e = Test-DocxXml $docBad
if ($e) { Fail ("control-character DOCX is not valid XML: " + $e) } else { Pass 'control-character DOCX is valid XML' }

# 4. real local collection through the app entry point
$sw = [Diagnostics.Stopwatch]::StartNew()
$o = Invoke-IABBAppServer -ComputerName $cn -RawDataPath $raw -ReportFolder $OutputPath -ClientName 'CI' -SectionTimeoutSec 240 -InformationVariable progress
$sw.Stop()
Write-Host ("Local collection: {0} [{1}] {2} in {3:N0} s, roles: {4}" -f $o.Status, $o.Transport, $o.Classification, $sw.Elapsed.TotalSeconds, $o.Roles)
foreach ($s in @($o.NonOkSteps)) { Write-Host ("  step: " + $s) }
foreach ($w in @($o.Warnings)) { Write-Host ("  warning: " + $w) }
Write-Host ("  progress messages: " + @($progress).Count)
if ($o.Status -eq 'Failed') { Fail 'local collection failed' } else { Pass ("local collection status " + $o.Status) }
if ($o.Transport -ne 'Local') { Fail ("local server not collected locally (transport " + $o.Transport + ")") } else { Pass 'local server collected locally' }
if (@($progress).Count -gt 0) { Pass 'progress messages on the Information stream' } else { Fail 'no progress messages on the Information stream' }
$json = Join-Path $raw ((ConvertTo-IABBSafeFileName $cn) + '.json')
if (Test-Path $json) { try { $null = Get-Content $json -Raw | ConvertFrom-Json; Pass 'RawData JSON written and parses' } catch { Fail ("RawData JSON does not parse: " + $_.Exception.Message) } } else { Fail 'RawData JSON missing' }
if ($o.DocName) {
    $e = Test-DocxXml (Join-Path $OutputPath $o.DocName)
    if ($e) { Fail ("local DOCX is not valid XML: " + $e) } else { Pass ("local DOCX valid: " + $o.DocName) }
    $x = $null; Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $OutputPath $o.DocName)); try { $sr = New-Object System.IO.StreamReader($z.GetEntry('word/document.xml').Open()); $x = $sr.ReadToEnd(); $sr.Dispose() } finally { $z.Dispose() }
    $ver = (Get-Content (Join-Path $root 'VERSION.txt') -Raw).Trim()
    if ($x -match ('Server Documentation Tool v' + [regex]::Escape($ver))) { Pass ('DOCX shows Server Documentation Tool v' + $ver) } else { Fail ('DOCX does not show Server Documentation Tool v' + $ver) }
} else { Fail ("no DOCX produced: " + $o.ReportError) }

$html = Join-Path $OutputPath 'CI_Server_Summary.html'
New-IABBAppSummary -Results @($o.Result) -CancelledServers @('CANCELLED-TEST') -OutputPath $html -ClientName 'CI'
if ((Test-Path $html) -and ((Get-Content $html -Raw) -match 'Cancelled by user')) { Pass 'HTML summary written (with a cancelled server)' } else { Fail 'HTML summary missing or incomplete' }

Write-Host ("Smoke test finished with {0} failure(s). Output: {1}" -f $script:failures, $OutputPath)
if ($script:failures -gt 0) { exit 1 }
exit 0
