using System;
using System.Runtime.InteropServices;

namespace AudioLink
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public int type; public InputUnion u; }

    static class Native
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const uint WM_QUIT = 0x12;
        public const int WM_CLIPBOARDUPDATE = 0x031D;
        public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
        public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    }

    // Plays input on this computer as if it came from a real keyboard/mouse.
    static class Inject
    {
        const uint MOVE = 0x1, LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10, MIDDLEDOWN = 0x20, MIDDLEUP = 0x40,
                   XDOWN = 0x80, XUP = 0x100, WHEEL = 0x800, HWHEEL = 0x1000, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
        const uint KEY_EXTENDED = 0x1, KEY_UP = 0x2, KEY_SCANCODE = 0x8;

        static void Mouse(int dx, int dy, int data, uint flags)
        {
            var i = new INPUT { type = 0 };
            i.u.mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags };
            Native.SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
        }

        // x, y are pixels from the top-left of the whole desktop (all monitors).
        public static void MoveTo(int x, int y)
        {
            int vw = Math.Max(2, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
            int vh = Math.Max(2, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
            int nx = (int)(x * 65535L / (vw - 1)), ny = (int)(y * 65535L / (vh - 1));
            Mouse(nx, ny, 0, MOVE | ABSOLUTE | VIRTUALDESK);
        }

        public static void Button(int button, bool down)
        {
            switch (button)
            {
                case 0: Mouse(0, 0, 0, down ? LEFTDOWN : LEFTUP); break;
                case 1: Mouse(0, 0, 0, down ? RIGHTDOWN : RIGHTUP); break;
                case 2: Mouse(0, 0, 0, down ? MIDDLEDOWN : MIDDLEUP); break;
                case 3: Mouse(0, 0, 1, down ? XDOWN : XUP); break;
                case 4: Mouse(0, 0, 2, down ? XDOWN : XUP); break;
            }
        }

        public static void Wheel(bool horizontal, int delta)
        {
            Mouse(0, 0, delta, horizontal ? HWHEEL : WHEEL);
        }

        public static void Key(int vk, int scan, bool extended, bool up)
        {
            var ki = new KEYBDINPUT();
            uint flags = (extended ? KEY_EXTENDED : 0) | (up ? KEY_UP : 0);
            // Scan codes mean "this physical key", so the laptop's own keyboard layout applies.
            // Pause and a few odd keys only work by virtual-key code.
            if (scan != 0 && vk != 0x13 && vk != 0x90) { ki.wScan = (ushort)scan; flags |= KEY_SCANCODE; }
            else { ki.wVk = (ushort)vk; ki.wScan = (ushort)scan; }
            ki.dwFlags = flags;
            var i = new INPUT { type = 1 };
            i.u.ki = ki;
            Native.SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
