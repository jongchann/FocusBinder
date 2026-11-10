using FocusBind.Core.Profiles;

namespace FocusBind.Core.Input;

/// <summary>Encodes wheel chords as existing key-down/wheel/key-up steps, preserving schema 1.</summary>
public static class WheelChord
{
    private static readonly (ModifierKeys Flag, int Key)[] Keys =
        [(ModifierKeys.Control, 0x11), (ModifierKeys.Alt, 0x12), (ModifierKeys.Shift, 0x10), (ModifierKeys.Windows, 0x5B)];

    public static List<AutomationAction> Wrap(AutomationAction wheel, ModifierKeys modifiers)
    {
        if (wheel.Kind is not (ActionKind.VerticalWheel or ActionKind.HorizontalWheel) || ((int)modifiers & ~15) != 0)
            throw new ArgumentException("Expected a wheel and valid modifiers.");
        var keys = Keys.Where(k => modifiers.HasFlag(k.Flag)).ToArray();
        return [.. keys.Select(k => new AutomationAction { Kind = ActionKind.KeyDown, KeyCode = k.Key }), wheel,
            .. keys.Reverse().Select(k => new AutomationAction { Kind = ActionKind.KeyUp, KeyCode = k.Key })];
    }

    public static bool TryRead(IReadOnlyList<AutomationAction> steps, out AutomationAction? wheel, out ModifierKeys modifiers)
    {
        wheel = null; modifiers = ModifierKeys.None;
        if (steps.Count is < 1 or > 9 || steps.Count % 2 == 0) return false;
        var middle = steps.Count / 2;
        if (steps[middle].Kind is not (ActionKind.VerticalWheel or ActionKind.HorizontalWheel)) return false;
        var flags = ModifierKeys.None;
        for (var i = 0; i < middle; i++)
        {
            var down = steps[i]; var up = steps[steps.Count - 1 - i];
            var flag = FlagForKey(down.KeyCode);
            if (down.Kind != ActionKind.KeyDown || up.Kind != ActionKind.KeyUp || up.KeyCode != down.KeyCode ||
                flag == ModifierKeys.None || flags.HasFlag(flag)) return false;
            flags |= flag;
        }
        wheel = steps[middle]; modifiers = flags; return true;
    }

    public static ModifierKeys FlagForKey(int code) => code switch
    {
        0x11 or 0xA2 or 0xA3 => ModifierKeys.Control,
        0x12 or 0xA4 or 0xA5 => ModifierKeys.Alt,
        0x10 or 0xA0 or 0xA1 => ModifierKeys.Shift,
        0x5B or 0x5C => ModifierKeys.Windows,
        _ => ModifierKeys.None
    };
}
