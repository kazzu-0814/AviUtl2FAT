using System.Text.Json;

namespace AviUtl2FAT.Core;

public sealed record SpeakerSettingsDocument(SpeakerDiarizationSettings? Diarization = null, IReadOnlyDictionary<string, SpeakerProfile>? Profiles = null)
{
    public static SpeakerSettingsDocument Default { get; } = new(SpeakerDiarizationSettings.Disabled, new Dictionary<string, SpeakerProfile>());
    public SpeakerSettingsDocument Normalize() => this with { Diarization = (Diarization ?? SpeakerDiarizationSettings.Disabled).Normalize(), Profiles = (Profiles ?? new Dictionary<string, SpeakerProfile>()).Values.Select(x => x.Normalize()).ToDictionary(x => x.Id, x => x, StringComparer.OrdinalIgnoreCase) };
}

public static class SpeakerSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static async Task<SpeakerSettingsDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return SpeakerSettingsDocument.Default;
            await using var stream = File.OpenRead(path);
            return (await JsonSerializer.DeserializeAsync<SpeakerSettingsDocument>(stream, Options, cancellationToken) ?? SpeakerSettingsDocument.Default).Normalize();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return SpeakerSettingsDocument.Default; }
    }
    public static Task SaveAsync(string path, SpeakerSettingsDocument settings, CancellationToken cancellationToken) =>
        FatFiles.WriteUtf8AtomicAsync(path, JsonSerializer.Serialize(settings.Normalize(), Options), cancellationToken);
}
