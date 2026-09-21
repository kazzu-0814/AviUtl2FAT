using AviUtl2FAT.Core;
using Xunit;

namespace AviUtl2FAT.Core.Tests;

public sealed class SpeakerDiarizationTests
{
    [Fact]
    public void Alignment_uses_maximum_audio_overlap_and_safe_fallback()
    {
        var timeline = new[] { new SpeakerTimelineEntry(0, 2, "a"), new SpeakerTimelineEntry(2, 5, "b") };
        Assert.Equal("B", SpeakerAlignment.Align(1.9, 4, timeline));
        Assert.Equal("A", SpeakerAlignment.Align(8, 9, timeline));
    }

    [Fact]
    public void Draft_metadata_is_backward_compatible_and_normalized()
    {
        var draft = FatDraft.Create(null, 0, 30, [], speakerMetadata: new SpeakerProjectMetadata(new SpeakerDiarizationSettings { Mode = "LOCAL", ExpectedSpeakers = 99 }, new Dictionary<string, SpeakerProfile> { ["a"] = new("a", "Narrator") }));
        var valid = draft.Validate();
        Assert.Equal("local", valid.SpeakerMetadata!.Diarization!.Mode);
        Assert.Equal(6, valid.SpeakerMetadata.Diarization.ExpectedSpeakers);
        Assert.Equal("Narrator", valid.SpeakerMetadata.Profiles!["A"].DisplayName);
    }
}
