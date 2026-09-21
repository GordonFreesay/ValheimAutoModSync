$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$versionPath = Join-Path $root "VERSION"
if (-not (Test-Path -LiteralPath $versionPath)) { throw "VERSION file is missing." }

$version = ([System.IO.File]::ReadAllText($versionPath)).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "VERSION must contain a semantic version such as 2.5.0. Found: $version"
}

$assemblyVersion = ($version -replace '-.*$','') + ".0"
$failures = New-Object System.Collections.Generic.List[string]

function Assert-RegexValue {
    param(
        [string]$Path,
        [string]$Pattern,
        [string]$Expected,
        [string]$Label
    )

    $full = Join-Path $root $Path
    if (-not (Test-Path -LiteralPath $full)) {
        $failures.Add("$($Path): missing")
        return
    }

    $text = [System.IO.File]::ReadAllText($full)
    $matches = [regex]::Matches($text, $Pattern)
    if ($matches.Count -eq 0) {
        $failures.Add("$($Path): could not find $Label")
        return
    }

    foreach ($m in $matches) {
        if ($m.Groups[1].Value -ne $Expected) {
            $failures.Add("$($Path): $Label '$($m.Groups[1].Value)' does not match VERSION '$Expected'")
        }
    }
}

Assert-RegexValue "Source\ValheimAutoModSync.Client.cs" 'PluginVersion\s*=\s*"([^"]+)"' $version "PluginVersion"
Assert-RegexValue "Source\ValheimAutoModSync.Server.cs" 'PluginVersion\s*=\s*"([^"]+)"' $version "PluginVersion"

foreach ($source in @(
    "Source\ValheimAutoModSync.Client.cs",
    "Source\ValheimAutoModSync.Server.cs",
    "Source\ValheimAutoModSync.Apply.cs",
    "Source\ValheimAutoModSync.Installer.cs",
    "Source\AutoModSync.BuildTool.cs"
)) {
    Assert-RegexValue $source 'AssemblyVersion\("([^"]+)"\)' $assemblyVersion "AssemblyVersion"
    Assert-RegexValue $source 'AssemblyFileVersion\("([^"]+)"\)' $assemblyVersion "AssemblyFileVersion"
}

Assert-RegexValue "Source\AutoModSyncInstaller.manifest" 'assemblyIdentity\s+version="([^"]+)"' $assemblyVersion "installer manifest version"
Assert-RegexValue "Thunderstore\thunderstore.toml" 'versionNumber\s*=\s*"([^"]+)"' $version "Thunderstore package version"

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Version consistency check passed: $version"
exit 0
