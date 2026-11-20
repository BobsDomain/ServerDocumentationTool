# IABB.DocumentBuilder - Server Documentation Tool v1.0.3
# v0.9.9h: strip characters that are illegal in XML 1.0 (control characters, unpaired surrogates) - they made Word refuse to open the DOCX
function ConvertTo-IABBCleanText { param([AllowNull()][object]$Value) if($null -eq $Value){ return '' }; [regex]::Replace([string]$Value,'[\x00-\x08\x0B\x0C\x0E-\x1F\uFFFE\uFFFF]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]','') }
function ConvertTo-IABBXmlText { param([AllowNull()][object]$Value) if($null -eq $Value){ return '' }; [Security.SecurityElement]::Escape((ConvertTo-IABBCleanText $Value)) }
function ConvertTo-IABBHtmlText { param([AllowNull()][object]$Value) [Net.WebUtility]::HtmlEncode((ConvertTo-IABBCleanText $Value)) }
# v0.9.9g: readable forest/domain functional levels (2000-2025, named and numeric)
function ConvertTo-IABBReadableMode {
    param([AllowNull()][object]$Value)
    if($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)){ return 'Not collected' }
    $s=([string]$Value).Trim()
    $num=@{'0'='Windows 2000';'1'='Windows Server 2003 Interim';'2'='Windows Server 2003';'3'='Windows Server 2008';'4'='Windows Server 2008 R2';'5'='Windows Server 2012';'6'='Windows Server 2012 R2';'7'='Windows Server 2016';'10'='Windows Server 2025'}
    if($num.ContainsKey($s)){ return ($num[$s]+' Functional Level') }
    if($s -eq 'WinThreshold'){ return 'Windows Server 2016 Functional Level' }
    if($s -eq 'Windows2000MixedDomain'){ return 'Windows 2000 Mixed Domain Functional Level' }
    if($s -eq 'Windows2000NativeDomain'){ return 'Windows 2000 Native Domain Functional Level' }
    if($s -eq 'Windows2000Forest'){ return 'Windows 2000 Forest Functional Level' }
    $m=[regex]::Match($s,'^Windows(\d{4})(R2)?(Interim)?(Forest|Domain)$')
    if($m.Success){ $t='Windows Server '+$m.Groups[1].Value; if($m.Groups[2].Success){ $t+=' R2' }; if($m.Groups[3].Success){ $t+=' Interim' }; return ($t+' '+$m.Groups[4].Value+' Functional Level') }
    $s
}
function Test-IABBWorkstationOS { param([object]$Server) ([string]$Server.OperatingSystem.Caption) -match 'Windows 10|Windows 11|Windows 7|Windows 8|Enterprise for Virtual Desktops' }
function Get-IABBDisplayRoles { param([object]$Server) @($Server.RolesDetected|?{-not [string]::IsNullOrWhiteSpace($_)}) }
function ConvertTo-IABBCellText { param([AllowNull()][object]$Value) if($null -eq $Value){ return '' }; if($Value -is [string]){ return $Value }; if($Value -is [System.Collections.IDictionary]){ return [string]$Value }; if($Value -is [System.Collections.IEnumerable]){ return ((@($Value)|%{ if($null -eq $_){''}else{[string]$_} }) -join ', ') }; [string]$Value }
function Get-IABBFlatCells {
    param([AllowNull()][object]$Item)
    if($Item -is [Array]){
        $n=0; $arr=0
        foreach($x in $Item){ $n++; if($x -is [Array]){ $arr++ } }
        if($n -gt 0 -and $arr -eq $n){ foreach($x in $Item){ Get-IABBFlatCells $x } }
        else { foreach($x in $Item){ ConvertTo-IABBCellText $x } }
    } else { ConvertTo-IABBCellText $Item }
}
function New-IABBRowsFlat { param([object[]]$Rows) foreach($c in @($Rows)){ Get-IABBFlatCells $c } }
function ConvertTo-IABBDouble { param([AllowNull()][object]$Value) if($null -eq $Value){ return $null }; if($Value -is [double] -or $Value -is [single] -or $Value -is [decimal] -or $Value -is [int] -or $Value -is [long] -or $Value -is [uint32] -or $Value -is [uint64]){ return [double]$Value }; $s=([string]$Value).Trim(); if([string]::IsNullOrWhiteSpace($s)){ return $null }; $d=0.0; if([double]::TryParse($s,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$d)){ return $d }; if([double]::TryParse($s,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::CurrentCulture,[ref]$d)){ return $d }; return $null }
function Add-IABBZipEntry { param($ZipArchive,[string]$EntryName,[string]$Content) $e=$ZipArchive.CreateEntry($EntryName,[IO.Compression.CompressionLevel]::Optimal); $s=$e.Open(); try{$w=New-Object IO.StreamWriter($s,(New-Object Text.UTF8Encoding($false))); try{$w.Write($Content)}finally{$w.Dispose()}}finally{$s.Dispose()} }
function Convert-IABBShareType { param([object]$Type) $v=[int64]0; [void][int64]::TryParse([string]$Type,[ref]$v); switch($v){0{'Disk Drive'}1{'Printer Queue'}2{'Device'}3{'IPC'}2147483648{'Administrative Share'}2147483651{'Administrative IPC'}default{if(($v -band 2147483648)-ne 0){'Administrative/Special'}else{[string]$Type}}} }
function Format-IABBDNSForwarders { param([object[]]$Forwarders) (@($Forwarders)|%{$ip=[string]$_;if($ip -eq '168.63.129.16'){'Azure Recursive Resolver (168.63.129.16)'}elseif(-not [string]::IsNullOrWhiteSpace($ip)){$ip}}) -join ', ' }
function Get-IABBFSMOHolderSummary { param([object]$FSMORoles) $h=@(); foreach($n in @('SchemaMaster','DomainNamingMaster','RIDMaster','PDCEmulator','InfrastructureMaster')){try{$v=$FSMORoles.$n;if(-not [string]::IsNullOrWhiteSpace([string]$v)){$h+=[string]$v}}catch{}}; $u=@($h|sort -Unique); if($u.Count -eq 0){'Not collected'}else{($u.Count.ToString()+' Unique FSMO Holder(s): '+($u -join ', '))} }
function Get-IABBGuestType { param([object]$Server) $pt=[string]$Server.Virtualization.PlatformType; if([string]::IsNullOrWhiteSpace($pt)){ return 'Unknown (not collected)' }; if($pt -eq 'Virtual'){'Virtual Machine'}elseif($Server.Virtualization.IsHyperVHost){'Physical Server (Hyper-V host)'}elseif(Test-IABBWorkstationOS $Server){'Physical Workstation'}else{'Physical Server'} }
function Get-IABBVirtualizationPlatform { param([object]$Server) switch([string]$Server.Virtualization.HostPlatform){'Hyper-V'{'Microsoft Hyper-V'}'VMware'{'VMware vSphere/ESXi'}'Physical Server'{'Physical Hardware'}'Physical (Hyper-V host)'{'Physical Hardware (runs Hyper-V)'}'Azure'{'Microsoft Azure'}default{if([string]::IsNullOrWhiteSpace([string]$Server.Virtualization.HostPlatform)){'Unknown (not collected)'}else{[string]$Server.Virtualization.HostPlatform}}} }
function Get-IABBIPv4String { param([object]$Server) $ips=@(); foreach($n in @($Server.NetworkAdapters)){ foreach($ip in (([string]$n.IPAddress)-split ',\s*')){ if($ip -match '^(\d{1,3}\.){3}\d{1,3}$'){ $ips+=$ip } } }; ((@($ips|sort -Unique)) -join ', ') }
function Get-IABBDataVolumes { param($Server) @($Server.VolumeDetails|?{ $_ -and ([string]$_.VolumeType -eq 'Mounted Volume' -or [string]$_.VolumeType -eq 'Mount Point (folder)') }) }
function Get-IABBStorageSummary { param($Server) $vols=@(Get-IABBDataVolumes $Server); $t=0.0;$fr=0.0;$low=0; foreach($v in $vols){ $c=ConvertTo-IABBDouble $v.CapacityGB; $f=ConvertTo-IABBDouble $v.FreeGB; $p=ConvertTo-IABBDouble $v.FreePct; if($null -ne $c){$t+=$c}; if($null -ne $f){$fr+=$f}; if($null -ne $p -and $p -lt 15){$low++} }; [pscustomobject]@{TotalGB=[math]::Round($t,2);FreeGB=[math]::Round($fr,2);UsedGB=[math]::Round($t-$fr,2);FreePct=if($t -gt 0){[math]::Round(($fr/$t)*100,2)}else{'N/A'};VolumeCount=$vols.Count;MountPointCount=@($vols|?{[string]$_.VolumeType -eq 'Mount Point (folder)'}).Count;LowVolumeCount=$low} }
function Get-IABBStorageTotalsRows { param($Server) $x=Get-IABBStorageSummary $Server; @(@('Total Storage GB',$x.TotalGB),@('Used Storage GB',$x.UsedGB),@('Free Storage GB',$x.FreeGB),@('Free Percentage',$x.FreePct),@('Data Volumes',$x.VolumeCount),@('Folder Mount Points',$x.MountPointCount)) }
function Get-IABBCollectionStatusRows { param($Server) $st=$Server.CollectionStatus; if(-not $st){ return @(,@('Status','Unknown')) }; @(@('Status',$st.Status),@('Transport',$st.Transport),@('Ping',$(if($st.PingOk){'OK'}else{'Failed'})),@('Reachable',$(if($st.Reachable){'Yes'}else{'No'})),@('Authorized',$(if($st.Authorized){'Yes'}else{'No'})),@('Classification',$st.Classification),@('Sections OK',$st.SectionsOk),@('Sections Failed',$st.SectionsFailed),@('Steps Timed Out',$st.StepsTimedOut),@('Error',$(if($Server.Error){$Server.Error}else{'None'}))) }
function Get-IABBTimingRows { param($Server) @($Server.SectionTimings|?{ $_ }|%{ $res=[string]$_.Result; $fill=switch($res){'OK'{''}'Timed out'{'FFF3CD'}'Failed'{'F8D7DA'}'No data'{'F8D7DA'}default{''}}; ,@([string]$_.Section,[string]$_.Seconds,$res,$fill) }) }
function Test-IABBCollected { param($Result) ($null -ne $Result) -and [bool]$Result.Success }
function Get-IABBNotCollectedText { param($Result) if($null -eq $Result){ return 'Not collected.' }; if($Result.Error){ return ('Not collected: '+[string]$Result.Error) }; 'Not collected.' }
function Get-IABBAppCountText { param($Server) if((Test-IABBCollected $Server.Applications) -and (@($Server.Applications.Applications|?{ $_ }).Count + @($Server.Applications.HiddenEntries|?{ $_ }).Count) -gt 0){ [string]$Server.Applications.Summary.TotalApplications }else{ 'Not collected' } }
$script:IABBGroupCap = 15
$script:IABBAppCap = 200
function Get-IABBCappedMembers { param([string]$Members,[int]$Count) $cap=$script:IABBGroupCap; $list=@(); if(-not [string]::IsNullOrWhiteSpace($Members)){ $list=@($Members -split ',\s*' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) }; if($Count -le $cap -or $list.Count -le $cap){ return ($list -join ', ') }; $shown=($list[0..($cap-1)] -join ', '); return ($shown + "  (+" + ($Count - $cap) + " more of " + $Count + " total; full list in RawData JSON)") }
function Get-IABBLocalGroupRows { param($Server) @($Server.LocalGroups|?{ $_ }|%{ $mc=0; [void][int]::TryParse([string]$_.MemberCount,[ref]$mc); ,@($_.Name,$_.Description,$_.MemberCount,(Get-IABBCappedMembers -Members ([string]$_.Members) -Count $mc)) }) }
function Get-IABBLargeGroups { param($Server) @($Server.LocalGroups|?{ $_ -and ([int]('0'+[string]$_.MemberCount)) -gt $script:IABBGroupCap }|sort { [int]('0'+[string]$_.MemberCount) } -Descending) }
function Get-IABBCertificateName { param($Cert) if(-not $Cert){return ''}; if(-not [string]::IsNullOrWhiteSpace([string]$Cert.FriendlyName)){return [string]$Cert.FriendlyName}; $subject=[string]$Cert.Subject; if($subject -match 'CN=([^,]+)'){$n=$Matches[1]; if($n -match '^[0-9a-fA-F-]{36}$'){return 'Unnamed Application Certificate'}; return $n}; if(-not [string]::IsNullOrWhiteSpace($subject)){return $subject}; return [string]$Cert.Thumbprint }
function Get-IABBUniqueCertificates {
    param($CertResult)
    $map=[ordered]@{}
    foreach($c in @($CertResult.Certificates)){
        if(-not $c){ continue }
        $key=[string]$c.Thumbprint; if([string]::IsNullOrWhiteSpace($key)){ $key=([string]$c.Subject)+'|'+([string]$c.NotAfter) }
        if(-not $map.Contains($key)){ $map[$key]=[pscustomobject]@{Name=(Get-IABBCertificateName $c);Stores=New-Object System.Collections.ArrayList;DaysUntilExpiry=$c.DaysUntilExpiry;Thumbprint=$c.Thumbprint} }
        $st=[string]$c.Store; if(-not [string]::IsNullOrWhiteSpace($st) -and -not $map[$key].Stores.Contains($st)){ [void]$map[$key].Stores.Add($st) }
    }
    @($map.Values)
}
function Test-IABBActionableStore { param([object[]]$Stores) foreach($s in @($Stores)){ if([string]$s -in @('My','WebHosting')){ return $true } }; return $false }
function Get-IABBCertStatus { param([AllowNull()][object]$Days,[object[]]$Stores) if($null -eq $Days -or [string]$Days -eq ''){ return 'Unknown' }; $d=0; if(-not [int]::TryParse([string]$Days,[ref]$d)){ return 'Unknown' }; if($d -lt 0){ if(Test-IABBActionableStore $Stores){ return 'EXPIRED' } else { return 'Expired (legacy root - informational)' } }elseif($d -le 30){ return 'Expiring <=30d' }elseif($d -le 60){ return 'Expiring <=60d' }else{ return 'OK' } }
function Get-IABBCertStatusFill { param([string]$Status) switch($Status){ 'EXPIRED'{'F8D7DA'} 'Expiring <=30d'{'F8D7DA'} 'Expiring <=60d'{'FFF3CD'} 'OK'{'D4EDDA'} 'Expired (legacy root - informational)'{'E7E6E6'} default{''} } }
# v0.9.9i: the table lists certificates in the server's own stores (My/WebHosting) plus Root/CA certificates expiring within 60 days.
# Other built-in Root/CA certificates (about 94% of rows in the v0.9.9h field test) are counted in the summary only; all are kept in RawData JSON.
function Test-IABBListedCertificate { param($Cert) if(Test-IABBActionableStore @($Cert.Stores)){ return $true }; $d=0; if([int]::TryParse([string]$Cert.DaysUntilExpiry,[ref]$d)){ return ($d -ge 0 -and $d -le 60) }; return $false }
function Get-IABBCertificateRows { param($CertResult) @(Get-IABBUniqueCertificates $CertResult | ?{ Test-IABBListedCertificate $_ } | sort { [int]$_.DaysUntilExpiry } | %{ $stores=@($_.Stores); $st=Get-IABBCertStatus $_.DaysUntilExpiry $stores; ,@($_.Name,($stores -join ', '),$_.DaysUntilExpiry,$st,(Get-IABBCertStatusFill $st)) }) }
function Get-IABBCertificateSummaryRows { param($CertResult) $u=@(Get-IABBUniqueCertificates $CertResult); $actExp=@($u|?{ [int]$_.DaysUntilExpiry -lt 0 -and (Test-IABBActionableStore @($_.Stores)) }).Count; $legExp=@($u|?{ [int]$_.DaysUntilExpiry -lt 0 -and -not (Test-IABBActionableStore @($_.Stores)) }).Count; $soon=@($u|?{ [int]$_.DaysUntilExpiry -ge 0 -and [int]$_.DaysUntilExpiry -le 60 }).Count; $own=@($u|?{ Test-IABBActionableStore @($_.Stores) }).Count; $hiddenRoots=@($u|?{ -not (Test-IABBListedCertificate $_) }).Count; @(@('Server Certificates (Personal / Web Hosting)',$own),@('Expiring Within 60 Days (all stores)',$soon),@('Expired (action required)',$actExp),@('Expired legacy roots (informational, not listed)',$legExp),@('Built-in Root/CA certificates (not listed)',$hiddenRoots),@('Unique Certificates (all stores)',$u.Count)) }
function Get-IABBPatchStatus { param([AllowNull()][object]$Days) if($null -eq $Days -or [string]$Days -eq ''){ return 'Unknown' }; $d=0; if(-not [int]::TryParse([string]$Days,[ref]$d)){ return 'Unknown' }; if($d -le 35){ 'Current' }elseif($d -le 90){ 'Behind' }else{ 'Overdue' } }
function Get-IABBPatchStatusFill { param([string]$Status) switch($Status){ 'Current'{'D4EDDA'} 'Behind'{'FFF3CD'} 'Overdue'{'F8D7DA'} default{''} } }
function Get-IABBPatchSummaryRows {
    param($Server)
    $p=$Server.PatchSummary
    if(-not $p){ return @(,@('Patch data','Not collected','')) }
    $st=Get-IABBPatchStatus $p.DaysSinceLastPatch
    @(
        @('Total Hotfixes',$p.HotfixCount,''),
        @('Latest Patch Date',$(if($p.LatestPatchDate){$p.LatestPatchDate}else{'Unknown'}),''),
        @('Latest KB(s)',$(if($p.LatestKB){$p.LatestKB}else{'Unknown'}),''),
        @('Days Since Last Patch',$(if($null -ne $p.DaysSinceLastPatch){$p.DaysSinceLastPatch}else{'Unknown'}),''),
        @('Patch Status',$st,(Get-IABBPatchStatusFill $st)),
        @('Hotfixes in Last 90 Days',$p.HotfixesLast90Days,''),
        @('Hotfixes Without Install Date',$p.UndatedCount,'')
    )
}
function Get-IABBDaysSinceLastPatchText { param($Server) $p=$Server.PatchSummary; if($p -and $null -ne $p.DaysSinceLastPatch){ return ([string]$p.DaysSinceLastPatch+' ('+(Get-IABBPatchStatus $p.DaysSinceLastPatch)+')') }; 'Unknown' }
function Get-IABBSqlProductName { param([string]$Build) if([string]::IsNullOrWhiteSpace($Build)){ return 'Unknown' }; $parts=$Build -split '\.'; $maj=0;$min=0; [void][int]::TryParse($parts[0],[ref]$maj); if($parts.Count -gt 1){ [void][int]::TryParse($parts[1],[ref]$min) }; switch($maj){ 17{'SQL Server 2025'} 16{'SQL Server 2022'} 15{'SQL Server 2019'} 14{'SQL Server 2017'} 13{'SQL Server 2016'} 12{'SQL Server 2014'} 11{'SQL Server 2012'} 10{ if($min -ge 50){'SQL Server 2008 R2'}else{'SQL Server 2008'} } 9{'SQL Server 2005'} default{ 'SQL Server ('+$Build+')' } } }
function Get-IABBSqlBuild { param($Instance) if(-not [string]::IsNullOrWhiteSpace([string]$Instance.PatchLevel)){ return [string]$Instance.PatchLevel }; [string]$Instance.Version }
function Format-IABBSqlPort { param([AllowNull()][string]$Port) $p=([string]$Port).Trim(); if($p -eq 'Dynamic (0)'){ return 'Dynamic' }; $p }
function Get-IABBServiceByName { param($Server,[string]$Name) @($Server.Services|?{ ([string]$_.Name) -eq $Name })|select -First 1 }
function Get-IABBServiceStateText { param($Server,[string]$Name) $svc=Get-IABBServiceByName $Server $Name; if(-not $svc){ return 'Not installed' }; $mode=[string]$svc.StartMode; if($mode -eq 'Disabled'){ return 'Disabled' }; ([string]$svc.State+' / '+$mode) }
function Get-IABBServiceAccount { param($Server,[string]$Name) $svc=Get-IABBServiceByName $Server $Name; if($svc){ [string]$svc.StartName }else{ '' } }
function Get-IABBRoleServiceMap { @{ 'Domain Controller'=@('NTDS','ADWS','Netlogon','Kdc','IsmServ','DFSR','NtFrs','Dfs','W32Time','Dnscache','LanmanServer','SamSs');'DNS Server'=@('DNS');'DHCP Server'=@('DHCPServer');'AD CS Server'=@('CertSvc','CertPropSvc');'IIS Server'=@('W3SVC','WAS','IISADMIN','AppHostSvc');'SQL Server'=@('MSSQLSERVER','MSSQL$*','SQLSERVERAGENT','SQLAgent$*','SQLBrowser','SQLWriter','MSSQLFDLauncher*','MsDtsServer*','SSISTELEMETRY*','SQLTELEMETRY*','ReportServer*','SQLServerReportingServices','PowerBIReportServer','MSSQLServerOLAPService','MSOLAP$*');'Hyper-V Host'=@('vmms','vmcompute');'Hyper-V Host (nested)'=@('vmms','vmcompute');'File Server'=@('LanmanServer');'Print Server'=@('Spooler');'DFS Namespace Server'=@('Dfs');'DFS Replication Server'=@('DFSR');'Remote Desktop Session Host'=@('TermService','SessionEnv','UmRdpService','Tssdis','TermServLicensing','RDMS','TScPubRPC') } }
function Get-IABBRoleServiceRows {
    param($Server)
    $map=Get-IABBRoleServiceMap
    $rows=@(); $seen=@{}
    foreach($role in @(Get-IABBDisplayRoles $Server)){
        if(-not $map.ContainsKey($role)){ continue }
        foreach($pat in $map[$role]){
            $hits=@($Server.Services|?{ ([string]$_.Name) -like $pat -and ([string]$_.Name) -notlike '*MICROSOFT##*' }|sort Name)
            foreach($svc in $hits){
                $dk="$role|"+([string]$svc.Name).ToLowerInvariant(); if($seen.ContainsKey($dk)){continue}; $seen[$dk]=$true
                $state=[string]$svc.State; $startMode=[string]$svc.StartMode
                $status=if($startMode -eq 'Disabled'){'Disabled'}elseif($state -eq 'Running'){'Running'}elseif($state -eq 'Stopped'){'Stopped'}else{$state}
                $rows+=,@($role,$svc.Name,$svc.DisplayName,$status,$startMode)
            }
        }
    }
    $rows
}
function Get-IABBExecutiveDashboardRows { param($Server) $st=Get-IABBStorageSummary $Server; @(@('Server Name',$Server.ComputerName),@('Collection Status',$(if($Server.CollectionStatus){$Server.CollectionStatus.Status}else{'Unknown'})),@('Server Type',(Get-IABBGuestType $Server)),@('Operating System',$Server.OperatingSystem.Caption),@('Roles Count',@(Get-IABBDisplayRoles $Server).Count),@('Memory GB',$Server.Hardware.MemoryGB),@('Total Storage GB',$st.TotalGB),@('IPv4 Address',(Get-IABBIPv4String $Server)),@('Applications Installed',(Get-IABBAppCountText $Server)),@('Days Since Last Patch',(Get-IABBDaysSinceLastPatchText $Server))) }
function New-IABBHtmlSummary { param([array]$ServerResults,[string]$OutputPath,[string]$ClientName)
    $rows=''
    foreach($s in @($ServerResults)){ $st=$s.CollectionStatus; $status=if($st){$st.Status}else{'Unknown'}; $color=switch($status){'Complete'{'#137333'}'Partial'{'#b06000'}'Failed'{'#b00020'}default{'#555'}}; $issues=@($s.SectionTimings|?{ $_ -and $_.Result -ne 'OK' }|%{ [string]$_.Section+': '+[string]$_.Result }) -join '; '; $err=if($s.Error){[string]$s.Error}else{$issues}; if($s.PSObject.Properties['ReportError'] -and $s.ReportError){ $err=(@($err,('DOCX generation failed: '+[string]$s.ReportError))|?{ $_ }) -join '; ' }; $rows+=('<tr><td>'+(ConvertTo-IABBHtmlText $s.ComputerName)+'</td><td style="color:'+$color+';font-weight:bold">'+$status+'</td><td>'+(ConvertTo-IABBHtmlText $st.Transport)+'</td><td>'+(ConvertTo-IABBHtmlText $st.Classification)+'</td><td>'+(ConvertTo-IABBHtmlText $err)+'</td></tr>') }
    Save-IABBTextFile -Path $OutputPath -Content ('<html><head><meta charset="utf-8"><style>body{font-family:Segoe UI}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:6px}</style></head><body><h1>Server As-Built Documentation</h1><div>Client: '+(ConvertTo-IABBHtmlText $ClientName)+'<br>Generated By: Server Documentation Tool v1.0.3</div><h2>Run Summary</h2><table><tr><th>Server</th><th>Status</th><th>Transport</th><th>Classification</th><th>Error / Issues</th></tr>'+$rows+'</table></body></html>') }
