using System.Threading.Channels;

namespace AviUtl2FAT.Core;

/// <summary>Stable speaker labels.  v1.1.5 deliberately keeps the default as
/// A until a real diarization backend is selected; it never invents speakers
/// from text alone.  The stored IDs are ready for future diarization/name maps.</summary>
public static class SpeakerIds
{
    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 32 ? "A" : trimmed;
    }

    public static IReadOnlyList<string> DefaultChoices { get; } = ["A", "B", "C", "D", "E", "F"];
}

public enum CaptionProcessingState { Pending, Processing, Completed, Failed, Cancelled }

/// <summary>A bounded work item used between recognition and caption shaping.
/// It holds metadata only; PCM/WAV buffers remain on disk in the recognition
/// runtime and are not copied into the queue.</summary>
public sealed record CaptionQueueItem(
    string Id, double StartTime, double EndTime, string RecognitionText,
    string SpeakerId, CaptionProcessingState State = CaptionProcessingState.Pending,
    string? Error = null,
    string OriginalText = "",
    double? Confidence = null,
    bool Enabled = true);

public sealed record CaptionPipelineReport(int Enqueued, int Completed, int Failed, bool Cancelled, int MaximumDepth);

public static class CaptionProcessingPipeline
{
    public static async Task<(IReadOnlyList<FATCaption> Captions, CaptionPipelineReport Report)> BuildAsync(
        IReadOnlyList<TranscriptSegment> transcript,
        Func<TranscriptSegment, CancellationToken, ValueTask<FATCaption>> formatter,
        CancellationToken cancellationToken,
        int capacity = 128)
    {
        if (capacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        var input = Channel.CreateBounded<CaptionQueueItem>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true });
        var completed = new List<FATCaption>(transcript.Count);
        var failed = 0;
        var maximumDepth = 0;
        var pending = 0;
        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var item in transcript)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var queued = new CaptionQueueItem(
                        item.Id, item.StartTime, item.EndTime, item.Text,
                        SpeakerIds.Normalize(item.SpeakerId),
                        CaptionProcessingState.Pending, null, item.OriginalText,
                        item.Confidence, item.Enabled);
                    await input.Writer.WriteAsync(queued, cancellationToken);
                    maximumDepth = Math.Max(maximumDepth, Interlocked.Increment(ref pending));
                }
                input.Writer.TryComplete();
            }
            catch (Exception error) { input.Writer.TryComplete(error); throw; }
        }, cancellationToken);
        try
        {
            await foreach (var item in input.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Decrement(ref pending);
                try
                {
                    var processing = item with { State = CaptionProcessingState.Processing };
                    var segment = new TranscriptSegment(processing.Id, processing.StartTime, processing.EndTime,
                        processing.OriginalText, processing.RecognitionText, processing.Confidence, processing.Enabled, processing.SpeakerId);
                    completed.Add((await formatter(segment, cancellationToken)).Validate());
                }
                catch (OperationCanceledException) { throw; }
                catch { failed++; }
            }
            await producer;
            completed.Sort((left, right) => left.StartTime != right.StartTime ? left.StartTime.CompareTo(right.StartTime) : left.EndTime.CompareTo(right.EndTime));
            return (completed, new CaptionPipelineReport(transcript.Count, completed.Count, failed, false, maximumDepth));
        }
        catch (OperationCanceledException)
        {
            input.Writer.TryComplete();
            try { await producer; }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            throw;
        }
    }
}
