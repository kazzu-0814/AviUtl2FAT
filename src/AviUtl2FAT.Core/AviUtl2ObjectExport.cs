using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AviUtl2FAT.Core;

public sealed record AviUtl2TextStyle(
    string Font, string Size, string TextColor, string OutlineColor, string Decoration,
    string Alignment, int Bold, int Italic, string X, string Y, string Scale, string Opacity)
{
    // Values are taken from the confirmed AviUtl2 .object sample, not inferred.
    public static AviUtl2TextStyle Default { get; } = new("LINE Seed JP ExtraBold", "111.35", "000000", "ffffff", "縁取り文字(角)", "左寄せ[上]", 1, 0, "-891.50", "362.72", "100.000", "0.00");
}

/// <summary>Validated raw AviUtl2 .object text template. Unknown fields remain raw and untouched.</summary>
public sealed record AviUtl2ObjectTemplate(string RawText, bool HasUtf8Bom, string SourcePath, string Sha256, int TextLineIndex)
{
    public string RenderText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FatException("OBJECT_TEXT_INVALID", "Object text must not be empty.");
        if (text.IndexOfAny(['\r', '\n']) >= 0) throw new FatException("OBJECT_TEXT_NEWLINE_UNSUPPORTED", "Multiline .object text is not enabled until its AviUtl2 serialization is verified.");
        var lines = SplitLines(RawText);
        var line = lines[TextLineIndex];
        var equals = line.IndexOf('=');
        // SplitLines deliberately retains the original newline.  Replace only the value
        // portion, then put that newline back so the following property remains a
        // separate property rather than becoming part of テキスト.
        lines[TextLineIndex] = line[..(equals + 1)] + text + GetLineEnding(line);
        return string.Concat(lines);
    }

    public string RenderCaption(FATCaption caption, double fps, AviUtl2TextStyle style, int? startFrameOverride = null, int? endFrameOverride = null)
    {
        var rendered = RenderText(caption.Text);
        var lines = SplitLines(rendered);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["frame"] = $"{startFrameOverride ?? FatFiles.Frame(caption.StartTime, fps)},{endFrameOverride ?? FatFiles.Frame(caption.EndTime, fps)}",
            ["サイズ"] = style.Size, ["フォント"] = style.Font, ["文字色"] = style.TextColor,
            ["影・縁色"] = style.OutlineColor, ["文字装飾"] = style.Decoration, ["文字揃え"] = style.Alignment,
            ["B"] = style.Bold.ToString(), ["I"] = style.Italic.ToString(), ["X"] = style.X, ["Y"] = style.Y,
            ["拡大率"] = style.Scale, ["透明度"] = style.Opacity
        };
        for (var index = 0; index < lines.Count; index++)
        {
            var ending = GetLineEnding(lines[index]);
            var line = lines[index].TrimEnd('\r', '\n'); var equals = line.IndexOf('=');
            if (equals <= 0 || !values.TryGetValue(line[..equals], out var value)) continue;
            lines[index] = line[..(equals + 1)] + value + ending;
        }
        return string.Concat(lines);
    }

    /// <summary>
    /// Converts the verified single-object serialization into the verified
    /// multi-object serialization recorded by AviUtl2 itself:
    /// [0]/[0.0]/[0.1], [1]/[1.0]/[1.1], ... .
    /// </summary>
    public string RenderMultiObjectCaption(FATCaption caption, double fps, AviUtl2TextStyle style, int objectIndex, int layer, int? startFrameOverride = null, int? endFrameOverride = null)
    {
        if (objectIndex < 0) throw new FatException("OBJECT_MULTI_INDEX_INVALID", "The multi-object index must not be negative.");
        if (layer < 1) throw new FatException("OBJECT_MULTI_LAYER_INVALID", "AviUtl2 layer must be at least 1.");
        var lines = SplitLines(RenderCaption(caption, fps, style, startFrameOverride, endFrameOverride));
        if (lines.Count < 4 || TrimSection(lines[0]) != "[Object]")
            throw new FatException("OBJECT_MULTI_TEMPLATE_INVALID", "The template is not a verified single AviUtl2 object.");

        var result = new List<string>(lines.Count + 1);
        foreach (var source in lines)
        {
            var section = TrimSection(source);
            if (section == "[Object]")
            {
                result.Add($"[{objectIndex}]" + GetLineEnding(source));
                // layer= is confirmed by the two-object AviUtl2 capture.
                result.Add($"layer={layer}" + GetLineEnding(source));
                continue;
            }
            if (section.StartsWith("[Object.", StringComparison.Ordinal) && section.EndsWith(']'))
            {
                var suffix = section["[Object.".Length..^1];
                if (!int.TryParse(suffix, out _)) throw new FatException("OBJECT_MULTI_TEMPLATE_INVALID", "The template has an unverified effect section name.");
                result.Add($"[{objectIndex}.{suffix}]" + GetLineEnding(source));
                continue;
            }
            result.Add(source);
        }
        return string.Concat(result);
    }

    public AviUtl2TextStyle ReadKnownStyle()
    {
        var values = SplitLines(RawText).Select(line => line.TrimEnd('\r', '\n')).Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        var fallback = AviUtl2TextStyle.Default;
        string Get(string key, string value) => values.TryGetValue(key, out var result) ? result : value;
        int Number(string key, int value) => int.TryParse(Get(key, value.ToString()), out var result) ? result : value;
        return new AviUtl2TextStyle(Get("フォント", fallback.Font), Get("サイズ", fallback.Size), Get("文字色", fallback.TextColor), Get("影・縁色", fallback.OutlineColor), Get("文字装飾", fallback.Decoration), Get("文字揃え", fallback.Alignment), Number("B", fallback.Bold), Number("I", fallback.Italic), Get("X", fallback.X), Get("Y", fallback.Y), Get("拡大率", fallback.Scale), Get("透明度", fallback.Opacity));
    }

    public static AviUtl2ObjectTemplate CreateStandard(AviUtl2TextStyle? style = null)
    {
        var value = style ?? AviUtl2TextStyle.Default;
        var raw = $"[Object]\r\nframe=0,80\r\n[Object.0]\r\neffect.name=テキスト\r\nサイズ={value.Size}\r\n字間=0.00\r\n行間=0.00\r\n表示速度=0.00\r\nフォント={value.Font}\r\n文字色={value.TextColor}\r\n影・縁色={value.OutlineColor}\r\n文字装飾={value.Decoration}\r\n文字揃え={value.Alignment}\r\nB={value.Bold}\r\nI={value.Italic}\r\nテキスト=FAT\r\n文字毎に個別オブジェクト=0\r\n自動スクロール=0\r\n移動座標上に表示=0\r\nオブジェクトの長さを自動調節=0\r\n[Object.1]\r\neffect.name=標準描画\r\nX={value.X}\r\nY={value.Y}\r\nZ=0.00\r\nGroup=1\r\n中心X=0.00\r\n中心Y=0.00\r\n中心Z=0.00\r\nGroup3=1\r\nX軸回転=0.00\r\nY軸回転=0.00\r\nZ軸回転=0.00\r\nGroup2=1\r\n拡大率={value.Scale}\r\n縦横比=0.000\r\n透明度={value.Opacity}\r\n合成モード=通常\r\n";
        var line = SplitLines(raw).FindIndex(item => item.StartsWith("テキスト=", StringComparison.Ordinal));
        return new AviUtl2ObjectTemplate(raw, false, "FAT standard style", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), line);
    }

    internal static List<string> SplitLines(string text) => Regex.Matches(text, ".*?(?:\\r\\n|\\n|\\r|$)", RegexOptions.Singleline)
        .Select(match => match.Value).Where(value => value.Length > 0).ToList();

    internal static string GetLineEnding(string line) => line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
        : line.EndsWith('\n') ? "\n"
        : line.EndsWith('\r') ? "\r"
        : string.Empty;

    private static string TrimSection(string line) => line.TrimEnd('\r', '\n');
}

