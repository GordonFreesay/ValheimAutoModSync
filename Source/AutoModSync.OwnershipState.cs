using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValheimAutoModSync
{
    // Intent: One path/digest that AutoModSync may later retire only for the trusted server fingerprint whose successful transaction created ownership.
    internal sealed class AutoModSyncOwnershipEntry
    {
        internal char Kind;
        internal string RelativePath = "";
        internal long Size;
        internal string Sha256 = "";
    }

    // Intent: Persists per-trusted-server ownership of files actually installed by AutoModSync so later signed manifests can retire only those files.
    // Trust boundary: absence from a new signed manifest authorizes deletion only when the same server fingerprint previously acquired ownership
    // and the live file still matches the exact last-owned digest. Matching pre-existing/local files are never claimed merely because a manifest lists them.
    internal static class AutoModSyncOwnershipState
    {
        private const string Version = "AMSOWN1";
        private const int MaxEntries = 4096;
        internal const string PendingFileName = "ownership-next.txt";
        internal const string TransactionFileName = "ownership-next.txt";
        internal const string LastSuccessfulServerFileName = "last-successful-server.txt";

        // Intent: Loads this trusted server's ownership ledger; a missing ledger means the server owns nothing on this client.
        internal static List<AutoModSyncOwnershipEntry> ReadLedger(string amsRoot, string fingerprint)
        {
            ValidateFingerprint(fingerprint);
            string path = GetLedgerPath(amsRoot, fingerprint);
            if (!File.Exists(path)) return new List<AutoModSyncOwnershipEntry>();

            string actual;
            List<AutoModSyncOwnershipEntry> entries = ReadFile(path, fingerprint, out actual);
            return entries;
        }

        // Intent: Strictly parses one versioned ownership file and optionally requires an exact trusted-server fingerprint.
        internal static List<AutoModSyncOwnershipEntry> ReadFile(string path, string expectedFingerprint, out string actualFingerprint)
        {
            actualFingerprint = "";
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException("AutoModSync ownership metadata is missing.", path);

            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2 || !String.Equals(lines[0], Version, StringComparison.Ordinal))
                throw new InvalidDataException("AutoModSync ownership metadata version/header is invalid.");

            actualFingerprint = (lines[1] ?? "").Trim().ToLowerInvariant();
            ValidateFingerprint(actualFingerprint);
            if (!String.IsNullOrEmpty(expectedFingerprint)
                && !String.Equals(actualFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AutoModSync ownership metadata belongs to a different trusted server.");

            List<AutoModSyncOwnershipEntry> entries = new List<AutoModSyncOwnershipEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i;
            for (i = 2; i < lines.Length; i++)
            {
                if (String.IsNullOrWhiteSpace(lines[i])) continue;
                if (entries.Count >= MaxEntries)
                    throw new InvalidDataException("AutoModSync ownership metadata exceeds the entry limit.");

                string[] parts = lines[i].Split('|');
                if (parts.Length != 4 || parts[0].Length != 1)
                    throw new InvalidDataException("Malformed AutoModSync ownership entry.");

                char kind = parts[0][0];
                if (!IsSupportedKind(kind))
                    throw new InvalidDataException("Unsupported AutoModSync ownership kind.");

                long size;
                if (!Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0)
                    throw new InvalidDataException("Invalid AutoModSync ownership size.");

                string sha = (parts[2] ?? "").ToLowerInvariant();
                if (!IsSha256(sha))
                    throw new InvalidDataException("Invalid AutoModSync ownership SHA-256.");

                string rel;
                try { rel = AutoModSyncPathSafety.NormalizeRelative(Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]))); }
                catch { throw new InvalidDataException("Invalid AutoModSync ownership path encoding."); }
                ValidatePath(kind, rel);

                string key = kind + ":" + rel;
                if (!seen.Add(key))
                    throw new InvalidDataException("Duplicate AutoModSync ownership destination.");

                AutoModSyncOwnershipEntry entry = new AutoModSyncOwnershipEntry();
                entry.Kind = kind;
                entry.RelativePath = rel;
                entry.Size = size;
                entry.Sha256 = sha;
                entries.Add(entry);
            }

            return entries;
        }

        // Intent: Durably stages the exact post-commit ledger beside pending.txt before the apply helper is launched.
        internal static void WritePendingDurable(string amsRoot, string fingerprint, IList<AutoModSyncOwnershipEntry> entries)
        {
            string root = GetAmsRoot(amsRoot);
            Directory.CreateDirectory(root);
            AutoModSyncPathSafety.EnsureNoReparsePoints(root, root, true);
            WriteFileDurable(Path.Combine(root, PendingFileName), fingerprint, entries);
        }

        // Intent: Durably publishes one committed trusted-server ledger after all live file operations have COMMITTED.
        // Crash safety: a write-through temporary file is atomically replaced/moved into the fingerprint-scoped ledger path.
        internal static void WriteLedgerDurable(string amsRoot, string fingerprint, IList<AutoModSyncOwnershipEntry> entries)
        {
            ValidateFingerprint(fingerprint);
            string root = GetAmsRoot(amsRoot);
            string ownershipRoot = Path.Combine(root, "ownership");
            Directory.CreateDirectory(ownershipRoot);
            AutoModSyncPathSafety.EnsureNoReparsePoints(root, ownershipRoot, true);
            WriteFileDurable(GetLedgerPath(root, fingerprint), fingerprint, entries);
        }

        // Intent: Writes canonical versioned ownership metadata with strict path/digest validation and write-through durability.
        internal static void WriteFileDurable(string path, string fingerprint, IList<AutoModSyncOwnershipEntry> entries)
        {
            ValidateFingerprint(fingerprint);
            if (entries == null) throw new ArgumentNullException("entries");
            if (entries.Count > MaxEntries) throw new InvalidDataException("AutoModSync ownership metadata exceeds the entry limit.");

            List<AutoModSyncOwnershipEntry> canonical = CloneAndValidate(entries);
            canonical.Sort(delegate(AutoModSyncOwnershipEntry a, AutoModSyncOwnershipEntry b)
            {
                int kind = a.Kind.CompareTo(b.Kind);
                return kind != 0 ? kind : StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
            });

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Version);
            sb.AppendLine(fingerprint.ToLowerInvariant());
            int i;
            for (i = 0; i < canonical.Count; i++)
            {
                AutoModSyncOwnershipEntry entry = canonical[i];
                sb.Append(entry.Kind).Append('|')
                  .Append(entry.Size.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(entry.Sha256.ToLowerInvariant()).Append('|')
                  .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.RelativePath)))
                  .AppendLine();
            }

            string full = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(full);
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
            string temp = full + ".tmp";
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
            {
                writer.Write(sb.ToString());
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(full))
            {
                try
                {
                    File.Replace(temp, full, null, true);
                    return;
                }
                catch (PlatformNotSupportedException) { }
                catch (NotSupportedException) { }
                catch (IOException) { }
                File.Delete(full);
            }
            File.Move(temp, full);
        }

        // Intent: Compares two ledgers by canonical kind/path/digest content, independent of ordering.
        internal static bool Equivalent(IList<AutoModSyncOwnershipEntry> a, IList<AutoModSyncOwnershipEntry> b)
        {
            List<AutoModSyncOwnershipEntry> left = CloneAndValidate(a ?? new List<AutoModSyncOwnershipEntry>());
            List<AutoModSyncOwnershipEntry> right = CloneAndValidate(b ?? new List<AutoModSyncOwnershipEntry>());
            if (left.Count != right.Count) return false;

            Comparison<AutoModSyncOwnershipEntry> compare = delegate(AutoModSyncOwnershipEntry x, AutoModSyncOwnershipEntry y)
            {
                int kind = x.Kind.CompareTo(y.Kind);
                return kind != 0 ? kind : StringComparer.OrdinalIgnoreCase.Compare(x.RelativePath, y.RelativePath);
            };
            left.Sort(compare);
            right.Sort(compare);

            int i;
            for (i = 0; i < left.Count; i++)
            {
                if (left[i].Kind != right[i].Kind
                    || !String.Equals(left[i].RelativePath, right[i].RelativePath, StringComparison.OrdinalIgnoreCase)
                    || left[i].Size != right[i].Size
                    || !String.Equals(left[i].Sha256, right[i].Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        // Intent: Reports whether the immediately prior successful AutoModSync reconciliation was with this exact trusted server.
        // Conservative deletion rule: missing, malformed, or reparse-point state returns false and therefore disables stale deletion rather than broadening authority.
        internal static bool WasLastSuccessfulServer(string amsRoot, string fingerprint)
        {
            ValidateFingerprint(fingerprint);
            string root = GetAmsRoot(amsRoot);
            string path = Path.Combine(root, LastSuccessfulServerFileName);
            try
            {
                if (!File.Exists(path)) return false;
                AutoModSyncPathSafety.EnsureNoReparsePoints(root, path, true);
                string prior = (File.ReadAllText(path) ?? "").Trim();
                if (!IsSha256(prior)) return false;
                return String.Equals(prior, fingerprint, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // Intent: Durably records which trusted server most recently completed manifest reconciliation with no remaining AMS live-file operations.
        // Safety: the marker is fixed beneath AutoModSync, contains only a validated fingerprint, and is published through a write-through temp file.
        internal static void WriteLastSuccessfulServerDurable(string amsRoot, string fingerprint)
        {
            ValidateFingerprint(fingerprint);
            string root = GetAmsRoot(amsRoot);
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, LastSuccessfulServerFileName);
            string temp = path + ".tmp";
            AutoModSyncPathSafety.EnsureNoReparsePoints(root, path, true);
            AutoModSyncPathSafety.EnsureNoReparsePoints(root, temp, true);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
            {
                writer.Write(fingerprint.ToLowerInvariant());
                writer.Write(Environment.NewLine);
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

        // Intent: Returns the fixed fingerprint-scoped ledger path; server-controlled data never selects a filesystem directory.
        internal static string GetLedgerPath(string amsRoot, string fingerprint)
        {
            ValidateFingerprint(fingerprint);
            string root = GetAmsRoot(amsRoot);
            string ownershipRoot = Path.GetFullPath(Path.Combine(root, "ownership"));
            string path = Path.GetFullPath(Path.Combine(ownershipRoot, fingerprint.ToLowerInvariant() + ".txt"));
            string prefix = ownershipRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AutoModSync ownership path escaped its fixed root.");
            return path;
        }

        // Intent: Validates and clones ledger entries so callers cannot mutate canonicalization state while it is being persisted/compared.
        private static List<AutoModSyncOwnershipEntry> CloneAndValidate(IList<AutoModSyncOwnershipEntry> entries)
        {
            List<AutoModSyncOwnershipEntry> result = new List<AutoModSyncOwnershipEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i;
            for (i = 0; i < entries.Count; i++)
            {
                AutoModSyncOwnershipEntry source = entries[i];
                if (source == null) throw new InvalidDataException("Null AutoModSync ownership entry.");
                ValidatePath(source.Kind, source.RelativePath);
                if (source.Size < 0 || !IsSha256(source.Sha256))
                    throw new InvalidDataException("Invalid AutoModSync ownership digest metadata.");

                string key = source.Kind + ":" + source.RelativePath;
                if (!seen.Add(key)) throw new InvalidDataException("Duplicate AutoModSync ownership destination.");

                AutoModSyncOwnershipEntry copy = new AutoModSyncOwnershipEntry();
                copy.Kind = source.Kind;
                copy.RelativePath = AutoModSyncPathSafety.NormalizeRelative(source.RelativePath);
                copy.Size = source.Size;
                copy.Sha256 = source.Sha256.ToLowerInvariant();
                result.Add(copy);
            }
            return result;
        }

        // Intent: Restricts ownership to the same fixed BepInEx roots and protected-config exclusions as synchronization/apply.
        private static void ValidatePath(char kind, string relative)
        {
            if (!IsSupportedKind(kind)) throw new InvalidDataException("Unsupported AutoModSync ownership kind.");
            string rel = AutoModSyncPathSafety.NormalizeRelative(relative);
            if (String.IsNullOrEmpty(rel)) throw new InvalidDataException("Unsafe AutoModSync ownership path.");
            if (kind == 'C' && IsProtectedConfigName(Path.GetFileName(rel)))
                throw new InvalidDataException("Refusing protected BepInEx/AutoModSync config ownership.");
        }

        // Intent: Recognizes only destination kinds implemented by the signed manifest and apply helper.
        private static bool IsSupportedKind(char kind)
        {
            return kind == 'P' || kind == 'R' || kind == 'C';
        }

        // Intent: Keeps signing identity and loader-wide configuration outside server-owned lifecycle management.
        private static bool IsProtectedConfigName(string name)
        {
            return String.Equals(name, "ValheimAutoModSync.private.xml", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "ValheimAutoModSync.public.xml", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "BepInEx.cfg", StringComparison.OrdinalIgnoreCase);
        }

        // Intent: Validates fixed-length hexadecimal identities/digests before they influence ownership.
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

        // Intent: Validates the trusted-server fingerprint before it selects a ledger filename.
        private static void ValidateFingerprint(string fingerprint)
        {
            if (!IsSha256(fingerprint)) throw new InvalidDataException("Invalid AutoModSync ownership server fingerprint.");
        }

        // Intent: Normalizes the fixed AutoModSync root used for all ownership metadata.
        private static string GetAmsRoot(string amsRoot)
        {
            if (String.IsNullOrEmpty(amsRoot)) throw new ArgumentException("AutoModSync root is required.");
            return Path.GetFullPath(amsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
