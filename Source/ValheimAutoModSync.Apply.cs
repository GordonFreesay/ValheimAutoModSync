using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using System.Reflection;
using ValheimAutoModSync;

[assembly: AssemblyTitle("Valheim AutoModSync Apply Helper")]
[assembly: AssemblyDescription("Applies verified staged AutoModSync BepInEx files after Valheim exits, then relaunches Valheim.")]
[assembly: AssemblyCompany("GordonFreesay")]
[assembly: AssemblyProduct("Valheim AutoModSync")]
[assembly: AssemblyVersion("2.6.0.0")]
[assembly: AssemblyFileVersion("2.6.0.0")]

internal static class Program
{
    private const string TransactionDirectoryName = "apply-transaction";
    private const string TransactionManifestName = "manifest.txt";
    private const string PreparedMarkerName = "prepared.ok";
    private const string CommittedMarkerName = "committed.ok";

    private sealed class ApplyItem
    {
        public char Kind;
        public string RelativePath;
        public bool OldExists;
        public long NewSize;
        public string NewSha256;
        public long OldSize;
        public string OldSha256;
    }

    private sealed class FileDigest
    {
        public long Size;
        public string Sha256;
    }

    // Intent: Out-of-process apply/relaunch entry point, started only after the client has downloaded and verified staged files.
    // Workflow: waits for the old Valheim process to exit, atomically replaces staged plugin/patcher/allowlisted-config files where possible, preserves reconnect state, then relaunches through the saved package-manager context, Steam, or direct executable fallback.
    private static int Main(string[] args)
    {
        string amsRoot = null;
        string bepinexRoot = null;
        string gameRoot = null;
        string pluginRoot = null;
        string patcherRoot = null;
        string configRoot = null;
        string stagingRoot = null;
        string pending = null;

        try
        {
            int pid = 0;
            if (args.Length > 0) int.TryParse(args[0], out pid);
            if (pid > 0)
            {
                try
                {
                    Process p = Process.GetProcessById(pid);
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(5000); } catch { }
                    }
                }
                catch { }
            }

            // Give Steam and Windows a moment to release the old game's loaded DLLs before the transaction touches live files.
            Thread.Sleep(1500);

            string self = typeof(Program).Assembly.Location;
            string helperDir = Path.GetDirectoryName(self);
            amsRoot = helperDir;
            if (args.Length > 1 && !String.IsNullOrEmpty(args[1]))
                amsRoot = Path.GetFullPath(args[1]);

            bepinexRoot = Directory.GetParent(amsRoot).FullName;
            gameRoot = Directory.GetParent(bepinexRoot).FullName;
            pluginRoot = Path.Combine(bepinexRoot, "plugins");
            patcherRoot = Path.Combine(bepinexRoot, "patchers");
            configRoot = Path.Combine(bepinexRoot, "config");
            stagingRoot = Path.Combine(amsRoot, "staging");
            pending = Path.Combine(amsRoot, "pending.txt");

            AppendApplyLog(amsRoot, "Apply helper started.");

            // If an earlier helper was terminated between PREPARED and COMMITTED, restore the complete old set first.
            // If COMMITTED already exists, the complete new set won and only idempotent cleanup remains.
            RecoverInterruptedTransaction(amsRoot, pluginRoot, patcherRoot, configRoot, stagingRoot, pending);

            if (File.Exists(pending))
                ApplyPendingTransaction(amsRoot, pluginRoot, patcherRoot, configRoot, stagingRoot, pending);

            AppendApplyLog(amsRoot, "Apply state is clean; relaunching Valheim.");

            // reconnect.txt is intentionally left in place. The newly loaded AutoModSync client consumes it once
            // and performs the reconnect through Valheim's own FejdStartup/ServerJoinData flow after the main menu exists.
            if (TryLaunchSavedContext(amsRoot)) return 0;

            string gameExe = Path.Combine(gameRoot, "valheim.exe");
            if (!File.Exists(gameExe)) return 3;

            string steamExe = FindSteamExe();
            if (!String.IsNullOrEmpty(steamExe) && File.Exists(steamExe))
            {
                ProcessStartInfo steam = new ProcessStartInfo();
                steam.FileName = steamExe;
                steam.Arguments = "-applaunch 892970";
                steam.WorkingDirectory = Path.GetDirectoryName(steamExe);
                steam.UseShellExecute = true;
                Process.Start(steam);
                return 0;
            }

