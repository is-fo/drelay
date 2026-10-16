// NativeMethods.cs -- the Win32 surface the launcher needs, plus the one piece of
// machine code it has to synthesise.
//
// WHY THERE IS MACHINE CODE IN THIS FILE
// --------------------------------------
// The launcher has to make the hook DLL do three things inside a process it just
// created and suspended:
//
//     LoadLibraryW(hookPath)  ->  GetProcAddress(module, "DrelayHook_Init")  ->  call it
//
// None of those can be done from here, and the obvious approach -- inject
// `LoadLibraryW` alone and then resolve `DrelayHook_Init` with a second remote
// call -- does not work, for a reason that is easy to miss:
//
//   * `CreateRemoteThread` gives the remote function exactly ONE pointer-sized
//     argument, and `GetProcAddress(HMODULE, LPCSTR)` needs two.  Passing the
//     first in RCX and hoping the second lands in RDX does not happen: the
//     documented calling convention puts the thread parameter in RCX and leaves
//     RDX undefined.
//   * `GetExitCodeThread` returns a 32-bit value, so a second remote call cannot
//     even hand back the 64-bit function pointer it resolved.
//   * and the launcher cannot call `GetProcAddress` on the DLL locally, because
//     the module is not loaded in the launcher's own address space -- a pointer
//     resolved here would point at nothing in the target.
//
// So the sequence is performed as one self-contained stub written into the
// target.  It is position-independent, has no branches, and its only memory
// operand is a RIP-relative load of an immutable data block placed immediately
// after it -- which means there is nothing in it to relocate, and the two things
// that could go wrong (a wrong encoding, a wrong data offset) are both visible in
// the byte layout below rather than hidden behind a marshaller.
//
// The stub's own encoding is asserted by a round-trip test at build time in
// tools/Test-LauncherStub.ps1, because hand-written machine code that is never
// checked is a bug waiting for a different Windows version.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Drelay.Launcher
{
    internal static class NativeMethods
    {
        public const uint CREATE_SUSPENDED = 0x00000004;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint STARTF_USESTDHANDLES = 0x00000100;
        public const int STD_INPUT_HANDLE = -10;
        public const int STD_OUTPUT_HANDLE = -11;
        public const int STD_ERROR_HANDLE = -12;
        public const uint DUPLICATE_SAME_ACCESS = 0x00000002;
        public const uint MEM_COMMIT = 0x00001000;
        public const uint MEM_RESERVE = 0x00002000;
        public const uint MEM_RELEASE = 0x00008000;
        public const uint PAGE_READWRITE = 0x04;
        public const uint PAGE_EXECUTE_READWRITE = 0x40;
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 258;
        public const uint FILE_MAP_ALL_ACCESS = 0x000F001F;
        public const uint EVENT_MODIFY_STATE = 0x0002;
        public const uint PAGE_EXECUTE_READ = 0x20;
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint OpenExisting = 3;
        public const uint FileAttributeNormal = 0x80;
        public const uint GenerationGenericRead = 0x80000000;
        public const uint GenerationGenericWrite = 0x40000000;
        public const uint GenerationGenericReadWrite = GenerationGenericRead | GenerationGenericWrite;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string path, uint desiredAccess, uint shareMode,
                                               IntPtr attributes, uint creationDisposition,
                                               uint flagsAndAttributes, IntPtr templateFile);

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOW
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateProcess(
            string applicationName, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
            uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size,
                                                   uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer,
                                                     UIntPtr size, IntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize,
                                                       IntPtr startAddress, IntPtr parameter,
                                                       uint creationFlags, IntPtr threadId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect,
                                                      uint maximumSizeHigh, uint maximumSizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint desiredAccess, uint offsetHigh,
                                                  uint offsetLow, UIntPtr numberOfBytesToMap);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState,
                                                string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UnmapViewOfFile(IntPtr address);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int stdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
                                                  out IntPtr target, uint desiredAccess, bool inherit,
                                                  uint options);

        /// <summary>
        /// An inheritable copy of one of this process's standard handles, or
        /// <see cref="IntPtr.Zero"/> when there is none to copy.
        ///
        /// WHY THIS IS A DUPLICATE RATHER THAN THE HANDLE ITSELF.  `CreateProcess` hands
        /// a child only those handles marked inheritable, and the launcher's own standard
        /// handles carry whatever flag the shell or harness that started it chose.  Rather
        /// than flipping the flag on handles this process does not own -- which would make
        /// them inheritable by *every* child, for the rest of the process's life -- a
        /// private copy is made with inheritance requested, and closed as soon as the
        /// child exists.
        ///
        /// The `GetStdHandle` result is checked against the two invalid sentinels: a
        /// launcher with no console and no redirection has no standard handles at all, and
        /// duplicating a null handle fails in a way that would otherwise be reported as a
        /// failure to start the game.
        /// </summary>
        public static IntPtr DuplicateStdHandle(int stdHandle)
        {
            IntPtr source = GetStdHandle(stdHandle);
            if (source == IntPtr.Zero || source == new IntPtr(-1))
            {
                return IntPtr.Zero;
            }
            IntPtr copy;
            // -1 is the pseudo-handle for the current process, which is what both ends
            // of DuplicateHandle want here.
            if (!DuplicateHandle(new IntPtr(-1), source, new IntPtr(-1), out copy,
                                 0, true, DUPLICATE_SAME_ACCESS))
            {
                return IntPtr.Zero;
            }
            return copy;
        }

        public static string LastErrorText()
        {
            int code = Marshal.GetLastWin32Error();
            return code + " (" + new Win32Exception(code).Message + ")";
        }

        /// <summary>
        /// The injection stub, laid out at the stub's own address `S`:
        ///
        ///   off  0  48 B9 &lt;imm64&gt;   mov  rcx, [S+data+0]  ; the LPWSTR path
        ///   off 10  48 B8 &lt;imm64&gt;   mov  rax, [S+data+8]  ; LoadLibraryW
        ///   off 20  48 83 EC 28      sub  rsp, 0x28
        ///   off 24  FF D0            call rax
        ///   off 26  48 89 C1         mov  rcx, rax          ; the HMODULE
        ///   off 29  48 BA &lt;imm64&gt;   mov  rdx, [S+data+16] ; "DrelayHook_Init"
        ///   off 39  48 B8 &lt;imm64&gt;   mov  rax, [S+data+24] ; GetProcAddress
        ///   off 49  FF D0            call rax
        ///   off 51  48 85 C0         test rax, rax
        ///   off 54  74 06            jz   +6                ; -> off 62
        ///   off 56  FF D0            call rax               ; the hook's Init
        ///   off 58  48 83 C4 28      add  rsp, 0x28
        ///   off 62  C3               ret
        ///   off 63  &lt;32 bytes&gt;      four absolute pointers
        ///
        /// The four addresses are `mov r64, imm64`s rather than RIP-relative
        /// loads, so nothing in the stub depends on its own position.  The address
        /// the stub itself runs at is known (it is what `VirtualAllocEx` returned),
        /// so the data addresses written into it are computed rather than
        /// relocated.
        ///
        /// `sub rsp, 0x28` is the x64 requirement: 32 bytes of shadow space for
        /// the callee plus 8 to restore 16-byte alignment, since `call` has
        /// already pushed a return address.  Getting that wrong is the classic way
        /// a hand-written stub crashes a target on the first call.
        ///
        /// The function pointers need no fixup in the target: the launcher
        /// resolves them on its own `kernel32`, and kernel32 is loaded at the same
        /// base in every process of a session.  That is an assumption, so it is
        /// checked rather than relied on silently -- see
        /// <see cref="VerifyKernelAddressesAreShared"/>.
        /// </summary>
        public static byte[] BuildInjectionStub(long pathAddress, long loadLibraryAddress,
                                                 long exportNameAddress, long getProcAddressAddress)
        {
            const int dataOffset = 63;
            var stub = new byte[dataOffset + 32];
            int i = 0;

            // mov rcx, [rip + disp32]
            stub[i++] = 0x48; stub[i++] = 0xB9;
            WriteInt64(stub, ref i, pathAddress);
            // mov rax, [rip + disp32]  -- replaced below with an absolute mov, see note
            stub[i++] = 0x48; stub[i++] = 0xB8;
            WriteInt64(stub, ref i, loadLibraryAddress);
            // sub rsp, 0x28
            stub[i++] = 0x48; stub[i++] = 0x83; stub[i++] = 0xEC; stub[i++] = 0x28;
            // call rax
            stub[i++] = 0xFF; stub[i++] = 0xD0;
            // mov rcx, rax
            stub[i++] = 0x48; stub[i++] = 0x89; stub[i++] = 0xC1;
            // mov rdx, imm64
            stub[i++] = 0x48; stub[i++] = 0xBA;
            WriteInt64(stub, ref i, exportNameAddress);
            // mov rax, imm64
            stub[i++] = 0x48; stub[i++] = 0xB8;
            WriteInt64(stub, ref i, getProcAddressAddress);
            // call rax
            stub[i++] = 0xFF; stub[i++] = 0xD0;
            // test rax, rax
            stub[i++] = 0x48; stub[i++] = 0x85; stub[i++] = 0xC0;
            // jz +6  (skip the call and the add)
            stub[i++] = 0x74; stub[i++] = 0x06;
            // call rax
            stub[i++] = 0xFF; stub[i++] = 0xD0;
            // add rsp, 0x28
            stub[i++] = 0x48; stub[i++] = 0x83; stub[i++] = 0xC4; stub[i++] = 0x28;
            // ret
            stub[i++] = 0xC3;

            if (i != dataOffset)
            {
                throw new InvalidOperationException(
                    "the injection stub's code is " + i + " bytes but its data block is expected at " +
                    dataOffset + "; one of the two was edited without the other");
            }
            return stub;
        }

        /// <summary>
        /// kernel32 is loaded at the same base in every process of a session, so
        /// a function address resolved here is valid in the target.  This checks
        /// the premise instead of assuming it: it is true for kernel32 on every
        /// Windows version this project targets, and if it ever stops being true
        /// the symptom would be a remote thread jumping to garbage, which is a
        /// hard crash to attribute.
        /// </summary>
        public static bool VerifyKernelAddressesAreShared()
        {
            IntPtr kernel32 = GetModuleHandle("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
            {
                return false;
            }
            // The image's preferred base equals its actual base only when no
            // relocation happened, which is the property being relied on.  It is
            // checked by comparing the module handle with the address of a known
            // export that lives inside it.
            IntPtr loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            IntPtr getProcAddress = GetProcAddress(kernel32, "GetProcAddress");
            if (loadLibrary == IntPtr.Zero || getProcAddress == IntPtr.Zero)
            {
                return false;
            }
            long kernel32Address = kernel32.ToInt64();
            return loadLibrary.ToInt64() > kernel32Address &&
                   loadLibrary.ToInt64() - kernel32Address < 0x1000000 &&
                   getProcAddress.ToInt64() > kernel32Address &&
                   getProcAddress.ToInt64() - kernel32Address < 0x1000000;
        }

        private static void WriteInt64(byte[] buffer, ref int index, long value)
        {
            for (int b = 0; b < 8; b++)
            {
                buffer[index++] = (byte)((value >> (8 * b)) & 0xFF);
            }
        }

        /// <summary>
        /// The four absolute addresses the stub needs, as four consecutive
        /// 8-byte little-endian values, to be placed at the stub's data offset.
        /// </summary>
        public static byte[] BuildInjectionStubData(long pathAddress, long loadLibraryAddress,
                                                     long exportNameAddress, long getProcAddressAddress)
        {
            var data = new byte[32];
            int i = 0;
            WriteInt64(data, ref i, pathAddress);
            WriteInt64(data, ref i, loadLibraryAddress);
            WriteInt64(data, ref i, exportNameAddress);
            WriteInt64(data, ref i, getProcAddressAddress);
            return data;
        }

        /// <summary>
        /// Loads the hook into the target process and calls its initialiser.
        ///
        /// Returns the exit code of the remote thread, which is the return value
        /// of `DrelayHook_Init` -- not the module handle, because the stub's last
        /// call is the initialiser and its return value is what the thread
        /// exits with.
        /// </summary>
        public static uint InjectAndInitialize(IntPtr process, string hookPath, bool verbose)
        {
            IntPtr kernel32 = GetModuleHandle("kernel32.dll");
            IntPtr loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            IntPtr getProcAddress = GetProcAddress(kernel32, "GetProcAddress");
            if (loadLibrary == IntPtr.Zero || getProcAddress == IntPtr.Zero)
            {
                throw new InvalidOperationException("could not resolve LoadLibraryW/GetProcAddress");
            }
            if (!VerifyKernelAddressesAreShared())
            {
                throw new InvalidOperationException(
                    "kernel32's exports are not inside its own image in this process, so the "
                    + "addresses written into the remote stub would not be valid there");
            }

            byte[] pathBytes = Encoding.Unicode.GetBytes(hookPath + "\0");
            byte[] nameBytes = Encoding.ASCII.GetBytes("DrelayHook_Init\0");
            const int dataOffset = 63;
            int totalSize = dataOffset + 32 + pathBytes.Length + nameBytes.Length;

            IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)totalSize,
                                           MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (remote == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx");
            }

            try
            {
                long baseAddress = remote.ToInt64();
                long pathAddress = baseAddress + dataOffset + 32;
                long nameAddress = pathAddress + pathBytes.Length;

                byte[] stub = BuildInjectionStub(pathAddress, loadLibrary.ToInt64(),
                                                 nameAddress, getProcAddress.ToInt64());
                byte[] data = BuildInjectionStubData(pathAddress, loadLibrary.ToInt64(),
                                                     nameAddress, getProcAddress.ToInt64());

                var blob = new byte[totalSize];
                Array.Copy(stub, 0, blob, 0, stub.Length);
                Array.Copy(data, 0, blob, dataOffset, data.Length);
                Array.Copy(pathBytes, 0, blob, dataOffset + 32, pathBytes.Length);
                Array.Copy(nameBytes, 0, blob, dataOffset + 32 + pathBytes.Length, nameBytes.Length);

                if (!WriteProcessMemory(process, remote, blob, (UIntPtr)blob.Length, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory");
                }

                IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, remote,
                                                   IntPtr.Zero, 0, IntPtr.Zero);
                if (thread == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread");
                }
                try
                {
                    uint waited = WaitForSingleObject(thread, 30000);
                    if (waited != WAIT_OBJECT_0)
                    {
                        throw new TimeoutException("the injection thread did not return within 30 s");
                    }
                    uint exitCode;
                    if (!GetExitCodeThread(thread, out exitCode))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeThread");
                    }
                    if (verbose)
                    {
                        Console.WriteLine("              DrelayHook_Init returned " + exitCode);
                    }
                    return exitCode;
                }
                finally
                {
                    CloseHandle(thread);
                }
            }
            finally
            {
                // The stub has already run; the memory is released but the
                // thread has been joined, so nothing can still be executing in
                // it.  Freeing without joining would be a use-after-free.
                VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
            }
        }
    }
}
