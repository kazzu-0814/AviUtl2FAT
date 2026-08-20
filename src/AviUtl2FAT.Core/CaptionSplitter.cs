using System.Text.RegularExpressions;

namespace AviUtl2FAT.Core;

/// <summary>
/// Deterministic, offline subtitle splitting. This intentionally lives in Core:
/// splitting user-owned text must not depend on a Python process, an AI model,
/// or a network connection.
/// </summary>
public static class CaptionSplitter
{
    private static readonly Regex Boundaries = new(@"(?<=[。！？!?]|[,.])\s*|\s+", RegexOptions.Compiled);

    public static IReadOnlyList<FATCaption> Split(
        FATCaption caption,
        int maximumCharacters = 24,
        int maximumLines = 2,
        double minimumSeconds = 0.8)
    {
        var text = (caption.Text ?? string.Empty).Trim();
        var limit = Math.Max(4, maximumCharacters * Math.Max(1, maximumLines));
        if (text.Length <= limit) return [caption];

        var parts = new List<string>();
        var current = string.Empty;
        foreach (var token in Boundaries.Split(text).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var candidate = current + token;
            if (current.Length > 0 && candidate.Length > limit)
            {
                parts.Add(current);
                current = token;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0) parts.Add(current);

        if (parts.Count < 2)
        {
            parts = Enumerable.Range(0, (text.Length + limit - 1) / limit)
                .Select(index => text.Substring(index * limit, Math.Min(limit, text.Length - index * limit)))
                .ToList();
        }

        var duration = caption.EndTime - caption.StartTime;
        if (duration < minimumSeconds * parts.Count) return [caption];

        var weight = parts.Sum(value => Math.Max(1, value.Length));
        var start = caption.StartTime;
        var result = new List<FATCaption>(parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            var end = index == parts.Count - 1
                ? caption.EndTime
                : Math.Min(caption.EndTime, Math.Max(start + minimumSeconds, start + duration * Math.Max(1, parts[index].Length) / weight));
            result.Add(caption with { Id = $"{caption.Id}-{index + 1}", StartTime = start, EndTime = end, Text = parts[index] });
            start = end;
        }
        return result;
    }

    /// <summary>
    /// Makes the range selected in the editable subtitle text its own caption.
    /// This does not call Python, an AI provider, or a network service.
    /// </summary>
    public static IReadOnlyList<FATCaption> SplitSelection(
        FATCaption caption,
        int selectionStart,
        int selectionLength,
        double minimumSeconds = 0.8)
    {
        var text = caption.Text ?? string.Empty;
        if (selectionStart < 0 || selectionLength <= 0 || selectionStart >= text.Length) return [caption];
        var length = Math.Min(selectionLength, text.Length - selectionStart);
        var parts = new[]
        {
            text[..selectionStart].Trim(),
            text.Substring(selectionStart, length).Trim(),
            text[(selectionStart + length)..].Trim()
        }.Where(value => value.Length > 0).ToList();
        if (parts.Count < 2) return [caption];

        var duration = caption.EndTime - caption.StartTime;
        if (duration < minimumSeconds * parts.Count) return [caption];
        var total = parts.Sum(value => Math.Max(1, value.Length));
        var start = caption.StartTime;
        var result = new List<FATCaption>(parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            var end = index == parts.Count - 1
                ? caption.EndTime
                : Math.Min(caption.EndTime, Math.Max(start + minimumSeconds, start + duration * Math.Max(1, parts[index].Length) / total));
            result.Add(caption with { Id = $"{caption.Id}-{index + 1}", StartTime = start, EndTime = end, Text = parts[index] });
            start = end;
        }
        return result;
    }
}