function New-IABBOpenXmlDocx {
    param([array]$ServerResults,[string]$OutputPath,[string]$ClientName,[string]$TemplatePath,[int]$GroupMemberDisplayCap=15,[int]$ApplicationDisplayCap=200)
    $script:IABBGroupCap = $GroupMemberDisplayCap
    $script:IABBAppCap = $(if($ApplicationDisplayCap -gt 0){$ApplicationDisplayCap}else{200})
    Add-Type -AssemblyName System.IO.Compression; Add-Type -AssemblyName System.IO.Compression.FileSystem
    $UsableTwips = 9026
    function P([string]$Text,[string]$Style){ if($Style){ $pPr='<w:pPr><w:pStyle w:val="'+$Style+'"/><w:keepNext/><w:keepLines/><w:spacing w:before="160" w:after="100"/></w:pPr>' } else { $pPr='<w:pPr><w:spacing w:after="80"/></w:pPr>' }; '<w:p>'+$pPr+'<w:r><w:t xml:space="preserve">'+(ConvertTo-IABBXmlText $Text)+'</w:t></w:r></w:p>' }
    function Banner([string]$Text,[string]$Fill){ '<w:p><w:pPr><w:keepNext/><w:shd w:fill="'+$Fill+'"/><w:spacing w:before="120" w:after="120"/></w:pPr><w:r><w:rPr><w:b/><w:color w:val="FFFFFF"/><w:sz w:val="26"/></w:rPr><w:t xml:space="preserve">'+(ConvertTo-IABBXmlText $Text)+'</w:t></w:r></w:p>' }
    function TC([string]$Text,[int]$Wt,[bool]$H,[string]$Fill){$sh=if($H){'<w:shd w:fill="0F172A"/>'}elseif($Fill){'<w:shd w:fill="'+$Fill+'"/>'}else{''};$rp=if($H){'<w:rPr><w:b/><w:color w:val="FFFFFF"/></w:rPr>'}else{''};'<w:tc><w:tcPr><w:tcW w:w="'+$Wt+'" w:type="dxa"/>'+$sh+'</w:tcPr><w:p><w:pPr><w:spacing w:after="20"/></w:pPr><w:r>'+$rp+'<w:t xml:space="preserve">'+(ConvertTo-IABBXmlText $Text)+'</w:t></w:r></w:p></w:tc>'}
    function CW([int]$Count,[int[]]$Wts){ $w=@(); if($Wts -and $Wts.Count -eq $Count){ $sum=0; foreach($x in $Wts){$sum+=$x}; $acc=0; for($i=0;$i -lt $Count;$i++){ if($i -eq $Count-1){$w+=($UsableTwips-$acc)}else{$cw=[math]::Floor($UsableTwips*($Wts[$i]/$sum));$w+=$cw;$acc+=$cw} } } else { $b=[math]::Floor($UsableTwips/$Count); for($i=0;$i -lt $Count;$i++){ if($i -eq $Count-1){$w+=($UsableTwips-$b*($Count-1))}else{$w+=$b} } }; $w }
    function TblHead([int[]]$wd,[string[]]$Hd){ $g='<w:tblGrid>'; foreach($cw in $wd){$g+='<w:gridCol w:w="'+$cw+'"/>'}; $g+='</w:tblGrid>'; $x='<w:tbl><w:tblPr><w:tblW w:w="'+$UsableTwips+'" w:type="dxa"/><w:tblLayout w:type="fixed"/><w:tblBorders><w:top w:val="single" w:sz="4"/><w:left w:val="single" w:sz="4"/><w:bottom w:val="single" w:sz="4"/><w:right w:val="single" w:sz="4"/><w:insideH w:val="single" w:sz="4"/><w:insideV w:val="single" w:sz="4"/></w:tblBorders></w:tblPr>'+$g; $x+='<w:tr><w:trPr><w:tblHeader/><w:cantSplit/></w:trPr>'; for($j=0;$j -lt $Hd.Count;$j++){$x+=TC ([string]$Hd[$j]) $wd[$j] $true ''}; $x+'</w:tr>' }
    function Tbl([string[]]$Hd,[object[]]$R,[int[]]$Wts){
        $hc=@($Hd).Count; if($hc -eq 0){return ''}
        $flat=@(New-IABBRowsFlat $R); $wd=CW $hc $Wts
        $x=TblHead $wd $Hd
        if($flat.Count -gt 0 -and ($flat.Count % $hc -eq 0)){ for($i=0;$i -lt $flat.Count;$i+=$hc){ $x+='<w:tr><w:trPr><w:cantSplit/></w:trPr>'; for($j=0;$j -lt $hc;$j++){$x+=TC ([string]$flat[$i+$j]) $wd[$j] $false ''}; $x+='</w:tr>' } }
        elseif($flat.Count -gt 0){ $x+=MismatchRow $wd $flat.Count $hc }
        $x+'</w:tbl>'
    }
    function TblFill([string[]]$Hd,[object[]]$R,[int[]]$Wts,[int]$ShadeFrom){
        $hc=@($Hd).Count; $rc=$hc+1
        $flat=@(New-IABBRowsFlat $R); $wd=CW $hc $Wts
        $x=TblHead $wd $Hd
        if($flat.Count -gt 0 -and ($flat.Count % $rc -eq 0)){ for($i=0;$i -lt $flat.Count;$i+=$rc){ $fill=[string]$flat[$i+$hc]; $x+='<w:tr><w:trPr><w:cantSplit/></w:trPr>'; for($j=0;$j -lt $hc;$j++){ $cf=if($j -ge $ShadeFrom){$fill}else{''}; $x+=TC ([string]$flat[$i+$j]) $wd[$j] $false $cf }; $x+='</w:tr>' } }
        elseif($flat.Count -gt 0){ $x+=MismatchRow $wd $flat.Count $rc }
        $x+'</w:tbl>'
    }
    # v0.9.9h: rows that do not fit the column grid are no longer dropped silently - a visible warning row is written instead
    function MismatchRow([int[]]$wd,[int]$Cells,[int]$Cols){ $msg='Table data could not be laid out ('+$Cells+' values for '+$Cols+' columns). The full data is in the RawData JSON for this server.'; $y='<w:tr><w:trPr><w:cantSplit/></w:trPr>'; for($j=0;$j -lt $wd.Count;$j++){ $y+=TC $(if($j -eq 0){$msg}else{''}) $wd[$j] $false 'FFF3CD' }; $y+'</w:tr>' }
    function Gap(){'<w:p><w:pPr><w:spacing w:after="160"/></w:pPr></w:p>'}
    # v0.9.9h: section helpers - a failed / empty collection step shows "Not collected" instead of a header-only table
    function StepRes([string]$Name){ $tm=@($s.SectionTimings|?{ $_ -and [string]$_.Section -eq $Name })|Select-Object -First 1; if($tm){ [string]$tm.Result }else{ $null } }
    function NotCol([string]$Name){ $res=StepRes $Name; if($res -and $res -ne 'OK'){ ('Not collected: the "'+$Name+'" collection step result was '+$res+'. See Collection Warnings.') }elseif(-not $res){ ('Not collected: the "'+$Name+'" collection step did not run. See Collection Warnings.') }else{ $null } }
    function SecTbl([string]$Step,[string[]]$Hd,[object[]]$R,[int[]]$Wts,[string]$EmptyText){
        $rows=@($R); $nc=NotCol $Step
        if($rows.Count -eq 0){ if($nc){ return (P $nc $null) }; return (P $EmptyText $null) }
        $y=''; if($nc){ $y+=P ('Partial data: the "'+$Step+'" collection step result was '+(StepRes $Step)+'. See Collection Warnings.') $null }
        $y+(Tbl $Hd $rows $Wts)
    }
    $body=@()
    $body+=P 'Server As-Built Documentation' 'Title'
    $body+=P ('Client: '+$ClientName) $null
    $body+=P ('Generated: '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) $null
    $body+=P 'Generated By: Server Documentation Tool v1.0.3' $null
    $body+=P 'Document Version: 1.0.3' $null
    foreach($s in $ServerResults){
        $body+=P ('Server Details - '+$s.ComputerName) 'Heading1'
        $st=$s.CollectionStatus; $statusText=if($st){$st.Status}else{'Unknown'}
        $fill=switch($statusText){'Complete'{'137333'}'Partial'{'B06000'}'Failed'{'B00020'}default{'555555'}}
        if($statusText -eq 'Failed'){ $body+=Banner ("Collection Status: Failed - no inventory data was collected. See details below.") $fill }
        elseif($statusText -ne 'Complete'){ $body+=Banner ("Collection Status: "+$statusText+" - some data could not be collected. See details below.") $fill }
        else { $body+=Banner ("Collection Status: Complete") $fill }
        $body+=P 'Collection Status' 'Heading2'; $body+=Tbl @('Item','Value') @(Get-IABBCollectionStatusRows $s) @(30,70)
        $warns=@($s.CollectionWarnings|?{ -not [string]::IsNullOrWhiteSpace([string]$_) })
        if($warns.Count -gt 0){ $body+=P 'Collection Warnings' 'Heading2'; $body+=Tbl @('Warning') @($warns|%{,@([string]$_)}) @(100) }
        $timing=@(Get-IABBTimingRows $s)
        if($timing.Count -gt 0){ $body+=P 'Collection Timings' 'Heading2'; $body+=TblFill @('Step','Seconds','Result') $timing @(60,20,20) 1 }
        if($statusText -eq 'Failed'){
            $body+=P 'No inventory data was collected for this server, so the inventory sections are omitted. Resolve the issue shown under Classification and Error, then run the assessment again.' $null
            if([string]$st.Classification -like 'Authentication failed*'){ $body+=P 'Tip: tick "Use alternate credentials" and supply an administrator account from the target server''s domain (for example DOMAIN\adminuser).' $null }
            $body+=Gap
            continue
        }
        $body+=Gap
        $body+=P 'Executive Dashboard' 'Heading2'; $body+=Tbl @('Metric','Value') @(Get-IABBExecutiveDashboardRows $s) @(35,65); $body+=Gap
        $body+=P 'Executive Server Facts' 'Heading2'; $body+=Tbl @('Item','Value') @(@('Operating System',$s.OperatingSystem.Caption),@('Memory GB',$s.Hardware.MemoryGB),@('CPU',$s.Hardware.ProcessorModel),@('Total Storage GB',(Get-IABBStorageSummary $s).TotalGB),@('IPv4 Address',(Get-IABBIPv4String $s)),@('Roles',(@(Get-IABBDisplayRoles $s)-join ', '))) @(35,65); $body+=Gap
        $body+=P 'Platform Classification' 'Heading2'; $body+=Tbl @('Item','Value') @(@('Platform Type',$s.Virtualization.PlatformType),@('Host Platform',$s.Virtualization.HostPlatform),@('Guest Type',(Get-IABBGuestType $s)),@('Virtualization Platform',(Get-IABBVirtualizationPlatform $s)),@('Hyper-V Host',$(if($s.Virtualization.IsHyperVHost){'Yes'}elseif(@($s.RolesDetected) -contains 'Hyper-V Host (nested)'){'Nested (virtual machine running Hyper-V)'}else{'No'})),@('Manufacturer',$s.Virtualization.Manufacturer),@('Model',$s.Virtualization.Model),@('Serial Number',$s.Overview.SerialNumber)) @(35,65); $body+=Gap
        $body+=P 'Operating System' 'Heading2'; if($null -eq $s.OperatingSystem){ $body+=P (NotCol 'Time zone/OS') $null } else { $body+=Tbl @('Item','Value') @(@('OS',$s.OperatingSystem.Caption),@('Version',$s.OperatingSystem.Version),@('Build',$s.OperatingSystem.BuildNumber),@('Architecture',$s.OperatingSystem.Architecture),@('Install Date',$s.OperatingSystem.InstallDate),@('Last Boot',$s.OperatingSystem.LastBootTime),@('Uptime Days',$s.OperatingSystem.UptimeDays),@('Timezone',$s.OperatingSystem.TimeZone)) @(35,65) }; $body+=Gap
        $body+=P 'Processors' 'Heading2'; $body+=SecTbl 'Processors/Memory' @('Socket','Name','Cores','Logical CPUs','Max MHz') @($s.ProcessorDetails|?{ $_ }|%{,@($_.SocketDesignation,$_.Name,$_.Cores,$_.LogicalProcessors,$_.MaxClockSpeedMHz)}) @(12,52,12,12,12) 'No processor information returned.'; $body+=Gap
        $body+=P 'Storage Summary' 'Heading2'; $stRows=@(); if(@($s.VolumeDetails|?{ $_ }).Count -gt 0){ $stRows=@(Get-IABBStorageTotalsRows $s) }; $body+=SecTbl 'Disks/Volumes' @('Item','Value') $stRows @(40,60) 'No volumes returned.'
        $body+=P 'Physical Disks' 'Heading2'; $body+=SecTbl 'Disks/Volumes' @('Index','Model','Disk Type','Interface','Media','Size GB','Partitions') @($s.PhysicalDisks|?{ $_ }|%{,@($_.Index,$_.Model,$_.DiskClass,$_.InterfaceType,$_.MediaType,$_.SizeGB,$_.Partitions)}) @(9,25,15,12,20,10,9) 'No physical disks returned.'
        if(@($s.DiskMap).Count -gt 0){ $body+=P 'Disk to Volume Mapping' 'Heading2'; $body+=Tbl @('Physical Disk','Partition','Logical Disk') @($s.DiskMap|%{,@($_.PhysicalDisk,$_.Partition,$_.LogicalDisk)}) @(40,40,20); $body+=Gap }
        if(@($s.Partitions).Count -gt 0){ $body+=P 'Partitions' 'Heading2'; $body+=Tbl @('Disk','Partition','Type','Size GB','Bootable','Boot Partition','Primary','Starting Offset') @($s.Partitions|%{,@($_.DiskIndex,$_.Name,$_.Type,$_.SizeGB,$_.Bootable,$_.BootPartition,$_.Primary,$_.StartingOffset)}) @(8,20,24,12,10,10,8,8); $body+=Gap }
        $body+=P 'Volumes' 'Heading2'; $body+=SecTbl 'Disks/Volumes' @('Mount Path','Label','Type','File System','Capacity GB','Free GB','Free %','Health') @(Get-IABBDataVolumes $s|%{,@($_.MountPath,$_.Label,$_.VolumeType,$_.FileSystem,$_.CapacityGB,$_.FreeGB,$_.FreePct,$_.HealthFlag)}) @(18,16,16,12,11,10,9,8) 'No data volumes returned.'; $body+=Gap
        $body+=P 'Network Adapters' 'Heading2'; $body+=SecTbl 'Network' @('Name','Description','Speed Mbps','MAC','IPv4 Address','Gateway','DNS Servers','DHCP') @($s.NetworkAdapters|?{ $_ }|%{,@($_.Name,$_.Description,$_.SpeedMbps,$_.MACAddress,$_.IPAddress,$_.Gateway,$_.DNSServers,$_.DHCPEnabled)}) @(12,22,10,15,13,10,12,6) 'No IP-enabled network adapters returned.'; $body+=Gap
        if(@($s.NetworkAdapterDetail).Count -gt 0){ $body+=P 'Network Adapter Detail' 'Heading2'; foreach($n in @($s.NetworkAdapterDetail)){ $body+=Tbl @('Setting','Value') @(@('Adapter',$n.Name),@('Status',$n.Status),@('IPv4 Address',$n.IPv4Address),@('Subnet Mask',$n.SubnetMask),@('Default Gateway',$n.DefaultGateway),@('DNS Servers',$n.DNSServers),@('Connection-specific Suffix',$n.ConnSpecificSuffix),@('DNS Suffix Search Order',$n.DNSDomainSuffixSearchOrder),@('DHCP Enabled',$n.DHCPEnabled),@('DHCP Server',$n.DHCPServer),@('DHCP Lease Obtained',$n.DHCPLeaseObtained),@('DHCP Lease Expires',$n.DHCPLeaseExpires),@('WINS Servers',$n.WINSServers),@('NetBIOS over TCP/IP',$n.NetBIOSSetting)) @(35,65); $body+=Gap } }
        if(@($s.RolesFeatures).Count -gt 0){ $body+=P 'Installed Roles and Features' 'Heading2'; $body+=Tbl @('Name','Display Name','Type') @($s.RolesFeatures|%{,@($_.Name,$_.DisplayName,$_.FeatureType)}) @(30,50,20); $body+=Gap }
        $body+=P 'Role Services' 'Heading2'
        $roleSvcRows=@(Get-IABBRoleServiceRows $s)
        if($roleSvcRows.Count -gt 0){ $body+=Tbl @('Role','Service Name','Display Name','State','Start Mode') $roleSvcRows @(20,18,32,15,15) } elseif(NotCol 'Services/Hotfixes/Shares'){ $body+=P (NotCol 'Services/Hotfixes/Shares') $null } else { $body+=P 'No role-specific services detected for this server.' $null }
        foreach($rn in @($s.RoleNotes|?{ -not [string]::IsNullOrWhiteSpace([string]$_) })){ $body+=P ('Note: '+[string]$rn) $null }
        $body+=Gap
        $body+=P 'Application Summary' 'Heading2'
        if((Test-IABBCollected $s.Applications) -and @($s.Applications.Applications|?{ $_ }).Count -gt 0){
            $apps=@($s.Applications.Applications|?{ $_ }); $appCap=$script:IABBAppCap; $shownApps=@($apps|select -First $appCap)
            $shownText=if($apps.Count -gt $appCap){ ([string]$appCap+' of '+[string]$apps.Count) }else{ ('All ('+[string]$apps.Count+')') }
            $body+=Tbl @('Item','Count') @(@('Total Applications (Programs and Features)',$s.Applications.Summary.TotalApplications),@('Unique Publishers',$s.Applications.Summary.Publishers),@('Shown in This Report',$shownText),@('System Components / Updates (not listed)',$(if($null -ne $s.Applications.Summary.HiddenEntries){$s.Applications.Summary.HiddenEntries}else{0}))) @(50,50)
            $body+=P 'Installed Applications' 'Heading2'; $body+=Tbl @('Name','Publisher','Version','Install Date','Architecture') @($shownApps|%{,@($_.Name,$_.Publisher,$_.Version,$_.InstallDate,$_.Architecture)}) @(40,24,14,12,10)
            if($apps.Count -gt $appCap){ $body+=P ('Showing the first '+$appCap+' of '+$apps.Count+' applications (alphabetical). The full list is in the RawData JSON for this server.') $null }
        } elseif((Test-IABBCollected $s.Applications) -and @($s.Applications.HiddenEntries|?{ $_ }).Count -gt 0){ $body+=P ('No applications are listed in Programs and Features. '+@($s.Applications.HiddenEntries|?{ $_ }).Count+' system components / updates are not listed; see the RawData JSON.') $null
        } elseif(Test-IABBCollected $s.Applications){ $body+=P 'Not collected: the applications query returned no data. See Collection Warnings.' $null } else { $body+=P (Get-IABBNotCollectedText $s.Applications) $null }
        $body+=Gap
        $body+=P 'Patch Summary' 'Heading2'; $body+=TblFill @('Item','Value') @(Get-IABBPatchSummaryRows $s) @(45,55) 1
        $body+=P 'Patch Status: Current = last hotfix installed within 35 days; Behind = 36-90 days; Overdue = more than 90 days. Based on Windows hotfix install dates (Win32_QuickFixEngineering); application updates such as SQL Server CUs are not included.' $null
        $body+=P 'Hotfixes' 'Heading2'; $body+=SecTbl 'Services/Hotfixes/Shares' @('KB','Description','Installed On','Installed By') @($s.Hotfixes|?{ $_ }|%{,@($_.HotFixID,$_.Description,$(if($_.InstalledOn){$_.InstalledOn}else{$_.InstalledOnRaw}),$_.InstalledBy)}) @(16,24,22,38) 'No hotfixes recorded by Windows (Win32_QuickFixEngineering).'; $body+=Gap
        $body+=P 'Shares' 'Heading2'; $body+=SecTbl 'Services/Hotfixes/Shares' @('Name','Path','Description','Type') @($s.Shares|?{ $_ }|%{,@($_.Name,$_.Path,$_.Description,(Convert-IABBShareType $_.Type))}) @(16,34,28,22) 'No shares returned.'; $body+=Gap
        $acctNote=[string]$s.LocalAccountsNote
        $extrasFailed=($acctNote -like 'Not collected*')
        $body+=P 'Printers' 'Heading2'; if(@($s.Printers).Count -gt 0){ $body+=Tbl @('Name','Driver','Port','Shared','Share Name','Location','Default') @($s.Printers|%{,@($_.Name,$_.DriverName,$_.PortName,$_.Shared,$_.ShareName,$_.Location,$_.Default)}) @(22,22,14,9,12,12,9) } elseif($extrasFailed){ $body+=P $acctNote $null } elseif(-not [string]::IsNullOrWhiteSpace([string]$s.PrintersNote)){ $body+=P ([string]$s.PrintersNote) $null } else { $body+=P 'No printers installed.' $null }
        $body+=Gap; $body+=P 'Local Users' 'Heading2'
        if(@($s.LocalUsers).Count -gt 0){ $body+=Tbl @('Name','Full Name','Disabled','Locked','Password Expires','Password Required','SID') @($s.LocalUsers|%{,@($_.Name,$_.FullName,$_.Disabled,$_.Lockout,$_.PasswordExpires,$_.PasswordRequired,$_.SID)}) @(14,16,9,9,13,13,26) }
        elseif(-not [string]::IsNullOrWhiteSpace($acctNote)){ $body+=P $acctNote $null }
        else { $body+=P 'No local user accounts collected.' $null }
        $body+=Gap
        $largeGroups=@(Get-IABBLargeGroups $s)
        if(@($largeGroups).Count -gt 0){ $body+=P 'Large Groups (membership exceeds display cap)' 'Heading2'; $body+=Tbl @('Group','Member Count') @($largeGroups|%{,@($_.Name,$_.MemberCount)}) @(70,30); $body+=Gap }
        $groupHeading=if($s.RoleDetection -and $s.RoleDetection.IsDomainController){'Built-in Groups (Domain Controller)'}else{'Local Groups'}
        $body+=P $groupHeading 'Heading2'; if(@($s.LocalGroups).Count -gt 0){ $body+=Tbl @('Group','Description','Members','Members List (capped)') @(Get-IABBLocalGroupRows $s) @(20,30,10,40) } elseif($extrasFailed){ $body+=P $acctNote $null } else { $body+=P 'No local groups collected.' $null }
        # v0.9.9g: page file / environment variables say "Not collected" when the step failed
        $body+=Gap; $body+=P 'Page File' 'Heading2'; if(@($s.PageFiles).Count -gt 0){ $body+=Tbl @('Name','Allocated MB','Current Usage MB','Peak Usage MB') @($s.PageFiles|%{,@($_.Name,$_.AllocatedBaseSizeMB,$_.CurrentUsageMB,$_.PeakUsageMB)}) @(40,20,20,20) } elseif($extrasFailed){ $body+=P $acctNote $null } else { $body+=P 'No page file information collected.' $null }
        $body+=Gap; $body+=P 'Environment Variables (System)' 'Heading2'; if(@($s.EnvironmentVariables).Count -gt 0){ $body+=Tbl @('Name','Value') @($s.EnvironmentVariables|%{,@($_.Name,$_.Value)}) @(24,76) } elseif($extrasFailed){ $body+=P $acctNote $null } else { $body+=P 'No system environment variables collected.' $null }
        $body+=Gap; $body+=P 'Certificate Summary' 'Heading2'
        if((Test-IABBCollected $s.Certificates) -and @($s.Certificates.Certificates|?{ $_ }).Count -gt 0){
            $body+=Tbl @('Item','Count') @(Get-IABBCertificateSummaryRows $s.Certificates) @(60,40)
            $body+=P 'Certificates' 'Heading2'; $certRows=@(Get-IABBCertificateRows $s.Certificates)
            if($certRows.Count -gt 0){ $body+=TblFill @('Certificate Name','Store(s)','Days Until Expiry','Status') $certRows @(44,16,14,26) 2 } else { $body+=P 'No certificates in the Personal or Web Hosting stores, and no Root/CA certificates expiring within 60 days.' $null }
            $body+=P 'Listed: certificates in the server''s Personal (My) and Web Hosting stores, plus Root/CA certificates expiring within 60 days. Other built-in Root/CA certificates are counted in the summary and kept in the RawData JSON. Red EXPIRED applies to Personal/Web Hosting certificates that require action.' $null
        } elseif(Test-IABBCollected $s.Certificates){ $body+=P 'Not collected: the certificate query returned no data. See Collection Warnings.' $null } else { $body+=P (Get-IABBNotCollectedText $s.Certificates) $null }
        $body+=Gap
        if($s.RoleData.Contains('ADDS')){$adds=$s.RoleData['ADDS'];$body+=P 'Active Directory Domain Services' 'Heading1'; if(Test-IABBCollected $adds){$body+=Tbl @('Metric','Value') @(@('Forest Name',$adds.Forest.Name),@('Domain Name',$adds.Domain.DNSRoot),@('Forest Functional Level',(ConvertTo-IABBReadableMode $adds.Forest.ForestMode)),@('Domain Functional Level',(ConvertTo-IABBReadableMode $adds.Domain.DomainMode)),@('Domain Controllers',@($adds.DomainControllers).Count),@('Global Catalogs',@($adds.DomainControllers|?{$_.IsGlobalCatalog -eq $true}).Count),@('FSMO Role Holders',(Get-IABBFSMOHolderSummary $adds.FSMORoles)),@('Collected Via',$(if($adds.Method){[string]$adds.Method}else{'Not recorded'}))) @(35,65)}else{$body+=P (Get-IABBNotCollectedText $adds) $null};$body+=Gap}
        if($s.RoleData.Contains('DNS')){$dns=$s.RoleData['DNS'];$body+=P 'DNS Infrastructure' 'Heading1'; if(Test-IABBCollected $dns){$zones=@($dns.Zones);$body+=Tbl @('DNS Item','Value') @(@('Total Zones',$zones.Count),@('Reverse Lookup Zones',@($zones|?{$_.IsReverseLookupZone -eq $true}).Count),@('AD-Integrated Zones',@($zones|?{$_.IsDsIntegrated -eq $true}).Count),@('DNS Forwarders',(Format-IABBDNSForwarders $dns.Forwarders)),@('Collected Via',$(if($dns.Method){[string]$dns.Method}else{'Not recorded'}))) @(35,65)}else{$body+=P (Get-IABBNotCollectedText $dns) $null};$body+=Gap}
        if($s.RoleData.Contains('DHCP')){$dhcp=$s.RoleData['DHCP'];$body+=P 'DHCP Services' 'Heading1'; if(Test-IABBCollected $dhcp){ if($dhcp.Method){ $body+=P ('Collected via: '+[string]$dhcp.Method) $null }; if(@($dhcp.Scopes|?{ $_ }).Count -gt 0){ $body+=Tbl @('Scope ID','Name','Start Range','End Range','State') @($dhcp.Scopes|?{ $_ }|%{,@($_.ScopeId,$_.Name,$_.StartRange,$_.EndRange,$_.State)}) @(18,28,18,18,18) } else { $body+=P 'No IPv4 DHCP scopes are configured on this server.' $null } }else{$body+=P (Get-IABBNotCollectedText $dhcp) $null};$body+=Gap}
        if($s.RoleData.Contains('IIS')){$iis=$s.RoleData['IIS'];$body+=P 'Internet Information Services (IIS)' 'Heading1'; if(Test-IABBCollected $iis){$body+=Tbl @('Item','Value') @(@('IIS Version',$iis.Version),@('Sites',@($iis.Sites).Count),@('Application Pools',@($iis.AppPools).Count),@('Bindings',@($iis.Bindings).Count)) @(35,65);if(@($iis.Sites).Count -gt 0){$body+=P 'IIS Sites' 'Heading2';$body+=Tbl @('Name','ID','State','Physical Path','App Pool') @($iis.Sites|%{,@($_.Name,$_.ID,$_.State,$_.PhysicalPath,$_.AppPool)}) @(20,8,12,40,20)};if(@($iis.Bindings).Count -gt 0){$body+=P 'IIS Bindings' 'Heading2';$body+=Tbl @('Site','Protocol','Binding Information','Hostname','SSL Flags') @($iis.Bindings|%{,@($_.Site,$_.Protocol,$_.BindingInformation,$_.Hostname,$_.SslFlags)}) @(22,12,34,22,10)};if(@($iis.AppPools).Count -gt 0){$body+=P 'IIS Application Pools' 'Heading2';$body+=Tbl @('Name','State','Runtime','Pipeline','Identity') @($iis.AppPools|%{,@($_.Name,$_.State,$_.ManagedRuntimeVersion,$_.ManagedPipelineMode,$_.Identity)}) @(30,12,16,16,26)};if(@($iis.VirtualDirectories).Count -gt 0){$body+=P 'IIS Applications / Virtual Directories' 'Heading2';$body+=Tbl @('Site','Path','Physical Path','App Pool') @($iis.VirtualDirectories|%{,@($_.Site,$_.Path,$_.PhysicalPath,$_.AppPool)}) @(20,20,40,20)}}else{$body+=P (Get-IABBNotCollectedText $iis) $null};$body+=Gap}
        if($s.RoleData.Contains('SQL')){
            $sql=$s.RoleData['SQL']; $insts=@($sql.Instances|?{ $_ })
            $body+=P 'SQL Server' 'Heading1'
            if($insts.Count -gt 0){
                $body+=P 'SQL Server Instances' 'Heading2'
                $body+=Tbl @('Instance','Product','Build','Edition','Architecture','TCP Port') @($insts|%{ $b=Get-IABBSqlBuild $_; ,@($_.Instance,$(if($b){Get-IABBSqlProductName $b}else{'Unknown'}),$b,$_.Edition,$_.Architecture,(Format-IABBSqlPort $_.TcpPort)) }) @(16,16,14,28,12,14)
                $body+=P 'SQL Server Instance Services' 'Heading2'
                $body+=Tbl @('Instance','Data Root','Engine Service','Agent Service','Engine Account') @($insts|%{ ,@($_.Instance,$_.DataRoot,(Get-IABBServiceStateText $s ([string]$_.ServiceName)),(Get-IABBServiceStateText $s ([string]$_.AgentServiceName)),(Get-IABBServiceAccount $s ([string]$_.ServiceName))) }) @(14,30,18,18,20)
            } else {
                $body+=P (Get-IABBNotCollectedText $sql) $null
            }
            $body+=Gap
        }
    }
    $documentXml='<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>'+($body -join "`n")+'<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440"/></w:sectPr></w:body></w:document>'
    $stylesXml='<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:rPr><w:rFonts w:ascii="Segoe UI" w:hAnsi="Segoe UI"/><w:sz w:val="19"/></w:rPr></w:style><w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:pPr><w:keepNext/><w:keepLines/></w:pPr><w:rPr><w:b/><w:sz w:val="40"/><w:color w:val="0F172A"/></w:rPr></w:style><w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:pPr><w:keepNext/><w:keepLines/></w:pPr><w:rPr><w:b/><w:sz w:val="32"/><w:color w:val="1E3A8A"/></w:rPr></w:style><w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:pPr><w:keepNext/><w:keepLines/></w:pPr><w:rPr><w:b/><w:sz w:val="26"/><w:color w:val="334155"/></w:rPr></w:style></w:styles>'
    $ct='<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/></Types>'
    $rels='<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
    $docRels='<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>'
    if(Test-Path $OutputPath){Remove-Item $OutputPath -Force}
    $fs=[IO.File]::Open($OutputPath,[IO.FileMode]::CreateNew)
    try{$zip=New-Object IO.Compression.ZipArchive($fs,[IO.Compression.ZipArchiveMode]::Create);try{Add-IABBZipEntry $zip '[Content_Types].xml' $ct;Add-IABBZipEntry $zip '_rels/.rels' $rels;Add-IABBZipEntry $zip 'word/document.xml' $documentXml;Add-IABBZipEntry $zip 'word/styles.xml' $stylesXml;Add-IABBZipEntry $zip 'word/_rels/document.xml.rels' $docRels}finally{$zip.Dispose()}}finally{$fs.Dispose()}
}
Export-ModuleMember -Function New-IABBHtmlSummary,New-IABBOpenXmlDocx,Test-IABBWorkstationOS
