namespace AviUtl2FAT.Core;

public enum RecognitionProfile { Auto, Low, Standard, High, Detailed }
public enum ProcessingStage { Ready, Probing, Preprocessing, Transcribing, PostProcessing, Completed, Cancelled, Failed }

public sealed record SpeechProfileDefinition(RecognitionProfile Profile, string Model, string Device, string ComputeType, int BeamSize, bool VadEnabled);

public static class SpeechProfiles
{
    public static SpeechProfileDefinition Select(RecognitionProfile preference, int logicalCores, double availableRamGb, bool cudaAvailable)
    {
        if (preference == RecognitionProfile.High && (cudaAvailable || availableRamGb >= 24)) return new(RecognitionProfile.High, "medium", cudaAvailable ? "cuda" : "cpu", cudaAvailable ? "float16" : "int8", 8, true);
        if (preference == RecognitionProfile.Standard || (preference == RecognitionProfile.Auto && (cudaAvailable || (logicalCores >= 6 && availableRamGb >= 10)))) return new(RecognitionProfile.Standard, "small", cudaAvailable ? "cuda" : "cpu", cudaAvailable ? "float16" : "int8", 5, true);
        return new(RecognitionProfile.Low, "small", "cpu", "int8", 2, true);
    }
}

/// <summary>Speech recognition is intentionally separate from caption-generation providers.</summary>
public interface ISpeechRecognitionProvider
{
    string Id { get; }
    IReadOnlyList<string> SupportedModels { get; }
    SpeechProfileDefinition SelectProfile(RecognitionProfile preference, int logicalCores, double availableRamGb, bool cudaAvailable);
}

public sealed class FasterWhisperProvider : ISpeechRecognitionProvider
{
    // Names accepted by faster-whisper / Whisper model loading. They remain technical details in the beginner UI.
    public string Id => "faster-whisper";
    public IReadOnlyList<string> SupportedModels { get; } = ["tiny", "base", "small", "medium", "large-v3"];
    public SpeechProfileDefinition SelectProfile(RecognitionProfile preference, int logicalCores, double availableRamGb, bool cudaAvailable) =>
        SpeechProfiles.Select(preference == RecognitionProfile.Detailed ? RecognitionProfile.Standard : preference, logicalCores, availableRamGb, cudaAvailable);
}

public static class CaptionQuality
{
    public static string? Warning(FATCaption caption, int lineWarningLength = 42) =>
        caption.Confidence is < 0.45 ? "認識精度が低い可能性があります" :
        caption.Text.Length > lineWarningLength ? $"1行{caption.Text.Length}文字です。分割を検討してください" : null;
}

public sealed record FatUiState(ProcessingStage Stage = ProcessingStage.Ready, string? InputPath = null, int CaptionCount = 0)
{
    public int CurrentStep => CaptionCount > 0 ? 4 : InputPath is not null ? 2 : 1;
    public bool CanRecognize => InputPath is not null && Stage is not ProcessingStage.Transcribing and not ProcessingStage.Preprocessing;
}
