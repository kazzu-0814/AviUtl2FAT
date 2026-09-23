from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

from fat_engine.model_manager import ModelDownloadError, ModelManager, ModelOperationLock


def result(label: str, state: str, detail: str = "") -> None:
    suffix = f"  {detail}" if detail else ""
    print(f"{label:.<30} {state}{suffix}", flush=True)


def weight_probe(repo_id: str, filename: str, token: str | None, limit: int = 1024 * 1024) -> int:
    from huggingface_hub import hf_hub_url
    from huggingface_hub.utils import build_hf_headers, get_session

    url = hf_hub_url(repo_id, filename)
    headers = build_hf_headers(token=token)
    headers["Range"] = f"bytes=0-{limit - 1}"
    transferred = 0
    with get_session().stream("GET", url, headers=headers, follow_redirects=True, timeout=30) as response:
        response.raise_for_status()
        for chunk in response.iter_bytes(64 * 1024):
            transferred += len(chunk)
            if transferred >= limit:
                break
    return transferred


def main() -> int:
    parser = argparse.ArgumentParser(description="AltFactor Gemma download diagnostics")
    parser.add_argument("--model-id", default="gemma-4-e2b-it")
    parser.add_argument("--model-dir", type=Path, required=True)
    parser.add_argument("--probe-weight", action="store_true")
    parser.add_argument("--download", action="store_true")
    args = parser.parse_args()

    print("AltFactor Gemma Diagnostics\n", flush=True)
    result("Portable Python", "PASS", sys.version.split()[0])
    try:
        import huggingface_hub
        from huggingface_hub import HfApi, get_token, snapshot_download
        result("huggingface_hub", "PASS", huggingface_hub.__version__)
    except ImportError as error:
        result("huggingface_hub", "FAILED", str(error))
        return 2

    manager = ModelManager(args.model_dir)
    try:
        definition = manager.registry.get(args.model_id)
    except ValueError as error:
        result("Model ID", "FAILED", str(error))
        return 2

    token = get_token()
    result("Hugging Face Auth", "PASS" if token else "INFO", "signed in" if token else "no saved token (public repos may still work)")
    try:
        info = HfApi().model_info(definition.source, files_metadata=True, token=token)
        siblings = list(info.siblings or [])
        result("Repository Access", "PASS", definition.source)
        weight_names = [item.rfilename for item in siblings if item.rfilename.endswith((".safetensors", ".bin"))]
        if not weight_names:
            raise ModelDownloadError("MODEL_NOT_FOUND", "No model weight files were listed by the repository.")

        with ModelOperationLock(args.model_dir, args.model_id):
            destination = manager.registry.path_for(definition)
            snapshot_download(
                repo_id=definition.source,
                local_dir=destination,
                allow_patterns=["config.json", "tokenizer_config.json", "processor_config.json"],
                token=token,
                max_workers=2,
            )
            result("Config Download", "PASS", "Phase A metadata")

            free = shutil.disk_usage(args.model_dir).free
            required = int(definition.estimated_download_bytes * 1.1)
            if free < required:
                raise ModelDownloadError("MODEL_DISK_FULL", f"{free:,} bytes free; {required:,} bytes required")
            result("Storage", "PASS", f"{free / 1_000_000_000:.1f} GB free")

            if args.probe_weight:
                transferred = weight_probe(definition.source, weight_names[0], token)
                result("Weight Access", "PASS", f"Phase B transferred {transferred:,} bytes")
            else:
                result("Weight Access", "SKIPPED", "use -ProbeWeight for a 1 MiB transfer test")

        if args.download:
            # ModelManager acquires the same per-model lock used by the GUI.
            def progress(event: dict[str, object]) -> None:
                phase = str(event.get("phase", "Downloading"))
                downloaded = int(event.get("downloaded_bytes", 0) or 0)
                total = int(event.get("total_bytes", 0) or 0)
                percent = float(event.get("percent", 0) or 0)
                print(f"[{phase}] {percent:5.1f}%  {downloaded:,} / {total:,} bytes", flush=True)
            manager.download(args.model_id, progress, lambda: False)
            result("Full Download", "PASS", str(manager.registry.path_for(definition)))

        print(f"\n{definition.display_name} is ready to download." if not args.download else f"\n{definition.display_name} is installed.", flush=True)
        return 0
    except ModelDownloadError as error:
        result("Gemma Diagnostic", "FAILED", f"{error.code}: {error}")
        return 1
    except KeyboardInterrupt:
        result("Gemma Diagnostic", "CANCELLED", "Partial data was kept for retry.")
        return 130
    except Exception as error:
        mapped = manager.downloader._download_error(error, bool(token))
        result("Gemma Diagnostic", "FAILED", f"{mapped.code}: {mapped}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
