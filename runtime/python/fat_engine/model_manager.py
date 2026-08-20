from __future__ import annotations

import gc
import os
import shutil
import subprocess
import sys
from dataclasses import asdict, dataclass
from enum import StrEnum
from pathlib import Path
from typing import Callable


class ModelState(StrEnum):
    NOT_INSTALLED = "NotInstalled"; DOWNLOADING = "Downloading"; INSTALLED = "Installed"
    LOADING = "Loading"; READY = "Ready"; UNLOADING = "Unloading"; INCOMPLETE = "Incomplete"; BROKEN = "Broken"; ERROR = "Error"


class ModelVariant(StrEnum):
    FULL = "Full"; QUANTIZED = "Quantized"; EXTERNAL = "External"


class ModelRecommendation(StrEnum):
    RECOMMENDED = "Recommended"; POSSIBLE = "Possible"; NOT_RECOMMENDED = "NotRecommended"; UNSUPPORTED = "Unsupported"


@dataclass(frozen=True)
class ModelDefinition:
    id: str; display_name: str; provider: str; source: str; local_directory: str
    estimated_download_bytes: int; license_url: str
    required_files: tuple[str, ...] = ("config.json",)
    description: str = "Instruction Tuned"
    variant: ModelVariant = ModelVariant.FULL
    architecture: str = "Gemma 4"
    recommended_ram_bytes: int = 16_000_000_000
    recommended_vram_bytes: int = 0
    supports_cpu: bool = True
    supports_cuda: bool = True
    supports_audio: bool = False
    gated: bool = True


class ModelRegistry:
    """Local metadata only. Building/status never contacts Hugging Face."""
    def __init__(self, models_root: Path) -> None:
        self.models_root = models_root
        gb = 1_000_000_000
        self._items = {
            "gemma-4-e2b-it": ModelDefinition("gemma-4-e2b-it", "Gemma 4 E2B", "Google", "google/gemma-4-E2B-it", "gemma-4-e2b-it", int(10.3 * gb), "https://huggingface.co/google/gemma-4-E2B-it", recommended_ram_bytes=16 * gb, recommended_vram_bytes=12 * gb, supports_audio=True),
            "gemma-4-e4b-it": ModelDefinition("gemma-4-e4b-it", "Gemma 4 E4B", "Google", "google/gemma-4-E4B-it", "gemma-4-e4b-it", 18 * gb, "https://huggingface.co/google/gemma-4-E4B-it", recommended_ram_bytes=24 * gb, recommended_vram_bytes=16 * gb, supports_audio=True),
            "gemma-4-12b-it": ModelDefinition("gemma-4-12b-it", "Gemma 4 12B", "Google", "google/gemma-4-12B-it", "gemma-4-12b-it", 28 * gb, "https://huggingface.co/google/gemma-4-12B-it", recommended_ram_bytes=40 * gb, recommended_vram_bytes=24 * gb, supports_audio=True),
            "gemma-4-26b-a4b-it": ModelDefinition("gemma-4-26b-a4b-it", "Gemma 4 26B A4B", "Google", "google/gemma-4-26B-A4B-it", "gemma-4-26b-a4b-it", 55 * gb, "https://huggingface.co/google/gemma-4-26B-A4B-it", recommended_ram_bytes=72 * gb, recommended_vram_bytes=48 * gb),
        }
    def get(self, model_id: str) -> ModelDefinition:
        if model_id not in self._items: raise ValueError(f"MODEL_UNKNOWN: {model_id}")
        return self._items[model_id]
    def all(self) -> list[ModelDefinition]: return list(self._items.values())
    def path_for(self, definition: ModelDefinition) -> Path: return self.models_root / definition.local_directory


