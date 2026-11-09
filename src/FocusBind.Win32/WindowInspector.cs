using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using FocusBind.Core.Profiles;

namespace FocusBind.Win32;

public sealed record WindowBounds(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed record WindowSnapshot(
    nint Handle, uint ProcessId, uint ThreadId, WindowIdentity Identity,
    WindowBounds ClientBounds, uint Dpi, bool IsVisible, bool IsMinimized,
    bool IsForeground)
{
    public bool CanReceiveInput => IsVisible && !IsMinimized && !ClientBounds.IsEmpty && IsForeground;
    public string ClientSize => $"{ClientBounds.Width}×{ClientBounds.Height}";
    public string DisplayState => IsMinimized ? "최소화" : "표시 중";
    public bool IsAuxiliary => ClientBounds.Width < 200 || ClientBounds.Height < 120 ||
        Identity.WindowClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Identity.Title)
        ? Identity.Title
        : $"(제목 없음) {DisplayProcessName} [{Identity.WindowClass}]";
    public string DisplayProcessName => string.IsNullOrWhiteSpace(Identity.ExecutablePath)
        ? Identity.ProcessName ?? $"PID {ProcessId}"
        : System.IO.Path.GetFileName(Identity.ExecutablePath);
}

public static class WindowInspector
{
    public static WindowSnapshot? GetForeground() => TryGet(NativeMethods.GetForegroundWindow());

    public static WindowSnapshot? TryGet(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd)) return null;
        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (threadId == 0 || processId == 0) return null;
        if (!NativeMethods.GetClientRect(hwnd, out var rect)) return null;
        var origin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return null;
        var title = new StringBuilder(Math.Min(NativeMethods.GetWindowTextLength(hwnd) + 1, 32768));
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        if (title.Length == 0)
        {
            // Some captionless windows still implement WM_GETTEXT. Bound the wait so a hung app cannot freeze refresh.
            var alternate = new StringBuilder(1024);
            if (NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WmGetText, (nuint)alternate.Capacity,
                alternate, NativeMethods.SmtoAbortIfHung, 30, out var length) != 0 && length > 0)
                title = alternate;
        }
        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        var path = GetProcessPath(processId) ?? string.Empty;
        var processName = string.IsNullOrWhiteSpace(path) ? GetProcessName(processId) : System.IO.Path.GetFileName(path);
        var identity = new WindowIdentity(path, title.ToString(), className.ToString(), processName);
        return new WindowSnapshot(hwnd, processId, threadId, identity,
            new WindowBounds(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top),
            NativeMethods.GetDpiForWindow(hwnd), NativeMethods.IsWindowVisible(hwnd), NativeMethods.IsIconic(hwnd),
            NativeMethods.GetForegroundWindow() == hwnd);
    }

    public static IReadOnlyList<WindowSnapshot> ListTopLevelWindows()
    {
        var windows = new List<WindowSnapshot>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var snapshot = TryGet(hwnd);
            // Fullscreen games can minimize as soon as FocusBind receives focus.
            // Keep their windows available for profile selection while minimized.
            if (snapshot is { IsVisible: true } && (!snapshot.ClientBounds.IsEmpty || snapshot.IsMinimized))
                windows.Add(snapshot);
            return true;
        }, 0);
        return windows;
    }

    private static string? GetProcessPath(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return null;
        try
        {
            var name = new StringBuilder(32768);
            uint size = (uint)name.Capacity;
            return NativeMethods.QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(process); }
    }

    private static string? GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName + ".exe";
        }
        catch { return null; }
    }
}
