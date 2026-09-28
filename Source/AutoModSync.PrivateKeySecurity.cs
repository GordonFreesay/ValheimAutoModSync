using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ValheimAutoModSync
{
    // Intent: Applies and verifies an explicit Windows DACL for the persistent AutoModSync server signing credential.
    // Compatibility: uses native Windows security APIs because Unity/Valheim Mono does not implement the WindowsIdentity SID/token APIs used by desktop .NET Framework.
    // Security: the private key is readable/writable only by the current process identity, LocalSystem, and local Administrators; inherited/broad ACL entries are removed.
    internal static class AutoModSyncPrivateKeySecurity
    {
        private const uint TokenQuery = 0x0008;
        private const int TokenUser = 1;
        private const int SeFileObject = 1;
        private const uint DaclSecurityInformation = 0x00000004;
        private const uint ProtectedDaclSecurityInformation = 0x80000000;
        private const ushort SeDaclProtected = 0x1000;
        private const byte AccessAllowedAceType = 0x00;
        private const uint FileAllAccess = 0x001F01FF;
        private const uint SddlRevision1 = 1;

        // Intent: Replaces the private-key file DACL with a protected allow-list for the current process identity, LocalSystem, and local Administrators, then verifies the exact result.
        // Fail-closed: callers must treat any ACL write/verification failure as a signing-identity failure and must not continue using the key.
        internal static void HardenPrivateKeyFile(string path)
        {
            path = Path.GetFullPath(path ?? "");
            if (!File.Exists(path)) throw new FileNotFoundException("AutoModSync private signing key does not exist.", path);

            HashSet<string> allowed = AllowedSidStrings();
            string sddl = BuildProtectedDaclSddl(allowed);

            IntPtr securityDescriptor = IntPtr.Zero;
            try
            {
                uint descriptorBytes;
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    sddl, SddlRevision1, out securityDescriptor, out descriptorBytes))
                    ThrowLastWin32("AutoModSync could not build the private-key security descriptor");

                bool daclPresent;
                bool daclDefaulted;
                IntPtr dacl;
                if (!GetSecurityDescriptorDacl(securityDescriptor, out daclPresent, out dacl, out daclDefaulted)
                    || !daclPresent
                    || dacl == IntPtr.Zero)
                    ThrowLastWin32("AutoModSync could not read the generated private-key DACL");

                uint result = SetNamedSecurityInfoW(
                    path,
                    SeFileObject,
                    DaclSecurityInformation | ProtectedDaclSecurityInformation,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    dacl,
                    IntPtr.Zero);
                if (result != 0)
                    throw new UnauthorizedAccessException("AutoModSync could not apply the private-key ACL. Win32 error " + result + ".");
            }
            finally
            {
                if (securityDescriptor != IntPtr.Zero) LocalFree(securityDescriptor);
            }

            VerifyPrivateKeyFile(path);
        }

        // Intent: Reads the private-key security descriptor back from Windows and verifies a protected DACL containing exactly one Full Control allow ACE for each approved SID and no inherited/broad entries.
        internal static void VerifyPrivateKeyFile(string path)
        {
            path = Path.GetFullPath(path ?? "");
            if (!File.Exists(path)) throw new FileNotFoundException("AutoModSync private signing key does not exist.", path);

            HashSet<string> allowed = AllowedSidStrings();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IntPtr owner;
            IntPtr group;
            IntPtr dacl;
            IntPtr sacl;
            IntPtr securityDescriptor;
            uint result = GetNamedSecurityInfoW(
                path,
                SeFileObject,
                DaclSecurityInformation,
                out owner,
                out group,
                out dacl,
                out sacl,
                out securityDescriptor);
            if (result != 0)
                throw new UnauthorizedAccessException("AutoModSync could not read the private-key ACL. Win32 error " + result + ".");

            try
            {
                ushort control;
                uint revision;
                if (!GetSecurityDescriptorControl(securityDescriptor, out control, out revision))
                    ThrowLastWin32("AutoModSync could not inspect private-key ACL protection");
                if ((control & SeDaclProtected) == 0)
                    throw new UnauthorizedAccessException("AutoModSync private signing key still inherits filesystem permissions.");
                if (dacl == IntPtr.Zero)
                    throw new UnauthorizedAccessException("AutoModSync private signing key has no DACL.");

                int aceCount = (ushort)Marshal.ReadInt16(dacl, 4);
                if (aceCount != allowed.Count)
                    throw new UnauthorizedAccessException("AutoModSync private signing key ACL contains an unexpected number of access rules.");

                int i;
                for (i = 0; i < aceCount; i++)
                {
                    IntPtr ace;
                    if (!GetAce(dacl, i, out ace) || ace == IntPtr.Zero)
                        ThrowLastWin32("AutoModSync could not inspect a private-key ACL entry");

                    byte aceType = Marshal.ReadByte(ace, 0);
                    byte aceFlags = Marshal.ReadByte(ace, 1);
                    uint mask = unchecked((uint)Marshal.ReadInt32(ace, 4));
                    if (aceType != AccessAllowedAceType)
                        throw new UnauthorizedAccessException("AutoModSync private signing key ACL contains a non-Allow rule.");
                    if (aceFlags != 0)
                        throw new UnauthorizedAccessException("AutoModSync private signing key ACL contains inherited or unexpected ACE flags.");
                    if ((mask & FileAllAccess) != FileAllAccess)
                        throw new UnauthorizedAccessException("AutoModSync private signing key ACL contains an entry without Full Control.");

                    string sid = SidPointerToString(new IntPtr(ace.ToInt64() + 8L));
                    if (!allowed.Contains(sid))
                        throw new UnauthorizedAccessException("AutoModSync private signing key ACL contains an unexpected principal.");
                    if (!seen.Add(sid))
                        throw new UnauthorizedAccessException("AutoModSync private signing key ACL contains a duplicate principal.");
                }

                foreach (string sid in allowed)
                {
                    if (!seen.Contains(sid))
                        throw new UnauthorizedAccessException("AutoModSync private signing key ACL is missing a required principal.");
                }
            }
            finally
            {
                if (securityDescriptor != IntPtr.Zero) LocalFree(securityDescriptor);
            }
        }

        // Intent: Builds the exact three-principal SID allow-list while naturally deduplicating the current identity if it is already LocalSystem.
        private static HashSet<string> AllowedSidStrings()
        {
            HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            allowed.Add(CurrentProcessUserSid());
            allowed.Add("S-1-5-18");
            allowed.Add("S-1-5-32-544");
            return allowed;
        }

        // Intent: Produces one protected SDDL DACL with Full Control allow ACEs only for the approved SID set; no inherited ACEs or broad groups are carried forward.
        private static string BuildProtectedDaclSddl(HashSet<string> allowed)
        {
            if (allowed == null || allowed.Count == 0) throw new ArgumentException("AutoModSync private-key SID allow-list is empty.");

            System.Text.StringBuilder sddl = new System.Text.StringBuilder("D:P");
            foreach (string sid in allowed)
                sddl.Append("(A;;FA;;;").Append(sid).Append(")");
            return sddl.ToString();
        }

        // Intent: Retrieves the exact process-token user SID through Win32 APIs that work under Valheim's Windows Mono runtime.
        private static string CurrentProcessUserSid()
        {
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token) || token == IntPtr.Zero)
                    ThrowLastWin32("AutoModSync could not open the current process token for private-key protection");

                uint needed = 0;
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out needed);
                if (needed == 0)
                    ThrowLastWin32("AutoModSync could not size the current process token user information");

                buffer = Marshal.AllocHGlobal(unchecked((int)needed));
                if (!GetTokenInformation(token, TokenUser, buffer, needed, out needed))
                    ThrowLastWin32("AutoModSync could not read the current process token user information");

                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero)
                    throw new UnauthorizedAccessException("AutoModSync current process token did not contain a user SID.");
                return SidPointerToString(sid);
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        // Intent: Converts one native SID pointer into its canonical S-1-... string without relying on Mono's unimplemented WindowsIdentity.User property.
        private static string SidPointerToString(IntPtr sid)
        {
            IntPtr text = IntPtr.Zero;
            try
            {
                if (sid == IntPtr.Zero || !ConvertSidToStringSidW(sid, out text) || text == IntPtr.Zero)
                    ThrowLastWin32("AutoModSync could not convert a Windows SID");
                string value = Marshal.PtrToStringUni(text);
                if (String.IsNullOrWhiteSpace(value))
                    throw new UnauthorizedAccessException("AutoModSync resolved an empty Windows SID.");
                return value;
            }
            finally
            {
                if (text != IntPtr.Zero) LocalFree(text);
            }
        }

        // Intent: Turns the immediately preceding Win32 failure into a deterministic fail-closed signing-key exception without exposing unrelated system state.
        private static void ThrowLastWin32(string message)
        {
            int error = Marshal.GetLastWin32Error();
            throw new UnauthorizedAccessException(message + ". Win32 error " + error + ".");
        }

        // Intent: Returns the pseudo-handle for the current process so its access token can be queried without opening another process handle.\n        [DllImport("kernel32.dll")]\n        private static extern IntPtr GetCurrentProcess();

        // Intent: Releases the native process-token handle acquired for current-user SID discovery.\n        [DllImport("kernel32.dll", SetLastError = true)]\n        private static extern bool CloseHandle(IntPtr handle);

        // Intent: Releases LocalAlloc-backed buffers returned by Windows SID/security-descriptor conversion APIs.\n        [DllImport("kernel32.dll")]\n        private static extern IntPtr LocalFree(IntPtr memory);

        // Intent: Opens the current process token with query access so AutoModSync can determine the exact runtime account SID.\n        [DllImport("advapi32.dll", SetLastError = true)]\n        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        // Intent: Reads TOKEN_USER data from the current process token for exact runtime-account SID discovery.\n        [DllImport("advapi32.dll", SetLastError = true)]\n        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

        // Intent: Converts a native SID into canonical S-1-... text for deterministic ACL allow-list comparison.\n        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]\n        private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

        // Intent: Converts the protected exact-principal SDDL string into a native security descriptor for DACL application.\n        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]\n        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string stringSecurityDescriptor, uint stringSdRevision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

        // Intent: Extracts the DACL pointer from the generated native security descriptor before applying it to the private-key file.\n        [DllImport("advapi32.dll", SetLastError = true)]\n        private static extern bool GetSecurityDescriptorDacl(IntPtr securityDescriptor, out bool daclPresent, out IntPtr dacl, out bool daclDefaulted);

        // Intent: Applies the protected exact-principal DACL to the private-key file without changing owner, group, or SACL.\n        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]\n        private static extern uint SetNamedSecurityInfoW(string objectName, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        // Intent: Reads the applied file security descriptor and DACL back from Windows for independent post-write verification.\n        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]\n        private static extern uint GetNamedSecurityInfoW(string objectName, int objectType, uint securityInfo, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

        // Intent: Reads security-descriptor control flags so verification can require that DACL inheritance is disabled.\n        [DllImport("advapi32.dll", SetLastError = true)]\n        private static extern bool GetSecurityDescriptorControl(IntPtr securityDescriptor, out ushort control, out uint revision);

        // Intent: Enumerates individual DACL ACEs so verification can reject unexpected principals, flags, rule types, or rights.\n        [DllImport("advapi32.dll", SetLastError = true)]\n        private static extern bool GetAce(IntPtr acl, int aceIndex, out IntPtr ace);
    }
}