public static class AviUtl2ObjectTemplateParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static async Task<AviUtl2ObjectTemplate> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FatException("OBJECT_TEMPLATE_NOT_FOUND", "The AviUtl2 object template was not found.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length == 0 || bytes.Contains((byte)0)) throw new FatException("OBJECT_TEMPLATE_INVALID", "The .object template must be a non-empty UTF-8 text file.");
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        string raw;
        try { raw = StrictUtf8.GetString(hasBom ? bytes[3..] : bytes); }
        catch (DecoderFallbackException error) { throw new FatException("OBJECT_TEMPLATE_INVALID", "The .object template is not valid UTF-8 text.", error); }
        var lines = AviUtl2ObjectTemplate.SplitLines(raw);
        if (lines.Count < 4 || !Trim(lines[0]).Equals("[Object]", StringComparison.Ordinal) || !Trim(lines[1]).StartsWith("frame=", StringComparison.Ordinal))
            throw new FatException("OBJECT_TEMPLATE_INVALID", "The .object template does not have the verified AviUtl2 [Object]/frame header.");
        var textSection = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (Trim(lines[index]).Equals("effect.name=テキスト", StringComparison.Ordinal))
            {
                if (textSection >= 0) throw new FatException("OBJECT_TEMPLATE_INVALID", "Multiple text effects are not supported by the template exporter.");
                textSection = index;
            }
        }
        if (textSection < 0) throw new FatException("OBJECT_TEMPLATE_INVALID", "The verified AviUtl2 text effect (effect.name=テキスト) was not found.");
        var textLine = -1;
        for (var index = textSection + 1; index < lines.Count && !Trim(lines[index]).StartsWith("[Object.", StringComparison.Ordinal); index++)
        {
            if (Trim(lines[index]).StartsWith("テキスト=", StringComparison.Ordinal)) { textLine = index; break; }
        }
        if (textLine < 0) throw new FatException("OBJECT_TEMPLATE_INVALID", "The verified text property (テキスト=) was not found in the text effect.");
        return new AviUtl2ObjectTemplate(raw, hasBom, Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(bytes)), textLine);
    }

    public static async Task RoundTripAsync(AviUtl2ObjectTemplate template, string destinationPath, CancellationToken cancellationToken)
    {
        await AtomicWriteAsync(destinationPath, template.RawText, template.HasUtf8Bom, cancellationToken);
    }

    /// <summary>Reads one verified object section as one-key-per-line properties.</summary>
    public static IReadOnlyDictionary<string, string> ReadObjectProperties(AviUtl2ObjectTemplate template, int objectIndex)
    {
        var section = $"[Object.{objectIndex}]";
        var lines = AviUtl2ObjectTemplate.SplitLines(template.RawText);
        var start = lines.FindIndex(line => Trim(line).Equals(section, StringComparison.Ordinal));
        if (start < 0) throw new FatException("OBJECT_TEMPLATE_INVALID", $"The {section} section was not found.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = start + 1; index < lines.Count; index++)
        {
            var line = Trim(lines[index]);
            if (line.StartsWith("[Object.", StringComparison.Ordinal)) break;
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            values[line[..equals]] = line[(equals + 1)..];
        }
        return values;
    }

    internal static async Task AtomicWriteAsync(string path, string content, bool bom, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = path + ".tmp";
        byte[] prefix = bom ? Encoding.UTF8.Preamble.ToArray() : Array.Empty<byte>();
        var body = Encoding.UTF8.GetBytes(content);
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(prefix, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporary, path, true);
    }

    private static string Trim(string line) => line.TrimEnd('\r', '\n');
}

