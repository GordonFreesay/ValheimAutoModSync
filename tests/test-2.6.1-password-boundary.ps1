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
Write-Host '[1/10] PASS 2.6.1 runtime version metadata is active.'

$presence = Section $server 'private static void SendAuthenticationRequiredAck' 'private static PasswordAuthChallenge GetOrCreatePasswordAuthChallenge' 'Presence-only acknowledgement'
foreach ($forbidden in @('_publicKeyXml','_publicFingerprint','_manifestText','_manifestSignature','bundle-window1','bundle-resume1','bundle-scheduler1','preflight-quarantine1')) {
    Assert-NotContains $presence $forbidden 'Presence-only acknowledgement'
}
Assert-Contains $presence 'ack.Write("auth-required2")' 'Presence-only acknowledgement'
Assert-Contains $presence 'ack.Write(challengeBase64' 'Presence-only acknowledgement'
Write-Host '[2/10] PASS pre-auth acknowledgement exposes only product/auth state plus an opaque random challenge.'

$challenge = Section $server 'private static PasswordAuthChallenge GetOrCreatePasswordAuthChallenge' '// Intent: Preserves 2.6.0 behavior on public servers' 'One-time password challenge'
Assert-Contains $challenge 'byte[] nonce = new byte[32];' 'One-time password challenge'
Assert-Contains $challenge 'RandomNumberGenerator.Create()' 'One-time password challenge'
Assert-Contains $challenge 'HMACSHA256' 'One-time password challenge'
Assert-Contains $challenge '"AMS4-AUTH2\0"' 'One-time password challenge'
Assert-Contains $challenge 'ConstantTimeEquals' 'One-time password challenge'

$auth = Section $server 'private static void RPC_Auth' '// Intent: Releases the server-side third-party RPC quarantine' 'Auth verification'
Assert-Before $auth 'PasswordAuthChallenges.Remove(rpc);' 'ComputePasswordAuthResponse' 'One-time auth consumption'
Assert-Contains $auth 'TotalMinutes > 15.0' 'Challenge expiry'
Assert-Contains $auth 'ConstantTimeEquals(response, expected)' 'Constant-time auth'
Assert-NotContains $auth 'String.Equals(proof, expectedHash' 'Reusable verifier comparison'
Write-Host '[3/10] PASS AMS password authentication is random-challenge, one-use, expiring, HMAC-based, and non-replayable.'

$hello = Section $server 'private static void RPC_Hello' 'private static void SendAuthorizedPreflight' 'Hello boundary'
Assert-Before $hello 'AmsPreflightPeers.Add(rpc);' 'EnsurePreflightAuthorizationOrChallenge(rpc)' 'Hello boundary'
Assert-Before $hello 'EnsurePreflightAuthorizationOrChallenge(rpc)' 'SendAuthorizedPreflight(rpc)' 'Hello boundary'

$authorized = Section $server 'private static void SendAuthorizedPreflight' 'private static void RPC_GetBundle' 'Authorized preflight'
Assert-Before $authorized 'RequireProtectedSyncAuthorization(rpc)' 'EnsureManifest(false)' 'Authorized preflight'
Assert-Contains $authorized '_publicKeyXml' 'Authorized preflight'
Assert-Contains $authorized '_manifestSignature' 'Authorized preflight'
Write-Host '[4/10] PASS fingerprint/manifest emission remains centralized behind exact-connection authorization.'

foreach ($pair in @(
    @('private static void RPC_GetBundle(ZRpc rpc, ZPackage pkg)','private static void StartScheduledBundleTransfer','bundle request'),
    @('private static void RPC_GetBundleChunk(ZRpc rpc, ZPackage pkg)','private static void RPC_GetBundleBatch','chunk request'),
    @('private static void RPC_GetBundleBatch(ZRpc rpc, ZPackage pkg)','#if AMS_DEV_TESTS','batch request')
)) {
    $section = Section $server $pair[0] $pair[1] $pair[2]
    Assert-Before $section 'RequireProtectedSyncAuthorization(rpc)' 'pkg.Read' $pair[2]
}
Write-Host '[5/10] PASS every bundle entry point rechecks authorization before parsing or serving protected state.'

Assert-Contains $server 'private static readonly HashSet<ZRpc> PasswordAuthorizedPeers' 'Connection-scoped authorization'
Assert-Contains $server 'private static readonly Dictionary<ZRpc, PasswordAuthChallenge> PasswordAuthChallenges' 'Connection-scoped challenge'
Assert-Contains $server 'PasswordAuthChallenges.Remove(rpc);' 'Disconnect cleanup'
Assert-Contains $server 'PasswordAuthorizedPeers.Remove(rpc);' 'Disconnect cleanup'
Assert-Contains $server 'AmsPreflightPeers.Remove(rpc);' 'Disconnect cleanup'
Assert-Contains $server '[HarmonyPatch(typeof(ZNet), "RPC_ServerHandshake")]' 'Early vanilla handshake gate'
Assert-Contains $server '[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]' 'Early vanilla PeerInfo gate'
Write-Host '[6/10] PASS authentication/challenge state is exact-ZRpc scoped and revoked on disconnect.'

