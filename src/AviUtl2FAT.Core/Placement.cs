namespace AviUtl2FAT.Core;

public interface IPlacementAdapter
{
    string Id { get; }
    Task PlaceAsync(string path, IReadOnlyList<FATCaption> captions, double fps, CancellationToken cancellationToken);
}

public sealed class JsonPlacementAdapter : IPlacementAdapter
{
    public string Id => "json";
    public Task PlaceAsync(string path, IReadOnlyList<FATCaption> captions, double fps, CancellationToken cancellationToken) => FatFiles.WritePlacementAsync(path, captions, fps, cancellationToken);
}

// Reserved for the formal AviUtl2 text-object API. It intentionally cannot create host objects in v0.7.1.
public sealed class AviUtl2TextObjectAdapter : IPlacementAdapter
{
    public string Id => "aviutl2-text";
    public Task PlaceAsync(string path, IReadOnlyList<FATCaption> captions, double fps, CancellationToken cancellationToken) => throw new FatException("FAT_PLACEMENT_API_UNAVAILABLE", "The AviUtl2 text-object creation API is not available in this FAT build.");
}

public enum PlacementMode { Native, PlacementJson, Srt, Txt, Json }
public sealed record PlacementRequest(IReadOnlyList<FATCaption> Captions, double Fps, int Layer = 5);
public sealed record PlacementResult(bool Success, int Requested, int Placed, int Skipped, IReadOnlyList<string> FailedCaptionIds);

public sealed class FakeAviUtl2PlacementAdapter : IPlacementAdapter
{
    public string Id => "fake-native";
    public List<PlacementItem> Placed { get; } = [];
    public Task PlaceAsync(string path, IReadOnlyList<FATCaption> captions, double fps, CancellationToken cancellationToken)
    {
        Placed.AddRange(captions.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Text)).Select(x => new PlacementItem(x.Id, x.Text, x.StartTime, x.EndTime, FatFiles.Frame(x.StartTime, fps), FatFiles.Frame(x.EndTime, fps))));
        return Task.CompletedTask;
    }
}

public interface ICaptionExporter { string Id { get; } Task ExportAsync(string path, IReadOnlyList<FATCaption> captions, CancellationToken cancellationToken); }
public sealed class TxtCaptionExporter : ICaptionExporter
{
    public string Id => "txt";
    public Task ExportAsync(string path, IReadOnlyList<FATCaption> captions, CancellationToken token) =>
        FatFiles.WriteUtf8AtomicAsync(path, string.Join("\r\n", captions.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Text)).Select(x => x.Text)) + "\r\n", token);
}
public sealed class SrtCaptionExporter : ICaptionExporter
{
    public string Id => "srt";
    public Task ExportAsync(string path, IReadOnlyList<FATCaption> captions, CancellationToken token) => FatFiles.WriteUtf8AtomicAsync(path, string.Join("\r\n\r\n", captions.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Text)).Select((x, i) => $"{i + 1}\r\n{Time(x.StartTime)} --> {Time(x.EndTime)}\r\n{x.Text}")) + "\r\n", token);
    private static string Time(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\,fff");
}
