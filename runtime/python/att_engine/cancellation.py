from __future__ import annotations
from pathlib import Path

class OperationCancelledError(RuntimeError): pass

def throw_if_cancelled(cancel_file: Path | None) -> None:
    if cancel_file is not None and cancel_file.exists():
        raise OperationCancelledError("キャンセル要求を受信しました")
