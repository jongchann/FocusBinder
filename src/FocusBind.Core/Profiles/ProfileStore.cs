using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusBind.Core.Profiles;

public sealed class ProfileValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class ProfileStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public async Task<ProfileDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path)) return new ProfileDocument();
        await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            throw new ProfileValidationException(["schemaVersion is required and must be an integer."]);
        if (number != ProfileDocument.CurrentSchemaVersion)
            throw new ProfileValidationException([$"Unsupported schemaVersion: {number}."]);
        var document = json.RootElement.Deserialize<ProfileDocument>(Options);
        var errors = ProfileValidator.Validate(document);
        if (errors.Count > 0) throw new ProfileValidationException(errors);
        return document!;
    }

    public async Task SaveAsync(ProfileDocument document, CancellationToken cancellationToken = default)
    {
        var errors = ProfileValidator.Validate(document);
        if (errors.Count > 0) throw new ProfileValidationException(errors);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path)) File.Replace(temporary, Path, null);
            else File.Move(temporary, Path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
