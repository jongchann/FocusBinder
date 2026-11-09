using FocusBind.Core.Profiles;

namespace FocusBind.Core.Input;

/// <summary>KeyCode is a virtual key for keys, or a 1..5 button number for mouse buttons.</summary>
public readonly record struct InputEvent(
    TriggerKind Kind, int Code, bool IsDown, WheelDirection WheelDirection, long TimestampMilliseconds);

public sealed record InputDecision(bool SuppressOriginal, IReadOnlyList<Mapping> FiredMappings)
{
    public static InputDecision Pass { get; } = new(false, []);
}