class DeviceManager:
    _cached: dict[str, object] | None = None
    @staticmethod
    def status() -> dict[str, object]:
        # Importing torch can be expensive on Windows.  The persistent worker's
        # hardware does not change during one FAT session, so probe once only.
        if DeviceManager._cached is not None: return dict(DeviceManager._cached)
        result: dict[str, object] = {"cpu": os.cpu_count() or 1, "cuda": False, "gpu": None, "vram_bytes": None, "selected": "cpu", "ram_total_bytes": None, "ram_available_bytes": None}
        try:
            import psutil
            memory = psutil.virtual_memory(); result["ram_total_bytes"] = memory.total; result["ram_available_bytes"] = memory.available
        except Exception: pass
        # Do not import torch merely to paint a model-manager card: on some
        # systems that probe can stall for tens of seconds.  A real
        # Transformers load imports torch itself; an already-loaded torch is
        # still used here for exact CUDA/VRAM information.
        torch = sys.modules.get("torch")
        try:
            if torch is not None:
                result["cuda"] = bool(torch.cuda.is_available())
                if result["cuda"]: result.update(selected="cuda", gpu=torch.cuda.get_device_name(0), vram_bytes=int(torch.cuda.get_device_properties(0).total_memory))
        except Exception: pass
        DeviceManager._cached = result
        return dict(result)


class IModelDownloader:
    def download(self, definition: ModelDefinition, destination: Path, progress: Callable[[dict[str, object]], None], cancel: Callable[[], bool]) -> None: raise NotImplementedError


