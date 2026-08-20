from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path
from typing import Any

from fat_engine import PROTOCOL, VERSION
from fat_engine.models import Caption
from fat_engine.providers import ProviderRegistry
from fat_engine.adaptive import NeedAiDetector, RuleProcessor, SmartSplitter, classify_runtime


# FAT IPC is UTF-8 JSON Lines.  Explicitly override the Windows console code
# page so Japanese progress/error messages cannot fail with CP932 encoding.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
from fat_engine.model_manager import ModelManager


def emit(message_type: str, request_id: str | None, payload: object | None = None, error: dict[str, str] | None = None) -> None:
    print(json.dumps({"protocol": PROTOCOL, "version": VERSION, "id": request_id, "type": message_type, "payload": payload, "error": error}, ensure_ascii=False), flush=True)


def captions(payload: dict[str, Any]) -> list[Caption]:
    return [Caption(str(item["id"]), float(item["start_time"]), float(item["end_time"]), str(item.get("original_transcript", item.get("text", ""))), str(item.get("text", "")), str(item.get("provider", "passthrough")), item.get("model"), item.get("confidence")).validate() for item in payload.get("captions", [])]


def generate(message: dict[str, Any], registry: ProviderRegistry) -> None:
    payload = message.get("payload") or {}
    provider = registry.get(str(payload.get("provider", "passthrough")))
    operation = str(message.get("type", "ai.generate")).split(".")[-1]
    method = getattr(provider, operation, provider.generate)
    payload.setdefault("operation", message.get("type", "ai.generate"))
    payload.setdefault("output_language", payload.get("outputLanguage", "ja"))
    result = method(captions(payload), payload)
    emit("result", message.get("id"), {"captions": [item.to_dict() for item in result], "provider": provider.id})


def recognize(message: dict[str, Any]) -> None:
    payload = message.get("payload") or {}
    required = ("python", "script", "input", "output", "ffmpeg", "ffprobe", "model_dir")
    missing = [key for key in required if not payload.get(key)]
    if missing: raise ValueError("speech.recognize is missing: " + ", ".join(missing))
    command = [str(payload["python"]), str(payload["script"]), "--input", str(payload["input"]), "--output", str(payload["output"]), "--model", str(payload.get("model", "small")), "--language", str(payload.get("language", "ja")), "--device", str(payload.get("device", "auto")), "--compute-type", str(payload.get("compute_type", "auto")), "--profile", str(payload.get("profile", "auto")), "--audio-enhancement", str(payload.get("audio_enhancement", "auto")), "--filler-mode", str(payload.get("filler_mode", "auto")), "--dictionary", str(payload.get("dictionary", "")), "--fps", str(payload.get("fps", 30)), "--ffmpeg", str(payload["ffmpeg"]), "--ffprobe", str(payload["ffprobe"]), "--model-dir", str(payload["model_dir"]), "--vad"]
    environment = dict(os.environ, PYTHONUTF8="1", PYTHONIOENCODING="utf-8")
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace", env=environment)
    assert process.stdout is not None
    legacy_error: dict[str, object] | None = None
    for line in process.stdout:
        try: event = json.loads(line)
        except json.JSONDecodeError: continue
        if event.get("type") == "error":
            legacy_error = event
        emit("progress", message.get("id"), event)
    stderr = process.stderr.read() if process.stderr else ""
    if process.wait() != 0:
        if legacy_error:
            code = str(legacy_error.get("code", "ATT_RECOGNITION_FAILED"))
            detail = str(legacy_error.get("message", "legacy recognition failed"))
            raise RuntimeError(f"{code}: {detail}")
        raise RuntimeError(stderr[-2000:] or "legacy recognition failed")
    emit("result", message.get("id"), {"output": str(payload["output"])})


def health(registry: ProviderRegistry, manager: ModelManager) -> dict[str, object]:
    available = manager.get_memory_status().get("ram_available_bytes")
    cuda = False
    try:
        import ctranslate2
        cuda = ctranslate2.get_cuda_device_count() > 0
    except Exception:
        pass
    return {"state": "ready", "python": sys.version.split()[0], "ffmpeg": True,
            "ai_registry": registry.status(), "logical_cores": os.cpu_count(),
            "ram_available_bytes": available, "cuda": cuda,
            "adaptive_profile": classify_runtime(os.cpu_count(), available if isinstance(available, int) else None, cuda).__dict__}


def process_captions(message: dict[str, Any]) -> None:
    payload = message.get("payload") or {}
    language = str(payload.get("language", "")) or None
    mode = str(payload.get("ai_mode", "auto"))
    cleaned = RuleProcessor().process(captions(payload), str(payload.get("filler_mode", "auto")), language)
    needs_ai = {item.id for item in NeedAiDetector().select(cleaned, mode)}
    emit("result", message.get("id"), {"captions": [item.to_dict() for item in cleaned], "need_ai_ids": sorted(needs_ai), "total": len(cleaned), "ai_candidates": len(needs_ai)})


def split_captions(message: dict[str, Any]) -> None:
    payload = message.get("payload") or {}
    if str(payload.get("mode", "auto")) == "none":
        values = captions(payload)
    else:
        splitter = SmartSplitter(); values = []
        for caption in captions(payload): values.extend(splitter.split(caption, int(payload.get("max_characters", 24)), int(payload.get("max_lines", 2)), float(payload.get("minimum_seconds", 0.8))))
    emit("result", message.get("id"), {"captions": [item.to_dict() for item in values]})


def main() -> int:
    manager = ModelManager(Path.cwd() / "models")
    registry = ProviderRegistry(manager)
    for line in sys.stdin:
        try:
            message = json.loads(line)
            if message.get("protocol") != PROTOCOL or message.get("version") != VERSION: raise ValueError("unsupported fat-python protocol")
            message_type = message.get("type")
            if message_type == "engine.status": emit("result", message.get("id"), {"providers": registry.status(), "models": manager.status(), "memory": manager.get_memory_status()})
            elif message_type == "engine.health": emit("result", message.get("id"), health(registry, manager))
            elif message_type == "caption.process": process_captions(message)
            elif message_type == "caption.split": split_captions(message)
            elif message_type == "model.status": emit("result", message.get("id"), manager.status((message.get("payload") or {}).get("model_id")))
            elif message_type == "model.validate": emit("result", message.get("id"), manager.validate(str((message.get("payload") or {}).get("model_id", "gemma-4-e2b-it"))))
            elif message_type == "model.load": emit("result", message.get("id"), registry.get("gemma").load(str((message.get("payload") or {}).get("model_id", "gemma-4-e2b-it"))))
            elif message_type == "model.unload": emit("result", message.get("id"), manager.unload_model(str((message.get("payload") or {}).get("model_id", "gemma-4-e2b-it"))))
            elif message_type == "model.download":
                payload = message.get("payload") or {}
                if payload.get("confirmed") is not True: raise ValueError("MODEL_DOWNLOAD_REQUIRES_CONFIRMATION")
                def progress(event: dict[str, object]) -> None: emit("progress", message.get("id"), event)
                emit("result", message.get("id"), manager.download(str(payload.get("model_id", "gemma-4-e2b-it")), progress, lambda: False))
            elif message_type in {"ai.generate", "ai.rewrite", "ai.shorten", "ai.naturalize"}: generate(message, registry)
            elif message.get("type") == "speech.recognize": recognize(message)
            else: raise ValueError("unsupported command")
        except Exception as error:
            emit("error", message.get("id") if "message" in locals() else None, error={"code": "FAT_PYTHON_ERROR", "message": str(error)})
    return 0


if __name__ == "__main__": raise SystemExit(main())
