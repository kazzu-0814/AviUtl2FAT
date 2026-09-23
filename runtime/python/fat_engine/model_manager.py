from __future__ import annotations

import ctypes
import errno
import json
import os
import shutil
import subprocess
import sys
import threading
import time
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


class ModelDownloadError(RuntimeError):
    """A download failure that can be shown to the user without losing its cause."""
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def _process_is_running(process_id: int) -> bool:
    if process_id <= 0:
        return False
    if process_id == os.getpid():
        return True
    if os.name == "nt":
        # SYNCHRONIZE is sufficient to query whether the process is signalled.
        handle = ctypes.windll.kernel32.OpenProcess(0x00100000, False, process_id)
        if not handle:
            return False
        try:
            return ctypes.windll.kernel32.WaitForSingleObject(handle, 0) == 0x00000102
        finally:
            ctypes.windll.kernel32.CloseHandle(handle)
    try:
        os.kill(process_id, 0)
        return True
    except (OSError, PermissionError):
        return False


class ModelOperationLock:
    """Cross-process, per-model guard shared by the GUI worker and diagnostics."""

    def __init__(self, models_root: Path, model_id: str) -> None:
        self.path = models_root / f".{model_id}.operation.lock"
        self._owned = False

    def __enter__(self) -> "ModelOperationLock":
        self.path.parent.mkdir(parents=True, exist_ok=True)
        for _ in range(2):
            try:
                descriptor = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                    json.dump({"pid": os.getpid(), "created": time.time()}, stream)
                self._owned = True
                return self
            except FileExistsError:
                try:
                    details = json.loads(self.path.read_text(encoding="utf-8"))
                    owner = int(details.get("pid", 0))
                except (OSError, ValueError, TypeError, json.JSONDecodeError):
                    try:
                        if time.time() - self.path.stat().st_mtime < 30:
                            raise ModelDownloadError(
                                "MODEL_OPERATION_IN_PROGRESS",
                                "Another model operation is acquiring its lock. Retry in a moment.",
                            )
                    except FileNotFoundError:
                        continue
                    owner = 0
                if _process_is_running(owner):
                    raise ModelDownloadError(
                        "MODEL_OPERATION_IN_PROGRESS",
                        "Another operation is already using this model. Wait for it to finish or cancel it before retrying.",
                    )
                try:
                    self.path.unlink()
                except FileNotFoundError:
                    pass
                except OSError as error:
                    raise ModelDownloadError(
                        "MODEL_OPERATION_IN_PROGRESS",
                        "The previous model operation has not released its lock yet. Retry in a moment.",
                    ) from error
        raise ModelDownloadError("MODEL_OPERATION_IN_PROGRESS", "Another model operation is already in progress.")

    def __exit__(self, _type, _value, _traceback) -> None:
        if self._owned:
            try:
                self.path.unlink()
            except FileNotFoundError:
                pass
            self._owned = False


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
            "gemma-4-e2b-it": ModelDefinition("gemma-4-e2b-it", "Gemma 4 E2B", "Google", "google/gemma-4-E2B-it", "gemma-4-e2b-it", int(10.3 * gb), "https://huggingface.co/google/gemma-4-E2B-it", recommended_ram_bytes=16 * gb, recommended_vram_bytes=12 * gb, supports_audio=True, gated=False),
            "gemma-4-e4b-it": ModelDefinition("gemma-4-e4b-it", "Gemma 4 E4B", "Google", "google/gemma-4-E4B-it", "gemma-4-e4b-it", 18 * gb, "https://huggingface.co/google/gemma-4-E4B-it", recommended_ram_bytes=24 * gb, recommended_vram_bytes=16 * gb, supports_audio=True, gated=False),
            "gemma-4-12b-it": ModelDefinition("gemma-4-12b-it", "Gemma 4 12B", "Google", "google/gemma-4-12B-it", "gemma-4-12b-it", 28 * gb, "https://huggingface.co/google/gemma-4-12B-it", recommended_ram_bytes=40 * gb, recommended_vram_bytes=24 * gb, supports_audio=True, gated=False),
            "gemma-4-26b-a4b-it": ModelDefinition("gemma-4-26b-a4b-it", "Gemma 4 26B A4B", "Google", "google/gemma-4-26B-A4B-it", "gemma-4-26b-a4b-it", 55 * gb, "https://huggingface.co/google/gemma-4-26B-A4B-it", recommended_ram_bytes=72 * gb, recommended_vram_bytes=48 * gb, gated=False),
            # Metadata only. These entries never trigger a download; a user must
            # explicitly place a compatible local model/runtime in the folder.
            "llm-jp-3-1.8b-instruct": ModelDefinition("llm-jp-3-1.8b-instruct", "LLM-jp-3 1.8B Instruct", "LLM-jp", "llm-jp/llm-jp-3-1.8b-instruct", "llm-jp-3-1.8b-instruct", 4 * gb, "https://huggingface.co/llm-jp", architecture="LLM-jp", recommended_ram_bytes=8 * gb, recommended_vram_bytes=4 * gb, gated=False),
            "llm-jp-3-3.7b-instruct": ModelDefinition("llm-jp-3-3.7b-instruct", "LLM-jp-3 3.7B Instruct", "LLM-jp", "llm-jp/llm-jp-3-3.7b-instruct", "llm-jp-3-3.7b-instruct", 8 * gb, "https://huggingface.co/llm-jp", architecture="LLM-jp", recommended_ram_bytes=16 * gb, recommended_vram_bytes=8 * gb, gated=False),
            "llama-3-elyza-jp-8b-gguf": ModelDefinition("llama-3-elyza-jp-8b-gguf", "Llama-3-ELYZA-JP-8B (GGUF)", "ELYZA", "elyza/Llama-3-ELYZA-JP-8B-GGUF", "llama-3-elyza-jp-8b-gguf", 6 * gb, "https://huggingface.co/elyza", required_files=("*.gguf",), architecture="Llama 3", variant=ModelVariant.QUANTIZED, recommended_ram_bytes=16 * gb, recommended_vram_bytes=8 * gb, gated=False),
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
        if not result["cuda"]:
            # A short, optional probe avoids importing torch just for UI status.
            try:
                output = subprocess.check_output(["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader,nounits"], text=True, encoding="utf-8", timeout=2).splitlines()[0]
                name, memory_mb = [part.strip() for part in output.split(",", 1)]
                result.update(gpu=name, vram_bytes=int(memory_mb) * 1024 * 1024)
            except Exception: pass
        DeviceManager._cached = result
        return dict(result)


