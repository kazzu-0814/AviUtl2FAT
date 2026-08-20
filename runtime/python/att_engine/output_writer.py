from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable


def seconds_to_frame(seconds: float, fps: float) -> int:
    if seconds < 0 or fps <= 0:
        raise ValueError("seconds and fps must be valid")
    return int(seconds * fps + 0.5)


def timestamp(seconds: float) -> str:
    milliseconds = int(seconds * 1000 + 0.5)
    hours, remainder = divmod(milliseconds, 3_600_000)
    minutes, remainder = divmod(remainder, 60_000)
    secs, millis = divmod(remainder, 1000)
    return f"{hours:02}:{minutes:02}:{secs:02}.{millis:03}"


class OutputWriter:
    def __init__(self, fps: float) -> None:
        self.fps = fps

    def build_result(self, source: str, language: str | None, model: str,
                     segments: Iterable[dict[str, object]], source_duration: float = 0,
                     device: str = "auto", compute_type: str = "int8") -> dict[str, object]:
        output: list[dict[str, object]] = []
        for item in segments:  # generatorを逐次消費し、進捗通知も継続する
            start, end = float(item["start"]), float(item["end"])
            output.append({**item, "start_frame": seconds_to_frame(start, self.fps),
                           "end_frame": seconds_to_frame(end, self.fps), "speaker": None,
                           "confidence": None, "style": {"font": None, "size": None, "color": None,
                           "outline_color": None, "position": None}})
        return {"format": "AviUtl2 ATT", "version": "0.5.5", "status":"completed", "is_partial":False, "source_file": source,
                "source_duration": source_duration, "language": language, "model": model,
                "device": device, "compute_type": compute_type, "fps": self.fps,
                "created_at": datetime.now(timezone.utc).astimezone().isoformat(), "segments": output}

    @staticmethod
    def write_json(path: Path, result: dict[str, object]) -> None:
        path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")

    @staticmethod
    def write_text(path: Path, segments: list[dict[str, object]]) -> None:
        blocks = [f"[{timestamp(float(item['start']))} - {timestamp(float(item['end']))}]\n{item['text']}"
                  for item in segments]
        path.write_text("\n\n".join(blocks) + "\n", encoding="utf-8")

    @staticmethod
    def write_srt(path: Path, segments: list[dict[str, object]], bom: bool = False) -> None:
        def srt_time(value: float) -> str: return timestamp(max(0, value)).replace(".", ",")
        blocks=[]
        for index,item in enumerate(segments,1):
            start=max(0,float(item["start"])); end=max(start+.001,float(item["end"]))
            blocks.append(f"{index}\r\n{srt_time(start)} --> {srt_time(end)}\r\n{item['text']}")
        path.write_text("\r\n\r\n".join(blocks)+"\r\n",encoding="utf-8-sig" if bom else "utf-8",newline="")
