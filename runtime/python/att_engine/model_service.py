from __future__ import annotations
import json, shutil, time
from pathlib import Path
from .cancellation import OperationCancelledError, throw_if_cancelled
from .errors import AttError
from .progress import emit

MODEL_REPOS={"tiny":"Systran/faster-whisper-tiny","base":"Systran/faster-whisper-base","small":"Systran/faster-whisper-small","medium":"Systran/faster-whisper-medium","large-v3":"Systran/faster-whisper-large-v3"}
REQUIRED=("config.json","model.bin","tokenizer.json")

def model_path(root: Path, model: str) -> Path: return root / model
def validate(root: Path, model: str, cancel_file: Path | None = None, load: bool = True) -> dict[str, object]:
    throw_if_cancelled(cancel_file); target=model_path(root,model)
    missing=[name for name in REQUIRED if not (target/name).is_file() or (target/name).stat().st_size==0]
    marker=target/".incomplete"
    if missing: return {"valid":False,"state":"incomplete","missing":missing,"path":str(target)}
    if marker.exists() and not load: return {"valid":False,"state":"incomplete","missing":missing,"path":str(target)}
    if load:
        try:
            from faster_whisper import WhisperModel
            WhisperModel(str(target),device="cpu",compute_type="int8")
        except Exception as error: return {"valid":False,"state":"corrupted","error":str(error),"path":str(target)}
    marker.unlink(missing_ok=True)
    return {"valid":True,"state":"installed","size_bytes":sum(p.stat().st_size for p in target.rglob("*") if p.is_file()),"path":str(target)}

def download(root: Path, model: str, cancel_file: Path | None = None) -> None:
    if model not in MODEL_REPOS: raise AttError("ATT_MODEL_VALIDATION_FAILED","不正なモデルIDです")
    root.mkdir(parents=True,exist_ok=True); target=model_path(root,model); marker=target/".incomplete"; target.mkdir(parents=True,exist_ok=True);marker.write_text("downloading",encoding="utf-8")
    try:
        from huggingface_hub import snapshot_download
        emit("model_download_progress",model=model,value=None,message="モデルをダウンロードしています（容量不確定）",stage="download")
        throw_if_cancelled(cancel_file)
        snapshot_download(MODEL_REPOS[model],local_dir=target)
        throw_if_cancelled(cancel_file);marker.unlink(missing_ok=True)
        result=validate(root,model,cancel_file,load=True)
        if not result["valid"]: raise AttError("ATT_MODEL_VALIDATION_FAILED",json.dumps(result,ensure_ascii=False))
        emit("model_download_progress",model=model,value=100,message="ダウンロードと検証が完了しました",stage="completed")
    except OperationCancelledError: raise
    except Exception as error: raise AttError("ATT_MODEL_DOWNLOAD_INCOMPLETE",f"モデル取得に失敗しました: {error}") from error

def safe_delete(root: Path, model: str) -> None:
    if model not in MODEL_REPOS: raise AttError("ATT_MODEL_DELETE_FAILED","不正なモデルIDです")
    target=model_path(root,model); resolved_root=root.resolve(); resolved=target.resolve()
    if resolved.parent != resolved_root or target.is_symlink(): raise AttError("ATT_MODEL_DELETE_FAILED","安全でない削除パスです")
    if target.exists(): shutil.rmtree(target)
