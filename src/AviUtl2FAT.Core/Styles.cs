using System.Text.Json;

namespace AviUtl2FAT.Core;

public sealed class FatStyleStore(string directory)
{
    public static FatStyleStore Default { get; } = new(Path.Combine(AppContext.BaseDirectory, "config", "styles"));
    public async Task SaveAsync(string name, AviUtl2TextStyle style, CancellationToken cancellationToken)
    {
        var safe = string.Concat(name.Where(char.IsLetterOrDigit));
        if (string.IsNullOrWhiteSpace(safe)) throw new FatException("STYLE_NAME_INVALID", "Style name is required.");
        await FatFiles.WriteUtf8AtomicAsync(Path.Combine(directory, safe + ".json"), JsonSerializer.Serialize(style, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }
    public async Task<AviUtl2TextStyle?> LoadAsync(string name, CancellationToken cancellationToken)
    {
        var safe = string.Concat(name.Where(char.IsLetterOrDigit)); var path = Path.Combine(directory, safe + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<AviUtl2TextStyle>(await File.ReadAllTextAsync(path, cancellationToken)); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new FatException("STYLE_FILE_INVALID", "保存済みスタイルを読み込めませんでした。別のスタイルを選ぶか、スタイルを再登録してください。", error);
        }
    }
}
