# IABB.Collector.IIS - Server Documentation Tool v1.0.3
function Invoke-IABBIISCollection {
    param([string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300)
    $sb={
        $r=[ordered]@{Success=$false;Version=$null;Sites=@();AppPools=@();Bindings=@();VirtualDirectories=@();Error=$null}
        # v0.9.9h: host name is the third part of bindingInformation (ip:port:host); IPv6 addresses keep their brackets
        function Get-BindingHost([string]$Info){ $m=[regex]::Match([string]$Info,'^(.*):(\d+):(.*)$'); if($m.Success){ return $m.Groups[3].Value }; return '' }
        try{
            $useWeb=$false
            try{ Import-Module WebAdministration -ErrorAction Stop; $useWeb=$true }catch{ $useWeb=$false }
            try{ $v=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\InetStp' -ErrorAction SilentlyContinue); if($v){ $r.Version=("$($v.MajorVersion).$($v.MinorVersion)") } }catch{}
            if($useWeb){
                $r.AppPools=@(Get-ChildItem IIS:\AppPools -ErrorAction SilentlyContinue | ForEach-Object { [ordered]@{Name=[string]$_.Name;State=[string]$_.State;ManagedRuntimeVersion=[string]$_.ManagedRuntimeVersion;ManagedPipelineMode=[string]$_.ManagedPipelineMode;Identity=[string]$_.ProcessModel.IdentityType} })
                $sites=@(Get-ChildItem IIS:\Sites -ErrorAction SilentlyContinue)
                $siteRows=@();$bindRows=@();$vdirRows=@()
                foreach($site in $sites){
                    $siteRows+=[ordered]@{Name=[string]$site.Name;ID=[string]$site.ID;State=[string]$site.State;PhysicalPath=[string]$site.PhysicalPath;AppPool=[string]$site.applicationPool}
                    foreach($b in @($site.Bindings.Collection)){ $bindRows+=[ordered]@{Site=[string]$site.Name;Protocol=[string]$b.protocol;BindingInformation=[string]$b.bindingInformation;Hostname=(Get-BindingHost ([string]$b.bindingInformation));SslFlags=[string]$b.sslFlags} }
                    try{ foreach($app in @(Get-WebApplication -Site $site.Name -ErrorAction SilentlyContinue)){ $vdirRows+=[ordered]@{Site=[string]$site.Name;Path=[string]$app.path;PhysicalPath=[string]$app.PhysicalPath;AppPool=[string]$app.applicationPool} } }catch{}
                }
                $r.Sites=@($siteRows);$r.Bindings=@($bindRows);$r.VirtualDirectories=@($vdirRows)
            } else {
                $appcmd=Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
                if(Test-Path $appcmd){
                    $siteRows=@();$bindRows=@()
                    $raw=& $appcmd list site 2>$null
                    foreach($line in @($raw)){ $m=[regex]::Match($line,'SITE\s+"([^"]+)"\s+\(id:(\d+),bindings:(.*?),state:(\w+)\)'); if($m.Success){ $sn=$m.Groups[1].Value;$siteRows+=[ordered]@{Name=$sn;ID=$m.Groups[2].Value;State=$m.Groups[4].Value;PhysicalPath='';AppPool=''}; foreach($bd in @($m.Groups[3].Value -split ',' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })){ $bi=(($bd -split '/',2)[1]); $bindRows+=[ordered]@{Site=$sn;Protocol=($bd -split '/')[0];BindingInformation=$bi;Hostname=$(if(($bd -split '/')[0] -match '^https?$'){Get-BindingHost $bi}else{''});SslFlags=''} } } }
                    $poolRows=@()
                    $rawp=& $appcmd list apppool 2>$null
                    foreach($line in @($rawp)){ $m=[regex]::Match($line,'APPPOOL\s+"([^"]+)"\s+\(MgdVersion:([^,]*),MgdMode:([^,]*),state:(\w+)\)'); if($m.Success){ $poolRows+=[ordered]@{Name=$m.Groups[1].Value;State=$m.Groups[4].Value;ManagedRuntimeVersion=$m.Groups[2].Value;ManagedPipelineMode=$m.Groups[3].Value;Identity=''} } }
                    $r.Sites=@($siteRows);$r.Bindings=@($bindRows);$r.AppPools=@($poolRows)
                }
            }
            $r.Success=$true
        }catch{ $r.Error=$_.Exception.Message }
        [pscustomobject]$r
    }
    $res=Invoke-IABBTimed -ScriptBlock $sb -ComputerName $ComputerName -Credential $Credential -TimeoutSec $TimeoutSec -Name 'IIS'
    $o=@($res.Output)|select -First 1
    if($res.TimedOut){ return [pscustomobject]@{Success=$false;TimedOut=$true;Version=$null;Sites=@();AppPools=@();Bindings=@();VirtualDirectories=@();Error="Timed out after $TimeoutSec seconds"} }
    if(-not $o){ return [pscustomobject]@{Success=$false;TimedOut=$false;Version=$null;Sites=@();AppPools=@();Bindings=@();VirtualDirectories=@();Error=[string]$res.Error} }
    $o
}
Export-ModuleMember -Function Invoke-IABBIISCollection
