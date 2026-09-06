from __future__ import annotations

import json
from typing import Any, Callable


# The command-line recognizer writes plain JSON Lines to stdout.  The long-lived
# FAT engine embeds that recognizer and supplies an envelope callback instead.
# Keeping this tiny switch here avoids a second recognition process solely to
# translate progress messages.
_callback: Callable[[str, dict[str, Any]], None] | None = None


def set_callback(callback: Callable[[str, dict[str, Any]], None] | None) -> None:
    global _callback
    _callback = callback


def emit(event_type: str, **values: Any) -> None:
    if _callback is not None:
        _callback(event_type, values)
        return
    print(json.dumps({"type": event_type, **values}, ensure_ascii=False), flush=True)
