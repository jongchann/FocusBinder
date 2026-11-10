using FocusBind.Core.Profiles;

namespace FocusBind.Core.Input;

public sealed record TriggerConflict(Mapping First, Mapping Second);

public static class TriggerConflictDetector
{
    public static IReadOnlyList<TriggerConflict> Find(Profile profile)
    {
        var conflicts = new List<TriggerConflict>();
        var enabled = profile.Mappings.Where(mapping => mapping.Enabled).ToArray();
        for (var first = 0; first < enabled.Length; first++)
        for (var second = first + 1; second < enabled.Length; second++)
        {
            if (SameTrigger(enabled[first].Trigger, enabled[second].Trigger))
                conflicts.Add(new TriggerConflict(enabled[first], enabled[second]));
        }
        return conflicts;
    }

    public static bool SameTrigger(InputTrigger first, InputTrigger second) =>
        first.Kind == second.Kind && first.Phase == second.Phase && first.Modifiers == second.Modifiers &&
        (first.Kind switch
        {
            TriggerKind.Key => first.KeyCode == second.KeyCode,
            TriggerKind.MouseButton => first.MouseButton == second.MouseButton,
            TriggerKind.VerticalWheel or TriggerKind.HorizontalWheel => first.WheelDirection == second.WheelDirection,
            _ => false
        }) &&
        (first.Phase != TriggerPhase.LongPress || first.LongPressMilliseconds == second.LongPressMilliseconds);
}
