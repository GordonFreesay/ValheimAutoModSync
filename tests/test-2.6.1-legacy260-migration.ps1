param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$serverPath = Join-Path $root 'Source\ValheimAutoModSync.Server.cs'
$server = [IO.File]::ReadAllText($serverPath)

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

Write-Host '[1/6] Checking the compatibility exception is limited to released 2.6.0...'
$legacyVersion = Section $server 'private static bool IsLegacy260MigrationClient' '// Intent: Derives an in-memory migration-grant key' '2.6.0 version gate'
Assert-Contains $legacyVersion 'String.Equals(version, "2.6.0", StringComparison.Ordinal)' '2.6.0 version gate'
Assert-Contains $legacyVersion 'String.Equals(version, "2.6.0.0", StringComparison.Ordinal)' '2.6.0 version gate'
Assert-NotContains $legacyVersion 'StartsWith("2.6."' '2.6.0 version gate'
Write-Host '  PASS only the released 2.6.0 version forms can enter the legacy migration bridge.'

Write-Host '[2/6] Checking the released 2.6.0 discovery timeout leads into the untouched vanilla server-key handshake...'
$ensure = Section $server 'private static bool EnsurePreflightAuthorizationOrChallenge' '// Intent: Handles the client''s one-time password response.' 'Legacy authorization selection'
Assert-Before $ensure 'if (!ClientSupportsCapability(rpc, "password-auth2"))' 'PasswordAuthChallenge challenge = GetOrCreatePasswordAuthChallenge(rpc);' 'Legacy authorization selection'
Assert-Before $ensure 'if (Legacy260PasswordBootstrapPeers.Contains(rpc)) return false;' 'Legacy260PasswordBootstrapPeers.Add(rpc);' 'Legacy bootstrap idempotency'
Assert-Contains $ensure 'TryConsumeLegacy260MigrationGrant(rpc)' 'One-use migration reconnect'
Assert-Contains $ensure 'Legacy260MigrationAuthorizedPeers.Add(rpc);' 'One-use migration reconnect'
$legacyBranch = Section $ensure 'if (!ClientSupportsCapability(rpc, "password-auth2"))' 'PasswordAuthChallenge challenge = GetOrCreatePasswordAuthChallenge(rpc);' 'Legacy branch'
Assert-NotContains $legacyBranch 'SendAuthenticationRequiredAck' 'Legacy pre-server-key disclosure'
Assert-NotContains $legacyBranch 'rpc.Invoke("ClientHandshake"' 'Legacy synthetic handshake'
Assert-NotContains $legacyBranch 'TryGetServerPasswordSalt' 'Legacy synthetic handshake'
Assert-Contains $legacyBranch 'original Valheim handshake reaches the normal server-key check' 'Legacy timeout bootstrap'
Write-Host '  PASS 2.6.0 receives no AMS acknowledgement or synthetic ClientHandshake; its original vanilla handshake performs the server-key check.'

Write-Host '[3/6] Checking grant lifetime, identity binding, and one-use consumption...'
$identity = Section $server 'private static string Legacy260MigrationIdentityKey' '// Intent: Issues one short-lived migration reconnect' 'Legacy identity key'
Assert-Contains $identity 'rpc.GetSocket().GetHostName()' 'Legacy identity key'
Assert-Contains $identity 'SHA256.Create()' 'Legacy identity key'
Assert-Contains $identity '"AMS260-MIGRATION\0" + identity' 'Legacy identity key'
Assert-Contains $server 'private const int Legacy260MigrationGrantMinutes = 5;' 'Legacy grant lifetime'
$consume = Section $server 'private static bool TryConsumeLegacy260MigrationGrant' '// Intent: Confirms that Valheim''s original RPC_PeerInfo accepted' 'Legacy grant consumption'
Assert-Before $consume 'Legacy260MigrationGrants.TryGetValue' 'Legacy260MigrationGrants.Remove(key);' 'Legacy grant consumption'
Assert-Contains $consume 'DateTime.UtcNow <= expiresUtc' 'Legacy grant expiry'
Write-Host '  PASS migration authorization is hashed-identity-bound, five-minute, memory-only, and consumed once.'

