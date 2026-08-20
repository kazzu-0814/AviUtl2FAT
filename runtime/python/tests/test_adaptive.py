import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fat_engine.adaptive import NeedAiDetector, RuleProcessor, SmartSplitter, classify_runtime
from fat_engine.models import Caption


class AdaptiveTests(unittest.TestCase):
    def caption(self, text: str) -> Caption:
        return Caption("1", 0, 1, text, text, detected_language="ja")

    def test_low_profile_limits_cpu_and_ai(self):
        profile = classify_runtime(4, 8 * 1024 ** 3, False)
        self.assertEqual("low", profile.tier)
        self.assertEqual("int8", profile.compute_type)
        self.assertFalse(profile.allow_large_local_model)

    def test_rule_processor_normalizes_but_preserves_transcript(self):
        value = RuleProcessor().clean(self.caption("  えー、 こんにちは、、  "), "aggressive", "ja")
        self.assertEqual("こんにちは、", value.text)
        self.assertEqual("  えー、 こんにちは、、  ", value.original_transcript)

    def test_need_ai_selects_long_caption_only(self):
        values = [self.caption("短い字幕です。"), self.caption("これはとても長い字幕であり、読みやすくするために文章を整える必要があるかもしれません。")]
        selected = NeedAiDetector().select(values)
        self.assertEqual(["これはとても長い字幕であり、読みやすくするために文章を整える必要があるかもしれません。"], [item.text for item in selected])

    def test_smart_split_preserves_range_and_source(self):
        source = Caption("source", 0, 6, "原文", "こんにちは、なのだ。AviUtl2 FATの動作確認をしているのだ。", detected_language="ja")
        values = SmartSplitter().split(source, maximum_characters=12, maximum_lines=1)
        self.assertGreater(len(values), 1)
        self.assertEqual(0, values[0].start_time)
        self.assertEqual(6, values[-1].end_time)
        self.assertTrue(all(item.start_time < item.end_time and item.original_transcript == "原文" for item in values))
