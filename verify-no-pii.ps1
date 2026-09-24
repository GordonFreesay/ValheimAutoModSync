param(
    [string]$Root = (Split-Path -Parent $MyInvocation.MyCommand.Path),
    [string]$ArtifactPaths = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync first-party privacy guard.
# Intent: prevent machine/user-specific identifiers from entering tracked first-party source or AutoModSync-authored build artifacts.
# Scope: third-party license attribution is intentionally excluded because it must remain verbatim for legal compliance.
# This guard does not classify the public AutoModSync/GordonFreesay project brand, repository URLs, or website URL as private PII.

$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
$failures = New-Object 'System.Collections.Generic.List[string]'

function Add-Failure([string]$Location,[string]$Kind,[string]$Value) {
    [void]$failures.Add(($Location + ': ' + $Kind + ' -> ' + $Value))
}

function Test-PublicIpv4([string]$Value) {
    $parts = $Value.Split('.')
    if ($parts.Count -ne 4) { return $false }
    $n = @()
    foreach ($part in $parts) {
        $v = 0
        if (-not [Int32]::TryParse($part,[ref]$v) -or $v -lt 0 -or $v -gt 255) { return $false }
        $n += $v
    }

    # Non-public/documentation ranges are allowed in source examples.
    if ($n[0] -eq 10) { return $false }
    if ($n[0] -eq 127) { return $false }
    if ($n[0] -eq 0) { return $false }
    if ($n[0] -eq 169 -and $n[1] -eq 254) { return $false }
    if ($n[0] -eq 172 -and $n[1] -ge 16 -and $n[1] -le 31) { return $false }
    if ($n[0] -eq 192 -and $n[1] -eq 168) { return $false }
    if ($n[0] -eq 100 -and $n[1] -ge 64 -and $n[1] -le 127) { return $false }
    if ($n[0] -eq 192 -and $n[1] -eq 0 -and $n[2] -eq 2) { return $false }
    if ($n[0] -eq 198 -and $n[1] -eq 51 -and $n[2] -eq 100) { return $false }
    if ($n[0] -eq 203 -and $n[1] -eq 0 -and $n[2] -eq 113) { return $false }
    if ($n[0] -ge 224) { return $false }
    return $true
}

function Scan-Text([string]$Text,[string]$Location) {
    if ([String]::IsNullOrEmpty($Text)) { return }

    foreach ($m in [regex]::Matches($Text,'(?i)\b[A-Z]:\\Users\\([^\\\r\n]+)\\')) {
        $profile = $m.Groups[1].Value
        if ($profile -notmatch '^(Public|Default|Default User|All Users|<[^>]+>|%[^%]+%|\$\{?[^}\\]+\}?)$') {
            Add-Failure $Location 'literal Windows user-profile path' $m.Value
        }
    }

    foreach ($m in [regex]::Matches($Text,'(?i)(?:/Users|/home)/([^/\s]+)/')) {
        $profile = $m.Groups[1].Value
        if ($profile -notmatch '^(user|username|runner|<[^>]+>|\$\{?[^}]+\}?)$') {
            Add-Failure $Location 'literal POSIX user-home path' $m.Value
        }
    }

    foreach ($m in [regex]::Matches($Text,'(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b')) {
        Add-Failure $Location 'email address' $m.Value
    }

    foreach ($m in [regex]::Matches($Text,'\bS-1-5-21-(?:\d+-){3}\d+\b')) {
        Add-Failure $Location 'Windows account SID' $m.Value
    }

    foreach ($m in [regex]::Matches($Text,'\b7656119\d{10}\b')) {
        Add-Failure $Location 'SteamID64-like identifier' $m.Value
    }

    foreach ($m in [regex]::Matches($Text,'(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)')) {
        $contextStart = [Math]::Max(0, $m.Index - 48)
        $context = $Text.Substring($contextStart, $m.Index - $contextStart)
        if ($context -match '(?i)(Assembly(?:File)?Version|ProductVersion|FileVersion|\bversion)\s*[^\r\n]{0,24}}

function Get-TrackedFirstPartyFiles {
    $files = @()
    try {
        $raw = & git -C $rootPath ls-files 2>$null
        if ($LASTEXITCODE -eq 0) { $files = @($raw) }
    } catch {}

    if ($files.Count -eq 0) {
        $files = @(Get-ChildItem -LiteralPath $rootPath -File -Recurse | ForEach-Object {
            $_.FullName.Substring($rootPath.Length).TrimStart('\','/').Replace('\','/')
        })
    }

    $allowedExtensions = @('.cs','.ps1','.bat','.cmd','.md','.txt','.cfg','.json','.yml','.yaml','.xml','.props','.csproj','.gitignore','.gitattributes')
    foreach ($rel in $files) {
        $normalized = ($rel -replace '\\','/').TrimStart('/')
        if ($normalized -match '^(THIRD_PARTY_LICENSES|DevBuild|Dist|\.git)/') { continue }
        $ext = [IO.Path]::GetExtension($normalized).ToLowerInvariant()
        $name = [IO.Path]::GetFileName($normalized).ToLowerInvariant()
        if (($allowedExtensions -notcontains $ext) -and ($allowedExtensions -notcontains ('.' + $name))) { continue }
        $full = Join-Path $rootPath ($normalized -replace '/','\')
        if (Test-Path -LiteralPath $full -PathType Leaf) { $full }
    }
}

function Get-PrintableBinaryText([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $ascii = New-Object Text.StringBuilder
    $utf16 = New-Object Text.StringBuilder
    $out = New-Object Text.StringBuilder

    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $b = $bytes[$i]
        if ($b -ge 32 -and $b -le 126) {
            [void]$ascii.Append([char]$b)
        } else {
            if ($ascii.Length -ge 6) { [void]$out.AppendLine($ascii.ToString()) }
            [void]$ascii.Clear()
        }

        if ($i + 1 -lt $bytes.Length -and $bytes[$i + 1] -eq 0 -and $b -ge 32 -and $b -le 126) {
            [void]$utf16.Append([char]$b)
            $i++
        } else {
            if ($utf16.Length -ge 6) { [void]$out.AppendLine($utf16.ToString()) }
            [void]$utf16.Clear()
        }
    }

    if ($ascii.Length -ge 6) { [void]$out.AppendLine($ascii.ToString()) }
    if ($utf16.Length -ge 6) { [void]$out.AppendLine($utf16.ToString()) }
    return $out.ToString()
}

foreach ($file in @(Get-TrackedFirstPartyFiles)) {
    try {
        $relative = $file.Substring($rootPath.Length).TrimStart('\','/')
        Scan-Text ([IO.File]::ReadAllText($file)) $relative
    } catch {
        throw "PII guard could not inspect tracked file '$file': $($_.Exception.Message)"
    }
}

$artifactList = @()
if (-not [String]::IsNullOrWhiteSpace($ArtifactPaths)) {
    $artifactList = @($ArtifactPaths.Split(';') | Where-Object { -not [String]::IsNullOrWhiteSpace($_) })
}

foreach ($artifact in $artifactList) {
    $full = [IO.Path]::GetFullPath($artifact)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "PII guard artifact does not exist: $full"
    }
    Scan-Text (Get-PrintableBinaryText $full) ("artifact " + [IO.Path]::GetFileName($full))
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw ("PII guard failed with " + $failures.Count + " finding(s). Remove machine/user-specific identifiers before building or publishing.")
}

Write-Host ("PII guard passed: first-party tracked text" + ($(if ($artifactList.Count -gt 0) { " + " + $artifactList.Count + " authored artifact(s)" } else { "" })) + ".")
) {
            continue
        }
        if (Test-PublicIpv4 $m.Value) {
            Add-Failure $Location 'public IPv4 literal' $m.Value
        }
    }
}

function Get-TrackedFirstPartyFiles {
    $files = @()
    try {
        $raw = & git -C $rootPath ls-files 2>$null
        if ($LASTEXITCODE -eq 0) { $files = @($raw) }
    } catch {}

    if ($files.Count -eq 0) {
        $files = @(Get-ChildItem -LiteralPath $rootPath -File -Recurse | ForEach-Object {
            $_.FullName.Substring($rootPath.Length).TrimStart('\','/').Replace('\','/')
        })
    }

    $allowedExtensions = @('.cs','.ps1','.bat','.cmd','.md','.txt','.cfg','.json','.yml','.yaml','.xml','.props','.csproj','.gitignore','.gitattributes')
    foreach ($rel in $files) {
        $normalized = ($rel -replace '\\','/').TrimStart('/')
        if ($normalized -match '^(THIRD_PARTY_LICENSES|DevBuild|Dist|\.git)/') { continue }
        $ext = [IO.Path]::GetExtension($normalized).ToLowerInvariant()
        $name = [IO.Path]::GetFileName($normalized).ToLowerInvariant()
        if (($allowedExtensions -notcontains $ext) -and ($allowedExtensions -notcontains ('.' + $name))) { continue }
        $full = Join-Path $rootPath ($normalized -replace '/','\')
        if (Test-Path -LiteralPath $full -PathType Leaf) { $full }
    }
}

function Get-PrintableBinaryText([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $ascii = New-Object Text.StringBuilder
    $utf16 = New-Object Text.StringBuilder
    $out = New-Object Text.StringBuilder

    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $b = $bytes[$i]
        if ($b -ge 32 -and $b -le 126) {
            [void]$ascii.Append([char]$b)
        } else {
            if ($ascii.Length -ge 6) { [void]$out.AppendLine($ascii.ToString()) }
            [void]$ascii.Clear()
        }

        if ($i + 1 -lt $bytes.Length -and $bytes[$i + 1] -eq 0 -and $b -ge 32 -and $b -le 126) {
            [void]$utf16.Append([char]$b)
            $i++
        } else {
            if ($utf16.Length -ge 6) { [void]$out.AppendLine($utf16.ToString()) }
            [void]$utf16.Clear()
        }
    }

    if ($ascii.Length -ge 6) { [void]$out.AppendLine($ascii.ToString()) }
    if ($utf16.Length -ge 6) { [void]$out.AppendLine($utf16.ToString()) }
    return $out.ToString()
}

foreach ($file in @(Get-TrackedFirstPartyFiles)) {
    try {
        $relative = $file.Substring($rootPath.Length).TrimStart('\','/')
        Scan-Text ([IO.File]::ReadAllText($file)) $relative
    } catch {
        throw "PII guard could not inspect tracked file '$file': $($_.Exception.Message)"
    }
}

$artifactList = @()
if (-not [String]::IsNullOrWhiteSpace($ArtifactPaths)) {
    $artifactList = @($ArtifactPaths.Split(';') | Where-Object { -not [String]::IsNullOrWhiteSpace($_) })
}

foreach ($artifact in $artifactList) {
    $full = [IO.Path]::GetFullPath($artifact)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "PII guard artifact does not exist: $full"
    }
    Scan-Text (Get-PrintableBinaryText $full) ("artifact " + [IO.Path]::GetFileName($full))
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw ("PII guard failed with " + $failures.Count + " finding(s). Remove machine/user-specific identifiers before building or publishing.")
}

Write-Host ("PII guard passed: first-party tracked text" + ($(if ($artifactList.Count -gt 0) { " + " + $artifactList.Count + " authored artifact(s)" } else { "" })) + ".")
