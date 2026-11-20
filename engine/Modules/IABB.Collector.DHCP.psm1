# IABB.Collector.DHCP - Server Documentation Tool v1.0.3
# v0.9.9h: runs on the target DHCP server via WinRM; falls back to RSAT on this PC with a CIM session that carries the supplied credentials.
function Invoke-IABBDHCPCollection {
    param([string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300,[bool]$UseRemoting=$true)
    $sb={
        param($Server,$Cred,$Protocol)
        $r=[ordered]@{Success=$false;ModuleMissing=$false;Scopes=@();Reservations=@();ServerOptions=@();Status='Not collected';Error=$null;Recommendation=$null}
        try{ Import-Module DhcpServer -ErrorAction Stop }catch{ $r.ModuleMissing=$true; $r.Error=('DhcpServer PowerShell module not available: '+$_.Exception.Message); $r.Recommendation='Install the DHCP Server management tools (RSAT) on the target or on this PC.'; return [pscustomobject]$r }
        $cs=$null
        try{
            $q=@{ErrorAction='Stop'}
            if($Server){
                $opt=New-CimSessionOption -Protocol $Protocol
                $sp=@{ComputerName=$Server;SessionOption=$opt;ErrorAction='Stop'}; if($Cred){ $sp.Credential=$Cred }
                $cs=New-CimSession @sp; $q.CimSession=$cs
            }
            $r.Scopes=@(Get-DhcpServerv4Scope @q | ForEach-Object { [pscustomobject]@{ScopeId=[string]$_.ScopeId;Name=[string]$_.Name;StartRange=[string]$_.StartRange;EndRange=[string]$_.EndRange;State=[string]$_.State} })
            $r.Success=$true; $r.Status='Collected'
        }catch{ $r.Error=$_.Exception.Message; $r.Recommendation='Check DHCP Server management tools and that the account is a member of DHCP Administrators or local Administrators on the target.' }
        finally{ if($cs){ Remove-CimSession -CimSession $cs -ErrorAction SilentlyContinue } }
        [pscustomobject]$r
    }
    Invoke-IABBRoleQuery -ScriptBlock $sb -ComputerName $ComputerName -Credential $Credential -TimeoutSec $TimeoutSec -UseRemoting $UseRemoting -Name 'DHCP Server'
}
Export-ModuleMember -Function Invoke-IABBDHCPCollection
