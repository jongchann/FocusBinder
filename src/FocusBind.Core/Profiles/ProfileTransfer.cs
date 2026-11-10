using System.Text.Json;
using FocusBind.Core.Input;

namespace FocusBind.Core.Profiles;

public static class ProfileSelections
{
    public static bool SameWindow(WindowMatcher first, WindowMatcher second) =>
        string.Equals(first.ExecutablePath ?? "", second.ExecutablePath ?? "", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.ExecutableName ?? "", second.ExecutableName ?? "", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.WindowClass ?? "", second.WindowClass ?? "", StringComparison.Ordinal) &&
        string.Equals(first.TitlePattern ?? "", second.TitlePattern ?? "", StringComparison.Ordinal);

    public static void Select(ProfileDocument document, Guid profileId, WindowIdentity window)
    {
        var profile = document.Profiles.Single(p => p.Id == profileId);
        if (!ProfileResolver.Matches(profile.Window, window)) throw new ArgumentException("Profile does not match the target window.");
        var matcher = new WindowMatcher
        {
            ExecutablePath = string.IsNullOrWhiteSpace(window.ExecutablePath) ? null : window.ExecutablePath,
            ExecutableName = string.IsNullOrWhiteSpace(window.ExecutablePath) ? window.ProcessName : Path.GetFileName(window.ExecutablePath),
            WindowClass = window.WindowClass
        };
        document.WindowSelections.RemoveAll(s => SameWindow(s.Window, matcher));
        document.WindowSelections.Insert(0, new WindowSelection { ProfileId = profileId, Window = matcher });
    }
}

public enum ProfileImportMode { Copy, Merge, Skip }
public sealed record ProfileImportChoice(ProfileImportMode Mode, Guid? TargetId = null);

public static class ProfileTransfer
{
    public static IReadOnlyList<Profile> Conflicts(ProfileDocument current, Profile incoming) =>
        current.Profiles.Where(p => p.Id == incoming.Id ||
            string.Equals(p.Name.Trim(), incoming.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Id == incoming.Id).ToArray();

    /// <summary>Imports copies by default; explicit merges preserve existing settings and conflicting mappings.</summary>
    public static ProfileDocument Merge(ProfileDocument current, ProfileDocument imported,
        IReadOnlyDictionary<Guid, ProfileImportChoice>? choices = null)
    {
        Validate(current);
        Validate(imported);
        var result = Clone(current);
        var incoming = Clone(imported);
        var ids = new Dictionary<Guid, Guid>();
        foreach (var profile in incoming.Profiles)
        {
            var choice = choices?.GetValueOrDefault(profile.Id) ?? new(ProfileImportMode.Copy);
            if (!Enum.IsDefined(choice.Mode)) throw new ArgumentException("Unknown import mode.");
            if (choice.Mode == ProfileImportMode.Skip) continue;
            if (choice.Mode == ProfileImportMode.Merge)
            {
                if (!Conflicts(current, profile).Any(p => p.Id == choice.TargetId))
                    throw new ArgumentException("Select a conflicting profile to merge into.");
                var target = result.Profiles.Single(p => p.Id == choice.TargetId);
                ids[profile.Id] = target.Id;
                MergeMappings(target, profile);
                continue;
            }
            var old = profile.Id;
            profile.Id = Guid.NewGuid();
            ids[old] = profile.Id;
            var name = profile.Name;
            for (var suffix = 1; result.Profiles.Any(p => string.Equals(p.Name.Trim(), profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)); suffix++)
                profile.Name = name[..Math.Min(175, name.Length)] + $" (가져옴 {suffix})";
            foreach (var mapping in profile.Mappings) mapping.Id = Guid.NewGuid();
            result.Profiles.Add(profile);
        }
        foreach (var selection in incoming.WindowSelections)
        {
            if (!ids.TryGetValue(selection.ProfileId, out var selectedId)) continue;
            selection.ProfileId = selectedId;
            if (!result.WindowSelections.Any(s => ProfileSelections.SameWindow(s.Window, selection.Window)))
                result.WindowSelections.Add(selection);
        }
        Validate(result);
        return result;
    }

    private static void MergeMappings(Profile target, Profile incoming)
    {
        var layouts = new Dictionary<Guid, Guid>();
        foreach (var mapping in incoming.Mappings)
        {
            if (target.Mappings.Any(m => m.Id == mapping.Id || TriggerConflictDetector.SameTrigger(m.Trigger, mapping.Trigger))) continue;
            Remap(mapping.Actions.Steps);
            mapping.Id = Guid.NewGuid();
            target.Mappings.Add(mapping);
        }

        void Remap(IEnumerable<AutomationAction> actions)
        {
            foreach (var action in actions)
            {
                if (action.Kind == ActionKind.CoordinateClick && action.Coordinate is { } coordinate)
                {
                    if (!layouts.TryGetValue(coordinate.LayoutId, out var id))
                    {
                        var source = incoming.Layouts.Single(l => l.Id == coordinate.LayoutId);
                        // Ignore identity/default status when reusing equivalent layout geometry.
                        string Signature(LayoutVariant layout)
                        {
                            var copy = JsonSerializer.Deserialize<LayoutVariant>(JsonSerializer.Serialize(layout))!;
                            copy.Id = Guid.Empty; copy.IsDefault = false;
                            return JsonSerializer.Serialize(copy);
                        }
                        var existing = target.Layouts.FirstOrDefault(l => Signature(l) == Signature(source));
                        if (existing is null)
                        {
                            existing = JsonSerializer.Deserialize<LayoutVariant>(JsonSerializer.Serialize(source))!;
                            existing.Id = Guid.NewGuid(); existing.IsDefault = false;
                            target.Layouts.Add(existing);
                        }
                        layouts[coordinate.LayoutId] = id = existing.Id;
                    }
                    coordinate.LayoutId = id;
                }
                Remap(action.Steps); Remap(action.ElseSteps);
            }
        }
    }

    private static ProfileDocument Clone(ProfileDocument source) => JsonSerializer.Deserialize<ProfileDocument>(JsonSerializer.Serialize(source))!;
    private static void Validate(ProfileDocument document)
    {
        var errors = ProfileValidator.Validate(document);
        if (errors.Count > 0) throw new ProfileValidationException(errors);
    }
}
