$ErrorActionPreference = 'SilentlyContinue'
$candidates = New-Object System.Collections.Generic.List[string]
$steamRoots = New-Object System.Collections.Generic.List[string]

function Add-Candidate([string]$p) {
    if (-not [string]::IsNullOrWhiteSpace($p)) { $script:candidates.Add($p) }
}
function Add-SteamRoot([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return }
    $p = $p.Trim().Trim('"').Replace('/','\\').TrimEnd('\\')
    if ($script:steamRoots -notcontains $p) { $script:steamRoots.Add($p) }
}

$pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$pf64 = [Environment]::GetEnvironmentVariable('ProgramFiles')
if ($pf86) { Add-SteamRoot (Join-Path $pf86 'Steam') }
if ($pf64) { Add-SteamRoot (Join-Path $pf64 'Steam') }

try { Add-SteamRoot ((Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam').SteamPath) } catch {}
try { Add-SteamRoot ((Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam').InstallPath) } catch {}
try { Add-SteamRoot ((Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Valve\Steam').InstallPath) } catch {}

foreach ($root in @($steamRoots)) {
    Add-Candidate (Join-Path $root 'steamapps\common\Valheim')
    $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        try {
            $text = [IO.File]::ReadAllText($vdf)
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $lib = $m.Groups[1].Value -replace '\\\\','\\'
                Add-Candidate (Join-Path $lib 'steamapps\common\Valheim')
            }
        } catch {}
    }
}

foreach ($drive in [IO.DriveInfo]::GetDrives()) {
    try {
        if (-not $drive.IsReady) { continue }
        $r = $drive.RootDirectory.FullName
        Add-Candidate (Join-Path $r 'SteamLibrary\steamapps\common\Valheim')
        Add-Candidate (Join-Path $r 'Steam\steamapps\common\Valheim')
    } catch {}
}

$seen = @{}
foreach ($candidate in $candidates) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
    try { $full = [IO.Path]::GetFullPath($candidate.TrimEnd('\\')) } catch { continue }
    $key = $full.ToLowerInvariant()
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true
    if (Test-Path -LiteralPath (Join-Path $full 'valheim.exe')) {
        [Console]::Out.WriteLine($full)
        exit 0
    }
}
exit 1
