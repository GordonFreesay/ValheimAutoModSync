using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

internal static class Program
{
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

                    string src;
                    string dst;
                    if (kind != 'P') continue;
                    src = SafeUnder(Path.Combine(stagingRoot, "plugins"), rel) + ".amsnew";
                    dst = SafeUnder(pluginRoot, rel);

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

    private static string Quote(string s)
    {
        return "\"" + (s ?? "").Replace("\"", "") + "\"";
    }

    private static string NormalizeRelative(string value)
    {
        if (value == null) return "";
        value = value.Replace('\\', '/').TrimStart('/');
        if (value.Length == 0 || value == ".." || value.IndexOf("../", StringComparison.Ordinal) >= 0 || value.IndexOf(':') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) return "";
        return value;
    }

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
