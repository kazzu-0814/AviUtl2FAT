from __future__ import annotations
import json, subprocess
from pathlib import Path
from .errors import AttError

def probe(ffprobe: Path, source: Path) -> dict[str, object]:
    if not ffprobe.is_file(): raise AttError("ATT_ENV_FFPROBE_NOT_FOUND", f"ffprobeが見つかりません: {ffprobe}")
    result = subprocess.run([str(ffprobe), "-v", "error", "-show_format", "-show_streams", "-of", "json", str(source)], capture_output=True, text=True, encoding="utf-8", errors="replace", check=False, creationflags=0x08000000)
    if result.returncode: raise AttError("ATT_MEDIA_PROBE_FAILED", result.stderr[-2000:])
    try: data=json.loads(result.stdout)
    except json.JSONDecodeError as error: raise AttError("ATT_MEDIA_PROBE_FAILED", "ffprobeのJSONが不正です") from error
    audio = next((x for x in data.get("streams",[]) if x.get("codec_type")=="audio"), None)
    if audio is None: raise AttError("ATT_MEDIA_NO_AUDIO_STREAM", "音声ストリームがありません")
    try: data["duration_seconds"]=float(data.get("format",{}).get("duration",0))
    except (TypeError,ValueError): data["duration_seconds"]=0.0
    data["audio"] = {"codec": audio.get("codec_name"), "sample_rate": audio.get("sample_rate"), "channels": audio.get("channels"), "bitrate": audio.get("bit_rate")}
    return data
