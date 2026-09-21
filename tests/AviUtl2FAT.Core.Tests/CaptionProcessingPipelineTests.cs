using AviUtl2FAT.Core;
using Xunit;

namespace AviUtl2FAT.Core.Tests;

public sealed class CaptionProcessingPipelineTests
{
    [Fact]
    public async Task Bounded_pipeline_preserves_speaker_metadata_and_order()
    {
        var source = new[]
        {
            new TranscriptSegment("2", 2, 3, "original-2", "text-2", SpeakerId: "b"),
            new TranscriptSegment("1", 0, 1, "original-1", "text-1", SpeakerId: "A")
        };

        var (captions, report) = await CaptionProcessingPipeline.BuildAsync(source,
            (segment, _) => ValueTask.FromResult(new FATCaption(segment.Id, segment.StartTime, segment.EndTime,
                segment.OriginalText, segment.Text, SpeakerId: segment.SpeakerId)), CancellationToken.None, capacity: 1);

        Assert.Equal(new[] { "1", "2" }, captions.Select(caption => caption.Id));
        Assert.Equal(new[] { "A", "B" }, captions.Select(caption => caption.SpeakerId));
        Assert.Equal(2, report.Completed);
        Assert.Equal(0, report.Failed);
        // The metric includes a producer already waiting to enqueue, so it is
        // backlog rather than only the channel's physical one-item capacity.
        Assert.InRange(report.MaximumDepth, 1, source.Length);
    }

    [Fact]
    public async Task Pipeline_records_failed_item_and_continues()
    {
        var source = new[]
        {
            new TranscriptSegment("ok", 0, 1, "o", "ok"),
            new TranscriptSegment("bad", 1, 2, "b", "bad"),
            new TranscriptSegment("later", 2, 3, "l", "later")
        };

        var (captions, report) = await CaptionProcessingPipeline.BuildAsync(source, (segment, _) =>
        {
            if (segment.Id == "bad") throw new InvalidOperationException("expected test failure");
            return ValueTask.FromResult(new FATCaption(segment.Id, segment.StartTime, segment.EndTime,
                segment.OriginalText, segment.Text, SpeakerId: segment.SpeakerId));
        }, CancellationToken.None);

        Assert.Equal(new[] { "ok", "later" }, captions.Select(caption => caption.Id));
        Assert.Equal(1, report.Failed);
        Assert.Equal(2, report.Completed);
    }

    [Fact]
    public async Task Pipeline_cancellation_stops_without_returning_partial_success()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new[] { new TranscriptSegment("one", 0, 1, "original", "text") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CaptionProcessingPipeline.BuildAsync(source,
                (segment, _) => ValueTask.FromResult(new FATCaption(segment.Id, segment.StartTime, segment.EndTime,
                    segment.OriginalText, segment.Text)), cancellation.Token));
    }
}
