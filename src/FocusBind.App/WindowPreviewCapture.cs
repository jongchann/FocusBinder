using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using FocusBind.Win32;

namespace FocusBind.App;

internal static class WindowPreviewCapture
{
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(
        nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    internal static BitmapSource Capture(WindowSnapshot target)
    {
        var bounds = target.ClientBounds;
        if (!target.CanReceiveInput || bounds.IsEmpty) throw new InvalidOperationException("대상 창을 활성화한 뒤 다시 캡처하세요.");
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
        nint previous = 0;
        try
        {
            if (screen == 0 || memory == 0 || bitmap == 0) throw new Win32Exception();
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x00CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
}
