"""Recognition entry point for FAT's persistent Python engine.

The legacy ``recognize.py`` command remains available for diagnostics.  This
module runs the same pipeline in the already-running engine so the model and
safe per-session media caches are not discarded after every request.
"""
from __future__ import annotations

import hashlib
import tempfile
from pathlib import Path
from typing import Any

from .audio_converter import AudioConverter
from .cancellation import throw_if_cancelled
from .errors import AttError
from .media_probe import probe
from .model_service import validate as validate_model
from .output_writer import OutputWriter
from .recognizer import FasterWhisperRecognizer, RecognitionOptions
from .speech_pipeline import TranscriptPostProcessor, build_initial_prompt, select_profile, warning_for
from .speech_recovery import SpeechRecoverySettings, find_candidates, is_recoverable, merge_recovered, recovery_window


class RecognitionService:
    def __init__(self) -> None:
        self._probe_cache: dict[tuple[str, int, int], dict[str, object]] = {}
        self._audio_cache: dict[tuple[str, int, int, str], Path] = {}

    @staticmethod
    def _signature(path: Path) -> tuple[str, int, int]:
        stat = path.stat()
        return str(path.resolve()), stat.st_size, stat.st_mtime_ns

    def _probe(self, ffprobe: Path, source: Path) -> dict[str, object]:
        key = self._signature(source)
        cached = self._probe_cache.get(key)
        if cached is not None:
            return cached
        result = probe(ffprobe, source)
        self._probe_cache = {key: result}
        return result

    def _audio(self, ffmpeg: Path, source: Path, total_seconds: float, enhancement: str, cancel_file: Path | None) -> Path:
        source_key = (*self._signature(source), enhancement)
        cached = self._audio_cache.get(source_key)
        if cached is not None and cached.is_file() and cached.stat().st_size > 44:
            return cached
        cache_dir = Path(tempfile.gettempdir()) / "AviUtl2FAT" / "recognition-audio-cache"
        cache_dir.mkdir(parents=True, exist_ok=True)
        digest = hashlib.sha256("\0".join(map(str, source_key)).encode("utf-8")).hexdigest()[:24]
        output = cache_dir / f"{digest}.wav"
        AudioConverter(ffmpeg).convert(source, output, total_seconds, cancel_file, enhancement)
        # One active media cache is enough for the common retry/regenerate
        # workflow and prevents unbounded temporary WAV retention.
        for item in set(self._audio_cache.values()):
            if item != output:
                try:
                    item.unlink(missing_ok=True)
                except OSError:
                    pass
        self._audio_cache = {source_key: output}
        return output

    def recognize(self, payload: dict[str, Any]) -> dict[str, object]:
        required = ("input", "output", "ffmpeg", "ffprobe", "model_dir")
        missing = [name for name in required if not payload.get(name)]
        if missing:
            raise AttError("FAT_IPC_INVALID", "音声認識要求に不足があります: " + ", ".join(missing))
        source, output = Path(str(payload["input"])), Path(str(payload["output"]))
        ffmpeg, ffprobe, model_dir = Path(str(payload["ffmpeg"])), Path(str(payload["ffprobe"])), Path(str(payload["model_dir"]))
        if not source.is_file():
            raise AttError("FAT_INPUT_NOT_FOUND", f"入力ファイルが見つかりません: {source}")
        output.parent.mkdir(parents=True, exist_ok=True)
        model_dir.mkdir(parents=True, exist_ok=True)
        cancel_file = Path(str(payload["cancel_file"])) if payload.get("cancel_file") else None
        throw_if_cancelled(cancel_file)
        from .progress import emit
        emit("progress", stage="environment", value=5, message="実行環境を確認しています")
        media = self._probe(ffprobe, source)
        total_seconds = float(media.get("duration_seconds", 0))
        emit("progress", stage="probe", value=8, message="動画と音声を確認しました", audio=media.get("audio"))
        emit("progress", stage="convert", value=12, message="音声を変換しています", total_seconds=total_seconds)
        wav_path = self._audio(ffmpeg, source, total_seconds, str(payload.get("audio_enhancement", "auto")), cancel_file)
        emit("progress", stage="convert", value=20, message="音声変換が完了しました", total_seconds=total_seconds, cached=True)
        profile = select_profile(str(payload.get("profile", "auto")))
        requested_model = str(payload.get("model", "auto"))
        model = requested_model if requested_model != "auto" else profile.model
        profile_detail = profile.name
        if requested_model == "auto" and not validate_model(model_dir, model, cancel_file, load=False).get("valid"):
            if validate_model(model_dir, "small", cancel_file, load=False).get("valid"):
                model, profile_detail = "small", f"{profile.name}（導入済み small を使用）"
        device = str(payload.get("device", "auto")) if str(payload.get("device", "auto")) != "auto" else profile.device
        compute_type = str(payload.get("compute_type", "auto")) if str(payload.get("compute_type", "auto")) != "auto" else profile.compute_type
        language_value = str(payload.get("language", "auto"))
        dictionary = str(payload.get("dictionary", ""))
        options = RecognitionOptions(model, None if language_value == "auto" else language_value, device, compute_type,
                                     True, model_dir, False, False, cancel_file,
                                     profile.beam_size, 5, 0.0, True, False,
                                     build_initial_prompt(dictionary.split(",")), profile.cpu_threads)
        emit("progress", stage="profile", value=24, message=f"認識プロファイル: {profile_detail}", profile=profile.name, device=device, compute_type=compute_type, model=model)
        segments, language = FasterWhisperRecognizer(options).recognize(wav_path, total_seconds)
        collected = list(segments)
        filler_mode = str(payload.get("filler_mode", "auto"))
        collected = TranscriptPostProcessor().process(collected, total_seconds, language, filler_mode)
        recovery_settings = SpeechRecoverySettings.select(str(payload.get("speech_recovery", "auto")))
        if recovery_settings.enabled:
            emit("progress", stage="recovery", value=90, message="長い空白区間の認識漏れを確認しています")
            recovered: list[dict[str, object]] = []
            recognizer = FasterWhisperRecognizer(options)
            candidates = find_candidates(wav_path, collected, total_seconds, recovery_settings)
            for index, candidate in enumerate(candidates, start=1):
                throw_if_cancelled(cancel_file)
                emit("status", message=f"認識漏れ候補を確認しています ({index}/{len(candidates)})")
                start, end = recovery_window(candidate, total_seconds, recovery_settings)
                values, _ = recognizer.recognize_window(wav_path, start, end)
                cleaned = TranscriptPostProcessor().process(values, 0, language, filler_mode)
                recovered.extend(item for item in cleaned if candidate.start <= (float(item["start"]) + float(item["end"])) / 2 <= candidate.end and is_recoverable(item, recovery_settings))
            collected = merge_recovered(collected, recovered)
            emit("progress", stage="recovery", value=96, message=f"認識漏れ補正を完了しました（追加 {len(recovered)} 件）")
        for index, item in enumerate(collected, start=1):
            item["id"] = index
            item["warning"] = warning_for(item)
        writer = OutputWriter(float(payload.get("fps", 30)))
        result = writer.build_result(source.name, language, model, collected, total_seconds, device, compute_type)
        emit("progress", stage="save", value=98, message="出力を保存しています")
        writer.write_json(output, result)
        writer.write_text(output.with_suffix(".txt"), result["segments"])
        writer.write_srt(output.with_suffix(".srt"), result["segments"])
        emit("progress", stage="completed", value=100, message="完了")
        return {"output": str(output), "captions": len(collected), "audio_cached": True}
