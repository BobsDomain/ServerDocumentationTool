# IABB.Core - Server Documentation Tool v1.1.0
function Get-IABBConfig { param([Parameter(Mandatory)][string]$ApplicationRoot) $p=Join-Path $ApplicationRoot 'Config\appsettings.json'; if(Test-Path $p){Get-Content $p -Raw|ConvertFrom-Json}else{[pscustomobject]@{Version='1.1.0';DefaultOutputRoot='C:\ServerDocumentation';ServerOperatingSystemFilter='Windows Server';TimeoutSeconds=30;CimOperationTimeoutSec=180;SectionTimeoutSec=300;IncludeWorkstations=$false;EnableDcomFallback=$true;GroupMemberDisplayCap=15;ApplicationDisplayCap=200;CertificateExpiringDays=60}} }
function ConvertTo-IABBSafeFileName { param([string]$Name) if([string]::IsNullOrWhiteSpace($Name)){'Unknown'}else{(($Name -replace '[\\/:*?"<>|]','_') -replace '\s+','_')} }
function Save-IABBTextFile { param([Parameter(Mandatory)][string]$Path,[AllowNull()][string]$Content) $parent=Split-Path -Parent $Path; if($parent -and -not(Test-Path $parent)){New-Item -Path $parent -ItemType Directory -Force|Out-Null}; [IO.File]::WriteAllText($Path,[string]$Content,(New-Object Text.UTF8Encoding($false))) }
function New-IABBRunFolder { param([string]$OutputRoot,[string]$ClientName) if([string]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot='C:\ServerDocumentation'}; if([string]::IsNullOrWhiteSpace($ClientName)){$ClientName='Client'}; $safe=ConvertTo-IABBSafeFileName $ClientName; $root=Join-Path $OutputRoot ($safe+'_AsBuilt_'+(Get-Date -Format 'yyyyMMdd_HHmmss')); $raw=Join-Path $root 'RawData'; New-Item -Path $raw -ItemType Directory -Force|Out-Null; [pscustomobject]@{Root=$root;RawData=$raw;ClientName=$safe} }
function Import-IABBServerList { param([Parameter(Mandatory)][string]$Path) if(-not (Test-Path $Path)){ throw "Server list file not found: $Path" }; if([IO.Path]::GetExtension($Path) -ieq '.csv'){Import-Csv $Path|%{[pscustomobject]@{ServerName=($_.ServerName,$_.Server,$_.ComputerName,$_.Name|?{$_}|select -First 1);Source='CSV Import'}}|?{-not [string]::IsNullOrWhiteSpace($_.ServerName)}}else{Get-Content $Path|?{-not [string]::IsNullOrWhiteSpace($_)}|%{[pscustomobject]@{ServerName=$_.Trim();Source='TXT Import'}}} }
function ConvertFrom-IABBServerText { param([AllowNull()][string]$Text) if([string]::IsNullOrWhiteSpace($Text)){ return @() }; $names=@($Text -split '[\r\n,;\t ]+' | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique); @($names | ForEach-Object { [pscustomobject]@{ServerName=$_;Source='Manual Entry'} }) }
# v1.0.0: the local server is also recognised by its fully qualified name (domain discovery supplies FQDNs).
# Before v1.0.0, the PC running the tool was treated as remote when given as SERVER.domain: WinRM loopback was refused, DCOM was used, and the report was Partial.
$script:IABBLocalNames=$null
function Get-IABBLocalNames {
    if($null -eq $script:IABBLocalNames){
        $cn=([string]$env:COMPUTERNAME).ToLowerInvariant()
        $names=@('.','localhost','127.0.0.1','::1',$cn)
        $dom=''; try{ $dom=([string][System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().DomainName).Trim().ToLowerInvariant() }catch{}
        if($dom){ $names+=($cn+'.'+$dom) }
        try{ $hn=([string][System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName).Trim().ToLowerInvariant(); if($hn){ $names+=$hn } }catch{}
        $script:IABBLocalNames=@($names|Where-Object { $_ }|Select-Object -Unique)
    }
    $script:IABBLocalNames
}
function Test-IABBIsLocal { param([string]$ComputerName) if([string]::IsNullOrWhiteSpace($ComputerName)){ return $true }; $n=$ComputerName.Trim().ToLowerInvariant(); if($n -ne '.'){ $n=$n.TrimEnd('.') }; return (@(Get-IABBLocalNames) -contains $n) }
# v1.0.0: one reusable PowerShell remoting session per server. Every WinRM step (local accounts, applications, certificates, IIS, SQL, role queries) reuses it.
# In the v0.9.9i field test, opening a new connection cost about 84 s per step on slow hosts. If no session is registered, or it is broken, each step connects on its own as before.
$script:IABBSessions=@{}
function Register-IABBSession { param([string]$ComputerName,$Session) if($ComputerName -and $Session){ $script:IABBSessions[$ComputerName.Trim().ToLowerInvariant()]=$Session } }
function Get-IABBSession { param([string]$ComputerName) if([string]::IsNullOrWhiteSpace($ComputerName)){ return $null }; $k=$ComputerName.Trim().ToLowerInvariant(); if($script:IABBSessions.ContainsKey($k)){ $s=$script:IABBSessions[$k]; if($s -and [string]$s.State -eq 'Opened' -and [string]$s.Availability -eq 'Available'){ return $s } }; return $null }
function Unregister-IABBSession { param([string]$ComputerName) if([string]::IsNullOrWhiteSpace($ComputerName)){ return }; $k=$ComputerName.Trim().ToLowerInvariant(); if($script:IABBSessions.ContainsKey($k)){ $s=$script:IABBSessions[$k]; $script:IABBSessions.Remove($k); if($s){ try{ Remove-PSSession -Session $s -ErrorAction SilentlyContinue }catch{} } } }
function Invoke-IABBTimed {
    param([Parameter(Mandatory)][scriptblock]$ScriptBlock,[object[]]$ArgumentList=@(),[string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300,[string]$Name='Operation')
    $job=$null
    try{
        if(Test-IABBIsLocal $ComputerName){ $job=Start-Job -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList }
        else{
            $sess=Get-IABBSession $ComputerName
            if($sess){ $job=Invoke-Command -Session $sess -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList -AsJob -ErrorAction Stop }
            else{ $p=@{ComputerName=$ComputerName;ScriptBlock=$ScriptBlock;ArgumentList=$ArgumentList;AsJob=$true;ErrorAction='Stop'}; if($Credential){$p.Credential=$Credential}; $job=Invoke-Command @p }
        }
        $sw=[Diagnostics.Stopwatch]::StartNew(); $timedOut=$false
        while($true){
            $done=Wait-Job -Job $job -Timeout 1
            if($done){ break }
            try{ [System.Windows.Forms.Application]::DoEvents() }catch{}
            if($sw.Elapsed.TotalSeconds -ge $TimeoutSec){ $timedOut=$true; break }
        }
        if($timedOut){ Stop-Job -Job $job -ErrorAction SilentlyContinue }
        $errs=$null
        $out=@(Receive-Job -Job $job -ErrorAction SilentlyContinue -ErrorVariable errs)
        $err=$null
        if(-not $timedOut -and $job.State -eq 'Failed'){ $reason=@($job.ChildJobs|%{ $_.JobStateInfo.Reason }|?{ $_ })|select -First 1; $err=if($reason){[string]$reason.Message}else{'Job failed'} }
        elseif(@($errs).Count -gt 0 -and $out.Count -eq 0){ $err=[string]@($errs)[0] }
        [pscustomobject]@{Output=$out;TimedOut=$timedOut;Error=$err}
    }catch{ [pscustomobject]@{Output=@();TimedOut=$false;Error=$_.Exception.Message} }
    finally{ if($job){ Remove-Job -Job $job -Force -ErrorAction SilentlyContinue } }
}
# v0.9.9h: result recorded for a server whose collection stopped unexpectedly, so the run continues and the report shows 'Failed'
function New-IABBFailedResult {
    param([string]$ComputerName,[string]$Message)
    $now=(Get-Date).ToString('s')
    $st=[pscustomobject]@{Status='Failed';Transport='None';Reachable=$false;PingOk=$false;Authorized=$false;Classification='Unexpected error during collection';SectionsOk=0;SectionsFailed=0;StepsTimedOut=0;Detail=$Message}
    [pscustomobject]@{SchemaVersion='1.1.0';ComputerName=$ComputerName;CollectionStarted=$now;CollectionFinished=$now;Success=$false;Error=$Message;CollectionStatus=$st;SectionTimings=@();RolesDetected=@();RoleData=[ordered]@{};CollectionWarnings=@('Collection stopped unexpectedly: '+$Message)}
}
# v0.9.9h: run a role query (AD DS / DNS / DHCP) against the TARGET server, with the supplied credentials.
# The script block must accept param($Server,$Cred,$Protocol) and return one object with Success, Error and ModuleMissing.
#   1. Target is this PC          -> run locally (Server = $null).
#   2. WinRM available            -> run ON the target via remoting (Server = $null, the target's own domain/service).
#   3. WinRM unavailable, or the role module is missing on the target -> run from this PC using RSAT,
#      pointed at the target (-Server / -CimSession) with the supplied credentials.
function Invoke-IABBRoleQuery {
    param([Parameter(Mandatory)][scriptblock]$ScriptBlock,[string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300,[bool]$UseRemoting=$true,[string]$Name='Role collection')
    $notes=@(); $res=$null; $method=$null
    if(Test-IABBIsLocal $ComputerName){
        $res=Invoke-IABBTimed -ScriptBlock $ScriptBlock -ArgumentList @($null,$null,'Wsman') -TimeoutSec $TimeoutSec -Name $Name
        $method='Local (this server)'
    } else {
        $proto='Dcom'
        if($UseRemoting){
            $res=Invoke-IABBTimed -ScriptBlock $ScriptBlock -ArgumentList @($null,$null,'Wsman') -ComputerName $ComputerName -Credential $Credential -TimeoutSec $TimeoutSec -Name $Name
            $method='On target via WinRM'
            $first=@($res.Output)|Select-Object -First 1
            if(-not $res.TimedOut -and ($null -eq $first -or $first.ModuleMissing)){
                $why=if($first){[string]$first.Error}else{[string]$res.Error}
                if($first){ $proto='Wsman' }
                $notes+=($Name+' could not be collected on the target ('+$why+'); retried from this PC using RSAT.')
                $res=$null
            }
        }
        if($null -eq $res){
            $res=Invoke-IABBTimed -ScriptBlock $ScriptBlock -ArgumentList @($ComputerName,$Credential,$proto) -TimeoutSec $TimeoutSec -Name $Name
            $method=('From this PC via RSAT ('+$(if($proto -eq 'Dcom'){'DCOM'}else{'WSMan'})+')')
        }
    }
    if($res.TimedOut){ return [pscustomobject]@{Success=$false;TimedOut=$true;ComputerName=$ComputerName;Error=("Timed out after $TimeoutSec seconds");Method=$method;Notes=@($notes)} }
    $o=@($res.Output)|Select-Object -First 1
    if($null -eq $o){ return [pscustomobject]@{Success=$false;TimedOut=$false;ComputerName=$ComputerName;Error=$(if($res.Error){[string]$res.Error}else{'No data returned'});Method=$method;Notes=@($notes)} }
    $out=$o|Select-Object -Property * -ExcludeProperty PSComputerName,RunspaceId,PSShowComputerName,ModuleMissing
    $out|Add-Member -NotePropertyName ComputerName -NotePropertyValue $ComputerName -Force
    $out|Add-Member -NotePropertyName TimedOut -NotePropertyValue $false -Force
    $out|Add-Member -NotePropertyName Method -NotePropertyValue $method -Force
    $out|Add-Member -NotePropertyName Notes -NotePropertyValue @($notes) -Force
    $out
}
Export-ModuleMember -Function *