public sealed record AviUtl2ObjectExportResult(string DirectoryPath, int Exported, int Skipped, string ManifestPath);

/// <summary>How the experimental multi-object writer assigns AviUtl2 layers.</summary>
public enum AviUtl2LayerPlacementMode { Auto, SingleLayer, Manual }

/// <summary>
/// Values are deliberately expressed in AviUtl2's verified one-based layer notation.
/// Manual currently means a fixed user-selected layer; per-caption manual layers need
/// explicit caption metadata and are therefore not guessed by the exporter.
/// </summary>
public sealed record AviUtl2MultiObjectExportOptions(
    AviUtl2LayerPlacementMode Mode = AviUtl2LayerPlacementMode.Auto,
    int StartLayer = 1,
    int MaxAutoLayers = 8,
    int MinimumGapFrames = 0)
{
    public AviUtl2MultiObjectExportOptions Validate()
    {
        if (StartLayer < 1) throw new FatException("OBJECT_MULTI_LAYER_INVALID", "AviUtl2 layer must be at least 1.");
        if (MaxAutoLayers < 1) throw new FatException("OBJECT_MULTI_LAYER_LIMIT_INVALID", "The maximum automatic layer count must be at least 1.");
        if (MinimumGapFrames < 0) throw new FatException("OBJECT_MULTI_GAP_INVALID", "The minimum frame gap cannot be negative.");
        return this;
    }
}

