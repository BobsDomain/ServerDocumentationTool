# Builds the release files from the compiled app.
#   pwsh -File build/Package.ps1 -Version 2.0.0
# Output: dist/ServerDocumentationTool.exe and dist/ServerDocumentationTool.exe.sha256
# The exe is the whole application (no Engine folder, no updater, no .config file).
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Configuration = 'Release',
    [string]$OutDir = 'dist'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root $OutDir
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -Path $out -ItemType Directory -Force | Out-Null

$name = 'ServerDocumentationTool.exe'
$src = Join-Path $root "src/ServerDocumentationTool/bin/$Configuration/net48/$name"
if (-not (Test-Path $src)) { throw "Build output not found: $src" }
$exe = Join-Path $out $name
Copy-Item $src $exe

$vi = (Get-Item $exe).VersionInfo
if (-not $vi.FileVersion.StartsWith($Version)) { throw "$name has version $($vi.FileVersion), expected $Version" }
if ($vi.ProductName -ne 'Server Documentation Tool') { throw "$name has product name '$($vi.ProductName)'" }

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path ($exe + '.sha256') -Value ($hash + '  ' + $name) -NoNewline -Encoding ascii
Write-Host "Created $name v$Version ($([math]::Round((Get-Item $exe).Length / 1KB)) KB), SHA256 $hash"
