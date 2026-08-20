namespace AviUtl2FAT.Core;

public interface IAIProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool IsAvailable { get; }
    Task<CaptionGenerationResponse> GenerateCaptionsAsync(CaptionGenerationRequest request, CancellationToken cancellationToken);
}

public sealed class PassthroughProvider : IAIProvider
{
    public const string ProviderId = "passthrough";
    public string Id => ProviderId;
    public string DisplayName => "Passthrough (no AI)";
    public bool IsAvailable => true;
    public Task<CaptionGenerationResponse> GenerateCaptionsAsync(CaptionGenerationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var captions = request.Transcript.Select(x => new FATCaption(x.Id, x.StartTime, x.EndTime, x.OriginalText, x.Text, Id, null, x.Confidence, x.Enabled).Validate()).ToArray();
        return Task.FromResult(new CaptionGenerationResponse(captions, Id));
    }
}

public sealed class UnavailableProvider(string id, string displayName) : IAIProvider
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public bool IsAvailable => false;
    public Task<CaptionGenerationResponse> GenerateCaptionsAsync(CaptionGenerationRequest request, CancellationToken cancellationToken) => throw new FatException("FAT_PROVIDER_UNAVAILABLE", $"{DisplayName} is not configured in FAT v0.7. Select Passthrough.");
}

public sealed class ProviderRegistry(IEnumerable<IAIProvider> providers)
{
    private readonly Dictionary<string, IAIProvider> _providers = providers.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    public static ProviderRegistry CreateDefault() => new([new PassthroughProvider(), new UnavailableProvider("gemma", "Gemma"), new UnavailableProvider("llama", "Llama"), new UnavailableProvider("openai", "OpenAI")]);
    public IAIProvider GetRequired(string providerId) => _providers.TryGetValue(providerId, out var provider) ? provider : throw new FatException("FAT_PROVIDER_NOT_FOUND", $"AI Provider '{providerId}' is not registered.");
    public IReadOnlyList<IAIProvider> All => _providers.Values.OrderBy(x => x.Id).ToArray();
}