public sealed record AviUtl2ObjectPlacement(FATCaption Caption, int ObjectIndex, int Layer, int StartFrame, int EndFrame);

/// <summary>
/// Absolute timing metadata derived from caption order.  Gap values are
/// observational only: they are used for diagnostics, merge/split policy, and
/// manifests, never to move captions earlier or close silence.
/// </summary>
public sealed record CaptionTimingGap(string CaptionId, double StartTime, double EndTime, double? GapBefore, double? GapAfter)
{
    public static IReadOnlyList<CaptionTimingGap> Calculate(IReadOnlyList<FATCaption> captions)
    {
        var ordered = captions
            .Where(caption => caption.Enabled && !string.IsNullOrWhiteSpace(caption.Text))
            .OrderBy(caption => caption.StartTime)
            .Select((caption, order) => new { caption, order })
            .OrderBy(item => item.caption.StartTime)
            .ThenBy(item => item.order)
            .Select(item => item.caption)
            .ToArray();
        var result = new CaptionTimingGap[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            var previous = index > 0 ? ordered[index - 1] : null;
            var next = index + 1 < ordered.Length ? ordered[index + 1] : null;
            result[index] = new CaptionTimingGap(
                ordered[index].Id,
                ordered[index].StartTime,
                ordered[index].EndTime,
                previous is null ? null : Math.Max(0, ordered[index].StartTime - previous.EndTime),
                next is null ? null : Math.Max(0, next.StartTime - ordered[index].EndTime));
        }
        return result;
    }
}

public sealed record AviUtl2MultiObjectExportResult(
    string FilePath,
    int Exported,
    int Skipped,
    IReadOnlyList<int> UsedLayers,
    string ManifestPath,
    AviUtl2LayerPlacementMode LayerMode)
{
    /// <summary>Compatibility display value for callers that only show one layer.</summary>
    public int Layer => UsedLayers.Count == 1 ? UsedLayers[0] : UsedLayers.DefaultIfEmpty(0).Min();
}

public sealed class AviUtl2ObjectExporter(AviUtl2ObjectTemplate template, AviUtl2TextStyle? style = null)
{
    public async Task<AviUtl2ObjectExportResult> ExportAsync(string directoryPath, IReadOnlyList<FATCaption> captions, double fps, IProgress<(int Current, int Total)>? progress, CancellationToken cancellationToken)
    {
        var valid = captions.Where(caption => caption.Enabled && !string.IsNullOrWhiteSpace(caption.Text)).ToArray();
        var incomplete = directoryPath + ".incomplete";
        Directory.CreateDirectory(directoryPath);
        await File.WriteAllTextAsync(incomplete, "incomplete", cancellationToken);
        try
        {
            var map = new List<object>();
            for (var index = 0; index < valid.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var caption = valid[index];
                var start = FatFiles.Frame(caption.StartTime, fps); var end = FatFiles.Frame(caption.EndTime, fps);
                var name = $"FAT_{index + 1:0000}.object";
                await AviUtl2ObjectTemplateParser.AtomicWriteAsync(Path.Combine(directoryPath, name), template.RenderCaption(caption, fps, style ?? AviUtl2TextStyle.Default), template.HasUtf8Bom, cancellationToken);
                map.Add(new { file = name, caption_id = caption.Id, start_time = caption.StartTime, end_time = caption.EndTime, output_start = caption.StartTime, output_end = caption.EndTime, start_frame = start, end_frame = end, text = caption.Text, style = style ?? AviUtl2TextStyle.Default });
                progress?.Report((index + 1, valid.Length));
            }
            var manifest = Path.Combine(directoryPath, "fat-object-export.json");
            await AviUtl2ObjectTemplateParser.AtomicWriteAsync(manifest, JsonSerializer.Serialize(new { format = "fat-object-export", version = 1, template = new { template.SourcePath, template.Sha256 }, captions = map }, new JsonSerializerOptions { WriteIndented = true }), false, cancellationToken);
            File.Delete(incomplete);
            return new AviUtl2ObjectExportResult(directoryPath, valid.Length, captions.Count - valid.Length, manifest);
        }
        catch
        {
            // Keep the marker so an interrupted export is never mistaken for a complete set.
            throw;
        }
    }
}

