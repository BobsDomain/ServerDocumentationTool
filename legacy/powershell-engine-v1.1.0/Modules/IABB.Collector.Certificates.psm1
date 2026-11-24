# IABB.Collector.Certificates - Server Documentation Tool v1.1.0
function Invoke-IABBCertificateCollection {
    param([string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300)
    $r=[ordered]@{Success=$false;ComputerName=$ComputerName;Certificates=@();Summary=$null;Error=$null;TimedOut=$false}
    $sb={ $stores=@('My','Root','CA','WebHosting');$now=Get-Date;foreach($store in $stores){Get-ChildItem "Cert:\LocalMachine\$store" -ErrorAction SilentlyContinue|%{[pscustomobject]@{Store=$store;FriendlyName=[string]$_.FriendlyName;Subject=[string]$_.Subject;Issuer=[string]$_.Issuer;Thumbprint=[string]$_.Thumbprint;NotAfter=$_.NotAfter;DaysUntilExpiry=[math]::Round(($_.NotAfter-$now).TotalDays,0);HasPrivateKey=[bool]$_.HasPrivateKey}}}}
    $res=Invoke-IABBTimed -ScriptBlock $sb -ComputerName $ComputerName -Credential $Credential -TimeoutSec $TimeoutSec -Name 'Certificates'
    $certs=@($res.Output|?{ $_ -and $_.Thumbprint })
    $r.Certificates=$certs
    $r.Summary=[ordered]@{TotalCertificates=$certs.Count;ExpiringWithin60Days=@($certs|?{$_.DaysUntilExpiry -ge 0 -and $_.DaysUntilExpiry -le 60}).Count;Expired=@($certs|?{$_.DaysUntilExpiry -lt 0}).Count}
    if($res.TimedOut){ $r.TimedOut=$true; $r.Error="Timed out after $TimeoutSec seconds" } elseif($res.Error -and $certs.Count -eq 0){ $r.Error=$res.Error } else { $r.Success=$true }
    [pscustomobject]$r
}
Export-ModuleMember -Function *
