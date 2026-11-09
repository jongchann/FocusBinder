using System.Text.RegularExpressions;

namespace FocusBind.Core.Profiles;

public sealed record WindowIdentity(string ExecutablePath, string Title, string WindowClass, string? ProcessName = null);

public static class ProfileResolver
{
    public static Profile? Resolve(ProfileDocument document, WindowIdentity window)
    {
        var matching = document.Profiles.Where(p => Matches(p.Window, window)).ToList();
        var selection = document.WindowSelections.FirstOrDefault(s => Matches(s.Window, window) && matching.Any(p => p.Id == s.ProfileId));
        if (selection is not null) return matching.First(p => p.Id == selection.ProfileId);
        return matching.OrderByDescending(p => p.Priority)
            .ThenByDescending(p => Specificity(p.Window))
            .ThenBy(p => p.Id).FirstOrDefault();
    }

    public static bool Matches(WindowMatcher matcher, WindowIdentity window)
    {
        var pathUnavailable = string.IsNullOrWhiteSpace(window.ExecutablePath);
        // A protected/elevated process may reveal its name but deny its full path.
        // Require the saved window class as a second identifier in that case.
        if (pathUnavailable && string.IsNullOrWhiteSpace(matcher.WindowClass)) return false;
        if (!string.IsNullOrWhiteSpace(matcher.ExecutablePath))
        {
            if (pathUnavailable)
            {
                if (!string.Equals(System.IO.Path.GetFileName(matcher.ExecutablePath), window.ProcessName,
                        StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (!string.Equals(System.IO.Path.GetFullPath(matcher.ExecutablePath),
                         System.IO.Path.GetFullPath(window.ExecutablePath), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(matcher.ExecutableName)
            && !string.Equals(matcher.ExecutableName,
                pathUnavailable ? window.ProcessName : System.IO.Path.GetFileName(window.ExecutablePath),
                StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(matcher.WindowClass)
            && !string.Equals(matcher.WindowClass, window.WindowClass, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(matcher.TitlePattern)
            && !Regex.IsMatch(window.Title, matcher.TitlePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return false;
        return !string.IsNullOrWhiteSpace(matcher.ExecutablePath) || !string.IsNullOrWhiteSpace(matcher.ExecutableName);
    }

    private static int Specificity(WindowMatcher matcher) =>
        (string.IsNullOrWhiteSpace(matcher.ExecutablePath) ? 0 : 8) +
        (string.IsNullOrWhiteSpace(matcher.ExecutableName) ? 0 : 4) +
        (string.IsNullOrWhiteSpace(matcher.WindowClass) ? 0 : 2) +
        (string.IsNullOrWhiteSpace(matcher.TitlePattern) ? 0 : 1);
}
