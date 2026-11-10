using FocusBind.Core.Profiles;

namespace FocusBind.Core.Input;

/// <summary>Processes physical input on a single hook thread. Reset when the target or profile changes.</summary>
public sealed class TriggerStateMachine
{
    private readonly HashSet<int> _keys = [];
    private readonly HashSet<int> _buttons = [];
    private readonly Dictionary<(TriggerKind Kind, int Code), long> _pressedAt = [];
    private readonly HashSet<(TriggerKind Kind, int Code)> _suppressed = [];
    private readonly HashSet<Guid> _longPressFired = [];

    public void Reset()
    {
        _keys.Clear();
        _buttons.Clear();
        _pressedAt.Clear();
        _suppressed.Clear();
        _longPressFired.Clear();
    }

    public InputDecision Process(InputEvent input, IReadOnlyList<Mapping> mappings, ModifierKeys? wheelModifiers = null)
    {
        if (input.Kind is TriggerKind.VerticalWheel or TriggerKind.HorizontalWheel)
        {
            var wheelMatches = mappings.Where(m => m.Enabled && m.Trigger.Kind == input.Kind
                && m.Trigger.WheelDirection == input.WheelDirection
                && m.Trigger.Modifiers == (wheelModifiers ?? CurrentModifiers())).ToArray();
            return new InputDecision(wheelMatches.Any(m => m.ConsumeOriginal), wheelMatches);
        }

        var pressed = input.Kind == TriggerKind.Key ? _keys : _buttons;
        var id = (input.Kind, input.Code);
        var wasPressed = pressed.Contains(input.Code);
        if (input.IsDown && !wasPressed)
        {
            pressed.Add(input.Code);
            _pressedAt[id] = input.TimestampMilliseconds;
        }

        var modifiers = CurrentModifiers(input.Kind == TriggerKind.Key ? input.Code : 0);
        var matching = mappings.Where(m => m.Enabled && m.Trigger.Kind == input.Kind
            && TriggerCode(m.Trigger) == input.Code && m.Trigger.Modifiers == modifiers).ToArray();
        var fired = input.IsDown
            ? wasPressed ? [] : matching.Where(m => m.Trigger.Phase == TriggerPhase.Press).ToArray()
            : wasPressed ? matching.Where(m => m.Trigger.Phase == TriggerPhase.Release).ToArray() : [];

        if (input.IsDown && !wasPressed && matching.Any(m => m.ConsumeOriginal)) _suppressed.Add(id);
        var suppress = _suppressed.Contains(id);
        if (!input.IsDown)
        {
            pressed.Remove(input.Code);
            _pressedAt.Remove(id);
            _suppressed.Remove(id);
            foreach (var mapping in mappings.Where(m => m.Trigger.Kind == input.Kind && TriggerCode(m.Trigger) == input.Code))
                _longPressFired.Remove(mapping.Id);
        }
        return new InputDecision(suppress, fired);
    }

    public IReadOnlyList<Mapping> Poll(long timestampMilliseconds, IReadOnlyList<Mapping> mappings)
    {
        var fired = new List<Mapping>();
        foreach (var mapping in mappings)
        {
            var trigger = mapping.Trigger;
            if (!mapping.Enabled || trigger.Phase != TriggerPhase.LongPress ||
                trigger.Kind is not (TriggerKind.Key or TriggerKind.MouseButton) ||
                _longPressFired.Contains(mapping.Id)) continue;
            var id = (trigger.Kind, TriggerCode(trigger));
            if (!_pressedAt.TryGetValue(id, out var started) ||
                timestampMilliseconds - started < trigger.LongPressMilliseconds ||
                trigger.Modifiers != CurrentModifiers(trigger.Kind == TriggerKind.Key ? trigger.KeyCode : 0)) continue;
            _longPressFired.Add(mapping.Id);
            fired.Add(mapping);
        }
        return fired;
    }

    private static int TriggerCode(InputTrigger trigger) =>
        trigger.Kind == TriggerKind.Key ? trigger.KeyCode : trigger.MouseButton;

    private ModifierKeys CurrentModifiers(int excludedKey = 0)
    {
        bool Has(int first, int second, int third = 0) =>
            first != excludedKey && _keys.Contains(first) ||
            second != excludedKey && _keys.Contains(second) ||
            third != 0 && third != excludedKey && _keys.Contains(third);
        var result = ModifierKeys.None;
        if (Has(0x10, 0xA0, 0xA1)) result |= ModifierKeys.Shift;
        if (Has(0x11, 0xA2, 0xA3)) result |= ModifierKeys.Control;
        if (Has(0x12, 0xA4, 0xA5)) result |= ModifierKeys.Alt;
        if (Has(0x5B, 0x5C)) result |= ModifierKeys.Windows;
        return result;
    }
}
