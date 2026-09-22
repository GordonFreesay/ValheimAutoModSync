using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;

[assembly: AssemblyTitle("Valheim AutoModSync Server")]
[assembly: AssemblyDescription("Server-side signed manifest and synchronized BepInEx plugin transfer component.")]
[assembly: AssemblyCompany("GordonFreesay")]
[assembly: AssemblyProduct("Valheim AutoModSync")]
[assembly: AssemblyVersion("2.6.0.0")]
[assembly: AssemblyFileVersion("2.6.0.0")]

namespace ValheimAutoModSync
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ServerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.gordonfreesay.valheimautomodsync.server";
        public const string PluginName = "Valheim AutoModSync Server";
        public const string PluginVersion = "2.6.0";
        public const int ProtocolVersion = 4;

        internal const string RpcHello = "AMS4_Hello";
        internal const string RpcAck = "AMS4_Ack";
        internal const string RpcManifestBegin = "AMS4_ManifestBegin";
        internal const string RpcManifestChunk = "AMS4_ManifestChunk";
        internal const string RpcManifestEnd = "AMS4_ManifestEnd";
        internal const string RpcGetBundle = "AMS4_GetBundle";
        internal const string RpcGetBundleChunk = "AMS4_GetBundleChunk";
        internal const string RpcGetBundleBatch = "AMS4_GetBundleBatch";
        internal const string RpcBundleBegin = "AMS4_BundleBegin";
        internal const string RpcBundleChunk = "AMS4_BundleChunk";
        internal const string RpcBundleBatch = "AMS4_BundleBatch";
        internal const string RpcBundleEnd = "AMS4_BundleEnd";
        internal const string RpcError = "AMS4_Error";

        private static ServerPlugin _instance;
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<string> _excludePatterns;
        private static ConfigEntry<string> _serverOnlyPatterns;
        private static ConfigEntry<string> _clientRequiredPatterns;
        private static ConfigEntry<string> _syncConfigPatterns;
        private static ConfigEntry<bool> _syncPatchers;
        private static ConfigEntry<int> _manifestCacheSeconds;
        private static ConfigEntry<int> _chunkBytes;
        private static ConfigEntry<int> _maxFileMiB;
        private static ConfigEntry<int> _maxBundleMiB;
        private static ConfigEntry<int> _maxExpandedBundleMiB;
        private static ConfigEntry<int> _bundleCacheSeconds;
        private static ConfigEntry<int> _bundleCacheMaxMiB;
        private static ConfigEntry<int> _transferSendRateMax;
        private static ConfigEntry<int> _transferSendRateMin;
        private static ConfigEntry<int> _transferSendBufferBytes;

        private static readonly object ManifestLock = new object();
        private static readonly HashSet<ZRpc> Registered = new HashSet<ZRpc>();
        private static DateTime _manifestBuiltUtc = DateTime.MinValue;
        private static string _manifestText = "";
        private static string _manifestSignature = "";
        private static string _publicKeyXml = "";
        private static string _publicFingerprint = "";
        private static RSACryptoServiceProvider _signer;
        private static Dictionary<string, FileRecord> _files = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<ZRpc, string> ClientVersions = new Dictionary<ZRpc, string>();
        private static readonly Dictionary<ZRpc, string> ClientCapabilities = new Dictionary<ZRpc, string>();
        private static readonly Dictionary<ZRpc, BundleTransfer> BundleTransfers = new Dictionary<ZRpc, BundleTransfer>();

        // Phase 3 bundle cache: published ZIPs are immutable and reference-counted while clients read them.
        // BundleBuilds serializes only identical cache keys so simultaneous fresh clients share one build instead of recompressing the same bytes.
        private static readonly object BundleCacheLock = new object();
        private static readonly Dictionary<string, BundleArtifact> BundleArtifactCache = new Dictionary<string, BundleArtifact>(StringComparer.Ordinal);
        private static readonly Dictionary<string, BundleBuildState> BundleBuilds = new Dictionary<string, BundleBuildState>(StringComparer.Ordinal);
        
        private sealed class FileRecord
        {
            public string RelativePath;
            public string FullPath;
            public long Size;
            public string Sha256;
            public char Kind;
        }

        private sealed class BundleArtifact
        {
            public string CacheKey;
            public string ZipPath;
            public long Size;
            public string Sha256;
            public int FileCount;
            public long ExpandedBytes;
            public DateTime CreatedUtc;
            public DateTime LastUsedUtc;
            public int ActiveTransfers;
            public double ZipBuildSeconds;
            public double ZipHashSeconds;
        }

        private sealed class BundleBuildState
        {
            public bool Complete;
            public Exception Error;
        }

        private sealed class BundleTransfer
        {
            public BundleArtifact Artifact;
            public string ZipPath;
            public long Size;
            public string Sha256;
            public int ChunkBytes;
            public int TotalChunks;
            public int FileCount;
            public uint SteamConnectionHandle;
            public int OriginalSendRateMax;
            public int OriginalSendRateMin;
            public int OriginalSendBufferBytes;
            public bool ChangedSendRateMax;
            public bool ChangedSendRateMin;
            public bool ChangedSendBuffer;
            public DateTime LastSteamTelemetryUtc;
        }

        // Intent: BepInEx server entry point; binds server/transfer limits, loads the signing identity, clears stale cache files, and installs only the server-side connection hooks.
        // The server uses Valheim's existing ZRpc connection and never opens a separate synchronization listener.
        private void Awake()
        {
            _instance = this;
            _enabled = Config.Bind("General", "Enabled", true, "Enable the AutoModSync server role on this Valheim instance.");
            _excludePatterns = Config.Bind("General", "ExcludePatterns",
                "ValheimAutoModSync.Server.dll;ValheimAutoModSync.Client.dll;ValheimAutoModSync.Apply.exe;manifest.json;icon.png;README.md;CHANGELOG.md;LICENSE;THIRD-PARTY-NOTICES.md;*.pdb;*.mdb;*.log;*.tmp;*.bak;*.md",
                "Semicolon-separated wildcard patterns that will not be sent to clients from any synchronized root. Match is checked against both the relative path and file name.");
            _serverOnlyPatterns = Config.Bind("Compatibility", "ServerOnlyPatterns", "",
                "Semicolon-separated plugin/patcher wildcard patterns that exist on the server but must never be copied to clients.");
            _clientRequiredPatterns = Config.Bind("Compatibility", "ClientRequiredPatterns", "",
                "Optional semicolon-separated plugin/patcher wildcard allowlist. Empty means every non-excluded, non-server-only plugin/patcher file is client-required. When set, only matching files are advertised.");
            _syncPatchers = Config.Bind("Compatibility", "SyncPatchers", true,
                "Synchronize BepInEx\\patchers recursively. Disable only if this server's patchers are known to be server-only; ServerOnlyPatterns can exclude individual patchers.");
            _syncConfigPatterns = Config.Bind("Compatibility", "SyncConfigPatterns", "",
                "Explicit semicolon-separated allowlist for BepInEx\\config files that clients must receive. Empty disables config synchronization. Never use '*' unless every server config is intentionally client-safe.");
            _manifestCacheSeconds = Config.Bind("General", "ManifestCacheSeconds", 5, "How long the server caches synchronized-root hashes before rescanning.");
            _chunkBytes = Config.Bind("Transfer", "ChunkBytes", 24576, "Raw file bytes per RPC chunk before Base64 encoding. 24576 is conservative for Valheim's RPC transport.");
            _maxFileMiB = Config.Bind("Transfer", "MaxFileMiB", 128, "Refuse to transfer a single file larger than this many MiB.");
            _maxBundleMiB = Config.Bind("Transfer", "MaxBundleMiB", 2048, "Refuse to build a compressed change package larger than this many MiB.");
            _maxExpandedBundleMiB = Config.Bind("Transfer", "MaxExpandedBundleMiB", 4096, "Refuse a requested change set whose signed source files exceed this many MiB before compression.");
            _bundleCacheSeconds = Config.Bind("Transfer", "BundleCacheSeconds", 600, "How long a completed immutable bundle remains reusable after its last client use. 0 keeps artifacts only while actively referenced.");
            _bundleCacheMaxMiB = Config.Bind("Transfer", "BundleCacheMaxMiB", 4096, "Maximum total on-disk size of retained completed bundle artifacts. Active transfers are never deleted; idle least-recently-used artifacts are evicted to meet this budget.");
            _transferSendRateMax = Config.Bind("Transfer", "SendRateMaxBytesPerSec", 67108864, "Temporary per-connection Steam send-rate ceiling used only while sending an AutoModSync bundle.");
            _transferSendRateMin = Config.Bind("Transfer", "SendRateMinBytesPerSec", 16777216, "Temporary per-connection Steam send-rate floor used only during an AutoModSync bundle. Steam's estimator can remain pinned to this floor for the entire short preflight transfer, so this value materially affects observed sync speed. Set 0 to leave the minimum unchanged.");
            _transferSendBufferBytes = Config.Bind("Transfer", "SendBufferBytes", 33554432, "Temporary per-connection Steam reliable send-buffer target used only during an AutoModSync bundle. Set 0 to leave the buffer unchanged.");

            try
            {
                LoadIdentity();
                CleanupOldBundleCache();
                Logger.LogInfo("AutoModSync uses Valheim's existing ZRpc connection; no additional listening port is opened.");
                Logger.LogInfo("AutoModSync server fingerprint: " + _publicFingerprint);
                new Harmony(PluginGuid).PatchAll(typeof(NetworkPatches));
                Logger.LogInfo("AutoModSync early connection hooks installed.");
                try { UpgradeDevelopmentTransferDefaults(); }
                catch (Exception migrateEx) { Logger.LogWarning("AutoModSync could not migrate development transfer defaults; continuing with existing values: " + migrateEx.Message); }
            }
            catch (Exception ex)
            {
                Logger.LogError("AutoModSync startup failed: " + ex);
            }
        }

        // Intent: Loads or creates the persistent RSA server identity used to sign manifests.
        // Workflow: generates a 2048-bit keypair only when absent, writes the public key, and derives the fingerprint clients pin on first trust.
        private static void LoadIdentity()
        {
            string path = Path.Combine(Paths.ConfigPath, "ValheimAutoModSync.private.xml");
            string publicPath = Path.Combine(Paths.ConfigPath, "ValheimAutoModSync.public.xml");

            if (!File.Exists(path))
            {
                string parent = Path.GetDirectoryName(path);
                if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);

                using (RSACryptoServiceProvider generated = new RSACryptoServiceProvider(2048))
                {
                    generated.PersistKeyInCsp = false;
                    string privateXml = generated.ToXmlString(true);
                    string generatedPublicXml = generated.ToXmlString(false);
                    File.WriteAllText(path, privateXml + Environment.NewLine, new UTF8Encoding(false));
                    File.WriteAllText(publicPath, generatedPublicXml + Environment.NewLine, new UTF8Encoding(false));
                }

                if (_instance != null) _instance.Logger.LogInfo("Generated a new AutoModSync server signing identity at " + path + ". Back up this private identity to preserve the server fingerprint.");
            }

            string xml = File.ReadAllText(path).Trim();
            RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048);
            rsa.PersistKeyInCsp = false;
            rsa.FromXmlString(xml);
            _signer = rsa;
            _publicKeyXml = rsa.ToXmlString(false);
            if (!File.Exists(publicPath))
                File.WriteAllText(publicPath, _publicKeyXml + Environment.NewLine, new UTF8Encoding(false));
            using (SHA256 sha = SHA256.Create()) _publicFingerprint = ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(_publicKeyXml)));
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class NetworkPatches
        {
            // Intent: Registers AutoModSync RPC handlers as early as possible for each incoming server-side ZNet connection.
            private static void Prefix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance == null || !__instance.IsServer()) return;
                if (_enabled == null || !_enabled.Value || peer == null || peer.m_rpc == null) return;
                RegisterRpc(peer.m_rpc);
            }

            // Intent: Repeats idempotent RPC registration after Valheim's OnNewConnection body, covering game-version/order differences without double-registering.
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance == null || !__instance.IsServer()) return;
                if (_enabled == null || !_enabled.Value || peer == null || peer.m_rpc == null) return;
                RegisterRpc(peer.m_rpc);
            }
        }

        // Intent: Migrates only the exact earlier development-default tuples to the current 2.6 transfer baseline.
        // Evidence: a 313.4 MiB fresh-client run stayed pinned to the configured 8 MiB/s Steam floor, so the next development baseline tests 16/64/32.
        // Scope: administrator-customized values are preserved unless they exactly equal one of the known prior development defaults.
        private static void UpgradeDevelopmentTransferDefaults()
        {
            if (_transferSendRateMin == null || _transferSendRateMax == null || _transferSendBufferBytes == null) return;

            bool old25Defaults = _transferSendRateMin.Value == 1048576 &&
                                 _transferSendRateMax.Value == 8388608 &&
                                 _transferSendBufferBytes.Value == 8388608;
            bool old26Defaults = _transferSendRateMin.Value == 8388608 &&
                                 _transferSendRateMax.Value == 33554432 &&
                                 _transferSendBufferBytes.Value == 16777216;

            if (old25Defaults || old26Defaults)
            {
                _transferSendRateMin.Value = 16777216;
                _transferSendRateMax.Value = 67108864;
                _transferSendBufferBytes.Value = 33554432;
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync upgraded known development transfer defaults to Min=16 MiB/s, Max=64 MiB/s, Buffer=32 MiB.");
            }
        }

        // Intent: Registers all AMS4 RPC names once on a peer's ZRpc.
        // The server handles hello/bundle requests and installs no-op receivers for response-only message names so protocol traffic is explicit and bounded.
        private static void RegisterRpc(ZRpc rpc)
        {
            if (rpc == null || Registered.Contains(rpc)) return;
            try
            {
                rpc.Register<ZPackage>(RpcHello, new Action<ZRpc, ZPackage>(RPC_Hello));
                rpc.Register<ZPackage>(RpcAck, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcGetBundle, new Action<ZRpc, ZPackage>(RPC_GetBundle));
                rpc.Register<ZPackage>(RpcGetBundleChunk, new Action<ZRpc, ZPackage>(RPC_GetBundleChunk));
                rpc.Register<ZPackage>(RpcGetBundleBatch, new Action<ZRpc, ZPackage>(RPC_GetBundleBatch));
                rpc.Register<ZPackage>(RpcManifestBegin, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcManifestChunk, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcManifestEnd, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcBundleBegin, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcBundleChunk, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcBundleBatch, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcBundleEnd, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcError, new Action<ZRpc, ZPackage>(RPC_NoOp));
                Registered.Add(rpc);
                if (_instance != null) _instance.Logger.LogDebug("AutoModSync registered AMS4 handlers on a new peer ZRpc.");
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Could not register AutoModSync RPCs for a peer: " + ex.Message);
            }
        }

        // Intent: Safe placeholder for protocol messages that are response-only from the server's perspective.
        private static void RPC_NoOp(ZRpc rpc, ZPackage pkg) { }

        // Intent: Handles the client's AMS4_Hello preflight probe.
        // Workflow: validates protocol, immediately sends the optional 2.5.0 acknowledgement, builds/uses the signed manifest, then streams its header and ordered text chunks before normal Valheim mod validation begins.
        private static void RPC_Hello(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (_instance != null) _instance.Logger.LogInfo("AutoModSync received AMS4 preflight hello.");
                int protocol = pkg.ReadInt();
                string clientVersion = "";
                string clientCapabilities = "";
                try { clientVersion = pkg.ReadString(); } catch { clientVersion = ""; }
                try { clientCapabilities = pkg.ReadString(); } catch { clientCapabilities = ""; }
                ClientVersions[rpc] = clientVersion ?? "";
                ClientCapabilities[rpc] = clientCapabilities ?? "";
                if (protocol != ProtocolVersion)
                {
                    SendError(rpc, "AutoModSync protocol mismatch. Server=" + ProtocolVersion + " Client=" + protocol);
                    return;
                }

                ZPackage ack = new ZPackage();
                ack.Write(ProtocolVersion);
                ack.Write(PluginVersion);
                ack.Write("bundle-window1;bundle-batch1;bundle-pipeline1");
                rpc.Invoke(RpcAck, new object[] { ack });

                EnsureManifest(false);
                if (ManifestRequiresRootSync() && clientCapabilities.IndexOf("roots1", StringComparison.Ordinal) < 0)
                {
                    SendError(rpc, "This server requires AutoModSync root synchronization support (patchers/config). Update the AutoModSync client to 2.5.0 or newer.");
                    return;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(_manifestText);
                int partChars = 24000;
                int totalParts = Math.Max(1, (_manifestText.Length + partChars - 1) / partChars);

                ZPackage begin = new ZPackage();
                begin.Write(ProtocolVersion);
                begin.Write(totalParts);
                begin.Write(bytes.Length.ToString(CultureInfo.InvariantCulture));
                begin.Write(_publicKeyXml);
                begin.Write(_manifestSignature);
                begin.Write("bundle1");
                rpc.Invoke(RpcManifestBegin, new object[] { begin });

                int part;
                for (part = 0; part < totalParts; part++)
                {
                    int start = part * partChars;
                    int len = Math.Min(partChars, _manifestText.Length - start);
                    string text = len > 0 ? _manifestText.Substring(start, len) : "";
                    ZPackage chunk = new ZPackage();
                    chunk.Write(part);
                    chunk.Write(text);
                    rpc.Invoke(RpcManifestChunk, new object[] { chunk });
                }

                ZPackage end = new ZPackage();
                end.Write(totalParts);
                rpc.Invoke(RpcManifestEnd, new object[] { end });
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Manifest send failed: " + ex);
                SendError(rpc, "Server failed to create the AutoModSync manifest.");
            }
        }

        // Intent: Builds a compressed package containing exactly the current signed manifest records requested by this client.
        // Security: every request resolves to a fixed P/R/C manifest destination; duplicate/count/expanded/compressed limits are enforced before or during construction.
        private static void RPC_GetBundle(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                int count = pkg.ReadInt();
                if (count < 1 || count > 4096) throw new InvalidDataException("Invalid AutoModSync bundle request size.");

                EnsureManifest(false);
                List<FileRecord> records = new List<FileRecord>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long maxExpandedBytes = (long)Math.Max(1, _maxExpandedBundleMiB.Value) * 1024L * 1024L;
                long expandedBytes = 0L;
                int i;
                for (i = 0; i < count; i++)
                {
                    string requested = pkg.ReadString();
                    FileRecord record = ResolveBundleRecord(requested);
                    string key = record.Kind + ":" + record.RelativePath;
                    if (!seen.Add(key)) throw new InvalidDataException("Duplicate file in AutoModSync bundle request.");
                    if (record.Size < 0 || record.Size > maxExpandedBytes - expandedBytes)
                        throw new InvalidDataException("Requested AutoModSync content exceeds the configured expanded transfer limit.");
                    expandedBytes += record.Size;
                    records.Add(record);
                }

                // Canonical order makes the cache key and ZIP bytes independent of client request ordering.
                records.Sort(delegate(FileRecord a, FileRecord b)
                {
                    int byKind = a.Kind.CompareTo(b.Kind);
                    return byKind != 0 ? byKind : StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
                });

                CleanupBundle(rpc);

                long maxBundleBytes = (long)Math.Max(1, _maxBundleMiB.Value) * 1024L * 1024L;
                string cacheStatus;
                double waitSeconds;
                Stopwatch prepareWatch = Stopwatch.StartNew();
                BundleArtifact artifact = AcquireBundleArtifact(records, expandedBytes, maxBundleBytes, out cacheStatus, out waitSeconds);
                prepareWatch.Stop();

                int rawChunk = Math.Max(4096, Math.Min(49152, _chunkBytes.Value));
                BundleTransfer transfer = new BundleTransfer();
                transfer.Artifact = artifact;
                transfer.ZipPath = artifact.ZipPath;
                transfer.Size = artifact.Size;
                transfer.Sha256 = artifact.Sha256;
                transfer.ChunkBytes = rawChunk;
                transfer.TotalChunks = (int)((transfer.Size + rawChunk - 1L) / rawChunk);
                transfer.FileCount = artifact.FileCount;

                // Store the transfer before transport tuning/RPC publication so every later failure path releases the artifact reference.
                BundleTransfers[rpc] = transfer;
                TryTuneTransferTransport(rpc, transfer);

                ZPackage begin = new ZPackage();
                begin.Write(transfer.Size.ToString(CultureInfo.InvariantCulture));
                begin.Write(transfer.Sha256);
                begin.Write(transfer.TotalChunks);
                begin.Write(transfer.FileCount);
                rpc.Invoke(RpcBundleBegin, new object[] { begin });

                if (_instance != null)
                {
                    _instance.Logger.LogInfo("AutoModSync bundle ready: cache=" + cacheStatus +
                        ", key=" + ShortCacheKey(artifact.CacheKey) +
                        ", files=" + artifact.FileCount.ToString(CultureInfo.InvariantCulture) +
                        ", compressed=" + FormatBytes(artifact.Size) +
                        ", expanded=" + FormatBytes(artifact.ExpandedBytes) +
                        ", prepare=" + prepareWatch.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s" +
                        (waitSeconds > 0.0005 ? ", singleFlightWait=" + waitSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s" : "") + ".");
                }
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Bundle preparation failed: " + ex);
                CleanupBundle(rpc);
                SendError(rpc, "Server failed while preparing the compressed AutoModSync package: " + ex.Message);
            }
        }

        // Intent: Returns a retained immutable bundle for this exact signed content set, or performs the one allowed build for that cache key.
        // Concurrency: waiters for an identical key block on BundleBuildState and then acquire the published artifact; different keys are free to build independently.
        private static BundleArtifact AcquireBundleArtifact(List<FileRecord> records, long expandedBytes, long maxBundleBytes, out string cacheStatus, out double waitSeconds)
        {
            string cacheKey = BuildBundleCacheKey(records);
            DateTime waitStartedUtc = DateTime.MinValue;
            bool waited = false;
            BundleBuildState state = null;

            while (true)
            {
                lock (BundleCacheLock)
                {
                    DateTime now = DateTime.UtcNow;
                    PruneBundleCacheLocked(now);

                    BundleArtifact cached;
                    if (BundleArtifactCache.TryGetValue(cacheKey, out cached))
                    {
                        if (!File.Exists(cached.ZipPath))
                        {
                            BundleArtifactCache.Remove(cacheKey);
                        }
                        else
                        {
                            if (cached.Size > maxBundleBytes)
                                throw new InvalidDataException("Cached AutoModSync package exceeds the current configured server transfer limit.");

                            cached.ActiveTransfers++;
                            cached.LastUsedUtc = now;
                            cacheStatus = waited ? "WAIT-HIT" : "HIT";
                            waitSeconds = waited ? (now - waitStartedUtc).TotalSeconds : 0.0;
                            return cached;
                        }
                    }

                    if (BundleBuilds.TryGetValue(cacheKey, out state))
                    {
                        if (!waited)
                        {
                            waited = true;
                            waitStartedUtc = now;
                            if (_instance != null)
                                _instance.Logger.LogInfo("AutoModSync bundle cache WAIT key=" + ShortCacheKey(cacheKey) + "; another client is building the identical artifact.");
                        }

                        while (!state.Complete) Monitor.Wait(BundleCacheLock);
                        if (state.Error != null)
                            throw new InvalidOperationException("The shared AutoModSync bundle build failed.", state.Error);

                        continue;
                    }

                    state = new BundleBuildState();
                    BundleBuilds.Add(cacheKey, state);
                    break;
                }
            }

            try
            {
                BundleArtifact built = BuildBundleArtifact(cacheKey, records, expandedBytes, maxBundleBytes);
                lock (BundleCacheLock)
                {
                    built.ActiveTransfers = 1;
                    built.LastUsedUtc = DateTime.UtcNow;
                    BundleArtifactCache[cacheKey] = built;
                    state.Complete = true;
                    BundleBuilds.Remove(cacheKey);
                    Monitor.PulseAll(BundleCacheLock);
                }

                cacheStatus = "MISS";
                waitSeconds = 0.0;
                return built;
            }
            catch (Exception ex)
            {
                lock (BundleCacheLock)
                {
                    state.Error = ex;
                    state.Complete = true;
                    BundleBuilds.Remove(cacheKey);
                    Monitor.PulseAll(BundleCacheLock);
                }
                throw;
            }
        }

        // Intent: Builds one ZIP to a private temporary path, hashes it, then atomically publishes the completed file under its deterministic content key.
        // Safety: incomplete builds are never inserted into BundleArtifactCache and temporary files are removed on every failure path.
        private static BundleArtifact BuildBundleArtifact(string cacheKey, List<FileRecord> records, long expandedBytes, long maxBundleBytes)
        {
            string cacheRoot = Path.Combine(Paths.BepInExRootPath, "AutoModSync", "cache");
            if (!Directory.Exists(cacheRoot)) Directory.CreateDirectory(cacheRoot);

            string tempPath = Path.Combine(cacheRoot, "bundle-build-" + Guid.NewGuid().ToString("N") + ".tmp");
            string finalPath = Path.Combine(cacheRoot, "bundle-cache-" + cacheKey + ".zip");
            Stopwatch totalWatch = Stopwatch.StartNew();
            Stopwatch zipWatch = new Stopwatch();
            Stopwatch hashWatch = new Stopwatch();

            try
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);

                zipWatch.Start();
                using (FileStream output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, false))
                {
                    int i;
                    for (i = 0; i < records.Count; i++)
                    {
                        FileRecord record = records[i];
                        string entryName = ArchivePrefix(record.Kind) + record.RelativePath.Replace('\\', '/');
                        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                        using (Stream entryStream = entry.Open())
                        using (FileStream input = new FileStream(record.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            CopyIntoBundleBounded(input, entryStream, output, maxBundleBytes, record.Sha256);
                        }
                        if (output.Length > maxBundleBytes)
                            throw new InvalidDataException("Compressed AutoModSync package exceeds the configured server transfer limit.");
                    }
                }
                zipWatch.Stop();

                FileInfo fi = new FileInfo(tempPath);
                if (fi.Length > maxBundleBytes)
                    throw new InvalidDataException("Compressed AutoModSync package exceeds the configured server transfer limit.");

                hashWatch.Start();
                string bundleSha256 = Sha256File(tempPath);
                hashWatch.Stop();

                File.Move(tempPath, finalPath);
                totalWatch.Stop();

                BundleArtifact artifact = new BundleArtifact();
                artifact.CacheKey = cacheKey;
                artifact.ZipPath = finalPath;
                artifact.Size = fi.Length;
                artifact.Sha256 = bundleSha256;
                artifact.FileCount = records.Count;
                artifact.ExpandedBytes = expandedBytes;
                artifact.CreatedUtc = DateTime.UtcNow;
                artifact.LastUsedUtc = artifact.CreatedUtc;
                artifact.ZipBuildSeconds = zipWatch.Elapsed.TotalSeconds;
                artifact.ZipHashSeconds = hashWatch.Elapsed.TotalSeconds;

                if (_instance != null)
                    _instance.Logger.LogInfo("AutoModSync bundle cache MISS key=" + ShortCacheKey(cacheKey) +
                        ": ZIP build=" + artifact.ZipBuildSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s" +
                        ", ZIP SHA-256=" + artifact.ZipHashSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s" +
                        ", total=" + totalWatch.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s" +
                        ", " + FormatBytes(artifact.Size) + " from " + FormatBytes(expandedBytes) + " expanded source bytes.");

                return artifact;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        // Intent: Creates a deterministic identity for the exact requested signed records, independent of client request ordering.
        // The key includes archive format generation plus kind/path/size/content hash, so any synchronized content change necessarily selects a different artifact.
        private static string BuildBundleCacheKey(List<FileRecord> records)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("ams4-zip-fastest-v1\n");
            int i;
            for (i = 0; i < records.Count; i++)
            {
                FileRecord record = records[i];
                sb.Append(record.Kind).Append('\t')
                  .Append(record.RelativePath).Append('\t')
                  .Append(record.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(record.Sha256).Append('\n');
            }

            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        // Intent: Releases one client's reference to a shared immutable artifact and performs bounded TTL/LRU cleanup only after it is no longer active.
        private static void ReleaseBundleArtifact(BundleArtifact artifact)
        {
            if (artifact == null) return;
            lock (BundleCacheLock)
            {
                if (artifact.ActiveTransfers > 0) artifact.ActiveTransfers--;
                artifact.LastUsedUtc = DateTime.UtcNow;
                PruneBundleCacheLocked(artifact.LastUsedUtc);
            }
        }

        // Intent: Enforces the configured completed-artifact lifetime and total cache budget without deleting files still referenced by live transfers.
        // Budget eviction is least-recently-used among idle artifacts; active artifacts may temporarily exceed the configured retained-cache budget.
        private static void PruneBundleCacheLocked(DateTime now)
        {
            int cacheSeconds = _bundleCacheSeconds == null ? 600 : Math.Max(0, _bundleCacheSeconds.Value);
            long maxCacheBytes = (long)(_bundleCacheMaxMiB == null ? 4096 : Math.Max(0, _bundleCacheMaxMiB.Value)) * 1024L * 1024L;
            List<string> removeKeys = new List<string>();

            foreach (KeyValuePair<string, BundleArtifact> pair in BundleArtifactCache)
            {
                BundleArtifact artifact = pair.Value;
                if (artifact == null)
                {
                    removeKeys.Add(pair.Key);
                    continue;
                }

                if (artifact.ActiveTransfers > 0) continue;
                bool missing = String.IsNullOrEmpty(artifact.ZipPath) || !File.Exists(artifact.ZipPath);
                bool expired = cacheSeconds == 0 || (now - artifact.LastUsedUtc).TotalSeconds > cacheSeconds;
                if (missing || expired) removeKeys.Add(pair.Key);
            }

            int i;
            for (i = 0; i < removeKeys.Count; i++) RemoveCachedArtifactLocked(removeKeys[i]);

            long totalBytes = 0L;
            List<BundleArtifact> idle = new List<BundleArtifact>();
            foreach (KeyValuePair<string, BundleArtifact> pair in BundleArtifactCache)
            {
                BundleArtifact artifact = pair.Value;
                if (artifact == null || String.IsNullOrEmpty(artifact.ZipPath) || !File.Exists(artifact.ZipPath)) continue;
                totalBytes += Math.Max(0L, artifact.Size);
                if (artifact.ActiveTransfers <= 0) idle.Add(artifact);
            }

            if (totalBytes <= maxCacheBytes) return;

            idle.Sort(delegate(BundleArtifact a, BundleArtifact b)
            {
                return a.LastUsedUtc.CompareTo(b.LastUsedUtc);
            });

            for (i = 0; i < idle.Count && totalBytes > maxCacheBytes; i++)
            {
                BundleArtifact artifact = idle[i];
                if (artifact.ActiveTransfers > 0) continue;
                long bytes = Math.Max(0L, artifact.Size);
                RemoveCachedArtifactLocked(artifact.CacheKey);
                totalBytes -= bytes;
            }
        }

        // Intent: Removes one idle artifact from the in-memory index and best-effort deletes its immutable ZIP.
        // Caller must hold BundleCacheLock and must never pass an artifact that is actively referenced.
        private static void RemoveCachedArtifactLocked(string cacheKey)
        {
            if (String.IsNullOrEmpty(cacheKey)) return;
            BundleArtifact artifact;
            if (!BundleArtifactCache.TryGetValue(cacheKey, out artifact)) return;
            if (artifact != null && artifact.ActiveTransfers > 0) return;

            BundleArtifactCache.Remove(cacheKey);
            if (artifact != null && !String.IsNullOrEmpty(artifact.ZipPath))
            {
                try { if (File.Exists(artifact.ZipPath)) File.Delete(artifact.ZipPath); } catch { }
            }
        }

        // Intent: Copies one signed source file into the ZIP while observing the compressed-output ceiling and re-hashing the exact bytes being archived.
        // Cache correctness: a same-size local file change after manifest signing must fail this build instead of poisoning the shared artifact cache under the old signed hash.
        // Resource safety: the final post-ZIP check remains authoritative because central-directory bytes are written when the archive closes.
        private static void CopyIntoBundleBounded(Stream input, Stream entryStream, FileStream output, long maxBundleBytes, string expectedSha256)
        {
            byte[] buffer = new byte[81920];
            using (SHA256 sourceHash = SHA256.Create())
            {
                while (true)
                {
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    // HashAlgorithm permits the same array for input/output; this avoids a second full source-file read before cache publication.
                    sourceHash.TransformBlock(buffer, 0, read, buffer, 0);
                    entryStream.Write(buffer, 0, read);
                    if (output.Length > maxBundleBytes)
                        throw new InvalidDataException("Compressed AutoModSync package exceeds the configured server transfer limit.");
                }

                sourceHash.TransformFinalBlock(new byte[0], 0, 0);
                string actualSha256 = ToHex(sourceHash.Hash);
                if (!String.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Server content changed after the signed manifest was created; reconnect to refresh synchronization state.");
            }
        }

        // Intent: Formats a full bundle cache key into a compact log identifier while preserving enough entropy to correlate build/hit/wait events.
        private static string ShortCacheKey(string cacheKey)
        {
            if (String.IsNullOrEmpty(cacheKey)) return "(none)";
            return cacheKey.Length <= 12 ? cacheKey : cacheKey.Substring(0, 12);
        }

        // Intent: Serves one legacy chunk or a bounded 2.5 transfer window beginning at the requested chunk index, then emits the unchanged AMS4 completion message when the client requests TotalChunks.
        // Performance: opens/seeks the prepared ZIP once per requested window and sends up to 16 sequential chunks from that stream, removing most per-chunk RPC round trips and file open/seek operations while preserving ordered delivery.
        private static void RPC_GetBundleChunk(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                int index = pkg.ReadInt();
                int requestedCount = 1;
                try { requestedCount = pkg.ReadInt(); } catch { requestedCount = 1; }
                requestedCount = Math.Max(1, Math.Min(16, requestedCount));

                BundleTransfer transfer;
                if (!BundleTransfers.TryGetValue(rpc, out transfer) || transfer == null || !File.Exists(transfer.ZipPath))
                    throw new InvalidDataException("No active AutoModSync package exists for this client.");
                if (index < 0 || index > transfer.TotalChunks) throw new InvalidDataException("Invalid AutoModSync package chunk request.");

                if (index == transfer.TotalChunks)
                {
                    ZPackage end = new ZPackage();
                    end.Write(transfer.Sha256);
                    end.Write(transfer.FileCount);
                    rpc.Invoke(RpcBundleEnd, new object[] { end });
                    CleanupBundle(rpc);
                    return;
                }

                byte[] buffer = new byte[transfer.ChunkBytes];
                using (FileStream stream = new FileStream(transfer.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    stream.Seek((long)index * transfer.ChunkBytes, SeekOrigin.Begin);
                    int sent;
                    for (sent = 0; sent < requestedCount && index + sent < transfer.TotalChunks; sent++)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0) throw new EndOfStreamException("Unexpected end of compressed AutoModSync package.");

                        ZPackage chunk = new ZPackage();
                        chunk.Write(index + sent);
                        chunk.Write(Convert.ToBase64String(buffer, 0, read));
                        rpc.Invoke(RpcBundleChunk, new object[] { chunk });
                    }
                }
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Bundle chunk transfer failed: " + ex);
                CleanupBundle(rpc);
                SendError(rpc, "Server failed while transferring the compressed AutoModSync package: " + ex.Message);
            }
        }

        // Intent: Serves one or more bounded binary batch messages for a requested compressed-bundle window, or the unchanged completion message when the client requests TotalChunks.
        // Performance: each Steam message remains capped near 384 KiB, but a current client can request up to 128 chunks at once so several batch messages are queued back-to-back and the reliable pipe stays full instead of waiting for a client request after every ~384 KiB.
        private static void RPC_GetBundleBatch(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                int index = pkg.ReadInt();
                int requestedCount = 1;
                try { requestedCount = pkg.ReadInt(); } catch { requestedCount = 1; }
                requestedCount = Math.Max(1, Math.Min(128, requestedCount));

                BundleTransfer transfer;
                if (!BundleTransfers.TryGetValue(rpc, out transfer) || transfer == null || !File.Exists(transfer.ZipPath))
                    throw new InvalidDataException("No active AutoModSync package exists for this client.");
                if (index < 0 || index > transfer.TotalChunks) throw new InvalidDataException("Invalid AutoModSync package batch request.");

                if (index == transfer.TotalChunks)
                {
                    ZPackage end = new ZPackage();
                    end.Write(transfer.Sha256);
                    end.Write(transfer.FileCount);
                    rpc.Invoke(RpcBundleEnd, new object[] { end });
                    CleanupBundle(rpc);
                    return;
                }

                const int maxBatchBytes = 384 * 1024;
                int maxChunksPerMessage = Math.Max(1, maxBatchBytes / Math.Max(1, transfer.ChunkBytes));
                int remaining = Math.Min(requestedCount, transfer.TotalChunks - index);
                byte[] buffer = new byte[transfer.ChunkBytes];

                using (FileStream stream = new FileStream(transfer.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    stream.Seek((long)index * transfer.ChunkBytes, SeekOrigin.Begin);
                    int cursor = index;
                    while (remaining > 0)
                    {
                        int count = Math.Min(maxChunksPerMessage, remaining);
                        ZPackage batch = new ZPackage();
                        batch.Write(cursor);
                        batch.Write(count);

                        int sent;
                        for (sent = 0; sent < count; sent++)
                        {
                            int read = stream.Read(buffer, 0, buffer.Length);
                            if (read <= 0) throw new EndOfStreamException("Unexpected end of compressed AutoModSync package.");
                            batch.Write(cursor + sent);
                            if (read == buffer.Length)
                            {
                                batch.Write(buffer);
                            }
                            else
                            {
                                byte[] tail = new byte[read];
                                Buffer.BlockCopy(buffer, 0, tail, 0, read);
                                batch.Write(tail);
                            }
                        }

                        rpc.Invoke(RpcBundleBatch, new object[] { batch });
                        cursor += count;
                        remaining -= count;
                    }
                }

                LogTransferSteamTelemetry(transfer);
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Bundle batch transfer failed: " + ex);
                CleanupBundle(rpc);
                SendError(rpc, "Server failed while transferring the compressed AutoModSync package batch: " + ex.Message);
            }
        }

        // Intent: Resolves a client request string to one current FileRecord from the server's signed manifest map.
        // Security: validates kind/path, existence, fixed BepInEx source containment, reparse points, and the configured per-file size limit before opening a source.
        private static FileRecord ResolveBundleRecord(string requested)
        {
            if (String.IsNullOrEmpty(requested) || requested.Length < 3 || requested[1] != ':')
                throw new InvalidDataException("Invalid AutoModSync file request.");

            char kind = requested[0];
            if (!IsSupportedManifestKind(kind)) throw new InvalidDataException("Invalid AutoModSync file kind.");
            string relative = NormalizeRelative(requested.Substring(2));
            if (relative.Length == 0) throw new InvalidDataException("Invalid AutoModSync relative path.");

            string lookup = kind + ":" + relative;
            FileRecord record;
            if (!_files.TryGetValue(lookup, out record) || !File.Exists(record.FullPath))
            {
                EnsureManifest(true);
                if (!_files.TryGetValue(lookup, out record) || !File.Exists(record.FullPath))
                    throw new FileNotFoundException("Requested synchronized file is not available: " + relative);
            }

            string sourceRelative = NormalizeRelative(MakeRelative(Paths.BepInExRootPath, record.FullPath));
            if (sourceRelative.Length == 0) throw new InvalidDataException("Requested source escaped the BepInEx root.");
            string safeSource = AutoModSyncPathSafety.SafeUnderRoot(Paths.BepInExRootPath, sourceRelative, true);
            if (!String.Equals(Path.GetFullPath(record.FullPath), safeSource, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Requested source path changed during bundle preparation.");

            long maxBytes = (long)Math.Max(1, _maxFileMiB.Value) * 1024L * 1024L;
            FileInfo current = new FileInfo(safeSource);
            if (current.Length > maxBytes) throw new InvalidDataException("File exceeds server transfer limit: " + relative);
            if (current.Length != record.Size)
                throw new InvalidDataException("Server content changed after the signed manifest was created; reconnect to refresh synchronization state.");

            return record;
        }

        // Intent: Temporarily tunes the exact SteamNetworkingSockets connection carrying this AutoModSync bundle so Valheim's low default rate/buffer settings do not throttle file synchronization.
        // Compatibility: obtains the transport directly from ZRpc (so pre-handshake peers do not need to be discoverable through ZNet.GetPeers), unwraps ServerSync-style decorators, changes only this live connection, and records every previous value for cleanup.
        private static void TryTuneTransferTransport(ZRpc rpc, BundleTransfer transfer)
        {
            if (rpc == null || transfer == null) return;
            int desiredMax = _transferSendRateMax == null ? 67108864 : Math.Max(153600, _transferSendRateMax.Value);
            int desiredMin = _transferSendRateMin == null ? 16777216 : Math.Max(0, Math.Min(desiredMax, _transferSendRateMin.Value));
            int desiredBuffer = _transferSendBufferBytes == null ? 33554432 : Math.Max(0, _transferSendBufferBytes.Value);

            try
            {
                object socket = GetRpcTransport(rpc);
                if (socket == null)
                {
                    if (_instance != null) _instance.Logger.LogWarning("AutoModSync could not inspect the active ZRpc transport; Steam transfer tuning was skipped.");
                    return;
                }
                if (socket.GetType().Name != "ZSteamSocket")
                {
                    if (_instance != null) _instance.Logger.LogInfo("AutoModSync bundle transport is " + socket.GetType().Name + "; Steam-specific transfer tuning does not apply.");
                    return;
                }

                uint handle = GetSteamConnectionHandle(socket);
                if (handle == 0u)
                {
                    if (_instance != null) _instance.Logger.LogWarning("AutoModSync found ZSteamSocket but could not resolve its Steam connection handle; transfer tuning was skipped.");
                    return;
                }

                int beforeMax = ReadSteamConnectionInt(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax);
                int beforeMin = ReadSteamConnectionInt(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin);
                int beforeBuffer = ReadSteamConnectionInt(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize);

                transfer.SteamConnectionHandle = handle;
                transfer.OriginalSendRateMax = beforeMax;
                transfer.OriginalSendRateMin = beforeMin;
                transfer.OriginalSendBufferBytes = beforeBuffer;

                if (desiredMax > 0 && (beforeMax <= 0 || beforeMax < desiredMax))
                    transfer.ChangedSendRateMax = WriteSteamConnectionInt(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, desiredMax);
                if (desiredMin > 0 && (beforeMin <= 0 || beforeMin < desiredMin))
                    transfer.ChangedSendRateMin = WriteSteamConnectionInt(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, desiredMin);
                if (desiredBuffer > 0 && (beforeBuffer <= 0 || beforeBuffer < desiredBuffer))
                    transfer.ChangedSendBuffer = WriteSteamConnectionInt(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, desiredBuffer);

                int afterMax = ReadSteamConnectionInt(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax);
                int afterMin = ReadSteamConnectionInt(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin);
                int afterBuffer = ReadSteamConnectionInt(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize);

                if (_instance != null)
                {
                    _instance.Logger.LogInfo("AutoModSync Steam bundle transport: SendRateMin " + ShowSteamValue(beforeMin) + " -> " + ShowSteamValue(afterMin) +
                        ", SendRateMax " + ShowSteamValue(beforeMax) + " -> " + ShowSteamValue(afterMax) +
                        ", SendBuffer " + ShowSteamValue(beforeBuffer) + " -> " + ShowSteamValue(afterBuffer) + " B.");
                    if (afterMax > 0 && afterMax < desiredMax)
                        _instance.Logger.LogWarning("AutoModSync requested Steam SendRateMax " + desiredMax.ToString(CultureInfo.InvariantCulture) + " B/s but the live connection reports " + afterMax.ToString(CultureInfo.InvariantCulture) + " B/s.");
                    if (desiredMin > 0 && afterMin > 0 && afterMin < desiredMin)
                        _instance.Logger.LogWarning("AutoModSync requested Steam SendRateMin " + desiredMin.ToString(CultureInfo.InvariantCulture) + " B/s but the live connection reports " + afterMin.ToString(CultureInfo.InvariantCulture) + " B/s.");
                }
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync Steam transfer tuning unavailable: " + ex.Message);
            }
        }

        // Intent: Restores every Steam connection setting that AutoModSync changed for the completed/aborted bundle, leaving settings owned by Valheim or another networking mod exactly as they were before the transfer.
        private static void RestoreTransferTransport(BundleTransfer transfer)
        {
            if (transfer == null || transfer.SteamConnectionHandle == 0u) return;
            try
            {
                bool maxOk = true, minOk = true, bufferOk = true;
                if (transfer.ChangedSendRateMax && transfer.OriginalSendRateMax > 0)
                    maxOk = WriteSteamConnectionInt(transfer.SteamConnectionHandle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, transfer.OriginalSendRateMax);
                if (transfer.ChangedSendRateMin && transfer.OriginalSendRateMin > 0)
                    minOk = WriteSteamConnectionInt(transfer.SteamConnectionHandle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, transfer.OriginalSendRateMin);
                if (transfer.ChangedSendBuffer && transfer.OriginalSendBufferBytes > 0)
                    bufferOk = WriteSteamConnectionInt(transfer.SteamConnectionHandle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, transfer.OriginalSendBufferBytes);

                if (_instance != null && (transfer.ChangedSendRateMax || transfer.ChangedSendRateMin || transfer.ChangedSendBuffer))
                {
                    if (maxOk && minOk && bufferOk)
                        _instance.Logger.LogInfo("AutoModSync restored the Steam bundle transport settings after synchronization.");
                    else
                        _instance.Logger.LogWarning("AutoModSync could not restore one or more Steam bundle transport settings after synchronization.");
                }
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("AutoModSync could not restore Steam bundle transport settings: " + ex.Message);
            }
            finally
            {
                transfer.ChangedSendRateMax = false;
                transfer.ChangedSendRateMin = false;
                transfer.ChangedSendBuffer = false;
            }
        }

        // Intent: Reads the live transport directly from ZRpc and unwraps nested decorator sockets by following an instance field named Original.
        // Reason: AutoModSync preflight runs before the vanilla handshake is released, so searching only ZNet's ready peer list can miss the connection and silently leave Valheim's 153600 B/s rate in place.
        private static object GetRpcTransport(ZRpc rpc)
        {
            if (rpc == null) return null;
            FieldInfo socketField = AccessTools.Field(typeof(ZRpc), "m_socket");
            object current = socketField == null ? null : socketField.GetValue(rpc);
            int guard = 0;
            while (current != null && guard++ < 16)
            {
                FieldInfo original = current.GetType().GetField("Original", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (original == null) break;
                object next = original.GetValue(current);
                if (next == null || Object.ReferenceEquals(next, current)) break;
                current = next;
            }
            return current;
        }

        // Intent: Extracts the numeric HSteamNetConnection handle from the real ZSteamSocket without depending on the wrapper struct's private field name.
        private static uint GetSteamConnectionHandle(object socket)
        {
            if (socket == null) return 0u;
            FieldInfo conField = socket.GetType().GetField("m_con", BindingFlags.Instance | BindingFlags.NonPublic);
            if (conField == null) return 0u;
            object con = conField.GetValue(socket);
            if (con == null) return 0u;
            FieldInfo[] fields = con.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int i;
            for (i = 0; i < fields.Length; i++)
                if (fields[i].FieldType == typeof(uint)) return (uint)fields[i].GetValue(con);
            return 0u;
        }

        // Intent: Samples Steam's live connection telemetry at a low rate during bundle delivery so throughput tests show whether Steam's own bandwidth estimator or reliable queue is still the limiting layer.
        private static void LogTransferSteamTelemetry(BundleTransfer transfer)
        {
            if (transfer == null || transfer.SteamConnectionHandle == 0u || _instance == null) return;
            DateTime now = DateTime.UtcNow;
            if (transfer.LastSteamTelemetryUtc != DateTime.MinValue && (now - transfer.LastSteamTelemetryUtc).TotalSeconds < 5.0) return;
            transfer.LastSteamTelemetryUtc = now;
            try
            {
                HSteamNetConnection connection = new HSteamNetConnection(transfer.SteamConnectionHandle);
                SteamNetConnectionRealTimeStatus_t status = default(SteamNetConnectionRealTimeStatus_t);
                SteamNetConnectionRealTimeLaneStatus_t lane = default(SteamNetConnectionRealTimeLaneStatus_t);
                EResult result = SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lane);
                if (result != EResult.k_EResultOK)
                {
                    _instance.Logger.LogDebug("AutoModSync Steam telemetry unavailable: " + result.ToString());
                    return;
                }
                _instance.Logger.LogInfo("AutoModSync Steam transfer telemetry: rate=" + status.m_nSendRateBytesPerSecond.ToString(CultureInfo.InvariantCulture) +
                    " B/s, pendingReliable=" + status.m_cbPendingReliable.ToString(CultureInfo.InvariantCulture) +
                    " B, unackedReliable=" + status.m_cbSentUnackedReliable.ToString(CultureInfo.InvariantCulture) +
                    " B, ping=" + status.m_nPing.ToString(CultureInfo.InvariantCulture) + " ms.");
            }
            catch (Exception ex)
            {
                _instance.Logger.LogDebug("AutoModSync Steam telemetry read failed: " + ex.Message);
            }
        }

        // Intent: Formats a SteamNetworkingSockets integer setting for transfer diagnostics without hiding unavailable reads behind a plausible numeric value.
        private static string ShowSteamValue(int value)
        {
            return value < 0 ? "n/a" : value.ToString(CultureInfo.InvariantCulture);
        }

        // Intent: Reads one int32 SteamNetworkingSockets setting at connection scope; returns -1 when the dedicated-server Steam interface cannot provide it.
        private static int ReadSteamConnectionInt(uint handle, ESteamNetworkingConfigValue key)
        {
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buffer, 0);
                ulong size = 4;
                ESteamNetworkingConfigDataType dataType;
                ESteamNetworkingGetConfigValueResult result = SteamGameServerNetworkingUtils.GetConfigValue(key,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, new IntPtr((long)handle), out dataType, buffer, ref size);
                if (result != ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OK &&
                    result != ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OKInherited) return -1;
                return Marshal.ReadInt32(buffer);
            }
            catch { return -1; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // Intent: Writes one int32 SteamNetworkingSockets setting at connection scope using the dedicated-server interface.
        private static bool WriteSteamConnectionInt(uint handle, ESteamNetworkingConfigValue key, int value)
        {
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buffer, value);
                return SteamGameServerNetworkingUtils.SetConfigValue(key,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, new IntPtr((long)handle),
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, buffer);
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // Intent: Removes one client's transfer state, restores its temporary Steam tuning, and releases its reference to the shared immutable bundle artifact.
        // Cache lifetime/eviction is handled separately so a completed client cannot delete a ZIP still being read by another client.
        private static void CleanupBundle(ZRpc rpc)
        {
            BundleTransfer transfer;
            if (!BundleTransfers.TryGetValue(rpc, out transfer)) return;
            BundleTransfers.Remove(rpc);
            RestoreTransferTransport(transfer);

            if (transfer != null && transfer.Artifact != null)
            {
                ReleaseBundleArtifact(transfer.Artifact);
            }
            else if (transfer != null && !String.IsNullOrEmpty(transfer.ZipPath))
            {
                // Compatibility fallback for any pre-cache transfer object created before a development hot reload.
                try { if (File.Exists(transfer.ZipPath)) File.Delete(transfer.ZipPath); } catch { }
            }
        }

        // Intent: Removes orphaned bundle files from a previous server process.
        // Published-cache metadata is intentionally in-memory only, so no ZIP from an earlier process is trusted/reused after restart.
        private static void CleanupOldBundleCache()
        {
            try
            {
                string cacheRoot = Path.Combine(Paths.BepInExRootPath, "AutoModSync", "cache");
                if (!Directory.Exists(cacheRoot)) return;

                string[] zipFiles = Directory.GetFiles(cacheRoot, "bundle-*.zip", SearchOption.TopDirectoryOnly);
                string[] tempFiles = Directory.GetFiles(cacheRoot, "bundle-build-*.tmp", SearchOption.TopDirectoryOnly);
                int i;
                for (i = 0; i < zipFiles.Length; i++)
                {
                    try { File.Delete(zipFiles[i]); } catch { }
                }
                for (i = 0; i < tempFiles.Length; i++)
                {
                    try { File.Delete(tempFiles[i]); } catch { }
                }
            }
            catch { }
        }

        // Intent: Formats byte counts into readable B/KB/MB/GB values for transfer logs.
        private static string FormatBytes(long value)
        {
            double n = value;
            string[] units = new string[] { "B", "KB", "MB", "GB" };
            int unit = 0;
            while (n >= 1024.0 && unit < units.Length - 1) { n /= 1024.0; unit++; }
            return n.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        // Intent: Sends a bounded AutoModSync error string over the existing peer RPC; failures while reporting are deliberately swallowed to avoid destabilizing Valheim networking.
        private static void SendError(ZRpc rpc, string message)
        {
            try
            {
                ZPackage p = new ZPackage();
                p.Write(message ?? "AutoModSync error");
                rpc.Invoke(RpcError, new object[] { p });
            }
            catch { }
        }

        // Intent: Scans BepInEx/plugins, hashes eligible files, builds the canonical manifest text/file map, and signs that exact text.
        // Workflow: honors the short cache interval unless forced, excludes configured/non-distributable files, substitutes the release client payload for standalone installs, sorts paths deterministically, and publishes map/text/signature atomically under a lock.
        private static void EnsureManifest(bool force)
        {
            lock (ManifestLock)
            {
                int seconds = _manifestCacheSeconds == null ? 5 : Math.Max(0, _manifestCacheSeconds.Value);
                if (!force && _manifestBuiltUtc != DateTime.MinValue && (DateTime.UtcNow - _manifestBuiltUtc).TotalSeconds <= seconds) return;

                List<FileRecord> records = new List<FileRecord>();
                AddManifestRoot(records, 'P', Paths.PluginPath, false);

                string patcherRoot = Path.Combine(Paths.BepInExRootPath, "patchers");
                if (_syncPatchers == null || _syncPatchers.Value) AddManifestRoot(records, 'R', patcherRoot, false);

                string configPatterns = _syncConfigPatterns == null ? "" : (_syncConfigPatterns.Value ?? "");
                if (!String.IsNullOrWhiteSpace(configPatterns)) AddManifestRoot(records, 'C', Paths.ConfigPath, true);

                // 2.4.5+: no packed game-root bootstrap is distributed or synchronized.

                string releaseClientPlugin = Path.Combine(Paths.BepInExRootPath, "AutoModSync", "release", "ValheimAutoModSync.Client.dll");
                if (!IsPackageManagedAutoModSync() && File.Exists(releaseClientPlugin))
                {
                    // The standalone release payload is outside BepInEx/plugins but still under the fixed BepInEx root.
                    // Reparse validation prevents a local junction/symlink from turning that special source into an arbitrary read.
                    AutoModSyncPathSafety.EnsureNoReparsePoints(Paths.BepInExRootPath, releaseClientPlugin, true);
                    records.RemoveAll(delegate(FileRecord x) { return x.Kind == 'P' && String.Equals(x.RelativePath, "ValheimAutoModSync.Client.dll", StringComparison.OrdinalIgnoreCase); });
                    FileInfo cfi = new FileInfo(releaseClientPlugin);
                    FileRecord cr = new FileRecord();
                    cr.Kind = 'P';
                    cr.RelativePath = "ValheimAutoModSync.Client.dll";
                    cr.FullPath = releaseClientPlugin;
                    cr.Size = cfi.Length;
                    cr.Sha256 = Sha256File(releaseClientPlugin);
                    records.Add(cr);
                }

                records.Sort(delegate(FileRecord a, FileRecord b)
                {
                    int byKind = a.Kind.CompareTo(b.Kind);
                    return byKind != 0 ? byKind : StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
                });
                Dictionary<string, FileRecord> map = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
                StringBuilder sb = new StringBuilder();
                int j;
                for (j = 0; j < records.Count; j++)
                {
                    FileRecord r = records[j];
                    if (r.RelativePath.IndexOf('\t') >= 0 || r.RelativePath.IndexOf('\r') >= 0 || r.RelativePath.IndexOf('\n') >= 0) continue;
                    map[r.Kind + ":" + r.RelativePath] = r;
                    sb.Append(r.Kind).Append('\t').Append(r.Sha256).Append('\t').Append(r.Size.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.RelativePath).Append('\n');
                }

                _files = map;
                _manifestText = sb.ToString();
                _manifestSignature = SignManifest(Encoding.UTF8.GetBytes(_manifestText));
                _manifestBuiltUtc = DateTime.UtcNow;
                if (_instance != null) _instance.Logger.LogDebug("AutoModSync manifest: " + map.Count + " files.");
            }
        }

        // Intent: Adds one permitted BepInEx subtree to the signed manifest while preserving paths relative to that subtree.
        // Policy: plugin/patcher roots honor exclusion, server-only, and optional client-required rules; config files are included only by the explicit SyncConfigPatterns allowlist.
        // Security: recursion never follows reparse-point directories/files, and every source is rechecked beneath the fixed root before hashing.
        private static void AddManifestRoot(List<FileRecord> records, char kind, string root, bool configAllowlistRequired)
        {
            if (records == null || String.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            List<string> files = EnumerateManifestFiles(root);
            int i;
            for (i = 0; i < files.Count; i++)
            {
                string full = files[i];
                string rel = NormalizeRelative(MakeRelative(root, full));
                string name = Path.GetFileName(full);
                if (rel.Length == 0)
                {
                    if (_instance != null) _instance.Logger.LogWarning("AutoModSync skipped an unsafe Windows path while scanning " + root + ": " + full);
                    continue;
                }
                if (IsExcluded(rel, name)) continue;

                // Recheck after enumeration to narrow the window in which a local filesystem entry could be swapped for a junction/symlink.
                full = AutoModSyncPathSafety.SafeUnderRoot(root, rel, true);

                if (kind == 'C')
                {
                    if (IsProtectedConfigName(name)) continue;
                    if (configAllowlistRequired && !MatchesPatterns(rel, name, _syncConfigPatterns == null ? "" : (_syncConfigPatterns.Value ?? ""))) continue;
                }
                else
                {
                    if (MatchesPatterns(rel, name, _serverOnlyPatterns == null ? "" : (_serverOnlyPatterns.Value ?? ""))) continue;
                    string required = _clientRequiredPatterns == null ? "" : (_clientRequiredPatterns.Value ?? "");
                    if (!String.IsNullOrWhiteSpace(required) && !MatchesPatterns(rel, name, required)) continue;
                }

                FileInfo fi = new FileInfo(full);
                FileRecord r = new FileRecord();
                r.Kind = kind;
                r.RelativePath = rel;
                r.FullPath = full;
                r.Size = fi.Length;
                r.Sha256 = Sha256File(full);
                records.Add(r);
            }
        }

        // Intent: Recursively enumerates one manifest source tree without following filesystem reparse points.
        // Security: a junction/symlink inside plugins, patchers, or config must never expand the server's distributable source boundary.
        private static List<string> EnumerateManifestFiles(string root)
        {
            List<string> files = new List<string>();
            Stack<string> pending = new Stack<string>();
            string rootFull = Path.GetFullPath(root);
            pending.Push(rootFull);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                string[] currentFiles = Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly);
                int i;
                for (i = 0; i < currentFiles.Length; i++)
                {
                    if (AutoModSyncPathSafety.IsReparsePoint(currentFiles[i]))
                    {
                        if (_instance != null) _instance.Logger.LogWarning("AutoModSync skipped reparse-point source file: " + currentFiles[i]);
                        continue;
                    }
                    files.Add(currentFiles[i]);
                }

                string[] directories = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly);
                for (i = 0; i < directories.Length; i++)
                {
                    if (AutoModSyncPathSafety.IsReparsePoint(directories[i]))
                    {
                        if (_instance != null) _instance.Logger.LogWarning("AutoModSync skipped reparse-point source directory: " + directories[i]);
                        continue;
                    }
                    pending.Push(directories[i]);
                }
            }

            return files;
        }

        // Intent: Hard-blocks AutoModSync identity files and BepInEx's loader-wide config from remote config synchronization even when an administrator uses a broad allowlist.
        private static bool IsProtectedConfigName(string name)
        {
            return String.Equals(name, "ValheimAutoModSync.private.xml", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "ValheimAutoModSync.public.xml", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "BepInEx.cfg", StringComparison.OrdinalIgnoreCase);
        }

        // Intent: Reports whether the current signed manifest contains roots that legacy AMS4 clients cannot install safely.
        private static bool ManifestRequiresRootSync()
        {
            foreach (FileRecord record in _files.Values)
                if (record != null && record.Kind != 'P') return true;
            return false;
        }

        // Intent: Maps a manifest kind to its fixed ZIP namespace so no server-supplied path can choose an arbitrary client destination.
        private static string ArchivePrefix(char kind)
        {
            if (kind == 'P') return "plugins/";
            if (kind == 'R') return "patchers/";
            if (kind == 'C') return "config/";
            throw new InvalidDataException("Unsupported AutoModSync manifest kind.");
        }

        // Intent: Limits synchronized manifest kinds to the three hardcoded BepInEx destinations supported by the 2.5 client/apply helper.
        private static bool IsSupportedManifestKind(char kind)
        {
            return kind == 'P' || kind == 'R' || kind == 'C';
        }

        // Intent: Applies one semicolon-separated wildcard list to both a relative path and filename for compatibility classification and config allowlisting.
        private static bool MatchesPatterns(string relative, string name, string raw)
        {
            if (String.IsNullOrWhiteSpace(raw)) return false;
            string[] patterns = raw.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            int i;
            for (i = 0; i < patterns.Length; i++)
            {
                string p = patterns[i].Trim().Replace('\\', '/');
                if (p.Length == 0) continue;
                if (WildcardMatch(relative, p) || WildcardMatch(name, p)) return true;
            }
            return false;
        }

        // Intent: Detects whether the server plugin itself is running from a package-manager subdirectory so standalone release-client payload behavior is not mixed into managed profiles.
        private static bool IsPackageManagedAutoModSync()
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(typeof(ServerPlugin).Assembly.Location);
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

        // Intent: Applies the configured semicolon-separated exclusion patterns to both a relative path and its filename before a file can enter the synchronized manifest.
        private static bool IsExcluded(string relative, string name)
        {
            if (String.Equals(name, "ValheimAutoModSync.Server.dll", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(name, "ValheimAutoModSync.Client.dll", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(name, "ValheimAutoModSync.Apply.exe", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(name, "icon.png", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(name, "CHANGELOG.md", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(name, "LICENSE", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(name, "THIRD-PARTY-NOTICES.md", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(name, "ValheimAutoModSync.private.xml", StringComparison.OrdinalIgnoreCase)) return true;
            string raw = _excludePatterns == null ? "" : (_excludePatterns.Value ?? "");
            return MatchesPatterns(relative, name, raw);
        }

        // Intent: Implements case-insensitive '*'/'?' wildcard matching for exclusion rules without invoking a shell or regular-expression engine.
        private static bool WildcardMatch(string text, string pattern)
        {
            text = (text ?? "").Replace('\\', '/');
            pattern = (pattern ?? "").Replace('\\', '/');
            int t = 0, p = 0, star = -1, mark = -1;
            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t]))) { t++; p++; continue; }
                if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; continue; }
                if (star != -1) { p = star + 1; t = ++mark; continue; }
                return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        // Intent: Canonicalizes manifest relative paths and rejects parent traversal, drive/URI separators, tabs, and newline characters before they enter protocol data.
        private static string NormalizeRelative(string value)
        {
            return AutoModSyncPathSafety.NormalizeRelative(value);
        }

        // Intent: Converts an absolute plugin path to a normalized relative path rooted at BepInEx/plugins; used only after the scan has already enumerated beneath that root.
        private static string MakeRelative(string root, string full)
        {
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(full);
            if (!f.StartsWith(r, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path escaped plugin root.");
            return f.Substring(r.Length);
        }

        // Intent: Computes SHA-256 for a server file while permitting concurrent readers; the hash becomes the content identity advertised in the signed manifest.
        private static string Sha256File(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (SHA256 sha = SHA256.Create()) return ToHex(sha.ComputeHash(fs));
        }

        // Intent: Signs the exact manifest bytes with the server's persistent RSA private key using SHA-256, returning Base64 for transport.
        private static string SignManifest(byte[] data)
        {
            if (_signer == null) throw new InvalidOperationException("AutoModSync signing identity is not loaded.");
            byte[] sig = _signer.SignData(data, CryptoConfig.MapNameToOID("SHA256"));
            return Convert.ToBase64String(sig);
        }


        // Intent: Converts fingerprints/hashes to deterministic lowercase hexadecimal for logs, trust display, and comparisons.
        private static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            int i;
            for (i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
