namespace Loupedeck.RTSSPerformancePlugin.Interop
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Win32 file-mapping imports used to read the RTSS shared memory block.
    /// </summary>
    internal static class NativeMethods
    {
        /// <summary>Name of the RTSS v2 shared memory object.</summary>
        public const String RTSSSharedMemoryName = "RTSSSharedMemoryV2";

        /// <summary>Desired access: read-only view.</summary>
        public const UInt32 FILE_MAP_READ = 0x0004;

        /// <summary>Desired access: read/write view.</summary>
        public const UInt32 FILE_MAP_WRITE = 0x0002;

        public const UInt32 FILE_MAP_ALL_ACCESS = 0x000F001F;

        public const Int32 ERROR_FILE_NOT_FOUND = 2;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenFileMappingW")]
        public static extern IntPtr OpenFileMapping(
            UInt32 dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] Boolean bInheritHandle,
            String lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(
            IntPtr hFileMappingObject,
            UInt32 dwDesiredAccess,
            UInt32 dwFileOffsetHigh,
            UInt32 dwFileOffsetLow,
            UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern Boolean UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern Boolean CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public UInt32 AllocationProtect;
            public UIntPtr RegionSize;
            public UInt32 State;
            public UInt32 Protect;
            public UInt32 Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern UIntPtr VirtualQuery(
            IntPtr lpAddress,
            out MemoryBasicInformation lpBuffer,
            UIntPtr dwLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryW")]
        public static extern IntPtr LoadLibrary(String lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, String lpProcName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern UInt32 GetWindowThreadProcessId(IntPtr hWnd, out UInt32 lpdwProcessId);

        /// <summary>
        /// Process id owning the foreground window, or 0 when there is none.
        ///
        /// This is what makes active-application selection deterministic rather than heuristic:
        /// the window the user is looking at is, by definition, the application they mean. Frame
        /// counters alone cannot distinguish a game from a background app that happens to redraw.
        /// </summary>
        public static UInt32 TryGetForegroundProcessId()
        {
            try
            {
                var window = GetForegroundWindow();

                if (window == IntPtr.Zero)
                {
                    return 0;
                }

                return GetWindowThreadProcessId(window, out var processId) == 0 ? 0 : processId;
            }
            catch (Exception)
            {
                // A foreground query must never take down a poll tick.
                return 0;
            }
        }
    }
}
