using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimAutoModSync
{
    // Intent: Centralizes the Windows path rules shared by the client, server, and apply helper.
    // Security: synchronization is allowed only beneath fixed AutoModSync/BepInEx roots; this helper also rejects
    // Windows alias/device-name tricks and existing reparse points that could redirect a seemingly-contained path.
    internal static class AutoModSyncPathSafety
    {
        private const int MaxRelativePathChars = 4096;
        private const int MaxSegmentChars = 255;

        private static readonly HashSet<string> ReservedDeviceNames = new HashSet<string>(
            new string[]
            {
                "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            },
            StringComparer.OrdinalIgnoreCase);

        // Intent: Converts one protocol path to canonical forward-slash relative form.
        // Security: rejects rooted/parent paths, Windows reserved names, control/invalid characters,
        // trailing-dot/space aliases, and unreasonable segment/path lengths before filesystem use.
        internal static string NormalizeRelative(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > MaxRelativePathChars) return "";
            if (value[0] == '/' || value[0] == '\\' || value.IndexOf(':') >= 0) return "";

            value = value.Replace('\\', '/');
            string[] parts = value.Split('/');
            int i;
            for (i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (!IsSafeSegment(part)) return "";
            }
            return String.Join("/", parts);
        }

        // Intent: Resolves a validated relative path beneath an explicit trusted root.
        // Security: full-path containment is checked before walking existing components for reparse points.
        internal static string SafeUnderRoot(string rootPath, string relative, bool includeLeafInReparseCheck)
        {
            string rel = NormalizeRelative(relative);
            if (rel.Length == 0) throw new InvalidDataException("Unsafe AutoModSync relative path.");

            string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = root + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AutoModSync path escaped its fixed root.");

            EnsureNoReparsePoints(root, full, includeLeafInReparseCheck);
            return full;
        }

        // Intent: Rechecks an already-resolved path immediately before sensitive filesystem use.
        // Security: a junction/symlink/reparse point anywhere below the trusted root could redirect reads or writes
        // outside that root even when string-level GetFullPath containment succeeds.
        internal static void EnsureNoReparsePoints(string rootPath, string fullPath, bool includeLeaf)
        {
            string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = root + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(fullPath);
            bool isRoot = String.Equals(full, root, StringComparison.OrdinalIgnoreCase);
            if (!isRoot && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AutoModSync reparse check escaped its fixed root. Root='" + root + "' Full='" + full + "'.");

            string relative = isRoot ? "" : full.Substring(prefix.Length);
            string[] parts = relative.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            int count = includeLeaf ? parts.Length : Math.Max(0, parts.Length - 1);
            string current = root;
            int i;
            for (i = 0; i < count; i++)
            {
                current = Path.Combine(current, parts[i]);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("AutoModSync refuses to traverse a filesystem reparse point: " + parts[i]);
            }
        }

        // Intent: Lets the server's recursive scanner decide whether a filesystem object is safe to traverse.
        // Security: errors other than a genuinely absent path are propagated so an unreadable/ambiguous object is
        // not silently treated as a normal source file or directory.
        internal static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        // Intent: Validates one Windows path segment independently of any filesystem state.
        private static bool IsSafeSegment(string part)
        {
            if (String.IsNullOrEmpty(part) || part == "." || part == ".." || part.Length > MaxSegmentChars) return false;
            if (part[part.Length - 1] == '.' || part[part.Length - 1] == ' ') return false;

            char[] invalid = Path.GetInvalidFileNameChars();
            int i;
            for (i = 0; i < part.Length; i++)
            {
                char c = part[i];
                if (c < 32 || Array.IndexOf(invalid, c) >= 0) return false;
            }

            int dot = part.IndexOf('.');
            string deviceStem = dot < 0 ? part : part.Substring(0, dot);
            if (ReservedDeviceNames.Contains(deviceStem)) return false;
            return true;
        }
    }
}
