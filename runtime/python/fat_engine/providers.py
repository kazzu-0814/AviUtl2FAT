from __future__ import annotations
from abc import ABC, abstractmethod
from pathlib import Path
from .models import Caption
from .model_manager import ModelManager

class OutputValidator:
    @staticmethod
    def text(value: object) -> str:
        if not isinstance(value, str): raise ValueError("AI_INVALID_OUTPUT")
        value = " ".join(value.strip().split())
        if not value: raise ValueError("AI_EMPTY_OUTPUT")
        return value

class PromptTemplates:
    _supported = {"ja", "en"}
    @classmethod
    def render(cls, operation: str, language: str, previous: str, current: str, following: str) -> str:
        language = language if language in cls._supported else "ja"
        path = Path(__file__).resolve().parents[1] / "prompts" / language / f"{operation}.txt"
        if not path.is_file(): path = Path(__file__).resolve().parents[1] / "prompts" / language / "naturalize.txt"
        return path.read_text(encoding="utf-8").format(previous=previous, current=current, next=following)

class InferenceBackend(ABC):
    @abstractmethod
    def load(self, path: Path, definition=None) -> None: ...
    @abstractmethod
    def generate(self, prompt: str) -> str: ...

class FakeGemmaBackend(InferenceBackend):
    def load(self, path: Path, definition=None) -> None: self.path = path; self.definition = definition
    def generate(self, prompt: str) -> str: return prompt.split("CURRENT:", 1)[-1].split("\n", 1)[0].strip()

class TransformersBackend(InferenceBackend):
    def load(self, path: Path, definition=None) -> None:
        try:
            from transformers import AutoModelForMultimodalLM, AutoProcessor
            import torch
        except ImportError as error: raise RuntimeError("GEMMA_BACKEND_NOT_INSTALLED: install requirements-ai.txt") from error
        self.processor = AutoProcessor.from_pretrained(str(path), local_files_only=True)
        self.model = AutoModelForMultimodalLM.from_pretrained(str(path), local_files_only=True, dtype="auto", device_map="auto" if torch.cuda.is_available() else None)
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        if self.device == "cpu": self.model.to("cpu")
        self.model.eval()
    def generate(self, prompt: str) -> str:
        messages = [{"role": "system", "content": "Return only the requested subtitle text."}, {"role": "user", "content": prompt}]
        inputs = self.processor.apply_chat_template(messages, tokenize=True, return_dict=True, return_tensors="pt", add_generation_prompt=True, enable_thinking=False)
        inputs = inputs.to(self.model.device); input_length = inputs["input_ids"].shape[-1]
        output = self.model.generate(**inputs, max_new_tokens=160, do_sample=False)
        return self.processor.decode(output[0][input_length:], skip_special_tokens=True).strip()

class AIProvider(ABC):
    id: str
    @abstractmethod
    def generate(self, captions: list[Caption], request: dict[str, object]) -> list[Caption]: ...
    def rewrite(self, captions, request): return self.generate(captions, request)
    def shorten(self, captions, request): return self.generate(captions, request)
    def naturalize(self, captions, request): return self.generate(captions, request)

class PassthroughProvider(AIProvider):
    id = "passthrough"
    def generate(self, captions, request):
        language = str(request.get("output_language", request.get("outputLanguage", "ja")))
        return [Caption(x.id, x.start_time, x.end_time, x.original_transcript, x.text, self.id, None, x.confidence, x.detected_language, language).validate() for x in captions]

class GemmaProvider(AIProvider):
    id = "gemma"
    def __init__(self, manager: ModelManager, backend: InferenceBackend) -> None: self.manager, self.backend = manager, backend
    @staticmethod
    def _model_id(request: dict[str, object] | None = None) -> str: return str((request or {}).get("model_id", "gemma-4-e2b-it"))
    def health_check(self) -> dict[str, object]:
        loaded = next((item.id for item in self.manager.registry.all() if self.manager.is_loaded(item.id)), None)
        return {"provider": self.id, "ready": loaded is not None, "model_id": loaded, "models": self.manager.status()}
    def load(self, model_id: str = "gemma-4-e2b-it") -> dict[str, object]:
        definition = self.manager.registry.get(model_id)
        return self.manager.load_model(model_id, lambda path: self.backend.load(path, definition) or self.backend, enforce_memory=not isinstance(self.backend, FakeGemmaBackend))
    def generate(self, captions, request):
        model_id = self._model_id(request)
        if not self.manager.is_loaded(model_id): raise RuntimeError("MODEL_NOT_INSTALLED")
        output=[]; language = str(request.get("output_language", request.get("outputLanguage", "ja")))
        operation = str(request.get("operation", "naturalize")).replace("ai.", "")
        for index, caption in enumerate(captions):
            previous = captions[index-1].text if index else ""; following = captions[index+1].text if index + 1 < len(captions) else ""
            prompt = PromptTemplates.render(operation, language, previous, caption.text, following)
            text = OutputValidator.text(self.backend.generate(prompt))
            output.append(Caption(caption.id, caption.start_time, caption.end_time, caption.original_transcript, text, self.id, model_id, caption.confidence, caption.detected_language, language))
        return output

class UnavailableProvider(AIProvider):
    def __init__(self, provider_id): self.id = provider_id
    def generate(self, captions, request): raise RuntimeError(f"PROVIDER_UNAVAILABLE: {self.id}")

class ProviderRegistry:
    def __init__(self, manager: ModelManager | None = None) -> None:
        self.manager = manager or ModelManager(Path.cwd() / "runtime" / "models")
        self._providers={"passthrough": PassthroughProvider(), "gemma": GemmaProvider(self.manager, TransformersBackend())}
        for item in ("llama", "japanese_ai", "future"): self._providers[item]=UnavailableProvider(item)
    def register(self, provider): self._providers[provider.id]=provider
    def get(self, provider_id):
        if provider_id not in self._providers: raise KeyError(f"Unknown provider '{provider_id}'")
        return self._providers[provider_id]
    def status(self): return [{"id": key, "available": key == "passthrough" or (key == "gemma" and self._providers[key].health_check()["ready"])} for key in self._providers]
