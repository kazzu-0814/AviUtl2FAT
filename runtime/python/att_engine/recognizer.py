from __future__ import annotations

from dataclasses import dataclass
from dataclasses import replace
from pathlib import Path
from typing import Iterator
import time
import tempfile
import wave

from .errors import AttError
from .progress import emit
from .cancellation import throw_if_cancelled
from .model_service import MODEL_REPOS, model_path, validate

FILLERS = ("あのー", "そのー", "えっと", "うーん", "えー", "あの", "その", "まあ", "なんか")


@dataclass(frozen=True)
class RecognitionOptions:
    model: str
    language: str | None
    device: str
    compute_type: str
    vad: bool
    model_dir: Path
    remove_spaces: bool
    remove_fillers: bool
    cancel_file: Path | None = None
    beam_size: int = 5
    best_of: int = 5
    temperature: float = 0.0
    word_timestamps: bool = False
    condition_on_previous_text: bool = True
    initial_prompt: str = ""
    cpu_threads: int = 0


def clean_text(text: str, remove_spaces: bool, remove_fillers: bool) -> str:
    value = text.strip()
    if remove_fillers:
        for filler in FILLERS:
            value = value.replace(filler, "")
    if remove_spaces:
        value = value.replace(" ", "").replace("\u3000", "")
    return value.strip()


