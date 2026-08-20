from __future__ import annotations

import json
from typing import Any


def emit(event_type: str, **values: Any) -> None:
    print(json.dumps({"type": event_type, **values}, ensure_ascii=False), flush=True)
