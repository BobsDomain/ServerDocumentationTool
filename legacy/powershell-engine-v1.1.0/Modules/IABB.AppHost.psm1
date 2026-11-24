# IABB.AppHost - Server Documentation Tool v1.1.0
# Entry points used by the Windows app (InfrastructureAsBuiltBuilder.exe).
# The app runs these inside a PowerShell runspace pool, one server per pipeline. Progress messages are sent on the
# Information stream with the tag 'IABBProgress'. The legacy PowerShell launcher (Start-InfrastructureAsBuiltBuilder.ps1)
# does not use this module.

function Complete-IABBAppServer {
    # Saves the RawData JSON and the DOCX for one server, then returns a summary object for the app.
    param([Parameter(Mandatory)]$Result,[string]$RawDataPath,[string]$ReportFolder,[string]$ClientName,[int]$GroupMemberDisplayCap=15,[int]$ApplicationDisplayCap=200,[bool]$IncludeWorkstations=$false)
    $name=[string]$Result.ComputerName
    $jsonErr=$null
    try{ Save-IABBTextFile -Path (Join-Path $RawDataPath ((ConvertTo-IABBSafeFileName $name)+'.json')) -Content ($Result|ConvertTo-Json -Depth 15) }catch{ $jsonErr=$_.Exception.Message }
    $st=if($Result.CollectionStatus){[string]$Result.CollectionStatus.Status}else{'Unknown'}
    $skipped=$false; $doc=$null; $repErr=$null
    if(-not $IncludeWorkstations -and $st -ne 'Failed' -and (Test-IABBWorkstationOS $Result)){ $skipped=$true }
    else{
        $doc=(ConvertTo-IABBSafeFileName $name)+'_AsBuilt.docx'
        try{ New-IABBOpenXmlDocx -ServerResults @($Result) -OutputPath (Join-Path $ReportFolder $doc) -ClientName $ClientName -GroupMemberDisplayCap $GroupMemberDisplayCap -ApplicationDisplayCap $ApplicationDisplayCap }
        catch{ $repErr=$_.Exception.Message; $doc=$null; try{ $Result|Add-Member -NotePropertyName ReportError -NotePropertyValue $repErr -Force }catch{} }
    }
    [pscustomobject]@{
        ComputerName=$name
        Status=$st
        Success=[bool]$Result.Success
        Transport=$(if($Result.CollectionStatus){[string]$Result.CollectionStatus.Transport}else{''})
        Classification=$(if($Result.CollectionStatus){[string]$Result.CollectionStatus.Classification}else{''})
        Roles=((@($Result.RolesDetected)|Where-Object { $_ }) -join ', ')
        NonOkSteps=@(@($Result.SectionTimings)|Where-Object { $_ -and [string]$_.Result -ne 'OK' }|ForEach-Object { [string]$_.Section+' - '+[string]$_.Result+' ('+[string]$_.Seconds+' s)' })
        Warnings=@(@($Result.CollectionWarnings)|Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }|ForEach-Object { [string]$_ })
        DocName=$doc
        ReportError=$repErr
        JsonError=$jsonErr
        SkippedWorkstation=$skipped
        Result=$Result
    }
}

function Invoke-IABBAppServer {
    # Collects one server and writes its JSON and DOCX. Never throws: an unexpected error is recorded as a Failed server.
    param([Parameter(Mandatory)][string]$ComputerName,[Management.Automation.PSCredential]$Credential,[bool]$EnableDcomFallback=$true,[int]$TimeoutSeconds=30,[int]$CimOperationTimeoutSec=180,[int]$SectionTimeoutSec=300,[string]$RawDataPath,[string]$ReportFolder,[string]$ClientName,[int]$GroupMemberDisplayCap=15,[int]$ApplicationDisplayCap=200,[bool]$IncludeWorkstations=$false)
    $cb={ param($m) Write-Information -MessageData ([string]$m) -Tags 'IABBProgress' }
    $r=$null; $err=$null
    try{
        $p=@{ComputerName=$ComputerName;TimeoutSeconds=$TimeoutSeconds;EnableDcomFallback=$EnableDcomFallback;CimOperationTimeoutSec=$CimOperationTimeoutSec;SectionTimeoutSec=$SectionTimeoutSec;OnProgress=$cb}
        if($Credential){ $p.Credential=$Credential }
        $r=Invoke-IABBServerCollection @p
    }catch{ $err=$_.Exception.Message }
    if($null -eq $r){ $r=New-IABBFailedResult -ComputerName $ComputerName -Message $(if($err){$err}else{'Collection returned no result.'}) }
    Write-Information -MessageData 'Writing reports' -Tags 'IABBProgress'
    Complete-IABBAppServer -Result $r -RawDataPath $RawDataPath -ReportFolder $ReportFolder -ClientName $ClientName -GroupMemberDisplayCap $GroupMemberDisplayCap -ApplicationDisplayCap $ApplicationDisplayCap -IncludeWorkstations $IncludeWorkstations
}

function Invoke-IABBAppFailedServer {
    # Used by the app when a server exceeds its time limit: records it as Failed, with JSON and DOCX, without collecting.
    param([Parameter(Mandatory)][string]$ComputerName,[string]$Message,[string]$RawDataPath,[string]$ReportFolder,[string]$ClientName,[int]$GroupMemberDisplayCap=15,[int]$ApplicationDisplayCap=200)
    $r=New-IABBFailedResult -ComputerName $ComputerName -Message $Message
    $r.CollectionStatus.Classification=$Message
    Complete-IABBAppServer -Result $r -RawDataPath $RawDataPath -ReportFolder $ReportFolder -ClientName $ClientName -GroupMemberDisplayCap $GroupMemberDisplayCap -ApplicationDisplayCap $ApplicationDisplayCap -IncludeWorkstations $true
}

function New-IABBAppSummary {
    # HTML run summary. Servers cancelled by the user are added as Failed ("Cancelled by user").
    param([object[]]$Results,[string[]]$CancelledServers,[string]$OutputPath,[string]$ClientName)
    $all=@(@($Results)|Where-Object { $_ })
    foreach($c in @($CancelledServers|Where-Object { $_ })){ $f=New-IABBFailedResult -ComputerName $c -Message 'Cancelled by user before collection finished.'; $f.CollectionStatus.Classification='Cancelled by user'; $all+=$f }
    New-IABBHtmlSummary -ServerResults $all -OutputPath $OutputPath -ClientName $ClientName
}

Export-ModuleMember -Function Complete-IABBAppServer,Invoke-IABBAppServer,Invoke-IABBAppFailedServer,New-IABBAppSummary