            // Last-resort relaunch if Steam itself cannot be located.
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = gameExe;
            psi.WorkingDirectory = gameRoot;
            psi.UseShellExecute = true;
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            // A caught failure during APPLYING is repaired immediately when possible. A process kill/power loss
            // cannot execute this catch; the next helper invocation follows the same journal and repairs it then.
            try
            {
                if (!String.IsNullOrEmpty(amsRoot) &&
                    !String.IsNullOrEmpty(pluginRoot) &&
                    !String.IsNullOrEmpty(patcherRoot) &&
                    !String.IsNullOrEmpty(configRoot) &&
                    !String.IsNullOrEmpty(stagingRoot) &&
                    !String.IsNullOrEmpty(pending))
                {
                    RecoverInterruptedTransaction(amsRoot, pluginRoot, patcherRoot, configRoot, stagingRoot, pending);
                }
            }
            catch (Exception recoveryEx)
            {
                try { AppendApplyLog(amsRoot, "Recovery after apply failure also failed: " + recoveryEx); } catch { }
            }

            try
            {
                string dir = !String.IsNullOrEmpty(amsRoot) ? amsRoot : Path.GetDirectoryName(typeof(Program).Assembly.Location);
                File.WriteAllText(Path.Combine(dir, "apply-error.txt"), ex.ToString());
                AppendApplyLog(dir, "Apply failed: " + ex);
            }
            catch { }
            return 1;
        }
    }

    // Intent: Applies pending verified staging as one journaled transaction.
    // Invariant: every old destination is durably backed up and PREPARED is durably recorded before the first live write; COMMITTED is recorded only after every new destination is verified.
    private static void ApplyPendingTransaction(string amsRoot, string pluginRoot, string patcherRoot, string configRoot, string stagingRoot, string pending)
    {
        List<ApplyItem> items = ReadPendingItems(pending);
        if (items.Count == 0) throw new InvalidDataException("AutoModSync pending transaction contained no files.");

        string txRoot = Path.Combine(amsRoot, TransactionDirectoryName);
        if (Directory.Exists(txRoot))
            throw new InvalidOperationException("AutoModSync transaction directory still exists after recovery.");

        Directory.CreateDirectory(txRoot);
        AutoModSyncPathSafety.EnsureNoReparsePoints(amsRoot, txRoot, true);

        try
        {
            AppendApplyLog(amsRoot, "Preparing transactional apply for " + items.Count + " file(s).");
            PrepareTransaction(txRoot, items, pluginRoot, patcherRoot, configRoot, stagingRoot);
            WriteMarkerDurable(Path.Combine(txRoot, PreparedMarkerName), "PREPARED");
            AppendApplyLog(amsRoot, "Transaction PREPARED; all old-state backups are durable.");

            int i;
            for (i = 0; i < items.Count; i++)
            {
                ApplyItem item = items[i];
                ApplyOneItem(item, pluginRoot, patcherRoot, configRoot, stagingRoot);
                AppendApplyLog(amsRoot, "Applied " + (i + 1).ToString() + "/" + items.Count.ToString() + ": " + item.Kind + ":" + item.RelativePath);
            }

            WriteMarkerDurable(Path.Combine(txRoot, CommittedMarkerName), "COMMITTED");
            AppendApplyLog(amsRoot, "Transaction COMMITTED; complete new state verified.");

            FinalizeCommittedTransaction(txRoot, items, pluginRoot, patcherRoot, configRoot, stagingRoot, pending);
            AppendApplyLog(amsRoot, "Committed transaction cleanup complete.");
        }
        catch
        {
            // Leave PREPARED/manifest/backups intact for Main's recovery path or the next helper process.
            throw;
        }
    }

    // Intent: Handles durable journal state left by an interrupted helper before any new transaction begins.
    // PREPARED without COMMITTED rolls every destination back to the old set; COMMITTED keeps the new set and finishes cleanup.
    private static void RecoverInterruptedTransaction(string amsRoot, string pluginRoot, string patcherRoot, string configRoot, string stagingRoot, string pending)
    {
        string txRoot = Path.Combine(amsRoot, TransactionDirectoryName);
        if (!Directory.Exists(txRoot)) return;

        AutoModSyncPathSafety.EnsureNoReparsePoints(amsRoot, txRoot, true);
        string prepared = Path.Combine(txRoot, PreparedMarkerName);
        string committed = Path.Combine(txRoot, CommittedMarkerName);

        if (File.Exists(committed))
        {
            List<ApplyItem> committedItems = ReadTransactionManifest(txRoot);
            AppendApplyLog(amsRoot, "Found interrupted COMMITTED transaction; preserving complete new state and finishing cleanup.");
            FinalizeCommittedTransaction(txRoot, committedItems, pluginRoot, patcherRoot, configRoot, stagingRoot, pending);
            return;
        }

        if (!File.Exists(prepared))
        {
            // By construction no live write occurs before PREPARED is durably created. An unprepared directory can therefore be discarded safely.
            AppendApplyLog(amsRoot, "Discarding incomplete pre-PREPARED transaction; no live files were modified.");
            DeleteTransactionDirectory(amsRoot, txRoot);
            return;
        }

        List<ApplyItem> items = ReadTransactionManifest(txRoot);
        AppendApplyLog(amsRoot, "Found interrupted PREPARED transaction; rolling back to complete old state.");

        int i;
        for (i = items.Count - 1; i >= 0; i--)
            RollBackOneItem(txRoot, items[i], pluginRoot, patcherRoot, configRoot);

        DeleteTransactionDirectory(amsRoot, txRoot);
        AppendApplyLog(amsRoot, "Rollback complete; staged new files remain available for a clean retry.");
    }

    // Intent: Builds durable old-state backups and a self-contained manifest before any destination is changed.
    // Safety: staging and live paths are rechecked for reparse redirection, duplicate destinations are rejected earlier, and each backup is hashed after a write-through copy.
    private static void PrepareTransaction(string txRoot, List<ApplyItem> items, string pluginRoot, string patcherRoot, string configRoot, string stagingRoot)
    {
        int i;
        for (i = 0; i < items.Count; i++)
        {
            ApplyItem item = items[i];
            string srcRoot;
            string dstRoot;
            ResolveRoots(item.Kind, stagingRoot, pluginRoot, patcherRoot, configRoot, out srcRoot, out dstRoot);

            string src = SafeUnder(srcRoot, item.RelativePath) + ".amsnew";
            string dst = SafeUnder(dstRoot, item.RelativePath);
            AutoModSyncPathSafety.EnsureNoReparsePoints(srcRoot, src, true);
            AutoModSyncPathSafety.EnsureNoReparsePoints(dstRoot, dst, true);

            if (!File.Exists(src)) throw new FileNotFoundException("Verified staged AutoModSync file is missing.", src);
            if (Directory.Exists(dst)) throw new InvalidDataException("AutoModSync destination is unexpectedly a directory: " + item.RelativePath);

            FileDigest newDigest = HashFile(src);
            item.NewSize = newDigest.Size;
            item.NewSha256 = newDigest.Sha256;
            item.OldExists = File.Exists(dst);
            item.OldSize = -1L;
            item.OldSha256 = "";

            if (item.OldExists)
            {
                string backupRoot = Path.Combine(txRoot, "backup", KindDirectory(item.Kind));
                string backup = SafeUnder(backupRoot, item.RelativePath) + ".amsold";
                string backupParent = Path.GetDirectoryName(backup);
                if (!Directory.Exists(backupParent)) Directory.CreateDirectory(backupParent);
                AutoModSyncPathSafety.EnsureNoReparsePoints(backupRoot, backup, true);

                FileDigest oldDigest = CopyFileDurable(dst, backup);
                item.OldSize = oldDigest.Size;
                item.OldSha256 = oldDigest.Sha256;
                VerifyFile(backup, item.OldSize, item.OldSha256, "transaction backup");
            }
        }

        WriteTransactionManifest(txRoot, items);
    }

    // Intent: Applies one new file without consuming its verified staging copy, allowing a later rollback/retry after interruption.
    // Transaction safety: the current old destination must still match the durable backup metadata before replacement, and the resulting live file must match the prepared new digest.
    private static void ApplyOneItem(ApplyItem item, string pluginRoot, string patcherRoot, string configRoot, string stagingRoot)
    {
        string srcRoot;
        string dstRoot;
        ResolveRoots(item.Kind, stagingRoot, pluginRoot, patcherRoot, configRoot, out srcRoot, out dstRoot);

        string src = SafeUnder(srcRoot, item.RelativePath) + ".amsnew";
        string dst = SafeUnder(dstRoot, item.RelativePath);
        AutoModSyncPathSafety.EnsureNoReparsePoints(srcRoot, src, true);
        AutoModSyncPathSafety.EnsureNoReparsePoints(dstRoot, dst, true);

        VerifyFile(src, item.NewSize, item.NewSha256, "staged replacement");

        if (item.OldExists)
        {
            if (!File.Exists(dst)) throw new IOException("AutoModSync destination disappeared after transaction preparation: " + item.RelativePath);
            VerifyFile(dst, item.OldSize, item.OldSha256, "live pre-apply file");
        }
        else if (File.Exists(dst) || Directory.Exists(dst))
        {
            throw new IOException("AutoModSync destination appeared after transaction preparation: " + item.RelativePath);
        }

        string parent = Path.GetDirectoryName(dst);
        if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);

        string temp = dst + ".amstxnnew";
        AutoModSyncPathSafety.EnsureNoReparsePoints(dstRoot, temp, true);
        try { if (File.Exists(temp)) File.Delete(temp); } catch { }

        FileDigest copied = CopyFileDurable(src, temp);
        if (copied.Size != item.NewSize || !ConstantEquals(copied.Sha256, item.NewSha256))
            throw new InvalidDataException("AutoModSync temporary replacement did not match prepared staging: " + item.RelativePath);

        ReplaceFromTemp(temp, dst);
        VerifyFile(dst, item.NewSize, item.NewSha256, "live post-apply file");
    }

    // Intent: Restores exactly the old destination represented by one PREPARED transaction entry.
    // Existing-old entries are restored from durable backups; originally absent entries are deleted if a partial apply created them.
    private static void RollBackOneItem(string txRoot, ApplyItem item, string pluginRoot, string patcherRoot, string configRoot)
    {
        string ignoredSrcRoot;
        string dstRoot;
        ResolveRoots(item.Kind, "", pluginRoot, patcherRoot, configRoot, out ignoredSrcRoot, out dstRoot);
        string dst = SafeUnder(dstRoot, item.RelativePath);
        AutoModSyncPathSafety.EnsureNoReparsePoints(dstRoot, dst, true);

        string applyTemp = dst + ".amstxnnew";
        string rollbackTemp = dst + ".amsrollback";
        try { if (File.Exists(applyTemp)) File.Delete(applyTemp); } catch { }
        try { if (File.Exists(rollbackTemp)) File.Delete(rollbackTemp); } catch { }

        if (!item.OldExists)
        {
            if (File.Exists(dst)) File.Delete(dst);
            return;
        }

        string backupRoot = Path.Combine(txRoot, "backup", KindDirectory(item.Kind));
        string backup = SafeUnder(backupRoot, item.RelativePath) + ".amsold";
        AutoModSyncPathSafety.EnsureNoReparsePoints(backupRoot, backup, true);
        VerifyFile(backup, item.OldSize, item.OldSha256, "rollback backup");

        string parent = Path.GetDirectoryName(dst);
        if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);

        FileDigest copied = CopyFileDurable(backup, rollbackTemp);
        if (copied.Size != item.OldSize || !ConstantEquals(copied.Sha256, item.OldSha256))
            throw new InvalidDataException("AutoModSync rollback copy did not match its durable backup: " + item.RelativePath);

        ReplaceFromTemp(rollbackTemp, dst);
        VerifyFile(dst, item.OldSize, item.OldSha256, "rolled-back live file");
    }

    // Intent: Completes only post-COMMIT cleanup. Once COMMITTED exists, the new live set is authoritative and must never be rolled back because cleanup was interrupted.
    private static void FinalizeCommittedTransaction(string txRoot, List<ApplyItem> items, string pluginRoot, string patcherRoot, string configRoot, string stagingRoot, string pending)
    {
        int i;
        for (i = 0; i < items.Count; i++)
        {
            ApplyItem item = items[i];
            string srcRoot;
            string ignoredDstRoot;
            ResolveRoots(item.Kind, stagingRoot, pluginRoot, patcherRoot, configRoot, out srcRoot, out ignoredDstRoot);
            string src = SafeUnder(srcRoot, item.RelativePath) + ".amsnew";
            AutoModSyncPathSafety.EnsureNoReparsePoints(srcRoot, src, true);
            try { if (File.Exists(src)) File.Delete(src); } catch { throw; }
        }

        if (File.Exists(pending)) File.Delete(pending);
        DeleteTransactionDirectory(Path.GetDirectoryName(txRoot), txRoot);
    }

    // Intent: Strictly parses the pending list into unique fixed-root transaction entries; malformed lines are errors rather than silently skipped.
    private static List<ApplyItem> ReadPendingItems(string pending)
    {
        string[] lines = File.ReadAllLines(pending);
        List<ApplyItem> items = new List<ApplyItem>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int i;
        for (i = 0; i < lines.Length; i++)
        {
            string line = lines[i] ?? "";
            if (line.Length < 3 || line[1] != ':') throw new InvalidDataException("Malformed AutoModSync pending entry.");
            char kind = line[0];
            if (!IsSupportedKind(kind)) throw new InvalidDataException("Unsupported AutoModSync pending-file kind.");

            string rel = NormalizeRelative(line.Substring(2));
            if (rel.Length == 0) throw new InvalidDataException("Unsafe AutoModSync pending-file path.");
            if (kind == 'C' && IsProtectedConfigName(Path.GetFileName(rel)))
                throw new InvalidDataException("Refusing to apply a protected BepInEx/AutoModSync config file.");

            string key = kind + ":" + rel;
            if (!seen.Add(key)) throw new InvalidDataException("Duplicate AutoModSync pending-file destination.");

            ApplyItem item = new ApplyItem();
            item.Kind = kind;
            item.RelativePath = rel;
            items.Add(item);
        }
        return items;
    }

    // Intent: Persists the complete rollback/verification metadata before PREPARED is created.
    // Format is versioned and uses Base64 for the relative path so parsing does not depend on filename punctuation.
    private static void WriteTransactionManifest(string txRoot, List<ApplyItem> items)
    {
        string path = Path.Combine(txRoot, TransactionManifestName);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("AMSTXN1");

        int i;
        for (i = 0; i < items.Count; i++)
        {
            ApplyItem item = items[i];
            sb.Append(item.Kind).Append('|')
              .Append(item.OldExists ? "1" : "0").Append('|')
              .Append(item.NewSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
              .Append(item.NewSha256 ?? "").Append('|')
              .Append(item.OldSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
              .Append(item.OldSha256 ?? "").Append('|')
              .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(item.RelativePath ?? "")))
              .AppendLine();
        }

        WriteTextDurable(path, sb.ToString());
    }

    // Intent: Reads only the helper's versioned transaction journal; malformed metadata stops recovery rather than guessing at rollback destinations.
    private static List<ApplyItem> ReadTransactionManifest(string txRoot)
    {
        string path = Path.Combine(txRoot, TransactionManifestName);
        if (!File.Exists(path)) throw new InvalidDataException("AutoModSync transaction manifest is missing.");

        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2 || !String.Equals(lines[0], "AMSTXN1", StringComparison.Ordinal))
            throw new InvalidDataException("AutoModSync transaction manifest version/header is invalid.");

        List<ApplyItem> items = new List<ApplyItem>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int i;
        for (i = 1; i < lines.Length; i++)
        {
            if (String.IsNullOrWhiteSpace(lines[i])) continue;
            string[] parts = lines[i].Split('|');
            if (parts.Length != 7) throw new InvalidDataException("Malformed AutoModSync transaction entry.");

            char kind;
            if (parts[0].Length != 1) throw new InvalidDataException("Invalid AutoModSync transaction kind.");
            kind = parts[0][0];
            if (!IsSupportedKind(kind)) throw new InvalidDataException("Unsupported AutoModSync transaction kind.");

            bool oldExists;
            if (parts[1] == "1") oldExists = true;
            else if (parts[1] == "0") oldExists = false;
            else throw new InvalidDataException("Invalid AutoModSync transaction old-state marker.");

            long newSize;
            long oldSize;
            if (!Int64.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out newSize) || newSize < 0)
                throw new InvalidDataException("Invalid AutoModSync transaction new-file size.");
            if (!Int64.TryParse(parts[4], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out oldSize))
                throw new InvalidDataException("Invalid AutoModSync transaction old-file size.");

            string newSha = parts[3] ?? "";
            string oldSha = parts[5] ?? "";
            if (!IsSha256(newSha)) throw new InvalidDataException("Invalid AutoModSync transaction new-file SHA-256.");
            if (oldExists)
            {
                if (oldSize < 0 || !IsSha256(oldSha)) throw new InvalidDataException("Invalid AutoModSync transaction old-file metadata.");
            }
            else
            {
                oldSize = -1L;
                oldSha = "";
            }

            string rel;
            try { rel = NormalizeRelative(Encoding.UTF8.GetString(Convert.FromBase64String(parts[6]))); }
            catch { throw new InvalidDataException("Invalid AutoModSync transaction path encoding."); }
            if (rel.Length == 0) throw new InvalidDataException("Unsafe AutoModSync transaction path.");
            if (kind == 'C' && IsProtectedConfigName(Path.GetFileName(rel)))
                throw new InvalidDataException("Refusing a protected BepInEx/AutoModSync config path in the transaction journal.");

            string key = kind + ":" + rel;
            if (!seen.Add(key)) throw new InvalidDataException("Duplicate AutoModSync transaction destination.");

            ApplyItem item = new ApplyItem();
            item.Kind = kind;
            item.RelativePath = rel;
            item.OldExists = oldExists;
            item.NewSize = newSize;
            item.NewSha256 = newSha;
            item.OldSize = oldSize;
            item.OldSha256 = oldSha;
            items.Add(item);
        }

        if (items.Count == 0) throw new InvalidDataException("AutoModSync transaction manifest contained no entries.");
        return items;
    }

    // Intent: Maps signed file kinds to fixed staging/live roots; no transaction metadata can select an arbitrary filesystem destination.
    private static void ResolveRoots(char kind, string stagingRoot, string pluginRoot, string patcherRoot, string configRoot, out string srcRoot, out string dstRoot)
    {
        if (kind == 'P') { srcRoot = String.IsNullOrEmpty(stagingRoot) ? "" : Path.Combine(stagingRoot, "plugins"); dstRoot = pluginRoot; return; }
        if (kind == 'R') { srcRoot = String.IsNullOrEmpty(stagingRoot) ? "" : Path.Combine(stagingRoot, "patchers"); dstRoot = patcherRoot; return; }
        if (kind == 'C') { srcRoot = String.IsNullOrEmpty(stagingRoot) ? "" : Path.Combine(stagingRoot, "config"); dstRoot = configRoot; return; }
        throw new InvalidDataException("Unsupported AutoModSync file kind.");
    }

    // Intent: Keeps the transaction kind vocabulary restricted to the fixed synchronized BepInEx roots.
    private static bool IsSupportedKind(char kind)
    {
        return kind == 'P' || kind == 'R' || kind == 'C';
    }

    // Intent: Repeats client/server protection for AutoModSync identity/global BepInEx configuration at the final out-of-process write boundary.
    private static bool IsProtectedConfigName(string name)
    {
        return String.Equals(name, "ValheimAutoModSync.private.xml", StringComparison.OrdinalIgnoreCase)
            || String.Equals(name, "ValheimAutoModSync.public.xml", StringComparison.OrdinalIgnoreCase)
            || String.Equals(name, "BepInEx.cfg", StringComparison.OrdinalIgnoreCase);
    }

    // Intent: Returns the fixed directory name associated with one manifest kind.
    private static string KindDirectory(char kind)
    {
        if (kind == 'P') return "plugins";
        if (kind == 'R') return "patchers";
        if (kind == 'C') return "config";
        throw new InvalidDataException("Unsupported AutoModSync file kind.");
    }

    // Intent: Copies a file with write-through semantics while hashing the exact bytes written; callers use the digest to prove backups/replacements before changing transaction state.
    private static FileDigest CopyFileDurable(string source, string destination)
    {
        long size = 0L;
        byte[] buffer = new byte[131072];
        using (SHA256 sha = SHA256.Create())
        using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.WriteThrough))
        {
            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                output.Write(buffer, 0, read);
                sha.TransformBlock(buffer, 0, read, buffer, 0);
                size += read;
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            output.Flush(true);

            FileDigest digest = new FileDigest();
            digest.Size = size;
            digest.Sha256 = ToHex(sha.Hash);
            return digest;
        }
    }

    // Intent: Hashes a stable staging/live file while preventing concurrent writers from mutating the bytes during measurement.
    private static FileDigest HashFile(string path)
    {
        long size = 0L;
        byte[] buffer = new byte[131072];
        using (SHA256 sha = SHA256.Create())
        using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                sha.TransformBlock(buffer, 0, read, buffer, 0);
                size += read;
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);

            FileDigest digest = new FileDigest();
            digest.Size = size;
            digest.Sha256 = ToHex(sha.Hash);
            return digest;
        }
    }

    // Intent: Verifies one transaction file against journal metadata before it can influence rollback/commit state.
    private static void VerifyFile(string path, long expectedSize, string expectedSha256, string label)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("AutoModSync " + label + " is missing.", path);
        FileDigest digest = HashFile(path);
        if (digest.Size != expectedSize || !ConstantEquals(digest.Sha256, expectedSha256))
            throw new InvalidDataException("AutoModSync " + label + " failed transaction verification: " + path);
    }

    // Intent: Promotes a fully written same-directory temporary file to the destination.
    // File.Replace is preferred for existing files; the copy fallback remains recoverable because PREPARED backups already exist.
    private static void ReplaceFromTemp(string temp, string destination)
    {
        if (File.Exists(destination))
        {
            try
            {
                File.Replace(temp, destination, null, true);
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (NotSupportedException) { }
            catch (IOException) { }

            File.Copy(temp, destination, true);
            File.Delete(temp);
            return;
        }

        File.Move(temp, destination);
    }

    // Intent: Writes PREPARED/COMMITTED journal state with write-through + Flush(true) before the transaction crosses that recovery boundary.
    private static void WriteMarkerDurable(string path, string state)
    {
        WriteTextDurable(path, state + "|" + DateTime.UtcNow.ToString("o") + Environment.NewLine);
    }

    // Intent: Writes a small transaction metadata file durably; a marker is never considered present until its complete bytes have been flushed to disk.
    private static void WriteTextDurable(string path, string text)
    {
        using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
        {
            writer.Write(text ?? "");
            writer.Flush();
            stream.Flush(true);
        }
    }

    // Intent: Removes the helper-owned transaction tree only after rollback or committed cleanup has reached a complete state.
    private static void DeleteTransactionDirectory(string amsRoot, string txRoot)
    {
        AutoModSyncPathSafety.EnsureNoReparsePoints(amsRoot, txRoot, true);
        if (Directory.Exists(txRoot)) Directory.Delete(txRoot, true);
    }

    // Intent: Appends human-readable transaction milestones for interruption testing and postmortem support without changing the authoritative journal.
    private static void AppendApplyLog(string amsRoot, string message)
    {
        if (String.IsNullOrEmpty(amsRoot)) return;
        try
        {
            string path = Path.Combine(amsRoot, "apply.log");
            File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + (message ?? "") + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    // Intent: Constant-time-ish comparison for transaction hashes; both inputs are expected lowercase/uppercase hexadecimal of fixed length.
    private static bool ConstantEquals(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int diff = 0;
        int i;
        for (i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    // Intent: Validates transaction hash text before trusting journal metadata.
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

    // Intent: Converts digest bytes to stable lowercase hexadecimal for transaction metadata and verification.
    private static string ToHex(byte[] bytes)
    {
        StringBuilder sb = new StringBuilder(bytes.Length * 2);
        int i;
        for (i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    // Intent: Restores the exact executable/working-directory/argument context captured before a package-managed restart.
    // Workflow: decodes the saved context, prefers a fresh Steam app launch with the saved Doorstop arguments, otherwise starts the executable directly after stripping inherited DOORSTOP_* environment variables.
    private static bool TryLaunchSavedContext(string amsRoot)
    {
        string contextPath = Path.Combine(amsRoot, "launch-context.txt");
        if (!File.Exists(contextPath)) return false;

        string[] lines = File.ReadAllLines(contextPath);
        if (lines.Length < 3 || !String.Equals(lines[0], "AMSLAUNCH1", StringComparison.Ordinal))
            throw new InvalidDataException("AutoModSync package-manager launch context is invalid.");

        string executable = DecodeLaunchField(lines[1]);
        string workingDirectory = DecodeLaunchField(lines[2]);
        if (String.IsNullOrEmpty(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Saved Valheim executable no longer exists.", executable);

        if (String.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
            workingDirectory = Path.GetDirectoryName(executable);

        StringBuilder arguments = new StringBuilder();
        int i;
        for (i = 3; i < lines.Length; i++)
        {
            if (arguments.Length > 0) arguments.Append(' ');
            arguments.Append(QuoteArgument(DecodeLaunchField(lines[i])));
        }

        // Package managers launch the game through Steam with Doorstop arguments.
        // Re-enter through Steam as well so the replacement game receives a fresh
        // process environment rather than inheriting Doorstop's runtime markers
        // from the old injected Valheim process via this helper.
        string steamExe = FindSteamExe();
        if (!String.IsNullOrEmpty(steamExe) && File.Exists(steamExe))
        {
            ProcessStartInfo steam = new ProcessStartInfo();
            steam.FileName = steamExe;
            steam.Arguments = "-applaunch 892970" + (arguments.Length > 0 ? " " + arguments.ToString() : "");
            steam.WorkingDirectory = Path.GetDirectoryName(steamExe);
            steam.UseShellExecute = true;
            Process.Start(steam);
        }
        else
        {
            // Last-resort direct relaunch if Steam cannot be located. Strip every
            // inherited Doorstop variable and rely on the saved Doorstop CLI args
            // to bootstrap the requested package-manager profile from scratch.
            ProcessStartInfo relaunch = new ProcessStartInfo();
            relaunch.FileName = executable;
            relaunch.Arguments = arguments.ToString();
            relaunch.WorkingDirectory = workingDirectory;
            relaunch.UseShellExecute = false;
            relaunch.CreateNoWindow = false;

            System.Collections.Specialized.StringCollection remove = new System.Collections.Specialized.StringCollection();
            foreach (string key in relaunch.EnvironmentVariables.Keys)
            {
                if (!String.IsNullOrEmpty(key) && key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase))
                    remove.Add(key);
            }
            int r;
            for (r = 0; r < remove.Count; r++) relaunch.EnvironmentVariables.Remove(remove[r]);

            Process.Start(relaunch);
        }

        try { File.Delete(contextPath); } catch { }
        return true;
    }

    // Intent: Decodes one Base64 UTF-8 field from the saved launch-context file.
    private static string DecodeLaunchField(string encoded)
    {
        if (encoded == null) encoded = "";
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    // Intent: Quotes one Windows command-line argument using backslash/quote escaping compatible with normal Windows argv parsing.
    private static string QuoteArgument(string value)
    {
        if (value == null) value = "";
        if (value.Length > 0 && value.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return value;

        StringBuilder sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        int i;
        for (i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                sb.Append('\\', backslashes);
                backslashes = 0;
            }
            sb.Append(c);
        }

        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    // Intent: Locates steam.exe without downloading or installing anything.
    // Workflow: checks current-user and machine registry locations, then the normal Program Files directories.
    private static string FindSteamExe()
    {
        string value = ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamExe");
        if (!String.IsNullOrEmpty(value) && File.Exists(value)) return value;

        string install = ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (String.IsNullOrEmpty(install)) install = ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!String.IsNullOrEmpty(install))
        {
            string candidate = Path.Combine(install, "steam.exe");
            if (File.Exists(candidate)) return candidate;
        }

        string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!String.IsNullOrEmpty(pf86))
        {
            string candidate = Path.Combine(pf86, "Steam", "steam.exe");
            if (File.Exists(candidate)) return candidate;
        }

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!String.IsNullOrEmpty(pf))
        {
            string candidate = Path.Combine(pf, "Steam", "steam.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }

    // Intent: Reads a string-valued registry setting defensively; inaccessible or missing values resolve to an empty string instead of aborting relaunch.
    private static string ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using (RegistryKey key = hive.OpenSubKey(subKey))
            {
                if (key == null) return "";
                object value = key.GetValue(valueName);
                if (value == null) return "";
                return value.ToString().Replace('/', Path.DirectorySeparatorChar);
            }
        }
        catch { return ""; }
    }

    // Intent: Validates whether a string is a direct host:port or [IPv6]:port endpoint rather than a Steam/lobby identifier.
    // Note: retained as a small validation helper even though reconnect consumption occurs in the client plugin.
    private static bool LooksLikeDirectEndpoint(string host)
    {
        if (String.IsNullOrEmpty(host)) return false;
        host = host.Trim();
        if (host.IndexOf(' ') >= 0 || host.IndexOf('\r') >= 0 || host.IndexOf('\n') >= 0) return false;
        if (host.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase) || host.StartsWith("Steamworks.", StringComparison.OrdinalIgnoreCase)) return false;

        int colon;
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            int close = host.LastIndexOf(']');
            if (close <= 1 || close + 2 >= host.Length || host[close + 1] != ':') return false;
            colon = close + 1;
        }
        else
        {
            colon = host.LastIndexOf(':');
            if (colon <= 0) return false;
        }

        int port;
        if (!Int32.TryParse(host.Substring(colon + 1), out port) || port < 1 || port > 65535) return false;
        return true;
    }

    // Intent: Produces a simple quoted string after removing embedded quotes for legacy/safe command construction.
    private static string Quote(string s)
    {
        return "\"" + (s ?? "").Replace("\"", "") + "\"";
    }

    // Intent: Applies the same Windows-safe relative-path rules used by the network-facing client and server.
    private static string NormalizeRelative(string value)
    {
        return AutoModSyncPathSafety.NormalizeRelative(value);
    }

    // Intent: Resolves one pending path beneath its fixed live/staging root and rejects reparse-point redirection.
    // Security: this is intentionally repeated in the helper because process restart creates a new TOCTOU boundary.
    private static string SafeUnder(string root, string rel)
    {
        return AutoModSyncPathSafety.SafeUnderRoot(root, rel, true);
    }
}
