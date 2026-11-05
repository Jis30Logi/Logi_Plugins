namespace Loupedeck.ETS2Plugin.Helpers
{
    using System;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;

    internal static class KeySender
    {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

        private const uint INPUT_KEYBOARD     = 1;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint KEYEVENTF_KEYUP    = 0x0002;
        private const uint WM_KEYDOWN         = 0x0100;
        private const uint WM_KEYUP           = 0x0101;

        private static IntPtr FindETS2Window()
        {
            var result = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                if (sb.ToString().IndexOf("Euro Truck Simulator", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// Sends a key press using all available methods for maximum game compatibility.
        /// </summary>
        public static void PressKey(ushort vkCode, ushort scanCode)
        {
            // Method 1: PostMessage directly to ETS2 window (works regardless of focus)
            var hWnd = FindETS2Window();
            if (hWnd != IntPtr.Zero)
            {
                var lDown = (IntPtr)((scanCode << 16) | 0x0001);
                var lUp   = (IntPtr)(0xC0000000u | (uint)(scanCode << 16) | 0x0001u);
                PostMessage(hWnd, WM_KEYDOWN, (IntPtr)vkCode, lDown);
                Thread.Sleep(30);
                PostMessage(hWnd, WM_KEYUP, (IntPtr)vkCode, lUp);
                return;
            }

            // Method 2: keybd_event fallback (older API, broader compatibility)
            keybd_event((byte)vkCode, (byte)scanCode, 0, UIntPtr.Zero);
            Thread.Sleep(30);
            keybd_event((byte)vkCode, (byte)scanCode, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void PressScanCode(ushort scanCode)
        {
            var inputs = new INPUT[]
            {
                new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = scanCode, dwFlags = KEYEVENTF_SCANCODE } } },
                new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = scanCode, dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP } } }
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
