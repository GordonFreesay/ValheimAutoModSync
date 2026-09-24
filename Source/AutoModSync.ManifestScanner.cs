using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimAutoModSync
{
    // Intent: Enumerates files beneath one fixed manifest source root without following filesystem reparse points.
    // Security: junctions/symlinks inside plugins, patchers, config, or other explicitly scanned roots must never
    // expand the server's distributable source boundary. File reparse points are excluded as sources as well.
    internal static class AutoModSyncManifestScanner
    {
        internal static List<string> EnumerateFiles(string root, Action<string> warning)
        {
            List<string> files = new List<string>();
            if (String.IsNullOrEmpty(root) || !Directory.Exists(root)) return files;

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
                        if (warning != null) warning("AutoModSync skipped reparse-point source file: " + currentFiles[i]);
                        continue;
                    }
                    files.Add(currentFiles[i]);
                }

                string[] directories = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly);
                for (i = 0; i < directories.Length; i++)
                {
                    if (AutoModSyncPathSafety.IsReparsePoint(directories[i]))
                    {
                        if (warning != null) warning("AutoModSync skipped reparse-point source directory: " + directories[i]);
                        continue;
                    }
                    pending.Push(directories[i]);
                }
            }

            return files;
        }
    }
}
