from __future__ import annotations

import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


@dataclass(frozen=True)
class SpeechProfile:
    name: str
    model: str
    device: str
    compute_type: str
    beam_size: int
    cpu_threads: int
    vad: bool = True


def _available_ram_bytes() -> int | None:
    try:
        import psutil
        return int(psutil.virtual_memory().available)
    except Exception:
        return None


def select_profile(profile: str, logical_cores: int | None = None, available_ram_bytes: int | None = None, cuda: bool | None = None) -> SpeechProfile:
    """Choose a profile from the actual machine capabilities."""
    cores = max(1, logical_cores or os.cpu_count() or 1)
    available_memory = available_ram_bytes if available_ram_bytes is not None else _available_ram_bytes()
    ram_gb = (available_memory or 0) / (1024 ** 3)
    # Do not probe CUDA here.  Some Windows graphics drivers can block during
    # enumeration, while this method runs on the recognition progress path.
    cuda_available = bool(cuda) if cuda is not None else False
    requested = (profile or "auto").lower()
    if requested == "high" and (cuda_available or ram_gb >= 24):
        return SpeechProfile("high", "medium", "cuda" if cuda_available else "cpu", "float16" if cuda_available else "int8", 8, max(1, cores - 2))
    if requested == "standard" or (requested == "auto" and (cuda_available or (ram_gb >= 10 and cores >= 6))):
        return SpeechProfile("standard", "small", "cuda" if cuda_available else "cpu", "float16" if cuda_available else "int8", 5, max(1, min(8, cores - 2)))
    return SpeechProfile("low", "base", "cpu", "int8", 2, max(1, min(4, max(1, cores // 2))))


def build_initial_prompt(words: Iterable[str], maximum_characters: int = 240) -> str:
    unique: list[str] = []
    for word in words:
        value = str(word).strip()
        if value and value not in unique:
            unique.append(value)
    return "、".join(unique)[:maximum_characters]


class TranscriptPostProcessor:
    _spaces = re.compile(r"[\s\u3000]+")
    _duplicate_punctuation = re.compile(r"([、。！？!?])\1+")
    _fillers = {
        "ja": re.compile(r"^(?:えー+|あのー*|そのー*|まあ)[、,\s]*", re.I),
        "en": re.compile(r"^(?:uh+|um+|you know)[,\s]*", re.I),
    }

    def clean(self, text: str, language: str | None, filler_mode: str = "auto") -> str:
        value = self._duplicate_punctuation.sub(r"\1", self._spaces.sub(" ", text).strip())
        lang = (language or "").lower().split("-", 1)[0]
        if filler_mode in {"auto", "organize"} and (filler_mode == "organize" or len(value) > 16):
            pattern = self._fillers.get(lang)
            if pattern:
                value = pattern.sub("", value).strip()
        return value

    def process(self, segments: Iterable[dict[str, object]], duration: float, language: str | None, filler_mode: str = "auto") -> list[dict[str, object]]:
        result: list[dict[str, object]] = []
        previous = ""
        for source in segments:
            item = dict(source)
            text = self.clean(str(item.get("text", "")), language, filler_mode)
            start = max(0.0, min(float(item.get("start", 0.0)), duration)) if duration > 0 else max(0.0, float(item.get("start", 0.0)))
            end = max(start + 0.01, float(item.get("end", start + 0.01)))
            if duration > 0:
                end = min(end, duration)
                if end <= start: continue
            # VAD/no-speech guard: don't create subtitles from likely silence.
            if float(item.get("no_speech_probability", 0.0) or 0.0) >= 0.85 and len(text) < 8:
                continue
            if text and text == previous and end - start < 3.0:
                continue
            if not text:
                continue
            item.update(text=text, start=start, end=end)
            result.append(item)
            previous = text
        return result


def warning_for(segment: dict[str, object], maximum_characters: int = 42) -> str | None:
    text = str(segment.get("text", ""))
    confidence = segment.get("confidence")
    if isinstance(confidence, (int, float)) and confidence < 0.45:
        return "認識精度が低い可能性があります"
    if len(text) > maximum_characters:
        return f"1行{len(text)}文字です。分割を検討してください"
    return None
