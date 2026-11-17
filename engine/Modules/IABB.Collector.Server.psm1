# IABB.Collector.Server - Infrastructure As-Built Builder v1.0.0
function ConvertTo-IABBIPv4Collection { param([string[]]$Values) @($Values | Where-Object { [string]$_ -match '^(\d{1,3}\.){3}\d{1,3}$' }) }
function Get-IABBNetBiosOption { param($Value) switch([string]$Value){'0'{'Default (from DHCP)'}'1'{'Enabled'}'2'{'Disabled'}default{if([string]::IsNullOrWhiteSpace([string]$Value)){'Not set'}else{[string]$Value}}} }
function Get-IABBAssocDeviceId { param($Ref) if($null -eq $Ref){return ''}; try{ $v=$Ref.DeviceID; if(-not [string]::IsNullOrWhiteSpace([string]$v)){ return ([string]$v -replace '\\\\','\') } }catch{}; try{ $m=[regex]::Match([string]$Ref,'DeviceID\s*=\s*"([^"]+)"'); if($m.Success){ return ($m.Groups[1].Value -replace '\\\\','\') } }catch{}; return '' }
function Get-IABBVolumeClassification {
    param([AllowNull()][string]$DriveLetter,[AllowNull()][string]$Name,[AllowNull()][string]$Caption)
    $dl=([string]$DriveLetter).Trim()
    $path=([string]$Name).Trim(); if([string]::IsNullOrWhiteSpace($path)){ $path=([string]$Caption).Trim() }
    if(-not [string]::IsNullOrWhiteSpace($dl)){ return [pscustomobject]@{VolumeType='Mounted Volume';MountPath=$dl;IsData=$true} }
    elseif($path -match '^[A-Za-z]:\\.+'){ return [pscustomobject]@{VolumeType='Mount Point (folder)';MountPath=$path;IsData=$true} }
    else { return [pscustomobject]@{VolumeType='System/Recovery';MountPath=$path;IsData=$false} }
}
function Convert-IABBHotfixDate {
    param([AllowNull()][object]$Value)
    if($null -eq $Value){ return $null }
    if($Value -is [datetime]){ return [datetime]$Value }
    $s=([string]$Value).Trim()
    if([string]::IsNullOrWhiteSpace($s)){ return $null }
    $fmts=[string[]]@('M/d/yyyy','MM/dd/yyyy','M/d/yyyy h:mm:ss tt','M/d/yyyy HH:mm:ss','yyyy-MM-dd','yyyyMMdd')
    $d=[datetime]::MinValue
    if([datetime]::TryParseExact($s,$fmts,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$d)){ return $d }
    if($s -match '^[0-9a-fA-F]{15,16}$'){ try{ return [datetime]::FromFileTimeUtc([Convert]::ToInt64($s,16)).ToLocalTime() }catch{} }
    if([datetime]::TryParse($s,[ref]$d)){ return $d }
    return $null
}
function Get-IABBPlatformInfo {
    param([string]$Manufacturer,[string]$Model,[bool]$HasVmms,[string]$AssetTag)
    $mf=[string]$Manufacturer; $md=[string]$Model
    $platform='Physical'; $hostp='Physical Server'
    if($mf -match 'VMware' -or $md -match 'VMware'){ $platform='Virtual'; $hostp='VMware' }
    elseif($mf -match 'Microsoft' -and $md -match 'Virtual Machine'){ $platform='Virtual'; $hostp='Hyper-V'; if(([string]$AssetTag).Trim() -eq '7783-7084-3265-9085-8269-3286-77'){ $hostp='Azure' } }
    elseif($md -match 'VirtualBox' -or $mf -match 'innotek'){ $platform='Virtual'; $hostp='Oracle VirtualBox' }
    elseif($mf -match 'QEMU|Red Hat|Nutanix' -or $md -match 'KVM|QEMU|AHV'){ $platform='Virtual'; $hostp='KVM/QEMU' }
    elseif($mf -match '^Xen' -or $md -match 'HVM domU'){ $platform='Virtual'; $hostp='Xen' }
    elseif($md -match '^Virtual'){ $platform='Virtual'; $hostp='Unknown Hypervisor' }
    $isHvHost=($platform -eq 'Physical' -and $HasVmms)
    if($isHvHost){ $hostp='Physical (Hyper-V host)' }
    [pscustomobject]@{PlatformType=$platform;HostPlatform=$hostp;IsVirtual=($platform -eq 'Virtual');IsHyperVHost=$isHvHost}
}
function New-IABBCimSession {
    param([string]$ComputerName,[Management.Automation.PSCredential]$Credential,[bool]$EnableDcomFallback=$true,[int]$OperationTimeoutSec=60)
    $result=[pscustomobject]@{Session=$null;Protocol=$null;Error=$null;WsmanError=$null}
    $s=$null
    try{
        $opt=New-CimSessionOption -Protocol Wsman
        $p=@{ComputerName=$ComputerName;SessionOption=$opt;OperationTimeoutSec=$OperationTimeoutSec;ErrorAction='Stop'}; if($Credential){$p.Credential=$Credential}
        $s=New-CimSession @p
        Get-CimInstance -CimSession $s Win32_OperatingSystem -OperationTimeoutSec $OperationTimeoutSec -ErrorAction Stop | Out-Null
        $result.Session=$s; $result.Protocol='WSMan'; return $result
    }catch{ $wsErr=$_.Exception.Message; $result.WsmanError=$wsErr; if($s){ $s|Remove-CimSession -ErrorAction SilentlyContinue; $s=$null } }
    if($EnableDcomFallback){
        try{
            $opt=New-CimSessionOption -Protocol Dcom
            $p=@{ComputerName=$ComputerName;SessionOption=$opt;OperationTimeoutSec=$OperationTimeoutSec;ErrorAction='Stop'}; if($Credential){$p.Credential=$Credential}
            $s=New-CimSession @p
            Get-CimInstance -CimSession $s Win32_OperatingSystem -OperationTimeoutSec $OperationTimeoutSec -ErrorAction Stop | Out-Null
            $result.Session=$s; $result.Protocol='DCOM'; return $result
        }catch{ $dcErr=$_.Exception.Message; if($s){ $s|Remove-CimSession -ErrorAction SilentlyContinue; $s=$null }; $result.Error="WSMan failed: $wsErr | DCOM failed: $dcErr"; return $result }
    }
    $result.Error="WSMan failed: $wsErr"; return $result
}
function Test-IABBCollectionReadiness {
    param([string]$ComputerName,[Management.Automation.PSCredential]$Credential,[bool]$EnableDcomFallback=$true,[int]$OperationTimeoutSec=60)
    $r=[ordered]@{Reachable=$false;PingOk=$false;Transport='None';Authorized=$false;Classification='Unknown';Detail=$null;WsmanError=$null}
    if(Test-IABBIsLocal $ComputerName){ $r.Reachable=$true;$r.PingOk=$true;$r.Transport='Local';$r.Authorized=$true;$r.Classification='OK (Local)';return [pscustomobject]$r }
    try{ $r.PingOk=[bool](Test-Connection -ComputerName $ComputerName -Count 1 -Quiet -ErrorAction SilentlyContinue) }catch{ $r.PingOk=$false }
    $probe=New-IABBCimSession -ComputerName $ComputerName -Credential $Credential -EnableDcomFallback $EnableDcomFallback -OperationTimeoutSec $OperationTimeoutSec
    $r.WsmanError=$probe.WsmanError
    if($probe.Session){ $r.Reachable=$true;$r.Transport=$probe.Protocol;$r.Authorized=$true;$r.Classification="OK ($($probe.Protocol))"; $probe.Session|Remove-CimSession -ErrorAction SilentlyContinue }
    else {
        $r.Detail=$probe.Error; $e=[string]$probe.Error
        if(-not $r.PingOk){ $r.Classification='Unreachable (no ping)' }
        elseif($e -match '0x8009030e|0x80090322|0x8009030c|0x80090311|logon session does not exist|Kerberos|Service Principal Name|no trust'){ $r.Classification='Authentication failed (Kerberos/credentials) - run with alternate credentials for the target domain' }
        elseif($e -match 'Access is denied|0x80070005'){ $r.Classification='Access denied (account is not a local administrator on the target)' }
        elseif($e -match 'WinRM|WS-Management|cannot connect|0x80338012|timeout|timed out|RPC server'){ $r.Classification='WMI/WinRM blocked or not responding (firewall, service off or timeout)' }
        else{ $r.Classification='CIM connection failed' }
    }
    [pscustomobject]$r
}
function Invoke-IABBRoleCollectorTimed {
    # v0.9.9h: the role collector now targets $ComputerName with $Credential (on the target via WinRM, or RSAT from this PC).
    param([string]$FunctionName,[string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300,[bool]$UseRemoting=$true)
    $o=$null; $err=$null
    try{ $o=& $FunctionName -ComputerName $ComputerName -Credential $Credential -TimeoutSec $TimeoutSec -UseRemoting $UseRemoting }catch{ $err=$_.Exception.Message }
    $to=[bool]($o -and $o.TimedOut)
    [pscustomobject]@{Data=$o;TimedOut=$to;Error=$(if($err){$err}elseif(-not $o){'No data returned'}elseif($to){"Timed out after $TimeoutSec seconds"}elseif(-not $o.Success){[string]$o.Error}else{$null})}
}
$script:IABBExtrasBlock={
    param([int]$Budget,[int]$T,[bool]$IsDC)
    $sw=[Diagnostics.Stopwatch]::StartNew()
    function Emit([string]$s,$d){ [pscustomobject]@{Section=$s;Data=@($d)} }
    function Get-MemberName($m){
        $p=''; try{ $p=[string]$m.GetType().InvokeMember('ADsPath','GetProperty',$null,$m,$null) }catch{ return '' }
        $p=$p -replace '^WinNT://',''
        $parts=@($p -split '/')
        if($parts.Count -ge 3 -and $parts[1] -ieq $env:COMPUTERNAME){ return [string]$parts[2] }
        if($parts.Count -eq 2){ return ([string]$parts[0]+'\'+[string]$parts[1]) }
        if($parts.Count -ge 3){ return ([string]$parts[1]+'\'+[string]$parts[2]) }
        return $p
    }
    # v0.9.9i: a stopped/disabled Print Spooler is a setting, not a fault - record a note instead of a 'Generic failure' warning
    $spool=$null; $spoolKnown=$false
    try{ $spool=@(Get-CimInstance Win32_Service -Filter "Name='Spooler'" -OperationTimeoutSec $T -ErrorAction Stop)|Select-Object -First 1; $spoolKnown=$true }catch{}
    if($spoolKnown -and -not $spool){ Emit 'PrintersNote' 'The Print Spooler service is not installed, so no printers are available on this server.' }
    elseif($spoolKnown -and [string]$spool.State -ne 'Running'){ Emit 'PrintersNote' ('The Print Spooler service is '+[string]$spool.State+' ('+[string]$spool.StartMode+'), so no printers are available on this server.') }
    else{ try{ Emit 'Printers' @(Get-CimInstance Win32_Printer -OperationTimeoutSec $T -ErrorAction Stop | ForEach-Object { [ordered]@{Name=[string]$_.Name;DriverName=[string]$_.DriverName;PortName=[string]$_.PortName;Shared=[string]$_.Shared;ShareName=[string]$_.ShareName;Location=[string]$_.Location;Default=[string]$_.Default} }) }catch{ Emit 'Warning' ('Printers: '+$_.Exception.Message) } }
    try{ Emit 'PageFiles' @(Get-CimInstance Win32_PageFileUsage -OperationTimeoutSec $T -ErrorAction Stop | ForEach-Object { [ordered]@{Name=[string]$_.Name;AllocatedBaseSizeMB=$_.AllocatedBaseSize;CurrentUsageMB=$_.CurrentUsage;PeakUsageMB=$_.PeakUsage} }) }catch{ Emit 'Warning' ('Page file: '+$_.Exception.Message) }
    try{ Emit 'EnvironmentVariables' @(Get-CimInstance Win32_Environment -OperationTimeoutSec $T -ErrorAction Stop | Where-Object { $_.SystemVariable -eq $true } | ForEach-Object { [ordered]@{Name=[string]$_.Name;Value=[string]$_.VariableValue} }) }catch{ Emit 'Warning' ('Environment variables: '+$_.Exception.Message) }
    try{ if(Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue){ Emit 'RolesFeatures' @(Get-WindowsFeature -ErrorAction Stop | Where-Object { $_.Installed } | ForEach-Object { [ordered]@{Name=[string]$_.Name;DisplayName=[string]$_.DisplayName;FeatureType=[string]$_.FeatureType} }) } }catch{ Emit 'Warning' ('Roles and features: '+$_.Exception.Message) }
    if($IsDC){
        Emit 'LocalUsers' @()
        Emit 'Note' 'Domain controller: there are no local (SAM) user accounts. Only the built-in domain groups (S-1-5-32-*) are listed below; full domain group membership belongs in Active Directory documentation.'
    } else {
        $cn=$env:COMPUTERNAME
        try{ Emit 'LocalUsers' @(Get-CimInstance Win32_UserAccount -Filter "Domain='$cn'" -OperationTimeoutSec $T -ErrorAction Stop | ForEach-Object { [ordered]@{Name=[string]$_.Name;FullName=[string]$_.FullName;Disabled=[string]$_.Disabled;Lockout=[string]$_.Lockout;PasswordExpires=[string]$_.PasswordExpires;PasswordRequired=[string]$_.PasswordRequired;SID=[string]$_.SID} }) }catch{ Emit 'Warning' ('Local users: '+$_.Exception.Message) }
    }
    try{
        $filter=if($IsDC){"LocalAccount=True AND SID LIKE 'S-1-5-32-%'"}else{"Domain='"+$env:COMPUTERNAME+"'"}
        $groups=@(Get-CimInstance Win32_Group -Filter $filter -OperationTimeoutSec $T -ErrorAction Stop | Sort-Object Name)
        $rows=@(); $skipped=0
        foreach($g in $groups){
            if($sw.Elapsed.TotalSeconds -ge $Budget){ $skipped++; $rows+=[ordered]@{Name=[string]$g.Name;Description=[string]$g.Description;SID=[string]$g.SID;MemberCount='';Members='(member lookup skipped - time limit reached)'}; continue }
            $members=@()
            try{
                $adsi=[ADSI]('WinNT://'+$env:COMPUTERNAME+'/'+[string]$g.Name+',group')
                $members=@($adsi.psbase.Invoke('Members') | ForEach-Object { Get-MemberName $_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            }catch{ $members=@('(member lookup failed: '+$_.Exception.Message+')') }
            $rows+=[ordered]@{Name=[string]$g.Name;Description=[string]$g.Description;SID=[string]$g.SID;MemberCount=@($members).Count;Members=(@($members) -join ', ')}
        }
        Emit 'LocalGroups' $rows
        if($skipped -gt 0){ Emit 'Warning' ("Local groups: member lookup skipped for $skipped group(s) after reaching the time limit.") }
    }catch{ Emit 'Warning' ('Local groups: '+$_.Exception.Message) }
}
function Invoke-IABBServerCollection{
param([string]$ComputerName,[int]$TimeoutSeconds=30,[Management.Automation.PSCredential]$Credential,[bool]$EnableDcomFallback=$true,[int]$CimOperationTimeoutSec=180,[int]$SectionTimeoutSec=300,[scriptblock]$OnProgress)
$r=[ordered]@{SchemaVersion='1.0.0';ComputerName=$ComputerName;CollectionStarted=(Get-Date).ToString('s');CollectionFinished=$null;Success=$false;Error=$null;CollectionStatus=$null;SectionTimings=@();Overview=$null;Hardware=$null;OperatingSystem=$null;Storage=@();Services=@();Hotfixes=@();Shares=@();ProcessorDetails=@();MemoryModules=@();PhysicalDisks=@();Partitions=@();DiskMap=@();VolumeDetails=@();NetworkAdapters=@();NetworkAdapterDetail=@();PatchSummary=$null;Virtualization=$null;RdsSessionHost=$false;RolesDetected=@();RoleDetection=$null;RoleData=[ordered]@{};Applications=$null;Certificates=$null;LocalUsers=@();LocalGroups=@();LocalAccountsNote=$null;PrintersNote=$null;RoleNotes=@();LocalAccountsCollected=$false;Printers=@();PageFiles=@();EnvironmentVariables=@();RolesFeatures=@();CollectionWarnings=@()}
$status=[ordered]@{Status='Failed';Transport='None';Reachable=$false;PingOk=$false;Authorized=$false;Classification='Unknown';SectionsOk=0;SectionsFailed=0;StepsTimedOut=0;Detail=$null}
function Send-IABBProgress([string]$Message){ if($OnProgress){ try{ & $OnProgress $Message }catch{} } }
$script:__stepResult=$null
function Invoke-IABBStep {
    param([string]$Name,[scriptblock]$Body)
    Send-IABBProgress $Name
    $script:__stepResult=$null
    $sw=[Diagnostics.Stopwatch]::StartNew(); $res='OK'
    try{ & $Body | Out-Null }
    catch{ $res='Failed'; $r.CollectionWarnings+=("Step '"+$Name+"' failed: "+$_.Exception.Message) }
    finally{ $sw.Stop(); if($script:__stepResult){ $res=$script:__stepResult }; $r.SectionTimings+=[ordered]@{Section=$Name;Seconds=[math]::Round($sw.Elapsed.TotalSeconds,1);Result=$res} }
}
function Set-IABBStepOutcome { param($Result,[string]$Label) if($null -eq $Result){ $script:__stepResult='Failed'; $r.CollectionWarnings+=($Label+': no data returned.'); return }; if($Result.TimedOut){ $script:__stepResult='Timed out'; $r.CollectionWarnings+=($Label+' timed out after '+$SectionTimeoutSec+' seconds.'); return }; if(-not $Result.Success){ $script:__stepResult='Failed'; $r.CollectionWarnings+=($Label+' not collected: '+[string]$Result.Error) } }
# v0.9.9h: zero-row sanity rule - a step that always returns data on a working server, but returned none, is marked 'No data' (counts as failed -> Partial)
function Test-IABBStepData { param([string]$Label,$Items) if(@(@($Items)|Where-Object { $null -ne $_ }).Count -eq 0){ if(-not $script:__stepResult){ $script:__stepResult='No data' }; $r.CollectionWarnings+=($Label+' returned no data. A working server always has at least one entry, so this section is treated as not collected.') } }
$session=$null
$swProbe=[Diagnostics.Stopwatch]::StartNew()
Send-IABBProgress 'Readiness probe'
$ready=Test-IABBCollectionReadiness -ComputerName $ComputerName -Credential $Credential -EnableDcomFallback $EnableDcomFallback -OperationTimeoutSec ([math]::Max(30,$TimeoutSeconds))
$swProbe.Stop(); $r.SectionTimings+=[ordered]@{Section='Readiness probe';Seconds=[math]::Round($swProbe.Elapsed.TotalSeconds,1);Result=$(if($ready.Reachable){'OK'}else{'Failed'})}
$status.Reachable=$ready.Reachable;$status.PingOk=$ready.PingOk;$status.Transport=$ready.Transport;$status.Authorized=$ready.Authorized;$status.Classification=$ready.Classification;$status.Detail=$ready.Detail
if(-not $ready.Reachable){ $r.Error="Collection could not start: $($ready.Classification). $($ready.Detail)"; $r.CollectionWarnings+=("Collection could not start: "+$ready.Classification); $status.Status='Failed'; $r.CollectionStatus=[pscustomobject]$status; $r.CollectionFinished=(Get-Date).ToString('s'); return [pscustomobject]$r }
if($ready.Transport -eq 'DCOM'){ $r.CollectionWarnings+=('WinRM (WSMan) connection failed, so DCOM was used. Steps that need PowerShell remoting (local accounts, applications, certificates, IIS, SQL) will fail until WinRM access is granted. WSMan error: '+[string]$ready.WsmanError) }
$T=$CimOperationTimeoutSec
try{
    Send-IABBProgress 'Connecting (CIM session)'
    # v1.0.0: the local server is always collected as the signed-in user (Windows does not accept alternate credentials for local connections)
    if(Test-IABBIsLocal $ComputerName){ $cp=@{OperationTimeoutSec=$T}; if($Credential){ $r.CollectionWarnings+=('This is the server running the tool, so it was collected as the signed-in user; the alternate credentials are not used for local collection.') } }
    else{ $cs=New-IABBCimSession -ComputerName $ComputerName -Credential $Credential -EnableDcomFallback $EnableDcomFallback -OperationTimeoutSec $T; if(-not $cs.Session){ throw ("CIM session could not be established. "+$cs.Error) }; $session=$cs.Session;$status.Transport=$cs.Protocol;$cp=@{CimSession=$session;OperationTimeoutSec=$T} }
    # v1.0.0: open one reusable remoting session for all WinRM steps of this server
    if(-not (Test-IABBIsLocal $ComputerName) -and [string]$status.Transport -eq 'WSMan'){
        Send-IABBProgress 'Opening remoting session'
        try{
            $so=New-PSSessionOption -OpenTimeout ([int]([math]::Max(30,$TimeoutSeconds)*1000))
            $sp=@{ComputerName=$ComputerName;SessionOption=$so;ErrorAction='Stop'}; if($Credential){ $sp.Credential=$Credential }
            Register-IABBSession -ComputerName $ComputerName -Session (New-PSSession @sp)
        }catch{ Send-IABBProgress ('Reusable remoting session not available ('+$_.Exception.Message+'); each step connects separately') }
    }
    Send-IABBProgress 'System information'
    $cs0=Get-CimInstance @cp Win32_ComputerSystem -ErrorAction Stop;$bios=Get-CimInstance @cp Win32_BIOS -ErrorAction SilentlyContinue;$enc=@(Get-CimInstance @cp Win32_SystemEnclosure -ErrorAction SilentlyContinue)|Select-Object -First 1;$os=Get-CimInstance @cp Win32_OperatingSystem -ErrorAction Stop
    $isDC=([int]$cs0.DomainRole -ge 4)
    Invoke-IABBStep 'Processors/Memory' { $procs=@(Get-CimInstance @cp Win32_Processor -ErrorAction Stop);$mem=@(Get-CimInstance @cp Win32_PhysicalMemory -ErrorAction SilentlyContinue); $r.Hardware=[ordered]@{ProcessorModel=(($procs|select -ExpandProperty Name -Unique)-join ', ');MemoryGB=if($cs0.TotalPhysicalMemory){[math]::Round($cs0.TotalPhysicalMemory/1GB,2)}else{$null};MemoryModuleCount=@($mem).Count}; $r.ProcessorDetails=@($procs|%{[ordered]@{SocketDesignation=$_.SocketDesignation;Name=$_.Name;Cores=$_.NumberOfCores;LogicalProcessors=$_.NumberOfLogicalProcessors;MaxClockSpeedMHz=$_.MaxClockSpeed}}); $r.MemoryModules=@($mem|%{[ordered]@{BankLabel=$_.BankLabel;DeviceLocator=$_.DeviceLocator;CapacityGB=if($_.Capacity){[math]::Round($_.Capacity/1GB,2)}else{$null};SpeedMHz=$_.Speed;Manufacturer=$_.Manufacturer;PartNumber=([string]$_.PartNumber).Trim()}}); Test-IABBStepData 'Processors' $r.ProcessorDetails }
    Invoke-IABBStep 'Disks/Volumes' { $disks=@(Get-CimInstance @cp Win32_DiskDrive -ErrorAction Stop);$parts=@(Get-CimInstance @cp Win32_DiskPartition -ErrorAction SilentlyContinue);$logical=@(Get-CimInstance @cp Win32_LogicalDisk -Filter "DriveType=3" -ErrorAction SilentlyContinue);$vols=@(Get-CimInstance @cp Win32_Volume -Filter "DriveType=3" -ErrorAction SilentlyContinue);$diskToPart=@(Get-CimInstance @cp Win32_DiskDriveToDiskPartition -ErrorAction SilentlyContinue);$partToLogical=@(Get-CimInstance @cp Win32_LogicalDiskToPartition -ErrorAction SilentlyContinue); $r.PhysicalDisks=@($disks|sort Index|%{[ordered]@{Index=$_.Index;Model=$_.Model;DiskClass=if([string]$_.Model -match 'Virtual|VMware'){'Virtual Disk'}else{'Physical Disk'};InterfaceType=$_.InterfaceType;MediaType=$_.MediaType;SizeGB=if($_.Size){[math]::Round($_.Size/1GB,2)}else{$null};Partitions=$_.Partitions}}); $r.Partitions=@($parts|sort DiskIndex,Index|%{[ordered]@{DiskIndex=$_.DiskIndex;Name=$_.Name;Type=$_.Type;SizeGB=if($_.Size){[math]::Round($_.Size/1GB,2)}else{$null};Bootable=$_.Bootable;BootPartition=$_.BootPartition;Primary=$_.PrimaryPartition;StartingOffset=$_.StartingOffset}}); $ptl=@{}; foreach($pl in @($partToLogical)){ $pN=Get-IABBAssocDeviceId $pl.Antecedent; $lN=Get-IABBAssocDeviceId $pl.Dependent; if(-not [string]::IsNullOrWhiteSpace($pN)){ $ptl[$pN]=$lN } }; $mapRows=@(); foreach($dp in @($diskToPart)){ $dD=Get-IABBAssocDeviceId $dp.Antecedent; $pD=Get-IABBAssocDeviceId $dp.Dependent; $lD=''; if($ptl.ContainsKey($pD)){$lD=$ptl[$pD]}; $mapRows+=[ordered]@{PhysicalDisk=$dD;Partition=$pD;LogicalDisk=$lD} }; $r.DiskMap=@($mapRows); $r.Storage=@($logical|sort DeviceID|%{[ordered]@{Drive=$_.DeviceID;FileSystem=$_.FileSystem;SizeGB=if($_.Size){[math]::Round($_.Size/1GB,2)}else{0};FreeGB=if($_.FreeSpace){[math]::Round($_.FreeSpace/1GB,2)}else{0}}}); $r.VolumeDetails=@($vols|sort DriveLetter,Name,Label|%{$vol=$_;$fp=if($vol.Capacity){[math]::Round(($vol.FreeSpace/$vol.Capacity)*100,2)}else{$null};$cls=Get-IABBVolumeClassification -DriveLetter $vol.DriveLetter -Name $vol.Name -Caption $vol.Caption;[ordered]@{DriveLetter=$vol.DriveLetter;MountPath=$cls.MountPath;VolumeType=$cls.VolumeType;IsData=$cls.IsData;Label=$vol.Label;FileSystem=$vol.FileSystem;CapacityGB=if($vol.Capacity){[math]::Round($vol.Capacity/1GB,2)}else{$null};FreeGB=if($vol.FreeSpace){[math]::Round($vol.FreeSpace/1GB,2)}else{$null};FreePct=$fp;HealthFlag=if($fp -ne $null -and $fp -lt 15){'LOW SPACE'}else{'OK'};BlockSize=$vol.BlockSize}}); Test-IABBStepData 'Physical disks' $r.PhysicalDisks; Test-IABBStepData 'Volumes' $r.VolumeDetails }
    Invoke-IABBStep 'Network' {
        $nicCfg=@(Get-CimInstance @cp Win32_NetworkAdapterConfiguration -Filter "IPEnabled=True" -ErrorAction Stop);$nics=@(Get-CimInstance @cp Win32_NetworkAdapter -ErrorAction SilentlyContinue)
        $r.NetworkAdapters=@($nicCfg|sort Description|%{$cfg=$_;$ad=@($nics|?{$_.Index -eq $cfg.Index}|select -First 1);$ipv4=@(ConvertTo-IABBIPv4Collection $cfg.IPAddress);$dns4=@(ConvertTo-IABBIPv4Collection $cfg.DNSServerSearchOrder);[ordered]@{Name=if($ad){[string]$ad.NetConnectionID}else{$null};Description=[string]$cfg.Description;SpeedMbps=if($ad -and $ad.Speed){[math]::Round($ad.Speed/1000000,0)}else{$null};MACAddress=[string]$cfg.MACAddress;IPAddress=($ipv4 -join ', ');Gateway=(@($cfg.DefaultIPGateway) -join ', ');DNSServers=($dns4 -join ', ');DHCPEnabled=$cfg.DHCPEnabled}})
        $r.NetworkAdapterDetail=@($nicCfg|sort Description|%{$cfg=$_;$ad=@($nics|?{$_.Index -eq $cfg.Index}|select -First 1);$ipv4=@(ConvertTo-IABBIPv4Collection $cfg.IPAddress);$dns4=@(ConvertTo-IABBIPv4Collection $cfg.DNSServerSearchOrder);$masks=@($cfg.IPSubnet|?{$_ -match '^(\d{1,3}\.){3}\d{1,3}$'});$wins=@();if($cfg.WINSPrimaryServer){$wins+=[string]$cfg.WINSPrimaryServer};if($cfg.WINSSecondaryServer){$wins+=[string]$cfg.WINSSecondaryServer};[ordered]@{Name=if($ad){[string]$ad.NetConnectionID}else{[string]$cfg.Description};Status=if($ad){switch([string]$ad.NetConnectionStatus){'2'{'Connected'}'7'{'Media disconnected'}'0'{'Disconnected'}default{[string]$ad.NetConnectionStatus}}}else{'Unknown'};IPv4Address=($ipv4 -join ', ');SubnetMask=($masks -join ', ');DefaultGateway=(@($cfg.DefaultIPGateway) -join ', ');DNSServers=($dns4 -join ', ');ConnSpecificSuffix=[string]$cfg.DNSDomain;DNSDomainSuffixSearchOrder=(@($cfg.DNSDomainSuffixSearchOrder) -join ', ');DHCPEnabled=[string]$cfg.DHCPEnabled;DHCPServer=[string]$cfg.DHCPServer;DHCPLeaseObtained=[string]$cfg.DHCPLeaseObtained;DHCPLeaseExpires=[string]$cfg.DHCPLeaseExpires;WINSServers=($wins -join ', ');NetBIOSSetting=(Get-IABBNetBiosOption $cfg.TcpipNetbiosOptions)}})
        Test-IABBStepData 'Network adapters' $r.NetworkAdapters
    }
    Invoke-IABBStep 'Services/Hotfixes/Shares' {
        $svcs=@(Get-CimInstance @cp Win32_Service -ErrorAction Stop);$hot=@(Get-CimInstance @cp Win32_QuickFixEngineering -ErrorAction SilentlyContinue);$shares=@(Get-CimInstance @cp Win32_Share -ErrorAction SilentlyContinue)
        $r.Services=@($svcs|sort DisplayName|%{[ordered]@{Name=$_.Name;DisplayName=$_.DisplayName;State=$_.State;StartMode=$_.StartMode;StartName=$_.StartName}})
        $hf=@($hot|%{ $dt=Convert-IABBHotfixDate $_.InstalledOn; [pscustomobject]@{HotFixID=[string]$_.HotFixID;Description=[string]$_.Description;InstalledOnDate=$dt;InstalledOnRaw=[string]$_.InstalledOn;InstalledBy=[string]$_.InstalledBy} })
        $hfSorted=@($hf|sort -Property @{Expression={ if($_.InstalledOnDate){$_.InstalledOnDate}else{[datetime]::MinValue} };Descending=$true},@{Expression={$_.HotFixID};Descending=$false})
        $r.Hotfixes=@($hfSorted|%{[ordered]@{HotFixID=$_.HotFixID;Description=$_.Description;InstalledOn=$(if($_.InstalledOnDate){$_.InstalledOnDate.ToString('yyyy-MM-dd')}else{''});InstalledOnRaw=$_.InstalledOnRaw;InstalledBy=$_.InstalledBy}})
        $dated=@($hf|?{ $null -ne $_.InstalledOnDate }); $now=Get-Date
        $latest=$null; if($dated.Count -gt 0){ $latest=($dated|sort InstalledOnDate -Descending|select -First 1) }
        $latestKbs=''; if($latest){ $latestKbs=(@($dated|?{ $_.InstalledOnDate.Date -eq $latest.InstalledOnDate.Date }|sort HotFixID|%{$_.HotFixID}) -join ', ') }
        $r.PatchSummary=[ordered]@{HotfixCount=@($hot).Count;DatedCount=$dated.Count;UndatedCount=(@($hot).Count-$dated.Count);LatestPatchDate=$(if($latest){$latest.InstalledOnDate.ToString('yyyy-MM-dd')}else{$null});LatestKB=$latestKbs;DaysSinceLastPatch=$(if($latest){[int][math]::Floor(($now-$latest.InstalledOnDate).TotalDays)}else{$null});HotfixesLast90Days=@($dated|?{ ($now-$_.InstalledOnDate).TotalDays -le 90 }).Count}
        $r.Shares=@($shares|sort Name|%{[ordered]@{Name=$_.Name;Path=$_.Path;Description=$_.Description;Type=$_.Type}})
        Test-IABBStepData 'Services' $r.Services
    }
    Invoke-IABBStep 'Time zone/OS' {
        $tz=Get-CimInstance @cp Win32_TimeZone -ErrorAction SilentlyContinue|select -First 1
        $r.OperatingSystem=[ordered]@{Caption=$os.Caption;Version=$os.Version;BuildNumber=$os.BuildNumber;InstallDate=$os.InstallDate;LastBootTime=$os.LastBootUpTime;UptimeDays=if($os.LastBootUpTime){[math]::Round(((Get-Date)-$os.LastBootUpTime).TotalDays,2)}else{$null};Architecture=$os.OSArchitecture;TimeZone=if($tz){$tz.Caption}else{$null}}
        # v0.9.9g: RD Session Host mode (works over WSMan and DCOM)
        try{ $ts=@(Get-CimInstance @cp -Namespace 'root\cimv2\TerminalServices' -ClassName Win32_TerminalServiceSetting -ErrorAction Stop)|select -First 1; if($ts -and [int]$ts.TerminalServerMode -eq 1){ $r.RdsSessionHost=$true } }catch{}
    }
    $hasVmms=(@($r.Services|?{ [string]$_.Name -eq 'vmms' }).Count -gt 0)
    $pi=Get-IABBPlatformInfo -Manufacturer ([string]$cs0.Manufacturer) -Model ([string]$cs0.Model) -HasVmms $hasVmms -AssetTag ([string]$enc.SMBIOSAssetTag)
    $note=if($pi.IsHyperVHost){'Physical server running the Hyper-V role (virtualization host).'}elseif($pi.IsVirtual){'Server is virtual.'}else{'Server appears physical.'}
    $r.Virtualization=[ordered]@{PlatformType=$pi.PlatformType;HostPlatform=$pi.HostPlatform;IsVirtual=[bool]$pi.IsVirtual;IsHyperVHost=[bool]$pi.IsHyperVHost;Manufacturer=$cs0.Manufacturer;Model=$cs0.Model;ReportingNote=$note}
    $r.Overview=[ordered]@{Name=$cs0.Name;Domain=$cs0.Domain;SerialNumber=$bios.SerialNumber;DomainRole=[int]$cs0.DomainRole}
    Invoke-IABBStep 'Local accounts, printers, page file, features' {
        $res=Invoke-IABBTimed -ScriptBlock $script:IABBExtrasBlock -ArgumentList @([int]($SectionTimeoutSec*0.8),$T,$isDC) -ComputerName $ComputerName -Credential $Credential -TimeoutSec $SectionTimeoutSec -Name 'Local accounts and extras'
        foreach($o in @($res.Output)){ if(-not $o){ continue }; switch([string]$o.Section){ 'Printers'{$r.Printers=@($o.Data)} 'PrintersNote'{ $r.PrintersNote=[string]@($o.Data)[0] } 'PageFiles'{$r.PageFiles=@($o.Data)} 'EnvironmentVariables'{$r.EnvironmentVariables=@($o.Data)} 'RolesFeatures'{$r.RolesFeatures=@($o.Data)} 'LocalUsers'{$r.LocalUsers=@($o.Data);$r.LocalAccountsCollected=$true} 'LocalGroups'{$r.LocalGroups=@($o.Data);$r.LocalAccountsCollected=$true} 'Note'{ $r.LocalAccountsNote=[string]@($o.Data)[0] } 'Warning'{ foreach($wtxt in @($o.Data)){ $r.CollectionWarnings+=[string]$wtxt } } } }
        if($res.TimedOut){ $script:__stepResult='Timed out'; $r.CollectionWarnings+=("Local accounts/printers/features stopped after $SectionTimeoutSec seconds (timeout). Data collected before the timeout is shown.") }
        elseif($res.Error -and @($res.Output).Count -eq 0){ $r.LocalAccountsNote=('Not collected: '+[string]$res.Error); throw $res.Error }
    }
    $rd=Invoke-IABBRoleDetection -ServerData ([pscustomobject]$r);$r.RoleDetection=$rd;$r.RolesDetected=@($rd.Roles)
    $r.RoleNotes=@($rd.Notes|?{ -not [string]::IsNullOrWhiteSpace([string]$_) })   # v0.9.9i: informational, shown under Role Services (not a collection warning)
    # v0.9.9h: AD DS / DNS / DHCP are collected from the TARGET with the supplied credentials (on the target via WinRM, else RSAT from this PC)
    $useRemoting=([string]$status.Transport -ne 'DCOM')
    $roleSteps=@(@('Domain Controller','ADDS','Invoke-IABBADDSCollection','Active Directory'),@('DNS Server','DNS','Invoke-IABBDNSCollection','DNS Server'),@('DHCP Server','DHCP','Invoke-IABBDHCPCollection','DHCP Server'))
    foreach($rs in $roleSteps){
        if($rs[0] -in @($r.RolesDetected)){
            $key=$rs[1];$fn=$rs[2];$label=$rs[3]
            Invoke-IABBStep $label { $x=Invoke-IABBRoleCollectorTimed -FunctionName $fn -ComputerName $ComputerName -Credential $Credential -TimeoutSec $SectionTimeoutSec -UseRemoting $useRemoting; if($x.Data){ $r.RoleData[$key]=$x.Data; foreach($nt in @($x.Data.Notes)){ if(-not [string]::IsNullOrWhiteSpace([string]$nt)){ $r.CollectionWarnings+=[string]$nt } } } else { $r.RoleData[$key]=[pscustomobject]@{Success=$false;Error=$x.Error} }; if($x.TimedOut){ $script:__stepResult='Timed out'; $r.CollectionWarnings+=($label+' collection timed out after '+$SectionTimeoutSec+' seconds.') } elseif($x.Error){ $script:__stepResult='Failed'; $r.CollectionWarnings+=($label+' not collected: '+$x.Error) } }
        }
    }
    if('IIS Server' -in @($r.RolesDetected)){ Invoke-IABBStep 'IIS' { $x=Invoke-IABBIISCollection -ComputerName $ComputerName -Credential $Credential -TimeoutSec $SectionTimeoutSec; $r.RoleData['IIS']=$x; Set-IABBStepOutcome $x 'IIS' } }
    if('SQL Server' -in @($r.RolesDetected)){ Invoke-IABBStep 'SQL Server' {
        $x=Invoke-IABBSqlCollection -ComputerName $ComputerName -Credential $Credential -TimeoutSec $SectionTimeoutSec
        Set-IABBStepOutcome $x 'SQL Server'
        $insts=@($x.Instances|?{ $_ }); $known=@{}; foreach($i in $insts){ $known[([string]$i.Instance).ToUpperInvariant()]=$true }
        foreach($svc in @($r.Services|?{ ([string]$_.Name -eq 'MSSQLSERVER' -or [string]$_.Name -like 'MSSQL$*') -and [string]$_.Name -notlike '*MICROSOFT##*' })){
            $nm=[string]$svc.Name; $inst=if($nm -eq 'MSSQLSERVER'){'MSSQLSERVER'}else{$nm.Substring(6)}
            if(-not $known.ContainsKey($inst.ToUpperInvariant())){ $insts+=[pscustomobject]@{Instance=$inst;InstanceId='';Version='';PatchLevel='';Edition='(details not found in registry)';DataRoot='';TcpPort='';ServiceName=$nm;AgentServiceName=$(if($inst -eq 'MSSQLSERVER'){'SQLSERVERAGENT'}else{'SQLAgent$'+$inst});Architecture=''} }
        }
        $x.Instances=@($insts); $r.RoleData['SQL']=$x
    } }
    Invoke-IABBStep 'Installed applications' { $x=Invoke-IABBApplicationCollection -ComputerName $ComputerName -Credential $Credential -TimeoutSec $SectionTimeoutSec; $r.Applications=$x; Set-IABBStepOutcome $x 'Applications inventory'; if($x -and $x.Success){ Test-IABBStepData 'Applications inventory' @(@($x.Applications)+@($x.HiddenEntries)) } }
    Invoke-IABBStep 'Certificates' { $x=Invoke-IABBCertificateCollection -ComputerName $ComputerName -Credential $Credential -TimeoutSec $SectionTimeoutSec; $r.Certificates=$x; Set-IABBStepOutcome $x 'Certificates inventory'; if($x -and $x.Success){ Test-IABBStepData 'Certificates inventory' $x.Certificates } }
    $r.Success=$true
}catch{$r.Error=$_.Exception.Message;$r.CollectionWarnings+=('Core collection failed: '+$_.Exception.Message)}finally{if($session){$session|Remove-CimSession -ErrorAction SilentlyContinue};Unregister-IABBSession -ComputerName $ComputerName;$r.CollectionFinished=(Get-Date).ToString('s')}
$steps=@($r.SectionTimings|?{ $_ -and $_.Section -ne 'Readiness probe' })
$status.SectionsOk=@($steps|?{ $_.Result -eq 'OK' }).Count;$status.SectionsFailed=@($steps|?{ $_.Result -eq 'Failed' -or $_.Result -eq 'No data' }).Count;$status.StepsTimedOut=@($steps|?{ $_.Result -eq 'Timed out' }).Count
if($r.Success -and $status.SectionsFailed -eq 0 -and $status.StepsTimedOut -eq 0){ $status.Status='Complete' }elseif($r.Success){ $status.Status='Partial' }else{ $status.Status='Failed' }
$r.CollectionStatus=[pscustomobject]$status
Send-IABBProgress ('Done - '+$status.Status)
[pscustomobject]$r}
Export-ModuleMember -Function Invoke-IABBServerCollection,Get-IABBVolumeClassification,New-IABBCimSession,Test-IABBCollectionReadiness,Convert-IABBHotfixDate,Invoke-IABBRoleCollectorTimed,Get-IABBPlatformInfo
