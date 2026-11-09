using System.Runtime.InteropServices;

namespace FocusBind.Win32;

/// <summary>Changes only audio sessions owned by the selected window's process, across active render devices.</summary>
public static class ProcessAudio
{
    public static int Adjust(uint processId, float delta, bool? mute, bool toggleMute = false, Action<string>? trace = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var enumerator = (IDeviceEnumerator)new DeviceEnumerator();
        IDeviceCollection? devices = null;
        var changed = 0;
        try
        {
            enumerator.EnumAudioEndpoints(0, 1, out devices);
            devices.GetCount(out var count);
            trace?.Invoke($"audio devices={count} targetPid={processId}");
            for (uint d = 0; d < count; d++)
            {
                devices.Item(d, out var device);
                object? managerObject = null;
                ISessionEnumerator? sessions = null;
                try
                {
                    var iid = typeof(ISessionManager).GUID;
                    device.Activate(ref iid, 23, 0, out managerObject);
                    var manager = (ISessionManager)managerObject;
                    manager.GetSessionEnumerator(out sessions);
                    sessions.GetCount(out var length);
                    trace?.Invoke($"audio device={d} sessions={length}");
                    for (var i = 0; i < length; i++)
                    {
                        sessions.GetSession(i, out var session);
                        try
                        {
                            session.GetProcessId(out var pid);
                            trace?.Invoke($"audio session device={d} index={i} pid={pid} match={pid == processId}");
                            if (pid != processId) continue;
                            var volume = (ISimpleVolume)session;
                            var context = Guid.Empty;
                            if (toggleMute)
                            {
                                volume.GetMute(out var current);
                                mute ??= !current;
                            }
                            if (mute.HasValue) { volume.SetMute(mute.Value, ref context); trace?.Invoke($"audio mute={mute.Value}"); }
                            else { volume.GetMasterVolume(out var level); volume.SetMasterVolume(Math.Clamp(level + delta, 0, 1), ref context); }
                            changed++;
                        }
                        finally { Marshal.ReleaseComObject(session); }
                    }
                }
                finally
                {
                    if (sessions is not null) Marshal.ReleaseComObject(sessions);
                    if (managerObject is not null) Marshal.ReleaseComObject(managerObject);
                    Marshal.ReleaseComObject(device);
                }
            }
            return changed;
        }
        finally { if (devices is not null) Marshal.ReleaseComObject(devices); Marshal.ReleaseComObject(enumerator); }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator { void EnumAudioEndpoints(int flow, uint mask, out IDeviceCollection devices); }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceCollection { void GetCount(out uint count); void Item(uint index, out IDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice { void Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance); }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionManager
    {
        void GetAudioSessionControl(nint guid, uint flags, out nint control);
        void GetSimpleAudioVolume(nint guid, uint flags, out nint volume);
        void GetSessionEnumerator(out ISessionEnumerator sessions);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionEnumerator { void GetCount(out int count); void GetSession(int index, out ISessionControl session); }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionControl
    {
        void GetState(out int state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, nint context);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, nint context);
        void GetGroupingParam(out Guid grouping);
        void SetGroupingParam(ref Guid grouping, nint context);
        void RegisterAudioSessionNotification(nint events);
        void UnregisterAudioSessionNotification(nint events);
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetProcessId(out uint pid);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleVolume
    {
        void SetMasterVolume(float level, ref Guid context);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
