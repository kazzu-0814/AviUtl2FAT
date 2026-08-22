using System.Text.Json;

namespace AviUtl2FAT.Core;

/// <summary>FAT's editable work state.  This is deliberately separate from AviUtl2 .object output.</summary>
public sealed record FatDraft(
    string Format,
    int SchemaVersion,
    DateTimeOffset SavedAtUtc,
    string? MediaPath,
    double DurationSeconds,
    double FramesPerSecond,
    IReadOnlyList<FATCaption> Captions,
    AviUtl2TextStyle? Style = null,
    IReadOnlyList<IReadOnlyList<FATCaption>>? UndoSnapshots = null,
    IReadOnlyDictionary<string, string>? SplitMetadata = null)
{
    public const string FormatName = "AviUtl2 FAT Draft";
    public const int CurrentSchemaVersion = 1;

    public static FatDraft Create(string? mediaPath, double durationSeconds, double fps, IReadOnlyList<FATCaption> captions,
        AviUtl2TextStyle? style = null, IReadOnlyList<IReadOnlyList<FATCaption>>? undoSnapshots = null,
        IReadOnlyDictionary<string, string>? splitMetadata = null) =>
        new(FormatName, CurrentSchemaVersion, DateTimeOffset.UtcNow, mediaPath, Math.Max(0, durationSeconds), fps > 0 ? fps : 30,
            captions.Select(caption => caption.Validate()).ToArray(), style, undoSnapshots, splitMetadata);

    public FatDraft Validate()
    {
        if (!string.Equals(Format, FormatName, StringComparison.Ordinal) || SchemaVersion != CurrentSchemaVersion)
            throw new FatException("FAT_DRAFT_UNSUPPORTED", "この作業ファイルの形式またはバージョンには対応していません。");
        if (DurationSeconds < 0 || FramesPerSecond <= 0 || Captions is null)
            throw new FatException("FAT_DRAFT_INVALID", "作業ファイルの動画情報または字幕情報が不正です。");
        return this with { Captions = Captions.Select(caption => caption.Validate()).OrderBy(caption => caption.StartTime).ThenBy(caption => caption.EndTime).ToArray() };
    }
}

public static class FatDraftStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task SaveAsync(string path, FatDraft draft, CancellationToken cancellationToken)
    {
        if (!path.EndsWith(".fatdraft", StringComparison.OrdinalIgnoreCase))
            throw new FatException("FAT_DRAFT_EXTENSION_INVALID", "作業ファイルの拡張子は .fatdraft を使用してください。");
        draft.Validate();
        await FatFiles.WriteUtf8AtomicAsync(path, JsonSerializer.Serialize(draft, JsonOptions), cancellationToken);
    }

    public static async Task<FatDraft> LoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var draft = await JsonSerializer.DeserializeAsync<FatDraft>(stream, JsonOptions, cancellationToken);
            return (draft ?? throw new JsonException("Draft is empty.")).Validate();
        }
        catch (FatException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new FatException("FAT_DRAFT_INVALID", "作業ファイルを読み込めませんでした。破損していないか確認してください。", error);
        }
    }

    public static string AutosavePath(string directory) => Path.Combine(directory, "latest.fatdraft");
}
