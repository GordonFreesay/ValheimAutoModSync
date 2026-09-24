using System;
using System.IO;

namespace ValheimAutoModSync
{
    // Intent: Centralizes server-side resource ceilings that must be enforced before or during ZIP construction.
    // Security: arithmetic is overflow-safe and failed private build artifacts are removable without publishing them.
    internal static class AutoModSyncServerResourceSafety
    {
        // Intent: Adds one signed source file to a requested expanded-byte total without crossing the configured ceiling.
        internal static long AddExpandedSource(long fileSize, long expandedBefore, long maxExpandedBytes, string errorMessage)
        {
            if (fileSize < 0 || expandedBefore < 0 || maxExpandedBytes < 1 || fileSize > maxExpandedBytes - expandedBefore)
                throw new InvalidDataException(errorMessage ?? "AutoModSync expanded content exceeds the configured transfer limit.");
            return expandedBefore + fileSize;
        }

        // Intent: Rejects compressed output as soon as the observable ZIP length exceeds the configured server ceiling.
        internal static void EnsureCompressedWithinLimit(long compressedBytes, long maxBundleBytes)
        {
            if (compressedBytes < 0 || maxBundleBytes < 1 || compressedBytes > maxBundleBytes)
                throw new InvalidDataException("Compressed AutoModSync package exceeds the configured server transfer limit.");
        }

        // Intent: Removes an unpublished private bundle-build temporary file after any failed construction attempt.
        internal static void DeleteUnpublishedTemp(string tempPath)
        {
            if (String.IsNullOrEmpty(tempPath)) return;
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { }
        }
    }
}
