using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ValheimAutoModSync
{
    // Intent: Wire/disk representation of one resumable immutable bundle prefix.
    // Security: this object never carries a filesystem destination; it identifies only an already-trusted bundle artifact and a verified prefix boundary.
    internal sealed class AutoModSyncResumeCandidate
    {
        internal string BundleSha256 = "";
        internal long BundleSize;
        internal int ChunkBytes;
        internal int TotalChunks;
        internal int FileCount;
        internal int NextChunk;
        internal string PrefixSha256 = "";
    }

    // Intent: Owns the client's single bounded partial-bundle slot and the server-side exact-prefix validation used by Phase 4 resume.
    // Design: resume is allowed only when server identity, requested signed file set, exact bundle SHA/size/chunk geometry, and prefix SHA-256 all match.
    // A mismatched/corrupt/stale candidate is discarded or rejected and transfer restarts from chunk zero; final full-bundle SHA-256 remains authoritative.
    internal static class AutoModSyncResumeState
    {
        private const string MetadataVersion = "AMSRESUME1";
        internal const long DefaultMaxAgeSeconds = 24L * 60L * 60L;

        // Intent: Produces a stable identity for the exact signed change set requested from one trusted server.
        // Canonical records are sorted so harmless request ordering differences cannot create a false mismatch.
        internal static string ComputeRequestKey(string serverFingerprint, IList<string> canonicalRecords)
        {
            if (!IsSha256(serverFingerprint)) throw new InvalidDataException("Invalid AutoModSync resume server fingerprint.");
            List<string> rows = new List<string>();
            if (canonicalRecords != null)
            {
                int i;
                for (i = 0; i < canonicalRecords.Count; i++) rows.Add(canonicalRecords[i] ?? "");
            }
            rows.Sort(StringComparer.Ordinal);

            StringBuilder sb = new StringBuilder();
            sb.Append("ams4-resume-request-v1\n");
            sb.Append(serverFingerprint.ToLowerInvariant()).Append('\n');
            int j;
            for (j = 0; j < rows.Count; j++) sb.Append(rows[j]).Append('\n');
            return Sha256Bytes(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // Intent: Examines the bounded client partial slot before a bundle request and returns only a complete-chunk prefix.
        // Crash safety: an interrupted final write is rounded down to the prior full chunk boundary before its prefix hash is offered to the server.
        internal static bool TryPrepareClientCandidate(
            string amsRoot,
            string serverFingerprint,
            string requestKey,
            long maxAgeSeconds,
            out AutoModSyncResumeCandidate candidate,
            out string reason)
        {
            candidate = null;
            reason = "";
            try
            {
                ResumeMetadata metadata;
                if (!TryReadMetadata(amsRoot, out metadata, out reason))
                {
                    // Invalid/orphaned resume state is never useful on a later attempt. Discard is reparse-safe and
                    // deliberately leaves an unsafe local junction/symlink untouched rather than following it.
                    try
                    {
                        if (File.Exists(GetMetadataPath(amsRoot)) || File.Exists(GetPartialPath(amsRoot)))
                            Discard(amsRoot);
                    }
                    catch { }
                    return false;
                }
                if (!ConstantEquals(metadata.ServerFingerprint, serverFingerprint) || !ConstantEquals(metadata.RequestKey, requestKey))
                {
                    reason = "saved partial belongs to a different trusted server or signed change set";
                    Discard(amsRoot);
                    return false;
                }

                long ageSeconds = Math.Max(0L, (DateTime.UtcNow.Ticks - metadata.UpdatedUtcTicks) / TimeSpan.TicksPerSecond);
                if (maxAgeSeconds >= 0 && ageSeconds > maxAgeSeconds)
                {
                    reason = "saved partial exceeded the resume age limit";
                    Discard(amsRoot);
                    return false;
                }

                string partial = GetPartialPath(amsRoot);
                if (!File.Exists(partial))
                {
                    reason = "resume metadata exists without its partial bundle";
                    Discard(amsRoot);
                    return false;
                }

                long length = new FileInfo(partial).Length;
                if (length < 0 || length > metadata.BundleSize)
                {
                    reason = "saved partial length is outside the advertised bundle";
                    Discard(amsRoot);
                    return false;
                }

                int nextChunk;
                long resumeBytes;
                if (length == metadata.BundleSize)
                {
                    nextChunk = metadata.TotalChunks;
                    resumeBytes = metadata.BundleSize;
                }
                else
                {
                    nextChunk = (int)(length / metadata.ChunkBytes);
                    resumeBytes = (long)nextChunk * metadata.ChunkBytes;
                    if (nextChunk <= 0)
                    {
                        reason = "saved partial does not contain one complete chunk";
                        Discard(amsRoot);
                        return false;
                    }
                    if (nextChunk >= metadata.TotalChunks)
                    {
                        reason = "saved partial chunk geometry is invalid";
                        Discard(amsRoot);
                        return false;
                    }

                    if (length != resumeBytes)
                    {
                        using (FileStream trim = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None))
                        {
                            trim.SetLength(resumeBytes);
                            trim.Flush(true);
                        }
                    }
                }

                AutoModSyncPathSafety.EnsureNoReparsePoints(GetResumeRoot(amsRoot), partial, true);
                string prefixSha = Sha256Prefix(partial, resumeBytes);

                candidate = new AutoModSyncResumeCandidate();
                candidate.BundleSha256 = metadata.BundleSha256;
                candidate.BundleSize = metadata.BundleSize;
                candidate.ChunkBytes = metadata.ChunkBytes;
                candidate.TotalChunks = metadata.TotalChunks;
                candidate.FileCount = metadata.FileCount;
                candidate.NextChunk = nextChunk;
                candidate.PrefixSha256 = prefixSha;
                reason = "resume candidate ready";
                return true;
            }
            catch (Exception ex)
            {
                reason = "saved partial could not be validated: " + ex.Message;
                try { Discard(amsRoot); } catch { }
                candidate = null;
                return false;
            }
        }

        // Intent: Creates a fresh bounded client partial after the server has published the exact bundle header.
        // Durability: metadata and an empty file are both present before network bytes are appended; a crash between them only loses resume optimization, never verification.
        internal static FileStream CreateFreshClientPartial(
            string amsRoot,
            string serverFingerprint,
            string requestKey,
            string bundleSha256,
            long bundleSize,
            int chunkBytes,
            int totalChunks,
            int fileCount,
            out string partialPath)
        {
            ValidateIdentity(serverFingerprint, requestKey, bundleSha256, bundleSize, chunkBytes, totalChunks, fileCount);
            Discard(amsRoot);
            string resumeRoot = GetResumeRoot(amsRoot);
            Directory.CreateDirectory(resumeRoot);
            AutoModSyncPathSafety.EnsureNoReparsePoints(amsRoot, resumeRoot, true);

            partialPath = GetPartialPath(amsRoot);
            using (FileStream empty = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                empty.Flush(true);
            }

            ResumeMetadata metadata = new ResumeMetadata();
            metadata.ServerFingerprint = serverFingerprint.ToLowerInvariant();
            metadata.RequestKey = requestKey.ToLowerInvariant();
            metadata.BundleSha256 = bundleSha256.ToLowerInvariant();
            metadata.BundleSize = bundleSize;
            metadata.ChunkBytes = chunkBytes;
            metadata.TotalChunks = totalChunks;
            metadata.FileCount = fileCount;
            metadata.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            WriteMetadataDurable(amsRoot, metadata);

            return new FileStream(partialPath, FileMode.Open, FileAccess.Write, FileShare.None);
        }

        // Intent: Opens a server-accepted prefix only after rechecking disk metadata, byte boundary, and prefix SHA-256 immediately before append.
        internal static bool TryOpenAcceptedClientPartial(
            string amsRoot,
            string serverFingerprint,
            string requestKey,
            AutoModSyncResumeCandidate accepted,
            out FileStream stream,
            out string partialPath,
            out long resumeBytes,
            out string reason)
        {
            stream = null;
            partialPath = "";
            resumeBytes = 0L;
            reason = "";
            try
            {
                if (accepted == null || accepted.NextChunk <= 0) { reason = "server did not accept a resumable prefix"; return false; }

                ResumeMetadata metadata;
                if (!TryReadMetadata(amsRoot, out metadata, out reason)) return false;
                ValidateIdentity(serverFingerprint, requestKey, accepted.BundleSha256, accepted.BundleSize, accepted.ChunkBytes, accepted.TotalChunks, accepted.FileCount);

                if (!ConstantEquals(metadata.ServerFingerprint, serverFingerprint)
                    || !ConstantEquals(metadata.RequestKey, requestKey)
                    || !ConstantEquals(metadata.BundleSha256, accepted.BundleSha256)
                    || metadata.BundleSize != accepted.BundleSize
                    || metadata.ChunkBytes != accepted.ChunkBytes
                    || metadata.TotalChunks != accepted.TotalChunks
                    || metadata.FileCount != accepted.FileCount)
                {
                    reason = "server-accepted resume header no longer matches local metadata";
                    return false;
                }

                resumeBytes = accepted.NextChunk == accepted.TotalChunks
                    ? accepted.BundleSize
                    : (long)accepted.NextChunk * accepted.ChunkBytes;
                if (resumeBytes <= 0 || resumeBytes > accepted.BundleSize)
                {
                    reason = "server-accepted resume offset is invalid";
                    return false;
                }

                partialPath = GetPartialPath(amsRoot);
                if (!File.Exists(partialPath) || new FileInfo(partialPath).Length != resumeBytes)
                {
                    reason = "local partial length changed after the resume offer";
                    return false;
                }

                AutoModSyncPathSafety.EnsureNoReparsePoints(GetResumeRoot(amsRoot), partialPath, true);
                string prefixSha = Sha256Prefix(partialPath, resumeBytes);
                if (!ConstantEquals(prefixSha, accepted.PrefixSha256))
                {
                    reason = "local partial prefix changed after the resume offer";
                    return false;
                }

                stream = new FileStream(partialPath, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.Seek(resumeBytes, SeekOrigin.Begin);
                return true;
            }
            catch (Exception ex)
            {
                reason = "accepted partial could not be opened: " + ex.Message;
                try { if (stream != null) stream.Dispose(); } catch { }
                stream = null;
                return false;
            }
        }

        // Intent: Server-side proof that a client prefix belongs byte-for-byte to this exact immutable artifact.
        // The client cannot choose an arbitrary offset: the candidate must match bundle geometry and the server hashes the corresponding artifact prefix itself.
        internal static bool TryAcceptServerCandidate(
            string artifactPath,
            string artifactSha256,
            long artifactSize,
            int chunkBytes,
            int totalChunks,
            int fileCount,
            AutoModSyncResumeCandidate candidate,
            out long resumeBytes,
            out string reason)
        {
            resumeBytes = 0L;
            reason = "";
            try
            {
                if (candidate == null) { reason = "no resume candidate"; return false; }
                if (!File.Exists(artifactPath)) { reason = "bundle artifact is unavailable"; return false; }
                if (!IsSha256(candidate.BundleSha256) || !IsSha256(candidate.PrefixSha256))
                {
                    reason = "resume candidate contained an invalid SHA-256 value";
                    return false;
                }
                if (!ConstantEquals(candidate.BundleSha256, artifactSha256)
                    || candidate.BundleSize != artifactSize
                    || candidate.ChunkBytes != chunkBytes
                    || candidate.TotalChunks != totalChunks
                    || candidate.FileCount != fileCount)
                {
                    reason = "resume candidate does not identify the current immutable bundle";
                    return false;
                }
                if (candidate.NextChunk <= 0 || candidate.NextChunk > totalChunks)
                {
                    reason = "resume candidate chunk boundary is invalid";
                    return false;
                }

                resumeBytes = candidate.NextChunk == totalChunks
                    ? artifactSize
                    : (long)candidate.NextChunk * chunkBytes;
                if (resumeBytes <= 0 || resumeBytes > artifactSize)
                {
                    reason = "resume candidate byte boundary is invalid";
                    resumeBytes = 0L;
                    return false;
                }

                string serverPrefix = Sha256Prefix(artifactPath, resumeBytes);
                if (!ConstantEquals(serverPrefix, candidate.PrefixSha256))
                {
                    reason = "resume candidate prefix SHA-256 did not match the current immutable bundle";
                    resumeBytes = 0L;
                    return false;
                }

                reason = "exact artifact prefix verified";
                return true;
            }
            catch (Exception ex)
            {
                reason = "resume candidate validation failed: " + ex.Message;
                resumeBytes = 0L;
                return false;
            }
        }

        // Intent: Deletes only AutoModSync's bounded resume slot. It never touches extracted staging or live BepInEx files.
        // Security: an existing resume directory is reparse-checked before any deletion so a local junction/symlink cannot redirect cleanup outside AutoModSync.
        internal static void Discard(string amsRoot)
        {
            string resumeRoot = GetResumeRoot(amsRoot);
            if (!Directory.Exists(resumeRoot)) return;

            try
            {
                AutoModSyncPathSafety.EnsureNoReparsePoints(amsRoot, resumeRoot, true);
            }
            catch
            {
                // Fail closed on cleanup: leaving an unsafe local object behind is preferable to deleting through it.
                return;
            }

            try
            {
                string partial = GetPartialPath(amsRoot);
                AutoModSyncPathSafety.EnsureNoReparsePoints(resumeRoot, partial, true);
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch { }
            try
            {
                string metadata = GetMetadataPath(amsRoot);
                AutoModSyncPathSafety.EnsureNoReparsePoints(resumeRoot, metadata, true);
                if (File.Exists(metadata)) File.Delete(metadata);
            }
            catch { }
            try
            {
                string temp = GetMetadataPath(amsRoot) + ".tmp";
                AutoModSyncPathSafety.EnsureNoReparsePoints(resumeRoot, temp, true);
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch { }
            try
            {
                if (Directory.GetFileSystemEntries(resumeRoot).Length == 0)
                    Directory.Delete(resumeRoot);
            }
            catch { }
        }

        // Intent: Returns the fixed client partial-bundle path inside AutoModSync's private resume directory.
        internal static string GetPartialPath(string amsRoot)
        {
            return Path.Combine(GetResumeRoot(amsRoot), "bundle.partial");
        }

        // Intent: Returns the fixed client resume-metadata path inside AutoModSync's private resume directory.
        internal static string GetMetadataPath(string amsRoot)
        {
            return Path.Combine(GetResumeRoot(amsRoot), "bundle.meta");
        }

        // Intent: Resolves the helper-owned resume directory directly beneath the supplied AutoModSync root without accepting a caller-selected relative destination.
        private static string GetResumeRoot(string amsRoot)
        {
            if (String.IsNullOrEmpty(amsRoot)) throw new ArgumentException("AutoModSync root is required.");
            string root = Path.GetFullPath(amsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string resume = Path.GetFullPath(Path.Combine(root, "resume"));
            string prefix = root + Path.DirectorySeparatorChar;
            if (!resume.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AutoModSync resume path escaped its fixed root.");
            return resume;
        }

        private sealed class ResumeMetadata
        {
            internal string ServerFingerprint = "";
            internal string RequestKey = "";
            internal string BundleSha256 = "";
            internal long BundleSize;
            internal int ChunkBytes;
            internal int TotalChunks;
            internal int FileCount;
            internal long UpdatedUtcTicks;
        }

        // Intent: Strictly parses the versioned resume metadata and rejects inconsistent SHA identities, sizes, timestamps, or chunk geometry.
        private static bool TryReadMetadata(string amsRoot, out ResumeMetadata metadata, out string reason)
        {
            metadata = null;
            reason = "";
            string path = GetMetadataPath(amsRoot);
            if (!File.Exists(path)) { reason = "no saved resume metadata"; return false; }

            try
            {
                string resumeRoot = GetResumeRoot(amsRoot);
                AutoModSyncPathSafety.EnsureNoReparsePoints(amsRoot, resumeRoot, true);
                AutoModSyncPathSafety.EnsureNoReparsePoints(resumeRoot, path, true);

                string[] lines = File.ReadAllLines(path);
                if (lines.Length != 9 || !String.Equals(lines[0], MetadataVersion, StringComparison.Ordinal))
                    throw new InvalidDataException("AutoModSync resume metadata version/header is invalid.");

                ResumeMetadata m = new ResumeMetadata();
                m.ServerFingerprint = lines[1] ?? "";
                m.RequestKey = lines[2] ?? "";
                m.BundleSha256 = lines[3] ?? "";
                if (!IsSha256(m.ServerFingerprint) || !IsSha256(m.RequestKey) || !IsSha256(m.BundleSha256))
                    throw new InvalidDataException("AutoModSync resume metadata contains an invalid SHA-256 identity.");
                if (!Int64.TryParse(lines[4], NumberStyles.None, CultureInfo.InvariantCulture, out m.BundleSize) || m.BundleSize <= 0)
                    throw new InvalidDataException("AutoModSync resume metadata contains an invalid bundle size.");
                if (!Int32.TryParse(lines[5], NumberStyles.None, CultureInfo.InvariantCulture, out m.ChunkBytes) || m.ChunkBytes < 1)
                    throw new InvalidDataException("AutoModSync resume metadata contains an invalid chunk size.");
                if (!Int32.TryParse(lines[6], NumberStyles.None, CultureInfo.InvariantCulture, out m.TotalChunks) || m.TotalChunks < 1)
                    throw new InvalidDataException("AutoModSync resume metadata contains an invalid chunk count.");
                if (!Int32.TryParse(lines[7], NumberStyles.None, CultureInfo.InvariantCulture, out m.FileCount) || m.FileCount < 1)
                    throw new InvalidDataException("AutoModSync resume metadata contains an invalid file count.");
                if (!Int64.TryParse(lines[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out m.UpdatedUtcTicks)
                    || m.UpdatedUtcTicks <= 0 || m.UpdatedUtcTicks > DateTime.MaxValue.Ticks)
                    throw new InvalidDataException("AutoModSync resume metadata contains an invalid timestamp.");

                long expectedChunks = (m.BundleSize + m.ChunkBytes - 1L) / m.ChunkBytes;
                if (expectedChunks != m.TotalChunks)
                    throw new InvalidDataException("AutoModSync resume metadata chunk geometry is inconsistent.");

                metadata = m;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        // Intent: Publishes resume metadata through a write-through temporary file so a crash cannot create a trusted half-written record.
        private static void WriteMetadataDurable(string amsRoot, ResumeMetadata metadata)
        {
            string resumeRoot = GetResumeRoot(amsRoot);
            Directory.CreateDirectory(resumeRoot);
            AutoModSyncPathSafety.EnsureNoReparsePoints(amsRoot, resumeRoot, true);
            string path = GetMetadataPath(amsRoot);
            string temp = path + ".tmp";
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(MetadataVersion);
            sb.AppendLine(metadata.ServerFingerprint ?? "");
            sb.AppendLine(metadata.RequestKey ?? "");
            sb.AppendLine(metadata.BundleSha256 ?? "");
            sb.AppendLine(metadata.BundleSize.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(metadata.ChunkBytes.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(metadata.TotalChunks.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(metadata.FileCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(metadata.UpdatedUtcTicks.ToString(CultureInfo.InvariantCulture));

            using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
            {
                writer.Write(sb.ToString());
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, null, true);
                    return;
                }
                catch (PlatformNotSupportedException) { }
                catch (NotSupportedException) { }
                catch (IOException) { }
                File.Delete(path);
            }
            File.Move(temp, path);
        }

        // Intent: Validates the complete immutable-bundle identity and chunk geometry before resume state can be created or reopened.
        private static void ValidateIdentity(string serverFingerprint, string requestKey, string bundleSha256, long bundleSize, int chunkBytes, int totalChunks, int fileCount)
        {
            if (!IsSha256(serverFingerprint) || !IsSha256(requestKey) || !IsSha256(bundleSha256))
                throw new InvalidDataException("AutoModSync resume identity is invalid.");
            if (bundleSize <= 0 || chunkBytes < 1 || totalChunks < 1 || fileCount < 1)
                throw new InvalidDataException("AutoModSync resume bundle geometry is invalid.");
            if ((bundleSize + chunkBytes - 1L) / chunkBytes != totalChunks)
                throw new InvalidDataException("AutoModSync resume bundle chunk geometry is inconsistent.");
        }

        // Intent: Hashes exactly the requested prefix bytes and fails if the saved/artifact file ends before that resume boundary.
        private static string Sha256Prefix(string path, long bytes)
        {
            if (bytes <= 0) throw new InvalidDataException("AutoModSync resume prefix must contain bytes.");
            byte[] buffer = new byte[131072];
            long remaining = bytes;
            using (SHA256 sha = SHA256.Create())
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                while (remaining > 0)
                {
                    int want = (int)Math.Min((long)buffer.Length, remaining);
                    int read = input.Read(buffer, 0, want);
                    if (read <= 0) throw new EndOfStreamException("AutoModSync resume prefix ended early.");
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                    remaining -= read;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return ToHex(sha.Hash);
            }
        }

        // Intent: Produces a deterministic SHA-256 identity for canonical resume request metadata.
        private static string Sha256Bytes(byte[] data)
        {
            using (SHA256 sha = SHA256.Create()) return ToHex(sha.ComputeHash(data ?? new byte[0]));
        }

        // Intent: Accepts only fixed-length hexadecimal SHA-256 text before any resume identity comparison.
        private static bool IsSha256(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64) return false;
            int i;
            for (i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool digit = c >= '0' && c <= '9';
                bool lower = c >= 'a' && c <= 'f';
                bool upper = c >= 'A' && c <= 'F';
                if (!digit && !lower && !upper) return false;
            }
            return true;
        }

        // Intent: Compares fixed-length hexadecimal identities without early exit and without case sensitivity.
        private static bool ConstantEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            int i;
            for (i = 0; i < a.Length; i++) diff |= Char.ToLowerInvariant(a[i]) ^ Char.ToLowerInvariant(b[i]);
            return diff == 0;
        }

        // Intent: Converts digest bytes to canonical lowercase hexadecimal for persisted/wire resume identities.
        private static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            int i;
            for (i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
