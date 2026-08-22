"""Local, bounded recovery of likely missed speech between accepted captions.

Energy never creates a caption.  It only chooses a small number of gaps that
receive one additional faster-whisper pass, whose text and confidence are then
validated before it can be merged with the original recognition result.
"""
from __future__ import annotations

import math
import re
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


@dataclass(frozen=True)
class SpeechRecoverySettings:
    mode: str = "auto"
    min_gap_seconds: float = 2.2
    padding_seconds: float = 0.22
    maximum_candidates: int = 3
    minimum_rms_db: float = -38.0
    minimum_active_fraction: float = 0.18
    minimum_log_probability: float = -1.2

    @property
    def enabled(self) -> bool:
        """Whether the bounded recovery pass is permitted for this profile.

        `mode=none` must be a true opt-out, even if a settings object was
        reconstructed from an older configuration that does not carry an
        explicit `enabled` field.
        """
        return self.mode != "none" and self.maximum_candidates > 0

    @staticmethod
    def select(mode: str | None) -> "SpeechRecoverySettings":
        value = (mode or "auto").lower()
        if value == "none":
            return SpeechRecoverySettings("none", maximum_candidates=0)
        if value == "speech_priority":
            return SpeechRecoverySettings("speech_priority", min_gap_seconds=1.4, padding_seconds=0.30,
                                          maximum_candidates=5, minimum_rms_db=-42.0, minimum_active_fraction=0.14,
                                          minimum_log_probability=-1.45)
        return SpeechRecoverySettings()


@dataclass(frozen=True)
class SpeechRecoveryCandidate:
    start: float
    end: float
    energy_db: float
    active_fraction: float
    vad_speech: bool
    reason: str


_hallucination = re.compile(r"^(?:ご視聴ありがとうございました|字幕(?:をご覧)?いただき|music|♪)+[。!！ ]*$", re.I)


def _windows(samples: bytes, sample_width: int, channels: int, sample_rate: int, start_frame: int, end_frame: int) -> tuple[float, float, float]:
    """Return RMS dBFS, active fraction and a conservative voice-activity proxy."""
    if sample_width != 2 or channels < 1 or end_frame <= start_frame:
        return -120.0, 0.0, 0.0
    frame_bytes = sample_width * channels
    window = max(1, int(sample_rate * 0.10))
    rms_values: list[float] = []
    crossings: list[float] = []
    for frame in range(start_frame, end_frame, window):
        chunk = samples[frame * frame_bytes:min(end_frame, frame + window) * frame_bytes]
        if len(chunk) < frame_bytes: continue
        values = memoryview(chunk).cast("h")[::channels]
        if not values: continue
        mean_square = sum(int(value) * int(value) for value in values) / len(values)
        rms_values.append(math.sqrt(mean_square) / 32768.0)
        crossings.append(sum(1 for previous, current in zip(values, values[1:]) if (previous < 0) != (current < 0)) / max(1, len(values) - 1))
    if not rms_values: return -120.0, 0.0, 0.0
    rms = math.sqrt(sum(value * value for value in rms_values) / len(rms_values))
    db = 20 * math.log10(max(rms, 1e-6))
    active = sum(value >= 0.012 for value in rms_values) / len(rms_values)
    return db, active, sum(crossings) / len(crossings)


def find_candidates(wav_path: Path, accepted_segments: Iterable[dict[str, object]], duration: float, settings: SpeechRecoverySettings) -> list[SpeechRecoveryCandidate]:
    if settings.maximum_candidates <= 0 or not wav_path.is_file() or duration <= 0:
        return []
    ordered = sorted(accepted_segments, key=lambda item: (float(item.get("start", 0)), float(item.get("end", 0))))
    boundaries = [(0.0, float(ordered[0].get("start", 0))) ] if ordered else [(0.0, duration)]
    boundaries += [(float(left.get("end", 0)), float(right.get("start", 0))) for left, right in zip(ordered, ordered[1:])]
    if ordered: boundaries.append((float(ordered[-1].get("end", 0)), duration))
    try:
        with wave.open(str(wav_path), "rb") as source:
            if source.getcomptype() != "NONE": return []
            samples = source.readframes(source.getnframes())
            rate, width, channels = source.getframerate(), source.getsampwidth(), source.getnchannels()
    except (OSError, wave.Error):
        return []
    candidates: list[SpeechRecoveryCandidate] = []
    for start, end in boundaries:
        if end - start < settings.min_gap_seconds: continue
        start = max(0.0, start); end = min(duration, end)
        energy, active, crossing = _windows(samples, width, channels, rate, int(start * rate), int(end * rate))
        vad_speech = active >= settings.minimum_active_fraction and crossing >= 0.015
        if energy >= settings.minimum_rms_db and vad_speech:
            candidates.append(SpeechRecoveryCandidate(start, end, round(energy, 2), round(active, 3), True, "energy+vad"))
    return candidates[:settings.maximum_candidates]


def recovery_window(candidate: SpeechRecoveryCandidate, duration: float, settings: SpeechRecoverySettings) -> tuple[float, float]:
    return max(0.0, candidate.start - settings.padding_seconds), min(duration, candidate.end + settings.padding_seconds)


def is_recoverable(segment: dict[str, object], settings: SpeechRecoverySettings) -> bool:
    text = str(segment.get("text", "")).strip()
    log_probability = float(segment.get("avg_logprob", -99) or -99)
    no_speech = float(segment.get("no_speech_probability", 1) or 1)
    return bool(text) and not _hallucination.match(text) and log_probability >= settings.minimum_log_probability and no_speech < 0.82


def merge_recovered(existing: Iterable[dict[str, object]], recovered: Iterable[dict[str, object]], overlap_seconds: float = 0.25) -> list[dict[str, object]]:
    result = [dict(item) for item in existing]
    for candidate in recovered:
        text = str(candidate.get("text", "")).strip()
        start, end = float(candidate.get("start", 0)), float(candidate.get("end", 0))
        duplicate = any(str(item.get("text", "")).strip() == text and min(end, float(item.get("end", 0))) - max(start, float(item.get("start", 0))) > overlap_seconds for item in result)
        if text and end > start and not duplicate: result.append(dict(candidate))
    return sorted(result, key=lambda item: (float(item.get("start", 0)), float(item.get("end", 0)), str(item.get("text", ""))))
