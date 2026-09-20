$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$sourceRoot = Join-Path $root "Source"
$files = Get-ChildItem -LiteralPath $sourceRoot -Filter *.cs -File -Recurse

# This intentionally matches the simple method style used by this repository.
# It is a documentation guard, not a general-purpose C# parser.
$methodPattern = '^[ 	]*(public|private|protected|internal)[ 	]+(static[ 	]+)?(extern[ 	]+)?([A-Za-z0-9_<>[],.?]+[ 	]+)+[A-Za-z0-9_]+[ 	]*('

$failures = New-Object System.Collections.Generic.List[string]

foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)

    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -notmatch $methodPattern) { continue }

        $foundIntent = $false
        $j = $i - 1
        while ($j -ge 0 -and [string]::IsNullOrWhiteSpace($lines[$j])) { $j-- }

        # Intent/Workflow comments are normally immediately above the method.
        # Look across a short contiguous comment block so two-line explanations pass.
        $checked = 0
        while ($j -ge 0 -and $checked -lt 4 -and $lines[$j].TrimStart().StartsWith("//")) {
            if ($lines[$j].TrimStart().StartsWith("// Intent:")) {
                $foundIntent = $true
                break
            }
            $j--
            $checked++
        }

        if (-not $foundIntent) {
            $relative = $file.FullName.Substring($root.Length).TrimStart('\','/')
            $failures.Add(("{0}:{1}: missing // Intent: comment above {2}" -f $relative, ($i + 1), $lines[$i].Trim()))
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host ("Source documentation check passed: {0} C# file(s)." -f $files.Count)
exit 0
