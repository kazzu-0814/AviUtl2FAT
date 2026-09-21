namespace AviUtl2FAT.Core;

/// <summary>Extension point for real audio diarization. Providers must never fabricate speakers.</summary>
public interface ISpeakerDiarizationProvider
{
    string Id { get; }
    bool IsAvailable(SpeakerDiarizationSettings settings);
    Task<IReadOnlyList<SpeakerTimelineEntry>> DiarizeAsync(string audioPath, SpeakerDiarizationSettings settings, CancellationToken cancellationToken);
}

public sealed class DisabledSpeakerDiarizationProvider : ISpeakerDiarizationProvider
{
    public const string ProviderId = "disabled";
    public string Id => ProviderId;
    public bool IsAvailable(SpeakerDiarizationSettings settings) => true;
    public Task<IReadOnlyList<SpeakerTimelineEntry>> DiarizeAsync(string audioPath, SpeakerDiarizationSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SpeakerTimelineEntry>>([]);
}

/// <summary>Marker for the isolated Python local provider. The app never downloads or loads a model itself.</summary>
public sealed class LocalSpeakerDiarizationProvider : ISpeakerDiarizationProvider
{
    public const string ProviderId = "local-python";
    public string Id => ProviderId;
    public bool IsAvailable(SpeakerDiarizationSettings settings) => settings.Normalize().Mode is "auto" or "local" && !string.IsNullOrWhiteSpace(settings.ModelPath) && File.Exists(settings.ModelPath);
    public Task<IReadOnlyList<SpeakerTimelineEntry>> DiarizeAsync(string audioPath, SpeakerDiarizationSettings settings, CancellationToken cancellationToken) =>
        throw new FatException("FAT_SPEAKER_PROVIDER_ISOLATED", "The local speaker provider runs in the isolated Python worker.");
}

/// <summary>Alignment is deterministic: maximum overlap wins, then the earliest timeline entry.</summary>
public static class SpeakerAlignment
{
    public static string Align(double startTime, double endTime, IEnumerable<SpeakerTimelineEntry>? timeline, string fallback = "A")
    {
        var start = Math.Max(0, startTime);
        var end = Math.Max(start, endTime);
        var bestOverlap = 0d;
        SpeakerTimelineEntry? best = null;
        foreach (var entry in timeline ?? [])
        {
            var valid = entry.Validate();
            var overlap = Math.Max(0, Math.Min(end, valid.EndTime) - Math.Max(start, valid.StartTime));
            if (overlap > bestOverlap || (overlap > 0 && Math.Abs(overlap - bestOverlap) < 0.0000001 && (best is null || valid.StartTime < best.StartTime)))
            {
                bestOverlap = overlap;
                best = valid;
            }
        }
        return best is null ? SpeakerIds.Normalize(fallback) : best.SpeakerId;
    }

    public static IReadOnlyList<TranscriptSegment> Apply(IEnumerable<TranscriptSegment> transcript, IEnumerable<SpeakerTimelineEntry>? timeline, string fallback = "A") =>
        transcript.Select(segment => segment with { SpeakerId = Align(segment.StartTime, segment.EndTime, timeline, fallback) }).ToArray();
}

/// <summary>Names and colors are presentation metadata only; object export always uses the source style.</summary>
public sealed record SpeakerProfile(string Id, string? DisplayName = null, string? DisplayColor = null)
{
    public SpeakerProfile Normalize() => this with { Id = SpeakerIds.Normalize(Id), DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(), DisplayColor = string.IsNullOrWhiteSpace(DisplayColor) ? null : DisplayColor.Trim() };
}

public sealed record SpeakerProjectMetadata(
    SpeakerDiarizationSettings? Diarization = null,
    IReadOnlyDictionary<string, SpeakerProfile>? Profiles = null)
{
    public static SpeakerProjectMetadata Default { get; } = new();
    public SpeakerProjectMetadata Normalize() => this with
    {
        Diarization = (Diarization ?? SpeakerDiarizationSettings.Disabled).Normalize(),
        Profiles = (Profiles ?? new Dictionary<string, SpeakerProfile>())
            .Values.Select(profile => profile.Normalize()).GroupBy(profile => profile.Id).ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase)
    };
}
