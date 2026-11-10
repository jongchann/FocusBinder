using System.Text.Json;

namespace FocusBind.Core.Profiles;

public sealed class DiagnosticLoggingSettings
{
    public bool Enabled { get; set; }
}

public sealed class DiagnosticLoggingSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, WriteIndented = true };
    public DiagnosticLoggingSettings Load() => !File.Exists(path) ? new() :
        JsonSerializer.Deserialize<DiagnosticLoggingSettings>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("로그 설정이 비어 있습니다.");
    public void Save(DiagnosticLoggingSettings settings)
    {
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
