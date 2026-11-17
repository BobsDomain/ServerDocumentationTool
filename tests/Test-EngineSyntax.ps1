# Parses every PowerShell file in the repository with the Windows PowerShell 5.1 parser. Fails on any syntax error.
# Run by GitHub Actions on every push. Also safe to run locally:  powershell -File .\tests\Test-EngineSyntax.ps1
$root = Split-Path -Parent $PSScriptRoot
$files = @(Get-ChildItem -Path $root -Recurse -Include *.ps1,*.psm1 -File | Where-Object { $_.FullName -notmatch '\\(bin|obj|dist)\\' })
$bad = 0
foreach ($f in $files) {
    $tokens = $null; $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
    if (@($errors).Count -gt 0) {
        $bad++
        foreach ($e in $errors) { Write-Host ("SYNTAX ERROR {0}:{1}: {2}" -f $f.FullName.Substring($root.Length + 1), $e.Extent.StartLineNumber, $e.Message) }
    } else {
        Write-Host ("OK  {0}" -f $f.FullName.Substring($root.Length + 1))
    }
}
Write-Host ("Parsed {0} file(s) with PowerShell {1}; {2} with errors." -f $files.Count, $PSVersionTable.PSVersion, $bad)
if ($bad -gt 0) { exit 1 }
exit 0