/// <summary>Experimental exporter backed by a real two-text-object AviUtl2 .object capture.</summary>
public sealed class AviUtl2MultiObjectExporter(AviUtl2ObjectTemplate template, AviUtl2TextStyle? style = null)
{
    private readonly AviUtl2TextStyle _style = style ?? AviUtl2TextStyle.Default;

    /// <summary>Creates a deterministic, stable plan without touching disk.</summary>
    public IReadOnlyList<AviUtl2ObjectPlacement> Plan(IReadOnlyList<FATCaption> captions, double fps, AviUtl2MultiObjectExportOptions? options = null)
    {
        if (fps <= 0) throw new FatException("OBJECT_MULTI_FPS_INVALID", "FPS must be greater than zero.");
        var value = (options ?? new AviUtl2MultiObjectExportOptions()).Validate();
        // Enumerable.OrderBy is stable, so equal StartTime values retain their input order.
        var valid = captions.Select((caption, order) => new { caption, order })
            .Where(item => item.caption.Enabled && !string.IsNullOrWhiteSpace(item.caption.Text))
            .OrderBy(item => item.caption.StartTime).ThenBy(item => item.order).ToArray();
        if (valid.Length == 0) throw new FatException("OBJECT_MULTI_EMPTY", "There are no enabled captions to export.");

        var planned = new List<AviUtl2ObjectPlacement>(valid.Length);
        // Each element tracks a one-based AviUtl2 layer and its final planned item.
        // Time is the authority for overlap: a caption ending at exactly the next
        // caption's start is contiguous, even if display precision rounds both to
        // the same frame.
        var layerEnds = new List<(int Layer, double EndTime, int EndFrame, int PlanIndex)>();
        foreach (var item in valid)
        {
            var start = FatFiles.Frame(item.caption.StartTime, fps);
            var end = FatFiles.Frame(item.caption.EndTime, fps);
            if (item.caption.StartTime < 0 || item.caption.EndTime <= item.caption.StartTime || end < start)
                throw new FatException("OBJECT_MULTI_TIMING_INVALID", $"Caption '{item.caption.Id}' has invalid timing.");

            var selected = -1;
            for (var index = 0; index < layerEnds.Count; index++)
            {
                var timeDoesNotOverlap = layerEnds[index].EndTime <= item.caption.StartTime;
                var frameGapSatisfied = value.MinimumGapFrames == 0 || layerEnds[index].EndFrame + value.MinimumGapFrames < start;
                if (timeDoesNotOverlap && frameGapSatisfied) { selected = index; break; }
            }
            if (selected < 0)
            {
                if ((value.Mode is AviUtl2LayerPlacementMode.SingleLayer or AviUtl2LayerPlacementMode.Manual) && layerEnds.Count > 0)
                    throw new FatException("OBJECT_MULTI_OVERLAP_UNSUPPORTED", "Overlapping captions cannot share the selected AviUtl2 layer. Use automatic layer allocation or resolve the overlap.");
                if (layerEnds.Count >= value.MaxAutoLayers)
                    throw new FatException("OBJECT_MULTI_LAYER_LIMIT", $"The caption overlap requires more than {value.MaxAutoLayers} AviUtl2 layers.");
                layerEnds.Add((value.StartLayer + layerEnds.Count, item.caption.EndTime, end, planned.Count));
                selected = layerEnds.Count - 1;
            }
            else
            {
                var previous = layerEnds[selected];
                // v1.0.4 Subtitle Gap Preservation:
                // StartTime/EndTime are absolute caption timing.  Do not shorten
                // the previous object to satisfy inclusive frame display rules;
                // that would silently rewrite user/Whisper timing and can erase
                // or distort gaps.  Overlap decisions remain time-based, while
                // serialized frames stay direct conversions from each caption.
                layerEnds[selected] = (previous.Layer, item.caption.EndTime, end, planned.Count);
            }
            planned.Add(new AviUtl2ObjectPlacement(item.caption, planned.Count, layerEnds[selected].Layer, start, end));
        }
        return planned;
    }