Write-Host '[4/6] Checking only a successful vanilla PeerInfo can issue the grant...'
$ready = Section $server 'private static bool IsValheimPeerReady' '// Intent: Allows protected synchronization disclosure' 'Vanilla ready verification'
Assert-Contains $ready 'instance.GetPeers()' 'Vanilla ready verification'
Assert-Contains $ready 'peer.m_rpc == rpc' 'Vanilla ready verification'
Assert-Contains $ready 'peer.IsReady()' 'Vanilla ready verification'
$peerGate = Section $server '[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]' '// Intent: Migrates only the exact earlier development-default tuples' 'PeerInfo migration gate'
Assert-Contains $peerGate 'if (Legacy260PasswordBootstrapPeers.Contains(rpc)) return true;' 'Legacy vanilla PeerInfo exception'
Assert-Before $peerGate '!IsValheimPeerReady(__instance, rpc)' 'IssueLegacy260MigrationGrant(rpc)' 'Grant after vanilla success'
Assert-Contains $peerGate 'DiscardServerPreflightQuarantine(rpc);' 'Bootstrap traffic discard'
Assert-Contains $peerGate 'rpc.Invoke("Disconnect", new object[0])' 'Bootstrap disconnect'
Write-Host '  PASS wrong/failed PeerInfo cannot issue a grant; successful bootstrap traffic is discarded before disconnect.'

Write-Host '[5/6] Checking the released 2.6.0 timeout is converted into a server-key challenge without exposing the quarantined server mod inventory...'
$handshakeGate = Section $server 'private static class ProtectedServerHandshakeGatePatch' '[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]' 'ServerHandshake migration gate'
$legacyHandshake = Section $handshakeGate 'if (Legacy260PasswordBootstrapPeers.Contains(rpc))' 'if (Legacy260MigrationAuthorizedPeers.Contains(rpc)' 'Legacy ServerHandshake bootstrap'
$jotunnSentinel = Section $server 'private static void SendLegacy260JotunnPresenceSentinel' '// Intent: Never delays core server denial/password controls' 'Legacy Jotunn presence sentinel'
$quarantine = Section $server 'private static class ServerInvokePreflightQuarantinePatch' '// Intent: Starts a bounded hold for third-party preflight messages' 'Server preflight quarantine'
$deferredPreflight = Section $server 'private static void ScheduleLegacy260AuthorizedPreflight' '// Intent: Releases the server-side third-party RPC quarantine' 'Deferred legacy authorized preflight'
Assert-Contains $legacyHandshake 'Legacy260PasswordChallengeSentPeers.Contains(rpc)' 'Legacy challenge idempotency'
Assert-Contains $legacyHandshake 'TryGetServerPasswordSalt(out legacySalt)' 'Legacy server-key salt'
Assert-Before $legacyHandshake 'SendLegacy260JotunnPresenceSentinel(rpc)' 'rpc.Invoke("ClientHandshake", new object[] { true, legacySalt })' 'Presence sentinel before server-key challenge'
Assert-Contains $legacyHandshake 'protected synchronization remains withheld' 'Legacy pre-access withholding'
Assert-Contains $legacyHandshake 'return false;' 'Original ServerHandshake suppression'
Assert-NotContains $legacyHandshake 'ReleaseServerPreflightQuarantine' 'Bootstrap compatibility quarantine'
Assert-Contains $server 'Legacy260JotunnVersionDataRpc = "RPC_Jotunn_ReceiveVersionData"' 'Jotunn presence RPC name'
Assert-Contains $jotunnSentinel 'sentinel.Write(0);' 'Zero-module compatibility sentinel'
Assert-Contains $jotunnSentinel 'sentinel.Write("");' 'No server version-string disclosure'
Assert-Contains $jotunnSentinel 'sentinel.Write((uint)0);' 'No server network-version disclosure'
Assert-Contains $jotunnSentinel 'Legacy260JotunnPresenceBypassPeers.Add(rpc);' 'Exact-RPC sentinel bypass'
Assert-Contains $jotunnSentinel 'Legacy260JotunnPresenceBypassPeers.Remove(rpc);' 'Sentinel bypass cleanup'
Assert-NotContains $jotunnSentinel 'ClientVersions' 'No client/server mod inventory access'
Assert-NotContains $jotunnSentinel '_publicFingerprint' 'No signing fingerprint disclosure'
Assert-Contains $quarantine 'Legacy260JotunnPresenceBypassPeers.Contains(__instance)' 'Exact-RPC quarantine exception'
Assert-Contains $quarantine 'String.Equals(method, Legacy260JotunnVersionDataRpc, StringComparison.Ordinal)' 'Exact-method quarantine exception'
Assert-Contains $deferredPreflight 'ack.Write(ProtocolVersion);' 'Authorized migration acknowledgement protocol'
Assert-Contains $deferredPreflight 'rpc.Invoke(RpcAck, new object[] { ack });' 'Authorized migration acknowledgement send'
Assert-Before $deferredPreflight 'rpc.Invoke(RpcAck, new object[] { ack });' 'Legacy260DeferredAuthorizedPreflights[rpc] = DateTime.UtcNow.AddMilliseconds(150.0);' 'Ack before deferred manifest schedule'
Assert-Contains $deferredPreflight 'SendAuthorizedPreflight(rpc);' 'Deferred signed synchronization discovery'
Assert-Contains $server 'ServiceLegacy260DeferredAuthorizedPreflights(now);' 'Deferred preflight service loop'
Assert-Contains $server 'Legacy260MigrationAuthorizedPeers.Contains(rpc) && IsLegacy260MigrationClient(rpc)' 'Legacy reconnect scheduling branch'
Assert-Contains $server 'ScheduleLegacy260AuthorizedPreflight(rpc);' 'Legacy reconnect scheduling call'
Assert-Contains $handshakeGate 'Legacy260MigrationAuthorizedPeers.Contains(rpc) && !PasswordAuthorizedPeers.Contains(rpc)' 'Migration-only gameplay denial'
Assert-Contains $handshakeGate 'blocked gameplay handshake on a migration-only 2.6.0 authorization' 'Migration-only gameplay denial'
Assert-Contains $server '(!PasswordAuthorizedPeers.Contains(rpc) && !Legacy260MigrationAuthorizedPeers.Contains(rpc))' 'Protected migration-data authorization'
Write-Host '  PASS 2.6.0 receives only a zero-module Jotunn presence sentinel before server access, then an authorized ACK is allowed to leave the reconnect hello handler before signed migration discovery; the real server mod/config inventory stays gated until the one-use grant is consumed.'