class IModelDownloader:
    def download(self, definition: ModelDefinition, destination: Path, progress: Callable[[dict[str, object]], None], cancel: Callable[[], bool]) -> None: raise NotImplementedError


class HuggingFaceModelDownloader(IModelDownloader):
    """Downloads only on an explicit command, using the installed Python runtime.

    The portable runtime has the ``huggingface_hub`` package, but does not
    guarantee a separately installed ``hf.exe`` command.  Calling the library
    also keeps the existing Hugging Face login/token behaviour intact.
    """
    def __init__(self, snapshot_download: Callable[..., object] | None = None,
                 model_info: Callable[..., object] | None = None) -> None:
        self._snapshot_download = snapshot_download
        self._model_info = model_info

    @staticmethod
    def _download_error(error: Exception, authenticated: bool) -> ModelDownloadError:
        response = getattr(error, "response", None)
        status_code = getattr(response, "status_code", None)
        name = type(error).__name__
        detail = str(error).lower()
        if (isinstance(error, OSError) and getattr(error, "errno", None) == errno.ENOSPC) or any(
            marker in detail for marker in ("no space left on device", "disk full", "not enough space")
        ):
            return ModelDownloadError("MODEL_DISK_FULL", "The model drive ran out of free space. Existing files and cache data were kept for a safe retry.")
        if name == "LocalTokenNotFoundError" or status_code == 401:
            return ModelDownloadError("MODEL_NOT_AUTHENTICATED", "Hugging Face authentication is required. Sign in with the bundled Python environment and retry.")
        if name == "GatedRepoError" or "gated repo" in detail:
            code = "MODEL_NOT_AUTHENTICATED" if not authenticated else "MODEL_GATED_MODEL"
            message = ("Hugging Face authentication is required for this model." if not authenticated else
                       "This model requires repository access approval. Accept its Hugging Face terms, then retry.")
            return ModelDownloadError(code, message)
        if name in {"RepositoryNotFoundError", "RevisionNotFoundError"} or status_code == 404:
            return ModelDownloadError("MODEL_NOT_FOUND", "The official model repository or revision was not found. Check the model ID and network connection.")
        if status_code == 403:
            return ModelDownloadError("MODEL_ACCESS_DENIED", "Access to this Gemma model was denied. Confirm your Hugging Face account permissions and retry.")
        if name in {"ConnectError", "ConnectTimeout", "ReadTimeout", "ProxyError", "NetworkError"} or any(
            marker in detail for marker in ("timed out", "timeout", "name resolution", "connection error", "network is unreachable")
        ):
            return ModelDownloadError("MODEL_NETWORK_ERROR", "The Hugging Face connection failed. Check the network or proxy and retry; existing files and cache data were kept.")
        # Do not echo arbitrary HTTP/request text here: signed download URLs or
        # authorization details can be embedded in third-party exceptions.
        return ModelDownloadError("MODEL_DOWNLOAD_FAILED", f"Model download failed ({name}). Run the PowerShell diagnostic for a safe connectivity check.")

    @staticmethod
    def _downloaded_bytes(destination: Path) -> int:
        if not destination.exists():
            return 0
        total = 0
        # huggingface_hub 1.x creates process-unique ``*.incomplete`` files.
        # A process that is terminated for cancellation can leave more than
        # one attempt for the same logical blob.  Count only the largest one
        # so progress never double-counts retry artifacts.
        incomplete_by_blob: dict[str, int] = {}
        for item in destination.rglob("*"):
            if not item.is_file() or item.name.endswith(".lock") or item.name.endswith(".metadata"):
                continue
            try:
                size = item.stat().st_size
            except (FileNotFoundError, PermissionError):
                continue
            if item.name.endswith(".incomplete"):
                parts = item.name.rsplit(".", 2)
                logical_name = parts[0] if len(parts) == 3 else item.name
                logical_key = str(item.parent / logical_name)
                incomplete_by_blob[logical_key] = max(incomplete_by_blob.get(logical_key, 0), size)
            else:
                total += size
        total += sum(incomplete_by_blob.values())
        return total

    def download(self, definition, destination, progress, cancel) -> None:
        destination.parent.mkdir(parents=True, exist_ok=True)
        if cancel():
            raise ModelDownloadError("MODEL_DOWNLOAD_CANCELLED", "Model download was cancelled.")
        progress({"state": ModelState.DOWNLOADING, "phase": "Connecting", "message": "Connecting to Hugging Face...", "current_file": None,
                  "downloaded_bytes": 0, "total_bytes": definition.estimated_download_bytes, "percent": 0.0})
        try:
            snapshot_download = self._snapshot_download
            if snapshot_download is None:
                from huggingface_hub import snapshot_download as installed_snapshot_download
                snapshot_download = installed_snapshot_download
            from huggingface_hub import HfApi, get_token
        except ImportError as error:
            raise ModelDownloadError("MODEL_DOWNLOAD_RUNTIME_MISSING", "Hugging Face download support is missing. Reinstall the AltFactor Python runtime.") from error
        token = get_token()
        try:
            progress({"state": ModelState.DOWNLOADING, "phase": "Authenticating", "message": "Checking repository access and authentication...",
                      "current_file": None, "downloaded_bytes": 0, "total_bytes": definition.estimated_download_bytes, "percent": 0.0})
            info = (self._model_info or HfApi().model_info)(definition.source, files_metadata=True, token=token)
            repo_files = list(getattr(info, "siblings", ()) or ())
            remote_total = sum(int(getattr(item, "size", 0) or 0) for item in repo_files)
            total_bytes = remote_total or definition.estimated_download_bytes

            # Phase A deliberately downloads metadata only.  A successful
            # config request is not reported as an installed model.
            progress({"state": ModelState.DOWNLOADING, "phase": "Metadata", "message": "Downloading model metadata (Phase A)...",
                      "current_file": "config/tokenizer", "downloaded_bytes": 0, "total_bytes": total_bytes, "percent": 0.0})
            metadata_patterns = ["config.json", "generation_config.json", "processor_config.json", "preprocessor_config.json",
                                 "tokenizer.json", "tokenizer.model", "tokenizer_config.json", "chat_template.jinja", "*.txt", "*.model"]
            snapshot_download(repo_id=definition.source, local_dir=destination, allow_patterns=metadata_patterns,
                              token=token, max_workers=2)
            if cancel():
                raise ModelDownloadError("MODEL_DOWNLOAD_CANCELLED", "Model download was cancelled. Existing files and cache data were kept for retry.")

            progress({"state": ModelState.DOWNLOADING, "phase": "Downloading", "message": "Downloading model weights (Phase B)...",
                      "current_file": "model weights", "downloaded_bytes": self._downloaded_bytes(destination),
                      "total_bytes": total_bytes, "percent": 0.0})
            monitor_stop = threading.Event()
            def monitor() -> None:
                last_value = -1
                while not monitor_stop.wait(0.75):
                    downloaded = self._downloaded_bytes(destination)
                    if downloaded == last_value:
                        continue
                    last_value = downloaded
                    progress({"state": ModelState.DOWNLOADING, "phase": "Downloading", "message": "Downloading model weights (Phase B)...",
                              "current_file": "model weights", "downloaded_bytes": downloaded, "total_bytes": total_bytes,
                              "percent": min(98.0, downloaded * 100.0 / max(total_bytes, 1))})
            monitor_thread = threading.Thread(target=monitor, name="altfactor-model-progress", daemon=True)
            monitor_thread.start()
            try:
                # Keep the same local model directory on retry. Completed
                # files are reused, and hf_xet may reuse cached chunks. The
                # current huggingface_hub deliberately uses process-unique
                # incomplete files, so do not promise byte-perfect resume of
                # an interrupted temporary file.
                snapshot_download(repo_id=definition.source, local_dir=destination, token=token, max_workers=2)
            finally:
                monitor_stop.set()
                monitor_thread.join(timeout=2)
        except ModelDownloadError:
            raise
        except Exception as error:
            raise self._download_error(error, bool(token)) from error
        if cancel():
            raise ModelDownloadError("MODEL_DOWNLOAD_CANCELLED", "Model download was cancelled. Existing files and cache data were kept for retry.")
        downloaded = self._downloaded_bytes(destination)
        progress({"state": ModelState.DOWNLOADING, "phase": "Verifying", "message": "Verifying downloaded model files...", "current_file": None,
                  "downloaded_bytes": downloaded, "total_bytes": total_bytes, "percent": 99.0})
        progress({"state": ModelState.DOWNLOADING, "phase": "Installing", "message": "Registering the downloaded model...", "current_file": None,
                  "downloaded_bytes": downloaded, "total_bytes": total_bytes, "percent": 99.5})


