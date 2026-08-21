using AviUtl2FAT.Core;
using Xunit;

namespace AviUtl2FAT.Core.Tests;

public sealed class ProviderAndFilesTests
{
    [Fact]
    public void Caption_splitter_handles_japanese_quotes_and_preserves_timing()
    {
        var caption = new FATCaption("one", 0, 8, "原文", "これは「引用符」を含む長い字幕です。安全に分割して、編集可能なテキストとして残します。");
        var values = CaptionSplitter.Split(caption, maximumCharacters: 12, maximumLines: 1);
        Assert.Equal(2, values.Count);
        Assert.Equal("one-1", values[0].Id);
        Assert.Equal("one-2", values[1].Id);
        Assert.Equal(0, values[0].StartTime);
        Assert.Equal(8, values[1].EndTime);
        Assert.All(values, value => Assert.Equal("原文", value.OriginalTranscript));
        Assert.DoesNotContain("\n", values[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Caption_splitter_can_make_the_user_selected_text_its_own_caption()
    {
        const string text = "こんにちは、AviUtl2 FATです。よろしくお願いします。";
        var caption = new FATCaption("one", 0, 9, "原文", text);
        var start = text.IndexOf("AviUtl2 FAT", StringComparison.Ordinal);
        var values = CaptionSplitter.SplitSelection(caption, start, "AviUtl2 FAT".Length);
        Assert.Equal(["こんにちは、", "AviUtl2 FAT", "です。よろしくお願いします。"], values.Select(value => value.Text));
        Assert.Equal(0, values[0].StartTime);
        Assert.Equal(9, values[^1].EndTime);
    }

    [Fact]
    public async Task Passthrough_provider_preserves_editable_transcript()
    {
        var provider = new PassthroughProvider();
        var response = await provider.GenerateCaptionsAsync(new CaptionGenerationRequest([new TranscriptSegment("1", 1.2, 3.5, "original", "recognized", 0.91)]), CancellationToken.None);
        var caption = Assert.Single(response.Captions);
        Assert.Equal("original", caption.OriginalTranscript);
        Assert.Equal("recognized", caption.Text);
        Assert.Equal(PassthroughProvider.ProviderId, caption.Provider);
    }

    [Theory]
    [InlineData(1.2, 30, 36)]
    [InlineData(1.25, 30, 38)]
    public void Placement_frames_are_rounded_consistently(double seconds, double fps, int expected) => Assert.Equal(expected, FatFiles.Frame(seconds, fps));

    [Fact]
    public async Task Placement_output_only_contains_enabled_caption()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-{Guid.NewGuid():N}.placement.json");
        try
        {
            await FatFiles.WritePlacementAsync(path, [new FATCaption("one", 0, 1, "A", "A"), new FATCaption("two", 1, 2, "B", "B", Enabled: false)], 30, CancellationToken.None);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("one", text, StringComparison.Ordinal);
            Assert.DoesNotContain("two", text, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Placement_json_preserves_five_second_and_ten_second_gaps_as_absolute_frames()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-gap-{Guid.NewGuid():N}.placement.json");
        try
        {
            await FatFiles.WritePlacementAsync(path,
                [
                    new FATCaption("a", 0, 2, "", "A"),
                    new FATCaption("b", 7, 9, "", "B"),
                    new FATCaption("c", 19, 21, "", "C")
                ],
                60, CancellationToken.None);
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var items = document.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            Assert.Equal((0, 120), (items[0].GetProperty("StartFrame").GetInt32(), items[0].GetProperty("EndFrame").GetInt32()));
            Assert.Equal((420, 540), (items[1].GetProperty("StartFrame").GetInt32(), items[1].GetProperty("EndFrame").GetInt32()));
            Assert.Equal((1140, 1260), (items[2].GetProperty("StartFrame").GetInt32(), items[2].GetProperty("EndFrame").GetInt32()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Json_exports_are_utf8_atomic_and_do_not_leave_a_temp_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-{Guid.NewGuid():N}.placement.json");
        try
        {
            await FatFiles.WritePlacementAsync(path, [new FATCaption("one", 0, 1, "原文", "日本語 English 😀")], 29.97, CancellationToken.None);
            var text = await File.ReadAllTextAsync(path);
            using var document = System.Text.Json.JsonDocument.Parse(text);
            Assert.Equal("日本語 English 😀", document.RootElement.GetProperty("Items")[0].GetProperty("Text").GetString());
            Assert.Contains("29.97", text, StringComparison.Ordinal);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public async Task Corrupted_ai_settings_fall_back_to_safe_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ broken settings", new System.Text.UTF8Encoding(false));
            var settings = await AiSelectionSettingsStore.LoadAsync(path, CancellationToken.None);
            Assert.Equal("auto", settings.PreferredAIProvider);
            Assert.True(settings.ConfirmCodexEveryTime);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public async Task Invalid_transcript_is_reported_as_a_structured_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-transcript-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"not_segments\":[]}", new System.Text.UTF8Encoding(false));
            var error = await Assert.ThrowsAsync<FatException>(() => FatFiles.ReadAttTranscriptAsync(path, CancellationToken.None));
            Assert.Equal("FAT_TRANSCRIPT_INVALID", error.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Protocol_v1_accepts_only_the_expected_protocol()
    {
        FatProtocols.Validate(FatProtocols.AppProtocol, 1, FatProtocols.AppProtocol);
        Assert.Throws<FatException>(() => FatProtocols.Validate("old-protocol", 1, FatProtocols.AppProtocol));
        Assert.Throws<FatException>(() => FatProtocols.Validate(FatProtocols.AppProtocol, 2, FatProtocols.AppProtocol));
    }

    [Fact]
    public async Task Json_placement_adapter_writes_a_document()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-placement-{Guid.NewGuid():N}.json");
        try
        {
            await new JsonPlacementAdapter().PlaceAsync(path, [new FATCaption("one", 0, 1, "A", "A")], 30, CancellationToken.None);
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Fake_native_placement_skips_disabled_and_empty_captions()
    {
        var adapter = new FakeAviUtl2PlacementAdapter();
        await adapter.PlaceAsync("unused", [new FATCaption("ja", 0, 1, "元", "日本語 AviUtl2"), new FATCaption("empty", 1, 2, "x", " "), new FATCaption("off", 2, 3, "x", "text", Enabled: false)], 30, CancellationToken.None);
        var item = Assert.Single(adapter.Placed);
        Assert.Equal((0, 30, "日本語 AviUtl2"), (item.StartFrame, item.EndFrame, item.Text));
    }

    [Fact]
    public async Task Srt_exporter_preserves_unicode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-{Guid.NewGuid():N}.srt");
        try { await new SrtCaptionExporter().ExportAsync(path, [new FATCaption("1", 0, 1.2, "原文", "日本語 English ！？")], CancellationToken.None); Assert.Contains("日本語 English ！？", await File.ReadAllTextAsync(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Srt_exporter_preserves_short_and_long_gaps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-gap-{Guid.NewGuid():N}.srt");
        try
        {
            await new SrtCaptionExporter().ExportAsync(path,
                [
                    new FATCaption("a", 0, 2, "", "A"),
                    new FATCaption("b", 2.1, 4, "", "B"),
                    new FATCaption("c", 14, 16, "", "C")
                ],
                CancellationToken.None);
            var output = await File.ReadAllTextAsync(path);
            Assert.Contains("00:00:00,000 --> 00:00:02,000", output, StringComparison.Ordinal);
            Assert.Contains("00:00:02,100 --> 00:00:04,000", output, StringComparison.Ordinal);
            Assert.Contains("00:00:14,000 --> 00:00:16,000", output, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Object_template_round_trips_and_replaces_only_verified_text_field()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fat-object-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "template.object");
        const string raw = "[Object]\r\nframe=0,80\r\n[Object.0]\r\neffect.name=テキスト\r\nフォント=LINE Seed JP ExtraBold\r\nテキスト=こんにちは、AviUtl2 FATです。\r\n文字毎に個別オブジェクト=0\r\n自動スクロール=0\r\n移動座標上に表示=0\r\nオブジェクトの長さを自動調節=0\r\n未知のプロパティ=そのまま\r\n[Object.1]\r\neffect.name=標準描画\r\nX=-891.50\r\n";
        try
        {
            await File.WriteAllTextAsync(source, raw, new System.Text.UTF8Encoding(false));
            var template = await AviUtl2ObjectTemplateParser.LoadAsync(source, CancellationToken.None);
            var roundTrip = Path.Combine(root, "roundtrip.object");
            await AviUtl2ObjectTemplateParser.RoundTripAsync(template, roundTrip, CancellationToken.None);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(roundTrip));
            var output = Path.Combine(root, "output");
            var result = await new AviUtl2ObjectExporter(template).ExportAsync(output, [new FATCaption("1", 0, 1, "原文", "FATから生成したテキストです。"), new FATCaption("2", 1, 2, "x", "", Enabled: false)], 30, null, CancellationToken.None);
            var generated = Assert.Single(Directory.GetFiles(output, "*.object"));
            var rendered = await File.ReadAllTextAsync(generated);
            Assert.Contains("テキスト=FATから生成したテキストです。", rendered, StringComparison.Ordinal);
            Assert.Contains("テキスト=FATから生成したテキストです。\r\n文字毎に個別オブジェクト=0", rendered, StringComparison.Ordinal);
            Assert.Contains("フォント=LINE Seed JP ExtraBold", rendered, StringComparison.Ordinal);
            Assert.Contains("X=-891.50", rendered, StringComparison.Ordinal);
            var reparsed = await AviUtl2ObjectTemplateParser.LoadAsync(generated, CancellationToken.None);
            var textProperties = AviUtl2ObjectTemplateParser.ReadObjectProperties(reparsed, 0);
            Assert.Equal("FATから生成したテキストです。", textProperties["テキスト"]);
            Assert.Equal("0", textProperties["文字毎に個別オブジェクト"]);
            Assert.Equal("0", textProperties["自動スクロール"]);
            Assert.Equal("0", textProperties["移動座標上に表示"]);
            Assert.Equal("0", textProperties["オブジェクトの長さを自動調節"]);
            Assert.Equal("そのまま", textProperties["未知のプロパティ"]);
            Assert.Equal(1, result.Exported);
            Assert.Equal(1, result.Skipped);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Object_template_rejects_multiline_text_until_serialization_is_verified()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-object-{Guid.NewGuid():N}.object");
        try
        {
            await File.WriteAllTextAsync(path, "[Object]\nframe=0,1\n[Object.0]\neffect.name=テキスト\nテキスト=本文\n", new System.Text.UTF8Encoding(false));
            var template = await AviUtl2ObjectTemplateParser.LoadAsync(path, CancellationToken.None);
            Assert.Equal("OBJECT_TEXT_NEWLINE_UNSUPPORTED", Assert.Throws<FatException>(() => template.RenderText("a\nb")).Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Experimental_multi_object_export_uses_verified_aviutl2_sections_and_layers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-multi-{Guid.NewGuid():N}.object");
        try
        {
            var template = AviUtl2ObjectTemplate.CreateStandard();
            var result = await new AviUtl2MultiObjectExporter(template).ExportAsync(path,
                [new FATCaption("1", 0, 80d / 60d, "一", "こんにちは、AviUtl2 FATです。"), new FATCaption("2", 81d / 60d, 161d / 60d, "二", "こんにちは、あいうえお")], 60, 1, CancellationToken.None);
            var output = await File.ReadAllTextAsync(path);
            Assert.Contains("[0]\r\nlayer=1\r\nframe=0,80\r\n[0.0]", output, StringComparison.Ordinal);
            Assert.Contains("テキスト=こんにちは、AviUtl2 FATです。\r\n文字毎に個別オブジェクト=0", output, StringComparison.Ordinal);
            Assert.Contains("[1]\r\nlayer=1\r\nframe=81,161\r\n[1.0]", output, StringComparison.Ordinal);
            Assert.Contains("テキスト=こんにちは、あいうえお", output, StringComparison.Ordinal);
            Assert.Contains("[1.1]\r\neffect.name=標準描画", output, StringComparison.Ordinal);
            Assert.Equal(2, result.Exported);
            Assert.Equal([1], result.UsedLayers);
            var manifest = await File.ReadAllTextAsync(result.ManifestPath);
            Assert.Contains("\"caption_id\": \"1\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"object_index\": 1", manifest, StringComparison.Ordinal);
            Assert.Contains("\"source_caption_id\": \"2\"", manifest, StringComparison.Ordinal);
            Assert.Contains("こんにちは、あいうえお", manifest, StringComparison.Ordinal);
        }
        finally { File.Delete(path); File.Delete(path + ".incomplete"); File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "fat-object-export.json")); }
    }

    [Fact]
    public async Task Experimental_multi_object_export_preserves_absolute_gaps_and_records_gap_metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fat-multi-gap-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "gap.object");
        try
        {
            var result = await new AviUtl2MultiObjectExporter(AviUtl2ObjectTemplate.CreateStandard()).ExportAsync(path,
                [new FATCaption("a", 0, 2, "", "A"), new FATCaption("b", 7, 9, "", "B"), new FATCaption("c", 19, 21, "", "C")],
                60, new AviUtl2MultiObjectExportOptions(AviUtl2LayerPlacementMode.SingleLayer), CancellationToken.None);
            var output = await File.ReadAllTextAsync(path);
            Assert.Contains("[0]\r\nlayer=1\r\nframe=0,120", output, StringComparison.Ordinal);
            Assert.Contains("[1]\r\nlayer=1\r\nframe=420,540", output, StringComparison.Ordinal);
            Assert.Contains("[2]\r\nlayer=1\r\nframe=1140,1260", output, StringComparison.Ordinal);
            Assert.Equal([1], result.UsedLayers);
            using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath));
            var timing = manifest.RootElement.GetProperty("timing").EnumerateArray().ToArray();
            Assert.Equal(5, timing[1].GetProperty("GapBefore").GetDouble());
            Assert.Equal(10, timing[2].GetProperty("GapBefore").GetDouble());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Caption_splitter_only_redistributes_time_inside_the_split_caption()
    {
        var split = CaptionSplitter.Split(new FATCaption("a", 0, 6, "", "こんにちは。今日はAviUtl2 FATをかなり丁寧に紹介します。"), maximumCharacters: 8, maximumLines: 1);
        var following = new FATCaption("b", 10, 12, "", "次の発話");
        Assert.True(split.Count >= 2);
        Assert.Equal(0, split[0].StartTime);
        Assert.Equal(6, split[^1].EndTime);
        Assert.Equal(10, following.StartTime);
        Assert.Equal(4, following.StartTime - split[^1].EndTime);
    }

    [Fact]
    public async Task Experimental_multi_object_export_rejects_overlap_on_the_verified_single_layer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-multi-{Guid.NewGuid():N}.object");
        try
        {
            var error = await Assert.ThrowsAsync<FatException>(() => new AviUtl2MultiObjectExporter(AviUtl2ObjectTemplate.CreateStandard()).ExportAsync(path,
                [new FATCaption("1", 0, 2, "一", "first"), new FATCaption("2", 1, 3, "二", "second")], 60, 1, CancellationToken.None));
            Assert.Equal("OBJECT_MULTI_OVERLAP_UNSUPPORTED", error.Code);
        }
        finally { File.Delete(path); File.Delete(path + ".incomplete"); }
    }

    [Fact]
    public async Task Multi_object_export_serializes_three_objects_in_stable_start_order()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fat-multi-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "fat-captions.object");
        try
        {
            var exporter = new AviUtl2MultiObjectExporter(AviUtl2ObjectTemplate.CreateStandard());
            var result = await exporter.ExportAsync(path,
                [new FATCaption("late", 2, 3, "", "三"), new FATCaption("first", 0, 1, "", "一"), new FATCaption("second", 1.1, 2, "", "二")],
                10, new AviUtl2MultiObjectExportOptions(), CancellationToken.None);
            var output = await File.ReadAllTextAsync(path);
            Assert.True(output.IndexOf("テキスト=一", StringComparison.Ordinal) < output.IndexOf("テキスト=二", StringComparison.Ordinal));
            Assert.True(output.IndexOf("テキスト=二", StringComparison.Ordinal) < output.IndexOf("テキスト=三", StringComparison.Ordinal));
            Assert.Contains("[2.1]", output, StringComparison.Ordinal);
            Assert.Equal(3, result.Exported);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Multi_object_auto_layer_reuses_earliest_available_layer_and_preserves_equal_time_order()
    {
        var exporter = new AviUtl2MultiObjectExporter(AviUtl2ObjectTemplate.CreateStandard());
        var plan = exporter.Plan(
            [new FATCaption("a", 0, 2, "", "A"), new FATCaption("b", 0, 1, "", "B"), new FATCaption("c", 1.1, 3, "", "C"), new FATCaption("d", 2.1, 4, "", "D")],
            10, new AviUtl2MultiObjectExportOptions(AviUtl2LayerPlacementMode.Auto, StartLayer: 5, MaxAutoLayers: 2));
        Assert.Equal(["a", "b", "c", "d"], plan.Select(item => item.Caption.Id));
        Assert.Equal([5, 6, 6, 5], plan.Select(item => item.Layer));
    }

    [Fact]
    public void Multi_object_reports_layer_limit_and_single_layer_overlap()
    {
        var exporter = new AviUtl2MultiObjectExporter(AviUtl2ObjectTemplate.CreateStandard());
        var captions = new[] { new FATCaption("a", 0, 2, "", "A"), new FATCaption("b", 1, 3, "", "B") };
        Assert.Equal("OBJECT_MULTI_LAYER_LIMIT", Assert.Throws<FatException>(() => exporter.Plan(captions, 60, new AviUtl2MultiObjectExportOptions(MaxAutoLayers: 1))).Code);
        Assert.Equal("OBJECT_MULTI_OVERLAP_UNSUPPORTED", Assert.Throws<FatException>(() => exporter.Plan(captions, 60, new AviUtl2MultiObjectExportOptions(AviUtl2LayerPlacementMode.SingleLayer))).Code);
    }

    [Fact]
    public async Task Multi_object_single_layer_accepts_contiguous_times_that_round_to_the_same_frame()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fat-multi-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "contiguous.object");
        try
        {
            var exporter = new AviUtl2MultiObjectExporter(AviUtl2ObjectTemplate.CreateStandard());
            var captions = new[] { new FATCaption("one", 0, 8.406, "", "前半"), new FATCaption("two", 8.406, 13.5, "", "後半") };
            var result = await exporter.ExportAsync(path, captions, 60, new AviUtl2MultiObjectExportOptions(AviUtl2LayerPlacementMode.SingleLayer), CancellationToken.None);
            var output = await File.ReadAllTextAsync(path);
            Assert.Equal([1], result.UsedLayers);
            Assert.Contains("frame=0,504", output, StringComparison.Ordinal);
            Assert.Contains("frame=504,810", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Multi_object_rejects_empty_or_disabled_caption_sets()
    {
        var exporter = new AviUtl2MultiObjectExporter(AviUtl2ObjectTemplate.CreateStandard());
        var error = Assert.Throws<FatException>(() => exporter.Plan([new FATCaption("off", 0, 1, "", "x", Enabled: false), new FATCaption("empty", 1, 2, "", " ")], 60));
        Assert.Equal("OBJECT_MULTI_EMPTY", error.Code);
    }

    [Fact]
    public async Task Multi_object_export_serializes_ten_unicode_captions_without_text_property_leakage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fat-multi-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "fat-captions.object");
        try
        {
            var captions = Enumerable.Range(0, 10).Select(index => new FATCaption($"id-{index}", index * 2, index * 2 + 1, "", $"字幕 {index} 日本語 😀")).ToArray();
            var result = await new AviUtl2MultiObjectExporter(AviUtl2ObjectTemplate.CreateStandard()).ExportAsync(path, captions, 30, new AviUtl2MultiObjectExportOptions(), CancellationToken.None);
            var output = await File.ReadAllTextAsync(path);
            Assert.Equal(10, result.Exported);
            Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(output, @"(?m)^\[\d+\]\r?$").Count);
            Assert.Contains("テキスト=字幕 9 日本語 😀\r\n文字毎に個別オブジェクト=0", output, StringComparison.Ordinal);
            Assert.DoesNotContain("テキスト=字幕 9 日本語 😀文字毎", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Codex_provider_preserves_original_transcript_and_marks_preview_metadata()
    {
        var provider = new CodexProvider(new FakeCodexBackend("今回はAviUtl2 FATを紹介します。"));
        var original = new FATCaption("1", 0, 2, "えー今回はですね、AviUtl2 FATを紹介していきたいと思います。", "えー今回はですね、AviUtl2 FATを紹介していきたいと思います。");
        var response = await provider.TransformAsync(new CaptionTransformRequest([original], CaptionOperation.Naturalize, "ja"), CancellationToken.None);
        var caption = Assert.Single(response.Captions);
        Assert.Equal(original.OriginalTranscript, caption.OriginalTranscript);
        Assert.Equal("今回はAviUtl2 FATを紹介します。", caption.Text);
        Assert.Equal("codex", caption.Provider);
        Assert.Equal("codex-cli", caption.Model);
    }

    [Fact]
    public async Task Rule_provider_is_offline_and_has_naturalize_and_shorten_capabilities()
    {
        var provider = new RuleBasedProvider();
        Assert.True(provider.Descriptor.Capabilities.HasFlag(AiProviderCapability.Offline));
        Assert.True(provider.Descriptor.Capabilities.HasFlag(AiProviderCapability.Naturalize));
        var response = await provider.TransformAsync(new CaptionTransformRequest([new FATCaption("1", 0, 1, "えー本文", "えー本文")], CaptionOperation.Naturalize, "ja"), CancellationToken.None);
        Assert.Equal("本文", Assert.Single(response.Captions).Text);
    }

    [Fact]
    public async Task Ai_selection_settings_round_trip_without_credentials()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fat-ai-{Guid.NewGuid():N}.json");
        try
        {
            await AiSelectionSettingsStore.SaveAsync(path, new AiSelectionSettings("codex", "gemma-4-e2b-it", "cli"), CancellationToken.None);
            var restored = await AiSelectionSettingsStore.LoadAsync(path, CancellationToken.None);
            Assert.Equal("codex", restored.PreferredAIProvider);
            Assert.Equal("cli", restored.CodexBackend);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Fake_app_server_connects_generates_and_disconnects_without_network()
    {
        var server = new FakeCodexAppServerBackend("短い字幕です。");
        await server.ConnectAsync(CancellationToken.None);
        Assert.Equal(CodexConnectionState.Connected, server.ConnectionState);
        Assert.Equal("短い字幕です。", await server.GenerateAsync("safe prompt", CancellationToken.None));
        await server.DisconnectAsync(CancellationToken.None);
        Assert.Equal(CodexConnectionState.Disconnected, server.ConnectionState);
    }

    [Fact]
    public async Task Auto_backend_prefers_connected_app_server_and_falls_back_to_cli()
    {
        var appServer = new FakeCodexAppServerBackend();
        var cli = new FakeCodexBackend();
        var fallback = await CodexBackendSelector.SelectAsync(CodexBackendPreference.Auto, appServer, cli, CancellationToken.None);
        Assert.Equal("Codex CLI", fallback.Name);
        await appServer.ConnectAsync(CancellationToken.None);
        var preferred = await CodexBackendSelector.SelectAsync(CodexBackendPreference.Auto, appServer, cli, CancellationToken.None);
        Assert.Equal("Codex App Server", preferred.Name);
    }

    [Fact]
    public async Task Explicit_app_server_requires_an_explicit_connection()
    {
        var server = new FakeCodexAppServerBackend();
        var cli = new FakeCodexBackend();
        var error = await Assert.ThrowsAsync<FatException>(() => CodexBackendSelector.SelectAsync(CodexBackendPreference.AppServer, server, cli, CancellationToken.None));
        Assert.Equal("CODEX_APP_SERVER_DISCONNECTED", error.Code);
    }

    [Fact]
    public void Codex_prompt_keeps_user_text_as_prompt_data_and_does_not_produce_shell_syntax()
    {
        var prompt = CodexPromptTemplates.Render(CaptionOperation.Naturalize, "ja", "", "\" & | > < 日本語 😀", "");
        Assert.Contains("\" & | > < 日本語 😀", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void V09_profile_ui_state_and_caption_warning_are_safe_for_low_spec()
    {
        var profile = SpeechProfiles.Select(RecognitionProfile.Auto, 4, 4, false);
        Assert.Equal(RecognitionProfile.Low, profile.Profile);
        Assert.Equal("int8", profile.ComputeType);
        Assert.True(profile.VadEnabled);
        Assert.True(new FatUiState(InputPath: "video.mp4").CanRecognize);
        Assert.Equal(2, new FatUiState(InputPath: "video.mp4").CurrentStep);
        Assert.Contains("精度", CaptionQuality.Warning(new FATCaption("1", 0, 1, "x", "x", Confidence: 0.1)));
    }

    [Fact]
    public void Caption_timeline_finds_current_caption_and_preserves_gaps()
    {
        var timeline = new CaptionTimeline([
            new FATCaption("a", 0, 2, "", "A"),
            new FATCaption("b", 7, 9, "", "B")
        ]);

        Assert.Equal("a", timeline.FindCaptionAtTime(1)?.Id);
        Assert.Null(timeline.FindCaptionAtTime(4));
        Assert.Equal("b", timeline.FindCaptionAtTime(8)?.Id);
    }

    [Fact]
    public void Caption_timeline_uses_start_inclusive_end_exclusive_boundary()
    {
        var timeline = new CaptionTimeline([
            new FATCaption("a", 0, 2, "", "A"),
            new FATCaption("b", 2, 4, "", "B")
        ]);

        Assert.Equal("a", timeline.FindCaptionAtTime(1.999)?.Id);
        Assert.Equal("b", timeline.FindCaptionAtTime(2.0)?.Id);
        Assert.Null(timeline.FindCaptionAtTime(4.0));
    }

    [Fact]
    public void Caption_timeline_is_rebuilt_after_split_and_merge_inputs()
    {
        var split = CaptionSplitter.SplitSelection(new FATCaption("a", 0, 6, "", "abcdef"), 2, 2, minimumSeconds: 0.5);
        var splitTimeline = new CaptionTimeline(split);
        Assert.Equal("a-2", splitTimeline.FindCaptionAtTime(split[1].StartTime + 0.01)?.Id);

        var merged = split[0] with { Id = "merged", EndTime = split[^1].EndTime, Text = string.Join("", split.Select(caption => caption.Text)) };
        var mergedTimeline = new CaptionTimeline([merged]);
        Assert.Equal("merged", mergedTimeline.FindCaptionAtTime(5.5)?.Id);
    }
}
