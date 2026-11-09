using System.Text.Json;

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

public static class ProfileTransfer
{
    /// <summary>Imports copies, preserving existing profiles, UI settings and window selections.</summary>
    public static ProfileDocument Merge(ProfileDocument current, ProfileDocument imported)
    {
        Validate(current);
        Validate(imported);
        var result = Clone(current);
        var incoming = Clone(imported);
        var ids = new Dictionary<Guid, Guid>();
        foreach (var profile in incoming.Profiles)
        {
            var old = profile.Id;
            profile.Id = Guid.NewGuid();
            ids[old] = profile.Id;
            if (result.Profiles.Any(p => p.Name == profile.Name)) profile.Name = profile.Name[..Math.Min(190, profile.Name.Length)] + " (가져옴)";
            foreach (var mapping in profile.Mappings) mapping.Id = Guid.NewGuid();
            result.Profiles.Add(profile);
        }
        foreach (var selection in incoming.WindowSelections)
        {
            selection.ProfileId = ids[selection.ProfileId];
            if (!result.WindowSelections.Any(s => ProfileSelections.SameWindow(s.Window, selection.Window)))
                result.WindowSelections.Add(selection);
        }
        Validate(result);
        return result;
    }

    private static ProfileDocument Clone(ProfileDocument source) => JsonSerializer.Deserialize<ProfileDocument>(JsonSerializer.Serialize(source))!;
    private static void Validate(ProfileDocument document)
    {
        var errors = ProfileValidator.Validate(document);
        if (errors.Count > 0) throw new ProfileValidationException(errors);
    }
}