class ModelManager:
    def __init__(self, models_root: Path, downloader: IModelDownloader | None = None, device_status: Callable[[], dict[str, object]] | None = None) -> None:
        self.registry = ModelRegistry(models_root); self.downloader = downloader or HuggingFaceModelDownloader(); self._device_status = device_status or DeviceManager.status
        self._loaded: dict[str, object] = {}; self._state: dict[str, ModelState] = {}
    def validate(self, model_id: str) -> dict[str, object]:
        definition = self.registry.get(model_id); path = self.registry.path_for(definition)
        if not path.exists(): return {"valid": False, "state": ModelState.NOT_INSTALLED, "missing": list(definition.required_files), "path": str(path)}
        missing = [file for file in definition.required_files if not (any(path.glob(file)) if "*" in file else (path / file).is_file())]
        if definition.variant == ModelVariant.QUANTIZED:
            weights = list(path.glob("*.gguf"))
            if not weights: missing.append("GGUF model file")
        else:
            if not any((path / item).is_file() for item in ("processor_config.json", "preprocessor_config.json")): missing.append("processor configuration")
            if not any((path / item).is_file() for item in ("tokenizer.json", "tokenizer.model", "tokenizer_config.json")): missing.append("tokenizer configuration")
            weights = list(path.glob("*.safetensors")) + list(path.glob("pytorch_model*.bin"))
            if not weights: missing.append("model weights (*.safetensors or pytorch_model*.bin)")
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
    def load_model(self, model_id: str, factory: Callable[[Path], object], enforce_memory: bool = True) -> dict[str, object]:
        checked = self.validate(model_id)
        if not checked["valid"]: raise RuntimeError("MODEL_NOT_INSTALLED")
        # Compatibility is advisory.  Quantization and backend choices can
        # substantially change memory use, so unsupported-looking hardware is
        # warned about in status but is not blocked from an explicit load.
        if enforce_memory: self.compatibility(model_id)
        if model_id in self._loaded: return self.status(model_id)
        for other in list(self._loaded): self.unload_model(other)
        self._state[model_id] = ModelState.LOADING
        try: self._loaded[model_id] = factory(Path(str(checked["path"]))); self._state[model_id] = ModelState.READY
        except Exception: self._state[model_id] = ModelState.ERROR; raise
        return self.status(model_id)
    def unload_model(self, model_id: str) -> dict[str, object]:
        # Releasing the reference is sufficient.  Forcing a full collection
        # here stalls the persistent worker and can make the UI look frozen.
        self._state[model_id] = ModelState.UNLOADING
        self._loaded.pop(model_id, None)
        return self.status(model_id)
    def get_loaded_model(self, model_id: str) -> object | None: return self._loaded.get(model_id)
    def is_loaded(self, model_id: str) -> bool: return model_id in self._loaded
    def get_memory_status(self) -> dict[str, object]: return self._device_status()
    def download(self, model_id, progress, cancel) -> dict[str, object]:
        definition = self.registry.get(model_id); compatibility = self.compatibility(model_id)
        if int(compatibility["disk_free_bytes"]) < int(compatibility["disk_required_bytes"]):
            raise ModelDownloadError("MODEL_DISK_FULL", "There is not enough free disk space for this model.")
        destination = self.registry.path_for(definition); self._state[model_id] = ModelState.DOWNLOADING
        try:
            with ModelOperationLock(self.registry.models_root, model_id):
                self.downloader.download(definition, destination, progress, cancel)
                if not self.validate(model_id)["valid"]: raise ModelDownloadError("MODEL_DOWNLOAD_FAILED", "The download completed but required model files are missing.")
                result = self.status(model_id)
                progress({"state": ModelState.INSTALLED, "phase": "Ready", "message": "Model download is ready.",
                          "current_file": None, "downloaded_bytes": result.get("bytes", 0),
                          "total_bytes": result.get("bytes", 0), "percent": 100.0})
                return result
        except Exception: self._state[model_id] = ModelState.INCOMPLETE if destination.exists() else ModelState.ERROR; raise
