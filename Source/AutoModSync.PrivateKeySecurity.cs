using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ValheimAutoModSync
{
    // Windows retains the original protected DACL; Linux x86_64 uses owner-only permissions.
    // Linux uses descriptor-relative operations and the stable statx ABI, not a guessed struct stat layout.
    // The configured parent directory is trusted (Docker intentionally symlinks BepInEx/config).
    // Key-file symlinks, hard links, special files, foreign owners, and writable shared parents are refused.
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
            if (UseLinuxFileSecurity())
            {
                using (LinuxKeyFile key = new LinuxKeyFile(path, false))
                {
                    if (LinuxFchmod(key.FileDescriptor, LinuxPrivateMode) != 0)
                        ThrowLastLinux("AutoModSync could not protect the private-key permissions");
                    key.Verify(true);
                }
                return;
            }

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
            if (UseLinuxFileSecurity())
            {
                using (LinuxKeyFile key = new LinuxKeyFile(path, false)) key.Verify(true);
                return;
            }

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

        // A new Linux key is private from its first byte, independently of the process umask.
        // CreateNew/O_EXCL prevents a competing startup from overwriting an existing identity.
        internal static void WriteNewPrivateKeyFile(string path, string contents)
        {
            if (contents == null) throw new ArgumentNullException("contents");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(contents);
            if (bytes.Length > LinuxMaxKeyBytes) throw new InvalidDataException("AutoModSync private key is too large.");

            if (!UseLinuxFileSecurity())
            {
                using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(bytes, 0, bytes.Length);
                return;
            }

            using (LinuxKeyFile key = new LinuxKeyFile(path, true))
            {
                bool complete = false;
                try
                {
                    if (LinuxFchmod(key.FileDescriptor, LinuxPrivateMode) != 0)
                        ThrowLastLinux("AutoModSync could not protect a new private key");
                    key.Verify(true);
                    GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    try
                    {
                        int offset = 0;
                        while (offset < bytes.Length)
                        {
                            long count = LinuxWrite(key.FileDescriptor, IntPtr.Add(pinned.AddrOfPinnedObject(), offset),
                                new UIntPtr((uint)(bytes.Length - offset))).ToInt64();
                            if (count < 0)
                            {
                                if (Marshal.GetLastWin32Error() == LinuxEintr) continue;
                                ThrowLastLinux("AutoModSync could not write the private key");
                            }
                            if (count == 0 || count > bytes.Length - offset)
                                throw new IOException("AutoModSync private-key write made invalid progress.");
                            offset += (int)count;
                        }
                    }
                    finally { pinned.Free(); }

                    if (LinuxFsync(key.FileDescriptor) != 0)
                        ThrowLastLinux("AutoModSync could not flush the private key");
                    key.Verify(true);
                    complete = true;
                }
                finally { if (!complete) key.DeleteCreatedFileBestEffort(); }
            }
        }

        // Linux reads the exact verified descriptor rather than reopening the path after hardening.
        internal static string ReadPrivateKeyFile(string path)
        {
            if (!UseLinuxFileSecurity())
            {
                VerifyPrivateKeyFile(path);
                return File.ReadAllText(path);
            }

            using (LinuxKeyFile key = new LinuxKeyFile(path, false))
            using (MemoryStream contents = new MemoryStream())
            {
                key.Verify(true);
                byte[] buffer = new byte[4096];
                while (true)
                {
                    long count = LinuxRead(key.FileDescriptor, buffer, new UIntPtr((uint)buffer.Length)).ToInt64();
                    if (count < 0)
                    {
                        if (Marshal.GetLastWin32Error() == LinuxEintr) continue;
                        ThrowLastLinux("AutoModSync could not read the private key");
                    }
                    if (count == 0) break;
                    if (count > buffer.Length || contents.Length + count > LinuxMaxKeyBytes)
                        throw new InvalidDataException("AutoModSync private key exceeds the read limit.");
                    contents.Write(buffer, 0, (int)count);
                }
                key.Verify(true);
                byte[] bytes = contents.ToArray();
                int start = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
            }
        }

        // These open flags are Linux x86_64 values. Reject other Unix ABIs before any mutation.
        private static bool UseLinuxFileSecurity()
        {
            PlatformID platform = Environment.OSVersion.Platform;
            if (platform == PlatformID.Win32NT) return false;
            if (platform != PlatformID.Unix)
                throw new PlatformNotSupportedException("AutoModSync key protection supports Windows and Linux x86_64.");
            byte[] uts = new byte[4096];
            if (LinuxUname(uts) != 0) ThrowLastLinux("AutoModSync could not identify the Unix platform");
            string system = Encoding.ASCII.GetString(uts, 0, 65).TrimEnd('\0');
            string machine = Encoding.ASCII.GetString(uts, 4 * 65, 65).TrimEnd('\0');
            if (IntPtr.Size != 8 || system != "Linux" || machine != "x86_64")
                throw new PlatformNotSupportedException("This AutoModSync patch supports Linux x86_64 only.");
            return true;
        }

        private const int LinuxReadOnly = 0;
        private const int LinuxWriteOnly = 1;
        private const int LinuxCreate = 0x40;
        private const int LinuxExclusive = 0x80;
        private const int LinuxNonBlock = 0x800;
        private const int LinuxDirectory = 0x10000;
        private const int LinuxNoFollow = 0x20000;
        private const int LinuxCloseOnExec = 0x80000;
        private const int LinuxAtEmptyPath = 0x1000;
        private const int LinuxAtSymlinkNoFollow = 0x100;
        private const uint LinuxStatxBasicStats = 0x7ff;
        private const uint LinuxStatxRequired = 0x10f; // TYPE, MODE, NLINK, UID, INO
        private const uint LinuxPrivateMode = 0x180; // 0600
        private const int LinuxMaxKeyBytes = 65536;
        private const int LinuxEintr = 4;

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct LinuxStatx
        {
            [FieldOffset(0)] internal uint Mask;
            [FieldOffset(16)] internal uint Links;
            [FieldOffset(20)] internal uint Uid;
            [FieldOffset(28)] internal ushort Mode;
            [FieldOffset(32)] internal ulong Inode;
            [FieldOffset(136)] internal uint DeviceMajor;
            [FieldOffset(140)] internal uint DeviceMinor;
        }

        private static LinuxStatx LinuxInspect(int directory, string name, int flags)
        {
            LinuxStatx info;
            try
            {
                if (LinuxStatxCall(directory, name, flags, LinuxStatxBasicStats, out info) != 0)
                    ThrowLastLinux("AutoModSync could not inspect private-key filesystem metadata");
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new PlatformNotSupportedException("AutoModSync Linux key protection requires libc statx (glibc 2.28+) and Linux 4.11+.", ex);
            }
            if ((info.Mask & LinuxStatxRequired) != LinuxStatxRequired)
                throw new UnauthorizedAccessException("AutoModSync filesystem omitted required ownership or permission metadata.");
            return info;
        }

        private static void ThrowLastLinux(string message)
        {
            int error = Marshal.GetLastWin32Error();
            throw new UnauthorizedAccessException(message + ". Linux errno " + error + ".");
        }

        private sealed class LinuxKeyFile : IDisposable
        {
            internal int FileDescriptor = -1;
            private int _directoryDescriptor = -1;
            private readonly string _name;
            private readonly bool _created;

            internal LinuxKeyFile(string path, bool create)
            {
                if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Private-key path is required.", "path");
                path = Path.GetFullPath(path);
                _name = Path.GetFileName(path);
                _created = create;
                try
                {
                    // The operator-configured root can be a symlink; the opened directory is checked directly.
                    _directoryDescriptor = LinuxOpen(Path.GetDirectoryName(path), LinuxReadOnly | LinuxDirectory | LinuxCloseOnExec);
                    if (_directoryDescriptor < 0) ThrowLastLinux("AutoModSync could not open the private-key directory");
                    LinuxStatx directory = LinuxInspect(_directoryDescriptor, "", LinuxAtEmptyPath);
                    uint uid = LinuxGetEffectiveUid();
                    if ((directory.Mode & 0xf000) != 0x4000 || (directory.Uid != uid && directory.Uid != 0)
                        || (directory.Mode & 0x12) != 0) // no group/other write (0022)
                        throw new UnauthorizedAccessException("AutoModSync private-key directory must belong to the runtime user or root and must not be group/world writable.");

                    int flags = (create ? LinuxWriteOnly | LinuxCreate | LinuxExclusive : LinuxReadOnly)
                        | LinuxNoFollow | LinuxNonBlock | LinuxCloseOnExec;
                    FileDescriptor = LinuxOpenAt(_directoryDescriptor, _name, flags, LinuxPrivateMode);
                    if (FileDescriptor < 0) ThrowLastLinux("AutoModSync could not open the private key without following links or overwriting a file");
                    Verify(false);
                }
                catch { Dispose(); throw; }
            }

            internal void Verify(bool requirePrivateMode)
            {
                LinuxStatx file = LinuxInspect(FileDescriptor, "", LinuxAtEmptyPath);
                if ((file.Mode & 0xf000) != 0x8000 || file.Links != 1 || file.Uid != LinuxGetEffectiveUid())
                    throw new UnauthorizedAccessException("AutoModSync private key must be a regular, singly-linked file owned by the runtime user.");
                if (requirePrivateMode && (file.Mode & 0xfff) != LinuxPrivateMode)
                    throw new UnauthorizedAccessException("AutoModSync private key must have exactly 0600 permissions.");
                LinuxStatx named = LinuxInspect(_directoryDescriptor, _name, LinuxAtSymlinkNoFollow);
                if (!SameFile(file, named))
                    throw new UnauthorizedAccessException("AutoModSync private-key path changed while verifying the identity.");
            }

            private static bool SameFile(LinuxStatx a, LinuxStatx b)
            {
                return a.Inode == b.Inode && a.DeviceMajor == b.DeviceMajor && a.DeviceMinor == b.DeviceMinor;
            }

            internal void DeleteCreatedFileBestEffort()
            {
                if (!_created || FileDescriptor < 0 || _directoryDescriptor < 0) return;
                try
                {
                    LinuxStatx file = LinuxInspect(FileDescriptor, "", LinuxAtEmptyPath);
                    LinuxStatx named = LinuxInspect(_directoryDescriptor, _name, LinuxAtSymlinkNoFollow);
                    if (SameFile(file, named)) LinuxUnlinkAt(_directoryDescriptor, _name, 0);
                }
                catch { }
            }

            public void Dispose()
            {
                if (FileDescriptor >= 0) { LinuxClose(FileDescriptor); FileDescriptor = -1; }
                if (_directoryDescriptor >= 0) { LinuxClose(_directoryDescriptor); _directoryDescriptor = -1; }
            }
        }

        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "uname", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern int LinuxUname([Out] byte[] buffer);
        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "geteuid", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint LinuxGetEffectiveUid();
        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "open", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int LinuxOpen(string path, int flags);
        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "openat", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int LinuxOpenAt(int directory, string path, int flags, uint mode);
        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "statx", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int LinuxStatxCall(int directory, string path, int flags, uint mask, out LinuxStatx info);
        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "fchmod", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern int LinuxFchmod(int descriptor, uint mode);
        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "read", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern IntPtr LinuxRead(int descriptor, [Out] byte[] buffer, UIntPtr count);
        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "write", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern IntPtr LinuxWrite(int descriptor, IntPtr buffer, UIntPtr count);
        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "fsync", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern int LinuxFsync(int descriptor);
        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "unlinkat", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int LinuxUnlinkAt(int directory, string path, int flags);
        [DllImport("libc.so.6", ExactSpelling = true, EntryPoint = "close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int LinuxClose(int descriptor);

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

        [DllImport("kernel32.dll")]
        // Intent: Returns the pseudo-handle for the current process so its access token can be queried without opening another process handle.
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        // Intent: Releases the native process-token handle acquired for current-user SID discovery.
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        // Intent: Releases LocalAlloc-backed buffers returned by Windows SID/security-descriptor conversion APIs.
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("advapi32.dll", SetLastError = true)]
        // Intent: Opens the current process token with query access so AutoModSync can determine the exact runtime account SID.
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        // Intent: Reads TOKEN_USER data from the current process token for exact runtime-account SID discovery.
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        // Intent: Converts a native SID into canonical S-1-... text for deterministic ACL allow-list comparison.
        private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        // Intent: Converts the protected exact-principal SDDL string into a native security descriptor for DACL application.
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string stringSecurityDescriptor, uint stringSdRevision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        // Intent: Extracts the DACL pointer from the generated native security descriptor before applying it to the private-key file.
        private static extern bool GetSecurityDescriptorDacl(IntPtr securityDescriptor, out bool daclPresent, out IntPtr dacl, out bool daclDefaulted);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        // Intent: Applies the protected exact-principal DACL to the private-key file without changing owner, group, or SACL.
        private static extern uint SetNamedSecurityInfoW(string objectName, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        // Intent: Reads the applied file security descriptor and DACL back from Windows for independent post-write verification.
        private static extern uint GetNamedSecurityInfoW(string objectName, int objectType, uint securityInfo, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        // Intent: Reads security-descriptor control flags so verification can require that DACL inheritance is disabled.
        private static extern bool GetSecurityDescriptorControl(IntPtr securityDescriptor, out ushort control, out uint revision);

        [DllImport("advapi32.dll", SetLastError = true)]
        // Intent: Enumerates individual DACL ACEs so verification can reject unexpected principals, flags, rule types, or rights.
        private static extern bool GetAce(IntPtr acl, int aceIndex, out IntPtr ace);
    }
}
