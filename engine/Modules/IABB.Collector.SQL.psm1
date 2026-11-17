# IABB.Collector.SQL - Infrastructure As-Built Builder v1.0.0
function Invoke-IABBSqlCollection {
    param([string]$ComputerName,[Management.Automation.PSCredential]$Credential,[int]$TimeoutSec=300)
    $sb={
        $out=[ordered]@{Success=$false;Instances=@();Error=$null}
        try{
            $roots=@(@('HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server','64-bit'),@('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Microsoft SQL Server','32-bit'))
            $skip=@('PSPath','PSParentPath','PSChildName','PSDrive','PSProvider')
            $rows=@(); $seen=@{}
            foreach($rt in $roots){
                $base=$rt[0]; $arch=$rt[1]
                $key=Join-Path $base 'Instance Names\SQL'
                if(-not (Test-Path $key)){ continue }
                $names=Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
                if(-not $names){ continue }
                foreach($p in $names.PSObject.Properties){
                    if($skip -contains $p.Name){ continue }
                    $inst=[string]$p.Name; $id=[string]$p.Value
                    if($inst -like '*MICROSOFT##*'){ continue }
                    if($seen.ContainsKey($inst.ToUpperInvariant())){ continue }
                    $seen[$inst.ToUpperInvariant()]=$true
                    $setup=Get-ItemProperty -Path (Join-Path $base "$id\Setup") -ErrorAction SilentlyContinue
                    $tcp=Get-ItemProperty -Path (Join-Path $base "$id\MSSQLServer\SuperSocketNetLib\Tcp\IPAll") -ErrorAction SilentlyContinue
                    $port=''
                    if($tcp){
                        $static=([string]$tcp.TcpPort).Trim(); $dyn=([string]$tcp.TcpDynamicPorts).Trim()
                        if($static -ne ''){ $port=$static }
                        elseif($dyn -ne ''){ if($dyn -eq '0'){ $port='Dynamic' } else { $port='Dynamic ('+$dyn+')' } }
                    }
                    $svc=if($inst -eq 'MSSQLSERVER'){'MSSQLSERVER'}else{'MSSQL$'+$inst}
                    $agent=if($inst -eq 'MSSQLSERVER'){'SQLSERVERAGENT'}else{'SQLAgent$'+$inst}
                    $rows+=[pscustomobject]@{Instance=$inst;InstanceId=$id;Version=[string]$setup.Version;PatchLevel=[string]$setup.PatchLevel;Edition=[string]$setup.Edition;DataRoot=[string]$setup.SQLDataRoot;TcpPort=$port;ServiceName=$svc;AgentServiceName=$agent;Architecture=$arch}
                }
            }
            $out.Instances=@($rows)
            $out.Success=$true
        }catch{ $out.Error=$_.Exception.Message }
        [pscustomobject]$out
    }
    $res=Invoke-IABBTimed -ScriptBlock $sb -ComputerName $ComputerName -Credential $Credential -TimeoutSec $TimeoutSec -Name 'SQL Server'
    if($res.TimedOut){ return [pscustomobject]@{Success=$false;TimedOut=$true;Instances=@();Error="Timed out after $TimeoutSec seconds"} }
    $o=@($res.Output)|select -First 1
    if(-not $o){ return [pscustomobject]@{Success=$false;TimedOut=$false;Instances=@();Error=[string]$res.Error} }
    [pscustomobject]@{Success=[bool]$o.Success;TimedOut=$false;Instances=@($o.Instances|?{ $_ }|%{[pscustomobject]@{Instance=$_.Instance;InstanceId=$_.InstanceId;Version=$_.Version;PatchLevel=$_.PatchLevel;Edition=$_.Edition;DataRoot=$_.DataRoot;TcpPort=$_.TcpPort;ServiceName=$_.ServiceName;AgentServiceName=$_.AgentServiceName;Architecture=$_.Architecture}});Error=$o.Error}
}
Export-ModuleMember -Function Invoke-IABBSqlCollection
