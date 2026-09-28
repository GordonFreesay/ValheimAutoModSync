using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ValheimAutoModSync
{
    // Intent: Applies and verifies an explicit Windows DACL for the persistent AutoModSync server signing credential.
    // Security: the private key is readable/writable only by the current Windows identity, LocalSystem, and local Administrators; inherited/broad ACL entries are removed.
    internal static class AutoModSyncPrivateKeySecurity
    {
        // Intent: Replaces the private-key file DACL with a protected allow-list for the current identity, LocalSystem, and local Administrators, then verifies the result.
        // Fail-closed: callers must treat any ACL write/verification failure as a signing-identity failure and must not continue using the key.
        internal static void HardenPrivateKeyFile(string path)
        {
            path = Path.GetFullPath(path ?? "");
            if (!File.Exists(path)) throw new FileNotFoundException("AutoModSync private signing key does not exist.", path);

            SecurityIdentifier current = CurrentUserSid();
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            SecurityIdentifier administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);

            HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddFullControl(security, current, added);
            AddFullControl(security, system, added);
            AddFullControl(security, administrators, added);

            File.SetAccessControl(path, security);
            VerifyPrivateKeyFile(path);
        }

        // Intent: Verifies that the private-key DACL is protected from inheritance and contains no explicit principals beyond the current identity, LocalSystem, and local Administrators.
        internal static void VerifyPrivateKeyFile(string path)
        {
            path = Path.GetFullPath(path ?? "");
            if (!File.Exists(path)) throw new FileNotFoundException("AutoModSync private signing key does not exist.", path);

            SecurityIdentifier current = CurrentUserSid();
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            SecurityIdentifier administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            allowed.Add(current.Value);
            allowed.Add(system.Value);
            allowed.Add(administrators.Value);

            FileSecurity security = File.GetAccessControl(path, AccessControlSections.Access);
            if (!security.AreAccessRulesProtected)
                throw new UnauthorizedAccessException("AutoModSync private signing key still inherits filesystem permissions.");

            HashSet<string> fullControl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AuthorizationRuleCollection rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
            int i;
            for (i = 0; i < rules.Count; i++)
            {
                FileSystemAccessRule rule = rules[i] as FileSystemAccessRule;
                if (rule == null) continue;

                SecurityIdentifier sid = rule.IdentityReference as SecurityIdentifier;
                if (sid == null || !allowed.Contains(sid.Value))
                    throw new UnauthorizedAccessException("AutoModSync private signing key ACL contains an unexpected principal.");

                if (rule.AccessControlType != AccessControlType.Allow)
                    throw new UnauthorizedAccessException("AutoModSync private signing key ACL contains a deny rule.");

                if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                    fullControl.Add(sid.Value);
            }

            foreach (string sid in allowed)
            {
                if (!fullControl.Contains(sid))
                    throw new UnauthorizedAccessException("AutoModSync private signing key ACL is missing required full-control access.");
            }
        }

        // Intent: Returns the exact Windows SID under which the current process is running so ACL hardening follows the real Valheim/server runtime identity.
        private static SecurityIdentifier CurrentUserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
            {
                if (identity == null || identity.User == null)
                    throw new UnauthorizedAccessException("AutoModSync could not determine the current Windows identity for private-key protection.");
                return identity.User;
            }
        }

        // Intent: Adds one explicit full-control allow rule once, avoiding duplicate rules when the current process already runs as SYSTEM or an administrator SID.
        private static void AddFullControl(FileSecurity security, SecurityIdentifier sid, HashSet<string> added)
        {
            if (security == null || sid == null || added == null) throw new ArgumentNullException();
            if (!added.Add(sid.Value)) return;

            FileSystemAccessRule rule = new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow);
            security.AddAccessRule(rule);
        }
    }
}
