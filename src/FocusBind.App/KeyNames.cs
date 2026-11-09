using System.Windows.Input;
using FocusBind.Core.Profiles;
using Modifiers = FocusBind.Core.Profiles.ModifierKeys;
using System.Globalization;
using System.Windows.Data;

namespace FocusBind.App;

internal static class KeyNames
{
    internal static string Format(int code, Modifiers modifiers)
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
        parts.Add($"{name} [VK {code} / 0x{code:X2}]");
        return string.Join(" + ", parts);
    }

    internal static Modifiers Current() =>
        (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? Modifiers.Control : 0) |
        (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? Modifiers.Shift : 0) |
        (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? Modifiers.Alt : 0) |
        (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows) ? Modifiers.Windows : 0);
}

public sealed class TriggerLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is InputTrigger trigger
        ? trigger.Kind == TriggerKind.Key ? KeyNames.Format(trigger.KeyCode, trigger.Modifiers)
            : $"{trigger.Modifiers} · {trigger.Kind} · 버튼 {trigger.MouseButton} · {trigger.WheelDirection}"
        : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
