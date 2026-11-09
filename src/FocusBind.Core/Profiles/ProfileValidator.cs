using System.Text.RegularExpressions;
using FocusBind.Core.Input;

namespace FocusBind.Core.Profiles;

public static class ProfileValidator
{
    public static IReadOnlyList<string> Validate(ProfileDocument? document)
    {
        var errors = new List<string>();
        if (document is null) return ["Document is null."];
        if (document.SchemaVersion != ProfileDocument.CurrentSchemaVersion)
            errors.Add($"Unsupported schemaVersion: {document.SchemaVersion}.");
        if (document.Profiles is null || document.WindowSelections is null || document.Ui is null)
            return [.. errors, "Document collections and UI settings are required."];

        var profileIds = new HashSet<Guid>();
        foreach (var profile in document.Profiles)
        {
            if (profile is null) { errors.Add("Null profile."); continue; }
            var path = $"profiles[{profile.Id}]";
            CheckId(profile.Id, path, profileIds, errors);
            CheckName(profile.Name, path, errors);
            CheckMatcher(profile.Window, path + ".window", errors);
            if (profile.MaxConcurrentRuns is < 1 or > 32) errors.Add(path + ".maxConcurrentRuns must be 1..32.");
            if (profile.MaxRunMilliseconds is < 100 or > 3600000) errors.Add(path + ".maxRunMilliseconds must be 100..3600000.");
            if (profile.Layouts is null || profile.Mappings is null) { errors.Add(path + " collections are required."); continue; }
            if (profile.Layouts.Count(l => l?.IsDefault == true) != 1) errors.Add(path + " needs exactly one default layout.");
            var layoutIds = new HashSet<Guid>();
            foreach (var layout in profile.Layouts)
            {
                if (layout is null) { errors.Add(path + " has null layout."); continue; }
                var lp = path + $".layouts[{layout.Id}]";
                CheckId(layout.Id, lp, layoutIds, errors);
                CheckName(layout.Name, lp, errors);
                if (layout.ReferenceWidth < 1 || layout.ReferenceHeight < 1) errors.Add(lp + " reference size must be positive.");
                if (!Finite(layout.MinAspectRatio) || !Finite(layout.MaxAspectRatio) || layout.MinAspectRatio <= 0 || layout.MaxAspectRatio < layout.MinAspectRatio)
                    errors.Add(lp + " aspect ratio range is invalid.");
                if (layout.Content is null || !Finite(layout.Content.X) || !Finite(layout.Content.Y) || !Finite(layout.Content.Width) || !Finite(layout.Content.Height)
                    || layout.Content.X < 0 || layout.Content.Y < 0 || layout.Content.Width <= 0 || layout.Content.Height <= 0
                    || layout.Content.X + layout.Content.Width > 1 || layout.Content.Y + layout.Content.Height > 1)
                    errors.Add(lp + " content rectangle must fit inside normalized client bounds.");
                if (!Enum.IsDefined(layout.Scale)) errors.Add(lp + " scale policy is invalid.");
            }
            var mappingIds = new HashSet<Guid>();
            foreach (var mapping in profile.Mappings)
            {
                if (mapping is null) { errors.Add(path + " has null mapping."); continue; }
                var mp = path + $".mappings[{mapping.Id}]";
                CheckId(mapping.Id, mp, mappingIds, errors);
                CheckName(mapping.Name, mp, errors);
                CheckTrigger(mapping.Trigger, mp + ".trigger", errors);
                if (mapping.Actions is null || !Enum.IsDefined(mapping.Actions.Concurrency) || mapping.Actions.Steps is null)
                    errors.Add(mp + " action sequence is invalid.");
                else CheckActions(mapping.Actions.Steps, mp + ".actions", layoutIds, errors, 0);
            }
            if (profile.Mappings.All(m => m?.Trigger is not null))
                foreach (var conflict in TriggerConflictDetector.Find(profile))
                    errors.Add($"{path} has duplicate trigger in mappings {conflict.First.Id} and {conflict.Second.Id}.");
        }
        foreach (var selection in document.WindowSelections)
        {
            if (selection is null) { errors.Add("Null window selection."); continue; }
            if (!profileIds.Contains(selection.ProfileId)) errors.Add($"Selection references missing profile {selection.ProfileId}.");
            CheckMatcher(selection.Window, "windowSelections.window", errors);
        }
        return errors;
    }

