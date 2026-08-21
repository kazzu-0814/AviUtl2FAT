using System.Text.Json;

namespace AviUtl2FAT.Core;

/// <summary>Actions that can be bound to a keyboard shortcut in FAT.</summary>
public enum ShortcutAction
{
    TogglePreviewPlayback,
    PreviousCaption,
    NextCaption,
    SplitCaption,
    MergeCaptions,
    Undo,
    CancelRecognition
}

public sealed record ShortcutBinding(ShortcutAction Action, string Key, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    public static ShortcutBinding DefaultTogglePlayback { get; } = new(ShortcutAction.TogglePreviewPlayback, "Space", Ctrl: true);

    public string DisplayText
    {
        get
        {
            var parts = new List<string>(4);
            if (Ctrl) parts.Add("Ctrl");
            if (Shift) parts.Add("Shift");
            if (Alt) parts.Add("Alt");
            parts.Add(Key == "Space" ? "Space" : Key);
            return string.Join(" + ", parts);
        }
    }

    public bool IsValid()
        => !string.IsNullOrWhiteSpace(Key) && Key.Length <= 32 && Key.All(character => char.IsLetterOrDigit(character) || character is '_' or ' ');
}

public sealed record ShortcutSettings(IReadOnlyList<ShortcutBinding>? Bindings = null)
{
    public static ShortcutSettings Default { get; } = new([ShortcutBinding.DefaultTogglePlayback]);

    public ShortcutBinding Get(ShortcutAction action)
        => Bindings?.FirstOrDefault(binding => binding.Action == action && binding.IsValid())
           ?? (action == ShortcutAction.TogglePreviewPlayback ? ShortcutBinding.DefaultTogglePlayback : new ShortcutBinding(action, ""));
}

/// <summary>
/// JSON persistence for non-sensitive keyboard preferences.  Broken settings are
/// deliberately treated as defaults so a keyboard configuration cannot stop FAT
/// from starting.
/// </summary>
public static class ShortcutSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static async Task<ShortcutSettings> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return ShortcutSettings.Default;
        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<ShortcutSettings>(stream, JsonOptions, cancellationToken);
            var toggle = settings?.Get(ShortcutAction.TogglePreviewPlayback);
            return settings is null || toggle is null || !toggle.IsValid() ? ShortcutSettings.Default : settings;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return ShortcutSettings.Default;
        }
    }

    public static async Task SaveAsync(string path, ShortcutSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Shortcut settings directory is required."));
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        File.Move(temporaryPath, path, true);
    }
}

/// <summary>Pure matching logic so the WPF event handler remains small and testable.</summary>
public sealed class ShortcutManager
{
    private readonly ShortcutSettings _settings;

    public ShortcutManager(ShortcutSettings? settings = null) => _settings = settings ?? ShortcutSettings.Default;

    public bool Matches(ShortcutAction action, string key, bool ctrl, bool shift, bool alt)
    {
        var binding = _settings.Get(action);
        return binding.IsValid()
            && string.Equals(binding.Key, key, StringComparison.OrdinalIgnoreCase)
            && binding.Ctrl == ctrl
            && binding.Shift == shift
            && binding.Alt == alt;
    }
}
