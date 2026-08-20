from __future__ import annotations
import subprocess, time
from pathlib import Path
from .cancellation import OperationCancelledError, throw_if_cancelled
from .errors import AttError
from .progress import emit

class AudioConverter:
    def __init__(self, ffmpeg_path: Path) -> None: self.ffmpeg_path = ffmpeg_path
    def convert(self, source: Path, destination: Path, total_seconds: float = 0, cancel_file: Path | None = None, enhancement: str = "auto") -> None:
        if not self.ffmpeg_path.is_file(): raise AttError("ATT_ENV_FFMPEG_NOT_FOUND", f"FFmpegが見つかりません: {self.ffmpeg_path}")
        throw_if_cancelled(cancel_file)
        filters=[]
        # dynaudnorm is deliberately modest: it improves quiet speech without
        # destructive noise removal or modifying the source media.
        if enhancement in {"auto", "weak", "standard"}: filters.append("dynaudnorm=f=150:g=5")
        if enhancement == "standard": filters.append("afftdn=nr=6")
        command=[str(self.ffmpeg_path),"-progress","pipe:1","-nostats","-y","-i",str(source),"-vn","-ac","1","-ar","16000"]
        if filters: command += ["-af", ",".join(filters)]
        command += ["-c:a","pcm_s16le",str(destination)]
        try: process=subprocess.Popen(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding="utf-8",errors="replace",creationflags=0x08000000)
        except OSError as error: raise AttError("ATT_MEDIA_CONVERT_FAILED",f"FFmpegを起動できません: {error}") from error
        try:
            while process.poll() is None:
                if cancel_file is not None and cancel_file.exists():
                    emit("cancel_acknowledged",message="FFmpeg変換中にキャンセル要求を受信しました")
                    process.terminate()
                    try: process.wait(timeout=2)
                    except subprocess.TimeoutExpired: process.kill()
                    raise OperationCancelledError("音声変換をキャンセルしました")
                line=process.stdout.readline().strip() if process.stdout else ""
                if line.startswith("out_time_ms=") and total_seconds>0:
                    try:
                        processed=int(line.split("=",1)[1])/1_000_000
                        emit("progress",stage="convert",value=10+min(10,processed/total_seconds*10),processed_seconds=processed,total_seconds=total_seconds,message="音声を正規化しています")
                    except ValueError: pass
                elif not line: time.sleep(.05)
            stderr=process.stderr.read() if process.stderr else ""
            if process.returncode != 0: raise AttError("ATT_MEDIA_CONVERT_FAILED",f"音声変換に失敗しました: {stderr[-2000:]}")
        finally:
            if process.poll() is None: process.kill()
