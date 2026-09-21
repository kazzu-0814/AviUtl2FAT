namespace AviUtl2FAT.Core;

/// <summary>Common contract for explicitly installed local LLM runtimes.</summary>
public interface ILocalLlmProvider
{
    string Id { get; }
    bool IsAvailable(LocalAiModel model);
    Task<LocalAiInferenceResult> GenerateAsync(LocalAiInferenceRequest request, CancellationToken cancellationToken);
}

public sealed record LocalAiModel(string Id, string DisplayName, string Provider, string Architecture, long RecommendedRamBytes, long RecommendedVramBytes, bool SupportsCpu, bool SupportsGpu, bool IsGated = false);
public sealed record LocalAiHardware(int LogicalProcessors, long? RamAvailableBytes, string? GpuName, long? VramBytes, bool CudaAvailable);
public sealed record LocalAiInferenceRequest(string ModelId, string Prompt, int MaxOutputTokens = 160, string Device = "auto");
public sealed record LocalAiInferenceResult(string Text, int? InputTokens = null, int? OutputTokens = null, double? TokensPerSecond = null, string? Detail = null);

public static class LocalAiRecommendations
{
    public static string Recommend(LocalAiModel model, LocalAiHardware hardware)
    {
        if (hardware.CudaAvailable && hardware.VramBytes >= model.RecommendedVramBytes && model.SupportsGpu) return "recommended";
        if (hardware.RamAvailableBytes >= model.RecommendedRamBytes && model.SupportsCpu) return "possible";
        return model.SupportsCpu ? "not-recommended" : "unsupported";
    }
}
