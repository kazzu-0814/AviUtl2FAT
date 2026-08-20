using System.Text.Json.Serialization;

namespace AviUtl2FAT.Core;

public sealed record FATCaption(
    string Id,
    double StartTime,
    double EndTime,
    string OriginalTranscript,
    string Text,
    string Provider = "passthrough",
    string? Model = null,
    double? Confidence = null,
    bool Enabled = true,
    string? DetectedLanguage = null,
    string? OutputLanguage = null)
{
    public FATCaption Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new FatException("FAT_CAPTION_INVALID", "Caption ID is required.");
        if (StartTime < 0 || EndTime < StartTime) throw new FatException("FAT_CAPTION_INVALID", "Caption timing is invalid.");
        return this with { OriginalTranscript = OriginalTranscript ?? string.Empty, Text = Text ?? string.Empty };
    }
}

public sealed record TranscriptSegment(string Id, double StartTime, double EndTime, string OriginalText, string Text, double? Confidence = null, bool Enabled = true);
public sealed record CaptionGenerationRequest(IReadOnlyList<TranscriptSegment> Transcript, string Style = "verbatim", string? Model = null);
public sealed record CaptionGenerationResponse(IReadOnlyList<FATCaption> Captions, string Provider, string? Model = null);
public sealed record FatError(string Code, string Message, string? Detail = null);
public sealed class FatException(string code, string message, Exception? inner = null) : Exception(message, inner) { public string Code { get; } = code; }

public sealed record FatSettings
{
    public string Version { get; init; } = "1.0.0";
    public string ProviderId { get; init; } = "passthrough";
    public string CaptionStyle { get; init; } = "verbatim";
    public string Language { get; init; } = "ja";
    public string UILanguage { get; init; } = "ja-JP";
    public string RecognitionLanguage { get; init; } = "auto";
    public string CaptionOutputLanguage { get; init; } = "ja";
    // "auto" lets the Python profile selector make a capability-based choice.
    // A fixed "small" here bypassed the low-spec profile entirely.
    public string RecognitionModel { get; init; } = "auto";
    public string Device { get; init; } = "auto";
    public string ComputeType { get; init; } = "auto";
    public string SpeechProfile { get; init; } = "auto";
    public string AudioEnhancement { get; init; } = "auto";
    public string FillerMode { get; init; } = "auto";
    public string CpuLoad { get; init; } = "auto";
    public IReadOnlyList<string> RecognitionDictionary { get; init; } = [];
    public double Fps { get; init; } = 30;
    public string? PythonPath { get; init; }
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public string? ModelDirectory { get; init; }
    public bool ConfirmBeforeCloudSend { get; init; } = true;
}

public sealed record FatProgress(string Stage, double? Value, string Message, int? CaptionsCreated = null);
public static class FatProtocols
{
    public const string AppProtocol = "fat-app-ipc";
    public const string PythonProtocol = "fat-python";
    public const int Version = 1;
    public static void Validate(string protocol, int version, string expected)
    {
        if (!string.Equals(protocol, expected, StringComparison.Ordinal) || version != Version)
            throw new FatException("FAT_IPC_VERSION_UNSUPPORTED", $"Unsupported IPC protocol '{protocol}' v{version}.");
    }
}

public sealed record FatIpcMessage(
    string Type,
    [property: JsonPropertyName("request_id")] string? RequestId = null,
    object? Payload = null,
    FatError? Error = null,
    string Protocol = FatProtocols.AppProtocol,
    int Version = FatProtocols.Version);
public sealed record RecognitionRequest(string InputPath, string OutputPath, FatSettings Settings, string ProviderId = "passthrough", string? Model = null);
public sealed record PlacementItem(string CaptionId, string Text, double StartTime, double EndTime, int StartFrame, int EndFrame, int Layer = 1);
public sealed record PlacementDocument(string Format, string Version, double Fps, IReadOnlyList<PlacementItem> Items);
