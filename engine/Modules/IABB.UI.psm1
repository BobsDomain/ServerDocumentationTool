# IABB.UI - Server Documentation Tool v1.0.2
function Start-IABBApplication{
param([string]$ApplicationRoot)
Add-Type -AssemblyName System.Windows.Forms;Add-Type -AssemblyName System.Drawing
$config=Get-IABBConfig -ApplicationRoot $ApplicationRoot
function Get-CfgInt($name,[int]$default){ try{ if($config.PSObject.Properties.Name -contains $name -and [int]$config.$name -gt 0){ return [int]$config.$name } }catch{}; $default }
$enableDcom=$true; try{ if($config.PSObject.Properties.Name -contains 'EnableDcomFallback'){ $enableDcom=[bool]$config.EnableDcomFallback } }catch{}
$groupCap=Get-CfgInt 'GroupMemberDisplayCap' 15
$appCap=Get-CfgInt 'ApplicationDisplayCap' 200
$cimTimeout=Get-CfgInt 'CimOperationTimeoutSec' 180
$sectionTimeout=Get-CfgInt 'SectionTimeoutSec' 300
$probeTimeout=Get-CfgInt 'TimeoutSeconds' 30
$form=New-Object Windows.Forms.Form;$form.Text="Server Documentation Tool v$($config.Version)";$form.Size=New-Object Drawing.Size(1260,860);$form.StartPosition='CenterScreen';$form.Font=New-Object Drawing.Font('Segoe UI',9)
function Add-C($c){$form.Controls.Add($c)}
$title=New-Object Windows.Forms.Label;$title.Text='Server Documentation Tool';$title.Font=New-Object Drawing.Font('Segoe UI',18,[Drawing.FontStyle]::Bold);$title.Location=New-Object Drawing.Point(20,12);$title.Size=New-Object Drawing.Size(760,34);Add-C $title
$lblClient=New-Object Windows.Forms.Label;$lblClient.Text='Client Name';$lblClient.Location=New-Object Drawing.Point(25,58);$lblClient.Size=New-Object Drawing.Size(90,22);Add-C $lblClient
$txtClient=New-Object Windows.Forms.TextBox;$txtClient.Location=New-Object Drawing.Point(120,55);$txtClient.Size=New-Object Drawing.Size(300,24);$txtClient.Text='Client';Add-C $txtClient
$lblOutput=New-Object Windows.Forms.Label;$lblOutput.Text='Output Path';$lblOutput.Location=New-Object Drawing.Point(25,88);$lblOutput.Size=New-Object Drawing.Size(90,22);Add-C $lblOutput
$txtOutput=New-Object Windows.Forms.TextBox;$txtOutput.Location=New-Object Drawing.Point(120,85);$txtOutput.Size=New-Object Drawing.Size(700,24);$txtOutput.Text=$config.DefaultOutputRoot;Add-C $txtOutput
$chkCred=New-Object Windows.Forms.CheckBox;$chkCred.Text='Use alternate credentials';$chkCred.Location=New-Object Drawing.Point(840,55);$chkCred.Size=New-Object Drawing.Size(220,24);Add-C $chkCred
$chkDcom=New-Object Windows.Forms.CheckBox;$chkDcom.Text='Enable DCOM fallback';$chkDcom.Location=New-Object Drawing.Point(840,85);$chkDcom.Size=New-Object Drawing.Size(220,24);$chkDcom.Checked=$enableDcom;Add-C $chkDcom
$grpTarget=New-Object Windows.Forms.GroupBox;$grpTarget.Text='Target Servers';$grpTarget.Location=New-Object Drawing.Point(25,118);$grpTarget.Size=New-Object Drawing.Size(1200,96);Add-C $grpTarget
$lblAdd=New-Object Windows.Forms.Label;$lblAdd.Text='Enter server name(s) - separate with comma, space or new line:';$lblAdd.Location=New-Object Drawing.Point(12,22);$lblAdd.Size=New-Object Drawing.Size(430,20);$grpTarget.Controls.Add($lblAdd)
$txtServers=New-Object Windows.Forms.TextBox;$txtServers.Location=New-Object Drawing.Point(12,44);$txtServers.Size=New-Object Drawing.Size(560,44);$txtServers.Multiline=$true;$txtServers.ScrollBars='Vertical';$grpTarget.Controls.Add($txtServers)
$btnAddServers=New-Object Windows.Forms.Button;$btnAddServers.Text='Add Servers';$btnAddServers.Location=New-Object Drawing.Point(584,44);$btnAddServers.Size=New-Object Drawing.Size(110,28);$grpTarget.Controls.Add($btnAddServers)
$btnImport=New-Object Windows.Forms.Button;$btnImport.Text='Import from File...';$btnImport.Location=New-Object Drawing.Point(704,44);$btnImport.Size=New-Object Drawing.Size(130,28);$grpTarget.Controls.Add($btnImport)
$btnAdd=New-Object Windows.Forms.Button;$btnAdd.Text='Add Local Server';$btnAdd.Location=New-Object Drawing.Point(844,44);$btnAdd.Size=New-Object Drawing.Size(120,28);$grpTarget.Controls.Add($btnAdd)
$btnDiscover=New-Object Windows.Forms.Button;$btnDiscover.Text='Discover Domain';$btnDiscover.Location=New-Object Drawing.Point(974,44);$btnDiscover.Size=New-Object Drawing.Size(120,28);$grpTarget.Controls.Add($btnDiscover)
$btnClear=New-Object Windows.Forms.Button;$btnClear.Text='Clear List';$btnClear.Location=New-Object Drawing.Point(1100,44);$btnClear.Size=New-Object Drawing.Size(90,28);$grpTarget.Controls.Add($btnClear)
$btnTest=New-Object Windows.Forms.Button;$btnTest.Text='Test Reachability';$btnTest.Location=New-Object Drawing.Point(25,224);$btnTest.Size=New-Object Drawing.Size(150,32);Add-C $btnTest
$btnRemove=New-Object Windows.Forms.Button;$btnRemove.Text='Remove Selected';$btnRemove.Location=New-Object Drawing.Point(185,224);$btnRemove.Size=New-Object Drawing.Size(150,32);Add-C $btnRemove
$btnStart=New-Object Windows.Forms.Button;$btnStart.Text='Start Assessment';$btnStart.Location=New-Object Drawing.Point(345,224);$btnStart.Size=New-Object Drawing.Size(170,32);Add-C $btnStart
$btnOpen=New-Object Windows.Forms.Button;$btnOpen.Text='Open Output Folder';$btnOpen.Location=New-Object Drawing.Point(525,224);$btnOpen.Size=New-Object Drawing.Size(150,32);$btnOpen.Enabled=$false;Add-C $btnOpen
$lblCount=New-Object Windows.Forms.Label;$lblCount.Text='0 server(s) queued';$lblCount.Location=New-Object Drawing.Point(700,230);$lblCount.Size=New-Object Drawing.Size(500,22);Add-C $lblCount
$grid=New-Object Windows.Forms.DataGridView;$grid.Location=New-Object Drawing.Point(25,268);$grid.Size=New-Object Drawing.Size(1200,330);$grid.AllowUserToAddRows=$false;$grid.AutoSizeColumnsMode='Fill';$grid.RowHeadersVisible=$false;$grid.SelectionMode='FullRowSelect'
$col=New-Object Windows.Forms.DataGridViewCheckBoxColumn;$col.Name='Include';$col.HeaderText='Include';[void]$grid.Columns.Add($col);foreach($c in @('Server','Source','Reachability','DetectedRoles','Status','Transport','Result')){[void]$grid.Columns.Add($c,$c)};Add-C $grid
$progress=New-Object Windows.Forms.ProgressBar;$progress.Location=New-Object Drawing.Point(25,606);$progress.Size=New-Object Drawing.Size(1200,20);Add-C $progress
$log=New-Object Windows.Forms.TextBox;$log.Location=New-Object Drawing.Point(25,634);$log.Size=New-Object Drawing.Size(1200,150);$log.Multiline=$true;$log.ScrollBars='Vertical';Add-C $log
$script:lastOutputFolder=$null;$script:cred=$null;$script:runLogPath=$null
# v0.9.9h: everything shown in the log pane during a run is also written to <run folder>\IABB_Run.log
function Add-UILog([string]$m){$line=(Get-Date -Format 'HH:mm:ss')+' '+$m+[Environment]::NewLine;$log.AppendText($line);if($script:runLogPath){try{[IO.File]::AppendAllText($script:runLogPath,$line)}catch{}};[Windows.Forms.Application]::DoEvents()}
function Update-Count { $n=@($grid.Rows).Count; $lblCount.Text=("{0} server(s) queued   |   CIM query limit {1}s, step limit {2}s" -f $n,$cimTimeout,$sectionTimeout) }
function Get-CredIfNeeded{if($chkCred.Checked){if(-not $script:cred){$script:cred=Get-Credential -Message 'Enter credentials'};return $script:cred};return $null}
function Add-ServerRows($Servers){$added=0;foreach($s in $Servers){$n=[string]$s.ServerName;if([string]::IsNullOrWhiteSpace($n)){continue};$exists=$false;foreach($row in $grid.Rows){if(([string]$row.Cells['Server'].Value).Equals($n,[StringComparison]::OrdinalIgnoreCase)){$exists=$true}};if(-not $exists){[void]$grid.Rows.Add($true,$n,[string]$s.Source,'Not tested','','Queued','','');$added++}};Update-Count;return $added}
function Get-IncludedServers{$arr=@();foreach($row in $grid.Rows){if([bool]$row.Cells['Include'].Value -and -not [string]::IsNullOrWhiteSpace([string]$row.Cells['Server'].Value)){$arr+=[pscustomobject]@{ServerName=([string]$row.Cells['Server'].Value).Trim();Row=$row}}};$arr}
$btnAddServers.Add_Click({try{$servers=@(ConvertFrom-IABBServerText -Text $txtServers.Text);if($servers.Count -eq 0){Add-UILog 'No valid server names entered.';return};$n=Add-ServerRows $servers;Add-UILog ("Added {0} server(s) from manual entry." -f $n);$txtServers.Clear()}catch{Add-UILog "Add error: $($_.Exception.Message)"}})
$btnImport.Add_Click({try{$dlg=New-Object Windows.Forms.OpenFileDialog;$dlg.Filter='Server lists (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*';if($dlg.ShowDialog() -eq [Windows.Forms.DialogResult]::OK){$servers=@(Import-IABBServerList -Path $dlg.FileName);$n=Add-ServerRows $servers;Add-UILog ("Imported {0} server(s) from {1}." -f $n,([IO.Path]::GetFileName($dlg.FileName)))}}catch{Add-UILog "Import error: $($_.Exception.Message)"}})
$btnAdd.Add_Click({$n=Add-ServerRows @([pscustomobject]@{ServerName=$env:COMPUTERNAME;Source='Local'});Add-UILog "Added local server: $env:COMPUTERNAME"})
$btnDiscover.Add_Click({try{Add-UILog 'Discovering domain servers...';$n=Add-ServerRows (Get-IABBDomainServers -OperatingSystemFilter $config.ServerOperatingSystemFilter);Add-UILog ("Added {0} server(s) from Active Directory." -f $n)}catch{Add-UILog "Domain discovery failed: $($_.Exception.Message)"}})
$btnClear.Add_Click({$grid.Rows.Clear();Update-Count;Add-UILog 'Cleared server list.'})
$btnRemove.Add_Click({$toRemove=@();foreach($row in $grid.SelectedRows){$toRemove+=$row};if($toRemove.Count -eq 0){Add-UILog 'No rows selected to remove.';return};foreach($row in $toRemove){$grid.Rows.Remove($row)};Update-Count;Add-UILog ("Removed {0} server(s)." -f $toRemove.Count)})
$btnTest.Add_Click({try{$servers=@(Get-IncludedServers);if($servers.Count -eq 0){throw 'No included servers to test.'};$cred=Get-CredIfNeeded;foreach($e in $servers){Add-UILog ("Testing "+$e.ServerName+" ...");$rd=Test-IABBCollectionReadiness -ComputerName $e.ServerName -Credential $cred -EnableDcomFallback ([bool]$chkDcom.Checked) -OperationTimeoutSec ([math]::Max(30,$probeTimeout));$e.Row.Cells['Reachability'].Value=$rd.Classification;$e.Row.Cells['Transport'].Value=$rd.Transport;$e.Row.Cells['Result'].Value=if($rd.Reachable){'Ready ('+$rd.Transport+')'}else{$rd.Classification};if(-not $rd.Reachable -and $rd.Detail){Add-UILog ("  "+$e.ServerName+": "+$rd.Detail)};if($rd.Transport -eq 'DCOM' -and $rd.WsmanError){Add-UILog ("  "+$e.ServerName+": WinRM failed, DCOM only - "+$rd.WsmanError)}};Add-UILog 'Readiness probe complete.'}catch{Add-UILog "Readiness Error: $($_.Exception.Message)"}})
$btnOpen.Add_Click({if($script:lastOutputFolder -and (Test-Path $script:lastOutputFolder)){Start-Process explorer.exe $script:lastOutputFolder}})
$btnStart.Add_Click({try{$servers=@(Get-IncludedServers);if($servers.Count -eq 0){throw 'No included servers selected.'};$btnStart.Enabled=$false;$run=New-IABBRunFolder -OutputRoot $txtOutput.Text.Trim() -ClientName $txtClient.Text.Trim();$script:lastOutputFolder=$run.Root
$script:runLogPath=Join-Path $run.Root 'IABB_Run.log'
Add-UILog "Created output folder: $($run.Root)"
$cred=Get-CredIfNeeded;$useDcom=[bool]$chkDcom.Checked
Add-UILog ("Server Documentation Tool v{0} - run started by {1}\{2} on {3}" -f $config.Version,$env:USERDOMAIN,$env:USERNAME,$env:COMPUTERNAME)
Add-UILog ("Client: {0} | Credentials: {1} | DCOM fallback: {2} | CIM query limit {3}s | step limit {4}s" -f $run.ClientName,$(if($cred){$cred.UserName}else{'current user'}),$useDcom,$cimTimeout,$sectionTimeout)
Add-UILog ("Servers ({0}): {1}" -f $servers.Count,((@($servers|%{ $_.ServerName })) -join ', '))
$results=@();$i=0;$nComplete=0;$nPartial=0;$nFailed=0
foreach($e in $servers){
    $row=$e.Row;$row.Cells['Status'].Value='Running';Add-UILog "Processing server: $($e.ServerName)"
    $logBox=$log;$gridRow=$row;$logFile=$script:runLogPath
    $cb={ param($m) try{ $gridRow.Cells['Status'].Value=('Running: '+$m) }catch{}; $line=(Get-Date -Format 'HH:mm:ss')+'    ... '+$m+[Environment]::NewLine; $logBox.AppendText($line); if($logFile){ try{ [IO.File]::AppendAllText($logFile,$line) }catch{} }; [Windows.Forms.Application]::DoEvents() }.GetNewClosure()
    # v0.9.9h: an unexpected error on one server no longer stops the run - the server is recorded as Failed and the run continues
    $r=$null
    try{ $r=Invoke-IABBServerCollection -ComputerName $e.ServerName -TimeoutSeconds $probeTimeout -Credential $cred -EnableDcomFallback $useDcom -CimOperationTimeoutSec $cimTimeout -SectionTimeoutSec $sectionTimeout -OnProgress $cb }
    catch{ Add-UILog ("  ERROR: collection for "+$e.ServerName+" stopped unexpectedly: "+$_.Exception.Message); $r=$null; $errMsg=$_.Exception.Message }
    if($null -eq $r){ $r=New-IABBFailedResult -ComputerName $e.ServerName -Message $(if($errMsg){$errMsg}else{'Collection returned no result.'}) }
    $errMsg=$null
    $results+=$r
    try{ Save-IABBTextFile -Path (Join-Path $run.RawData ((ConvertTo-IABBSafeFileName $e.ServerName)+'.json')) -Content ($r|ConvertTo-Json -Depth 15) }catch{ Add-UILog ("  WARN: could not save RawData JSON: "+$_.Exception.Message) }
    $st=if($r.CollectionStatus){$r.CollectionStatus.Status}else{'Unknown'};switch($st){'Complete'{$nComplete++}'Partial'{$nPartial++}'Failed'{$nFailed++}}
    $row.Cells['DetectedRoles'].Value=((@($r.RolesDetected))-join ', ');$row.Cells['Status'].Value=$st;$row.Cells['Transport'].Value=if($r.CollectionStatus){$r.CollectionStatus.Transport}else{''}
    $row.Cells['Result'].Value=if($r.Success){if($st -eq 'Partial'){'Partial - see warnings'}else{'Success'}}else{[string]$r.CollectionStatus.Classification}
    Add-UILog ("  -> "+$st+" ["+([string]$r.CollectionStatus.Transport)+"] "+([string]$r.CollectionStatus.Classification))
    foreach($t in @($r.SectionTimings|?{ $_ -and $_.Result -ne 'OK' })){ Add-UILog ("  STEP: "+$t.Section+" - "+$t.Result+" ("+$t.Seconds+" s)") }
    foreach($cw in @($r.CollectionWarnings)){Add-UILog ("  WARN: "+$cw)}
    $i++;$progress.Value=[math]::Min(100,[math]::Round(($i/$servers.Count)*100))
}
$reportResults=if($config.IncludeWorkstations){@($results)}else{@($results|?{ ($_.CollectionStatus -and $_.CollectionStatus.Status -eq 'Failed') -or -not (Test-IABBWorkstationOS $_) })}
Add-UILog ("Generating reports for {0} server(s)." -f @($reportResults).Count)
# v0.9.9h: each DOCX is generated on its own - one failure no longer stops the remaining reports or the run summary
$nDocOk=0;$nDocFail=0
foreach($srv in @($reportResults)){
    $docName=(ConvertTo-IABBSafeFileName $srv.ComputerName)+'_AsBuilt.docx'
    try{ New-IABBOpenXmlDocx -ServerResults @($srv) -OutputPath (Join-Path $run.Root $docName) -ClientName $run.ClientName -GroupMemberDisplayCap $groupCap -ApplicationDisplayCap $appCap; $nDocOk++; Add-UILog "Created DOCX: $docName" }
    catch{ $nDocFail++; $msg=$_.Exception.Message; try{ $srv|Add-Member -NotePropertyName ReportError -NotePropertyValue $msg -Force }catch{}; Add-UILog ("  ERROR: DOCX for "+$srv.ComputerName+" could not be generated: "+$msg) }
}
$htmlName="$($run.ClientName)_Server_Summary.html"
try{ New-IABBHtmlSummary -ServerResults @($reportResults) -OutputPath (Join-Path $run.Root $htmlName) -ClientName $run.ClientName; Add-UILog "Created HTML summary: $htmlName" }catch{ Add-UILog ("  ERROR: HTML summary could not be generated: "+$_.Exception.Message) }
Add-UILog ("Run summary: {0} Complete, {1} Partial, {2} Failed. Reports: {3} DOCX created, {4} DOCX failed." -f $nComplete,$nPartial,$nFailed,$nDocOk,$nDocFail)
Add-UILog ("Completed. Output files and run log are in: "+$run.Root);$btnOpen.Enabled=$true;$progress.Value=0}catch{Add-UILog "ERROR: $($_.Exception.Message)"}finally{$btnStart.Enabled=$true;$script:runLogPath=$null}})
Update-Count
Add-UILog 'Ready. Add target servers by name, import a list, add the local server, or discover the domain.'
[void]$form.ShowDialog()
}
Export-ModuleMember -Function Start-IABBApplication