Write-Host '[6/6] Checking exact-RPC migration state and unused grants are cleaned up...'
$cleanup = Section $server 'private static void CleanupDisconnectedOrIdleTransfers' '// Intent: Assigns one process-local opaque scheduler id' 'Legacy cleanup'
Assert-Contains $cleanup 'now > grant.Value' 'Legacy grant expiry cleanup'
Assert-Contains $cleanup 'Legacy260PasswordBootstrapPeers.Remove(rpc);' 'Legacy bootstrap cleanup'
Assert-Contains $cleanup 'Legacy260PasswordChallengeSentPeers.Remove(rpc);' 'Legacy server-key challenge cleanup'
Assert-Contains $cleanup 'Legacy260JotunnPresenceBypassPeers.Remove(rpc);' 'Legacy Jotunn sentinel bypass cleanup'
Assert-Contains $cleanup 'Legacy260MigrationAuthorizedPeers.Remove(rpc);' 'Legacy migration cleanup'
Assert-Contains $cleanup 'Legacy260DeferredAuthorizedPreflights.Remove(rpc);' 'Deferred legacy preflight cleanup'
Assert-Contains $cleanup 'Legacy260MigrationGrants.Remove(expiredLegacyGrants[lg])' 'Legacy grant expiry cleanup'
Write-Host '  PASS disconnected exact-RPC state and expired unconsumed grants are removed.'

Write-Host ''
Write-Host 'PASS: server-key-gated 2.6.0 -> 2.6.1 protected-server migration bridge is structurally gated.'
