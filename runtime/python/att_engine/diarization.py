"""Optional local audio speaker diarization without downloads or text guessing."""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

from .cancellation import throw_if_cancelled


class DiarizationUnavailable(RuntimeError): pass


@dataclass(frozen=True)
class SpeakerTurn:
    start: float
    end: float
    speaker_id: str
    confidence: float | None = None


def normalize_speaker(value: object) -> str:
    text = str(value or "A").strip().upper()
    return text if len(text) == 1 and "A" <= text <= "Z" else "A"


def align_segments(segments: Iterable[dict[str, object]], turns: Iterable[SpeakerTurn], fallback: str = "A") -> list[dict[str, object]]:
    values = list(segments)
    ordered = sorted(turns, key=lambda turn: (turn.start, turn.end, turn.speaker_id))
    for segment in values:
        start = max(0.0, float(segment.get("start", 0)))
        end = max(start, float(segment.get("end", 0)))
        best, best_overlap = None, 0.0
        for turn in ordered:
            overlap = max(0.0, min(end, turn.end) - max(start, turn.start))
            if overlap > best_overlap:
                best, best_overlap = turn, overlap
        segment["speaker_id"] = normalize_speaker(best.speaker_id if best else fallback)
        segment["speaker"] = segment["speaker_id"]
    return values


class ISpeakerDiarizationProvider:
    id = "base"
    def available(self, settings: dict[str, Any]) -> bool: raise NotImplementedError
    def diarize(self, audio_path: Path, settings: dict[str, Any], cancel_file: Path | None = None) -> list[SpeakerTurn]: raise NotImplementedError


class DisabledSpeakerDiarizationProvider(ISpeakerDiarizationProvider):
    id = "disabled"
    def available(self, settings: dict[str, Any]) -> bool: return True
    def diarize(self, audio_path: Path, settings: dict[str, Any], cancel_file: Path | None = None) -> list[SpeakerTurn]: return []


class LocalSpeakerDiarizationProvider(ISpeakerDiarizationProvider):
    """Only runs pyannote.audio when both it and a local model are user-installed."""
    id = "local-pyannote"
    def _model(self, settings: dict[str, Any]) -> Path | None:
        value = str(settings.get("model_path") or "").strip()
        return Path(value) if value else None
    def available(self, settings: dict[str, Any]) -> bool:
        model = self._model(settings)
        if model is None or not model.exists(): return False
        try:
            import pyannote.audio  # type: ignore
            return bool(pyannote.audio)
        except ImportError: return False
    def diarize(self, audio_path: Path, settings: dict[str, Any], cancel_file: Path | None = None) -> list[SpeakerTurn]:
        if not self.available(settings):
            raise DiarizationUnavailable("Speaker diarization requires installed pyannote.audio and an existing local model path; using Speaker A.")
        throw_if_cancelled(cancel_file)
        from pyannote.audio import Pipeline  # type: ignore
        model = self._model(settings)
        assert model is not None
        pipeline = Pipeline.from_pretrained(str(model))  # local filesystem path only; no download
        diarization = pipeline(str(audio_path), num_speakers=max(2, min(6, int(settings.get("expected_speakers", 2)))))
        labels: dict[str, str] = {}; output: list[SpeakerTurn] = []
        for turn, _, label in diarization.itertracks(yield_label=True):
            throw_if_cancelled(cancel_file)
            label = str(label)
            labels.setdefault(label, chr(ord("A") + len(labels)) if len(labels) < 26 else "A")
            output.append(SpeakerTurn(max(0.0, float(turn.start)), max(0.0, float(turn.end)), labels[label]))
        return output


class SpeakerDiarizationProviderFactory:
    def __init__(self) -> None: self.disabled, self.local = DisabledSpeakerDiarizationProvider(), LocalSpeakerDiarizationProvider()
    def select(self, mode: str, settings: dict[str, Any]) -> ISpeakerDiarizationProvider:
        if str(mode or "off").lower() == "off": return self.disabled
        if self.local.available(settings): return self.local
        raise DiarizationUnavailable("Speaker diarization is enabled but no configured local provider is available; using Speaker A.")
