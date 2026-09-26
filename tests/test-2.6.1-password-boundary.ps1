param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$serverPath = Join-Path $root 'Source\ValheimAutoModSync.Server.cs'
$clientPath = Join-Path $root 'Source\ValheimAutoModSync.Client.cs'
$versionPath = Join-Path $root 'VERSION'

$server = [IO.File]::ReadAllText($serverPath)
$client = [IO.File]::ReadAllText($clientPath)
$version = ([IO.File]::ReadAllText($versionPath)).Trim()

function Assert-Contains([string]$Text,[string]$Needle,[string]$Label) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) {
        throw "$Label is missing: $Needle"
    }
}

function Assert-NotContains([string]$Text,[string]$Needle,[string]$Label) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) {
        throw "$Label unexpectedly contains: $Needle"
    }
}

function Section([string]$Text,[string]$Start,[string]$End,[string]$Label) {
    $a = $Text.IndexOf($Start,[StringComparison]::Ordinal)
    if ($a -lt 0) { throw "$Label start marker missing: $Start" }
    $b = $Text.IndexOf($End,$a + $Start.Length,[StringComparison]::Ordinal)
    if ($b -lt 0) { throw "$Label end marker missing: $End" }
    return $Text.Substring($a,$b-$a)
}

function Assert-Before([string]$Text,[string]$First,[string]$Second,[string]$Label) {
    $a = $Text.IndexOf($First,[StringComparison]::Ordinal)
    $b = $Text.IndexOf($Second,[StringComparison]::Ordinal)
    if ($a -lt 0 -or $b -lt 0 -or $a -ge $b) {
        throw "$Label ordering is wrong: [$First] must precede [$Second]."
    }
}

if ($version -ne '2.6.1') { throw "Expected VERSION 2.6.1, found $version." }
Assert-Contains $server 'public const string PluginVersion = "2.6.1";' 'Server version'
Assert-Contains $client 'public const string PluginVersion = "2.6.1";' 'Client version'
Write-Host '[1/7] PASS 2.6.1 runtime version metadata is active.'

$presence = Section $server 'private static void SendAuthenticationRequiredAck' 'private static bool EnsurePreflightAuthorizationOrChallenge' 'Presence-only acknowledgement'
foreach ($forbidden in @('_publicKeyXml','_publicFingerprint','_manifestText','_manifestSignature','bundle-window1','bundle-resume1','bundle-scheduler1')) {
    Assert-NotContains $presence $forbidden 'Presence-only acknowledgement'
}
Assert-Contains $presence 'ack.Write("auth-required1")' 'Presence-only acknowledgement'
Write-Host '[2/7] PASS pre-authentication acknowledgement contains no protected synchronization metadata.'

$hello = Section $server 'private static void RPC_Hello' 'private static void SendAuthorizedPreflight' 'Hello boundary'
Assert-Before $hello 'AmsPreflightPeers.Add(rpc);' 'EnsurePreflightAuthorizationOrChallenge(rpc)' 'Hello boundary'
Assert-Before $hello 'EnsurePreflightAuthorizationOrChallenge(rpc)' 'SendAuthorizedPreflight(rpc)' 'Hello boundary'

$authorized = Section $server 'private static void SendAuthorizedPreflight' 'private static void RPC_GetBundle' 'Authorized preflight'
Assert-Before $authorized 'RequireProtectedSyncAuthorization(rpc)' 'EnsureManifest(false)' 'Authorized preflight'
Assert-Contains $authorized '_publicKeyXml' 'Authorized preflight'
Assert-Contains $authorized '_manifestSignature' 'Authorized preflight'
Write-Host '[3/7] PASS fingerprint/manifest emission is centralized behind the password authorization gate.'

foreach ($pair in @(
    @('private static void RPC_GetBundle(ZRpc rpc, ZPackage pkg)','private static void StartScheduledBundleTransfer','bundle request'),
    @('private static void RPC_GetBundleChunk(ZRpc rpc, ZPackage pkg)','private static void RPC_GetBundleBatch','chunk request'),
    @('private static void RPC_GetBundleBatch(ZRpc rpc, ZPackage pkg)','#if AMS_DEV_TESTS','batch request')
)) {
    $section = Section $server $pair[0] $pair[1] $pair[2]
    Assert-Before $section 'RequireProtectedSyncAuthorization(rpc)' 'pkg.Read' $pair[2]
}
Write-Host '[4/7] PASS every network-facing bundle request rechecks authorization before parsing or serving protected state.'

Assert-Contains $server 'private static readonly HashSet<ZRpc> PasswordAuthorizedPeers' 'Connection-scoped authorization'
Assert-Contains $server 'PasswordAuthorizedPeers.Remove(rpc);' 'Disconnect cleanup'
Assert-Contains $server 'PasswordChallengePeers.Remove(rpc);' 'Disconnect cleanup'
Assert-Contains $server 'AmsPreflightPeers.Remove(rpc);' 'Disconnect cleanup'
Assert-Contains $server '[HarmonyPatch(typeof(ZNet), "RPC_ServerHandshake")]' 'Early vanilla handshake gate'
Assert-Contains $server '[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]' 'Early vanilla PeerInfo gate'
Write-Host '[5/7] PASS authorization is exact-connection scoped and defensive vanilla bypass gates are present.'

Assert-Contains $client 'password-auth1' 'Client capability'
Assert-Contains $client 'TryCreateValheimPasswordProof(password, out proof)' 'Password proof path'
Assert-Contains $client 'auth.Write(proof);' 'Password proof path'
Assert-Contains $client 'rpc.Invoke(RpcAuth' 'Password proof path'
Assert-Contains $client 'Server password required.' 'Password boundary UI'
Assert-Contains $client 'fingerprint, mod list, hashes, sizes, configuration, and files remain hidden' 'Password boundary UI'
Write-Host '[6/7] PASS client uses password proof before requesting protected server state.'

Assert-Contains $client 'By choosing Yes, you also confirm that you have permission to receive any mods or configuration files provided by this server.' 'First-contact consent'
Assert-Contains $client 'AutoModSync is not responsible for verifying or enforcing third-party mod licensing or redistribution requirements' 'First-contact consent'
Write-Host '[7/7] PASS first-contact trust confirmation includes explicit receive-permission and licensing acknowledgement.'

Write-Host ''
Write-Host 'PASS: AutoModSync 2.6.1 password-protected server disclosure boundary contract is present.'
