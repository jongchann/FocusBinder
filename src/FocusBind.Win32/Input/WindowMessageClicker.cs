using System.ComponentModel;
using System.Runtime.InteropServices;
using FocusBind.Core.Coordinates;

namespace FocusBind.Win32.Input;

/// <summary>Posts client-coordinate mouse messages without moving the system cursor.</summary>
public sealed class WindowMessageClicker(nint handle)
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftDown = 0x0201;
    private const uint WmLeftUp = 0x0202;
    private readonly object _gate = new();
    private PixelPoint? _pressedClient;

    public static bool IsKnownIncompatible(string? windowClass) =>
        string.Equals(windowClass, "UnityWndClass", StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    public void MoveTo(PixelPoint screenPoint)
    {
        lock (_gate)
            Post(WmMouseMove, 0, Pack(ToClient(screenPoint)));
    }

    public void SetButton(PixelPoint screenPoint, bool down)
    {
        lock (_gate)
        {
            if (!down) { Release(); return; }
            var client = ToClient(screenPoint);
            var position = Pack(client);
            Post(WmLeftDown, 1, position);
            _pressedClient = client;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_pressedClient is not { } client) return;
            Post(WmLeftUp, 0, Pack(client));
            _pressedClient = null;
        }
    }

    private void Post(uint message, nuint buttons, nint position)
    {
        if (!PostMessage(handle, message, buttons, position))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Posting a coordinate mouse message failed.");
    }

    private PixelPoint ToClient(PixelPoint screenPoint)
    {
        var snapshot = WindowInspector.TryGet(handle);
        if (snapshot is not { CanReceiveInput: true })
            throw new OperationCanceledException("Target window is not foreground for message click.");
        if (IsKnownIncompatible(snapshot.Identity.WindowClass))
            throw new NotSupportedException("Unity window uses the physical cursor position for mouse clicks. Select cursor movement mode for this target.");
        var bounds = snapshot.ClientBounds;
        var client = new PixelPoint(screenPoint.X - bounds.X, screenPoint.Y - bounds.Y);
        if (client.X < 0 || client.Y < 0 || client.X >= bounds.Width || client.Y >= bounds.Height ||
            client.X > short.MaxValue || client.Y > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(screenPoint), "Message click is outside client bounds.");
        return client;
    }

    private static nint Pack(PixelPoint client) => (nint)(client.X | (client.Y << 16));
}
