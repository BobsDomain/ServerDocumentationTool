<# Infrastructure As-Built Builder Version: 1.0.0 (legacy PowerShell launcher; the app is InfrastructureAsBuiltBuilder.exe) #>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModuleRoot = Join-Path $ScriptRoot 'Modules'
foreach($m in @('IABB.Core.psm1','IABB.Discovery.psm1','IABB.RoleDetection.psm1','IABB.Collector.ADDS.psm1','IABB.Collector.DNS.psm1','IABB.Collector.DHCP.psm1','IABB.Collector.IIS.psm1','IABB.Collector.SQL.psm1','IABB.Collector.Applications.psm1','IABB.Collector.Certificates.psm1','IABB.Collector.Server.psm1','IABB.DocumentBuilder.psm1','IABB.UI.psm1')){
    Import-Module (Join-Path $ModuleRoot $m) -Force
}
Start-IABBApplication -ApplicationRoot $ScriptRoot
