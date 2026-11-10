using System.Text.Json;
using System.Text.Json.Serialization;
using FocusBind.Core.Profiles;

namespace FocusBind.Core.Overlay;

public sealed class ShortcutOverlaySettings
{
    public bool Enabled { get; set; }
    public int TransparencyPercent { get; set; } = 35;
    public OverlayProcessTarget? TargetProcess { get; set; }
    [JsonIgnore] public double Opacity => 1 - TransparencyPercent / 100.0;
    public void Validate()
    {
        if (TransparencyPercent is < 0 or > 100)
            throw new InvalidDataException("오버레이 투명도는 0~100%여야 합니다.");
        TargetProcess?.Validate();
    }
}

public sealed class OverlayProcessTarget
{
    public string ExecutableName { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public static OverlayProcessTarget FromWindow(WindowIdentity identity) => new()
    {
        ExecutableName = string.IsNullOrWhiteSpace(identity.ExecutablePath) ? identity.ProcessName ?? "" : Path.GetFileName(identity.ExecutablePath),
        ExecutablePath = string.IsNullOrWhiteSpace(identity.ExecutablePath) ? null : identity.ExecutablePath
    };
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutableName) || ExecutableName.IndexOfAny(['\\', '/']) >= 0)
            throw new InvalidDataException("대상 프로세스의 실행 파일 이름이 올바르지 않습니다.");
        var errors = ProfileValidator.ValidateWindowMatcher(new WindowMatcher { ExecutableName = ExecutableName, ExecutablePath = ExecutablePath });
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
    public bool Matches(WindowIdentity identity)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(identity.ExecutablePath) ? identity.ProcessName : Path.GetFileName(identity.ExecutablePath);
            if (!string.Equals(ExecutableName, name, StringComparison.OrdinalIgnoreCase)) return false;
            return string.IsNullOrWhiteSpace(ExecutablePath) || string.IsNullOrWhiteSpace(identity.ExecutablePath) ||
                string.Equals(Path.GetFullPath(ExecutablePath), Path.GetFullPath(identity.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

/// <summary>Local app preference, kept separate from imported/exported profile documents.</summary>
public sealed class ShortcutOverlaySettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, WriteIndented = true };
    public ShortcutOverlaySettings Load()
    {
        if (!File.Exists(path)) return new ShortcutOverlaySettings();
        var settings = JsonSerializer.Deserialize<ShortcutOverlaySettings>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("오버레이 설정이 비어 있습니다.");
        settings.Validate(); return settings;
    }
    public void Save(ShortcutOverlaySettings settings)
    {
        settings.Validate();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
