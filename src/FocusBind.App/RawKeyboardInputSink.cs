using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FocusBind.Core.Input;
using FocusBind.Core.Profiles;
using FocusBind.Win32.Input;

namespace FocusBind.App;

/// <summary>Receives physical keyboard input while another window is foreground.</summary>
public sealed class RawKeyboardInputSink : IDisposable
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nuint WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command, [Out] byte[]? data,
        ref uint size, uint headerSize);

    public event Action<InputEvent>? KeyboardInput;
    public event Action<long>? Tick;
    public Exception? LastError { get; private set; }

    public RawKeyboardInputSink(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("WPF window handle is unavailable.");
        var device = new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevInputSink, Target = handle };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw keyboard registration failed.");
        _source.AddHook(WndProc);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _timer.Tick += (_, _) => Tick?.Invoke(Environment.TickCount64);
        _timer.Start();
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmInput || _disposed) return 0;
        try
        {
            var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
            uint size = 0;
            if (GetRawInputData(lParam, RidInput, null, ref size, headerSize) == uint.MaxValue ||
                size < headerSize + 16 || size > 65536) return 0;
            var data = new byte[size];
            if (GetRawInputData(lParam, RidInput, data, ref size, headerSize) == uint.MaxValue) return 0;
            if (RawKeyboardDecoder.TryDecode(data, checked((int)headerSize), Environment.TickCount64, out var input))
                KeyboardInput?.Invoke(input);
        }
        catch (Exception ex) { LastError = ex; }
        // WPF still forwards WM_INPUT to DefWindowProc for foreground cleanup.
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _source.RemoveHook(WndProc);
        var device = new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevRemove };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            LastError = new Win32Exception(Marshal.GetLastWin32Error(), "Raw keyboard removal failed.");
    }
}
