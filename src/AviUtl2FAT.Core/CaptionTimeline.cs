namespace AviUtl2FAT.Core;

/// <summary>
/// Read-only caption timing index for preview synchronization.
/// FATCaption.StartTime / EndTime remain the single source of truth.
/// </summary>
public sealed class CaptionTimeline
{
    private readonly FATCaption[] _captions;

    public CaptionTimeline(IEnumerable<FATCaption> captions)
    {
        _captions = captions
            .Where(caption => caption.Enabled && caption.EndTime > caption.StartTime)
            .OrderBy(caption => caption.StartTime)
            .ThenBy(caption => caption.EndTime)
            .ToArray();
    }

    public FATCaption? FindCaptionAtTime(double seconds)
    {
        if (_captions.Length == 0 || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return null;

        var low = 0;
        var high = _captions.Length - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_captions[middle].StartTime <= seconds)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate < 0) return null;
        var caption = _captions[candidate];
        return seconds < caption.EndTime ? caption : null;
    }

    public int FindCaptionIndexAtTime(double seconds)
    {
        var caption = FindCaptionAtTime(seconds);
        if (caption is null) return -1;
        for (var index = 0; index < _captions.Length; index++)
            if (string.Equals(_captions[index].Id, caption.Id, StringComparison.Ordinal))
                return index;
        return -1;
    }
}

public enum PreviewPlayerStatus { NotLoaded, Opening, Stopped, Playing, Paused, Error }

public sealed record PreviewSettings(
    bool AutoFollowCaptions = true,
    bool ShowSubtitleOverlay = true,
    double Volume = 0.7);
