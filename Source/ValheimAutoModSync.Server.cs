using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;

[assembly: AssemblyTitle("Valheim AutoModSync Server")]
[assembly: AssemblyDescription("Server-side signed manifest and synchronized BepInEx plugin transfer component.")]
[assembly: AssemblyCompany("GordonFreesay")]
[assembly: AssemblyProduct("Valheim AutoModSync")]
[assembly: AssemblyVersion("2.5.0.0")]
[assembly: AssemblyFileVersion("2.5.0.0")]

namespace ValheimAutoModSync
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ServerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.gordonfreesay.valheimautomodsync.server";
        public const string PluginName = "Valheim AutoModSync Server";
        public const string PluginVersion = "2.5.0";
        public const int ProtocolVersion = 4;

        internal const string RpcHello = "AMS4_Hello";
        internal const string RpcAck = "AMS4_Ack";
        internal const string RpcManifestBegin = "AMS4_ManifestBegin";
        internal const string RpcManifestChunk = "AMS4_ManifestChunk";
        internal const string RpcManifestEnd = "AMS4_ManifestEnd";
        internal const string RpcGetBundle = "AMS4_GetBundle";
        internal const string RpcGetBundleChunk = "AMS4_GetBundleChunk";
        internal const string RpcBundleBegin = "AMS4_BundleBegin";
        internal const string RpcBundleChunk = "AMS4_BundleChunk";
        internal const string RpcBundleEnd = "AMS4_BundleEnd";
        internal const string RpcError = "AMS4_Error";

        private static ServerPlugin _instance;
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<string> _excludePatterns;
        private static ConfigEntry<int> _manifestCacheSeconds;
        private static ConfigEntry<int> _chunkBytes;
        private static ConfigEntry<int> _maxFileMiB;
        private static ConfigEntry<int> _maxBundleMiB;

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
        private static readonly Dictionary<ZRpc, BundleTransfer> BundleTransfers = new Dictionary<ZRpc, BundleTransfer>();
        
        private sealed class FileRecord
        {
            public string RelativePath;
            public string FullPath;
            public long Size;
            public string Sha256;
            public char Kind;
        }

        private sealed class BundleTransfer
        {
            public string ZipPath;
            public long Size;
            public string Sha256;
            public int ChunkBytes;
            public int TotalChunks;
            public int FileCount;
            public FileStream Stream;
        }

        // Intent: BepInEx server entry point; binds server/transfer limits, loads the signing identity, clears stale cache files, and installs only the server-side connection hooks.
        // The server uses Valheim's existing ZRpc connection and never opens a separate synchronization listener.
        private void Awake()
        {
            _instance = this;
            _enabled = Config.Bind("General", "Enabled", true, "Enable the AutoModSync server role on this Valheim instance.");
            _excludePatterns = Config.Bind("General", "ExcludePatterns",
                "ValheimAutoModSync.Server.dll;ValheimAutoModSync.Client.dll;ValheimAutoModSync.Apply.exe;manifest.json;icon.png;README.md;CHANGELOG.md;LICENSE;THIRD-PARTY-NOTICES.md;*.pdb;*.mdb;*.log;*.tmp;*.bak;*.md",
                "Semicolon-separated wildcard patterns that will not be sent to clients. Match is checked against both the relative path and file name.");
            _manifestCacheSeconds = Config.Bind("General", "ManifestCacheSeconds", 5, "How long the server caches plugin hashes before rescanning BepInEx\\plugins.");
            _chunkBytes = Config.Bind("Transfer", "ChunkBytes", 24576, "Raw file bytes per RPC chunk before Base64 encoding. 24576 is conservative for Valheim's RPC transport.");
            _maxFileMiB = Config.Bind("Transfer", "MaxFileMiB", 128, "Refuse to transfer a single file larger than this many MiB.");
            _maxBundleMiB = Config.Bind("Transfer", "MaxBundleMiB", 2048, "Refuse to build a compressed change package larger than this many MiB.");

            try
            {
                LoadIdentity();
                CleanupOldBundleCache();
                Logger.LogInfo("AutoModSync uses Valheim's existing ZRpc connection; no additional listening port is opened.");
                Logger.LogInfo("AutoModSync server fingerprint: " + _publicFingerprint);
                new Harmony(PluginGuid).PatchAll(typeof(NetworkPatches));
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
                rpc.Register<ZPackage>(RpcManifestBegin, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcManifestChunk, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcManifestEnd, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcBundleBegin, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcBundleChunk, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcBundleEnd, new Action<ZRpc, ZPackage>(RPC_NoOp));
                rpc.Register<ZPackage>(RpcError, new Action<ZRpc, ZPackage>(RPC_NoOp));
                Registered.Add(rpc);
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
                int protocol = pkg.ReadInt();
                string clientVersion = "";
                try { clientVersion = pkg.ReadString(); } catch { clientVersion = ""; }
                ClientVersions[rpc] = clientVersion ?? "";
                if (protocol != ProtocolVersion)
                {
                    SendError(rpc, "AutoModSync protocol mismatch. Server=" + ProtocolVersion + " Client=" + protocol);
                    return;
                }

                ZPackage ack = new ZPackage();
                ack.Write(ProtocolVersion);
                ack.Write(PluginVersion);
                ack.Write("bundle-window1");
                rpc.Invoke(RpcAck, new object[] { ack });

                EnsureManifest(false);
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

        // Intent: Builds a compressed package containing exactly the manifest records requested by this client.
        // Security: every request is resolved against the current signed file map, duplicates are rejected, configured size limits are enforced, and only plugin-kind records can enter the archive.
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
                int i;
                for (i = 0; i < count; i++)
                {
                    string requested = pkg.ReadString();
                    FileRecord record = ResolveBundleRecord(requested);
                    string key = record.Kind + ":" + record.RelativePath;
                    if (!seen.Add(key)) throw new InvalidDataException("Duplicate file in AutoModSync bundle request.");
                    records.Add(record);
                }

                CleanupBundle(rpc);
                string cacheRoot = Path.Combine(Paths.BepInExRootPath, "AutoModSync", "cache");
                if (!Directory.Exists(cacheRoot)) Directory.CreateDirectory(cacheRoot);
                string zipPath = Path.Combine(cacheRoot, "bundle-" + Guid.NewGuid().ToString("N") + ".zip");

                using (FileStream output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, false))
                {
                    for (i = 0; i < records.Count; i++)
                    {
                        FileRecord record = records[i];
                        string entryName = "plugins/" + record.RelativePath.Replace('\\', '/');
                        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                        using (Stream entryStream = entry.Open())
                        using (FileStream input = new FileStream(record.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            input.CopyTo(entryStream);
                        }
                    }
                }

                FileInfo fi = new FileInfo(zipPath);
                long maxBundleBytes = (long)Math.Max(1, _maxBundleMiB.Value) * 1024L * 1024L;
                if (fi.Length > maxBundleBytes)
                {
                    try { File.Delete(zipPath); } catch { }
                    throw new InvalidDataException("Compressed AutoModSync package exceeds the configured server transfer limit.");
                }

                int rawChunk = Math.Max(4096, Math.Min(49152, _chunkBytes.Value));
                BundleTransfer transfer = new BundleTransfer();
                transfer.ZipPath = zipPath;
                transfer.Size = fi.Length;
                transfer.Sha256 = Sha256File(zipPath);
                transfer.ChunkBytes = rawChunk;
                transfer.TotalChunks = (int)((transfer.Size + rawChunk - 1L) / rawChunk);
                transfer.FileCount = records.Count;
                transfer.Stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                BundleTransfers[rpc] = transfer;

                ZPackage begin = new ZPackage();
                begin.Write(transfer.Size.ToString(CultureInfo.InvariantCulture));
                begin.Write(transfer.Sha256);
                begin.Write(transfer.TotalChunks);
                begin.Write(transfer.FileCount);
                rpc.Invoke(RpcBundleBegin, new object[] { begin });

                if (_instance != null) _instance.Logger.LogInfo("Prepared compressed AutoModSync package for " + records.Count + " changed file(s): " + FormatBytes(transfer.Size));
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Bundle preparation failed: " + ex);
                CleanupBundle(rpc);
                SendError(rpc, "Server failed while preparing the compressed AutoModSync package: " + ex.Message);
            }
        }

        // Intent: Serves one legacy chunk or a bounded 2.5 transfer window beginning at the requested chunk index, then emits the unchanged AMS4 completion message when the client requests TotalChunks.
        // Performance: keeps the prepared ZIP stream open for the transfer and sends up to 16 sequential chunks per request, removing most per-chunk RPC round trips and file open/seek operations while preserving ordered delivery.
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

                if (transfer.Stream == null) transfer.Stream = new FileStream(transfer.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                long expectedOffset = (long)index * transfer.ChunkBytes;
                if (transfer.Stream.Position != expectedOffset) transfer.Stream.Seek(expectedOffset, SeekOrigin.Begin);

                byte[] buffer = new byte[transfer.ChunkBytes];
                int sent;
                for (sent = 0; sent < requestedCount && index + sent < transfer.TotalChunks; sent++)
                {
                    int read = transfer.Stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) throw new EndOfStreamException("Unexpected end of compressed AutoModSync package.");

                    ZPackage chunk = new ZPackage();
                    chunk.Write(index + sent);
                    chunk.Write(Convert.ToBase64String(buffer, 0, read));
                    rpc.Invoke(RpcBundleChunk, new object[] { chunk });
                }
            }
            catch (Exception ex)
            {
                if (_instance != null) _instance.Logger.LogWarning("Bundle chunk transfer failed: " + ex);
                CleanupBundle(rpc);
                SendError(rpc, "Server failed while transferring the compressed AutoModSync package: " + ex.Message);
            }
        }

        // Intent: Resolves a client request string to one current FileRecord from the server's manifest map.
        // Security: validates kind/path, refreshes the manifest if necessary, checks existence, and enforces the per-file size limit before returning a source path.
        private static FileRecord ResolveBundleRecord(string requested)
        {
            if (String.IsNullOrEmpty(requested) || requested.Length < 3 || requested[1] != ':')
                throw new InvalidDataException("Invalid AutoModSync file request.");
            char kind = requested[0];
            if (kind != 'P') throw new InvalidDataException("Invalid AutoModSync file kind.");
            string relative = NormalizeRelative(requested.Substring(2));
            if (relative.Length == 0) throw new InvalidDataException("Invalid AutoModSync relative path.");
            string lookup = kind + ":" + relative;
            FileRecord record;
            if (!_files.TryGetValue(lookup, out record) || !File.Exists(record.FullPath))
            {
                EnsureManifest(true);
                if (!_files.TryGetValue(lookup, out record) || !File.Exists(record.FullPath))
                    throw new FileNotFoundException("Requested plugin file is not available: " + relative);
            }
            long maxBytes = (long)Math.Max(1, _maxFileMiB.Value) * 1024L * 1024L;
            if (record.Size > maxBytes) throw new InvalidDataException("File exceeds server transfer limit: " + relative);
            return record;
        }

        // Intent: Removes per-client bundle-transfer state and deletes the temporary ZIP; safe to call after success or any failure.
        private static void CleanupBundle(ZRpc rpc)
        {
            BundleTransfer transfer;
            if (!BundleTransfers.TryGetValue(rpc, out transfer)) return;
            BundleTransfers.Remove(rpc);
            if (transfer != null)
            {
                try { if (transfer.Stream != null) transfer.Stream.Dispose(); } catch { }
                transfer.Stream = null;
                if (!String.IsNullOrEmpty(transfer.ZipPath))
                {
                    try { if (File.Exists(transfer.ZipPath)) File.Delete(transfer.ZipPath); } catch { }
                }
            }
        }

        // Intent: Deletes abandoned AutoModSync bundle ZIPs older than six hours so interrupted transfers do not accumulate indefinitely.
        private static void CleanupOldBundleCache()
        {
            try
            {
                string cacheRoot = Path.Combine(Paths.BepInExRootPath, "AutoModSync", "cache");
                if (!Directory.Exists(cacheRoot)) return;
                string[] files = Directory.GetFiles(cacheRoot, "bundle-*.zip", SearchOption.TopDirectoryOnly);
                int i;
                for (i = 0; i < files.Length; i++)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(files[i]);
                        if ((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalHours > 6.0) fi.Delete();
                    }
                    catch { }
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

                string root = Paths.PluginPath;
                List<FileRecord> records = new List<FileRecord>();
                if (Directory.Exists(root))
                {
                    string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                    int i;
                    for (i = 0; i < files.Length; i++)
                    {
                        string full = files[i];
                        string rel = MakeRelative(root, full).Replace('\\', '/');
                        if (IsExcluded(rel, Path.GetFileName(full))) continue;
                        FileInfo fi = new FileInfo(full);
                        FileRecord r = new FileRecord();
                        r.Kind = 'P';
                        r.RelativePath = rel;
                        r.FullPath = full;
                        r.Size = fi.Length;
                        r.Sha256 = Sha256File(full);
                        records.Add(r);
                    }
                }

                // 2.4.5+: no packed game-root bootstrap is distributed or synchronized.

                string releaseClientPlugin = Path.Combine(Paths.BepInExRootPath, "AutoModSync", "release", "ValheimAutoModSync.Client.dll");
                if (!IsPackageManagedAutoModSync() && File.Exists(releaseClientPlugin))
                {
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

                records.Sort(delegate(FileRecord a, FileRecord b) { return StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath); });
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
            string raw = _excludePatterns == null ? "" : (_excludePatterns.Value ?? "");
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
            if (value == null) return "";
            value = value.Replace('\\', '/').TrimStart('/');
            if (value.IndexOf("../", StringComparison.Ordinal) >= 0 || value == ".." || value.IndexOf(':') >= 0) return "";
            return value;
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
