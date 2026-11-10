using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FocusBind.Win32;

public enum WindowChangeKind { Foreground, Location }
public sealed record WindowChange(WindowChangeKind Kind, WindowSnapshot? Window);

/// <summary>WinEvent callbacks run on a dedicated message-loop thread. Subscribers must marshal UI work.</summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly NativeMethods.WinEventCallback _callback;
    private uint _threadId;
    private Exception? _startupError;
    private WindowSnapshot? _lastSnapshot;
    private int _disposed;

    public event Action<WindowChange>? Changed;
    public Exception? LastNotificationError { get; private set; }

    public ForegroundWatcher()
    {
        _callback = OnWinEvent;
        _thread = new Thread(Run) { IsBackground = true, Name = "FocusBind WinEvent watcher" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("WinEvent watcher did not start.");
        if (_startupError is not null) throw new InvalidOperationException("WinEvent watcher failed to start.", _startupError);
    }

    private void Run()
    {
        nint foregroundHook = 0, locationHook = 0;
        nuint timer = 0;
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(out _, 0, 0, 0, 0); // Create this thread's message queue.
            foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EventSystemForeground,
                NativeMethods.EventSystemForeground, 0, _callback, 0, 0, NativeMethods.WineventOutOfContext);
            locationHook = NativeMethods.SetWinEventHook(NativeMethods.EventObjectLocationChange,
                NativeMethods.EventObjectLocationChange, 0, _callback, 0, 0, NativeMethods.WineventOutOfContext);
            if (foregroundHook == 0 || locationHook == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWinEventHook failed.");
            timer = NativeMethods.SetTimer(0, 0, 250, 0);
            if (timer == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTimer failed.");
            _lastSnapshot = WindowInspector.GetForeground();
            _ready.Set();
            while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Msg == NativeMethods.WmTimer && message.WParam == timer)
                {
                    CheckCurrentWindow();
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
            if (foregroundHook != 0) NativeMethods.UnhookWinEvent(foregroundHook);
            if (locationHook != 0) NativeMethods.UnhookWinEvent(locationHook);
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        if (Volatile.Read(ref _disposed) != 0 || hwnd == 0 || objectId != NativeMethods.ObjidWindow || childId != 0) return;
        var foreground = NativeMethods.GetForegroundWindow();
        if (eventType == NativeMethods.EventObjectLocationChange && hwnd != foreground) return;
        var kind = eventType == NativeMethods.EventSystemForeground ? WindowChangeKind.Foreground : WindowChangeKind.Location;
        _lastSnapshot = WindowInspector.TryGet(foreground);
        Notify(new WindowChange(kind, _lastSnapshot));
    }

    private void CheckCurrentWindow()
    {
        var current = WindowInspector.GetForeground();
        var kind = current?.Handle != _lastSnapshot?.Handle ? WindowChangeKind.Foreground : WindowChangeKind.Location;
        if (kind == WindowChangeKind.Foreground || current?.ClientBounds != _lastSnapshot?.ClientBounds
            || current?.Dpi != _lastSnapshot?.Dpi || current?.IsMinimized != _lastSnapshot?.IsMinimized
            || current?.IsVisible != _lastSnapshot?.IsVisible)
        {
            _lastSnapshot = current;
            Notify(new WindowChange(kind, current));
        }
    }

    private void Notify(WindowChange change)
    {
        try { Changed?.Invoke(change); }
        catch (Exception ex) { LastNotificationError = ex; }
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
