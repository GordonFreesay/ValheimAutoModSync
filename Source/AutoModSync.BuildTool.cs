using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using System.Reflection;

[assembly: AssemblyTitle("AutoModSync Build Tool")]
[assembly: AssemblyDescription("Release-build and server-identity utility for Valheim AutoModSync.")]
[assembly: AssemblyCompany("GordonFreesay")]
[assembly: AssemblyProduct("Valheim AutoModSync")]
[assembly: AssemblyVersion("2.6.0.0")]
[assembly: AssemblyFileVersion("2.6.0.0")]

internal static class BuildTool
{
    private sealed class Entry
    {
        public char Mode;
        public string Source;
        public string Relative;
        public long Offset;
        public long Size;
        public string Sha;
    }

    // Intent: Command-line entry point used by the release/installer scripts.
    // Workflow: validates the requested subcommand, dispatches to identity/pack/findserver/sha256 helpers, and converts unexpected exceptions into a non-zero exit code.
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) return Usage();
            string cmd = args[0].ToLowerInvariant();
            if (cmd == "identity" && args.Length == 3) return EnsureIdentity(args[1], args[2]);
            if (cmd == "pack" && args.Length == 6) return Pack(args[1], args[2], args[3], args[4], args[5]);
            if (cmd == "findserver" && args.Length == 2) return FindServer(args[1]);
            if (cmd == "sha256" && args.Length == 2) return PrintSha256(args[1]);
            return Usage();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("AutoModSync BuildTool error: " + ex);
            return 1;
        }
    }

    // Intent: Prints the supported BuildTool command syntax and returns the conventional usage-error exit code.
    private static int Usage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  BuildTool identity <private.xml> <public.xml>");
        Console.Error.WriteLine("  BuildTool pack <version.template.dll> <version.dll> <bootstrapRoot> <client.dll> <apply.exe>");
        Console.Error.WriteLine("  BuildTool findserver <packageRoot>");
        Console.Error.WriteLine("  BuildTool sha256 <file>");
        return 2;
    }


    // Intent: Computes the SHA-256 of one file for deterministic integrity checks used by build and install scripts.
    // Workflow: canonicalizes the path, opens the file read-only, hashes the bytes, and prints lowercase hexadecimal.
    private static int PrintSha256(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("File not found for SHA-256", path);
        string hash;
        using (FileStream fs = File.OpenRead(path))
        using (SHA256 sha = SHA256.Create()) hash = ToHex(sha.ComputeHash(fs));
        Console.WriteLine(hash);
        return 0;
    }


    // Intent: Locates a Valheim Dedicated Server installation without modifying anything.
    // Workflow: builds candidates from nearby directories, Steam registry roots, libraryfolders.vdf, and common drive layouts, then returns the first folder containing valheim_server.exe.
    private static int FindServer(string packageRoot)
    {
        List<string> candidates = new List<string>();
        try
        {
            DirectoryInfo d = new DirectoryInfo(Path.GetFullPath(packageRoot));
            int up;
            for (up = 0; up < 7 && d != null; up++, d = d.Parent) candidates.Add(d.FullName);
        }
        catch { }

        List<string> steamRoots = new List<string>();
        AddRegistrySteamRoot(steamRoots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        AddRegistrySteamRoot(steamRoots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        AddRegistrySteamRoot(steamRoots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");

        int i;
        for (i = 0; i < steamRoots.Count; i++)
        {
            AddSteamCandidate(candidates, steamRoots[i]);
            AddLibrariesFromVdf(candidates, steamRoots[i]);
        }

        string[] drives = Environment.GetLogicalDrives();
        for (i = 0; i < drives.Length; i++)
        {
            string drive = drives[i];
            candidates.Add(Path.Combine(drive, "SteamLibrary", "steamapps", "common", "Valheim dedicated server"));
            candidates.Add(Path.Combine(drive, "Steam", "steamapps", "common", "Valheim dedicated server"));
            candidates.Add(Path.Combine(drive, "SteamCMD", "steamapps", "common", "Valheim dedicated server"));
            candidates.Add(Path.Combine(drive, "ValheimServer"));
            candidates.Add(Path.Combine(drive, "Valheim Dedicated Server"));
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (i = 0; i < candidates.Count; i++)
        {
            string c;
            try { c = Path.GetFullPath(candidates[i]); } catch { continue; }
            if (!seen.Add(c)) continue;
            if (IsServerRoot(c))
            {
                Console.WriteLine(c);
                return 0;
            }
        }
        return 3;
    }

    // Intent: Adds a Steam installation root from a registry value when that value exists and is readable; registry failures are intentionally non-fatal.
    private static void AddRegistrySteamRoot(List<string> roots, RegistryKey hive, string subkey, string valueName)
    {
        try
        {
            using (RegistryKey k = hive.OpenSubKey(subkey))
            {
                if (k == null) return;
                object v = k.GetValue(valueName);
                if (v != null && !String.IsNullOrEmpty(v.ToString())) roots.Add(v.ToString().Replace('/', Path.DirectorySeparatorChar));
            }
        }
        catch { }
    }

    // Intent: Converts a Steam root into the standard Valheim Dedicated Server candidate path and appends it to the search list.
    private static void AddSteamCandidate(List<string> candidates, string steamRoot)
    {
        if (String.IsNullOrEmpty(steamRoot)) return;
        candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "Valheim dedicated server"));
    }

    // Intent: Reads Steam libraryfolders.vdf and adds dedicated-server candidates from each declared library.
    // Workflow: performs a deliberately small parser for quoted path entries rather than changing Steam state.
    private static void AddLibrariesFromVdf(List<string> candidates, string steamRoot)
    {
        try
        {
            string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return;
            string[] lines = File.ReadAllLines(vdf);
            int i;
            for (i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                int first = line.IndexOf('\"', 6);
                if (first < 0) continue;
                int second = line.IndexOf('\"', first + 1);
                if (second < 0) continue;
                string path = line.Substring(first + 1, second - first - 1).Replace("\\\\", "\\");
                AddSteamCandidate(candidates, path);
            }
        }
        catch { }
    }

    // Intent: Performs the final read-only server-root test by checking for valheim_server.exe.
    private static bool IsServerRoot(string path)
    {
        try
        {
            return File.Exists(Path.Combine(path, "valheim_server.exe"));
        }
        catch { return false; }
    }

    // Intent: Creates or preserves the server RSA signing identity used to authenticate AutoModSync manifests.
    // Workflow: reuses an existing private key when present, otherwise generates a 2048-bit keypair, writes the public key, and prints its SHA-256 fingerprint.
    private static int EnsureIdentity(string privatePath, string publicPath)
    {
        privatePath = Path.GetFullPath(privatePath);
        publicPath = Path.GetFullPath(publicPath);
        string parent = Path.GetDirectoryName(privatePath);
        if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
        string publicXml;
        using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048))
        {
            rsa.PersistKeyInCsp = false;
            if (File.Exists(privatePath))
            {
                string privateXml = File.ReadAllText(privatePath).Trim();
                rsa.FromXmlString(privateXml);
                publicXml = rsa.ToXmlString(false);
                Console.WriteLine("Existing AutoModSync server identity preserved: " + privatePath);
            }
            else
            {
                string privateXml = rsa.ToXmlString(true);
                publicXml = rsa.ToXmlString(false);
                File.WriteAllText(privatePath, privateXml + Environment.NewLine, new UTF8Encoding(false));
                Console.WriteLine("Generated AutoModSync server identity: " + privatePath);
            }
        }
        File.WriteAllText(publicPath, publicXml + Environment.NewLine, new UTF8Encoding(false));
        byte[] bytes = Encoding.UTF8.GetBytes(publicXml);
        string fingerprint;
        using (SHA256 sha = SHA256.Create()) fingerprint = ToHex(sha.ComputeHash(bytes));
        Console.WriteLine("Server public fingerprint: " + fingerprint);
        return 0;
    }

    // Intent: Legacy one-file bootstrap packer retained for historical tooling compatibility; current transparent releases do not use the packed version.dll design.
    // Workflow: embeds only pinned bootstrap/runtime inputs plus AutoModSync files, records offsets/sizes/hashes in a manifest, and appends an AMS package footer.
    private static int Pack(string templatePath, string outputPath, string bootstrapRoot, string clientDll, string helperExe)
    {
        templatePath = Path.GetFullPath(templatePath);
        outputPath = Path.GetFullPath(outputPath);
        bootstrapRoot = Path.GetFullPath(bootstrapRoot);
        clientDll = Path.GetFullPath(clientDll);
        helperExe = Path.GetFullPath(helperExe);

        if (!File.Exists(templatePath)) throw new FileNotFoundException("version.dll template missing", templatePath);
        if (!File.Exists(clientDll)) throw new FileNotFoundException("client plugin missing", clientDll);
        if (!File.Exists(helperExe)) throw new FileNotFoundException("apply helper missing", helperExe);

        List<Entry> entries = new List<Entry>();
        // bootstrapRoot must be the verified pinned stock BepInExPack extraction,
        // never the live server installation. This keeps the universal client deterministic
        // and prevents unrelated server-local files from being embedded.
        AddFile(entries, 'M', Path.Combine(bootstrapRoot, "winhttp.dll"), "winhttp.dll", true);
        AddFile(entries, 'M', Path.Combine(bootstrapRoot, "doorstop_config.ini"), "doorstop_config.ini", true);
        AddDirectory(entries, 'M', Path.Combine(bootstrapRoot, "BepInEx", "core"), "BepInEx/core", true);
        // Do not embed BepInEx/patchers. AutoModSync needs no client patcher, and copying
        // a live server patchers directory could accidentally distribute unrelated code.
        AddFile(entries, 'A', clientDll, "BepInEx/plugins/ValheimAutoModSync.Client.dll", true);
        AddFile(entries, 'A', helperExe, "BepInEx/AutoModSync/ValheimAutoModSync.Apply.exe", true);

        string outDir = Path.GetDirectoryName(outputPath);
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

        using (FileStream output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] template = File.ReadAllBytes(templatePath);
            output.Write(template, 0, template.Length);

            int i;
            for (i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                e.Offset = output.Position;
                byte[] data = File.ReadAllBytes(e.Source);
                e.Size = data.LongLength;
                using (SHA256 sha = SHA256.Create()) e.Sha = ToHex(sha.ComputeHash(data));
                output.Write(data, 0, data.Length);
            }

            long manifestOffset = output.Position;
            StringBuilder manifest = new StringBuilder();
            for (i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                manifest.Append(e.Mode).Append('\t')
                    .Append(e.Sha).Append('\t')
                    .Append(e.Offset.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Relative.Replace('\\', '/')).Append('\n');
            }
            byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToString());
            output.Write(manifestBytes, 0, manifestBytes.Length);
            long manifestLength = manifestBytes.LongLength;

            byte[] magic = Encoding.ASCII.GetBytes("AMS2PKG1");
            output.Write(magic, 0, magic.Length);
            WriteInt64(output, manifestOffset);
            WriteInt64(output, manifestLength);
        }

        string sum;
        using (FileStream fs = File.OpenRead(outputPath))
        using (SHA256 sha = SHA256.Create()) sum = ToHex(sha.ComputeHash(fs));
        File.WriteAllText(outputPath + ".sha256.txt", sum + "  version.dll" + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine("Generated one-file client bootstrap:");
        Console.WriteLine("  " + outputPath);
        Console.WriteLine("Embedded bootstrap files: " + entries.Count);
        return 0;
    }

    // Intent: Adds every file under a source directory to a legacy package-entry list while preserving relative paths and enforcing required-directory semantics.
    private static void AddDirectory(List<Entry> list, char mode, string sourceDir, string targetDir, bool required)
    {
        if (!Directory.Exists(sourceDir))
        {
            if (required) throw new DirectoryNotFoundException("Required directory missing: " + sourceDir);
            return;
        }
        string[] files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        int i;
        for (i = 0; i < files.Length; i++)
        {
            string rel = files[i].Substring(sourceDir.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            AddFile(list, mode, files[i], targetDir.TrimEnd('/') + "/" + rel.Replace('\\', '/'), true);
        }
    }

    // Intent: Adds one validated source file to the legacy package-entry list.
    // Security: rejects parent traversal, control characters, and tab-delimited manifest injection in embedded target paths.
    private static void AddFile(List<Entry> list, char mode, string source, string target, bool required)
    {
        if (!File.Exists(source))
        {
            if (required) throw new FileNotFoundException("Required bootstrap file missing", source);
            return;
        }
        Entry e = new Entry();
        e.Mode = mode;
        e.Source = source;
        e.Relative = target.Replace('\\', '/').TrimStart('/');
        if (e.Relative.IndexOf("..", StringComparison.Ordinal) >= 0 || e.Relative.IndexOf('\t') >= 0 || e.Relative.IndexOf('\r') >= 0 || e.Relative.IndexOf('\n') >= 0)
            throw new InvalidDataException("Unsafe embedded path: " + e.Relative);
        list.Add(e);
    }

    // Intent: Writes a 64-bit integer in little-endian form so the legacy package footer has a deterministic binary layout.
    private static void WriteInt64(Stream s, long value)
    {
        byte[] b = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian) Array.Reverse(b);
        s.Write(b, 0, b.Length);
    }

    // Intent: Converts bytes to lowercase hexadecimal for hashes and fingerprints without culture-dependent formatting.
    private static string ToHex(byte[] bytes)
    {
        StringBuilder sb = new StringBuilder(bytes.Length * 2);
        int i;
        for (i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
