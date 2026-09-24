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
using System.Threading;
using UnityEngine;

[assembly: AssemblyTitle("Valheim AutoModSync Client")]
[assembly: AssemblyDescription("Client-side Valheim plugin synchronization, trust, verification, restart, and reconnect component.")]
[assembly: AssemblyCompany("GordonFreesay")]
[assembly: AssemblyProduct("Valheim AutoModSync")]
[assembly: AssemblyVersion("2.6.0.0")]
[assembly: AssemblyFileVersion("2.6.0.0")]

namespace ValheimAutoModSync
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ClientPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.gordonfreesay.valheimautomodsync.client";
        public const string PluginName = "Valheim AutoModSync Client";
        public const string PluginVersion = "2.6.0";
        public const int ProtocolVersion = 4;

        private const string RpcHello = "AMS4_Hello";
        private const string RpcAck = "AMS4_Ack";
        private const string RpcManifestBegin = "AMS4_ManifestBegin";
        private const string RpcManifestChunk = "AMS4_ManifestChunk";
        private const string RpcManifestEnd = "AMS4_ManifestEnd";
        private const string RpcGetBundle = "AMS4_GetBundle";
        private const string RpcGetBundleChunk = "AMS4_GetBundleChunk";
        private const string RpcGetBundleBatch = "AMS4_GetBundleBatch";
        private const string RpcBundleBegin = "AMS4_BundleBegin";
        private const string RpcBundleChunk = "AMS4_BundleChunk";
        private const string RpcBundleBatch = "AMS4_BundleBatch";
        private const string RpcBundleEnd = "AMS4_BundleEnd";
        private const string RpcQueueStatus = "AMS4_QueueStatus";
        private const string RpcError = "AMS4_Error";
        private const int BundleBatchChunks = 16;
        private const int BundlePipelineChunks = 128;
        private const string ClientCapabilities = "roots1;bundle-resume1;bundle-scheduler1";

        // 2.6 client-side hard ceilings are deliberately independent of server configuration.
        // A trusted server may choose smaller limits, but it cannot make this client allocate/write unbounded payloads.
        private const int MaxBundleFiles = AutoModSyncClientResourceSafety.MaxBundleFiles;
        private const int MaxBundleChunks = AutoModSyncClientResourceSafety.MaxBundleChunks;
        private const long MaxIncomingBundleBytes = AutoModSyncClientResourceSafety.MaxIncomingBundleBytes;
        private const long MaxExpandedSyncBytes = AutoModSyncClientResourceSafety.MaxExpandedSyncBytes;
        private const long MaxIndividualSyncFileBytes = AutoModSyncClientResourceSafety.MaxIndividualSyncFileBytes;

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
        private static bool _serverSupportsBundleWindow;
        private static bool _serverSupportsBundleBatch;
        private static bool _serverSupportsBundlePipeline;
        private static bool _serverSupportsBundleResume;
        private static bool _serverSupportsBundleScheduler;
        private static bool _allowPeerInfo;
        private static bool _preflightGateActive;
        private static bool _allowServerHandshake;
        private static bool _serverHandshakeHeld;
        private static object[] _heldServerHandshakeParameters = new object[0];
        private static DateTime _helloSentUtc;
        private static DateTime _lastHelloAttemptUtc;
        private static int _helloAttemptCount;
        private static readonly HashSet<ZRpc> Registered = new HashSet<ZRpc>();
        private static readonly HashSet<ZRpc> PreflightComplete = new HashSet<ZRpc>();

        private static int _manifestPartCount;
        private static string _manifestSignature = "";
        private static string _serverPublicKeyXml = "";
        private static string _serverFingerprint = "";
        private const string TrustPromptCaption = "Valheim AutoModSync - Trust Server";
        private static bool _trustPromptPending;
        private static string _trustPromptFingerprint = "";
        private static string _trustPromptManifest = "";
        private static ZRpc _trustPromptRpc;
        private static volatile int _trustPromptDecision;
        private static volatile int _trustPromptGeneration;
        private static volatile int _trustPromptNativeThreadId;
        private static int _staleTrustPromptThreadId;
        private static DateTime _staleTrustPromptDismissUntilUtc = DateTime.MinValue;
        private static DateTime _staleTrustPromptNextDismissUtc = DateTime.MinValue;
        private static readonly Dictionary<int, string> ManifestParts = new Dictionary<int, string>();
        private static readonly List<ManifestEntry> NeededFiles = new List<ManifestEntry>();
        private static FileStream _bundleStream;
        private static string _bundlePath = "";
        private static string _bundleSha256 = "";
        private static long _bundleSize;
        private static long _bundleBytesReceived;
        private static long _bundleSessionStartBytes;
        private static DateTime _bundleStartedUtc = DateTime.MinValue;
        private static int _bundleNextChunk;
        private static int _bundleTotalChunks;
        private static int _bundleWindowEndExclusive;
        private static int _bundleChunkBytes;
        private static string _bundleRequestKey = "";
        private static AutoModSyncResumeCandidate _resumeOfferedCandidate;
        private static bool _bundleResumeSlotActive;
#if AMS_DEV_TESTS
        private static int _devDisconnectAfterChunk;
        private static bool _devEmulateLegacyClient;
        private static int _devTrustDisconnectGeneration;
        private static DateTime _devTrustDisconnectUtc = DateTime.MinValue;
        private static bool _devUiPreviewActive;
        private static int _devUiPreviewIndex;
        private static DateTime _devUiPreviewNextUtc = DateTime.MinValue;
#endif
        private static bool _restartRequested;
        private static DateTime _quitAfterUtc = DateTime.MinValue;
        private static bool _quitIssued;
        private static readonly AutoModSyncUiState _uiState = new AutoModSyncUiState();
        private static bool _overlayVisible;
        private static DateTime _overlayHideUtc = DateTime.MinValue;
        private static Texture2D _uiLogoTexture;
        private static bool _uiLogoLoadAttempted;
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
        private static readonly List<AutoModSyncOwnershipEntry> DesiredOwnershipEntries = new List<AutoModSyncOwnershipEntry>();
        private static bool _ownershipLedgerChanged;

        private sealed class ManifestEntry
        {
            public char Kind;
            public string Sha256;
            public long Size;
            public string RelativePath;
        }

        // Intent: BepInEx client entry point; initializes only on the playable Valheim process, hands any interrupted apply back to the out-of-process transaction helper, restores reconnect state, and installs synchronization hooks.
        // Recovery safety: the running game never mutates live synchronized DLL/config destinations itself. A pending/journaled transaction causes an immediate helper-owned recovery restart before AMS can join a server.
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
                if (HasPendingApplyRecovery())
                {
                    ScheduleRecoveredStagingRestart();
                    return;
                }
                LoadStartupReconnectRequest();
#if AMS_DEV_TESTS
                TryStartDevelopmentUiPreview();
#endif
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
#if AMS_DEV_TESTS
            if (_devUiPreviewActive) UpdateDevelopmentUiPreview();
#endif
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

            if (_overlayVisible && _overlayHideUtc != DateTime.MinValue && DateTime.UtcNow >= _overlayHideUtc)
            {
                HideSyncOverlay();
            }

            if (_staleTrustPromptThreadId != 0 && _staleTrustPromptDismissUntilUtc != DateTime.MinValue)
            {
                DateTime dismissNow = DateTime.UtcNow;
                if (dismissNow > _staleTrustPromptDismissUntilUtc)
                {
                    _staleTrustPromptThreadId = 0;
                    _staleTrustPromptDismissUntilUtc = DateTime.MinValue;
                    _staleTrustPromptNextDismissUtc = DateTime.MinValue;
                }
                else if (_staleTrustPromptNextDismissUtc == DateTime.MinValue || dismissNow >= _staleTrustPromptNextDismissUtc)
                {
                    CloseNativeTrustPromptWindow(_staleTrustPromptThreadId, false);
                    _staleTrustPromptNextDismissUtc = dismissNow.AddMilliseconds(100.0);
                }
            }

#if AMS_DEV_TESTS
            if (_trustPromptPending && _devTrustDisconnectGeneration == _trustPromptGeneration &&
                _devTrustDisconnectUtc != DateTime.MinValue && DateTime.UtcNow >= _devTrustDisconnectUtc)
            {
                _devTrustDisconnectGeneration = 0;
                _devTrustDisconnectUtc = DateTime.MinValue;
                if (_instance != null)
                    _instance.Logger.LogWarning("AutoModSync DEV TEST closing the protected socket while the native trust dialog is still open.");
                try
                {
                    if (_pendingRpc != null && _pendingRpc.GetSocket() != null) _pendingRpc.GetSocket().Close();
                }
                catch (Exception ex)
                {
                    if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV TEST could not close the trust-dialog socket: " + ex.Message);
                }
            }
