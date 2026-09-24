using System;
using System.Globalization;
using System.IO;

namespace ValheimAutoModSync
{
    // Intent: Centralizes client-side hard resource ceilings and pure validation used before or during bundle writes.
    // Security: these limits are independent of server configuration so a trusted/misconfigured server cannot force
    // unbounded file counts, compressed bytes, expanded bytes, chunk counts, or malformed content identifiers.
    internal static class AutoModSyncClientResourceSafety
    {
        internal const int MaxBundleFiles = 4096;
        internal const int MaxBundleChunks = 524288;
        internal const long MaxIncomingBundleBytes = 2048L * 1024L * 1024L;
        internal const long MaxExpandedSyncBytes = 4096L * 1024L * 1024L;
        internal const long MaxIndividualSyncFileBytes = 512L * 1024L * 1024L;

        // Intent: Validates one required signed-manifest file against the hard per-file and cumulative expanded-byte ceilings.
        internal static long AddRequiredFile(long size, long expandedBefore, string relativePath)
        {
            if (size < 0 || size > MaxIndividualSyncFileBytes)
                throw new InvalidDataException("Required file exceeds the AutoModSync client hard limit: " + (relativePath ?? ""));
            if (expandedBefore < 0 || expandedBefore > MaxExpandedSyncBytes - size)
                throw new InvalidDataException("Required synchronized content exceeds the AutoModSync client expanded-size limit.");
            return expandedBefore + size;
        }

        // Intent: Rejects a required-file count that would exceed the bounded bundle manifest capacity.
        internal static void ValidateRequiredFileCount(int count)
        {
            if (count < 0 || count > MaxBundleFiles)
                throw new InvalidDataException("Required synchronized file count exceeds the AutoModSync client hard limit.");
        }

        // Intent: Validates a server bundle header before the client opens or writes the staging archive.
        internal static long ValidateBundleHeader(
            string sizeText,
            string sha256,
            int chunks,
            int files,
            int expectedFiles,
            bool supportsResume,
            int chunkBytes,
            int resumeStartChunk,
            out int normalizedChunkBytes)
        {
            long size;
            if (!Int64.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out size)
                || size <= 0
                || size > MaxIncomingBundleBytes
                || chunks < 1
                || chunks > MaxBundleChunks
                || files < 1
                || files > MaxBundleFiles
                || files != expectedFiles
                || !IsSha256Hex(sha256))
                throw new InvalidDataException("Compressed package header exceeded AutoModSync client safety limits or did not match the requested sync.");

            if (supportsResume)
            {
                if (chunkBytes < 4096 || chunkBytes > 65536
                    || (size + chunkBytes - 1L) / chunkBytes != chunks
                    || resumeStartChunk < 0 || resumeStartChunk > chunks)
                    throw new InvalidDataException("Compressed package resume header contained invalid chunk geometry.");
                normalizedChunkBytes = chunkBytes;
            }
            else
            {
                normalizedChunkBytes = (int)Math.Max(1L, (size + chunks - 1L) / chunks);
            }

            return size;
        }

        // Intent: Validates one decoded incoming chunk before any bytes are appended to the staging archive.
        internal static void ValidateIncomingChunk(
            int index,
            int length,
            long bytesReceived,
            long bundleSize,
            int totalChunks,
            bool supportsResume,
            int chunkBytes)
        {
            if (length < 1 || length > 65536)
                throw new InvalidDataException("Invalid compressed package chunk length.");

            if (supportsResume)
            {
                if (chunkBytes < 1 || index < 0 || index >= totalChunks)
                    throw new InvalidDataException("Compressed package chunk geometry is not initialized.");

                long offset = (long)index * chunkBytes;
                int expected = (int)Math.Min((long)chunkBytes, bundleSize - offset);
                if (expected <= 0 || length != expected)
                    throw new InvalidDataException("Compressed package chunk length did not match the resumable bundle geometry.");
            }

            if (bytesReceived < 0 || bundleSize < 0 || bytesReceived > bundleSize || (long)length > bundleSize - bytesReceived)
                throw new InvalidDataException("Compressed package exceeded its declared size.");
        }

        // Intent: Copies one ZIP entry while enforcing its signed size and the global expanded-byte ceiling continuously.
        internal static void CopyZipEntryBounded(Stream source, Stream destination, long expectedBytes, ref long cumulativeExpandedBytes)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (destination == null) throw new ArgumentNullException("destination");
            if (expectedBytes < 0 || expectedBytes > MaxIndividualSyncFileBytes)
                throw new InvalidDataException("Compressed package entry exceeds the AutoModSync client file limit.");

            byte[] buffer = new byte[81920];
            long written = 0L;
            while (true)
            {
                int read = source.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                if ((long)read > expectedBytes - written)
                    throw new InvalidDataException("Compressed package entry expanded beyond its signed size.");
                if (cumulativeExpandedBytes < 0 || (long)read > MaxExpandedSyncBytes - cumulativeExpandedBytes)
                    throw new InvalidDataException("Compressed package exceeded the AutoModSync client expanded-size limit.");

                destination.Write(buffer, 0, read);
                written += read;
                cumulativeExpandedBytes += read;
            }

            if (written != expectedBytes)
                throw new InvalidDataException("Compressed package entry ended before its signed size.");
        }

        // Intent: Writes one extracted ZIP entry to staging, verifies its signed digest/size, and removes any partial file on failure.
        // Security: callers may add the destination to pending apply state only after this method returns successfully.
        internal static void WriteVerifiedExtractedEntry(
            Stream source,
            string outputPath,
            long expectedBytes,
            string expectedSha256,
            ref long cumulativeExpandedBytes)
        {
            if (String.IsNullOrEmpty(outputPath)) throw new ArgumentException("outputPath");
            if (!IsSha256Hex(expectedSha256)) throw new InvalidDataException("Unpacked file expected SHA-256 is invalid.");

            try
            {
                using (FileStream destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    CopyZipEntryBounded(source, destination, expectedBytes, ref cumulativeExpandedBytes);

                FileInfo outInfo = new FileInfo(outputPath);
                if (!outInfo.Exists || outInfo.Length != expectedBytes || !String.Equals(Sha256File(outputPath), expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unpacked file failed signed-manifest verification.");
            }
            catch
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                throw;
            }
        }

        // Intent: Computes the SHA-256 of one extracted staging file for signed-manifest verification.
        private static string Sha256File(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                char[] chars = new char[hash.Length * 2];
                const string hex = "0123456789abcdef";
                int i;
                for (i = 0; i < hash.Length; i++)
                {
                    chars[i * 2] = hex[hash[i] >> 4];
                    chars[(i * 2) + 1] = hex[hash[i] & 15];
                }
                return new string(chars);
            }
        }

        // Intent: Validates protocol SHA-256 text before it is trusted as a content identifier.
        internal static bool IsSha256Hex(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64) return false;
            int i;
            for (i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}
