# IABB.RoleDetection - Server Documentation Tool v1.1.0
function Invoke-IABBRoleDetection {
    param([Parameter(Mandatory)][object]$ServerData)
    $roles=@(); $notes=@()
    $svcs=@($ServerData.Services|%{[string]$_.Name}); $os=[string]$ServerData.OperatingSystem.Caption
    $features=@($ServerData.RolesFeatures|?{ $_ }|%{[string]$_.Name})
    $domainRole=-1; try{ if($null -ne $ServerData.Overview -and $null -ne $ServerData.Overview.DomainRole -and [string]$ServerData.Overview.DomainRole -ne ''){ $domainRole=[int]$ServerData.Overview.DomainRole } }catch{}
    $isDC=($domainRole -ge 4)
    if($isDC){ $roles+='Domain Controller' }
    elseif($svcs -contains 'NTDS'){
        # v0.9.9i: informational role note (was a Collection Warning)
        $ntds=@($ServerData.Services|?{ [string]$_.Name -eq 'NTDS' })|Select-Object -First 1
        $notes+=("AD DS role files are still installed (NTDS service "+[string]$ntds.State+" / "+[string]$ntds.StartMode+") but this server is a member server, not a domain controller (DomainRole "+$domainRole+"). It was probably a domain controller that has been demoted.")
    }
    if($svcs -contains 'DNS'){$roles+='DNS Server'}
    if($svcs -contains 'DHCPServer'){$roles+='DHCP Server'}
    if($svcs -contains 'CertSvc'){$roles+='AD CS Server'}
    if($svcs -contains 'W3SVC'){$roles+='IIS Server'}
    if(@($svcs|?{ ($_ -eq 'MSSQLSERVER' -or $_ -like 'MSSQL$*') -and $_ -notlike '*MICROSOFT##*' }).Count -gt 0){$roles+='SQL Server'}
    # v0.9.9i: a virtual machine running Hyper-V is labelled as nested
    $isVirtual=[bool]($ServerData.Virtualization -and $ServerData.Virtualization.IsVirtual)
    if($svcs -contains 'vmms'){ if($isVirtual){$roles+='Hyper-V Host (nested)'}else{$roles+='Hyper-V Host'} }
    $runSvc=@($ServerData.Services|?{ [string]$_.State -eq 'Running' }|%{ [string]$_.Name })
    if(-not $isDC){
        if(($features -contains 'FS-DFS-Namespace') -or ($features.Count -eq 0 -and $runSvc -contains 'Dfs')){$roles+='DFS Namespace Server'}
        if(($features -contains 'FS-DFS-Replication') -or ($features.Count -eq 0 -and $runSvc -contains 'DFSR')){$roles+='DFS Replication Server'}
    }
    $isRdsh=($features -contains 'RDS-RD-Server') -or ($ServerData.RdsSessionHost -eq $true)
    if($isRdsh){$roles+='Remote Desktop Session Host'}
    # v0.9.9i: File / Print / DFS roles (were reported as General Purpose Server).
    # File Server only when no other workload role was found: in the field test 100 of 139 servers had at least one
    # non-admin share (backup, audit, client shares), so shares alone would label almost every server a file server.
    $workloadCount=@($roles|?{ [string]$_ -notlike 'DFS *' }).Count   # DFS roles do not count: a DFS server with shares is a file server
    $userShares=@($ServerData.Shares|?{ $_ -and [string]$_.Type -eq '0' -and -not ([string]$_.Name).EndsWith('$') -and ([string]$_.Name) -notin @('SYSVOL','NETLOGON') })
    if($userShares.Count -gt 0 -and $workloadCount -eq 0){$roles+='File Server'}
    if(@($ServerData.Printers|?{ $_ -and [string]$_.Shared -eq 'True' }).Count -gt 0){$roles+='Print Server'}
    if($roles.Count -eq 0){if($os -match 'Windows 10|Windows 11|Enterprise for Virtual Desktops'){$roles+='Workstation'}else{$roles+='General Purpose Server'}}
    [pscustomobject]@{Roles=@($roles|sort -Unique);IsDomainController=$isDC;DomainRole=$domainRole;IsRdsSessionHost=[bool]$isRdsh;Notes=@($notes)}
}
Export-ModuleMember -Function *