class HuggingFaceModelDownloader(IModelDownloader):
    """Uses only the user's existing Hugging Face login; invoked solely by model.download."""
    def download(self, definition, destination, progress, cancel) -> None:
        destination.parent.mkdir(parents=True, exist_ok=True)
        cli = Path(sys.executable).parent / ("hf.exe" if os.name == "nt" else "hf")
        process = subprocess.Popen([str(cli) if cli.is_file() else "hf", "download", definition.source, "--local-dir", str(destination)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
        assert process.stdout is not None
        for line in process.stdout:
            if cancel(): process.terminate(); process.wait(timeout=10); raise RuntimeError("MODEL_DOWNLOAD_CANCELLED")
            downloaded = sum(item.stat().st_size for item in destination.rglob("*") if item.is_file()) if destination.exists() else 0
            progress({"state": ModelState.DOWNLOADING, "message": line.strip(), "current_file": None, "downloaded_bytes": downloaded, "total_bytes": definition.estimated_download_bytes, "percent": min(99.0, downloaded * 100.0 / definition.estimated_download_bytes)})
        if process.wait() != 0: raise RuntimeError("MODEL_DOWNLOAD_FAILED: confirm Hugging Face access conditions and sign-in")


class ModelManager:
    def __init__(self, models_root: Path, downloader: IModelDownloader | None = None, device_status: Callable[[], dict[str, object]] | None = None) -> None:
        self.registry = ModelRegistry(models_root); self.downloader = downloader or HuggingFaceModelDownloader(); self._device_status = device_status or DeviceManager.status
        self._loaded: dict[str, object] = {}; self._state: dict[str, ModelState] = {}
    def validate(self, model_id: str) -> dict[str, object]:
        definition = self.registry.get(model_id); path = self.registry.path_for(definition)
        if not path.exists(): return {"valid": False, "state": ModelState.NOT_INSTALLED, "missing": list(definition.required_files), "path": str(path)}
        missing = [file for file in definition.required_files if not (path / file).is_file()]
        if not any((path / item).is_file() for item in ("processor_config.json", "preprocessor_config.json")): missing.append("processor configuration")
        if not any((path / item).is_file() for item in ("tokenizer.json", "tokenizer.model", "tokenizer_config.json")): missing.append("tokenizer configuration")
        weights = list(path.glob("*.safetensors")) + list(path.glob("pytorch_model*.bin"))
        if not missing and not weights: missing.append("model weights (*.safetensors or pytorch_model*.bin)")
        state = ModelState.INSTALLED if not missing else ModelState.INCOMPLETE; self._state[model_id] = state
        return {"valid": not missing, "state": state, "missing": missing, "path": str(path), "bytes": sum(p.stat().st_size for p in path.rglob("*") if p.is_file())}
    def compatibility(self, model_id: str) -> dict[str, object]:
        definition, device = self.registry.get(model_id), self._device_status()
        disk_root = self.registry.models_root if self.registry.models_root.exists() else self.registry.models_root.parent
        free_disk = shutil.disk_usage(disk_root).free; ram = device.get("ram_available_bytes"); cuda = bool(device.get("cuda")); vram = device.get("vram_bytes")
        disk_ok = free_disk >= int(definition.estimated_download_bytes * 1.1); ram_ok = isinstance(ram, int) and ram >= definition.recommended_ram_bytes; cuda_ok = cuda and isinstance(vram, int) and vram >= definition.recommended_vram_bytes
        if not disk_ok: recommendation, reason = ModelRecommendation.UNSUPPORTED, "DISK_INSUFFICIENT"
        elif cuda_ok: recommendation, reason = ModelRecommendation.RECOMMENDED, "CUDA_READY"
        elif ram_ok and definition.supports_cpu: recommendation, reason = ModelRecommendation.POSSIBLE, "CPU_MEMORY_OK"
        elif definition.supports_cpu: recommendation, reason = ModelRecommendation.NOT_RECOMMENDED, "MEMORY_INSUFFICIENT"
        else: recommendation, reason = ModelRecommendation.UNSUPPORTED, "DEVICE_UNSUPPORTED"
        return {"recommendation": recommendation, "reason": reason, "disk_free_bytes": free_disk, "disk_required_bytes": int(definition.estimated_download_bytes * 1.1), "ram_available_bytes": ram, "ram_required_bytes": definition.recommended_ram_bytes, "vram_bytes": vram, "vram_required_bytes": definition.recommended_vram_bytes, "device": device.get("selected", "cpu")}
    def status(self, model_id: str | None = None) -> object:
        if model_id is None: return [self.status(item.id) for item in self.registry.all()]
        definition = self.registry.get(model_id); checked = self.validate(model_id); state = ModelState.READY if model_id in self._loaded else self._state.get(model_id, checked["state"])
        return {**asdict(definition), **checked, "state": state, "loaded": model_id in self._loaded, "local_path": str(self.registry.path_for(definition)), "compatibility": self.compatibility(model_id), "device": self._device_status()}
    def _assert_load_safe(self, model_id: str) -> None:
        compatibility = self.compatibility(model_id)
        if compatibility["recommendation"] in (ModelRecommendation.NOT_RECOMMENDED, ModelRecommendation.UNSUPPORTED): raise RuntimeError("MODEL_INSUFFICIENT_MEMORY: " + str(compatibility["reason"]))
    def load_model(self, model_id: str, factory: Callable[[Path], object], enforce_memory: bool = True) -> dict[str, object]:
        checked = self.validate(model_id)
        if not checked["valid"]: raise RuntimeError("MODEL_NOT_INSTALLED")
        if enforce_memory: self._assert_load_safe(model_id)
        if model_id in self._loaded: return self.status(model_id)
        for other in list(self._loaded): self.unload_model(other)
        self._state[model_id] = ModelState.LOADING
        try: self._loaded[model_id] = factory(Path(str(checked["path"]))); self._state[model_id] = ModelState.READY
        except Exception: self._state[model_id] = ModelState.ERROR; raise
        return self.status(model_id)
    def unload_model(self, model_id: str) -> dict[str, object]: self._state[model_id] = ModelState.UNLOADING; self._loaded.pop(model_id, None); gc.collect(); return self.status(model_id)
    def get_loaded_model(self, model_id: str) -> object | None: return self._loaded.get(model_id)
    def is_loaded(self, model_id: str) -> bool: return model_id in self._loaded
    def get_memory_status(self) -> dict[str, object]: return self._device_status()
    def download(self, model_id, progress, cancel) -> dict[str, object]:
        definition = self.registry.get(model_id); compatibility = self.compatibility(model_id)
        if int(compatibility["disk_free_bytes"]) < int(compatibility["disk_required_bytes"]): raise RuntimeError("MODEL_DISK_SPACE_INSUFFICIENT")
        destination = self.registry.path_for(definition); self._state[model_id] = ModelState.DOWNLOADING
        try:
            self.downloader.download(definition, destination, progress, cancel)
            if not self.validate(model_id)["valid"]: raise RuntimeError("MODEL_DOWNLOAD_INCOMPLETE")
            return self.status(model_id)
        except Exception: self._state[model_id] = ModelState.INCOMPLETE if destination.exists() else ModelState.ERROR; raise
