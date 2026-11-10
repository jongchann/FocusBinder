using System.ComponentModel;
using System.Runtime.InteropServices;
using FocusBind.Core.Input;
using FocusBind.Core.Profiles;

namespace FocusBind.Win32.Input;

public static class InputInjection
{
    // Sent as dwExtraInfo so our low-level hooks can skip FocusBind's own SendInput events.
    public static readonly nuint Marker = Environment.Is64BitProcess ? unchecked((nuint)0x46424E445F494E50UL) : (nuint)0x46424E44U;
}

/// <summary>Low-level hooks and their callbacks live on one dedicated message-loop thread.</summary>
public sealed class LowLevelInputHook : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly NativeMethods.LowLevelHookCallback _keyboardCallback;
    private readonly NativeMethods.LowLevelHookCallback _mouseCallback;
    private uint _threadId;
    private Exception? _startupError;
    private int _disposed;
    private long _timerTicks;
    private long _callbackCount;
    private long _keyboardCallbacks;
    private long _mouseCallbacks;

    /// <summary>Called on the hook thread. Return true only when an active mapping consumes this event.</summary>
    public Func<InputEvent, bool>? Handler { get; set; }
    /// <summary>Called every 20 ms on the hook thread for long-press evaluation.</summary>
    public Action<long>? Tick { get; set; }
    public Exception? LastCallbackError { get; private set; }
    public Exception? ThreadError => _startupError;
    public bool IsThreadAlive => _thread.IsAlive;
    public long TimerTicks => Interlocked.Read(ref _timerTicks);
    public long CallbackCount => Interlocked.Read(ref _callbackCount);
    public long KeyboardCallbacks => Interlocked.Read(ref _keyboardCallbacks);
    public long MouseCallbacks => Interlocked.Read(ref _mouseCallbacks);

    /// <summary>Checks callback delivery with a zero-distance synthetic mouse move.</summary>
    public async Task<bool> ProbeAsync()
    {
        var before = MouseCallbacks;
        var input = new NativeMethods.SendInputData
        {
            Type = 0,
            Data = new NativeMethods.SendInputUnion
            {
                Mouse = new NativeMethods.MouseInput { Flags = 0x0001, ExtraInfo = InputInjection.Marker }
            }
        };
        if (NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.SendInputData>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Input hook probe could not send a no-op mouse move.");
        await Task.Delay(150);
        return MouseCallbacks > before;
    }

    /// <summary>Tests keyboard hook delivery with an unused F24 key tap marked as self-injected.</summary>
    public async Task<bool> ProbeKeyboardAsync()
    {
        var before = KeyboardCallbacks;
        var down = new NativeMethods.SendInputData
        {
            Type = 1,
            Data = new NativeMethods.SendInputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput { VirtualKey = 0x87, ExtraInfo = InputInjection.Marker }
            }
        };
        var up = down;
        up.Data.Keyboard.Flags = 0x0002;
        if (NativeMethods.SendInput(2, [down, up], Marshal.SizeOf<NativeMethods.SendInputData>()) != 2)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Keyboard hook probe could not send F24.");
        await Task.Delay(150);
        return KeyboardCallbacks > before;
    }

    public LowLevelInputHook()
    {
        _keyboardCallback = OnKeyboard;
        _mouseCallback = OnMouse;
        _thread = new Thread(Run) { IsBackground = true, Name = "FocusBind input hooks" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Input hook thread did not start.");
        if (_startupError is not null)
            throw new InvalidOperationException("Input hooks could not be installed.", _startupError);
    }

    private void Run()
    {
        nint keyboard = 0, mouse = 0;
        nuint timer = 0;
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(out _, 0, 0, 0, 0);
            var module = NativeMethods.GetModuleHandle(null);
            keyboard = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardCallback, module, 0);
            mouse = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseCallback, module, 0);
            if (keyboard == 0 || mouse == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed.");
            timer = NativeMethods.SetTimer(0, 0, 20, 0);
            if (timer == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTimer failed.");
            _ready.Set();
            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Input hook message loop failed.");
                if (message.Msg == NativeMethods.WmTimer && message.WParam == timer)
                {
                    Interlocked.Increment(ref _timerTicks);
                    try { Tick?.Invoke(Environment.TickCount64); }
                    catch (Exception ex) { LastCallbackError = ex; }
                    continue;
                }
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
        finally
        {
            if (timer != 0) NativeMethods.KillTimer(0, timer);
            if (keyboard != 0) NativeMethods.UnhookWindowsHookEx(keyboard);
            if (mouse != 0) NativeMethods.UnhookWindowsHookEx(mouse);
        }
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        Interlocked.Increment(ref _callbackCount);
        Interlocked.Increment(ref _keyboardCallbacks);
        if (code < 0 || Volatile.Read(ref _disposed) != 0) return NativeMethods.CallNextHookEx(0, code, message, data);
        var kind = (uint)message;
        if (kind is not (NativeMethods.WmKeyDown or NativeMethods.WmKeyUp or NativeMethods.WmSysKeyDown or NativeMethods.WmSysKeyUp))
            return NativeMethods.CallNextHookEx(0, code, message, data);
        var raw = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
        if (raw.ExtraInfo == InputInjection.Marker) return NativeMethods.CallNextHookEx(0, code, message, data);
        var input = new InputEvent(TriggerKind.Key, (int)raw.VirtualKey,
            kind is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown, WheelDirection.None, Environment.TickCount64);
        return Handle(input) ? 1 : NativeMethods.CallNextHookEx(0, code, message, data);
    }

    private nint OnMouse(int code, nuint message, nint data)
    {
        Interlocked.Increment(ref _callbackCount);
        Interlocked.Increment(ref _mouseCallbacks);
        if (code < 0 || Volatile.Read(ref _disposed) != 0) return NativeMethods.CallNextHookEx(0, code, message, data);
        var kind = (uint)message;
        var button = kind switch
        {
            NativeMethods.WmLButtonDown or NativeMethods.WmLButtonUp => 1,
            NativeMethods.WmRButtonDown or NativeMethods.WmRButtonUp => 2,
            NativeMethods.WmMButtonDown or NativeMethods.WmMButtonUp => 3,
            NativeMethods.WmXButtonDown or NativeMethods.WmXButtonUp or NativeMethods.WmMouseWheel or NativeMethods.WmMouseHWheel => 0,
            _ => -1
        };
        if (button < 0) return NativeMethods.CallNextHookEx(0, code, message, data);
        var raw = Marshal.PtrToStructure<NativeMethods.MouseHookData>(data);
        if (raw.ExtraInfo == InputInjection.Marker) return NativeMethods.CallNextHookEx(0, code, message, data);
        if (kind is NativeMethods.WmXButtonDown or NativeMethods.WmXButtonUp)
            button = ((raw.MouseData >> 16) & 0xFFFF) == 1 ? 4 : 5;
        var isWheel = kind is NativeMethods.WmMouseWheel or NativeMethods.WmMouseHWheel;
        var direction = isWheel ? (short)(raw.MouseData >> 16) > 0 ? WheelDirection.Positive : WheelDirection.Negative : WheelDirection.None;
        var input = new InputEvent(
            kind == NativeMethods.WmMouseWheel ? TriggerKind.VerticalWheel :
            kind == NativeMethods.WmMouseHWheel ? TriggerKind.HorizontalWheel : TriggerKind.MouseButton,
            button,
            isWheel || kind is NativeMethods.WmLButtonDown or NativeMethods.WmRButtonDown or NativeMethods.WmMButtonDown or NativeMethods.WmXButtonDown,
            direction, Environment.TickCount64);
        return Handle(input) ? 1 : NativeMethods.CallNextHookEx(0, code, message, data);
    }

    private bool Handle(InputEvent input)
    {
        try { return Handler?.Invoke(input) == true; }
        catch (Exception ex) { LastCallbackError = ex; return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, NativeMethods.WmQuit, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
        GC.SuppressFinalize(this);
    }
}