    private static void CheckActions(List<AutomationAction> actions, string path, HashSet<Guid> layoutIds, List<string> errors, int depth)
    {
        if (depth > 12) { errors.Add(path + " nesting exceeds 12 levels."); return; }
        if (actions.Count > 1000) errors.Add(path + " has more than 1000 actions.");
        foreach (var action in actions)
        {
            if (action is null) { errors.Add(path + " has null action."); continue; }
            var ap = path + $"[{action.Kind}]";
            if (!Enum.IsDefined(action.Kind)) { errors.Add(ap + " kind is invalid."); continue; }
            if (action.Steps is null || action.ElseSteps is null) { errors.Add(ap + " child actions are null."); continue; }
            switch (action.Kind)
            {
                case ActionKind.KeyDown or ActionKind.KeyUp or ActionKind.KeyTap when action.KeyCode is < 1 or > 255:
                    errors.Add(ap + " keyCode must be 1..255."); break;
                case ActionKind.MouseDown or ActionKind.MouseUp or ActionKind.MouseClick when action.MouseButton is < 1 or > 5:
                    errors.Add(ap + " mouseButton must be 1..5."); break;
                case ActionKind.Delay when action.DelayMilliseconds is < 0 or > 60000:
                    errors.Add(ap + " delayMilliseconds must be 0..60000."); break;
                case ActionKind.Repeat when action.RepeatCount is < 1 or > 1000:
                    errors.Add(ap + " repeatCount must be 1..1000."); break;
                case ActionKind.CoordinateClick:
                    var c = action.Coordinate;
                    if (c is null || !layoutIds.Contains(c.LayoutId) || !Enum.IsDefined(c.Space) || !Enum.IsDefined(c.Anchor)
                        || !Enum.IsDefined(c.ClickMode)
                        || !Finite(c.X) || !Finite(c.Y) || (c.Space is CoordinateSpace.ClientNormalized or CoordinateSpace.ContentNormalized && (c.X < 0 || c.X > 1 || c.Y < 0 || c.Y > 1)))
                        errors.Add(ap + " coordinate is invalid.");
                    break;
                case ActionKind.Conditional when string.IsNullOrWhiteSpace(action.Condition): errors.Add(ap + " needs condition."); break;
                case ActionKind.CallSequence when string.IsNullOrWhiteSpace(action.SequenceName): errors.Add(ap + " needs sequenceName."); break;
                case ActionKind.Lua when string.IsNullOrWhiteSpace(action.LuaSource): errors.Add(ap + " needs luaSource."); break;
            }
            CheckActions(action.Steps, ap + ".steps", layoutIds, errors, depth + 1);
            CheckActions(action.ElseSteps, ap + ".elseSteps", layoutIds, errors, depth + 1);
        }
    }

    private static void CheckTrigger(InputTrigger? trigger, string path, List<string> errors)
    {
        if (trigger is null || !Enum.IsDefined(trigger.Kind) || !Enum.IsDefined(trigger.Phase)
            || (trigger.Modifiers & ~((ModifierKeys)15)) != 0) { errors.Add(path + " is invalid."); return; }
        if (trigger.Kind == TriggerKind.Key && trigger.KeyCode is < 1 or > 255) errors.Add(path + " keyCode must be 1..255.");
        if (trigger.Kind == TriggerKind.MouseButton && trigger.MouseButton is < 1 or > 5) errors.Add(path + " mouseButton must be 1..5.");
        if (trigger.Kind is TriggerKind.VerticalWheel or TriggerKind.HorizontalWheel)
        {
            if (!Enum.IsDefined(trigger.WheelDirection) || trigger.WheelDirection == WheelDirection.None)
                errors.Add(path + " wheel direction is required.");
            if (trigger.Phase != TriggerPhase.Press)
                errors.Add(path + " wheel triggers support press phase only.");
        }
        if (trigger.Phase == TriggerPhase.LongPress && trigger.LongPressMilliseconds is < 50 or > 60000)
            errors.Add(path + " long press duration must be 50..60000.");
    }

    private static void CheckMatcher(WindowMatcher? matcher, string path, List<string> errors)
    {
        if (matcher is null) { errors.Add(path + " is required."); return; }
        if (string.IsNullOrWhiteSpace(matcher.ExecutablePath) && string.IsNullOrWhiteSpace(matcher.ExecutableName))
            errors.Add(path + " needs executable path or name.");
        if (!string.IsNullOrEmpty(matcher.TitlePattern))
        {
            try { _ = new Regex(matcher.TitlePattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { errors.Add(path + " titlePattern is invalid regex."); }
        }
    }

    private static void CheckId(Guid id, string path, HashSet<Guid> ids, List<string> errors)
    {
        if (id == Guid.Empty || !ids.Add(id)) errors.Add(path + " has empty or duplicate ID.");
    }
    private static void CheckName(string? name, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) errors.Add(path + " name must be 1..200 characters.");
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