Assert-Contains $client 'password-auth2;preflight-quarantine1' 'Client capabilities'
Assert-Contains $client 'TryCreateValheimPasswordProof(password, out verifier)' 'Local verifier derivation'
Assert-Contains $client 'TryCreatePasswordChallengeResponse(verifier, _serverPasswordAuthChallenge, out response)' 'One-time response path'
Assert-Contains $client 'auth.Write(response);' 'One-time response path'
Assert-Contains $client 'HMACSHA256' 'Client HMAC response'
Assert-NotContains $client 'auth.Write(verifier);' 'Reusable verifier transmission'
$clientAck = Section $client 'private static void RPC_Ack' '// Intent: Validates and records the signed-manifest header.' 'Client acknowledgement handling'
Assert-Contains $clientAck 'HideSyncOverlay();' 'Password-entry UI ownership'
Assert-NotContains $clientAck 'ShowSyncOverlay(AutoModSyncUiPhase.Checking, "Server password required."' 'Password-entry UI ownership'
Assert-Contains $client 'passwordDialogOpen && Input.GetKeyDown(KeyCode.Escape)' 'Password-entry cancel path'
Assert-Contains $client 'AbortAutoModSyncJoin("Server password entry was cancelled.")' 'Password-entry cancel path'
Assert-Contains $client 'Enum.Parse(statusField.FieldType, "Connecting")' 'Stale compatibility status normalization'
Assert-Contains $client 'restored Valheim connection status after stale preflight compatibility side effects' 'Stale compatibility status normalization'
Assert-Contains $client 'Process.GetCurrentProcess().MainWindowHandle' 'Trust-dialog owner binding'
Assert-Contains $client 'PromoteNativeTrustPrompt()' 'Trust-dialog foreground promotion'
Assert-Contains $client 'SetWindowPos(hwnd, HWND_TOPMOST' 'Trust-dialog foreground promotion'
Assert-Contains $client 'SetForegroundWindow(hwnd)' 'Trust-dialog foreground promotion'
Assert-NotContains $client 'MessageBox(IntPtr.Zero, message, TrustPromptCaption' 'Unowned trust dialog'
Write-Host '[7/10] PASS client keeps the reusable verifier local, leaves password entry unobscured/cancellable, neutralizes stale compatibility status side effects, and keeps first-contact trust visible above Valheim.'

$clientQuarantine = Section $client 'private static class InvokeServerHandshakeGatePatch' '// Intent: Identifies only AutoModSync protocol traffic' 'Client RPC quarantine'
Assert-Contains $clientQuarantine 'HeldPreflightInvocations.Count >= MaxHeldPreflightInvocations' 'Client RPC quarantine bound'
Assert-Contains $clientQuarantine 'HeldPreflightInvocations.Add(held);' 'Client RPC quarantine'
Assert-Contains $clientQuarantine 'String.Equals(method, "Disconnect", StringComparison.Ordinal)' 'Recognized-AMS stale disconnect handling'
Assert-Contains $clientQuarantine 'if (!_serverAcknowledged && !_serverRecognized) return true;' 'Non-AMS disconnect preservation'
Assert-Contains $clientQuarantine 'suppressed a stale preflight Disconnect emitted after AMS recognition' 'Recognized-AMS stale disconnect handling'
Assert-NotContains $client 'IsClientConnectionControlRpcMethod(method)' 'Obsolete blanket disconnect passthrough'
Assert-Contains $client '[HarmonyPriority(Priority.First + 200)]' 'Early quarantine priority'
Assert-Contains $client 'rpc.Invoke(RpcReady' 'Client readiness signal'
Assert-Contains $client 'rpc.Invoke(held.Method, held.Parameters);' 'Client ordered replay'
Write-Host '[8/10] PASS client quarantines stale preflight traffic, discards post-recognition false disconnects, and replays only safe queued RPCs after AMS readiness.'

$serverQuarantine = Section $server 'private static class ServerInvokePreflightQuarantinePatch' '// 2.6.1 defense-in-depth: an AMS-aware connection' 'Server RPC quarantine'
Assert-Contains $serverQuarantine 'PreflightQuarantinePeers.Contains(__instance)' 'Server RPC quarantine'
Assert-Contains $serverQuarantine 'queue.Count >= MaxHeldPreflightInvocations' 'Server RPC quarantine bound'
Assert-Contains $serverQuarantine 'queue.Add(held);' 'Server RPC quarantine'
Assert-Contains $serverQuarantine 'rpc.Invoke(queue[i].Method, queue[i].Parameters);' 'Server ordered replay'
Assert-Contains $server 'internal const string RpcReady = "AMS4_Ready";' 'Readiness RPC'
Assert-Contains $server 'PreflightReadyPeers.Add(rpc);' 'Readiness RPC'
Assert-Contains $server 'preflight-quarantine1' 'Server quarantine capability'
Assert-Contains $server 'MaxInactivePreflightMinutes = 30' 'Inactive preflight bound'
Assert-Contains $server '(now - started).TotalMinutes >= MaxInactivePreflightMinutes' 'Inactive preflight expiry'
Write-Host '[9/10] PASS server quarantines early compatibility/version RPCs, bounds inactive preflight state, and releases only after AMS readiness/legacy fallback.'

Assert-Contains $client 'BepInEx mods are executable code and can act with the permissions of your Valheim process/user account.' 'Executable-code trust warning'
Assert-Contains $client 'Choosing Yes authorizes this server to provide synchronized executable mod files and configuration to this PC.' 'First-contact security consent'
Assert-Contains $client 'Third-party content is provided by the server operator. AutoModSync does not determine or verify third-party licensing or redistribution rights.' 'First-contact responsibility boundary'
Assert-NotContains $client 'you have permission to receive any mods or configuration files provided by this server' 'Client must not certify server redistribution rights'
Assert-NotContains $client 'by choosing Yes, you confirm that permission yourself' 'Client must not certify server redistribution rights'
Write-Host '[10/10] PASS first-contact trust covers executable-code/security consent without making the joining player certify third-party redistribution rights.'

Write-Host ''
Write-Host 'PASS: AutoModSync 2.6.1 security/password/preflight-isolation contract is present.'
