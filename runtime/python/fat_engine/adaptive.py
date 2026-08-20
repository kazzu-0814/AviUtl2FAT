from __future__ import annotations

import os
import re
from dataclasses import dataclass

from .models import Caption


@dataclass(frozen=True)
class RuntimeProfile:
    tier: str
    cpu_threads: int
    whisper_model: str
    compute_type: str
    ai_batch_size: int
    allow_large_local_model: bool


def classify_runtime(logical_cores: int | None, available_ram_bytes: int | None, cuda: bool) -> RuntimeProfile:
    cores = max(1, logical_cores or (os.cpu_count() or 1))
    available_gb = (available_ram_bytes or 0) / (1024 ** 3)
    if cuda or (available_gb >= 24 and cores >= 12):
        return RuntimeProfile("high", max(1, cores - 2), "small", "auto", 12, True)
    if available_gb >= 10 and cores >= 6:
        return RuntimeProfile("standard", max(1, cores - 2), "small", "int8", 5, False)
    return RuntimeProfile("low", max(1, min(4, cores // 2 or 1)), "small", "int8", 2, False)


class RuleProcessor:
    """Deterministic cleanup only; it never changes the source transcript."""

    _whitespace = re.compile(r"[\s\u3000]+")
    _punctuation = re.compile(r"([、。！？!?])\1+")
    _filler_patterns = {
        "ja": re.compile(r"^(?:えー+|あのー*|まあ|そのー+)[、,\s]*", re.IGNORECASE),
        "en": re.compile(r"^(?:uh+|um+|you know)[,\s]*", re.IGNORECASE),
    }

    def clean(self, caption: Caption, filler_mode: str = "auto", language: str | None = None) -> Caption:
        text = self._punctuation.sub(r"\1", self._whitespace.sub(" ", caption.text).strip())
        normalized_language = (language or caption.detected_language or "").lower().split("-", 1)[0]
        if filler_mode == "aggressive" or (filler_mode == "auto" and len(text) > 16):
            pattern = self._filler_patterns.get(normalized_language)
            if pattern:
                text = pattern.sub("", text).strip()
        return Caption(caption.id, caption.start_time, caption.end_time, caption.original_transcript, text,
                       caption.provider, caption.model, caption.confidence, caption.detected_language,
                       caption.output_language).validate()

    def process(self, captions: list[Caption], filler_mode: str = "auto", language: str | None = None) -> list[Caption]:
        seen: set[tuple[float, str]] = set()
        result: list[Caption] = []
        for caption in captions:
            cleaned = self.clean(caption, filler_mode, language)
            key = (round(cleaned.start_time, 3), cleaned.text)
            if not cleaned.text or key in seen:
                continue
            seen.add(key)
            result.append(cleaned)
        return result


class NeedAiDetector:
    def needs_ai(self, caption: Caption, mode: str = "auto") -> bool:
        if mode == "none":
            return False
        text = caption.text.strip()
        if not text:
            return False
        if mode == "quality":
            return True
        if len(text) > 28 or text.count("、") >= 3 or text.count("。") >= 2:
            return True
        return bool(re.search(r"(?:えー+|あのー*|そのー+|\b(?:uh|um)\b)", text, re.IGNORECASE))

    def select(self, captions: list[Caption], mode: str = "auto") -> list[Caption]:
        return [caption for caption in captions if self.needs_ai(caption, mode)]


class SmartSplitter:
    _boundaries = re.compile(r"(?<=[。！？!?]|[,.])\s*|\s+")

    def split(self, caption: Caption, maximum_characters: int = 24, maximum_lines: int = 2, minimum_seconds: float = 0.8) -> list[Caption]:
        text = caption.text.strip()
        limit = max(4, maximum_characters * max(1, maximum_lines))
        if len(text) <= limit:
            return [caption]
        parts: list[str] = []; current = ""
        for token in filter(None, self._boundaries.split(text)):
            candidate = current + token
            if current and len(candidate) > limit:
                parts.append(current); current = token
            else: current = candidate
        if current: parts.append(current)
        if len(parts) < 2: parts = [text[index:index + limit] for index in range(0, len(text), limit)]
        duration = caption.end_time - caption.start_time
        if duration < minimum_seconds * len(parts): return [caption]
        total = sum(max(1, len(item)) for item in parts); cursor = caption.start_time; result = []
        for index, item in enumerate(parts):
            share = duration * max(1, len(item)) / total
            end = caption.end_time if index == len(parts) - 1 else max(cursor + minimum_seconds, cursor + share)
            result.append(Caption(f"{caption.id}-{index + 1}", cursor, min(end, caption.end_time), caption.original_transcript, item, caption.provider, caption.model, caption.confidence, caption.detected_language, caption.output_language))
            cursor = end
        return result