class FasterWhisperRecognizer:
    # A FAT Python engine is intentionally long-lived.  Reusing a loaded model
    # avoids the largest fixed cost of a second subtitle generation and also
    # lets bounded speech-recovery reuse the primary recognition model.
    _model_cache: dict[tuple[str, str, str, int], object] = {}

    def __init__(self, options: RecognitionOptions) -> None:
        self.options = options

    @staticmethod
    def _word_boundary(segment: object) -> tuple[float | None, float | None]:
        words = getattr(segment, "words", None)
        if not words:
            return None, None
        starts: list[float] = []
        ends: list[float] = []
        for word in words:
            start = getattr(word, "start", None)
            end = getattr(word, "end", None)
            if start is None or end is None:
                continue
            try:
                start_value = float(start)
                end_value = float(end)
            except (TypeError, ValueError):
                continue
            if end_value > start_value:
                starts.append(start_value)
                ends.append(end_value)
        if not starts or not ends:
            return None, None
        return min(starts), max(ends)

    def recognize(self, audio_path: Path, total_seconds: float = 0) -> tuple[Iterator[dict[str, object]], str | None]:
        if self.options.model not in MODEL_REPOS:
            raise AttError("FAT_SPEECH_MODEL_INVALID", f"未対応の音声認識モデルです: {self.options.model}")
        # Passing a model name to faster-whisper starts an implicit Hugging Face
        # download.  That can take a long time without progress events, which
        # previously left FAT stuck at the profile stage.  FAT only loads an
        # explicitly installed and validated local model.
        local_model = model_path(self.options.model_dir, self.options.model)
        validation = validate(self.options.model_dir, self.options.model, self.options.cancel_file, load=False)
        if not validation.get("valid"):
            missing = ", ".join(str(item) for item in validation.get("missing", []))
            detail = f" 不足: {missing}" if missing else ""
            raise AttError(
                "FAT_SPEECH_MODEL_NOT_INSTALLED",
                f"音声認識モデル '{self.options.model}' は未導入です。モデル管理から明示的にダウンロードしてください。保存先: {local_model}.{detail}",
            )
        try:
            from faster_whisper import WhisperModel
        except ImportError as error:
            raise AttError("DEPENDENCY_MISSING", "faster-whisperが未導入です。requirements.txtをインストールしてください") from error
        device = self.options.device
        compute = self.options.compute_type
        if device == "auto":
            try:
                import ctranslate2
                device = "cuda" if ctranslate2.get_cuda_device_count() > 0 else "cpu"
            except Exception:
                device = "cpu"
            if device == "cpu" and compute in ("float16", "int8_float16"):
                compute = "int8"
                emit("status", message="CUDAを利用できないためCPU/int8へ切り替えました")
        cache_key = (str(local_model.resolve()), device, compute, self.options.cpu_threads)
        try:
            throw_if_cancelled(self.options.cancel_file)
            model = self._model_cache.get(cache_key)
            if model is None:
                emit("status", message=f"モデルを読み込んでいます ({self.options.model}, {device}/{compute})")
                model = WhisperModel(str(local_model), device=device, compute_type=compute,
                                     cpu_threads=self.options.cpu_threads)
                # Keep only the active model.  Low-spec systems must not retain
                # several Whisper instances after a user changes profile.
                self._model_cache.clear()
                self._model_cache[cache_key] = model
            raw_segments, info = model.transcribe(str(audio_path), language=self.options.language,
                                                  beam_size=self.options.beam_size,best_of=self.options.best_of,
                                                  temperature=self.options.temperature,vad_filter=self.options.vad,
                                                  word_timestamps=self.options.word_timestamps,
                                                  condition_on_previous_text=self.options.condition_on_previous_text,
                                                  initial_prompt=self.options.initial_prompt or None)
        except Exception as error:
            if self.options.device == "auto" and device == "cuda":
                emit("status", message=f"CUDAの初期化に失敗したためCPU/int8で再試行します: {error}")
                try:
                    device, compute = "cpu", "int8"
                    fallback_key = (str(local_model.resolve()), device, compute, self.options.cpu_threads)
                    model = self._model_cache.get(fallback_key)
                    if model is None:
                        model = WhisperModel(str(local_model), device=device, compute_type=compute,cpu_threads=self.options.cpu_threads)
                        self._model_cache.clear()
                        self._model_cache[fallback_key] = model
                    raw_segments, info = model.transcribe(str(audio_path), language=self.options.language,
                                                          beam_size=self.options.beam_size,best_of=self.options.best_of,temperature=self.options.temperature,
                                                          vad_filter=self.options.vad,word_timestamps=self.options.word_timestamps,
                                                          condition_on_previous_text=self.options.condition_on_previous_text,initial_prompt=self.options.initial_prompt or None)
                except Exception as fallback_error:
                    raise AttError("ATT_MODEL_LOAD_FAILED", f"CUDAおよびCPUでモデルを開始できません: {fallback_error}") from fallback_error
            else:
                raise AttError("ATT_MODEL_LOAD_FAILED", f"モデルの読み込みまたは認識開始に失敗しました: {error}") from error

        def stream() -> Iterator[dict[str, object]]:
            try:
                throw_if_cancelled(self.options.cancel_file)
                started = time.monotonic()
                max_progress = 35.0
                for index, segment in enumerate(raw_segments, start=1):
                    throw_if_cancelled(self.options.cancel_file)
                    original = segment.text.strip(); text = clean_text(original, self.options.remove_spaces, self.options.remove_fillers)
                    word_start, word_end = self._word_boundary(segment)
                    start = float(segment.start)
                    end = float(segment.end)
                    if word_start is not None and word_end is not None:
                        # faster-whisper segment boundaries can be broad and
                        # sometimes touch the next segment, which makes FAT
                        # appear to erase silent gaps.  When word timestamps are
                        # available, use the actual first/last spoken word as
                        # the subtitle timing authority.
                        start = max(0.0, word_start)
                        end = max(start + 0.01, word_end)
                    average_logprob = getattr(segment, "avg_logprob", None)
                    no_speech = getattr(segment, "no_speech_prob", None)
                    # avg_logprob is negative; map it conservatively to 0..1 for UI warnings.
                    confidence = None if average_logprob is None else max(0.0, min(1.0, (float(average_logprob) + 2.0) / 2.0))
                    item = {"id": index, "start": start, "end": end, "segment_start": float(segment.start), "segment_end": float(segment.end), "original_text": original, "text": text, "enabled": True,
                            "avg_logprob": average_logprob, "no_speech_probability": no_speech, "confidence": confidence}
                    emit("segment", **item)
                    processed = max(0.0, float(segment.end))
                    if total_seconds > 0:
                        stage_percent = min(100.0, processed / total_seconds * 100.0)
                        progress = 35.0 + min(57.0, stage_percent * 0.57)
                    else:
                        stage_percent = None
                        progress = None
                    if progress is not None:
                        max_progress = max(max_progress, progress)
                    elapsed = max(0.0, time.monotonic() - started)
                    remaining = None
                    if total_seconds > 0 and processed > 0 and (index >= 3 or elapsed >= 8.0):
                        rate = processed / max(elapsed, 0.001)
                        remaining = max(0.0, (total_seconds - processed) / rate) if rate > 0 else None
                    emit("transcription_progress", stage="transcribing", value=round(max_progress, 1) if progress is not None else None,
                         processed_seconds=processed, total_seconds=total_seconds if total_seconds > 0 else None,
                         stage_percent=round(stage_percent, 1) if stage_percent is not None else None,
                         overall_percent=round(max_progress, 1) if progress is not None else None,
                         segments_created=index, elapsed_seconds=round(elapsed, 1),
                         estimated_remaining_seconds=round(remaining, 1) if remaining is not None else None,
                         message="音声を認識しています")
                    yield item
            except Exception as error:
                raise AttError("TRANSCRIPTION_FAILED", f"音声認識に失敗しました: {error}") from error

        return stream(), getattr(info, "language", self.options.language)

    def recognize_window(self, source_wav: Path, start_seconds: float, end_seconds: float) -> tuple[list[dict[str, object]], str | None]:
        """Recognize one already-vetted WAV interval without VAD re-segmentation.

        This intentionally creates a short temporary WAV.  It keeps recovery
        bounded and avoids decoding the original media again.
        """
        if end_seconds <= start_seconds:
            return [], self.options.language
        with wave.open(str(source_wav), "rb") as source:
            rate = source.getframerate()
            start_frame = max(0, min(source.getnframes(), int(start_seconds * rate)))
            end_frame = max(start_frame, min(source.getnframes(), int(end_seconds * rate)))
            with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temporary:
                clip_path = Path(temporary.name)
            try:
                with wave.open(str(clip_path), "wb") as output:
                    output.setparams(source.getparams())
                    source.setpos(start_frame)
                    output.writeframes(source.readframes(end_frame - start_frame))
                # The candidate was selected by energy and conservative VAD
                # checks, so a second VAD pass could wrongly remove weak speech.
                recovery = FasterWhisperRecognizer(replace(self.options, vad=False, condition_on_previous_text=False))
                stream, language = recovery.recognize(clip_path, 0)
                values = []
                for item in stream:
                    item["start"] = round(float(item["start"]) + start_seconds, 3)
                    item["end"] = round(float(item["end"]) + start_seconds, 3)
                    item["segment_start"] = round(float(item["segment_start"]) + start_seconds, 3)
                    item["segment_end"] = round(float(item["segment_end"]) + start_seconds, 3)
                    values.append(item)
                return values, language
            finally:
                clip_path.unlink(missing_ok=True)
