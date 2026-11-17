# IABB.Collector.Applications - Server Documentation Tool v1.0.2
function Invoke-IABBApplicationCollection {
    param([string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300)
    # v0.9.9i: Applications = what Programs and Features shows. System components and updates are kept separately in HiddenEntries (RawData JSON only).
    $r=[ordered]@{Success=$false;ComputerName=$ComputerName;Applications=@();HiddenEntries=@();Summary=$null;Error=$null;TimedOut=$false}
    $sb={
        $paths=@('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
        foreach($p in $paths){
            Get-ItemProperty $p -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName } | ForEach-Object {
                $arch=if($p -like '*WOW6432Node*'){'32-bit'}else{'64-bit'}
                $raw=([string]$_.InstallDate).Trim(); $iso=$raw
                if($raw -match '^(\d{4})(\d{2})(\d{2})$'){ $iso=($Matches[1]+'-'+$Matches[2]+'-'+$Matches[3]) }
                $dn=[string]$_.DisplayName
                $hidden=([string]$_.SystemComponent -eq '1') -or (-not [string]::IsNullOrWhiteSpace([string]$_.ParentKeyName)) -or ([string]$_.ReleaseType -match 'Update|Hotfix') -or ($dn -match '^(Security )?Update for |^Hotfix for ')
                [pscustomobject]@{Name=$dn;Publisher=[string]$_.Publisher;Version=[string]$_.DisplayVersion;InstallDate=$iso;Architecture=$arch;Hidden=[bool]$hidden}
            }
        }
    }
    $res=Invoke-IABBTimed -ScriptBlock $sb -ComputerName $ComputerName -Credential $Credential -TimeoutSec $TimeoutSec -Name 'Applications'
    $apps=@($res.Output|?{ $_ -and $_.Name })
    $r.Applications=@($apps|?{ -not $_.Hidden }|sort Name,Version -Unique|Select-Object Name,Publisher,Version,InstallDate,Architecture)
    $r.HiddenEntries=@($apps|?{ $_.Hidden }|sort Name,Version -Unique|Select-Object Name,Publisher,Version,InstallDate,Architecture)
    $r.Summary=[ordered]@{TotalApplications=@($r.Applications).Count;Publishers=@($r.Applications|?{ $_.Publisher }|%{ $_.Publisher }|select -Unique).Count;HiddenEntries=@($r.HiddenEntries).Count}
    if($res.TimedOut){ $r.TimedOut=$true; $r.Error="Timed out after $TimeoutSec seconds" } elseif($res.Error -and $apps.Count -eq 0){ $r.Error=$res.Error } else { $r.Success=$true }
    [pscustomobject]$r
}
Export-ModuleMember -Function *
