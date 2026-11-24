# IABB.Collector.DNS - Server Documentation Tool v1.1.0
# v0.9.9h: runs on the target DNS server via WinRM; falls back to RSAT on this PC with a CIM session that carries the supplied credentials.
# v1.1.0: zones and forwarders are read from the DNS Server WMI provider (root\MicrosoftDNS) with a hard per-query time limit,
#         so a DNS server that does not answer fails within about 2 minutes with a clear reason. Before, the DnsServer
#         cmdlets could hang until the 300 s step limit (seen on one DC in the field). The DnsServer module is the fallback
#         when the WMI provider is unavailable.
function Invoke-IABBDNSCollection {
    param([string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300,[bool]$UseRemoting=$true)
    $sb={
        param($Server,$Cred,$Protocol)
        $r=[ordered]@{Success=$false;ModuleMissing=$false;Zones=@();Forwarders=@();Source=$null;Error=$null}
        $T=120
        $cs=$null
        try{
            $q=@{ErrorAction='Stop';OperationTimeoutSec=$T}
            if($Server){
                $opt=New-CimSessionOption -Protocol $Protocol
                $sp=@{ComputerName=$Server;SessionOption=$opt;OperationTimeoutSec=$T;ErrorAction='Stop'}; if($Cred){ $sp.Credential=$Cred }
                $cs=New-CimSession @sp; $q.CimSession=$cs
            }
            $wmiOk=$false; $wmiErr=$null
            try{
                $zt=@{'1'='Primary';'2'='Secondary';'3'='Stub';'4'='Forwarder'}
                $du=@{'0'='None';'1'='NonsecureAndSecure';'2'='Secure'}
                $zones=@(Get-CimInstance @q -Namespace 'root\MicrosoftDNS' -ClassName MicrosoftDNS_Zone)
                $r.Zones=@($zones | Where-Object { [string]$_.ZoneType -ne '0' -and -not ([string]$_.Name).StartsWith('..') } | Sort-Object Name | ForEach-Object {
                    $t=[string]$_.ZoneType; $u=[string]$_.AllowUpdate
                    [pscustomobject]@{ZoneName=[string]$_.Name;ZoneType=$(if($zt.ContainsKey($t)){$zt[$t]}else{$t});IsDsIntegrated=[bool]$_.DsIntegrated;IsReverseLookupZone=[bool]$_.Reverse;DynamicUpdate=$(if($du.ContainsKey($u)){$du[$u]}else{$u})}
                })
                $r.Source='DNS WMI provider (root\MicrosoftDNS)'
                $wmiOk=$true
                try{
                    $srv=@(Get-CimInstance @q -Namespace 'root\MicrosoftDNS' -ClassName MicrosoftDNS_Server) | Select-Object -First 1
                    $r.Forwarders=@(@($srv.Forwarders) | Where-Object { $_ } | ForEach-Object { [string]$_ })
                }catch{}
            }catch{ $wmiErr=$_.Exception.Message }
            if(-not $wmiOk){
                if($wmiErr -match 'time'){
                    # The DNS service did not answer in time: do not fall back to the DnsServer cmdlets, which would hang as well.
                    throw ("The DNS Server did not answer the zone query within $T seconds (DNS WMI provider not responding): "+$wmiErr)
                }
                try{ Import-Module DnsServer -ErrorAction Stop }catch{ $r.ModuleMissing=$true; $r.Error=('DNS WMI provider unavailable ('+$wmiErr+') and the DnsServer PowerShell module is not available: '+$_.Exception.Message); return [pscustomobject]$r }
                $q2=@{ErrorAction='Stop'}; if($cs){ $q2.CimSession=$cs }
                $r.Zones=@(Get-DnsServerZone @q2 | ForEach-Object { [pscustomobject]@{ZoneName=[string]$_.ZoneName;ZoneType=[string]$_.ZoneType;IsDsIntegrated=[bool]$_.IsDsIntegrated;IsReverseLookupZone=[bool]$_.IsReverseLookupZone;DynamicUpdate=[string]$_.DynamicUpdate} })
                try{ $fw=Get-DnsServerForwarder @q2; $r.Forwarders=@(@($fw.IPAddress) | Where-Object { $_ } | ForEach-Object { $_.ToString() }) }catch{}
                $r.Source='DnsServer PowerShell module'
            }
            $r.Success=$true
        }catch{ $r.Error=$_.Exception.Message }
        finally{ if($cs){ Remove-CimSession -CimSession $cs -ErrorAction SilentlyContinue } }
        [pscustomobject]$r
    }
    Invoke-IABBRoleQuery -ScriptBlock $sb -ComputerName $ComputerName -Credential $Credential -TimeoutSec $TimeoutSec -UseRemoting $UseRemoting -Name 'DNS Server'
}
Export-ModuleMember -Function Invoke-IABBDNSCollection
