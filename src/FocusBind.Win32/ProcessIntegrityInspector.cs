using System.Runtime.InteropServices;

namespace FocusBind.Win32;

public static class ProcessIntegrityInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int informationClass, nint buffer,
        uint bufferSize, out uint requiredSize);
    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint index);

    public static int? TryGetRid(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
            try
            {
                GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var required);
                if (required < (uint)IntPtr.Size || required > 4096) return null;
                var buffer = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, required, out _)) return null;
                    var sid = Marshal.ReadIntPtr(buffer);
                    if (sid == 0) return null;
                    var countPointer = GetSidSubAuthorityCount(sid);
                    if (countPointer == 0) return null;
                    var count = Marshal.ReadByte(countPointer);
                    if (count == 0) return null;
                    var ridPointer = GetSidSubAuthority(sid, (uint)(count - 1));
                    return ridPointer == 0 ? null : Marshal.ReadInt32(ridPointer);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
}
