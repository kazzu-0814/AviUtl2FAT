using System.Text.Json;

namespace AviUtl2FAT.Core;

public static class FatFiles
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static async Task<IReadOnlyList<TranscriptSegment>> ReadAttTranscriptAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("segments", out var source) || source.ValueKind != JsonValueKind.Array)
                throw new FatException("FAT_TRANSCRIPT_INVALID", "音声認識結果に字幕セグメントがありません。もう一度認識を実行してください。");
            var segments = source.EnumerateArray().Select((item, index) => new TranscriptSegment(
                item.TryGetProperty("id", out var id) ? id.ToString() : (index + 1).ToString(),
                item.GetProperty("start").GetDouble(), item.GetProperty("end").GetDouble(),
                item.TryGetProperty("original_text", out var original) ? original.GetString() ?? string.Empty : item.GetProperty("text").GetString() ?? string.Empty,
                item.GetProperty("text").GetString() ?? string.Empty,
                item.TryGetProperty("confidence", out var confidence) && confidence.ValueKind == JsonValueKind.Number ? confidence.GetDouble() : null,
                !item.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean())).ToArray();
            return segments;
        }
        catch (FatException) { throw; }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new FatException("FAT_TRANSCRIPT_INVALID", "音声認識結果を読み込めませんでした。認識をもう一度実行してください。", error);
        }
    }
    public static async Task WriteCaptionsAsync(string path, IReadOnlyList<FATCaption> captions, CancellationToken cancellationToken)
    {
        await WriteUtf8AtomicAsync(path, JsonSerializer.Serialize(new { format = "AviUtl2 FAT", version = "1.0.0", captions }, JsonOptions), cancellationToken);
    }
    public static async Task WritePlacementAsync(string path, IReadOnlyList<FATCaption> captions, double fps, CancellationToken cancellationToken)
    {
        var items = captions.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Text)).Select(x => new PlacementItem(x.Id, x.Text, x.StartTime, x.EndTime, Frame(x.StartTime, fps), Frame(x.EndTime, fps))).ToArray();
        await WriteUtf8AtomicAsync(path, JsonSerializer.Serialize(new PlacementDocument("AviUtl2 FAT placement", "1.0.0", fps, items), JsonOptions), cancellationToken);
    }
    public static int Frame(double seconds, double fps) => checked((int)Math.Round(seconds * fps, MidpointRounding.AwayFromZero));

    public static async Task WriteUtf8AtomicAsync(string path, string contents, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new FatException("FAT_OUTPUT_PATH_INVALID", "出力先が指定されていません。");
        var fullPath = Path.GetFullPath(path); var directory = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(directory);
        var temporary = fullPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, contents, new System.Text.UTF8Encoding(false), cancellationToken);
            File.Move(temporary, fullPath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new FatException("FAT_OUTPUT_WRITE_FAILED", "ファイルを書き出せませんでした。保存先の権限と使用中のファイルを確認してください。", error);
        }
    }
}
