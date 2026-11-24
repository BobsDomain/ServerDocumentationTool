# IABB.Discovery - Server Documentation Tool v1.1.0
# v1.1.0: discovery marks computer objects that are not live servers instead of adding them:
#   disabled accounts, cluster name objects (CNO/VCO), and accounts with no logon for more than -StaleDays (default 90; 0 = off).
# In the field test, 13-14 discovered servers never answered (no ping, no WMI). Skipped objects are returned with
# Skipped=$true and a SkipReason so the app can log exactly what it left out.
function Get-IABBDomainServers {
    param([string]$OperatingSystemFilter='Windows Server',[int]$StaleDays=90)
    try{
        Import-Module ActiveDirectory -ErrorAction Stop
        $cut=(Get-Date).AddDays(-[math]::Abs($StaleDays))
        Get-ADComputer -Filter "OperatingSystem -like '*$OperatingSystemFilter*'" -Properties DNSHostName,Enabled,LastLogonTimestamp,ServicePrincipalName,OperatingSystem | Sort-Object Name | ForEach-Object {
            $name=if($_.DNSHostName){[string]$_.DNSHostName}else{[string]$_.Name}
            $last=$null; try{ if($_.LastLogonTimestamp){ $last=[datetime]::FromFileTime([int64]$_.LastLogonTimestamp) } }catch{}
            $reason=$null
            if($_.Enabled -eq $false){ $reason='Disabled computer account' }
            elseif(@(@($_.ServicePrincipalName) | Where-Object { [string]$_ -like 'MSClusterVirtualServer/*' }).Count -gt 0){ $reason='Failover cluster name object (not a server)' }
            elseif($StaleDays -gt 0 -and ($null -eq $last -or $last -lt $cut)){ $reason=('No logon to the domain for more than '+$StaleDays+' days (last logon: '+$(if($last){$last.ToString('yyyy-MM-dd')}else{'never'})+')') }
            [pscustomobject]@{ServerName=$name;Source='Active Directory';Skipped=[bool]$reason;SkipReason=$reason;LastLogon=$(if($last){$last.ToString('yyyy-MM-dd')}else{''});OperatingSystem=[string]$_.OperatingSystem}
        }
    }catch{ [pscustomobject]@{ServerName=$env:COMPUTERNAME;Source='Local fallback';Skipped=$false;SkipReason=$null;LastLogon='';OperatingSystem=''} }
}
function Test-IABBServerReachability { param([string]$ComputerName,[Management.Automation.PSCredential]$Credential) $r=[ordered]@{ComputerName=$ComputerName;Status='Unknown';Error=$null}; try{if(Test-Connection -ComputerName $ComputerName -Count 1 -Quiet){$s=$null;try{$p=@{ComputerName=$ComputerName;ErrorAction='Stop';OperationTimeoutSec=60};if($Credential){$p.Credential=$Credential};$s=New-CimSession @p;Get-CimInstance -CimSession $s Win32_OperatingSystem -OperationTimeoutSec 60 -ErrorAction Stop|Out-Null;$r.Status='Reachable'}finally{if($s){$s|Remove-CimSession -ErrorAction SilentlyContinue}}}else{$r.Status='Ping failed'}}catch{$r.Status='Failed';$r.Error=$_.Exception.Message};[pscustomobject]$r }
Export-ModuleMember -Function *
