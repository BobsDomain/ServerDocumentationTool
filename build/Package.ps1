# Builds the release ZIP and its SHA256 file from the compiled app and the engine.
#   pwsh -File build/Package.ps1 -Version 1.0.0
# Output: dist/InfrastructureAsBuiltBuilder_v<Version>.zip and dist/InfrastructureAsBuiltBuilder_v<Version>.zip.sha256
# The ZIP root is the application folder (the updater extracts it over the installed folder).
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Configuration = 'Release',
    [string]$OutDir = 'dist'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root $OutDir
$stage = Join-Path $out 'stage'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -Path $stage -ItemType Directory -Force | Out-Null

$app = Join-Path $root "src/InfrastructureAsBuiltBuilder/bin/$Configuration/net48"
$upd = Join-Path $root "src/IABB.Updater/bin/$Configuration/net48"
Copy-Item (Join-Path $app 'InfrastructureAsBuiltBuilder.exe') $stage
Copy-Item (Join-Path $upd 'IABB.Updater.exe') $stage
foreach ($cfg in @((Join-Path $app 'InfrastructureAsBuiltBuilder.exe.config'), (Join-Path $upd 'IABB.Updater.exe.config'))) {
    if (Test-Path $cfg) { Copy-Item $cfg $stage }
}
Copy-Item (Join-Path $root 'engine') (Join-Path $stage 'Engine') -Recurse
Copy-Item (Join-Path $root 'Docs') (Join-Path $stage 'Docs') -Recurse
Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'VERSION.txt') $stage

# Never ship the PowerShell reference assembly: Windows' own System.Management.Automation (GAC) must be used.
Get-ChildItem $stage -Recurse -Filter 'System.Management.Automation*.dll' | Remove-Item -Force

foreach ($req in @('InfrastructureAsBuiltBuilder.exe', 'IABB.Updater.exe', 'Engine/Modules/IABB.Core.psm1', 'Engine/Modules/IABB.AppHost.psm1', 'Engine/Modules/IABB.DocumentBuilder.psm1', 'Engine/Config/appsettings.json', 'Engine/Start-InfrastructureAsBuiltBuilder.ps1')) {
    if (-not (Test-Path (Join-Path $stage $req))) { throw "Package is missing $req" }
}
$fileVersion = (Get-Item (Join-Path $stage 'InfrastructureAsBuiltBuilder.exe')).VersionInfo.FileVersion
if (-not $fileVersion.StartsWith($Version)) { throw "InfrastructureAsBuiltBuilder.exe has version $fileVersion, expected $Version" }

$zipName = "InfrastructureAsBuiltBuilder_v$Version.zip"
$zip = Join-Path $out $zipName
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path ($zip + '.sha256') -Value ($hash + '  ' + $zipName) -NoNewline -Encoding ascii
Remove-Item $stage -Recurse -Force
Write-Host "Created $zipName ($([math]::Round((Get-Item $zip).Length / 1KB)) KB), SHA256 $hash"
