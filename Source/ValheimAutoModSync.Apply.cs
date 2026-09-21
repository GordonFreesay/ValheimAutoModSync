using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using System.Reflection;

[assembly: AssemblyTitle("Valheim AutoModSync Apply Helper")]
[assembly: AssemblyDescription("Applies verified staged AutoModSync BepInEx files after Valheim exits, then relaunches Valheim.")]
[assembly: AssemblyCompany("GordonFreesay")]
[assembly: AssemblyProduct("Valheim AutoModSync")]
[assembly: AssemblyVersion("2.5.0.0")]
[assembly: AssemblyFileVersion("2.5.0.0")]

internal static class Program
{
    // Intent: Out-of-process apply/relaunch entry point, started only after the client has downloaded and verified staged files.
    // Workflow: waits for the old Valheim process to exit, atomically replaces staged plugin/patcher/allowlisted-config files where possible, preserves reconnect state, then relaunches through the saved package-manager context, Steam, or direct executable fallback.
    private static int Main(string[] args)
    {
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
            // Give Steam a moment to observe that the old Valheim process is gone.
            // Launching too quickly can cause Steam to ignore the new app-launch request.
            Thread.Sleep(1500);

            string self = typeof(Program).Assembly.Location;
            string helperDir = Path.GetDirectoryName(self);
            string amsRoot = helperDir;
            if (args.Length > 1 && !String.IsNullOrEmpty(args[1]))
            {
                amsRoot = Path.GetFullPath(args[1]);
            }
            string bepinexRoot = Directory.GetParent(amsRoot).FullName;
            string gameRoot = Directory.GetParent(bepinexRoot).FullName;
            string pluginRoot = Path.Combine(bepinexRoot, "plugins");
            string patcherRoot = Path.Combine(bepinexRoot, "patchers");
            string configRoot = Path.Combine(bepinexRoot, "config");
            string stagingRoot = Path.Combine(amsRoot, "staging");
            string pending = Path.Combine(amsRoot, "pending.txt");
            string reconnectFile = Path.Combine(amsRoot, "reconnect.txt");

            if (File.Exists(pending))
            {
                string[] rels = File.ReadAllLines(pending);
                int i;
                for (i = 0; i < rels.Length; i++)
                {
                    string item = rels[i] ?? "";
                    if (item.Length < 3 || item[1] != ':') continue;
                    char kind = item[0];
                    string rel = NormalizeRelative(item.Substring(2));
                    if (rel.Length == 0) continue;

                    string srcRoot;
                    string dstRoot;
                    if (kind == 'P') { srcRoot = Path.Combine(stagingRoot, "plugins"); dstRoot = pluginRoot; }
                    else if (kind == 'R') { srcRoot = Path.Combine(stagingRoot, "patchers"); dstRoot = patcherRoot; }
                    else if (kind == 'C')
                    {
                        if (String.Equals(Path.GetFileName(rel), "ValheimAutoModSync.private.xml", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Refusing to apply an AutoModSync private identity as synchronized config.");
                        srcRoot = Path.Combine(stagingRoot, "config");
                        dstRoot = configRoot;
                    }
                    else throw new InvalidDataException("Unsupported AutoModSync pending-file kind.");
                    string src = SafeUnder(srcRoot, rel) + ".amsnew";
                    string dst = SafeUnder(dstRoot, rel);

                    if (!File.Exists(src)) continue;
                    string parent = Path.GetDirectoryName(dst);
                    if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                    string backup = dst + ".amsbak";
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                    if (File.Exists(dst))
                    {
                        try { File.Replace(src, dst, backup, true); }
                        catch
                        {
                            File.Copy(src, dst, true);
                            File.Delete(src);
                        }
                    }
                    else
                    {
                        File.Move(src, dst);
                    }
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                }
                try { File.Delete(pending); } catch { }
            }

            // reconnect.txt is intentionally left in place. The newly loaded
            // AutoModSync client consumes it once and performs the reconnect through
            // Valheim's own FejdStartup/ServerJoinData flow after the main menu exists.
            //
            // Package managers such as Thunderstore/r2modman launch the real valheim.exe
            // with profile-specific Doorstop arguments while BepInEx itself lives under
            // the profile directory. Reuse that exact saved launch context first.
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
            try
            {
                string dir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
                File.WriteAllText(Path.Combine(dir, "apply-error.txt"), ex.ToString());
            }
            catch { }
            return 1;
        }
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

    // Intent: Normalizes a relative AutoModSync path and rejects absolute/parent-traversal/control-character forms before filesystem use.
    private static string NormalizeRelative(string value)
    {
        if (value == null) return "";
        value = value.Replace('\\', '/').TrimStart('/');
        if (value.Length == 0 || value == ".." || value.IndexOf("../", StringComparison.Ordinal) >= 0 || value.IndexOf(':') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) return "";
        return value;
    }

    // Intent: Resolves a normalized relative path underneath an expected root and verifies the resulting full path cannot escape that root.
    private static string SafeUnder(string root, string rel)
    {
        rel = NormalizeRelative(rel);
        if (rel.Length == 0) throw new InvalidDataException("Unsafe AutoModSync path.");
        string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escaped AutoModSync root.");
        return full;
    }
}
