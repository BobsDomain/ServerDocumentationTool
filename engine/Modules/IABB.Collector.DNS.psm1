# IABB.Collector.DNS - Infrastructure As-Built Builder v1.0.0
# v0.9.9h: runs on the target DNS server via WinRM; falls back to RSAT on this PC with a CIM session that carries the supplied credentials.
function Invoke-IABBDNSCollection {
    param([string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300,[bool]$UseRemoting=$true)
    $sb={
        param($Server,$Cred,$Protocol)
        $r=[ordered]@{Success=$false;ModuleMissing=$false;Zones=@();Forwarders=@();Error=$null}
        try{ Import-Module DnsServer -ErrorAction Stop }catch{ $r.ModuleMissing=$true; $r.Error=('DnsServer PowerShell module not available: '+$_.Exception.Message); return [pscustomobject]$r }
        $cs=$null
        try{
            $q=@{ErrorAction='Stop'}
            if($Server){
                $opt=New-CimSessionOption -Protocol $Protocol
                $sp=@{ComputerName=$Server;SessionOption=$opt;ErrorAction='Stop'}; if($Cred){ $sp.Credential=$Cred }
                $cs=New-CimSession @sp; $q.CimSession=$cs
            }
            $r.Zones=@(Get-DnsServerZone @q | ForEach-Object { [pscustomobject]@{ZoneName=[string]$_.ZoneName;ZoneType=[string]$_.ZoneType;IsDsIntegrated=[bool]$_.IsDsIntegrated;IsReverseLookupZone=[bool]$_.IsReverseLookupZone;DynamicUpdate=[string]$_.DynamicUpdate} })
            try{ $fw=Get-DnsServerForwarder @q; $r.Forwarders=@(@($fw.IPAddress) | Where-Object { $_ } | ForEach-Object { $_.ToString() }) }catch{}
            $r.Success=$true
        }catch{ $r.Error=$_.Exception.Message }
        finally{ if($cs){ Remove-CimSession -CimSession $cs -ErrorAction SilentlyContinue } }
        [pscustomobject]$r
    }
    Invoke-IABBRoleQuery -ScriptBlock $sb -ComputerName $ComputerName -Credential $Credential -TimeoutSec $TimeoutSec -UseRemoting $UseRemoting -Name 'DNS Server'
}
Export-ModuleMember -Function Invoke-IABBDNSCollection
