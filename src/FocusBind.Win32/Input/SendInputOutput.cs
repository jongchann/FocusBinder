using System.ComponentModel;
using System.Runtime.InteropServices;
using FocusBind.Core.Coordinates;
using FocusBind.Core.Input;

namespace FocusBind.Win32.Input;

/// <summary>Foreground-only output primitive. The caller must verify the target before every action.</summary>
public sealed class SendInputOutput : IInputOutput, IDisposable
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyUpFlag = 0x0002;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHWheel = 0x1000;
    private const uint MouseMove = 0x0001;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;

    private readonly object _gate = new();
    private readonly HashSet<ushort> _pressedKeys = [];
    private readonly HashSet<int> _pressedButtons = [];
    private bool _disposed;

    public void KeyDown(int virtualKey) => SendKey(virtualKey, false);
    public void KeyUp(int virtualKey) => SendKey(virtualKey, true);
    public void KeyTap(int virtualKey)
    {
        KeyDown(virtualKey);
        KeyUp(virtualKey);
    }

    public void MouseDown(int button) => SendMouseButton(button, false);
    public void MouseUp(int button) => SendMouseButton(button, true);
    public void MouseClick(int button)
    {
        MouseDown(button);
        MouseUp(button);
    }

    public void Wheel(int amount, bool horizontal = false)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (amount == 0) return;
            Send(new NativeMethods.SendInputData
            {
                Type = InputMouse,
                Data = new NativeMethods.SendInputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        MouseData = unchecked((uint)amount),
                        Flags = horizontal ? MouseHWheel : MouseWheel,
                        ExtraInfo = InputInjection.Marker
                    }
                }
            });
        }
    }

    public PixelRect GetVirtualDesktop() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));

    public PixelPoint? GetCursorPosition() => NativeMethods.GetCursorPos(out var point)
        ? new PixelPoint(point.X, point.Y) : null;

    public void MoveTo(PixelPoint point)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!VirtualDesktopTransform.TryToAbsolute(point, GetVirtualDesktop(), out var absolute))
                throw new ArgumentOutOfRangeException(nameof(point), "Point is outside the virtual desktop.");
            Send(new NativeMethods.SendInputData
            {
                Type = InputMouse,
                Data = new NativeMethods.SendInputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        X = absolute.X,
                        Y = absolute.Y,
                        Flags = MouseMove | MouseAbsolute | MouseVirtualDesk,
                        ExtraInfo = InputInjection.Marker
                    }
                }
            });
        }
    }

    private void SendKey(int virtualKey, bool up)
    {
        if (virtualKey is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(virtualKey));
        lock (_gate)
        {
            ThrowIfDisposed();
            SendKeyCore((ushort)virtualKey, up);
        }
    }

    private void SendKeyCore(ushort virtualKey, bool up)
    {
        Send(new NativeMethods.SendInputData
        {
            Type = InputKeyboard,
            Data = new NativeMethods.SendInputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = up ? KeyUpFlag : 0,
                    ExtraInfo = InputInjection.Marker
                }
            }
        });
        if (up) _pressedKeys.Remove(virtualKey);
        else _pressedKeys.Add(virtualKey);
    }

    private void SendMouseButton(int button, bool up)
    {
        if (button is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(button));
        lock (_gate)
        {
            ThrowIfDisposed();
            SendMouseButtonCore(button, up);
        }
    }

    private void SendMouseButtonCore(int button, bool up)
    {
        var (flags, data) = button switch
        {
            1 => (up ? MouseLeftUp : MouseLeftDown, 0U),
            2 => (up ? MouseRightUp : MouseRightDown, 0U),
            3 => (up ? MouseMiddleUp : MouseMiddleDown, 0U),
            4 => (up ? MouseXUp : MouseXDown, 1U),
            5 => (up ? MouseXUp : MouseXDown, 2U),
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };
        Send(new NativeMethods.SendInputData
        {
            Type = InputMouse,
            Data = new NativeMethods.SendInputUnion
            {
                Mouse = new NativeMethods.MouseInput
                {
                    Flags = flags,
                    MouseData = data,
                    ExtraInfo = InputInjection.Marker
                }
            }
        });
        if (up) _pressedButtons.Remove(button);
        else _pressedButtons.Add(button);
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            var failures = new List<Exception>();
            foreach (var key in _pressedKeys.ToArray())
            {
                try { SendKeyCore(key, true); }
                catch (Exception ex) { failures.Add(ex); }
            }
            foreach (var button in _pressedButtons.ToArray())
            {
                try { SendMouseButtonCore(button, true); }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 0) throw new AggregateException("Could not release all injected inputs.", failures);
        }
    }

    private static void Send(NativeMethods.SendInputData input)
    {
        if (NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.SendInputData>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SendInputOutput));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ReleaseAll();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
