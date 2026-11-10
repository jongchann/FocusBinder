using System.Windows.Input;
using FocusBind.Core.Profiles;
using Modifiers = FocusBind.Core.Profiles.ModifierKeys;
using System.Globalization;
using System.Windows.Data;

namespace FocusBind.App;

internal static class KeyNames
{
    internal static Key ResolveKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };
    internal static string Format(int code, Modifiers modifiers, bool includeCode = true)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(Modifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(Modifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(Modifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(Modifiers.Windows)) parts.Add("Win");
        var name = code switch
        {
            0x20 => "Space (스페이스)", 0x0D => "Enter (엔터)", 0x1B => "Esc",
            0x08 => "Backspace", 0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
            >= 0x30 and <= 0x39 => ((char)code).ToString(),
            _ => KeyInterop.KeyFromVirtualKey(code).ToString()
        };
        parts.Add(includeCode ? $"{name} [VK {code} / 0x{code:X2}]" : name);
        return string.Join(" + ", parts);
    }

    internal static Modifiers Current() =>
        (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? Modifiers.Control : 0) |
        (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? Modifiers.Shift : 0) |
        (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? Modifiers.Alt : 0) |
        (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows) ? Modifiers.Windows : 0);

    internal static string FormatTrigger(InputTrigger trigger, bool includeCode = true)
    {
        if (trigger.Kind == TriggerKind.Key) return Format(trigger.KeyCode, trigger.Modifiers, includeCode);
        var parts = new List<string>();
        if (trigger.Modifiers.HasFlag(Modifiers.Control)) parts.Add("Ctrl");
        if (trigger.Modifiers.HasFlag(Modifiers.Alt)) parts.Add("Alt");
        if (trigger.Modifiers.HasFlag(Modifiers.Shift)) parts.Add("Shift");
        if (trigger.Modifiers.HasFlag(Modifiers.Windows)) parts.Add("Win");
        parts.Add(trigger.Kind switch
        {
            TriggerKind.MouseButton => trigger.MouseButton switch { 1 => "마우스 왼쪽", 2 => "마우스 오른쪽", 3 => "마우스 가운데", 4 => "마우스 X1", _ => "마우스 X2" },
            TriggerKind.VerticalWheel => trigger.WheelDirection == WheelDirection.Positive ? "휠 ↑" : "휠 ↓",
            _ => trigger.WheelDirection == WheelDirection.Positive ? "휠 →" : "휠 ←"
        });
        return string.Join(" + ", parts);
    }
}

public sealed class TriggerLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is InputTrigger trigger
        ? KeyNames.FormatTrigger(trigger)
        : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
