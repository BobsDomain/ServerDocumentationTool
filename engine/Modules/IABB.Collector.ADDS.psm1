# IABB.Collector.ADDS - Server Documentation Tool v1.0.3
# v0.9.9h: collects from the TARGET domain controller (was: the operator PC's own domain) and uses the supplied credentials.
function Invoke-IABBADDSCollection {
    param([string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300,[bool]$UseRemoting=$true)
    $sb={
        param($Server,$Cred,$Protocol)
        $r=[ordered]@{Success=$false;ModuleMissing=$false;Forest=$null;Domain=$null;FSMORoles=$null;DomainControllers=@();Error=$null}
        try{ Import-Module ActiveDirectory -ErrorAction Stop }catch{ $r.ModuleMissing=$true; $r.Error=('ActiveDirectory PowerShell module not available: '+$_.Exception.Message); return [pscustomobject]$r }
        try{
            $p=@{ErrorAction='Stop'}; if($Server){ $p.Server=$Server }; if($Cred){ $p.Credential=$Cred }
            $d=Get-ADDomain @p
            $f=Get-ADForest @p
            $r.Forest=[pscustomobject]@{Name=[string]$f.Name;ForestMode=[string]$f.ForestMode}
            $r.Domain=[pscustomobject]@{DNSRoot=[string]$d.DNSRoot;NetBIOSName=[string]$d.NetBIOSName;DomainMode=[string]$d.DomainMode}
            $r.FSMORoles=[pscustomobject]@{SchemaMaster=[string]$f.SchemaMaster;DomainNamingMaster=[string]$f.DomainNamingMaster;RIDMaster=[string]$d.RIDMaster;PDCEmulator=[string]$d.PDCEmulator;InfrastructureMaster=[string]$d.InfrastructureMaster}
            $r.DomainControllers=@(Get-ADDomainController -Filter * @p | ForEach-Object { [pscustomobject]@{HostName=[string]$_.HostName;Site=[string]$_.Site;OperatingSystem=[string]$_.OperatingSystem;IsGlobalCatalog=[bool]$_.IsGlobalCatalog} })
            $r.Success=$true
        }catch{ $r.Error=$_.Exception.Message }
        [pscustomobject]$r
    }
    Invoke-IABBRoleQuery -ScriptBlock $sb -ComputerName $ComputerName -Credential $Credential -TimeoutSec $TimeoutSec -UseRemoting $UseRemoting -Name 'Active Directory'
}
Export-ModuleMember -Function Invoke-IABBADDSCollection