    /// <summary>Writes one experimentally verified multi-object .object plus a detailed manifest.</summary>
    public async Task<AviUtl2MultiObjectExportResult> ExportAsync(string filePath, IReadOnlyList<FATCaption> captions, double fps, AviUtl2MultiObjectExportOptions? options, CancellationToken cancellationToken)
    {
        var value = (options ?? new AviUtl2MultiObjectExportOptions()).Validate();
        var plan = Plan(captions, fps, value);
        var incomplete = filePath + ".incomplete";
        var manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".", "fat-object-export.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");
        await File.WriteAllTextAsync(incomplete, "incomplete", cancellationToken);
        try
        {
            var content = new StringBuilder();
            foreach (var placement in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                content.Append(template.RenderMultiObjectCaption(placement.Caption, fps, _style, placement.ObjectIndex, placement.Layer, placement.StartFrame, placement.EndFrame));
            }
            await AviUtl2ObjectTemplateParser.AtomicWriteAsync(filePath, content.ToString(), template.HasUtf8Bom, cancellationToken);
            var manifest = new
            {
                format = "fat-object-export", version = 2, capability = "experimental", object_file = Path.GetFileName(filePath), fps,
                layer_mode = value.Mode.ToString(), start_layer = value.StartLayer, max_auto_layers = value.MaxAutoLayers, minimum_gap_frames = value.MinimumGapFrames,
                template = new { template.SourcePath, template.Sha256 },
                captions = plan.Select(placement => new
                {
                    caption_id = placement.Caption.Id, object_index = placement.ObjectIndex, layer = placement.Layer,
                    start_time = placement.Caption.StartTime, end_time = placement.Caption.EndTime,
                    start_frame = placement.StartFrame, end_frame = placement.EndFrame, text = placement.Caption.Text,
                    style = _style, source_caption_id = placement.Caption.Id, split_source_id = placement.Caption.Id
                }).ToArray(),
                timing = CaptionTimingGap.Calculate(plan.Select(item => item.Caption).ToArray())
            };
            await AviUtl2ObjectTemplateParser.AtomicWriteAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), false, cancellationToken);
            File.Delete(incomplete);
            return new AviUtl2MultiObjectExportResult(filePath, plan.Count, captions.Count - plan.Count, plan.Select(item => item.Layer).Distinct().Order().ToArray(), manifestPath, value.Mode);
        }
        catch
        {
            // Keep the marker: an object file without its matching manifest is incomplete.
            throw;
        }
    }

    /// <summary>Compatibility overload: one fixed verified layer.</summary>
    public Task<AviUtl2MultiObjectExportResult> ExportAsync(string filePath, IReadOnlyList<FATCaption> captions, double fps, int layer, CancellationToken cancellationToken)
        => ExportAsync(filePath, captions, fps, new AviUtl2MultiObjectExportOptions(AviUtl2LayerPlacementMode.SingleLayer, layer, 1), cancellationToken);
}