#endif

            if (_trustPromptPending && _trustPromptDecision != 0)
            {
                int decision = _trustPromptDecision;
                _trustPromptDecision = 0;

                if (decision == 1)
                    AcceptPendingServerTrust();
                else if (decision == 2)
                    RejectPendingServerTrust();
                else
                {
                    ClearPendingTrustPrompt();
                    AbortAutoModSyncJoin("AutoModSync could not open the server trust confirmation.");
                }
                return;
            }

            // Once a server has positively answered AMS, a dead socket is a failed protected session, not a non-AMS fail-open case.
            // This also guarantees that trust/download UI cannot remain stranded on screen after Valheim has already lost the connection.
            if (_waitingForServer && _pendingRpc != null && (_serverAcknowledged || _serverRecognized) && !IsRpcConnected(_pendingRpc))
            {
                HandleRecognizedConnectionLoss("AutoModSync connection ended before synchronization completed.");
                return;
            }

            if (_waitingForServer && _pendingRpc != null && _helloSentUtc != DateTime.MinValue)
            {
                DateTime now = DateTime.UtcNow;
                double elapsed = (now - _helloSentUtc).TotalSeconds;
                if (!_serverAcknowledged && !_serverRecognized && _preflightGateActive && _helloAttemptCount < 5 &&
                    (_lastHelloAttemptUtc == DateTime.MinValue || (now - _lastHelloAttemptUtc).TotalMilliseconds >= 600.0))
                {
                    SendPreflightHello(_pendingRpc, true);
                }
                if (!_serverAcknowledged && !_serverRecognized && elapsed > 3.25)
                {
                    Logger.LogDebug("No AutoModSync server response after " + _helloAttemptCount.ToString(CultureInfo.InvariantCulture) + " probe attempt(s); releasing the normal Valheim handshake.");
                    FailOpen("No AutoModSync preflight response was received.");
                }
                else if (_serverAcknowledged && !_serverRecognized && elapsed > 15.0)
                {
                    AbortAutoModSyncJoin("AutoModSync server acknowledged preflight but did not begin a manifest within 15 seconds.");
                }
            }
        }

        // Intent: Renders the Phase 7 state model as a branded gray/orange AutoModSync panel without owning synchronization policy.
        private void OnGUI()
        {
            if (!_overlayVisible || _uiState.Phase == AutoModSyncUiPhase.Hidden) return;

            EnsureUiLogoTexture();

            float width = Mathf.Min(720f, Mathf.Max(360f, Screen.width - 32f));
            float height = UiPanelHeight(_uiState.Phase);
            height = Mathf.Min(height, Mathf.Max(280f, Screen.height - 24f));
            float left = (Screen.width - width) * 0.5f;
            float top = (Screen.height - height) * 0.5f;
            Rect panel = new Rect(left, top, width, height);

            GUI.depth = -1000;
            Color previousColor = GUI.color;

            Color charcoal = new Color(0.075f, 0.082f, 0.094f, 0.985f);
            Color slate = new Color(0.115f, 0.125f, 0.145f, 0.98f);
            Color orange = new Color(1.00f, 0.36f, 0.055f, 1f);
            Color ember = new Color(1.00f, 0.56f, 0.12f, 1f);
            Color text = new Color(0.95f, 0.96f, 0.97f, 1f);
            Color muted = new Color(0.66f, 0.69f, 0.73f, 1f);
            Color danger = new Color(1.00f, 0.23f, 0.10f, 1f);
            Color phaseAccent = _uiState.Phase == AutoModSyncUiPhase.Failed ? danger : orange;

            float pulse = 0.32f + (0.18f * Mathf.PingPong(Time.realtimeSinceStartup * 0.8f, 1f));
            DrawSolidRect(new Rect(panel.x - 4f, panel.y - 4f, panel.width + 8f, panel.height + 8f),
                new Color(phaseAccent.r, phaseAccent.g, phaseAccent.b, pulse));
            DrawSolidRect(panel, charcoal);
            DrawOutlinedRect(panel, phaseAccent, 2f);
            DrawSolidRect(new Rect(panel.x + 2f, panel.y + 2f, panel.width - 4f, 4f), ember);

            GUIStyle brandStyle = new GUIStyle(GUI.skin.label);
            brandStyle.fontSize = 23;
            brandStyle.fontStyle = FontStyle.Bold;
            brandStyle.alignment = TextAnchor.MiddleLeft;
            brandStyle.normal.textColor = text;

            GUIStyle brandSubStyle = new GUIStyle(GUI.skin.label);
            brandSubStyle.fontSize = 11;
            brandSubStyle.fontStyle = FontStyle.Bold;
            brandSubStyle.alignment = TextAnchor.UpperLeft;
            brandSubStyle.normal.textColor = orange;

            GUIStyle statusStyle = new GUIStyle(GUI.skin.label);
            statusStyle.fontSize = 20;
            statusStyle.fontStyle = FontStyle.Bold;
            statusStyle.alignment = TextAnchor.MiddleCenter;
            statusStyle.wordWrap = true;
            statusStyle.normal.textColor = text;

            GUIStyle detailStyle = new GUIStyle(GUI.skin.label);
            detailStyle.fontSize = 13;
            detailStyle.alignment = TextAnchor.UpperCenter;
            detailStyle.wordWrap = true;
            detailStyle.normal.textColor = muted;

            GUIStyle smallStyle = new GUIStyle(GUI.skin.label);
            smallStyle.fontSize = 11;
            smallStyle.alignment = TextAnchor.MiddleCenter;
            smallStyle.wordWrap = true;
            smallStyle.normal.textColor = muted;

            GUIStyle valueStyle = new GUIStyle(GUI.skin.label);
            valueStyle.fontSize = 17;
            valueStyle.fontStyle = FontStyle.Bold;
            valueStyle.alignment = TextAnchor.MiddleCenter;
            valueStyle.normal.textColor = text;

            float headerY = top + 18f;
            if (_uiLogoTexture != null)
                GUI.DrawTexture(new Rect(left + 24f, headerY, 66f, 66f), _uiLogoTexture, ScaleMode.ScaleToFit, true);

            float titleLeft = left + (_uiLogoTexture != null ? 104f : 28f);
            GUI.Label(new Rect(titleLeft, headerY + 4f, width - (titleLeft - left) - 24f, 32f), "AUTOMODSYNC", brandStyle);
            GUI.Label(new Rect(titleLeft + 1f, headerY + 38f, width - (titleLeft - left) - 24f, 22f),
                "VALHEIM  •  VERIFIED MOD SYNCHRONIZATION", brandSubStyle);
            DrawSolidRect(new Rect(left + 24f, top + 96f, width - 48f, 1f), new Color(1f, 0.36f, 0.055f, 0.38f));

            GUI.Label(new Rect(left + 28f, top + 107f, width - 56f, 36f), _uiState.Status ?? "", statusStyle);
            GUI.Label(new Rect(left + 42f, top + 145f, width - 84f, 43f), _uiState.Detail ?? "", detailStyle);

            float y = top + 195f;

            if (_uiState.Phase == AutoModSyncUiPhase.Trust)
            {
                Rect trustBox = new Rect(left + 40f, y, width - 80f, 90f);
                DrawSolidRect(trustBox, slate);
                DrawOutlinedRect(trustBox, new Color(1f, 0.36f, 0.055f, 0.55f), 1f);
                GUI.Label(new Rect(trustBox.x + 10f, trustBox.y + 8f, trustBox.width - 20f, 18f), "SERVER FINGERPRINT", smallStyle);
                GUIStyle fingerprintStyle = new GUIStyle(detailStyle);
                fingerprintStyle.fontSize = 12;
                fingerprintStyle.fontStyle = FontStyle.Bold;
                fingerprintStyle.normal.textColor = text;
                GUI.Label(new Rect(trustBox.x + 12f, trustBox.y + 30f, trustBox.width - 24f, 52f),
                    FormatFingerprint(_uiState.ServerFingerprint), fingerprintStyle);
                y += 104f;
            }
            else if (_uiState.ManifestFiles > 0)
            {
                float gap = 8f;
                float innerWidth = width - 64f;
                float tileWidth = (innerWidth - (gap * 3f)) / 4f;
                DrawStatTile(new Rect(left + 32f, y, tileWidth, 64f), "MATCHED",
                    _uiState.MatchedFiles.ToString(CultureInfo.InvariantCulture), slate, muted, text);
                DrawStatTile(new Rect(left + 32f + tileWidth + gap, y, tileWidth, 64f), "CHANGED",
                    _uiState.ChangedFiles.ToString(CultureInfo.InvariantCulture), slate, muted, orange);
                DrawStatTile(new Rect(left + 32f + ((tileWidth + gap) * 2f), y, tileWidth, 64f), "REMOVED",
                    _uiState.RemovedFiles.ToString(CultureInfo.InvariantCulture), slate, muted, text);
                DrawStatTile(new Rect(left + 32f + ((tileWidth + gap) * 3f), y, tileWidth, 64f), "REQUIRED",
                    FormatBytes(_uiState.RequiredExpandedBytes), slate, muted, text);
                y += 78f;
            }

            if (_uiState.Phase == AutoModSyncUiPhase.Queued)
            {
                Rect queueBox = new Rect(left + 42f, y, width - 84f, 58f);
                DrawSolidRect(queueBox, slate);
                DrawOutlinedRect(queueBox, new Color(1f, 0.36f, 0.055f, 0.42f), 1f);
                GUI.Label(new Rect(queueBox.x + 10f, queueBox.y + 6f, queueBox.width * 0.5f - 10f, 22f), "QUEUE POSITION", smallStyle);
                GUI.Label(new Rect(queueBox.x + 10f, queueBox.y + 25f, queueBox.width * 0.5f - 10f, 27f),
                    _uiState.QueuePosition.ToString(CultureInfo.InvariantCulture), valueStyle);
                GUI.Label(new Rect(queueBox.x + queueBox.width * 0.5f, queueBox.y + 6f, queueBox.width * 0.5f - 10f, 22f), "ACTIVE TRANSFERS", smallStyle);
                GUI.Label(new Rect(queueBox.x + queueBox.width * 0.5f, queueBox.y + 25f, queueBox.width * 0.5f - 10f, 27f),
                    _uiState.QueueActive.ToString(CultureInfo.InvariantCulture) + " / " + _uiState.QueueMaxActive.ToString(CultureInfo.InvariantCulture), valueStyle);
                y += 70f;
            }
            else if (_uiState.Phase == AutoModSyncUiPhase.Downloading)
            {
                float progress = (float)_uiState.TransferProgress();
                GUI.Label(new Rect(left + 42f, y, width - 84f, 18f),
                    FormatBytes(_uiState.BytesReceived) + " / " + FormatBytes(_uiState.BundleBytes), smallStyle);
                y += 21f;
                DrawProgressBar(new Rect(left + 42f, y, width - 84f, 20f), progress, orange, ember);
                GUI.Label(new Rect(left + 42f, y, width - 84f, 20f),
                    Math.Round(progress * 100.0).ToString(CultureInfo.InvariantCulture) + "%", smallStyle);
                y += 31f;

                float metricWidth = (width - 100f) / 3f;
                DrawStatTile(new Rect(left + 42f, y, metricWidth, 54f), "CURRENT",
                    FormatRate(_uiState.CurrentBytesPerSecond), slate, muted, text);
                DrawStatTile(new Rect(left + 50f + metricWidth, y, metricWidth, 54f), "AVERAGE",
                    FormatRate(_uiState.AverageBytesPerSecond), slate, muted, text);
                DrawStatTile(new Rect(left + 58f + (metricWidth * 2f), y, metricWidth, 54f), "ETA",
                    FormatEta(_uiState.TransferEtaSeconds()), slate, muted, text);
                y += 63f;

                if (_uiState.SessionStartBytes > 0L)
                {
                    GUIStyle resumedStyle = new GUIStyle(smallStyle);
                    resumedStyle.fontStyle = FontStyle.Bold;
                    resumedStyle.normal.textColor = orange;
                    GUI.Label(new Rect(left + 42f, y, width - 84f, 20f),
                        "RESUMED  •  " + FormatBytes(_uiState.SessionStartBytes) + " retained from verified package data", resumedStyle);
                    y += 22f;
                }
            }
            else if (_uiState.Phase == AutoModSyncUiPhase.Verifying)
            {
                float verifyProgress = _uiState.VerificationTotal <= 0 ? 0f :
                    Mathf.Clamp01(_uiState.VerificationCompleted / (float)_uiState.VerificationTotal);
                GUI.Label(new Rect(left + 42f, y, width - 84f, 18f),
                    _uiState.VerificationCompleted.ToString(CultureInfo.InvariantCulture) + " / " +
                    _uiState.VerificationTotal.ToString(CultureInfo.InvariantCulture) + " files verified", smallStyle);
                y += 21f;
                DrawProgressBar(new Rect(left + 42f, y, width - 84f, 20f), verifyProgress, orange, ember);
                y += 31f;
            }
            else if (_uiState.Phase == AutoModSyncUiPhase.Applying ||
                     _uiState.Phase == AutoModSyncUiPhase.Restarting ||
                     _uiState.Phase == AutoModSyncUiPhase.Reconnecting ||
                     _uiState.Phase == AutoModSyncUiPhase.Checking ||
                     _uiState.Phase == AutoModSyncUiPhase.Comparing)
            {
                float activity = Mathf.PingPong(Time.realtimeSinceStartup * 0.55f, 1f);
                Rect track = new Rect(left + 50f, y + 8f, width - 100f, 5f);
                DrawSolidRect(track, new Color(0.22f, 0.23f, 0.25f, 1f));
                float segment = Mathf.Max(48f, track.width * 0.22f);
                float travel = Mathf.Max(0f, track.width - segment);
                DrawSolidRect(new Rect(track.x + (travel * activity), track.y, segment, track.height), orange);
                y += 28f;
            }

            string footer = "AMS " + PluginVersion + "  •  VALHEIM";
            if (!String.IsNullOrEmpty(_uiState.ServerFingerprint))
                footer += "  •  SERVER " + ShortFingerprint(_uiState.ServerFingerprint);
            GUI.Label(new Rect(left + 28f, top + height - 29f, width - 56f, 18f), footer, smallStyle);

            GUI.color = previousColor;
        }

        // Intent: Chooses a stable panel height for each presentation phase so telemetry remains readable without affecting synchronization behavior.
        private static float UiPanelHeight(AutoModSyncUiPhase phase)
        {
            if (phase == AutoModSyncUiPhase.Downloading) return 462f;
            if (phase == AutoModSyncUiPhase.Queued) return 392f;
            if (phase == AutoModSyncUiPhase.Trust) return 365f;
            if (phase == AutoModSyncUiPhase.Verifying) return 382f;
            if (phase == AutoModSyncUiPhase.Comparing || phase == AutoModSyncUiPhase.Checking) return 350f;
            if (phase == AutoModSyncUiPhase.Applying || phase == AutoModSyncUiPhase.Restarting) return 360f;
            if (phase == AutoModSyncUiPhase.Complete) return 342f;
            if (phase == AutoModSyncUiPhase.Failed) return 325f;
            if (phase == AutoModSyncUiPhase.Reconnecting) return 305f;
            return 300f;
        }

        // Intent: Draws a solid IMGUI rectangle using Unity's built-in white texture so no UI texture allocation is needed.
        private static void DrawSolidRect(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        // Intent: Draws a thin rectangular border for the branded synchronization panel and stat tiles.
        private static void DrawOutlinedRect(Rect rect, Color color, float thickness)
        {
            DrawSolidRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            DrawSolidRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            DrawSolidRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            DrawSolidRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        // Intent: Draws one compact comparison/telemetry tile with muted label text and a prominent value.
        private static void DrawStatTile(Rect rect, string label, string value, Color background, Color labelColor, Color valueColor)
        {
            DrawSolidRect(rect, background);
            GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 10;
            labelStyle.fontStyle = FontStyle.Bold;
            labelStyle.alignment = TextAnchor.MiddleCenter;
            labelStyle.normal.textColor = labelColor;
            GUIStyle valueStyle = new GUIStyle(GUI.skin.label);
            valueStyle.fontSize = 15;
            valueStyle.fontStyle = FontStyle.Bold;
            valueStyle.alignment = TextAnchor.MiddleCenter;
            valueStyle.normal.textColor = valueColor;
            GUI.Label(new Rect(rect.x + 4f, rect.y + 5f, rect.width - 8f, 18f), label, labelStyle);
            GUI.Label(new Rect(rect.x + 4f, rect.y + 24f, rect.width - 8f, rect.height - 27f), value, valueStyle);
        }

        // Intent: Draws a dark transfer/verification track with an orange-to-ember two-layer fill derived only from state-model progress.
        private static void DrawProgressBar(Rect rect, float progress, Color orange, Color ember)
        {
            progress = Mathf.Clamp01(progress);
            DrawSolidRect(rect, new Color(0.18f, 0.19f, 0.21f, 1f));
            DrawOutlinedRect(rect, new Color(1f, 1f, 1f, 0.08f), 1f);
            if (progress <= 0f) return;
            Rect fill = new Rect(rect.x + 2f, rect.y + 2f, (rect.width - 4f) * progress, rect.height - 4f);
            DrawSolidRect(fill, orange);
            if (fill.width > 8f)
                DrawSolidRect(new Rect(fill.x, fill.y, fill.width, Mathf.Max(2f, fill.height * 0.28f)), ember);
        }

        // Intent: Lazily loads the packaged AMS logo embedded in the client DLL; a missing/corrupt image degrades to text branding without breaking synchronization.
        private static void EnsureUiLogoTexture()
        {
            if (_uiLogoLoadAttempted) return;
            _uiLogoLoadAttempted = true;
            try
            {
                Assembly assembly = typeof(ClientPlugin).Assembly;
                using (Stream input = assembly.GetManifestResourceStream("ValheimAutoModSync.Branding.Logo.png"))
                {
                    if (input == null) throw new FileNotFoundException("Embedded AutoModSync logo resource was not found.");
                    byte[] bytes;
                    using (MemoryStream memory = new MemoryStream())
                    {
                        input.CopyTo(memory);
                        bytes = memory.ToArray();
                    }

                    Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                    if (!ImageConversion.LoadImage(texture, bytes))
                        throw new InvalidDataException("Embedded AutoModSync logo PNG could not be decoded.");
                    texture.wrapMode = TextureWrapMode.Clamp;
                    _uiLogoTexture = texture;
                }
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync UI logo unavailable; using text branding only: " + ex.Message);
            }
        }

        // Intent: Formats bytes-per-second telemetry as a compact player-facing transfer rate while preserving unknown rates as a dash.
        private static string FormatRate(double bytesPerSecond)
        {
            if (bytesPerSecond <= 1.0) return "—";
            return FormatBytes((long)Math.Round(bytesPerSecond)) + "/s";
        }

        // Intent: Formats ETA telemetry without inventing precision when the transfer model has not observed a usable rate.
        private static string FormatEta(double seconds)
        {
            if (seconds < 0.0 || Double.IsNaN(seconds) || Double.IsInfinity(seconds)) return "CALCULATING";
            if (seconds < 1.0) return "<1s";
            int whole = (int)Math.Ceiling(seconds);
            if (whole < 60) return whole.ToString(CultureInfo.InvariantCulture) + "s";
            int minutes = whole / 60;
            int remaining = whole % 60;
            if (minutes < 60) return minutes.ToString(CultureInfo.InvariantCulture) + "m " + remaining.ToString("00", CultureInfo.InvariantCulture) + "s";
            int hours = minutes / 60;
            return hours.ToString(CultureInfo.InvariantCulture) + "h " + (minutes % 60).ToString("00", CultureInfo.InvariantCulture) + "m";
        }

        // Intent: Produces a compact non-authoritative server identity label for the footer while trust decisions continue using the full fingerprint.
        private static string ShortFingerprint(string fingerprint)
        {
            if (String.IsNullOrEmpty(fingerprint)) return "UNKNOWN";
            if (fingerprint.Length <= 16) return fingerprint.ToUpperInvariant();
            return fingerprint.Substring(0, 8).ToUpperInvariant() + "…" + fingerprint.Substring(fingerprint.Length - 8).ToUpperInvariant();
        }

        // Intent: Publishes an already-decided lifecycle phase into the policy-free UI model and makes the overlay visible.
        private static void ShowSyncOverlay(AutoModSyncUiPhase phase, string status, string detail)
        {
            _uiState.SetPhase(phase, status, detail);
            _overlayHideUtc = DateTime.MinValue;
            _overlayVisible = phase != AutoModSyncUiPhase.Hidden;
        }

        // Intent: Shows a terminal or success state briefly, then relinquishes the menu/game UI automatically.
        private static void ShowTransientSyncOverlay(AutoModSyncUiPhase phase, string status, string detail, double seconds)
        {
            ShowSyncOverlay(phase, status, detail);
            _overlayHideUtc = DateTime.UtcNow.AddSeconds(Math.Max(0.5, seconds));
        }

        // Intent: Clears all presentation state when synchronization is finished, abandoned, or a normal non-AMS handshake resumes.
        private static void HideSyncOverlay()
        {
            _overlayVisible = false;
            _overlayHideUtc = DateTime.MinValue;
            _uiState.Reset();
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
                    // A created outgoing connection completes the one-shot restart reconnect; cancel any stale delayed character-start callback.
                    _startupReconnectCharacterStartPending = false;
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
                _heldServerHandshakeParameters = parameters == null ? new object[0] : (object[])parameters.Clone();
                if (_instance != null) _instance.Logger.LogDebug("AutoModSync held Valheim ServerHandshake with " + _heldServerHandshakeParameters.Length.ToString(CultureInfo.InvariantCulture) + " argument(s) until preflight completes.");
                return false;
            }
        }

        [HarmonyPatch(typeof(FejdStartup), "ShowCharacterSelection")]
        private static class ReconnectCharacterSelectionPatch
        {
            // Intent: During restart reconnect, notices the first character-selection screen and schedules the normal selected-character start action exactly once.
            // Safety: later ShowCharacterSelection callbacks can occur after the outgoing reconnect is already established; completed or already-pending reconnects must not start the character again.
            private static void Postfix()
            {
                if (!_startupReconnectDispatched || _startupReconnectFinished || _startupReconnectCharacterStartPending || _restartRequested) return;
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
                _serverSupportsBundleWindow = false;
                _serverSupportsBundleBatch = false;
                _serverSupportsBundlePipeline = false;
                _serverSupportsBundleResume = false;
                _serverSupportsBundleScheduler = false;
                _preflightGateActive = false;
#if AMS_DEV_TESTS
                _devEmulateLegacyClient = ConsumeDevelopmentLegacyClientMarker();
#endif
                _helloSentUtc = DateTime.UtcNow;
                ResetManifestState();

                try
                {
                    ZPackage hello = new ZPackage();
                    hello.Write(ProtocolVersion);
                    hello.Write(PluginVersion);
                    hello.Write(GetCurrentClientCapabilities());
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
                rpc.Register<ZPackage>(RpcGetBundleBatch, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcBundleBegin, new Action<ZRpc, ZPackage>(RPC_BundleBegin));
                rpc.Register<ZPackage>(RpcBundleChunk, new Action<ZRpc, ZPackage>(RPC_BundleChunk));
                rpc.Register<ZPackage>(RpcBundleBatch, new Action<ZRpc, ZPackage>(RPC_BundleBatch));
                rpc.Register<ZPackage>(RpcBundleEnd, new Action<ZRpc, ZPackage>(RPC_BundleEnd));
                rpc.Register<ZPackage>(RpcQueueStatus, new Action<ZRpc, ZPackage>(RPC_QueueStatus));
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

        // Intent: Handles the optional AMS4 preflight acknowledgement sent before server manifest hashing.
        // Workflow: validates protocol version, records that an AutoModSync server responded, and extends the timeout while manifest generation proceeds.
        private static void RPC_Ack(ZRpc rpc, ZPackage pkg)
        {
            if (!_waitingForServer || rpc != _pendingRpc) return;
            try
            {
                int protocol = pkg.ReadInt();
                string serverVersion = "";
                string capabilities = "";
                try { serverVersion = pkg.ReadString(); } catch { serverVersion = ""; }
                try { capabilities = pkg.ReadString(); } catch { capabilities = ""; }
                if (protocol != ProtocolVersion) throw new InvalidDataException("AutoModSync protocol mismatch during preflight acknowledgement.");
                _serverAcknowledged = true;
                _serverSupportsBundleWindow = capabilities.IndexOf("bundle-window1", StringComparison.Ordinal) >= 0;
                _serverSupportsBundleBatch = capabilities.IndexOf("bundle-batch1", StringComparison.Ordinal) >= 0;
                _serverSupportsBundlePipeline = capabilities.IndexOf("bundle-pipeline1", StringComparison.Ordinal) >= 0;
                _serverSupportsBundleResume = capabilities.IndexOf("bundle-resume1", StringComparison.Ordinal) >= 0;
                _serverSupportsBundleScheduler = capabilities.IndexOf("bundle-scheduler1", StringComparison.Ordinal) >= 0;
                ShowSyncOverlay(AutoModSyncUiPhase.Checking, "AutoModSync server detected.",
                    "Waiting for the signed server manifest...");
#if AMS_DEV_TESTS
                if (_devEmulateLegacyClient)
                {
                    _serverSupportsBundleResume = false;
                    _serverSupportsBundleScheduler = false;
                    if (_instance != null) _instance.Logger.LogInfo("AutoModSync DEV TEST emulating a pre-resume AMS4 client; bundle-resume1 and bundle-scheduler1 are ignored for this connection.");
                }
                if (serverVersion.StartsWith("2.5.", StringComparison.Ordinal)
                    && !_serverSupportsBundleResume
                    && !_serverSupportsBundleScheduler
                    && _instance != null)
                {
                    _instance.Logger.LogInfo("AutoModSync DEV TEST legacy-server compatibility confirmed: 2.5 AMS4 capability set accepted; resume/scheduler extensions remain disabled.");
                    if (_serverSupportsBundlePipeline)
                        _instance.Logger.LogInfo("AutoModSync DEV TEST legacy-server compatibility confirmed: using the 2.5 pipelined binary bundle transfer fallback.");
                    else if (_serverSupportsBundleBatch)
                        _instance.Logger.LogInfo("AutoModSync DEV TEST legacy-server compatibility confirmed: using the 2.5 binary batch bundle transfer fallback.");
                    else if (_serverSupportsBundleWindow)
                        _instance.Logger.LogInfo("AutoModSync DEV TEST legacy-server compatibility confirmed: using the 2.5 windowed bundle transfer fallback.");
                    else
                        _instance.Logger.LogInfo("AutoModSync DEV TEST legacy-server compatibility confirmed: using the original single-chunk AMS4 transfer fallback.");
                }
#endif
                if (_instance != null)
                {
                    _instance.Logger.LogDebug("AutoModSync preflight acknowledged by server " + serverVersion + ".");
                    if (_serverSupportsBundleResume) _instance.Logger.LogDebug("AutoModSync server supports exact-artifact bundle resume.");
                    if (_serverSupportsBundlePipeline) _instance.Logger.LogDebug("AutoModSync server supports pipelined binary bundle transfer.");
                    else if (_serverSupportsBundleBatch) _instance.Logger.LogDebug("AutoModSync server supports binary batched bundle transfer.");
                    else if (_serverSupportsBundleWindow) _instance.Logger.LogDebug("AutoModSync server supports windowed bundle transfer.");
                }
            }
            catch (Exception ex)
            {
                AbortAutoModSyncJoin("Invalid AutoModSync preflight acknowledgement: " + ex.Message);
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
                _uiState.SetServerFingerprint(_serverFingerprint);
                ShowSyncOverlay(AutoModSyncUiPhase.Checking, "Checking server mods...",
                    "Validating the signed manifest for this AutoModSync server.");
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync server detected; checking required mods before joining.");
            }
            catch (Exception ex)
            {
                AbortAutoModSyncJoin("Bad AutoModSync manifest header: " + ex.Message);
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
                AbortAutoModSyncJoin("Manifest transfer failed: " + ex.Message);
            }
        }

        // Intent: Reassembles and cryptographically verifies the complete server manifest, establishes server trust, compares local files, then either resumes Valheim or requests the verified change bundle.
        // Trust: once AMS has positively responded, signature/trust/content failures abort this join instead of falling through to an unsynchronized vanilla handshake.
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

                // 2.6 establishes TOFU identity even when every required file already happens to match.
                // First-contact trust is asynchronous so the Valheim networking loop stays alive while the player verifies the fingerprint.
                if (!IsServerTrusted(_serverFingerprint))
                {
                    BeginServerTrustPrompt(rpc, _serverFingerprint, manifest);
                    return;
                }

                ContinueVerifiedManifest(manifest);
            }
            catch (Exception ex)
            {
                AbortAutoModSyncJoin("AutoModSync manifest verification failed: " + ex.Message);
            }
        }

        // Intent: Continues only after the signed manifest's server identity is already trusted, then computes deltas and either resumes Valheim or requests the exact bundle.
        // Security: this method is reachable from both an existing trust pin and the explicit in-game Trust action; neither path bypasses signature verification or TOFU identity checks.
        private static void ContinueVerifiedManifest(string manifest)
        {
            try
            {
                if (!_serverRecognized || _pendingRpc == null || !IsRpcConnected(_pendingRpc))
                {
                    HandleRecognizedConnectionLoss("AutoModSync connection ended before the trusted manifest could continue.");
                    return;
                }

                ShowSyncOverlay(AutoModSyncUiPhase.Comparing, "Comparing server mods...",
                    "Signed manifest verified. Comparing required files with this client.");
                BuildNeededList(manifest);
                if (NeededFiles.Count == 0)
                {
                    if (PendingRelativePaths.Count > 0)
                    {
                        ShowSyncOverlay(AutoModSyncUiPhase.Applying, "Preparing synchronized changes...",
                            "Removing " + PendingRelativePaths.Count.ToString(CultureInfo.InvariantCulture) + " stale server-managed file(s) transactionally.");
                        if (_instance != null)
                            _instance.Logger.LogInfo("AutoModSync: " + PendingRelativePaths.Count.ToString(CultureInfo.InvariantCulture) + " stale owned file(s) require transactional removal.");
                        BeginApplyAndRestart();
                        return;
                    }

                    if (_ownershipLedgerChanged)
                    {
                        AutoModSyncOwnershipState.WriteLedgerDurable(GetAutoModSyncRoot(), _serverFingerprint, DesiredOwnershipEntries);
                        if (_instance != null)
                            _instance.Logger.LogInfo("AutoModSync ownership metadata updated without live file changes.");
                    }

                    AutoModSyncOwnershipState.WriteLastSuccessfulServerDurable(GetAutoModSyncRoot(), _serverFingerprint);
                    ShowTransientSyncOverlay(AutoModSyncUiPhase.Complete, "Already synchronized.",
                        "Required mods match this trusted server. Joining normally...", 1.5);
                    if (_instance != null) _instance.Logger.LogInfo("AutoModSync: client mods already match the trusted server.");
                    ResumeNormalHandshake();
                }
                else
                {
                    ShowSyncOverlay(AutoModSyncUiPhase.Comparing, "Mod comparison complete.",
                        NeededFiles.Count.ToString(CultureInfo.InvariantCulture) + " changed file(s)" +
                        (PendingRelativePaths.Count > 0 ? " and " + PendingRelativePaths.Count.ToString(CultureInfo.InvariantCulture) + " stale removal(s)" : "") +
                        " require synchronization. Preparing the verified package...");
                    if (_instance != null)
                        _instance.Logger.LogInfo("AutoModSync: requesting one compressed package containing " + NeededFiles.Count +
                            " missing/changed file(s)" + (PendingRelativePaths.Count > 0 ? " plus " + PendingRelativePaths.Count + " stale owned removal(s)." : "."));
                    RequestBundle();
                }
            }
            catch (Exception ex)
            {
                AbortAutoModSyncJoin("AutoModSync manifest comparison failed: " + ex.Message);
            }
        }

        // Intent: Parses the signed manifest into the exact missing/hash-mismatched set that may be transferred.
        // Security: malformed/duplicate destinations, invalid hashes, excessive individual files, and excessive expanded bytes are rejected before a bundle request is sent.
        private static void BuildNeededList(string manifest)
        {
            NeededFiles.Clear();
            PendingRelativePaths.Clear();
            DesiredOwnershipEntries.Clear();
            _ownershipLedgerChanged = false;

#if AMS_DEV_TESTS
            bool devEmulateNearlyBareClient = ConsumeDevelopmentNearlyBareClientMarker();
#else
            bool devEmulateNearlyBareClient = false;
#endif

            string amsRoot = GetAutoModSyncRoot();
            bool sameAsImmediatelyPriorSuccessfulServer = AutoModSyncOwnershipState.WasLastSuccessfulServer(amsRoot, _serverFingerprint);
            List<AutoModSyncOwnershipEntry> owned = AutoModSyncOwnershipState.ReadLedger(amsRoot, _serverFingerprint);
            Dictionary<string, AutoModSyncOwnershipEntry> ownedByKey = new Dictionary<string, AutoModSyncOwnershipEntry>(StringComparer.OrdinalIgnoreCase);
            int oi;
            for (oi = 0; oi < owned.Count; oi++)
                ownedByKey[owned[oi].Kind + ":" + owned[oi].RelativePath] = owned[oi];

            Dictionary<string, ManifestEntry> manifestByKey = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            string[] lines = manifest.Replace("\r", "").Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedNeededBytes = 0L;
            int i;
            for (i = 0; i < lines.Length; i++)
            {
                string[] fields = lines[i].Split(new char[] { '\t' }, 4);
                if (fields.Length != 4 || fields[0].Length != 1)
                    throw new InvalidDataException("Server manifest contained a malformed record.");

                char kind = fields[0][0];
                if (!IsSupportedManifestKind(kind))
                    throw new InvalidDataException("Server manifest contained an unsupported AutoModSync file kind.");

                long size;
                if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 0)
                    throw new InvalidDataException("Server manifest contained an invalid file size.");
                if (!IsSha256Hex(fields[1]))
                    throw new InvalidDataException("Server manifest contained an invalid SHA-256 value.");

                string rel = NormalizeRelative(fields[3]);
                if (rel.Length == 0)
                    throw new InvalidDataException("Server manifest contained an unsafe Windows path.");

                string destinationKey = kind + ":" + rel;
                if (!seen.Add(destinationKey))
                    throw new InvalidDataException("Server manifest contained a duplicate destination: " + rel);

                if (kind == 'P' && IsPackageManagedAutoModSync() && IsAutoModSyncOwnedRelativePath(rel))
                {
                    if (_instance != null) _instance.Logger.LogDebug("Ignoring server-advertised package-managed AutoModSync file: " + rel);
                    continue;
                }

                ManifestEntry e = new ManifestEntry();
                e.Kind = kind;
                e.Sha256 = fields[1].ToLowerInvariant();
                e.Size = size;
                e.RelativePath = rel;
                manifestByKey[destinationKey] = e;

                string local = SafeTargetPath(kind, rel);
                bool needed = !File.Exists(local);
                if (!needed)
                {
                    FileInfo localInfo = new FileInfo(local);
                    needed = localInfo.Length != e.Size || !ConstantEquals(Sha256File(local), e.Sha256);
                }
#if AMS_DEV_TESTS
                if (devEmulateNearlyBareClient
                    && !(kind == 'P' && String.Equals(Path.GetFileName(rel), "ValheimAutoModSync.Client.dll", StringComparison.OrdinalIgnoreCase)))
                    needed = true;
#endif

                AutoModSyncOwnershipEntry previousOwned;
                bool wasOwned = ownedByKey.TryGetValue(destinationKey, out previousOwned);
                bool stillExactOwnedBytes = wasOwned
                    && previousOwned.Size == e.Size
                    && ConstantEquals(previousOwned.Sha256, e.Sha256);

                // Retain ownership only when the manifest still names the exact digest AMS last installed.
                // A changed manifest digest is owned again only if AMS itself must perform the verified write.
                // If some external/manual action already changed an owned destination to the server's new bytes,
                // preserve those bytes but relinquish ownership rather than claiming a change AMS did not perform.
                if (stillExactOwnedBytes || needed)
                    AddDesiredOwnership(e.Kind, e.RelativePath, e.Size, e.Sha256);
                else if (wasOwned && _instance != null)
                    _instance.Logger.LogWarning("AutoModSync found an externally changed owned file already matching the server and relinquished ownership: " + e.Kind + ":" + e.RelativePath);

                if (!needed) continue;
                expandedNeededBytes = AutoModSyncClientResourceSafety.AddRequiredFile(e.Size, expandedNeededBytes, rel);
                NeededFiles.Add(e);
                AutoModSyncClientResourceSafety.ValidateRequiredFileCount(NeededFiles.Count);
            }

            // A signed-manifest omission can retire only a path this exact trusted server previously caused AMS to own.
            // If the live bytes changed since that successful install, preserve the local file and relinquish ownership instead.
            for (oi = 0; oi < owned.Count; oi++)
            {
                AutoModSyncOwnershipEntry prior = owned[oi];
                string key = prior.Kind + ":" + prior.RelativePath;
                if (manifestByKey.ContainsKey(key)) continue;

                string local = SafeTargetPath(prior.Kind, prior.RelativePath);
                if (File.Exists(local))
                {
                    FileInfo info = new FileInfo(local);
                    bool exactOwnedBytes = info.Length == prior.Size && ConstantEquals(Sha256File(local), prior.Sha256);
                    if (exactOwnedBytes && sameAsImmediatelyPriorSuccessfulServer)
                    {
                        PendingRelativePaths.Add(MakePendingDeleteEntry(prior));
                    }
                    else if (exactOwnedBytes)
                    {
                        // A server switch must never make one server clean up another server's/client's current payload.
                        // Retain this server's ownership record and defer deletion until two consecutive successful AMS reconciliations target this same fingerprint.
                        AddDesiredOwnership(prior.Kind, prior.RelativePath, prior.Size, prior.Sha256);
                        if (_instance != null)
                            _instance.Logger.LogInfo("AutoModSync deferred stale owned deletion because the immediately prior successful sync used a different server: " + prior.Kind + ":" + prior.RelativePath);
                    }
                    else if (_instance != null)
                    {
                        _instance.Logger.LogWarning("AutoModSync preserved locally modified stale file and relinquished server ownership: " + prior.Kind + ":" + prior.RelativePath);
                    }
                }
                else if (Directory.Exists(local) && _instance != null)
                {
                    _instance.Logger.LogWarning("AutoModSync preserved unexpected directory at stale owned path and relinquished server ownership: " + prior.Kind + ":" + prior.RelativePath);
                }
            }

            _ownershipLedgerChanged = !AutoModSyncOwnershipState.Equivalent(owned, DesiredOwnershipEntries);
            _uiState.SetComparison(
                manifestByKey.Count,
                Math.Max(0, manifestByKey.Count - NeededFiles.Count),
                NeededFiles.Count,
                PendingRelativePaths.Count,
                expandedNeededBytes);
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
        // Security: the request count is rechecked immediately before serialization so later code cannot accidentally bypass the manifest-time ceiling.
        private static void RequestBundle()
        {
            CloseBundleStream();
            if (_pendingRpc == null || NeededFiles.Count == 0) return;
            if (NeededFiles.Count > MaxBundleFiles) throw new InvalidDataException("AutoModSync bundle request exceeds the client file-count limit.");

            _bundleRequestKey = BuildBundleRequestKey();
            _resumeOfferedCandidate = null;

            if (_serverSupportsBundleResume)
            {
                string resumeReason;
                AutoModSyncResumeCandidate candidate;
                if (AutoModSyncResumeState.TryPrepareClientCandidate(
                    GetAutoModSyncRoot(),
                    _serverFingerprint,
                    _bundleRequestKey,
                    AutoModSyncResumeState.DefaultMaxAgeSeconds,
                    out candidate,
                    out resumeReason))
                {
                    _resumeOfferedCandidate = candidate;
                    if (_instance != null)
                        _instance.Logger.LogInfo("AutoModSync found resumable bundle prefix: " +
                            FormatBytes(candidate.NextChunk == candidate.TotalChunks ? candidate.BundleSize : (long)candidate.NextChunk * candidate.ChunkBytes) +
                            " already present; asking the server to verify the exact artifact prefix.");
                }
                else if (_instance != null && !String.IsNullOrEmpty(resumeReason) && resumeReason != "no saved resume metadata")
                {
                    _instance.Logger.LogDebug("AutoModSync did not offer saved bundle resume: " + resumeReason + ".");
                }
            }

            ZPackage request = new ZPackage();
            request.Write(NeededFiles.Count);
            int i;
            for (i = 0; i < NeededFiles.Count; i++) request.Write(NeededFiles[i].Kind + ":" + NeededFiles[i].RelativePath);

            // Optional AMS4 capability extension. Older servers never advertise bundle-resume1, so they receive the original request shape.
            if (_serverSupportsBundleResume)
            {
                request.Write(_resumeOfferedCandidate == null ? 0 : 1);
                if (_resumeOfferedCandidate != null)
                {
                    request.Write(_resumeOfferedCandidate.BundleSha256);
                    request.Write(_resumeOfferedCandidate.BundleSize.ToString(CultureInfo.InvariantCulture));
                    request.Write(_resumeOfferedCandidate.ChunkBytes);
                    request.Write(_resumeOfferedCandidate.TotalChunks);
                    request.Write(_resumeOfferedCandidate.FileCount);
                    request.Write(_resumeOfferedCandidate.NextChunk);
                    request.Write(_resumeOfferedCandidate.PrefixSha256);
                }
            }

            _pendingRpc.Invoke(RpcGetBundle, new object[] { request });
        }

        // Intent: Identifies the exact signed file set expected in the compressed bundle for safe cross-connection resume.
        private static string BuildBundleRequestKey()
        {
            List<string> rows = new List<string>();
            int i;
            for (i = 0; i < NeededFiles.Count; i++)
            {
                ManifestEntry e = NeededFiles[i];
                rows.Add(e.Kind + "\t" + e.RelativePath + "\t" + e.Size.ToString(CultureInfo.InvariantCulture) + "\t" + e.Sha256);
            }
            return AutoModSyncResumeState.ComputeRequestKey(_serverFingerprint, rows);
        }

        // Intent: Validates the server's bundle header before allocating/writing the staging archive, then requests the first chunk.
        // Security: compressed bytes, file count, chunk count, and SHA-256 syntax are bounded independently of server configuration.
        private static void RPC_BundleBegin(ZRpc rpc, ZPackage pkg)
        {
            if (rpc != _pendingRpc || NeededFiles.Count == 0) return;
            try
            {
                string sizeText = pkg.ReadString();
                string sha = pkg.ReadString();
                int chunks = pkg.ReadInt();
                int files = pkg.ReadInt();
                int chunkBytes = 0;
                int resumeStartChunk = 0;
                if (_serverSupportsBundleResume)
                {
                    chunkBytes = pkg.ReadInt();
                    resumeStartChunk = pkg.ReadInt();
                }

                long size = AutoModSyncClientResourceSafety.ValidateBundleHeader(
                    sizeText,
                    sha,
                    chunks,
                    files,
                    NeededFiles.Count,
                    _serverSupportsBundleResume,
                    chunkBytes,
                    resumeStartChunk,
                    out chunkBytes);

#if AMS_DEV_TESTS
                if (_devEmulateLegacyClient && !_serverSupportsBundleResume && _instance != null)
                    _instance.Logger.LogInfo("AutoModSync DEV TEST legacy-client compatibility confirmed: original AMS4 bundle header shape accepted.");

                if (ConsumeDevelopmentPhase3StopAfterBundleHeaderMarker())
                {
                    if (_instance != null)
                        _instance.Logger.LogInfo("AutoModSync DEV TEST Phase 3 stopping after validated bundle header before payload transfer.");
                    AbortAutoModSyncJoin("DEV TEST Phase 3 stopped after validated bundle header.");
                    return;
                }
#endif

                if (!_serverSupportsBundleResume)
                    resumeStartChunk = 0;

                string amsRoot = GetAutoModSyncRoot();
                FileStream stream = null;
                string bundlePath = "";
                long resumeBytes = 0L;
                bool resumed = false;

                if (_serverSupportsBundleResume && resumeStartChunk > 0 && _resumeOfferedCandidate != null)
                {
                    AutoModSyncResumeCandidate accepted = new AutoModSyncResumeCandidate();
                    accepted.BundleSha256 = sha;
                    accepted.BundleSize = size;
                    accepted.ChunkBytes = chunkBytes;
                    accepted.TotalChunks = chunks;
                    accepted.FileCount = files;
                    accepted.NextChunk = resumeStartChunk;
                    accepted.PrefixSha256 = _resumeOfferedCandidate.PrefixSha256;

                    string resumeReason = "";
                    if (_resumeOfferedCandidate.NextChunk == resumeStartChunk
                        && AutoModSyncResumeState.TryOpenAcceptedClientPartial(
                            amsRoot,
                            _serverFingerprint,
                            _bundleRequestKey,
                            accepted,
                            out stream,
                            out bundlePath,
                            out resumeBytes,
                            out resumeReason))
                    {
                        resumed = true;
                    }
                    else if (_instance != null)
                    {
                        _instance.Logger.LogWarning("AutoModSync server accepted a resume prefix that could not be safely reopened locally; restarting this bundle from zero. " + (resumeReason ?? ""));
                    }
                }

                if (!resumed)
                {
                    if (_serverSupportsBundleResume && _resumeOfferedCandidate != null && resumeStartChunk == 0 && _instance != null)
                        _instance.Logger.LogInfo("AutoModSync server declined the saved resume candidate; restarting this bundle from chunk 0.");

                    if (_serverSupportsBundleResume)
                    {
                        stream = AutoModSyncResumeState.CreateFreshClientPartial(
                            amsRoot,
                            _serverFingerprint,
                            _bundleRequestKey,
                            sha,
                            size,
                            chunkBytes,
                            chunks,
                            files,
                            out bundlePath);
                        _bundleResumeSlotActive = true;
                    }
                    else
                    {
                        string stagingRoot = Path.Combine(amsRoot, "staging");
                        if (!Directory.Exists(stagingRoot)) Directory.CreateDirectory(stagingRoot);
                        bundlePath = Path.Combine(stagingRoot, "bundle.zip.amsnew");
                        AutoModSyncPathSafety.EnsureNoReparsePoints(stagingRoot, bundlePath, true);
                        stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write, FileShare.None);
                        _bundleResumeSlotActive = false;
                    }
                    resumeStartChunk = 0;
                    resumeBytes = 0L;
                }
                else
                {
                    _bundleResumeSlotActive = true;
                }

                _bundlePath = bundlePath;
                _bundleStream = stream;
                _bundleSha256 = sha;
                _bundleSize = size;
                _bundleBytesReceived = resumeBytes;
                _bundleSessionStartBytes = resumeBytes;
                _bundleStartedUtc = DateTime.UtcNow;
                _bundleNextChunk = resumeStartChunk;
                _bundleTotalChunks = chunks;
                _bundleChunkBytes = chunkBytes;
                _bundleWindowEndExclusive = resumeStartChunk;
                _uiState.BeginTransfer(size, resumeBytes, _bundleStartedUtc);

#if AMS_DEV_TESTS
                _devDisconnectAfterChunk = ReadDevelopmentResumeDisconnectMarker(chunks);
#endif

                ShowSyncOverlay(AutoModSyncUiPhase.Downloading,
                    resumed ? "Resuming required mods..." : "Downloading required mods...",
                    resumed ? "Verified package data was retained from the previous interrupted transfer." : "Receiving the server's verified compressed mod package.");
                if (resumed && _instance != null)
                    _instance.Logger.LogInfo("AutoModSync exact-artifact resume accepted at chunk " + resumeStartChunk.ToString(CultureInfo.InvariantCulture) + "/" + chunks.ToString(CultureInfo.InvariantCulture) + " (" + FormatBytes(resumeBytes) + " retained).");

                if (_instance != null && _serverSupportsBundlePipeline)
                    _instance.Logger.LogInfo("AutoModSync using pipelined binary bundle transfer (up to " + BundlePipelineChunks.ToString(CultureInfo.InvariantCulture) + " chunks requested per window; " + BundleBatchChunks.ToString(CultureInfo.InvariantCulture) + " chunks per Steam message).");
                else if (_instance != null && _serverSupportsBundleBatch)
                    _instance.Logger.LogInfo("AutoModSync using binary batched bundle transfer (up to " + BundleBatchChunks.ToString(CultureInfo.InvariantCulture) + " chunks per request).");
                else if (_instance != null && _serverSupportsBundleWindow)
                    _instance.Logger.LogInfo("AutoModSync using windowed bundle transfer (" + BundleBatchChunks.ToString(CultureInfo.InvariantCulture) + " chunks per request).");
                RequestBundleChunk();
            }
            catch (Exception ex)
            {
                AbortAutoModSyncJoin("Could not prepare compressed mod download: " + ex.Message);
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
                if (encoded == null || encoded.Length > 70000) throw new InvalidDataException("Oversized compressed package chunk.");
                byte[] data = Convert.FromBase64String(encoded);
                ValidateBundleChunkLength(index, data == null ? 0 : data.Length);
                _bundleStream.Write(data, 0, data.Length);
                _bundleBytesReceived += data.Length;
                _bundleNextChunk++;
                _uiState.UpdateTransfer(_bundleBytesReceived, DateTime.UtcNow);
#if AMS_DEV_TESTS
                if (DevelopmentDisconnectForResumeIfArmed(rpc)) return;
#endif
                if (_bundleNextChunk >= _bundleTotalChunks || _bundleNextChunk >= _bundleWindowEndExclusive)
                    RequestBundleChunk();
            }
            catch (Exception ex)
            {
                AbortAutoModSyncJoin("Compressed mod package download failed: " + ex.Message);
            }
        }

        // Intent: Receives one binary batch containing several consecutive bundle chunks and appends them in strict manifest order.
        // Performance: avoids Base64 expansion and collapses many ZRpc messages into one bounded package while retaining per-chunk ordering and final bundle SHA-256 verification.
        private static void RPC_BundleBatch(ZRpc rpc, ZPackage pkg)
        {
            if (rpc != _pendingRpc || _bundleStream == null) return;
            try
            {
                int start = pkg.ReadInt();
                int count = pkg.ReadInt();
                if (!_serverSupportsBundleBatch || start != _bundleNextChunk || count < 1 || count > BundleBatchChunks || start + count > _bundleTotalChunks || start + count > _bundleWindowEndExclusive)
                    throw new InvalidDataException("Invalid compressed package batch.");

                int i;
                for (i = 0; i < count; i++)
                {
                    int index = pkg.ReadInt();
                    byte[] data = pkg.ReadByteArray();
                    if (index != _bundleNextChunk || data == null || data.Length < 1 || data.Length > 65536)
                        throw new InvalidDataException("Out-of-order or oversized compressed package batch chunk.");
                    ValidateBundleChunkLength(index, data.Length);
                    _bundleStream.Write(data, 0, data.Length);
                    _bundleBytesReceived += data.Length;
                    _bundleNextChunk++;
#if AMS_DEV_TESTS
                    if (DevelopmentDisconnectForResumeIfArmed(rpc)) return;
#endif
                }

                _uiState.UpdateTransfer(_bundleBytesReceived, DateTime.UtcNow);
                if (_bundleNextChunk >= _bundleTotalChunks || _bundleNextChunk >= _bundleWindowEndExclusive)
                    RequestBundleChunk();
            }
            catch (Exception ex)
            {
                AbortAutoModSyncJoin("Compressed mod package batch download failed: " + ex.Message);
            }
        }

        // Intent: Enforces the exact chunk geometry published by resume-capable servers so every persisted offset is a complete immutable-artifact boundary.
        private static void ValidateBundleChunkLength(int index, int length)
        {
            AutoModSyncClientResourceSafety.ValidateIncomingChunk(
                index,
                length,
                _bundleBytesReceived,
                _bundleSize,
                _bundleTotalChunks,
                _serverSupportsBundleResume,
                _bundleChunkBytes);
        }

        // Intent: Requests the next verified binary batch when supported, otherwise a bounded transfer window or one legacy AMS4 chunk.
        // Workflow: binary-batch peers move several raw chunks in one RPC; window-capable peers use several ordered chunk RPCs per request; legacy peers retain the original single-chunk pull.
        private static void RequestBundleChunk()
        {
            if (_pendingRpc == null || _bundleStream == null) return;
            int requestCount = _serverSupportsBundlePipeline ? BundlePipelineChunks : ((_serverSupportsBundleBatch || _serverSupportsBundleWindow) ? BundleBatchChunks : 1);
            _bundleWindowEndExclusive = Math.Min(_bundleTotalChunks, _bundleNextChunk + requestCount);
            ZPackage request = new ZPackage();
            request.Write(_bundleNextChunk);
            if (_serverSupportsBundleBatch || _serverSupportsBundleWindow) request.Write(requestCount);
            _pendingRpc.Invoke(_serverSupportsBundleBatch ? RpcGetBundleBatch : RpcGetBundleChunk, new object[] { request });
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

                _uiState.UpdateTransfer(_bundleSize, DateTime.UtcNow);
                _uiState.BeginVerification(NeededFiles.Count);
                ShowSyncOverlay(AutoModSyncUiPhase.Verifying, "Verifying synchronized files...",
                    "Checking extracted file sizes and SHA-256 hashes before anything can be applied.");
                ExtractBundleToStaging();
                if (_bundleResumeSlotActive)
                {
                    AutoModSyncResumeState.Discard(GetAutoModSyncRoot());
                    _bundleResumeSlotActive = false;
                }
                else
                {
                    try { File.Delete(_bundlePath); } catch { }
                }
                if (_instance != null)
                {
                    double elapsedSeconds = _bundleStartedUtc == DateTime.MinValue ? 0.0 : Math.Max(0.001, (DateTime.UtcNow - _bundleStartedUtc).TotalSeconds);
                    long sessionBytes = Math.Max(0L, _bundleSize - _bundleSessionStartBytes);
                    double mibPerSecond = (sessionBytes / (1024.0 * 1024.0)) / elapsedSeconds;
                    _instance.Logger.LogInfo("AutoModSync compressed package verified and unpacked: " + NeededFiles.Count + " changed file(s). Transfer " + FormatBytes(sessionBytes) +
                        (_bundleSessionStartBytes > 0 ? " after retaining " + FormatBytes(_bundleSessionStartBytes) : "") +
                        " in " + elapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s (" + mibPerSecond.ToString("0.00", CultureInfo.InvariantCulture) + " MiB/s).");
                }
                BeginApplyAndRestart();
            }
            catch (Exception ex)
            {
                AbortAutoModSyncJoin("Compressed mod package verification failed: " + ex.Message);
            }
        }

        // Intent: Extracts only files explicitly present in the signed NeededFiles set into AutoModSync staging.
        // Security: entry names, declared lengths, cumulative expanded bytes, reparse points, final sizes, and SHA-256 values are all checked before any live plugin is replaced.
        private static void ExtractBundleToStaging()
        {
            Dictionary<string, ManifestEntry> expected = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            int i;
            for (i = 0; i < NeededFiles.Count; i++)
            {
                ManifestEntry e = NeededFiles[i];
                if (e.Size < 0 || e.Size > MaxIndividualSyncFileBytes)
                    throw new InvalidDataException("Signed manifest entry exceeds the AutoModSync client file limit: " + e.RelativePath);
                string entryName = ManifestKindDirectory(e.Kind) + "/" + e.RelativePath.Replace('\\', '/');
                if (expected.ContainsKey(entryName))
                    throw new InvalidDataException("Signed manifest contains a duplicate archive destination: " + e.RelativePath);
                expected[entryName] = e;
            }

            HashSet<string> extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0L;
            using (FileStream input = new FileStream(_bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (ZipArchive archive = new ZipArchive(input, ZipArchiveMode.Read, false))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = NormalizeZipEntry(entry.FullName);
                    if (name.Length == 0) throw new InvalidDataException("Compressed package contained an unsafe Windows path.");

                    ManifestEntry expectedEntry;
                    if (!expected.TryGetValue(name, out expectedEntry))
                        throw new InvalidDataException("Compressed package contained an unexpected file: " + name);
                    if (!extracted.Add(name))
                        throw new InvalidDataException("Compressed package contained a duplicate file: " + name);
                    if (entry.Length != expectedEntry.Size)
                        throw new InvalidDataException("Compressed package entry length did not match the signed manifest: " + expectedEntry.RelativePath);

                    string stagingRoot = Path.Combine(GetAutoModSyncRoot(), "staging", ManifestKindDirectory(expectedEntry.Kind));
                    string output = SafeUnder(stagingRoot, expectedEntry.RelativePath) + ".amsnew";
                    string parent = Path.GetDirectoryName(output);
                    if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                    AutoModSyncPathSafety.EnsureNoReparsePoints(stagingRoot, output, true);

                    using (Stream source = entry.Open())
                    {
                        AutoModSyncClientResourceSafety.WriteVerifiedExtractedEntry(
                            source,
                            output,
                            expectedEntry.Size,
                            expectedEntry.Sha256,
                            ref expandedBytes);
                    }

                    // Pending apply acceptance occurs only after the extracted staging file has passed size/SHA-256 verification.
                    PendingRelativePaths.Add(MakePendingWriteEntry(expectedEntry));
                    _uiState.MarkVerified();
                }
            }

            if (extracted.Count != expected.Count)
                throw new InvalidDataException("Compressed package did not contain every requested file.");
        }

        // Intent: Copies one ZIP entry while enforcing the signed size continuously instead of trusting a final length check.
        // Security: this prevents a malicious compressed stream from expanding until disk exhaustion before AutoModSync notices the mismatch.
        private static void CopyZipEntryBounded(Stream source, Stream destination, long expectedBytes, ref long cumulativeExpandedBytes)
        {
            AutoModSyncClientResourceSafety.CopyZipEntryBounded(source, destination, expectedBytes, ref cumulativeExpandedBytes);
        }

        // Intent: Applies the same Windows-safe path policy to ZIP entry names used by the signed manifest.
        private static string NormalizeZipEntry(string value)
        {
            return AutoModSyncPathSafety.NormalizeRelative(value);
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

        // Intent: Displays FIFO synchronization-queue status while a recognized AMS server is holding this client for an active transfer slot.
        // Compatibility: this optional RPC is used only when bundle-scheduler1 was advertised during AMS4 acknowledgement.
        private static void RPC_QueueStatus(ZRpc rpc, ZPackage pkg)
        {
            if (rpc != _pendingRpc || !_serverRecognized || !_serverSupportsBundleScheduler) return;
            try
            {
                int position = pkg.ReadInt();
                int active = pkg.ReadInt();
                int maxActive = pkg.ReadInt();
                if (position < 1 || position > 100000 || active < 0 || maxActive < 1 || active > maxActive)
                    throw new InvalidDataException("Invalid AutoModSync synchronization queue status.");

                _uiState.SetQueue(position, active, maxActive);
                ShowSyncOverlay(AutoModSyncUiPhase.Queued, "Queued for synchronization...",
                    "The server is limiting simultaneous fresh-client transfers. Your place is reserved.");
                if (_instance != null)
                    _instance.Logger.LogInfo("AutoModSync synchronization queue: position " + position.ToString(CultureInfo.InvariantCulture) +
                        ", active=" + active.ToString(CultureInfo.InvariantCulture) + "/" + maxActive.ToString(CultureInfo.InvariantCulture) + ".");
            }
            catch (Exception ex)
            {
                AbortAutoModSyncJoin("Invalid AutoModSync synchronization queue status: " + ex.Message);
            }
        }

        // Intent: Handles a server-reported AMS error for the active RPC and falls back to normal Valheim behavior without applying files.
        private static void RPC_Error(ZRpc rpc, ZPackage pkg)
        {
            if (rpc != _pendingRpc) return;
            string message = "Server reported an AutoModSync error.";
            try { message = pkg.ReadString(); } catch { }
            AbortAutoModSyncJoin(message);
        }

        // Intent: Persists the verified pending-file list and reconnect token, launches the external apply helper, disconnects cleanly, then schedules Valheim to quit.
        // Reason: loaded plugin DLLs cannot be safely replaced in-process, so file replacement occurs after this process exits.
        private static void BeginApplyAndRestart()
        {
            if (_restartRequested) return;
            _restartRequested = true;
            ShowSyncOverlay(AutoModSyncUiPhase.Applying, "Preparing synchronized changes...",
                "Verified files are ready. Preparing a crash-safe apply transaction before Valheim restarts.");
            try
            {
                string amsRoot = GetAutoModSyncRoot();
                if (!Directory.Exists(amsRoot)) Directory.CreateDirectory(amsRoot);
                string pending = Path.Combine(amsRoot, "pending.txt");
                if (PendingRelativePaths.Count == 0)
                    throw new InvalidOperationException("AutoModSync apply/restart was requested without any live file operations.");
#if AMS_DEV_TESTS
                if (ConsumeDevelopmentApplyPreparationFailureMarker())
                    throw new InvalidOperationException("DEV TEST forced apply/restart preparation failure before durable transaction state.");
#endif

                // ownership-next is written first; pending.txt is the durable trigger observed by startup recovery.
                // A crash between these writes leaves harmless metadata but never a half-described live transaction.
                AutoModSyncOwnershipState.WritePendingDurable(amsRoot, _serverFingerprint, DesiredOwnershipEntries);
                WritePendingFileDurable(pending, PendingRelativePaths);
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

                ShowSyncOverlay(AutoModSyncUiPhase.Restarting,
                    reconnectAvailable ? "Sync complete. Restarting Valheim..." : "Sync complete. Restarting Valheim...",
                    reconnectAvailable ? "Verified changes will be applied out-of-process, then AutoModSync will reconnect automatically." : "Verified changes will be applied out-of-process before Valheim relaunches.");
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
                AbortAutoModSyncJoin("Mods downloaded but automatic apply/restart failed: " + ex.Message);
            }
        }

        // Intent: Publishes the verified pending-file list atomically and durably before launching the helper.
        // Safety: a temporary file is flushed with write-through semantics, then renamed on the same volume; an existing pending request is treated as recovery state instead of being overwritten.
        // Intent: Writes the versioned Phase 6 apply plan durably; write entries name verified staging files and delete entries carry the last-owned digest.
        private static void WritePendingFileDurable(string pendingPath, IList<string> entries)
        {
            if (File.Exists(pendingPath))
                throw new InvalidOperationException("An earlier AutoModSync pending apply still exists.");
            if (entries == null || entries.Count == 0)
                throw new InvalidDataException("AutoModSync pending apply contains no operations.");

            string temp = pendingPath + ".tmp";
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
            {
                writer.WriteLine("AMSPENDING2");
                int i;
                for (i = 0; i < entries.Count; i++)
                    writer.WriteLine(entries[i] ?? "");

                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temp, pendingPath);
        }

        // Intent: Encodes one verified staged replacement into the versioned apply plan without permitting a destination outside the fixed kind root.
        private static string MakePendingWriteEntry(ManifestEntry entry)
        {
            if (entry == null || !IsSupportedManifestKind(entry.Kind))
                throw new InvalidDataException("Invalid AutoModSync pending write entry.");
            string rel = NormalizeRelative(entry.RelativePath);
            if (rel.Length == 0) throw new InvalidDataException("Unsafe AutoModSync pending write path.");
            return "W|" + entry.Kind + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(rel));
        }

        // Intent: Encodes deletion authority from the exact last-owned digest; the apply helper rechecks these bytes after Valheim exits before deleting anything.
        private static string MakePendingDeleteEntry(AutoModSyncOwnershipEntry entry)
        {
            if (entry == null || !IsSupportedManifestKind(entry.Kind) || entry.Size < 0 || !IsSha256Hex(entry.Sha256))
                throw new InvalidDataException("Invalid AutoModSync pending delete entry.");
            string rel = NormalizeRelative(entry.RelativePath);
            if (rel.Length == 0) throw new InvalidDataException("Unsafe AutoModSync pending delete path.");
            return "D|" + entry.Kind + "|" + entry.Size.ToString(CultureInfo.InvariantCulture) + "|" +
                entry.Sha256.ToLowerInvariant() + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(rel));
        }

        // Intent: Adds one canonical post-commit ownership record; duplicate destinations indicate an internal manifest/ownership inconsistency.
        private static void AddDesiredOwnership(char kind, string relative, long size, string sha256)
        {
            string key = kind + ":" + relative;
            int i;
            for (i = 0; i < DesiredOwnershipEntries.Count; i++)
                if (String.Equals(DesiredOwnershipEntries[i].Kind + ":" + DesiredOwnershipEntries[i].RelativePath, key, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Duplicate AutoModSync desired ownership destination.");

            AutoModSyncOwnershipEntry entry = new AutoModSyncOwnershipEntry();
            entry.Kind = kind;
            entry.RelativePath = relative;
            entry.Size = size;
            entry.Sha256 = sha256.ToLowerInvariant();
            DesiredOwnershipEntries.Add(entry);
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

#if AMS_DEV_TESTS
            _devUiPreviewActive = false;
#endif
            _pendingRpc = rpc;
            _pendingPassword = "";
            _waitingForServer = true;
            _serverRecognized = false;
            _serverAcknowledged = false;
            _serverSupportsBundleWindow = false;
            _serverSupportsBundleBatch = false;
            _serverSupportsBundlePipeline = false;
            _serverSupportsBundleResume = false;
            _serverSupportsBundleScheduler = false;
            _preflightGateActive = true;
            _serverHandshakeHeld = false;
            _heldServerHandshakeParameters = new object[0];
#if AMS_DEV_TESTS
            _devEmulateLegacyClient = ConsumeDevelopmentLegacyClientMarker();
#endif
            _helloSentUtc = DateTime.MinValue;
            _lastHelloAttemptUtc = DateTime.MinValue;
            _helloAttemptCount = 0;
            ResetManifestState();
            if (_uiState.Phase != AutoModSyncUiPhase.Reconnecting)
                HideSyncOverlay();

            _reconnectHost = GetReconnectTarget(rpc);
            _reconnectBackend = _capturedServerBackend >= 0 ? _capturedServerBackend : GetCurrentOnlineBackend();
            if (_instance != null && _reconnectHost.Length > 0)
                _instance.Logger.LogDebug("AutoModSync preflight captured reconnect endpoint " + _reconnectHost + ".");
        }

        // Intent: Starts the AMS4_Hello preflight after Valheim has registered its base RPC handlers while preserving the timestamp of the first attempt for the fail-open deadline.
        private static void BeginPreflightProbe(ZRpc rpc)
        {
            if (rpc == null || rpc != _pendingRpc || !_preflightGateActive || PreflightComplete.Contains(rpc)) return;
            if (_helloSentUtc == DateTime.MinValue) _helloSentUtc = DateTime.UtcNow;
            SendPreflightHello(rpc, false);
        }

        // Intent: Sends or retries AMS4_Hello on the existing ZRpc without releasing the held vanilla handshake.
        // Reliability: retries cover the short race where the client can enqueue its first custom RPC before the dedicated server has finished registering AutoModSync handlers for that new ZRpc.
        private static void SendPreflightHello(ZRpc rpc, bool retry)
        {
            if (rpc == null || rpc != _pendingRpc || !_waitingForServer || _serverAcknowledged || _serverRecognized) return;
            try
            {
                ZPackage hello = new ZPackage();
                hello.Write(ProtocolVersion);
                hello.Write(PluginVersion);
                hello.Write(GetCurrentClientCapabilities());
                rpc.Invoke(RpcHello, new object[] { hello });
                _lastHelloAttemptUtc = DateTime.UtcNow;
                _helloAttemptCount++;
                if (_instance != null)
                    _instance.Logger.LogDebug("AutoModSync preflight probe " + _helloAttemptCount.ToString(CultureInfo.InvariantCulture) + (retry ? " retried." : " sent before Valheim ServerHandshake."));
            }
            catch (Exception ex)
            {
                if (retry)
                {
                    _lastHelloAttemptUtc = DateTime.UtcNow;
                    _helloAttemptCount++;
                    if (_instance != null) _instance.Logger.LogDebug("AutoModSync preflight retry failed: " + ex.Message);
                }
                else
                {
                    FailOpen("AutoModSync preflight probe failed: " + ex.Message);
                }
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
                object[] serverHandshakeParameters = _heldServerHandshakeParameters == null ? new object[0] : (object[])_heldServerHandshakeParameters.Clone();
                _waitingForServer = false;
                _serverRecognized = false;
                _serverAcknowledged = false;
                _serverSupportsBundleWindow = false;
                _serverSupportsBundleBatch = false;
                _serverSupportsBundlePipeline = false;
                _serverSupportsBundleResume = false;
                _serverSupportsBundleScheduler = false;
                _preflightGateActive = false;
                _serverHandshakeHeld = false;
                _heldServerHandshakeParameters = new object[0];
                _pendingRpc = null;
                _helloSentUtc = DateTime.MinValue;
                _lastHelloAttemptUtc = DateTime.MinValue;
                _helloAttemptCount = 0;
                if (!_restartRequested && _uiState.Phase != AutoModSyncUiPhase.Complete) HideSyncOverlay();
                ResetManifestState();

                if (rpc != null) PreflightComplete.Add(rpc);
                if (!releaseServerHandshake || rpc == null) return;

                try
                {
                    _allowServerHandshake = true;
                    rpc.Invoke("ServerHandshake", serverHandshakeParameters);
                    if (_instance != null) _instance.Logger.LogInfo("AutoModSync released the original Valheim ServerHandshake with " + serverHandshakeParameters.Length.ToString(CultureInfo.InvariantCulture) + " argument(s) after preflight.");
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
            _serverAcknowledged = false;
            _serverSupportsBundleWindow = false;
            _serverSupportsBundleBatch = false;
            _serverSupportsBundlePipeline = false;
            _serverSupportsBundleResume = false;
            _serverSupportsBundleScheduler = false;
            _pendingRpc = null;
            if (!_restartRequested && _uiState.Phase != AutoModSyncUiPhase.Complete) HideSyncOverlay();
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

        // Intent: Fail open only while discovering whether the remote endpoint supports AutoModSync.
        // Compatibility: genuine non-AMS servers keep the 2.5 behavior and receive the untouched normal Valheim handshake.
        private static void FailOpen(string reason)
        {
            CloseBundleStream();
            DeleteActiveBundleFile();
            if (_instance != null) _instance.Logger.LogWarning(reason + " AutoModSync discovery did not establish a protected AMS session; continuing Valheim normally.");
            ResumeNormalHandshake();
        }

        // Intent: Stops a join after the remote endpoint has positively entered the AutoModSync preflight path.
        // Security: signature/trust/path/resource/transfer failures must never become a way to bypass required synchronization and reach the vanilla handshake.
        private static void AbortAutoModSyncJoin(string reason)
        {
            ZRpc rpc = _pendingRpc;
            CloseBundleStream();
            DeleteActiveBundleFile();

            _waitingForServer = false;
            _serverRecognized = true;
            _serverAcknowledged = true;
            _allowPeerInfo = false;
            _allowServerHandshake = false;

            // Keep the gate armed for this exact RPC until the socket is closed. If close itself fails,
            // the original ServerHandshake remains held rather than silently falling through.
            _preflightGateActive = rpc != null;
            _serverHandshakeHeld = rpc != null;
            _heldServerHandshakeParameters = new object[0];

            ResetManifestState();
            ShowTransientSyncOverlay(AutoModSyncUiPhase.Failed, "AutoModSync blocked this join.",
                reason ?? "Synchronization failed.", 4.0);
            if (_instance != null) _instance.Logger.LogError((reason ?? "AutoModSync synchronization failed.") + " The recognized AutoModSync join was aborted.");

            if (rpc == null) return;
            try { rpc.Invoke("Disconnect", new object[0]); } catch { }
            try
            {
                if (rpc.GetSocket() != null) rpc.GetSocket().Close();
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync could not close the failed join socket cleanly: " + ex.Message);
            }
        }

        // Intent: Removes only the active compressed staging archive after a failed/discarded transfer.
        // Safety: extracted .amsnew files are not live BepInEx files and remain inert until a later verified apply phase.
        private static void DeleteActiveBundleFile()
        {
            if (_bundleResumeSlotActive)
            {
                AutoModSyncResumeState.Discard(GetAutoModSyncRoot());
                _bundleResumeSlotActive = false;
            }
            else if (!String.IsNullOrEmpty(_bundlePath))
            {
                try { if (File.Exists(_bundlePath)) File.Delete(_bundlePath); } catch { }
            }
        }

        // Intent: Clears per-manifest and per-bundle state so stale data from one connection cannot contaminate the next synchronization attempt.
        private static void ResetManifestState()
        {
            ClearPendingTrustPrompt();
            ManifestParts.Clear();
            _manifestPartCount = 0;
            _manifestSignature = "";
            _serverPublicKeyXml = "";
            _serverFingerprint = "";
            NeededFiles.Clear();
            PendingRelativePaths.Clear();
            DesiredOwnershipEntries.Clear();
            _ownershipLedgerChanged = false;
            _bundleSize = 0L;
            _bundleSha256 = "";
            _bundleNextChunk = 0;
            _bundleTotalChunks = 0;
            _bundleWindowEndExclusive = 0;
            _bundleChunkBytes = 0;
            _bundleBytesReceived = 0L;
            _bundleSessionStartBytes = 0L;
            _bundleStartedUtc = DateTime.MinValue;
            _bundlePath = "";
            _bundleRequestKey = "";
            _resumeOfferedCandidate = null;
            _bundleResumeSlotActive = false;
#if AMS_DEV_TESTS
            _devDisconnectAfterChunk = 0;
#endif
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
                ShowSyncOverlay(AutoModSyncUiPhase.Reconnecting, "Reconnecting to synchronized server...",
                    "Valheim restarted successfully. Waiting for the main menu network stack to become ready.");
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
                ShowTransientSyncOverlay(AutoModSyncUiPhase.Failed, "Automatic reconnect timed out.",
                    "Synchronization is already applied. Join the server again manually.", 5.0);
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
                ShowTransientSyncOverlay(AutoModSyncUiPhase.Failed, "Automatic reconnect could not continue.",
                    "The saved server endpoint was invalid. Join the server again manually.", 5.0);
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
                    ShowSyncOverlay(AutoModSyncUiPhase.Reconnecting, "Opening synchronized server connection...",
                        "AutoModSync is handing the saved endpoint back to Valheim.");
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

        // Intent: General containment helper for staging/state roots; rejects any normalized relative path whose full path escapes the supplied root.
        private static string SafeUnder(string rootPath, string relative)
        {
            return AutoModSyncPathSafety.SafeUnderRoot(rootPath, relative, true);
        }

        // Intent: Applies the shared 2.6 Windows-safe relative-path policy to every signed/staged path.
        private static string NormalizeRelative(string value)
        {
            return AutoModSyncPathSafety.NormalizeRelative(value);
        }

        // Intent: Computes a file's SHA-256 while allowing other readers, used for local manifest comparison and post-extraction verification.
        private static string Sha256File(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (SHA256 sha = SHA256.Create()) return ToHex(sha.ComputeHash(fs));
        }

        // Intent: Maps a manifest file-kind code to one hardcoded BepInEx destination root and rejects every other kind.
        // Security: the server can choose only a path relative to plugins, patchers, or explicitly allowlisted config; it cannot supply arbitrary filesystem destinations.
        private static string SafeTargetPath(char kind, string relative)
        {
            if (kind == 'P') return SafeBepInExRootPath(Paths.PluginPath, relative, "plugins");
            if (kind == 'R') return SafeBepInExRootPath(Path.Combine(Paths.BepInExRootPath, "patchers"), relative, "patchers");
            if (kind == 'C')
            {
                if (IsProtectedConfigName(Path.GetFileName(relative)))
                    throw new InvalidDataException("Refusing to synchronize a protected BepInEx/AutoModSync config file.");
                return SafeBepInExRootPath(Paths.ConfigPath, relative, "config");
            }
            throw new InvalidDataException("Unsupported AutoModSync target kind.");
        }

        // Intent: Independently rejects loader-wide BepInEx configuration and AutoModSync signing identity files even if a server attempts to advertise them as config entries.
        private static bool IsProtectedConfigName(string name)
        {
            return String.Equals(name, "ValheimAutoModSync.private.xml", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "ValheimAutoModSync.public.xml", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "BepInEx.cfg", StringComparison.OrdinalIgnoreCase);
        }

        // Intent: Maps each supported manifest kind to its fixed archive/staging directory name.
        private static string ManifestKindDirectory(char kind)
        {
            if (kind == 'P') return "plugins";
            if (kind == 'R') return "patchers";
            if (kind == 'C') return "config";
            throw new InvalidDataException("Unsupported AutoModSync manifest kind.");
        }

        // Intent: Recognizes only the manifest kinds implemented by both the client verifier and out-of-process apply helper.
        private static bool IsSupportedManifestKind(char kind)
        {
            return kind == 'P' || kind == 'R' || kind == 'C';
        }

        // Intent: Resolves one relative synchronized path beneath a fixed BepInEx root with full-path containment enforcement.
        private static string SafeBepInExRootPath(string rootPath, string relative, string label)
        {
            try
            {
                return AutoModSyncPathSafety.SafeUnderRoot(rootPath, relative, true);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Unsafe BepInEx\\" + label + " path: " + ex.Message, ex);
            }
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

        // Intent: Validates protocol SHA-256 text before it is trusted as a content identifier.
        private static bool IsSha256Hex(string value)
        {
            return AutoModSyncClientResourceSafety.IsSha256Hex(value);
        }

        // Intent: Derives the stable SHA-256 fingerprint shown/pinned for a server's public signing key.
        private static string Fingerprint(string publicXml)
        {
            using (SHA256 sha = SHA256.Create()) return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(publicXml ?? "")));
        }

        // Intent: Implements first-contact server trust.
        // Workflow: accepts an already-pinned fingerprint silently; otherwise shows a Windows confirmation dialog and persists the exact accepted fingerprint for future connections.
        // Intent: Returns whether this exact verified server fingerprint is already pinned locally.
        // First contact is handled separately by the non-blocking in-game trust prompt so network processing never pauses on a native modal dialog.
        private static bool IsServerTrusted(string fingerprint)
        {
            if (String.IsNullOrEmpty(fingerprint) || fingerprint.Length != 64) return false;
#if AMS_DEV_TESTS
            if (ConsumeDevelopmentForceTrustPromptMarker()) return false;
#endif
            string trusted = Path.Combine(GetAutoModSyncRoot(), "trusted-servers.txt");
            try
            {
                if (!File.Exists(trusted)) return false;
                string[] lines = File.ReadAllLines(trusted);
                int i;
                for (i = 0; i < lines.Length; i++)
                    if (String.Equals(lines[i].Trim(), fingerprint, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Could not read AutoModSync server trust: " + ex.Message);
                return false;
            }
        }

        // Intent: Starts explicit first-contact trust without blocking Unity's main/network loop.
        // Binding: the prompt stores the exact active ZRpc plus the already signature-verified manifest; acceptance is ignored if that session is no longer current/alive.
        private static void BeginServerTrustPrompt(ZRpc rpc, string fingerprint, string manifest)
        {
            if (rpc == null || rpc != _pendingRpc || !_serverRecognized || String.IsNullOrEmpty(fingerprint) || fingerprint.Length != 64)
                throw new InvalidDataException("AutoModSync could not bind the server trust prompt to the active verified session.");

            _trustPromptRpc = rpc;
            _trustPromptFingerprint = fingerprint;
            _trustPromptManifest = manifest ?? "";
            _trustPromptDecision = 0;
            int generation = ++_trustPromptGeneration;
            _trustPromptPending = true;
            _uiState.SetServerFingerprint(fingerprint);
            ShowSyncOverlay(AutoModSyncUiPhase.Trust, "Trust this server?",
                "A Windows confirmation dialog is open. Choose Yes only if you intended to join this server.");

            StartNativeTrustPrompt(fingerprint, generation);

#if AMS_DEV_TESTS
            if (ConsumeDevelopmentDisconnectDuringTrustMarker())
            {
                _devTrustDisconnectGeneration = generation;
                _devTrustDisconnectUtc = DateTime.UtcNow.AddMilliseconds(1500.0);
                if (_instance != null)
                    _instance.Logger.LogInfo("AutoModSync DEV TEST armed protected-socket loss while the native trust dialog remains open.");
            }
#endif

            if (_instance != null)
                _instance.Logger.LogInfo("AutoModSync is waiting for first-contact trust confirmation for server fingerprint " + fingerprint + ".");
        }


        // Intent: Shows the first-contact trust decision without blocking Unity's main/network thread.
        // Cursor ownership stays entirely with Valheim; the native dialog receives normal Windows mouse input independently.
        private static void StartNativeTrustPrompt(string fingerprint, int generation)
        {
            string pretty = FormatFingerprint(fingerprint).Replace("\n", "\r\n");
            string message = "This Valheim server wants AutoModSync permission to install or update executable mod files on this PC.\r\n\r\n" +
                             "Server fingerprint:\r\n" + pretty + "\r\n\r\n" +
                             "Choose Yes only if you intended to join this server. You will only be asked again if its server identity changes.";

            Thread thread = new Thread(delegate()
            {
                int decision;
                int nativeThreadId = unchecked((int)GetCurrentThreadId());
                if (generation == _trustPromptGeneration)
                    _trustPromptNativeThreadId = nativeThreadId;
                try
                {
                    const uint MB_YESNO = 0x00000004u;
                    const uint MB_ICONWARNING = 0x00000030u;
                    const uint MB_DEFBUTTON2 = 0x00000100u;
                    const uint MB_SETFOREGROUND = 0x00010000u;
                    const uint MB_TOPMOST = 0x00040000u;
                    int answer = MessageBox(IntPtr.Zero, message, TrustPromptCaption,
                        MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2 | MB_SETFOREGROUND | MB_TOPMOST);
                    decision = answer == 6 ? 1 : 2;
                }
                catch
                {
                    decision = -1;
                }

                // A stale dialog result must never apply to a later connection.
                if (generation == _trustPromptGeneration)
                {
                    _trustPromptDecision = decision;
                }
#if AMS_DEV_TESTS
                else if (_instance != null)
                {
                    _instance.Logger.LogInfo("AutoModSync DEV TEST ignored stale native trust-dialog result from generation " +
                        generation.ToString(CultureInfo.InvariantCulture) + "; current generation=" +
                        _trustPromptGeneration.ToString(CultureInfo.InvariantCulture) + ".");
                }
#endif
                if (_trustPromptNativeThreadId == nativeThreadId)
                    _trustPromptNativeThreadId = 0;
                if (_staleTrustPromptThreadId == nativeThreadId)
                {
                    _staleTrustPromptThreadId = 0;
                    _staleTrustPromptDismissUntilUtc = DateTime.MinValue;
                    _staleTrustPromptNextDismissUtc = DateTime.MinValue;
                }
            });

            thread.IsBackground = true;
            try { thread.SetApartmentState(ApartmentState.STA); } catch { }
            thread.Start();
        }

        // Intent: Best-effort dismissal when a protected connection dies while the native trust dialog is still open.
        // Reliability: MB_YESNO has no Cancel result, so WM_CLOSE can be ignored; after generation invalidation we post the dialog's IDNO command to the exact prompt thread and retry briefly for late window creation.
        private static bool CloseNativeTrustPromptWindow(int nativeThreadId, bool allowCaptionFallback)
        {
            const uint WM_COMMAND = 0x0111u;
            const int IDNO = 7;
            bool found = false;
            try
            {
                if (nativeThreadId != 0)
                {
                    EnumThreadWindows(unchecked((uint)nativeThreadId), delegate(IntPtr hwnd, IntPtr lParam)
                    {
                        found = true;
                        try { PostMessage(hwnd, WM_COMMAND, new IntPtr(IDNO), IntPtr.Zero); } catch { }
                        return true;
                    }, IntPtr.Zero);
                }

                if (!found && allowCaptionFallback)
                {
                    IntPtr hwnd = FindWindow(null, TrustPromptCaption);
                    if (hwnd != IntPtr.Zero)
                    {
                        found = true;
                        PostMessage(hwnd, WM_COMMAND, new IntPtr(IDNO), IntPtr.Zero);
                    }
                }
            }
            catch { }
            return found;
        }

        // Intent: Persists an explicitly accepted fingerprint and resumes only the same still-connected, signature-verified AMS session that opened the prompt.
        private static void AcceptPendingServerTrust()
        {
            if (!_trustPromptPending) return;

            ZRpc rpc = _trustPromptRpc;
            string fingerprint = _trustPromptFingerprint;
            string manifest = _trustPromptManifest;

            if (rpc == null || rpc != _pendingRpc || !_serverRecognized || !IsRpcConnected(rpc))
            {
                HandleRecognizedConnectionLoss("AutoModSync connection ended before server trust was accepted.");
                return;
            }

            try
            {
                string root = GetAutoModSyncRoot();
                string trusted = Path.Combine(root, "trusted-servers.txt");
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);

                // Recheck before appending so repeated GUI events cannot duplicate an existing pin.
                if (!IsServerTrusted(fingerprint))
                    File.AppendAllText(trusted, fingerprint.ToLowerInvariant() + Environment.NewLine, new UTF8Encoding(false));

                ClearPendingTrustPrompt();
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync trusted server fingerprint " + fingerprint + ".");
                ContinueVerifiedManifest(manifest);
            }
            catch (Exception ex)
            {
                ClearPendingTrustPrompt();
                AbortAutoModSyncJoin("Could not save AutoModSync server trust: " + ex.Message);
            }
        }

        // Intent: Rejects first-contact trust explicitly; the recognized AMS join is closed rather than falling through to an unsynchronized normal handshake.
        private static void RejectPendingServerTrust()
        {
            if (!_trustPromptPending) return;
            ClearPendingTrustPrompt();
            AbortAutoModSyncJoin("AutoModSync server identity was not trusted by the user.");
        }

        // Intent: Clears only the transient first-contact decision state and invalidates any outstanding native-dialog result.
        // A best-effort WM_CLOSE also removes a still-open prompt when the protected connection ends first.
        private static void ClearPendingTrustPrompt()
        {
            bool hadPrompt = _trustPromptPending;
            int nativeThreadId = _trustPromptNativeThreadId;

            _trustPromptPending = false;
            _trustPromptFingerprint = "";
            _trustPromptManifest = "";
            _trustPromptRpc = null;
            _trustPromptDecision = 0;
            _trustPromptGeneration++;
            _trustPromptNativeThreadId = 0;
#if AMS_DEV_TESTS
            _devTrustDisconnectGeneration = 0;
            _devTrustDisconnectUtc = DateTime.MinValue;
#endif

            if (hadPrompt)
            {
                _staleTrustPromptThreadId = nativeThreadId;
                _staleTrustPromptDismissUntilUtc = DateTime.UtcNow.AddSeconds(3.0);
                _staleTrustPromptNextDismissUtc = DateTime.MinValue;
                bool found = CloseNativeTrustPromptWindow(nativeThreadId, true);
                if (_instance != null)
                    _instance.Logger.LogInfo("AutoModSync invalidated the native trust prompt and requested dismissal" +
                        (found ? "." : "; the client will retry briefly in case the native window is still materializing."));
            }
        }

        // Intent: Produces a readable two-line fingerprint without changing the exact 64-hex value that is pinned and compared.
        private static string FormatFingerprint(string fingerprint)
        {
            if (String.IsNullOrEmpty(fingerprint) || fingerprint.Length != 64) return fingerprint ?? "";
            return fingerprint.Substring(0, 8) + "-" + fingerprint.Substring(8, 8) + "-" + fingerprint.Substring(16, 8) + "-" + fingerprint.Substring(24, 8) + "\n" +
                   fingerprint.Substring(32, 8) + "-" + fingerprint.Substring(40, 8) + "-" + fingerprint.Substring(48, 8) + "-" + fingerprint.Substring(56, 8);
        }

        // Intent: Checks the active ZRpc directly so a connection lost during trust/package preparation can clear UI/state immediately.
        private static bool IsRpcConnected(ZRpc rpc)
        {
            try { return rpc != null && rpc.IsConnected(); }
            catch { return false; }
        }

        // Intent: Clears a positively recognized AMS session when its transport dies before synchronization completes.
        // Security: this is fail-closed; it never replays the held vanilla handshake, and it removes stale trust/download UI instead of leaving an actionable prompt for a dead connection.
        private static void HandleRecognizedConnectionLoss(string reason)
        {
            CloseBundleStream();

            bool preservedResume = _serverSupportsBundleResume && _bundleResumeSlotActive && _bundleNextChunk > 0;
            long preservedBytes = preservedResume ? _bundleBytesReceived : 0L;
            if (!preservedResume) DeleteActiveBundleFile();
            ClearPendingTrustPrompt();

            _waitingForServer = false;
            _serverRecognized = false;
            _serverAcknowledged = false;
            _serverSupportsBundleWindow = false;
            _serverSupportsBundleBatch = false;
            _serverSupportsBundlePipeline = false;
            _serverSupportsBundleResume = false;
            _serverSupportsBundleScheduler = false;
            _preflightGateActive = false;
            _serverHandshakeHeld = false;
            _heldServerHandshakeParameters = new object[0];
            _pendingRpc = null;
            _helloSentUtc = DateTime.MinValue;
            _lastHelloAttemptUtc = DateTime.MinValue;
            _helloAttemptCount = 0;

            ResetManifestState();
            ShowTransientSyncOverlay(AutoModSyncUiPhase.Failed, "Synchronization interrupted.",
                preservedResume
                    ? "Connection lost. " + FormatBytes(preservedBytes) + " of verified package data was retained and can resume on the next join."
                    : "Connection lost before synchronization completed. Reconnect to try again.",
                5.0);

            if (_instance != null)
            {
                if (preservedResume)
                    _instance.Logger.LogWarning((reason ?? "AutoModSync connection ended.") + " Preserved " + FormatBytes(preservedBytes) + " of the verified bundle prefix; reconnect to resume after server prefix verification.");
                else
                    _instance.Logger.LogWarning((reason ?? "AutoModSync connection ended.") + " The protected join was discarded; reconnect to try again.");
            }
        }

        // Intent: Returns the capability string this connection should advertise; development legacy-client emulation deliberately omits 2.6 resume/scheduler tokens.
        private static string GetCurrentClientCapabilities()
        {
#if AMS_DEV_TESTS
            if (_devEmulateLegacyClient) return "roots1";
#endif
            return ClientCapabilities;
        }

#if AMS_DEV_TESTS
        // Intent: Consumes a one-shot marker that previews every Phase 7 presentation state at the main menu without opening a network connection or changing files.
        // Scope: development builds only; release binaries do not contain the preview path.
        private static void TryStartDevelopmentUiPreview()
        {
            try
            {
                string marker = Path.Combine(GetAutoModSyncRoot(), "phase7-test-ui-preview.once");
                if (!File.Exists(marker)) return;
                try { File.Delete(marker); } catch { }
                _devUiPreviewActive = true;
                _devUiPreviewIndex = 0;
                _devUiPreviewNextUtc = DateTime.MinValue;
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync DEV TEST starting Phase 7 branded UI preview.");
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV Phase 7 UI preview marker could not be consumed: " + ex.Message);
            }
        }

        // Intent: Advances the development-only branded UI preview through deterministic trust/comparison/queue/transfer/verification/restart states.
        private static void UpdateDevelopmentUiPreview()
        {
            DateTime now = DateTime.UtcNow;
            if (_devUiPreviewNextUtc != DateTime.MinValue && now < _devUiPreviewNextUtc) return;

            if (_devUiPreviewIndex >= 10)
            {
                _devUiPreviewActive = false;
                _devUiPreviewNextUtc = DateTime.MinValue;
                HideSyncOverlay();
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync DEV TEST Phase 7 branded UI preview completed.");
                return;
            }

            ShowDevelopmentUiPreviewStage(_devUiPreviewIndex, now);
            _devUiPreviewIndex++;
            _devUiPreviewNextUtc = now.AddSeconds(3.5);
        }

        // Intent: Seeds one deterministic presentation-only Phase 7 snapshot so the maintainer can visually inspect the renderer without mutating synchronization policy.
        private static void ShowDevelopmentUiPreviewStage(int stage, DateTime nowUtc)
        {
            const string fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            const long mib = 1024L * 1024L;

            _uiState.Reset();
            _uiState.SetServerFingerprint(fingerprint);
            _uiState.SetComparison(63, 61, 2, 1, 348L * mib);
            _overlayVisible = true;
            _overlayHideUtc = DateTime.MinValue;

            if (stage == 0)
            {
                ShowSyncOverlay(AutoModSyncUiPhase.Trust, "Trust this server?",
                    "Preview: the real trust state appears while the native Windows Yes/No confirmation remains open.");
            }
            else if (stage == 1)
            {
                ShowSyncOverlay(AutoModSyncUiPhase.Comparing, "Comparing server mods...",
                    "Preview: signed manifest verified. Comparing required files with this client.");
            }
            else if (stage == 2)
            {
                _uiState.SetQueue(3, 4, 4);
                ShowSyncOverlay(AutoModSyncUiPhase.Queued, "Queued for synchronization...",
                    "Preview: the server is limiting simultaneous fresh-client transfers. Your place is reserved.");
            }
            else if (stage == 3)
            {
                _uiState.BeginTransfer(313L * mib + (410L * 1024L), 96L * mib, nowUtc.AddSeconds(-6.0));
                _uiState.UpdateTransfer(188L * mib, nowUtc);
                ShowSyncOverlay(AutoModSyncUiPhase.Downloading, "Resuming required mods...",
                    "Preview: verified package data was retained from an interrupted transfer.");
            }
            else if (stage == 4)
            {
                _uiState.BeginVerification(63);
                int i;
                for (i = 0; i < 37; i++) _uiState.MarkVerified();
                ShowSyncOverlay(AutoModSyncUiPhase.Verifying, "Verifying synchronized files...",
                    "Preview: checking extracted file sizes and SHA-256 hashes before anything can be applied.");
            }
            else if (stage == 5)
            {
                ShowSyncOverlay(AutoModSyncUiPhase.Applying, "Preparing synchronized changes...",
                    "Preview: verified files are ready. Preparing a crash-safe apply transaction.");
            }
            else if (stage == 6)
            {
                ShowSyncOverlay(AutoModSyncUiPhase.Restarting, "Sync complete. Restarting Valheim...",
                    "Preview: verified changes will be applied out-of-process before Valheim relaunches.");
            }
            else if (stage == 7)
            {
                _uiState.SetComparison(0, 0, 0, 0, 0L);
                ShowSyncOverlay(AutoModSyncUiPhase.Reconnecting, "Reconnecting to synchronized server...",
                    "Preview: Valheim restarted successfully and AutoModSync is restoring the saved join.");
            }
            else if (stage == 8)
            {
                ShowSyncOverlay(AutoModSyncUiPhase.Complete, "Already synchronized.",
                    "Preview: required mods match this trusted server. Joining normally...");
            }
            else
            {
                ShowSyncOverlay(AutoModSyncUiPhase.Failed, "AutoModSync blocked this join.",
                    "Preview: a protected synchronization failure is shown briefly without releasing the held vanilla handshake.");
            }
        }
#endif

#if AMS_DEV_TESTS
        // Intent: Forces exactly one verified server identity through first-contact trust UI without altering the existing trust store.
        // Scope: used only to validate that explicit user rejection remains fail-closed on an otherwise already-pinned development server.
        private static bool ConsumeDevelopmentForceTrustPromptMarker()
        {
            try
            {
                string marker = Path.Combine(GetAutoModSyncRoot(), "phase1-test-force-trust-prompt.once");
                if (!File.Exists(marker)) return false;
                try { File.Delete(marker); } catch { }
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync DEV FAIL-CLOSED forcing first-contact trust prompt for this verified session.");
                return true;
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV trust-prompt marker could not be consumed: " + ex.Message);
                return false;
            }
        }

        // Intent: Forces one recognized connection to lose its transport while the native first-contact trust dialog is still pending.
        // Scope: development-only live validation of stale-dialog dismissal and generation-bound result rejection.
        private static bool ConsumeDevelopmentDisconnectDuringTrustMarker()
        {
            try
            {
                string marker = Path.Combine(GetAutoModSyncRoot(), "phase3-test-disconnect-during-trust.once");
                if (!File.Exists(marker)) return false;
                try { File.Delete(marker); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV trust-disconnect marker could not be consumed: " + ex.Message);
                return false;
            }
        }

        // Intent: Forces one apply/restart preparation failure after a bundle has been verified/extracted but before durable pending state exists.
        // Safety: the test leaves live BepInEx files untouched and exercises the production fail-closed abort path.
        private static bool ConsumeDevelopmentApplyPreparationFailureMarker()
        {
            try
            {
                string marker = Path.Combine(GetAutoModSyncRoot(), "phase1-test-fail-apply-prep.once");
                if (!File.Exists(marker)) return false;
                try { File.Delete(marker); } catch { }
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync DEV FAIL-CLOSED forcing apply/restart preparation failure before pending state.");
                return true;
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV apply-preparation marker could not be consumed: " + ex.Message);
                return false;
            }
        }

        // Intent: Stops one development cache-validation connection after the production bundle header has been validated.
        // Scope: avoids transferring the full prewarmed payload when the live test only needs to prove cache acquisition/retention.
        private static bool ConsumeDevelopmentPhase3StopAfterBundleHeaderMarker()
        {
            try
            {
                string marker = Path.Combine(GetAutoModSyncRoot(), "phase3-test-stop-after-bundle-header.once");
                if (!File.Exists(marker)) return false;
                try { File.Delete(marker); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV Phase 3 stop-after-header marker could not be consumed: " + ex.Message);
                return false;
            }
        }

        // Intent: Forces one trusted-manifest comparison to request the same nearly-bare-client file set used by dedicated-server startup prewarm.
        // Scope: existing live files are only treated as missing for request construction; the installed AutoModSync client DLL is excluded so the request key matches the startup baseline exactly.
        private static bool ConsumeDevelopmentNearlyBareClientMarker()
        {
            try
            {
                string marker = Path.Combine(GetAutoModSyncRoot(), "phase3-test-emulate-nearly-bare.once");
                if (!File.Exists(marker)) return false;
                try { File.Delete(marker); } catch { }
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync DEV TEST emulating a nearly-bare client for Phase 3 startup-prewarm validation; all signed files except the installed client DLL will be requested.");
                return true;
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV nearly-bare marker could not be consumed: " + ex.Message);
                return false;
            }
        }

        // Intent: Consumes a one-shot marker that makes the current connection behave like a pre-resume AMS4/2.5 client.
        // Scope: it keeps roots1 and the existing AMS4 protocol, omits bundle-resume1/bundle-scheduler1 from Hello, and ignores those server advertisements.
        private static bool ConsumeDevelopmentLegacyClientMarker()
        {
            try
            {
                string marker = Path.Combine(GetAutoModSyncRoot(), "resume-test-emulate-legacy-client.once");
                if (!File.Exists(marker)) return false;
                try { File.Delete(marker); } catch { }
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync DEV TEST armed: emulate pre-resume AMS4 client for this connection.");
                return true;
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV legacy-client marker could not be consumed: " + ex.Message);
                return false;
            }
        }
#endif

#if AMS_DEV_TESTS
        // Intent: One-shot single-client interruption emulator for Phase 4. The marker contains the completed chunk count at which the active socket is forcibly closed.
        // Safety: development builds consume the marker before transfer; release builds do not compile this code.
        private static int ReadDevelopmentResumeDisconnectMarker(int totalChunks)
        {
            try
            {
                string marker = Path.Combine(GetAutoModSyncRoot(), "resume-test-disconnect-after-chunks.once");
                if (!File.Exists(marker)) return 0;
                string raw = "";
                try { raw = File.ReadAllText(marker).Trim(); }
                finally { try { File.Delete(marker); } catch { } }

                int count;
                if (!Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1 || count >= totalChunks)
                {
                    if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV resume interruption marker must be between 1 and one less than the bundle chunk count.");
                    return 0;
                }

                if (_instance != null) _instance.Logger.LogInfo("AutoModSync DEV resume interruption armed after chunk " + count.ToString(CultureInfo.InvariantCulture) + ".");
                return count;
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV resume interruption marker could not be read: " + ex.Message);
                return 0;
            }
        }

        // Intent: Forces a real transport interruption after a deterministic complete-chunk boundary so one client can validate reconnect/resume without a second tester.
        private static bool DevelopmentDisconnectForResumeIfArmed(ZRpc rpc)
        {
            if (_devDisconnectAfterChunk <= 0 || _bundleNextChunk < _devDisconnectAfterChunk) return false;
            int boundary = _devDisconnectAfterChunk;
            _devDisconnectAfterChunk = 0;
            try { if (_bundleStream != null) _bundleStream.Flush(); } catch { }
            if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV TEST closing the transfer socket after chunk " + boundary.ToString(CultureInfo.InvariantCulture) + " to emulate an interrupted download.");
            try
            {
                if (rpc != null && rpc.GetSocket() != null) rpc.GetSocket().Close();
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync DEV TEST could not close the transfer socket: " + ex.Message);
            }
            return true;
        }
#endif

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        // Intent: Native trust prompt shown from a background thread so Unity networking continues while the user decides.
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        // Intent: Locates the transient native trust prompt as a fallback when its owning native thread has not been recorded yet.
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        // Intent: Callback signature used by EnumThreadWindows while targeting the exact native thread that owns a stale trust prompt.
        private delegate bool EnumThreadWindowsCallback(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        // Intent: Enumerates top-level windows owned by the exact background thread that created the current native trust prompt.
        private static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadWindowsCallback lpfn, IntPtr lParam);

        [DllImport("kernel32.dll")]
        // Intent: Captures the native Windows thread id of the background trust-prompt thread for precise stale-dialog dismissal.
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        // Intent: Posts a non-blocking native dialog command to a stale trust prompt from the Unity thread.
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

        // Intent: Detects unfinished helper-owned apply state without touching any live synchronized destination from the running game.
        // Transaction safety: pending.txt means verified staging still needs an apply; apply-transaction means a helper journal may require rollback or committed cleanup.
        private static bool HasPendingApplyRecovery()
        {
            string amsRoot = GetAutoModSyncRoot();
            string pending = Path.Combine(amsRoot, "pending.txt");
            string transaction = Path.Combine(amsRoot, "apply-transaction");
            return File.Exists(pending) || Directory.Exists(transaction);
        }

        // Intent: Immediately hands interrupted transaction recovery back to the external helper, then exits this mixed/uncertain process without joining any server.
        // Workflow: the helper waits for this PID to exit, resolves PREPARED as rollback/retry or COMMITTED as cleanup, preserves reconnect state when safe, and owns the next relaunch.
        private static void ScheduleRecoveredStagingRestart()
        {
            string amsRoot = GetAutoModSyncRoot();
            string helper = FindApplyHelper(amsRoot);
            if (!File.Exists(helper)) throw new FileNotFoundException("AutoModSync apply helper is missing during staged-file recovery.", helper);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = helper;
            psi.Arguments = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + " \"" + amsRoot.Replace("\"", "") + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);

            _restartRequested = true;
            _quitAfterUtc = DateTime.UtcNow.AddMilliseconds(900.0);
            if (_instance != null) _instance.Logger.LogWarning("AutoModSync detected unfinished transactional apply state; handing recovery to the external helper and restarting before any server join.");
        }

    }
}
