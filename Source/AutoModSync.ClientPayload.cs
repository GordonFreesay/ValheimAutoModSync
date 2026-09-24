using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ValheimAutoModSync
{
    // Intent: One recursively discovered file from the dedicated server's fixed client-only plugin payload root.
    internal sealed class AutoModSyncClientPayloadFile
    {
        internal string RelativePath = "";
        internal string FullPath = "";
        internal long Size;
        internal string Sha256 = "";
    }

    // Intent: Shared/testable filesystem logic for Phase 6 client-only plugin payload packaging and destination-collision rejection.
    // Trust boundary: the caller supplies one fixed local root; paths are normalized/reparse-checked beneath that root and never choose a client destination kind.
    internal static class AutoModSyncClientPayload
    {
        // Intent: Recursively discovers safe client-only payload files, applying the server's existing exclusion policy through the supplied predicate.
        // Server behavior: callers map every returned relative path to the existing signed P:<relative> destination; no new wire kind is created.
        internal static List<AutoModSyncClientPayloadFile> CollectPluginFiles(string root, Func<string, string, bool> isExcluded)
        {
            List<AutoModSyncClientPayloadFile> result = new List<AutoModSyncClientPayloadFile>();
            if (String.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            string rootFull = Path.GetFullPath(root);
            AutoModSyncPathSafety.EnsureNoReparsePoints(rootFull, rootFull, true);

            Stack<string> pending = new Stack<string>();
            pending.Push(rootFull);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                AutoModSyncPathSafety.EnsureNoReparsePoints(rootFull, current, true);

                string[] files = Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly);
                int i;
                for (i = 0; i < files.Length; i++)
                {
                    if (AutoModSyncPathSafety.IsReparsePoint(files[i])) continue;

                    string full = Path.GetFullPath(files[i]);
                    string rel = AutoModSyncPathSafety.NormalizeRelative(MakeRelative(rootFull, full));
                    if (rel.Length == 0) throw new InvalidDataException("AutoModSync ClientPayload contained an unsafe path.");
                    string name = Path.GetFileName(full);
                    if (isExcluded != null && isExcluded(rel, name)) continue;

                    full = AutoModSyncPathSafety.SafeUnderRoot(rootFull, rel, true);
                    FileInfo info = new FileInfo(full);
                    AutoModSyncClientPayloadFile item = new AutoModSyncClientPayloadFile();
                    item.RelativePath = rel;
                    item.FullPath = full;
                    item.Size = info.Length;
                    item.Sha256 = Sha256File(full);
                    result.Add(item);
                }

                string[] directories = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly);
                for (i = 0; i < directories.Length; i++)
                {
                    if (AutoModSyncPathSafety.IsReparsePoint(directories[i])) continue;
                    string directory = Path.GetFullPath(directories[i]);
                    AutoModSyncPathSafety.EnsureNoReparsePoints(rootFull, directory, true);
                    pending.Push(directory);
                }
            }

            result.Sort(delegate(AutoModSyncClientPayloadFile a, AutoModSyncClientPayloadFile b)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
            });
            return result;
        }

        // Intent: Registers one final manifest destination case-insensitively and fails explicitly instead of allowing one source tree to shadow another.
        internal static void RegisterUniqueDestination(IDictionary<string, string> sources, char kind, string relativePath, string sourceLabel)
        {
            if (sources == null) throw new ArgumentNullException("sources");
            string rel = AutoModSyncPathSafety.NormalizeRelative(relativePath);
            if (rel.Length == 0) throw new InvalidDataException("Unsafe AutoModSync manifest destination.");

            string key = kind + ":" + rel;
            string existing;
            if (sources.TryGetValue(key, out existing))
                throw new InvalidDataException("AutoModSync manifest destination collision for " + key +
                    " between " + (existing ?? "unknown source") + " and " + (sourceLabel ?? "unknown source") + ".");
            sources.Add(key, sourceLabel ?? "");
        }

        // Intent: Computes a path relative to one already-fixed local payload root without relying on URI/shell behavior.
        private static string MakeRelative(string root, string fullPath)
        {
            string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(fullPath);
            string prefix = basePath + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AutoModSync ClientPayload file escaped its fixed source root.");
            return full.Substring(prefix.Length);
        }

        // Intent: Hashes the exact client-only payload bytes before they enter the signed manifest.
        private static string Sha256File(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                int i;
                for (i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
