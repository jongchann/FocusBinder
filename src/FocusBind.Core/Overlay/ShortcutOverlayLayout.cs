using FocusBind.Core.Coordinates;
using FocusBind.Core.Input;
using FocusBind.Core.Profiles;

namespace FocusBind.Core.Overlay;

public sealed record ShortcutOverlayHint(Guid MappingId, string Name, InputTrigger Trigger, PixelPoint? Position);

public static class ShortcutOverlayLayout
{
    public static bool ShouldShow(ShortcutOverlaySettings settings, bool foregroundReady, bool targetMatches,
        bool hasProfile) => settings.Enabled && settings.TargetProcess is not null && settings.TransparencyPercent < 100 && foregroundReady && targetMatches && hasProfile;

    /// <summary>Positions use physical screen pixels, exactly as the action runner does.</summary>
    public static IReadOnlyList<ShortcutOverlayHint> Build(Profile profile, CoordinateContext context)
    {
        var hints = new List<ShortcutOverlayHint>();
        foreach (var mapping in profile.Mappings.Where(m => m.Enabled && m.Actions.Steps.Count > 0 && InputActionRunner.Supports(m.Actions, true)))
        {
            var coordinates = FindCoordinates(mapping.Actions.Steps).ToArray();
            if (coordinates.Length == 0)
                hints.Add(new ShortcutOverlayHint(mapping.Id, mapping.Name, mapping.Trigger, null));
            else
            {
                var positions = new HashSet<PixelPoint>();
                foreach (var target in coordinates)
                    if (CoordinateTransform.TryResolve(context, target, out var point) && positions.Add(point))
                        hints.Add(new ShortcutOverlayHint(mapping.Id, mapping.Name, mapping.Trigger, point));
            }
        }
        return hints;
    }
    private static IEnumerable<CoordinateTarget> FindCoordinates(IEnumerable<AutomationAction> actions)
    {
        foreach (var action in actions)
        {
            if (action.Kind == ActionKind.CoordinateClick && action.Coordinate is not null) yield return action.Coordinate;
            if (action.Kind == ActionKind.Repeat)
                foreach (var target in FindCoordinates(action.Steps)) yield return target;
        }
    }
}
