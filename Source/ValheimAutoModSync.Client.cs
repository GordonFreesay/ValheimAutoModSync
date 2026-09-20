using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

[assembly: AssemblyTitle("Valheim AutoModSync Client")]
[assembly: AssemblyDescription("Client-side Valheim plugin synchronization, trust, verification, restart, and reconnect component.")]
[assembly: AssemblyCompany("GordonFreesay")]
[assembly: AssemblyProduct("Valheim AutoModSync")]
[assembly: AssemblyVersion("2.5.0.0")]
[assembly: AssemblyFileVersion("2.5.0.0")]

namespace ValheimAutoModSync
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ClientPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.gordonfreesay.valheimautomodsync.client";
        public const string PluginName = "Valheim AutoModSync Client";
        public const string PluginVersion = "2.5.0";
        public const int ProtocolVersion = 4;

        private const string RpcHello = "AMS4_Hello";
        private const string RpcAck = "AMS4_Ack";
        private const string RpcManifestBegin = "AMS4_ManifestBegin";
        private const string RpcManifestChunk = "AMS4_ManifestChunk";
        private const string RpcManifestEnd = "AMS4_ManifestEnd";
        private const string RpcGetBundle = "AMS4_GetBundle";
        private const string RpcGetBundleChunk = "AMS4_GetBundleChunk";
        private const string RpcBundleBegin = "AMS4_BundleBegin";
        private const string RpcBundleChunk = "AMS4_BundleChunk";
        private const string RpcBundleEnd = "AMS4_BundleEnd";
        private const string RpcError = "AMS4_Error";

        private static ClientPlugin _instance;
        private static ZRpc _pendingRpc;
        private static string _pendingPassword = "";
        private static string _reconnectHost = "";
        private static int _reconnectBackend = -1;
        private static string _capturedServerTarget = "";
        private static int _capturedServerBackend = -1;
        private static bool _waitingForServer;
        private static bool _serverRecognized;
        private static bool _serverAcknowledged;
        private static bool _allowPeerInfo;
        private static bool _preflightGateActive;
        private static bool _allowServerHandshake;
        private static bool _serverHandshakeHeld;
        private static DateTime _helloSentUtc;
        private static readonly HashSet<ZRpc> Registered = new HashSet<ZRpc>();
        private static readonly HashSet<ZRpc> PreflightComplete = new HashSet<ZRpc>();

        private static int _manifestPartCount;
        private static string _manifestSignature = "";
        private static string _serverPublicKeyXml = "";
        private static string _serverFingerprint = "";
        private static readonly Dictionary<int, string> ManifestParts = new Dictionary<int, string>();
        private static readonly List<ManifestEntry> NeededFiles = new List<ManifestEntry>();
        private static FileStream _bundleStream;
        private static string _bundlePath = "";
        private static string _bundleSha256 = "";
        private static long _bundleSize;
        private static long _bundleBytesReceived;
        private static int _bundleNextChunk;
        private static int _bundleTotalChunks;
        private static bool _restartRequested;
        private static DateTime _quitAfterUtc = DateTime.MinValue;
        private static bool _quitIssued;
        private static bool _overlayVisible;
        private static string _overlayStatus = "";
        private static string _overlayCurrentFile = "";
        private static int _overlayTotalFiles;
        private static int _overlayCompletedFiles;
        private static float _overlayFileProgress;
        private static bool _overlayBundleMode;
        private static long _overlayBytesReceived;
        private static long _overlayBytesTotal;
        private static string _startupReconnectTarget = "";
        private static DateTime _startupReconnectNextUtc = DateTime.MinValue;
        private static int _startupReconnectAttempts;
        private static bool _startupReconnectFinished;
        private static bool _startupReconnectDispatched;
        private static bool _startupReconnectCharacterStartPending;
        private static DateTime _startupReconnectCharacterStartUtc = DateTime.MinValue;
        private static int _startupReconnectBackend = -1;
        private static bool _startupReconnectForceBackend;
        private static readonly List<string> PendingRelativePaths = new List<string>();

        private sealed class ManifestEntry
        {
            public char Kind;
            public string Sha256;
            public long Size;
            public string RelativePath;
        }

        // Intent: BepInEx client entry point; initializes only on the playable Valheim process, applies any safe leftover staging, restores reconnect state, and installs the Harmony hooks that drive synchronization.
        // Workflow: the network hooks are installed before joining servers so AutoModSync can preflight before third-party compatibility checks.
        private void Awake()
        {
            _instance = this;
            if (IsDedicatedServerProcess())
            {
                Logger.LogInfo("AutoModSync client role disabled on the Valheim dedicated-server process.");
                return;
            }
            try
            {
                HideBepInExConsoleAndDisableFutureConsole();
                ApplyPreviouslyStagedFilesIfPossible();
                LoadStartupReconnectRequest();
                Harmony harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(OnNewConnectionPatch));
                harmony.PatchAll(typeof(InvokeServerHandshakeGatePatch));
                harmony.PatchAll(typeof(SendPeerInfoPatch));
                harmony.PatchAll(typeof(ReconnectCharacterSelectionPatch));
                harmony.PatchAll(typeof(ReconnectJoinServerPatch));
                harmony.PatchAll(typeof(CaptureOriginalJoinRequestPatch));
                PatchServerTargetCapture(harmony);
                Logger.LogInfo("AutoModSync client ready. Server discovery uses Valheim's existing connection; no extra port is required.");
            }
            catch (Exception ex)
            {
                Logger.LogError("AutoModSync client startup failed: " + ex);
            }
        }

        // Intent: Prevents the client role from activating inside valheim_server.exe when the same package is installed on a dedicated server.
        private static bool IsDedicatedServerProcess()
        {
            try
            {
                string processName = Process.GetCurrentProcess().ProcessName ?? "";
                return processName.IndexOf("valheim_server", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        // Intent: Unity per-frame state machine for delayed quit, automatic reconnect, character continuation, and preflight timeouts.
        // Compatibility: non-AutoModSync servers are released after a short discovery window; acknowledged AutoModSync servers get a longer manifest-start window.
        private void Update()
        {
            if (_restartRequested && !_quitIssued && _quitAfterUtc != DateTime.MinValue && DateTime.UtcNow >= _quitAfterUtc)
            {
                _quitIssued = true;
                Application.Quit();
                return;
            }

            if (!_restartRequested && !_startupReconnectFinished && !_startupReconnectDispatched && _startupReconnectTarget.Length > 0 && DateTime.UtcNow >= _startupReconnectNextUtc)
            {
                TryStartupReconnect();
            }

            if (_startupReconnectCharacterStartPending && DateTime.UtcNow >= _startupReconnectCharacterStartUtc)
            {
                _startupReconnectCharacterStartPending = false;
                try
                {
                    if (FejdStartup.instance != null)
                    {
                        FejdStartup.instance.OnCharacterStart();
                        Logger.LogInfo("AutoModSync automatically started the previously selected character for reconnect.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("AutoModSync could not automatically start the selected character during reconnect: " + ex.Message);
                }
            }

            if (_waitingForServer && _pendingRpc != null && _helloSentUtc != DateTime.MinValue)
            {
                double elapsed = (DateTime.UtcNow - _helloSentUtc).TotalSeconds;
                if (!_serverAcknowledged && !_serverRecognized && elapsed > 3.0)
                {
                    Logger.LogDebug("No AutoModSync server response; releasing the normal Valheim handshake.");
                    FailOpen("No AutoModSync preflight response was received.");
                }
                else if (_serverAcknowledged && !_serverRecognized && elapsed > 15.0)
                {
                    FailOpen("AutoModSync server acknowledged preflight but did not begin a manifest within 15 seconds.");
                }
            }
        }

        // Intent: Draws the small in-game synchronization overlay and progress bar without depending on another UI framework.
        private void OnGUI()
        {
            if (!_overlayVisible) return;

            float width = Mathf.Min(560f, Mathf.Max(320f, Screen.width - 40f));
            float height = _overlayTotalFiles > 0 ? 190f : 135f;
            float left = (Screen.width - width) * 0.5f;
            float top = (Screen.height - height) * 0.5f;
            Rect panel = new Rect(left, top, width, height);

            GUI.depth = -1000;
            Color previousColor = GUI.color;
            Color previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.08f, 0.08f, 0.08f, 0.96f);
            GUI.Box(panel, GUIContent.none);
            GUI.backgroundColor = previousBackground;

            GUIStyle titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.alignment = TextAnchor.MiddleCenter;
            titleStyle.fontSize = 22;
            titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.normal.textColor = Color.white;

            GUIStyle statusStyle = new GUIStyle(GUI.skin.label);
            statusStyle.alignment = TextAnchor.MiddleCenter;
            statusStyle.fontSize = 16;
            statusStyle.wordWrap = true;
            statusStyle.normal.textColor = Color.white;

            GUIStyle detailStyle = new GUIStyle(GUI.skin.label);
            detailStyle.alignment = TextAnchor.MiddleCenter;
            detailStyle.fontSize = 13;
            detailStyle.wordWrap = true;
            detailStyle.normal.textColor = new Color(0.86f, 0.86f, 0.86f, 1f);

            GUI.Label(new Rect(left + 20f, top + 14f, width - 40f, 32f), "AutoModSync", titleStyle);
            GUI.Label(new Rect(left + 25f, top + 50f, width - 50f, 48f), _overlayStatus ?? "", statusStyle);

            if (_overlayTotalFiles > 0)
            {
                string detail;
                float overall;
                if (_overlayBundleMode)
                {
                    detail = _overlayTotalFiles.ToString(CultureInfo.InvariantCulture) + " changed file(s)  •  " + FormatBytes(_overlayBytesReceived) + " / " + FormatBytes(_overlayBytesTotal);
                    overall = _overlayBytesTotal <= 0 ? 0f : Mathf.Clamp01((float)(_overlayBytesReceived / (double)_overlayBytesTotal));
                }
                else
                {
                    int displayFile = Math.Min(_overlayCompletedFiles + 1, _overlayTotalFiles);
                    detail = _overlayCurrentFile.Length > 0
                        ? "File " + displayFile.ToString(CultureInfo.InvariantCulture) + " of " + _overlayTotalFiles.ToString(CultureInfo.InvariantCulture) + ": " + _overlayCurrentFile
                        : _overlayCompletedFiles.ToString(CultureInfo.InvariantCulture) + " of " + _overlayTotalFiles.ToString(CultureInfo.InvariantCulture) + " files complete";
                    overall = (_overlayCompletedFiles + Mathf.Clamp01(_overlayFileProgress)) / (float)_overlayTotalFiles;
                    overall = Mathf.Clamp01(overall);
                }
                GUI.Label(new Rect(left + 25f, top + 99f, width - 50f, 34f), detail, detailStyle);
                Rect bar = new Rect(left + 35f, top + 145f, width - 70f, 18f);
                GUI.color = new Color(0.20f, 0.20f, 0.20f, 1f);
                GUI.DrawTexture(bar, Texture2D.whiteTexture);
                if (overall > 0f)
                {
                    GUI.color = new Color(0.72f, 0.72f, 0.72f, 1f);
                    GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * overall, bar.height), Texture2D.whiteTexture);
                }
                GUI.color = previousColor;
                GUI.Label(new Rect(left + 35f, top + 164f, width - 70f, 20f), Math.Round(overall * 100f).ToString(CultureInfo.InvariantCulture) + "%", detailStyle);
            }

            GUI.color = previousColor;
        }

        // Intent: Sets the current synchronization status/detail text and marks the overlay visible.
        private static void ShowSyncOverlay(string status, string currentFile)
        {
            _overlayStatus = status ?? "";
            _overlayCurrentFile = currentFile ?? "";
            _overlayVisible = true;
        }

        // Intent: Clears all overlay/progress state when synchronization is finished or the normal handshake is resumed.
        private static void HideSyncOverlay()
        {
            _overlayVisible = false;
            _overlayStatus = "";
            _overlayCurrentFile = "";
            _overlayTotalFiles = 0;
            _overlayCompletedFiles = 0;
            _overlayFileProgress = 0f;
            _overlayBundleMode = false;
            _overlayBytesReceived = 0L;
            _overlayBytesTotal = 0L;
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class OnNewConnectionPatch
        {
            // Intent: On the client side, registers AutoModSync RPC handlers and arms the preflight gate before Valheim's OnNewConnection body can send ServerHandshake.
            // This early hook is what prevents Jotunn/other validators from rejecting a client before required files can be synchronized.
            private static void Prefix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance == null || __instance.IsServer()) return;
                if (peer == null || peer.m_rpc == null) return;
                if (_startupReconnectDispatched)
                {
                    DeleteReconnectToken();
                    _startupReconnectFinished = true;
                    if (_instance != null) _instance.Logger.LogInfo("AutoModSync reconnect created an outgoing Valheim connection; reconnect token cleared.");
                }
                RegisterRpc(peer.m_rpc);
                PreparePreflightGate(peer.m_rpc);
            }

            // Intent: After Valheim has registered its normal connection RPCs, sends the AutoModSync preflight probe on the same ZRpc connection.
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance == null || __instance.IsServer()) return;
                if (peer == null || peer.m_rpc == null) return;
                RegisterRpc(peer.m_rpc);
                BeginPreflightProbe(peer.m_rpc);
            }
        }

        [HarmonyPatch(typeof(ZRpc), "Invoke", new Type[] { typeof(string), typeof(object[]) })]
        private static class InvokeServerHandshakeGatePatch
        {
            [HarmonyPriority(Priority.First)]
            // Intent: Intercepts only the outgoing vanilla ServerHandshake while AutoModSync preflight is active.
            // Compatibility: every other RPC is untouched; the held ServerHandshake is replayed unchanged once preflight succeeds or fails open.
            private static bool Prefix(ZRpc __instance, string method, object[] parameters)
            {
                if (_allowServerHandshake) return true;
                if (!_preflightGateActive || __instance == null || __instance != _pendingRpc) return true;
                if (!String.Equals(method, "ServerHandshake", StringComparison.Ordinal)) return true;

                _serverHandshakeHeld = true;
                if (_instance != null) _instance.Logger.LogDebug("AutoModSync held Valheim ServerHandshake until preflight completes.");
                return false;
            }
        }

        [HarmonyPatch(typeof(FejdStartup), "ShowCharacterSelection")]
        private static class ReconnectCharacterSelectionPatch
        {
            // Intent: During restart reconnect, notices when Valheim reaches character selection and schedules the normal selected-character start action.
            private static void Postfix()
            {
                if (!_startupReconnectDispatched || _restartRequested) return;
                _startupReconnectCharacterStartPending = true;
                _startupReconnectCharacterStartUtc = DateTime.UtcNow.AddMilliseconds(650.0);
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync reconnect reached character selection; starting the selected character automatically.");
            }
        }

        [HarmonyPatch(typeof(FejdStartup), "JoinServer")]
        private static class ReconnectJoinServerPatch
        {
            // Intent: Restores the originally captured online backend just before Valheim joins the saved dedicated endpoint, preserving Steam/crossplay routing semantics.
            private static void Postfix(FejdStartup __instance)
            {
                if (!_startupReconnectForceBackend || _startupReconnectBackend < 0 || __instance == null) return;
                try
                {
                    ServerJoinData data = __instance.GetServerToJoin();
                    if (!data.IsValid || (int)data.m_type != 3) return;
                    ServerJoinDataDedicated dedicated = data.Dedicated;
                    string host = dedicated.GetHost();
                    int port = dedicated.m_port;
                    if (String.IsNullOrEmpty(host) || port <= 0) return;

                    ZNet.SetServerHost(host, port, (OnlineBackendType)_startupReconnectBackend);
                    _startupReconnectForceBackend = false;
                    if (_instance != null) _instance.Logger.LogInfo("AutoModSync restored reconnect backend " + _startupReconnectBackend.ToString(CultureInfo.InvariantCulture) + " for " + host + ":" + port.ToString(CultureInfo.InvariantCulture) + ".");
                }
                catch (Exception ex)
                {
                    if (_instance != null) _instance.Logger.LogWarning("AutoModSync could not restore reconnect backend: " + ex.Message);
                }
            }
        }

        [HarmonyPatch(typeof(FejdStartup), "ProceedJoinRequest", new Type[] { typeof(ServerJoinData) })]
        private static class CaptureOriginalJoinRequestPatch
        {
            // Intent: Captures the user's original ServerJoinData before Valheim transforms it, giving restart logic the cleanest available host/port source.
            private static void Prefix(ServerJoinData joinData)
            {
                CaptureOriginalJoinRequest(joinData);
            }
        }

        [HarmonyPatch(typeof(ZNet), "SendPeerInfo")]
        private static class SendPeerInfoPatch
        {
            // Intent: Legacy/fallback SendPeerInfo gate for connection paths that bypass the new pre-handshake gate.
            // Compatibility: a connection already completed by preflight passes through untouched; otherwise the older AMS4 probe behavior is retained.
            private static bool Prefix(ZNet __instance, ZRpc rpc, string password)
            {
                if (__instance == null || __instance.IsServer() || _allowPeerInfo) return true;
                if (rpc == null) return true;
                if (PreflightComplete.Contains(rpc)) return true;
                if (_preflightGateActive && rpc == _pendingRpc) return true;

                RegisterRpc(rpc);
                _pendingRpc = rpc;
                _pendingPassword = password ?? "";
                _reconnectHost = GetReconnectTarget(rpc);
                _reconnectBackend = _capturedServerBackend >= 0 ? _capturedServerBackend : GetCurrentOnlineBackend();
                if (_instance != null)
                {
                    if (_reconnectHost.Length > 0)
                    {
                        _instance.Logger.LogInfo("AutoModSync reconnect capture: endpoint=" + _reconnectHost + ", backend=" + _reconnectBackend.ToString(CultureInfo.InvariantCulture) + ".");
                        _instance.Logger.LogInfo("AutoModSync will reconnect to " + _reconnectHost + " after the required restart.");
                    }
                    else
                    {
                        _instance.Logger.LogWarning("AutoModSync reconnect capture failed: no usable endpoint was available. Restart will still occur, but automatic reconnect is disabled for this sync.");
                    }
                }
                _waitingForServer = true;
                _serverRecognized = false;
                _serverAcknowledged = false;
                _preflightGateActive = false;
                _helloSentUtc = DateTime.UtcNow;
                ResetManifestState();

                try
                {
                    ZPackage hello = new ZPackage();
                    hello.Write(ProtocolVersion);
                    hello.Write(PluginVersion);
                    rpc.Invoke(RpcHello, new object[] { hello });
                    if (_instance != null) _instance.Logger.LogDebug("AutoModSync probe sent before PeerInfo.");
                }
                catch (Exception ex)
                {
                    if (_instance != null) _instance.Logger.LogWarning("AutoModSync probe failed; continuing normally: " + ex.Message);
                    ContinuePeerInfo();
                }
                return false;
            }
        }

        // Intent: Registers all AMS4 RPC names exactly once on a ZRpc so manifest and bundle messages can be handled without replacing Valheim's own RPC table entries.
        private static void RegisterRpc(ZRpc rpc)
        {
            if (rpc == null || Registered.Contains(rpc)) return;
            try
            {
                rpc.Register<ZPackage>(RpcHello, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcAck, new Action<ZRpc, ZPackage>(RPC_Ack));
                rpc.Register<ZPackage>(RpcManifestBegin, new Action<ZRpc, ZPackage>(RPC_ManifestBegin));
                rpc.Register<ZPackage>(RpcManifestChunk, new Action<ZRpc, ZPackage>(RPC_ManifestChunk));
                rpc.Register<ZPackage>(RpcManifestEnd, new Action<ZRpc, ZPackage>(RPC_ManifestEnd));
                rpc.Register<ZPackage>(RpcGetBundle, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcGetBundleChunk, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcBundleBegin, new Action<ZRpc, ZPackage>(RPC_BundleBegin));
                rpc.Register<ZPackage>(RpcBundleChunk, new Action<ZRpc, ZPackage>(RPC_BundleChunk));
                rpc.Register<ZPackage>(RpcBundleEnd, new Action<ZRpc, ZPackage>(RPC_BundleEnd));
                rpc.Register<ZPackage>(RpcError, new Action<ZRpc, ZPackage>(RPC_Error));
                Registered.Add(rpc);
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Could not register AutoModSync RPCs: " + ex.Message);
            }
        }

        // Intent: Safe placeholder for protocol messages that are outbound-only on the client; receiving one requires no action.
        private static void RPC_NoOp(ZRpc rpc, ZPackage pkg) { }

        // Intent: Handles the optional 2.5.0 preflight acknowledgement sent before server manifest hashing.
        // Workflow: validates protocol version, records that an AutoModSync server responded, and extends the timeout while manifest generation proceeds.
        private static void RPC_Ack(ZRpc rpc, ZPackage pkg)
        {
            if (!_waitingForServer || rpc != _pendingRpc) return;
            try
            {
                int protocol = pkg.ReadInt();
                string serverVersion = "";
                try { serverVersion = pkg.ReadString(); } catch { serverVersion = ""; }
                if (protocol != ProtocolVersion) throw new InvalidDataException("AutoModSync protocol mismatch during preflight acknowledgement.");
                _serverAcknowledged = true;
                if (_instance != null) _instance.Logger.LogDebug("AutoModSync preflight acknowledged by server " + serverVersion + ".");
            }
            catch (Exception ex)
            {
                FailOpen("Invalid AutoModSync preflight acknowledgement: " + ex.Message);
            }
        }

        // Intent: Validates and records the signed-manifest header.
        // Security: checks protocol, capabilities, bounded manifest size, public-key/signature presence, derives the server fingerprint, then starts the visible comparison phase.
        private static void RPC_ManifestBegin(ZRpc rpc, ZPackage pkg)
        {
            if (!_waitingForServer || rpc != _pendingRpc) return;
            try
            {
                int protocol = pkg.ReadInt();
                int parts = pkg.ReadInt();
                string byteLength = pkg.ReadString();
                string publicKeyXml = pkg.ReadString();
                string signature = pkg.ReadString();
                string capabilities = "";
                try { capabilities = pkg.ReadString(); } catch { capabilities = ""; }
                if (!String.Equals(capabilities, "bundle1", StringComparison.Ordinal)) throw new InvalidDataException("Server does not support compressed AutoModSync packages.");
                if (protocol != ProtocolVersion || parts < 1 || parts > 10000) throw new InvalidDataException("Invalid AutoModSync manifest header.");
                long ignored;
                if (!long.TryParse(byteLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out ignored) || ignored < 0 || ignored > 32L * 1024L * 1024L)
                    throw new InvalidDataException("Invalid AutoModSync manifest size.");
                if (String.IsNullOrEmpty(publicKeyXml) || publicKeyXml.Length > 16384 || String.IsNullOrEmpty(signature) || signature.Length > 16384)
                    throw new InvalidDataException("Invalid AutoModSync server identity.");
                _serverAcknowledged = true;
                _serverRecognized = true;
                _manifestPartCount = parts;
                _serverPublicKeyXml = publicKeyXml;
                _manifestSignature = signature;
                _serverFingerprint = Fingerprint(publicKeyXml);
                ManifestParts.Clear();
                ShowSyncOverlay("Checking server mods...", "");
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync server detected; checking required mods before joining.");
            }
            catch (Exception ex)
            {
                FailOpen("Bad AutoModSync manifest header: " + ex.Message);
            }
        }

        // Intent: Accepts one numbered manifest text chunk only for the active server/RPC and stores it by index for ordered reconstruction.
        private static void RPC_ManifestChunk(ZRpc rpc, ZPackage pkg)
        {
            if (!_serverRecognized || rpc != _pendingRpc) return;
            try
            {
                int index = pkg.ReadInt();
                string text = pkg.ReadString();
                if (index < 0 || index >= _manifestPartCount) throw new InvalidDataException("Manifest part index out of range.");
                ManifestParts[index] = text ?? "";
            }
            catch (Exception ex)
            {
                FailOpen("Manifest transfer failed: " + ex.Message);
            }
        }

        // Intent: Reassembles and cryptographically verifies the complete server manifest, compares local files, then either resumes Valheim immediately or requests the verified change bundle.
        // Trust: executable transfer begins only after the server fingerprint is accepted.
        private static void RPC_ManifestEnd(ZRpc rpc, ZPackage pkg)
        {
            if (!_serverRecognized || rpc != _pendingRpc) return;
            try
            {
                int expected = pkg.ReadInt();
                if (expected != _manifestPartCount || ManifestParts.Count != _manifestPartCount)
                    throw new InvalidDataException("Manifest was incomplete.");

                StringBuilder sb = new StringBuilder();
                int i;
                for (i = 0; i < _manifestPartCount; i++)
                {
                    string part;
                    if (!ManifestParts.TryGetValue(i, out part)) throw new InvalidDataException("Manifest part missing.");
                    sb.Append(part);
                }
                string manifest = sb.ToString();
                if (!VerifyManifestSignature(_serverPublicKeyXml, _manifestSignature, Encoding.UTF8.GetBytes(manifest)))
                    throw new CryptographicException("AutoModSync server signature verification failed.");

                BuildNeededList(manifest);
                if (NeededFiles.Count == 0)
                {
                    HideSyncOverlay();
                    if (_instance != null) _instance.Logger.LogInfo("AutoModSync: client mods already match the server.");
                    ResumeNormalHandshake();
                }
                else
                {
                    if (!EnsureServerTrusted(_serverFingerprint))
                    {
                        FailOpen("AutoModSync server was not trusted by the user.");
                        return;
                    }
                    _overlayTotalFiles = NeededFiles.Count;
                    _overlayCompletedFiles = 0;
                    _overlayFileProgress = 0f;
                    _overlayBundleMode = true;
                    _overlayBytesReceived = 0L;
                    _overlayBytesTotal = 0L;
                    ShowSyncOverlay("Preparing compressed mod package...", "");
                    if (_instance != null) _instance.Logger.LogInfo("AutoModSync: requesting one compressed package containing " + NeededFiles.Count + " missing/changed file(s).");
                    RequestBundle();
                }
            }
            catch (Exception ex)
            {
                FailOpen("AutoModSync manifest verification failed: " + ex.Message);
            }
        }

        // Intent: Parses signed manifest rows into the exact set of missing or hash-mismatched plugin files.
        // Package-manager safeguard: ignores AutoModSync-owned files when the current AutoModSync installation is itself managed by a profile.
        private static void BuildNeededList(string manifest)
        {
            NeededFiles.Clear();
            PendingRelativePaths.Clear();
            string[] lines = manifest.Replace("\r", "").Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int i;
            for (i = 0; i < lines.Length; i++)
            {
                string[] fields = lines[i].Split(new char[] { '\t' }, 4);
                if (fields.Length != 4 || fields[0].Length != 1) continue;
                char kind = fields[0][0];
                if (kind != 'P') continue;
                long size;
                if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 0) continue;
                string rel = NormalizeRelative(fields[3]);
                if (rel.Length == 0) continue;
                if (IsPackageManagedAutoModSync() && IsAutoModSyncOwnedRelativePath(rel))
                {
                    if (_instance != null) _instance.Logger.LogDebug("Ignoring server-advertised package-managed AutoModSync file: " + rel);
                    continue;
                }
                ManifestEntry e = new ManifestEntry();
                e.Kind = kind;
                e.Sha256 = fields[1];
                e.Size = size;
                e.RelativePath = rel;
                string local = SafeTargetPath(kind, rel);
                if (!File.Exists(local) || !ConstantEquals(Sha256File(local), e.Sha256)) NeededFiles.Add(e);
            }
        }

        // Intent: Detects whether AutoModSync is running from a package-manager subdirectory rather than directly at BepInEx/plugins.
        private static bool IsPackageManagedAutoModSync()
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(typeof(ClientPlugin).Assembly.Location);
                if (String.IsNullOrEmpty(assemblyDir)) return false;

                string pluginRoot = Path.GetFullPath(Paths.PluginPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string actualDir = Path.GetFullPath(assemblyDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (String.Equals(pluginRoot, actualDir, StringComparison.OrdinalIgnoreCase)) return false;

                return File.Exists(Path.Combine(actualDir, "ValheimAutoModSync.Apply.exe"));
            }
            catch
            {
                return false;
            }
        }

        // Intent: Identifies filenames owned by AutoModSync itself so a package-managed install cannot be overwritten by a standalone server payload.
        private static bool IsAutoModSyncOwnedRelativePath(string relative)
        {
            string name;
            try
            {
                name = Path.GetFileName((relative ?? "").Replace('/', Path.DirectorySeparatorChar));
            }
            catch
            {
                return false;
            }

            return String.Equals(name, "ValheimAutoModSync.Client.dll", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "ValheimAutoModSync.Server.dll", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "ValheimAutoModSync.Apply.exe", StringComparison.OrdinalIgnoreCase);
        }

        // Intent: Requests one compressed bundle containing only the manifest entries the client proved it needs.
        private static void RequestBundle()
        {
            CloseBundleStream();
            if (_pendingRpc == null || NeededFiles.Count == 0) return;
            ZPackage request = new ZPackage();
            request.Write(NeededFiles.Count);
            int i;
            for (i = 0; i < NeededFiles.Count; i++) request.Write(NeededFiles[i].Kind + ":" + NeededFiles[i].RelativePath);
            _pendingRpc.Invoke(RpcGetBundle, new object[] { request });
        }

        // Intent: Validates the server's bundle header, creates the staging archive file, initializes byte/chunk counters, and requests the first chunk.
        private static void RPC_BundleBegin(ZRpc rpc, ZPackage pkg)
        {
            if (rpc != _pendingRpc || NeededFiles.Count == 0) return;
            try
            {
                string sizeText = pkg.ReadString();
                string sha = pkg.ReadString();
                int chunks = pkg.ReadInt();
                int files = pkg.ReadInt();
                long size;
                if (!long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 0 || chunks < 0 || files != NeededFiles.Count || String.IsNullOrEmpty(sha))
                    throw new InvalidDataException("Compressed package header did not match the requested sync.");

                string stagingRoot = Path.Combine(GetAutoModSyncRoot(), "staging");
                if (!Directory.Exists(stagingRoot)) Directory.CreateDirectory(stagingRoot);
                _bundlePath = Path.Combine(stagingRoot, "bundle.zip.amsnew");
                _bundleStream = new FileStream(_bundlePath, FileMode.Create, FileAccess.Write, FileShare.None);
                _bundleSha256 = sha;
                _bundleSize = size;
                _bundleBytesReceived = 0L;
                _bundleNextChunk = 0;
                _bundleTotalChunks = chunks;
                _overlayBundleMode = true;
                _overlayBytesReceived = 0L;
                _overlayBytesTotal = size;
                ShowSyncOverlay("Downloading compressed mod package...", "");
                RequestBundleChunk();
            }
            catch (Exception ex)
            {
                FailOpen("Could not prepare compressed mod download: " + ex.Message);
            }
        }

        // Intent: Appends exactly the next expected Base64-decoded bundle chunk to disk and advances the download state.
        // Security: out-of-order or excess chunks abort the sync instead of being accepted.
        private static void RPC_BundleChunk(ZRpc rpc, ZPackage pkg)
        {
            if (rpc != _pendingRpc || _bundleStream == null) return;
            try
            {
                int index = pkg.ReadInt();
                string encoded = pkg.ReadString();
                if (index != _bundleNextChunk || _bundleNextChunk >= _bundleTotalChunks) throw new InvalidDataException("Out-of-order compressed package chunk.");
                byte[] data = Convert.FromBase64String(encoded);
                _bundleStream.Write(data, 0, data.Length);
                _bundleBytesReceived += data.Length;
                _bundleNextChunk++;
                _overlayBytesReceived = _bundleBytesReceived;
                _overlayFileProgress = _bundleTotalChunks <= 0 ? 1f : Mathf.Clamp01(_bundleNextChunk / (float)_bundleTotalChunks);
                RequestBundleChunk();
            }
            catch (Exception ex)
            {
                FailOpen("Compressed mod package download failed: " + ex.Message);
            }
        }

        // Intent: Requests the current next bundle-chunk index; the server uses this pull model to keep transfer ordering deterministic.
        private static void RequestBundleChunk()
        {
            if (_pendingRpc == null || _bundleStream == null) return;
            ZPackage request = new ZPackage();
            request.Write(_bundleNextChunk);
            _pendingRpc.Invoke(RpcGetBundleChunk, new object[] { request });
        }

        // Intent: Finalizes the compressed bundle, verifies its declared hash/size/file count, extracts verified files into staging, and starts the restart/apply sequence.
        private static void RPC_BundleEnd(ZRpc rpc, ZPackage pkg)
        {
            if (rpc != _pendingRpc || NeededFiles.Count == 0) return;
            try
            {
                string sha = pkg.ReadString();
                int files = pkg.ReadInt();
                if (!ConstantEquals(sha, _bundleSha256) || files != NeededFiles.Count) throw new InvalidDataException("Compressed package completion did not match its header.");
                if (_bundleNextChunk != _bundleTotalChunks) throw new InvalidDataException("Compressed package completed before all chunks were received.");
                CloseBundleStream();
                FileInfo fi = new FileInfo(_bundlePath);
                if (!fi.Exists || fi.Length != _bundleSize || !ConstantEquals(Sha256File(_bundlePath), _bundleSha256))
                    throw new InvalidDataException("Compressed package failed SHA-256 verification.");

                _overlayBytesReceived = _bundleSize;
                ShowSyncOverlay("Verifying and unpacking required mods...", "");
                ExtractBundleToStaging();
                try { File.Delete(_bundlePath); } catch { }
                _overlayCompletedFiles = _overlayTotalFiles;
                _overlayFileProgress = 1f;
                _overlayBundleMode = false;
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync compressed package verified and unpacked: " + NeededFiles.Count + " changed file(s).");
                BeginApplyAndRestart();
            }
            catch (Exception ex)
            {
                FailOpen("Compressed mod package verification failed: " + ex.Message);
            }
        }

        // Intent: Extracts only files explicitly present in the signed NeededFiles set into AutoModSync staging.
        // Security: rejects unsafe, duplicate, unexpected, missing, wrong-size, or wrong-hash archive entries before any live plugin is replaced.
        private static void ExtractBundleToStaging()
        {
            Dictionary<string, ManifestEntry> expected = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            int i;
            for (i = 0; i < NeededFiles.Count; i++)
            {
                ManifestEntry e = NeededFiles[i];
                string entryName = "plugins/" + e.RelativePath.Replace('\\', '/');
                expected[entryName] = e;
            }

            HashSet<string> extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (FileStream input = new FileStream(_bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (ZipArchive archive = new ZipArchive(input, ZipArchiveMode.Read, false))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = NormalizeZipEntry(entry.FullName);
                    if (name.Length == 0) throw new InvalidDataException("Compressed package contained an unsafe path.");
                    ManifestEntry expectedEntry;
                    if (!expected.TryGetValue(name, out expectedEntry)) throw new InvalidDataException("Compressed package contained an unexpected file: " + name);
                    if (!extracted.Add(name)) throw new InvalidDataException("Compressed package contained a duplicate file: " + name);

                    string stagingRoot = Path.Combine(GetAutoModSyncRoot(), "staging", "plugins");
                    string output = SafeUnder(stagingRoot, expectedEntry.RelativePath) + ".amsnew";
                    string parent = Path.GetDirectoryName(output);
                    if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                    using (Stream source = entry.Open())
                    using (FileStream destination = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        source.CopyTo(destination);
                    }

                    FileInfo outInfo = new FileInfo(output);
                    if (!outInfo.Exists || outInfo.Length != expectedEntry.Size || !ConstantEquals(Sha256File(output), expectedEntry.Sha256))
                        throw new InvalidDataException("Unpacked file failed signed-manifest verification: " + expectedEntry.RelativePath);
                    PendingRelativePaths.Add(expectedEntry.Kind + ":" + expectedEntry.RelativePath);
                }
            }

            if (extracted.Count != expected.Count) throw new InvalidDataException("Compressed package did not contain every requested file.");
        }

        // Intent: Canonicalizes a ZIP entry to forward-slash relative form and rejects empty, dot, parent, or directory entries.
        private static string NormalizeZipEntry(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            value = value.Replace('\\', '/').TrimStart('/');
            if (value.EndsWith("/", StringComparison.Ordinal)) return "";
            string[] parts = value.Split('/');
            int i;
            for (i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0 || parts[i] == "." || parts[i] == "..") return "";
            }
            return String.Join("/", parts);
        }

        // Intent: Formats byte counts into human-readable B/KB/MB/GB strings for logs and the sync overlay.
        private static string FormatBytes(long value)
        {
            double n = value;
            string[] units = new string[] { "B", "KB", "MB", "GB" };
            int unit = 0;
            while (n >= 1024.0 && unit < units.Length - 1) { n /= 1024.0; unit++; }
            return n.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        // Intent: Handles a server-reported AMS error for the active RPC and falls back to normal Valheim behavior without applying files.
        private static void RPC_Error(ZRpc rpc, ZPackage pkg)
        {
            if (rpc != _pendingRpc) return;
            string message = "Server reported an AutoModSync error.";
            try { message = pkg.ReadString(); } catch { }
            FailOpen(message);
        }

        // Intent: Persists the verified pending-file list and reconnect token, launches the external apply helper, disconnects cleanly, then schedules Valheim to quit.
        // Reason: loaded plugin DLLs cannot be safely replaced in-process, so file replacement occurs after this process exits.
        private static void BeginApplyAndRestart()
        {
            if (_restartRequested) return;
            _restartRequested = true;
            try
            {
                string amsRoot = GetAutoModSyncRoot();
                if (!Directory.Exists(amsRoot)) Directory.CreateDirectory(amsRoot);
                string pending = Path.Combine(amsRoot, "pending.txt");
                File.WriteAllLines(pending, PendingRelativePaths.ToArray(), new UTF8Encoding(false));
                string reconnect = Path.Combine(amsRoot, "reconnect.txt");
                bool reconnectAvailable = !String.IsNullOrEmpty(_reconnectHost);
                if (reconnectAvailable)
                {
                    string reconnectPayload = _reconnectBackend.ToString(CultureInfo.InvariantCulture) + "|" + _reconnectHost;
                    File.WriteAllText(reconnect, Convert.ToBase64String(Encoding.UTF8.GetBytes(reconnectPayload)), new UTF8Encoding(false));
                    if (_instance != null) _instance.Logger.LogInfo("AutoModSync persisted reconnect token: backend=" + _reconnectBackend.ToString(CultureInfo.InvariantCulture) + ", endpoint=" + _reconnectHost + ".");
                }
                else
                {
                    try { if (File.Exists(reconnect)) File.Delete(reconnect); } catch { }
                    if (_instance != null) _instance.Logger.LogWarning("AutoModSync has no reconnect endpoint; restarting without automatic reconnect.");
                }

                PersistPackageManagedLaunchContext(amsRoot);

                string helper = FindApplyHelper(amsRoot);
                if (!File.Exists(helper)) throw new FileNotFoundException("AutoModSync apply helper is missing.", helper);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = helper;
                psi.Arguments = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + " \"" + amsRoot.Replace("\"", "") + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);

                _overlayCompletedFiles = _overlayTotalFiles;
                _overlayFileProgress = 1f;
                ShowSyncOverlay(reconnectAvailable ? "Sync complete. Restarting Valheim and reconnecting to server..." : "Sync complete. Restarting Valheim...", "");
                if (_instance != null) _instance.Logger.LogInfo("Mods synchronized. Closing this Valheim instance cleanly before applying updates and relaunching.");
                try
                {
                    if (ZNet.instance != null)
                    {
                        MethodInfo disconnect = AccessTools.Method(typeof(ZNet), "Disconnect");
                        if (disconnect != null) disconnect.Invoke(ZNet.instance, null);
                    }
                }
                catch { }
                _quitAfterUtc = DateTime.UtcNow.AddMilliseconds(900.0);
            }
            catch (Exception ex)
            {
                _restartRequested = false;
                FailOpen("Mods downloaded but automatic apply/restart failed: " + ex.Message);
            }
        }

        // Intent: Saves the exact executable, working directory, and command-line arguments used by a package-managed Valheim launch.
        // Workflow: fields are Base64-encoded line-by-line so the apply helper can recreate the same Doorstop/profile launch after restart.
        private static void PersistPackageManagedLaunchContext(string amsRoot)
        {
            string launchContext = Path.Combine(amsRoot, "launch-context.txt");
            if (!IsPackageManagedAutoModSync())
            {
                try { if (File.Exists(launchContext)) File.Delete(launchContext); } catch { }
                return;
            }

            try
            {
                string executable = "";
                try
                {
                    using (Process current = Process.GetCurrentProcess())
                    {
                        if (current.MainModule != null) executable = current.MainModule.FileName;
                    }
                }
                catch { }

                if (String.IsNullOrEmpty(executable) || !File.Exists(executable))
                    throw new FileNotFoundException("Could not determine the running Valheim executable.", executable);

                string workingDirectory = Environment.CurrentDirectory ?? "";
                string[] commandLine = Environment.GetCommandLineArgs();
                List<string> lines = new List<string>();
                lines.Add("AMSLAUNCH1");
                lines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(executable)));
                lines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(workingDirectory)));

                int i;
                for (i = 1; i < commandLine.Length; i++)
                {
                    string argument = commandLine[i] ?? "";
                    lines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(argument)));
                }

                File.WriteAllLines(launchContext, lines.ToArray(), new UTF8Encoding(false));
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync saved package-manager launch context for modded restart.");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(launchContext)) File.Delete(launchContext); } catch { }
                throw new InvalidOperationException("Could not persist package-manager launch context.", ex);
            }
        }

        // Intent: Locates the apply helper beside a package-managed plugin when present, otherwise uses the normal BepInEx/AutoModSync helper path.
        private static string FindApplyHelper(string amsRoot)
        {
            try
            {
                string pluginDir = Path.GetDirectoryName(typeof(ClientPlugin).Assembly.Location);
                if (!String.IsNullOrEmpty(pluginDir))
                {
                    string packaged = Path.Combine(pluginDir, "ValheimAutoModSync.Apply.exe");
                    if (File.Exists(packaged)) return packaged;
                }
            }
            catch { }

            return Path.Combine(amsRoot, "ValheimAutoModSync.Apply.exe");
        }

        // Intent: Arms the 2.5.0 pre-handshake gate for one outgoing ZRpc before vanilla ServerHandshake is emitted.
        // Workflow: resets manifest state, records reconnect information, and marks this connection as waiting for an AMS preflight result.
        private static void PreparePreflightGate(ZRpc rpc)
        {
            if (rpc == null || PreflightComplete.Contains(rpc)) return;

            _pendingRpc = rpc;
            _pendingPassword = "";
            _waitingForServer = true;
            _serverRecognized = false;
            _serverAcknowledged = false;
            _preflightGateActive = true;
            _serverHandshakeHeld = false;
            _helloSentUtc = DateTime.MinValue;
            ResetManifestState();

            _reconnectHost = GetReconnectTarget(rpc);
            _reconnectBackend = _capturedServerBackend >= 0 ? _capturedServerBackend : GetCurrentOnlineBackend();
            if (_instance != null && _reconnectHost.Length > 0)
                _instance.Logger.LogDebug("AutoModSync preflight captured reconnect endpoint " + _reconnectHost + ".");
        }

        // Intent: Sends AMS4_Hello after Valheim has finished registering its base RPC handlers but before the held ServerHandshake is released.
        private static void BeginPreflightProbe(ZRpc rpc)
        {
            if (rpc == null || rpc != _pendingRpc || !_preflightGateActive || PreflightComplete.Contains(rpc)) return;
            _helloSentUtc = DateTime.UtcNow;
            try
            {
                ZPackage hello = new ZPackage();
                hello.Write(ProtocolVersion);
                hello.Write(PluginVersion);
                rpc.Invoke(RpcHello, new object[] { hello });
                if (_instance != null) _instance.Logger.LogDebug("AutoModSync preflight probe sent before Valheim ServerHandshake.");
            }
            catch (Exception ex)
            {
                FailOpen("AutoModSync preflight probe failed: " + ex.Message);
            }
        }

        // Intent: Completes preflight and resumes the untouched normal connection flow.
        // If the early gate held ServerHandshake it replays that RPC once; if running in the legacy SendPeerInfo path it delegates to ContinuePeerInfo instead.
        private static void ResumeNormalHandshake()
        {
            if (_preflightGateActive)
            {
                ZRpc rpc = _pendingRpc;
                bool releaseServerHandshake = _serverHandshakeHeld;
                _waitingForServer = false;
                _serverRecognized = false;
                _serverAcknowledged = false;
                _preflightGateActive = false;
                _serverHandshakeHeld = false;
                _pendingRpc = null;
                _helloSentUtc = DateTime.MinValue;
                if (!_restartRequested) HideSyncOverlay();
                ResetManifestState();

                if (rpc != null) PreflightComplete.Add(rpc);
                if (!releaseServerHandshake || rpc == null) return;

                try
                {
                    _allowServerHandshake = true;
                    rpc.Invoke("ServerHandshake", new object[0]);
                    if (_instance != null) _instance.Logger.LogDebug("AutoModSync released Valheim ServerHandshake after preflight.");
                }
                catch (Exception ex)
                {
                    if (_instance != null) _instance.Logger.LogWarning("Could not resume Valheim ServerHandshake: " + ex.Message);
                }
                finally
                {
                    _allowServerHandshake = false;
                }
                return;
            }

            ContinuePeerInfo();
        }

        // Intent: Resumes the legacy SendPeerInfo path by invoking Valheim's original method under a one-call bypass flag so AutoModSync's own Harmony prefix does not intercept itself.
        private static void ContinuePeerInfo()
        {
            ZRpc rpc = _pendingRpc;
            string password = _pendingPassword;
            _waitingForServer = false;
            _serverRecognized = false;
            _pendingRpc = null;
            if (!_restartRequested) HideSyncOverlay();
            ResetManifestState();
            if (rpc == null || ZNet.instance == null) return;
            try
            {
                MethodInfo send = AccessTools.Method(typeof(ZNet), "SendPeerInfo", new Type[] { typeof(ZRpc), typeof(string) });
                if (send == null) throw new MissingMethodException("ZNet.SendPeerInfo(ZRpc,string)");
                _allowPeerInfo = true;
                send.Invoke(ZNet.instance, new object[] { rpc, password });
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Could not resume Valheim handshake: " + ex.Message);
            }
            finally
            {
                _allowPeerInfo = false;
            }
        }

        // Intent: Central fail-open path for discovery/verification/transfer errors.
        // Safety: closes any bundle stream, applies no unverified files, logs the reason, and releases the normal Valheim handshake.
        private static void FailOpen(string reason)
        {
            CloseBundleStream();
            if (_instance != null) _instance.Logger.LogWarning(reason + " AutoModSync will not modify files for this connection; continuing Valheim normally.");
            ResumeNormalHandshake();
        }

        // Intent: Clears per-manifest and per-bundle state so stale data from one connection cannot contaminate the next synchronization attempt.
        private static void ResetManifestState()
        {
            ManifestParts.Clear();
            _manifestPartCount = 0;
            _manifestSignature = "";
            _serverPublicKeyXml = "";
            _serverFingerprint = "";
            NeededFiles.Clear();
            _bundleSize = 0L;
            _bundleSha256 = "";
            _bundleNextChunk = 0;
            _bundleTotalChunks = 0;
            _bundleBytesReceived = 0L;
            CloseBundleStream();
        }

        // Intent: Disposes and nulls the active staging bundle stream defensively; safe to call repeatedly.
        private static void CloseBundleStream()
        {
            if (_bundleStream != null)
            {
                try { _bundleStream.Dispose(); } catch { }
                _bundleStream = null;
            }
        }

        // Intent: Reads the one-shot reconnect token written before restart and schedules a Valheim-native reconnect after the main menu becomes usable.
        private void LoadStartupReconnectRequest()
        {
            try
            {
                string reconnect = Path.Combine(GetAutoModSyncRoot(), "reconnect.txt");
                if (!File.Exists(reconnect)) return;
                string b64 = File.ReadAllText(reconnect).Trim();
                if (b64.Length == 0) return;
                string target = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                _startupReconnectBackend = -1;
                int separator = target.IndexOf('|');
                if (separator > 0)
                {
                    int parsedBackend;
                    if (Int32.TryParse(target.Substring(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedBackend))
                    {
                        _startupReconnectBackend = parsedBackend;
                        target = target.Substring(separator + 1);
                    }
                }
                target = NormalizeReconnectTarget(target);
                if (target.Length == 0)
                {
                    Logger.LogWarning("AutoModSync restart reconnect token did not contain a usable direct endpoint; discarding it.");
                    DeleteReconnectToken();
                    return;
                }
                _startupReconnectTarget = target;
                _startupReconnectAttempts = 0;
                _startupReconnectFinished = false;
                _startupReconnectDispatched = false;
                _startupReconnectCharacterStartPending = false;
                _startupReconnectNextUtc = DateTime.UtcNow.AddSeconds(1.5);
                Logger.LogInfo("AutoModSync queued one-shot in-game reconnect to " + target + " (backend " + _startupReconnectBackend.ToString(CultureInfo.InvariantCulture) + ").");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("AutoModSync could not consume restart reconnect token: " + ex.Message);
            }
        }

        // Intent: Polls until Valheim's join UI/network objects are ready, then reconnects through ProceedJoinRequest or the closest native fallback.
        // Failure handling: retries for a bounded period and removes the token on final failure.
        private void TryStartupReconnect()
        {
            _startupReconnectAttempts++;
            if (_startupReconnectAttempts > 60)
            {
                _startupReconnectFinished = true;
                DeleteReconnectToken();
                Logger.LogWarning("AutoModSync automatic reconnect timed out; leaving the player at the main menu.");
                return;
            }

            FejdStartup startup = FejdStartup.instance;
            if (startup == null)
            {
                _startupReconnectNextUtc = DateTime.UtcNow.AddMilliseconds(500.0);
                return;
            }

            string host;
            ushort port;
            if (!TrySplitReconnectEndpoint(_startupReconnectTarget, out host, out port))
            {
                _startupReconnectFinished = true;
                DeleteReconnectToken();
                Logger.LogWarning("AutoModSync automatic reconnect target was invalid: " + _startupReconnectTarget);
                return;
            }

            try
            {
                if (ZSteamMatchmaking.instance == null)
                {
                    _startupReconnectNextUtc = DateTime.UtcNow.AddMilliseconds(500.0);
                    return;
                }

                ServerJoinDataDedicated dedicated = new ServerJoinDataDedicated(host, port);
                ServerJoinData joinData = new ServerJoinData(dedicated);
                MethodInfo proceed = AccessTools.Method(typeof(FejdStartup), "ProceedJoinRequest", new Type[] { typeof(ServerJoinData) });
                if (proceed != null)
                {
                    _startupReconnectDispatched = true;
                    _startupReconnectForceBackend = _startupReconnectBackend >= 0;
                    proceed.Invoke(startup, new object[] { joinData });
                    Logger.LogInfo("AutoModSync dispatched reconnect through FejdStartup.ProceedJoinRequest to " + _startupReconnectTarget + ".");
                    return;
                }

                _startupReconnectDispatched = true;
                _startupReconnectForceBackend = _startupReconnectBackend >= 0;
                startup.SetServerToJoin(joinData);
                startup.JoinServer();
                Logger.LogInfo("AutoModSync dispatched fallback Valheim-native reconnect to " + _startupReconnectTarget + ".");
            }
            catch (Exception ex)
            {
                if (_startupReconnectAttempts >= 60)
                {
                    _startupReconnectFinished = true;
                    DeleteReconnectToken();
                    Logger.LogWarning("AutoModSync automatic reconnect failed: " + ex.Message);
                }
                else
                {
                    Logger.LogDebug("AutoModSync reconnect UI is not ready yet: " + ex.Message);
                    _startupReconnectNextUtc = DateTime.UtcNow.AddMilliseconds(500.0);
                }
            }
        }

        // Intent: Removes reconnect.txt after it has been consumed, superseded, or abandoned so restarts cannot loop forever.
        private static void DeleteReconnectToken()
        {
            try
            {
                string reconnect = Path.Combine(GetAutoModSyncRoot(), "reconnect.txt");
                if (File.Exists(reconnect)) File.Delete(reconnect);
            }
            catch { }
        }

        // Intent: Parses normalized host:port or [IPv6]:port text into a host string and validated UInt16 port for Valheim's dedicated-server join objects.
        private static bool TrySplitReconnectEndpoint(string target, out string host, out ushort port)
        {
            host = "";
            port = 0;
            target = NormalizeReconnectTarget(target);
            if (target.Length == 0) return false;

            string portText;
            if (target.StartsWith("[", StringComparison.Ordinal))
            {
                int close = target.LastIndexOf(']');
                if (close <= 1 || close + 2 >= target.Length || target[close + 1] != ':') return false;
                host = target.Substring(1, close - 1);
                portText = target.Substring(close + 2);
            }
            else
            {
                int colon = target.LastIndexOf(':');
                if (colon <= 0 || colon + 1 >= target.Length) return false;
                host = target.Substring(0, colon);
                portText = target.Substring(colon + 1);
            }

            if (host.Length == 0) return false;
            int parsedPort;
            if (!Int32.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out parsedPort) || parsedPort < 1 || parsedPort > 65535) return false;
            port = (ushort)parsedPort;
            return true;
        }

        // Intent: Captures direct dedicated host/port from the original join request before connection setup; non-dedicated join types are left for later ZNet resolution.
        private static void CaptureOriginalJoinRequest(ServerJoinData joinData)
        {
            try
            {
                // A new join request supersedes any target captured from an earlier connection.
                _capturedServerTarget = "";
                _capturedServerBackend = -1;

                if (!joinData.IsValid) return;
                int joinType = (int)joinData.m_type;
                if (joinType != 3)
                {
                    if (_instance != null) _instance.Logger.LogDebug("AutoModSync observed original join request type " + joinType.ToString(CultureInfo.InvariantCulture) + "; waiting for Valheim to resolve it to a direct endpoint.");
                    return;
                }

                ServerJoinDataDedicated dedicated = joinData.Dedicated;
                string endpoint = BuildReconnectEndpoint(dedicated.GetHost(), Convert.ToInt32(dedicated.m_port, CultureInfo.InvariantCulture));
                if (endpoint.Length == 0)
                {
                    if (_instance != null) _instance.Logger.LogWarning("AutoModSync saw a dedicated join request but could not parse its host/port.");
                    return;
                }

                _capturedServerTarget = endpoint;
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync captured original Valheim join target: " + endpoint + ".");
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogDebug("AutoModSync could not capture original Valheim join request: " + ex.Message);
            }
        }

        // Intent: Dynamically patches every ZNet.SetServerHost overload with one postfix to capture whatever endpoint/backend the running Valheim version actually selects.
        // Compatibility: reflection avoids hard-coding one Valheim overload signature.
        private static void PatchServerTargetCapture(Harmony harmony)
        {
            try
            {
                MethodInfo postfixMethod = AccessTools.Method(typeof(ClientPlugin), "CaptureServerHostPostfix");
                if (postfixMethod == null) return;
                HarmonyMethod postfix = new HarmonyMethod(postfixMethod);
                MethodInfo[] methods = typeof(ZNet).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                int patched = 0;
                int i;
                for (i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!String.Equals(method.Name, "SetServerHost", StringComparison.Ordinal)) continue;
                    harmony.Patch(method, null, postfix);
                    patched++;
                }
                if (_instance != null && patched > 0) _instance.Logger.LogDebug("AutoModSync hooked " + patched + " ZNet.SetServerHost overload(s) for restart reconnect capture.");
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogDebug("AutoModSync could not hook ZNet.SetServerHost; fallback reconnect discovery will be used: " + ex.Message);
            }
        }

        // Intent: Harmony postfix that inspects SetServerHost arguments, records a normalized endpoint, and captures the selected online-backend enum when available.
        private static void CaptureServerHostPostfix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 1) return;

                string target = "";
                if (__args.Length >= 2)
                {
                    int port;
                    if (TryConvertPort(__args[1], out port))
                    {
                        target = BuildReconnectEndpoint(FormatServerAddress(__args[0]), port);
                    }
                }

                if (target.Length == 0)
                {
                    target = NormalizeReconnectTarget(FormatServerAddress(__args[0]));
                }

                if (target.Length > 0)
                {
                    _capturedServerTarget = target;
                    if (_instance != null) _instance.Logger.LogDebug("AutoModSync captured ZNet server target: " + target + ".");
                }

                if (__args.Length >= 3 && __args[2] != null)
                {
                    try
                    {
                        _capturedServerBackend = Convert.ToInt32(__args[2], CultureInfo.InvariantCulture);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Intent: Converts an unknown reflected port object to a bounded integer port without throwing into the game's connection path.
        private static bool TryConvertPort(object value, out int port)
        {
            port = 0;
            if (value == null) return false;
            try
            {
                port = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return port >= 1 && port <= 65535;
            }
            catch { return false; }
        }

        // Intent: Builds a canonical reconnect endpoint from host + port, adding IPv6 brackets when needed, then validates it through NormalizeReconnectTarget.
        private static string BuildReconnectEndpoint(string host, int port)
        {
            if (String.IsNullOrEmpty(host) || port < 1 || port > 65535) return "";
            host = host.Trim().Trim('"');
            if (host.Length == 0) return "";

            string candidate;
            if (host.StartsWith("[", StringComparison.Ordinal) && host.EndsWith("]", StringComparison.Ordinal))
            {
                candidate = host + ":" + port.ToString(CultureInfo.InvariantCulture);
            }
            else if (host.IndexOf(':') >= 0)
            {
                candidate = "[" + host.Trim('[', ']') + "]:" + port.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                candidate = host + ":" + port.ToString(CultureInfo.InvariantCulture);
            }
            return NormalizeReconnectTarget(candidate);
        }

        // Intent: Reads Valheim's currently selected online backend through reflection, supporting either field or property layouts across game versions.
        private static int GetCurrentOnlineBackend()
        {
            try
            {
                FieldInfo field = AccessTools.Field(typeof(ZNet), "m_onlineBackend");
                if (field != null)
                {
                    object owner = field.IsStatic ? null : (object)ZNet.instance;
                    object value = owner == null && !field.IsStatic ? null : field.GetValue(owner);
                    if (value != null) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
            }
            catch { }

            try
            {
                PropertyInfo property = AccessTools.Property(typeof(ZNet), "OnlineBackend");
                if (property != null)
                {
                    MethodInfo getter = property.GetGetMethod(true);
                    object owner = getter != null && getter.IsStatic ? null : (object)ZNet.instance;
                    object value = getter == null || (owner == null && !getter.IsStatic) ? null : property.GetValue(owner, null);
                    if (value != null) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
            }
            catch { }

            return -1;
        }

        // Intent: Finds the best restart reconnect endpoint from captured join data, ZNet state, or finally the connected socket.
        // Preference: direct host:port values are used; Steam IDs/lobby identifiers are deliberately rejected because they are not valid dedicated endpoints for this reconnect path.
        private static string GetReconnectTarget(ZRpc rpc)
        {
            string target = NormalizeReconnectTarget(_capturedServerTarget);
            if (target.Length > 0) return target;

            // Valheim keeps the original join target in ZNet. Prefer that over the
            // connected Steam socket identity because the one-shot reconnect needs a direct host:port.
            try
            {
                MethodInfo getServerString = AccessTools.Method(typeof(ZNet), "GetServerString", Type.EmptyTypes);
                if (getServerString != null)
                {
                    object value = getServerString.Invoke(null, null);
                    target = NormalizeReconnectTarget(value == null ? "" : value.ToString());
                    if (target.Length > 0) return target;
                }
            }
            catch { }

            try
            {
                FieldInfo hostField = AccessTools.Field(typeof(ZNet), "m_serverHost");
                FieldInfo portField = AccessTools.Field(typeof(ZNet), "m_serverHostPort");
                if (hostField != null && portField != null)
                {
                    object hostOwner = hostField.IsStatic ? null : (object)ZNet.instance;
                    object portOwner = portField.IsStatic ? null : (object)ZNet.instance;
                    object hostValue = hostOwner == null && !hostField.IsStatic ? null : hostField.GetValue(hostOwner);
                    object portValue = portOwner == null && !portField.IsStatic ? null : portField.GetValue(portOwner);
                    int port;
                    if (TryConvertPort(portValue, out port))
                    {
                        target = BuildReconnectEndpoint(FormatServerAddress(hostValue), port);
                        if (target.Length > 0) return target;
                    }
                }
            }
            catch { }

            try
            {
                FieldInfo serverIpField = AccessTools.Field(typeof(ZNet), "m_serverIPAddr");
                if (serverIpField != null)
                {
                    target = NormalizeReconnectTarget(FormatServerAddress(serverIpField.GetValue(null)));
                    if (target.Length > 0) return target;
                }
            }
            catch { }

            // Fallbacks for Valheim versions/backends where the static target is unavailable.
            try
            {
                object socket = rpc == null ? null : rpc.GetSocket();
                if (socket != null)
                {
                    MethodInfo endPointMethod = socket.GetType().GetMethod("GetEndPointString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (endPointMethod != null)
                    {
                        object endpoint = endPointMethod.Invoke(socket, null);
                        target = NormalizeReconnectTarget(endpoint == null ? "" : endpoint.ToString());
                        if (target.Length > 0) return target;
                    }

                    MethodInfo hostMethod = socket.GetType().GetMethod("GetHostName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (hostMethod != null)
                    {
                        object host = hostMethod.Invoke(socket, null);
                        target = NormalizeReconnectTarget(host == null ? "" : host.ToString());
                        if (target.Length > 0) return target;
                    }
                }
            }
            catch { }

            return "";
        }

        // Intent: Converts reflected Valheim address objects into text, including address types that expose a ToString(ref string, bool) formatter.
        private static string FormatServerAddress(object value)
        {
            if (value == null) return "";
            string direct = value as string;
            if (direct != null) return direct;

            try
            {
                Type type = value.GetType();
                Type[] signature = new Type[] { typeof(string).MakeByRefType(), typeof(bool) };
                MethodInfo formatter = type.GetMethod("ToString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, signature, null);
                if (formatter != null)
                {
                    object[] args = new object[] { "", true };
                    formatter.Invoke(value, args);
                    string formatted = args[0] as string;
                    if (!String.IsNullOrEmpty(formatted)) return formatted;
                }
            }
            catch { }

            try { return value.ToString() ?? ""; }
            catch { return ""; }
        }

        // Intent: Canonicalizes candidate reconnect strings and rejects whitespace, Steam/lobby IDs, invalid ports, wildcard addresses, and non-host:port forms.
        // It also understands scheme prefixes, combined steamId/ip forms, and bracketed IPv6.
        private static string NormalizeReconnectTarget(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            value = value.Trim().Trim('"');
            if (value.Length == 0 || value.IndexOf(' ') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) return "";

            int scheme = value.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0 && scheme + 3 < value.Length) value = value.Substring(scheme + 3);

            // Older/current ZNet.GetServerString implementations may return steamId/ip:port.
            int slash = value.LastIndexOf('/');
            if (slash >= 0)
            {
                string right = slash + 1 < value.Length ? value.Substring(slash + 1) : "";
                string parsedRight = NormalizeReconnectTarget(right);
                if (parsedRight.Length > 0) return parsedRight;
                string left = slash > 0 ? value.Substring(0, slash) : "";
                string parsedLeft = NormalizeReconnectTarget(left);
                if (parsedLeft.Length > 0) return parsedLeft;
                return "";
            }

            if (value.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Steamworks.", StringComparison.OrdinalIgnoreCase)) return "";
            bool digitsOnly = true;
            int d;
            for (d = 0; d < value.Length; d++)
            {
                if (value[d] < '0' || value[d] > '9') { digitsOnly = false; break; }
            }
            if (digitsOnly) return ""; // Steam ID / lobby ID, not a direct host:port endpoint.

            string host = "";
            string portText = "";
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int close = value.LastIndexOf(']');
                if (close <= 1 || close + 2 >= value.Length || value[close + 1] != ':') return "";
                host = value.Substring(0, close + 1);
                portText = value.Substring(close + 2);
            }
            else
            {
                int colon = value.LastIndexOf(':');
                if (colon <= 0 || colon + 1 >= value.Length) return "";
                host = value.Substring(0, colon);
                portText = value.Substring(colon + 1);
            }

            int port;
            if (!Int32.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535) return "";
            if (host.Length == 0 || String.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) || String.Equals(host, "::", StringComparison.OrdinalIgnoreCase) || String.Equals(host, "[::]", StringComparison.OrdinalIgnoreCase)) return "";
            return host + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        // Intent: Resolves the persistent AutoModSync state directory for both standalone and package-managed layouts by walking from the loaded plugin toward BepInEx/plugins.
        private static string GetAutoModSyncRoot()
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(typeof(ClientPlugin).Assembly.Location);
                if (!String.IsNullOrEmpty(assemblyDir))
                {
                    DirectoryInfo current = new DirectoryInfo(Path.GetFullPath(assemblyDir));
                    while (current != null)
                    {
                        if (String.Equals(current.Name, "plugins", StringComparison.OrdinalIgnoreCase) && current.Parent != null)
                            return Path.Combine(current.Parent.FullName, "AutoModSync");
                        current = current.Parent;
                    }
                }
            }
            catch { }

            return Path.Combine(Paths.BepInExRootPath, "AutoModSync");
        }

        // Intent: Resolves a synchronized plugin relative path beneath BepInEx/plugins and verifies full-path containment before returning it.
        private static string SafePluginPath(string relative)
        {
            string rel = NormalizeRelative(relative);
            if (rel.Length == 0) throw new InvalidDataException("Unsafe plugin path.");
            string root = Path.GetFullPath(Paths.PluginPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(Paths.PluginPath, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Plugin path escaped BepInEx\\plugins.");
            return full;
        }


        // Intent: General containment helper for staging/state roots; rejects any normalized relative path whose full path escapes the supplied root.
        private static string SafeUnder(string rootPath, string relative)
        {
            string rel = NormalizeRelative(relative);
            if (rel.Length == 0) throw new InvalidDataException("Unsafe AutoModSync path.");
            string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(rootPath, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("AutoModSync path escaped its staging root.");
            return full;
        }

        // Intent: Normalizes synchronized relative paths and rejects parent traversal, drive/URI separators, tabs, and newline characters.
        private static string NormalizeRelative(string value)
        {
            if (value == null) return "";
            value = value.Replace('\\', '/').TrimStart('/');
            if (value.Length == 0 || value == ".." || value.IndexOf("../", StringComparison.Ordinal) >= 0 || value.IndexOf(':') >= 0 || value.IndexOf('\t') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) return "";
            return value;
        }

        // Intent: Computes a file's SHA-256 while allowing other readers, used for local manifest comparison and post-extraction verification.
        private static string Sha256File(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (SHA256 sha = SHA256.Create()) return ToHex(sha.ComputeHash(fs));
        }

        // Intent: Maps a manifest file-kind code to its permitted destination root.
        // Current protocol intentionally supports only plugin files ('P'); unknown kinds are rejected.
        private static string SafeTargetPath(char kind, string relative)
        {
            if (kind == 'P') return SafePluginPath(relative);
            throw new InvalidDataException("Unsupported AutoModSync target kind.");
        }

        // Intent: Verifies the server's RSA/SHA-256 manifest signature using only the public key delivered in the manifest header.
        // Trust of that key is handled separately by fingerprint pinning.
        private static bool VerifyManifestSignature(string publicXml, string signatureBase64, byte[] data)
        {
            try
            {
                byte[] signature = Convert.FromBase64String(signatureBase64);
                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicXml);
                    return rsa.VerifyData(data, CryptoConfig.MapNameToOID("SHA256"), signature);
                }
            }
            catch { return false; }
        }

        // Intent: Derives the stable SHA-256 fingerprint shown/pinned for a server's public signing key.
        private static string Fingerprint(string publicXml)
        {
            using (SHA256 sha = SHA256.Create()) return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(publicXml ?? "")));
        }

        // Intent: Implements first-contact server trust.
        // Workflow: accepts an already-pinned fingerprint silently; otherwise shows a Windows confirmation dialog and persists the exact accepted fingerprint for future connections.
        private static bool EnsureServerTrusted(string fingerprint)
        {
            if (String.IsNullOrEmpty(fingerprint) || fingerprint.Length != 64) return false;
            string root = GetAutoModSyncRoot();
            string trusted = Path.Combine(root, "trusted-servers.txt");
            try
            {
                if (File.Exists(trusted))
                {
                    string[] lines = File.ReadAllLines(trusted);
                    int i;
                    for (i = 0; i < lines.Length; i++)
                        if (String.Equals(lines[i].Trim(), fingerprint, StringComparison.OrdinalIgnoreCase)) return true;
                }

                string pretty = fingerprint.Substring(0, 8) + "-" + fingerprint.Substring(8, 8) + "-" + fingerprint.Substring(16, 8) + "-" + fingerprint.Substring(24, 8) + "\r\n" +
                                fingerprint.Substring(32, 8) + "-" + fingerprint.Substring(40, 8) + "-" + fingerprint.Substring(48, 8) + "-" + fingerprint.Substring(56, 8);
                string message = "This Valheim server wants AutoModSync permission to install or update executable mod files on this PC.\r\n\r\n" +
                                 "Server fingerprint:\r\n" + pretty + "\r\n\r\n" +
                                 "Choose Yes only if you intended to join this server. You will only be asked again if its server identity changes.";
                int answer = MessageBox(IntPtr.Zero, message, "Valheim AutoModSync - Trust Server", 0x00000004u | 0x00000030u | 0x00000100u);
                if (answer != 6) return false;
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                File.AppendAllText(trusted, fingerprint.ToLowerInvariant() + Environment.NewLine, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Could not save AutoModSync server trust: " + ex.Message);
                return false;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        // Intent: Native Windows MessageBox import used only for the explicit first-contact executable-code trust prompt.
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);


        // Intent: Converts hash/fingerprint bytes into deterministic lowercase hexadecimal.
        private static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            int i;
            for (i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // Intent: Compares equal-length strings without early exit so hash comparisons do not reveal the first differing character through timing.
        private static bool ConstantEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            int i;
            for (i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        [DllImport("kernel32.dll")]
        // Intent: Native Windows API import used to find the BepInEx console window when present.
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        // Intent: Native Windows API import used to hide an already-open BepInEx console window.
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // Intent: Hides the current BepInEx console and updates BepInEx.cfg so future launches keep the console disabled.
        // This affects presentation only; AutoModSync logging continues through BepInEx log files.
        private static void HideBepInExConsoleAndDisableFutureConsole()
        {
            try
            {
                IntPtr hwnd = GetConsoleWindow();
                if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 0);
            }
            catch { }

            try
            {
                DisableBepInExConsoleInConfig();
            }
            catch { }
        }


        // Intent: Edits only the [Logging.Console] Enabled setting in BepInEx.cfg, preserving the rest of the file and writing through a temporary replacement file.
        private static void DisableBepInExConsoleInConfig()
        {
            string configPath = Path.Combine(Paths.BepInExRootPath, "config", "BepInEx.cfg");
            if (!File.Exists(configPath)) return;

            string[] lines = File.ReadAllLines(configPath);
            List<string> output = new List<string>(lines.Length + 3);
            bool inConsoleSection = false;
            bool foundConsoleSection = false;
            bool foundEnabled = false;
            bool changed = false;
            int i;

            for (i = 0; i < lines.Length; i++)
            {
                string line = lines[i] ?? "";
                string trimmed = line.Trim();

                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    if (inConsoleSection && !foundEnabled)
                    {
                        output.Add("Enabled = false");
                        foundEnabled = true;
                        changed = true;
                    }

                    inConsoleSection = String.Equals(trimmed, "[Logging.Console]", StringComparison.OrdinalIgnoreCase);
                    if (inConsoleSection)
                    {
                        foundConsoleSection = true;
                        foundEnabled = false;
                    }
                    output.Add(line);
                    continue;
                }

                if (inConsoleSection && trimmed.Length > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal) && !trimmed.StartsWith(";", StringComparison.Ordinal))
                {
                    int equals = trimmed.IndexOf('=');
                    if (equals > 0)
                    {
                        string key = trimmed.Substring(0, equals).Trim();
                        if (String.Equals(key, "Enabled", StringComparison.OrdinalIgnoreCase))
                        {
                            foundEnabled = true;
                            if (!String.Equals(trimmed.Substring(equals + 1).Trim(), "false", StringComparison.OrdinalIgnoreCase))
                            {
                                output.Add("Enabled = false");
                                changed = true;
                            }
                            else
                            {
                                output.Add(line);
                            }
                            continue;
                        }
                    }
                }

                output.Add(line);
            }

            if (inConsoleSection && !foundEnabled)
            {
                output.Add("Enabled = false");
                changed = true;
            }

            if (!foundConsoleSection)
            {
                if (output.Count > 0 && output[output.Count - 1].Length != 0) output.Add("");
                output.Add("[Logging.Console]");
                output.Add("Enabled = false");
                changed = true;
            }

            if (!changed) return;

            string tempPath = configPath + ".amsnew";
            File.WriteAllLines(tempPath, output.ToArray(), new UTF8Encoding(false));
            File.Copy(tempPath, configPath, true);
            File.Delete(tempPath);
        }

        // Intent: Startup fallback that finishes already-verified staged plugin replacements if the external apply helper was interrupted on the prior restart.
        // Scope: it reads only AutoModSync pending entries and writes only contained plugin paths.
        private static void ApplyPreviouslyStagedFilesIfPossible()
        {
            // The external helper normally handles this after the previous process exits.
            // This fallback can safely finish plugin-file updates if the helper was interrupted.
            string amsRoot = GetAutoModSyncRoot();
            string pending = Path.Combine(amsRoot, "pending.txt");
            if (!File.Exists(pending)) return;
            try
            {
                string[] paths = File.ReadAllLines(pending);
                List<string> remaining = new List<string>();
                int i;
                for (i = 0; i < paths.Length; i++)
                {
                    string item = paths[i] ?? "";
                    if (item.Length < 3 || item[1] != ':') continue;
                    char kind = item[0];
                    string rel = NormalizeRelative(item.Substring(2));
                    if (rel.Length == 0) continue;
                    if (kind != 'P') continue;
                    string src = Path.Combine(amsRoot, "staging", "plugins", rel.Replace('/', Path.DirectorySeparatorChar)) + ".amsnew";
                    string dst = SafeTargetPath(kind, rel);
                    if (!File.Exists(src)) continue;
                    string parent = Path.GetDirectoryName(dst);
                    if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                    File.Copy(src, dst, true);
                    File.Delete(src);
                }
                if (remaining.Count == 0) File.Delete(pending);
                else File.WriteAllLines(pending, remaining.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

    }
}
