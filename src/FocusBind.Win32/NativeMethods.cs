using System.Runtime.InteropServices;
using System.Text;

namespace FocusBind.Win32;

internal static class NativeMethods
{
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int virtualKey);
    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventObjectLocationChange = 0x800B;
    internal const uint WineventOutOfContext = 0;
    internal const int ObjidWindow = 0;
    internal const uint WmQuit = 0x0012;
    internal const uint WmTimer = 0x0113;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint WmGetText = 0x000D;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmMButtonDown = 0x0207;
    internal const uint WmMButtonUp = 0x0208;
    internal const uint WmMouseWheel = 0x020A;
    internal const uint WmXButtonDown = 0x020B;
    internal const uint WmXButtonUp = 0x020C;
    internal const uint WmMouseHWheel = 0x020E;
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    internal delegate void WinEventCallback(nint hook, uint eventType, nint hwnd, int objectId,
        int childId, uint eventThread, uint eventTime);
    internal delegate bool EnumWindowsCallback(nint hwnd, nint parameter);
    internal delegate nint LowLevelHookCallback(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint HWnd;
        public uint Msg;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint LPrivate;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHookData
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SendInputData
    {
        public uint Type;
        public SendInputUnion Data;
    }
    [StructLayout(LayoutKind.Explicit)]
    internal struct SendInputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam,
        StringBuilder lParam, uint flags, uint timeoutMilliseconds, out nuint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder name, int length);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWinEventHook(uint eventMin, uint eventMax,
        nint module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", SetLastError = true)] internal static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PeekMessage(out Message message, nint hwnd, uint min, uint max, uint removeMessage);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nuint SetTimer(nint hwnd, nuint timerId, uint interval, nint callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint hwnd, nuint timerId);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(
        int hookId, LowLevelHookCallback callback, nint module, uint threadId);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(
        uint count, [In] SendInputData[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(
        nint process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
}
