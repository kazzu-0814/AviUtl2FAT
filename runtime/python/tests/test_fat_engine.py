import unittest
import tempfile
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fat_engine.models import Caption
from fat_engine.model_manager import ModelManager, ModelState, ModelRecommendation
from fat_engine.providers import FakeGemmaBackend, GemmaProvider, OutputValidator, PromptTemplates, ProviderRegistry


class FatEngineTests(unittest.TestCase):
    def test_passthrough_keeps_caption_data(self):
        item = Caption("1", 0, 1, "original", "recognized", confidence=0.9)
        result = ProviderRegistry().get("passthrough").generate([item], {})
        self.assertEqual("recognized", result[0].text)
        self.assertEqual("original", result[0].original_transcript)
        self.assertEqual("passthrough", result[0].provider)

    def test_stub_provider_fails_safely(self):
        with self.assertRaises(RuntimeError):
            ProviderRegistry().get("gemma").generate([], {})

    def test_missing_model_is_not_installed_and_never_downloads(self):
        with tempfile.TemporaryDirectory() as folder:
            manager = ModelManager(Path(folder))
            status = manager.status("gemma-4-e2b-it")
            self.assertEqual(ModelState.NOT_INSTALLED, status["state"])
            self.assertFalse(status["valid"])

    def test_registry_has_all_official_gemma_variants(self):
        with tempfile.TemporaryDirectory() as folder:
            manager = ModelManager(Path(folder))
            self.assertEqual(
                {"google/gemma-4-E2B-it", "google/gemma-4-E4B-it", "google/gemma-4-12B-it", "google/gemma-4-26B-A4B-it"},
                {item.source for item in manager.registry.all()})

    def test_low_memory_rejects_large_model_load(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); model = root / "gemma-4-26b-a4b-it"; model.mkdir()
            for name in ("config.json", "processor_config.json", "tokenizer_config.json", "model.safetensors"): (model / name).write_text("{}", encoding="utf-8")
            manager = ModelManager(root, device_status=lambda: {"cuda": False, "selected": "cpu", "ram_available_bytes": 4_000_000_000, "vram_bytes": None})
            with self.assertRaisesRegex(RuntimeError, "MODEL_INSUFFICIENT_MEMORY"): manager.load_model("gemma-4-26b-a4b-it", lambda _: object())
            self.assertEqual(ModelRecommendation.NOT_RECOMMENDED, manager.compatibility("gemma-4-26b-a4b-it")["recommendation"])

    def test_switching_unloads_previous_gemma_model(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for model_id in ("gemma-4-e2b-it", "gemma-4-e4b-it"):
                model = root / model_id; model.mkdir()
                for name in ("config.json", "processor_config.json", "tokenizer_config.json", "model.safetensors"): (model / name).write_text("{}", encoding="utf-8")
            manager = ModelManager(root, device_status=lambda: {"cuda": True, "selected": "cuda", "ram_available_bytes": 100_000_000_000, "vram_bytes": 100_000_000_000})
            provider = GemmaProvider(manager, FakeGemmaBackend()); provider.load("gemma-4-e2b-it"); provider.load("gemma-4-e4b-it")
            self.assertFalse(manager.is_loaded("gemma-4-e2b-it")); self.assertTrue(manager.is_loaded("gemma-4-e4b-it"))

    def test_fake_backend_preserves_caption_timing_and_original(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); model = root / "gemma-4-e2b-it"; model.mkdir()
            (model / "config.json").write_text("{}", encoding="utf-8")
            (model / "processor_config.json").write_text("{}", encoding="utf-8")
            (model / "tokenizer_config.json").write_text("{}", encoding="utf-8")
            (model / "model.safetensors").write_bytes(b"test")
            manager = ModelManager(root); provider = GemmaProvider(manager, FakeGemmaBackend())
            provider.load()
            source = Caption("1", 1.2, 3.4, "original", "recognized")
            result = provider.naturalize([source], {})[0]
            self.assertEqual((1.2, 3.4, "original"), (result.start_time, result.end_time, result.original_transcript))
            self.assertEqual("recognized", result.text)

    def test_output_validator_rejects_empty_result(self):
        with self.assertRaises(ValueError): OutputValidator.text("   ")

    def test_japanese_and_english_prompt_templates_keep_current_text(self):
        self.assertIn("AviUtl2 FAT", PromptTemplates.render("naturalize", "ja", "", "AviUtl2 FATを紹介します。", ""))
        self.assertIn("AviUtl2 FAT", PromptTemplates.render("naturalize", "en", "", "I'll introduce AviUtl2 FAT.", ""))

    def test_passthrough_supports_unicode_and_output_language(self):
        source = Caption("1", 0, 1, "日本語 English 😀", "日本語 English 😀")
        result = ProviderRegistry().get("passthrough").generate([source], {"output_language": "en"})[0]
        self.assertEqual("日本語 English 😀", result.text)
        self.assertEqual("en", result.output_language)


if __name__ == "__main__":
    unittest.main()
