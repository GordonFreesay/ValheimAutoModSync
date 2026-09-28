param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $root 'Source'
$helperSource = Join-Path $sourceRoot 'AutoModSync.PrivateKeySecurity.cs'
$serverSource = Join-Path $sourceRoot 'ValheimAutoModSync.Server.cs'
$installerSource = Join-Path $sourceRoot 'ValheimAutoModSync.Installer.cs'
$buildToolSource = Join-Path $sourceRoot 'AutoModSync.BuildTool.cs'
$releaseBuild = Join-Path $root 'build-release.bat'
$devBuild = Join-Path $root 'build-dev.bat'

function Read-Text([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file missing: $Path" }
    return [IO.File]::ReadAllText($Path)
}
function Require([string]$Text,[string]$Needle,[string]$Label) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw "$Label missing: $Needle" }
}

$helper = Read-Text $helperSource
$server = Read-Text $serverSource
$installer = Read-Text $installerSource
$buildTool = Read-Text $buildToolSource
$release = Read-Text $releaseBuild
$dev = Read-Text $devBuild

Write-Host '[1/5] Checking private-key hardening contract in production source...'
foreach ($needle in @(
    'OpenProcessToken(GetCurrentProcess(), TokenQuery',
    'GetTokenInformation(token, TokenUser',
    'ConvertSidToStringSidW',
    'ConvertStringSecurityDescriptorToSecurityDescriptorW',
    'SetNamedSecurityInfoW',
    'ProtectedDaclSecurityInformation',
    'GetNamedSecurityInfoW',
    'GetSecurityDescriptorControl',
    'GetAce(dacl, i, out ace)',
    'FileAllAccess',
    'VerifyPrivateKeyFile(path)'
)) {
    Require $helper $needle 'Private-key ACL helper'
}
if ($helper.IndexOf('WindowsIdentity',[StringComparison]::Ordinal) -ge 0) {
    throw 'Private-key ACL helper must not depend on WindowsIdentity; Valheim Mono does not implement the required SID/token APIs.'
}
if ($helper.IndexOf('File.SetAccessControl',[StringComparison]::Ordinal) -ge 0 -or
    $helper.IndexOf('File.GetAccessControl',[StringComparison]::Ordinal) -ge 0) {
    throw 'Private-key ACL helper must use the native Windows ACL path validated for Valheim Mono.'
}
Require $server 'AutoModSyncPrivateKeySecurity.HardenPrivateKeyFile(path);' 'Server runtime ACL hardening'
Require $installer 'AutoModSyncPrivateKeySecurity.HardenPrivateKeyFile(privatePath);' 'Installer identity ACL hardening'
Require $buildTool 'AutoModSyncPrivateKeySecurity.HardenPrivateKeyFile(privatePath);' 'BuildTool identity ACL hardening'
Write-Host '  PASS'

Write-Host '[2/5] Checking all compiled key-touching binaries link the shared helper...'
foreach ($needle in @(
    'AutoModSync.BuildTool.cs" "%SOURCE%\AutoModSync.IdentityDisplay.cs" "%SOURCE%\AutoModSync.PrivateKeySecurity.cs',
    'ValheimAutoModSync.Server.cs" "%SOURCE%\AutoModSync.IdentityDisplay.cs" "%SOURCE%\AutoModSync.PrivateKeySecurity.cs',
    'ValheimAutoModSync.Installer.cs" "%SOURCE%\AutoModSync.ApplyEngine.cs" "%SOURCE%\AutoModSync.IdentityDisplay.cs" "%SOURCE%\AutoModSync.PrivateKeySecurity.cs'
)) {
    Require $release $needle 'Release build ACL linkage'
}
Require $dev 'ValheimAutoModSync.Server.cs" "%SOURCE%\AutoModSync.IdentityDisplay.cs" "%SOURCE%\AutoModSync.PrivateKeySecurity.cs' 'Development server ACL linkage'
Require $dev 'ValheimAutoModSync.Installer.cs" "%SOURCE%\AutoModSync.ApplyEngine.cs" "%SOURCE%\AutoModSync.IdentityDisplay.cs" "%SOURCE%\AutoModSync.PrivateKeySecurity.cs' 'Development installer ACL linkage'
Write-Host '  PASS'

