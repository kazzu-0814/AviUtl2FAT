using AviUtl2FAT.Core;
using Xunit;

namespace AviUtl2FAT.Core.Tests;

public sealed class LocalAiEngineTests
{
    [Fact]
    public void Recommendation_does_not_claim_gpu_model_is_usable_without_vram()
    {
        var model = new LocalAiModel("elyza", "ELYZA", "ELYZA", "Llama", 16_000_000_000, 8_000_000_000, true, true);
        Assert.Equal("not-recommended", LocalAiRecommendations.Recommend(model, new LocalAiHardware(8, 4_000_000_000, null, null, false)));
        Assert.Equal("recommended", LocalAiRecommendations.Recommend(model, new LocalAiHardware(8, 32_000_000_000, "RTX", 8_000_000_000, true)));
    }
}
