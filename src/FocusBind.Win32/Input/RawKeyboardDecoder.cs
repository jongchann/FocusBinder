using FocusBind.Core.Input;
using FocusBind.Core.Profiles;

namespace FocusBind.Win32.Input;

public static class RawKeyboardDecoder
{
    public static bool TryDecode(ReadOnlySpan<byte> data, int headerSize, long timestamp, out InputEvent input)
    {
        input = default;
        if (headerSize < 0 || data.Length < headerSize + 16 || BitConverter.ToUInt32(data) != 1)
            return false;
        var keyboard = data[headerSize..];
        var flags = BitConverter.ToUInt16(keyboard[2..]);
        var virtualKey = BitConverter.ToUInt16(keyboard[6..]);
        var message = BitConverter.ToUInt32(keyboard[8..]);
        if (virtualKey is 0 or 255 || message is not (0x0100 or 0x0101 or 0x0104 or 0x0105))
            return false;
        input = new InputEvent(TriggerKind.Key, virtualKey,
            (flags & 1) == 0, WheelDirection.None, timestamp);
        return true;
    }
}