$temp = Join-Path $env:TEMP ('AMS261-PrivateKeyAcl-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
        $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    }
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw '.NET Framework C# compiler was not found.' }

    $runnerSource = Join-Path $temp 'PrivateKeyAclHarness.cs'
    $runnerExe = Join-Path $temp 'PrivateKeyAclHarness.exe'
    $runnerCode = @'
using System;
namespace ValheimAutoModSync
{
    internal static class PrivateKeyAclHarness
    {
        private static int Main(string[] args)
        {
            if (args == null || args.Length != 1) return 2;
            AutoModSyncPrivateKeySecurity.HardenPrivateKeyFile(args[0]);
            return 0;
        }
    }
}
'@
    [IO.File]::WriteAllText($runnerSource,$runnerCode,[Text.UTF8Encoding]::new($false))

    Write-Host '[3/5] Compiling exact production ACL helper...'
    & $csc /nologo /target:exe /optimize+ /langversion:5 /out:$runnerExe $runnerSource $helperSource
    if ($LASTEXITCODE -ne 0) { throw "Private-key ACL harness compilation failed with exit code $LASTEXITCODE." }
    Write-Host '  PASS'

    $key = Join-Path $temp 'ValheimAutoModSync.private.xml'
    [IO.File]::WriteAllText($key,'<RSAKeyValue>test-private-material</RSAKeyValue>',[Text.UTF8Encoding]::new($false))

    # Make the test meaningful by explicitly granting Everyone read access before hardening.
    $acl = Get-Acl -LiteralPath $key
    $everyone = New-Object System.Security.Principal.SecurityIdentifier('S-1-1-0')
    $broadRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $everyone,
        [System.Security.AccessControl.FileSystemRights]::Read,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($broadRule) | Out-Null
    Set-Acl -LiteralPath $key -AclObject $acl

    Write-Host '[4/5] Applying production ACL hardening to a deliberately broad key file...'
    & $runnerExe $key
    if ($LASTEXITCODE -ne 0) { throw "Private-key ACL hardening returned exit code $LASTEXITCODE." }
    Write-Host '  PASS'

    Write-Host '[5/5] Verifying final DACL principals and inheritance...'
    $finalAcl = Get-Acl -LiteralPath $key
    if (-not $finalAcl.AreAccessRulesProtected) { throw 'Private-key ACL still inherits permissions.' }

    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $allowed = @(
        $currentSid,
        'S-1-5-18',
        'S-1-5-32-544'
    ) | Select-Object -Unique

    $seenFull = @{}
    foreach ($rule in $finalAcl.Access) {
        $sid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
        if ($allowed -notcontains $sid) {
            throw "Unexpected private-key ACL principal remains: $sid"
        }
        if ($rule.IsInherited) {
            throw "Private-key ACL contains an inherited rule for $sid"
        }
        if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) {
            throw "Private-key ACL contains a non-Allow rule for $sid"
        }
        if (($rule.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -eq [System.Security.AccessControl.FileSystemRights]::FullControl) {
            $seenFull[$sid] = $true
        }
    }

    foreach ($sid in $allowed) {
        if (-not $seenFull.ContainsKey($sid)) { throw "Required FullControl ACL entry missing: $sid" }
    }

    if (($finalAcl.Access | ForEach-Object { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value }) -contains 'S-1-1-0') {
        throw 'Everyone remains on the private-key ACL after hardening.'
    }

    Write-Host '  PASS'
    Write-Host 'PASS: private signing-key ACL hardening removes inherited/broad access and permits only the runtime identity, SYSTEM, and Administrators.'
}
finally {
    try { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } } catch {}
}
