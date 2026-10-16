using System;
using System.IO;

namespace ValheimAutoModSync
{
    internal static class LinuxServerKeyHarness
    {
        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        public static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "ams-linux-key-" + Guid.NewGuid().ToString("N"));
            string key = Path.Combine(root, "ValheimAutoModSync.private.xml");
            try
            {
                Directory.CreateDirectory(root);
                AutoModSyncPrivateKeySecurity.WriteNewPrivateKeyFile(key, "server-identity\n");
                Require(AutoModSyncPrivateKeySecurity.ReadPrivateKeyFile(key) == "server-identity\n", "private key content changed during protected write/read");
                AutoModSyncPrivateKeySecurity.VerifyPrivateKeyFile(key);

                bool rejectedOverwrite = false;
                try { AutoModSyncPrivateKeySecurity.WriteNewPrivateKeyFile(key, "replacement\n"); }
                catch (IOException) { rejectedOverwrite = true; }
                catch (UnauthorizedAccessException) { rejectedOverwrite = true; }
                Require(rejectedOverwrite, "protected key creation overwrote an existing identity");

                Console.WriteLine("PASS: Linux server key creation, verification, readback, and no-overwrite protection.");
                return 0;
            }
            finally
            {
                try { if (File.Exists(key)) File.Delete(key); } catch { }
                try { if (Directory.Exists(root)) Directory.Delete(root); } catch { }
            }
        }
    }
}
