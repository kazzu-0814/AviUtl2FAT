"""明示実行用の実モデルCPUスモークテスト（通常の単体テストからは除外）。"""
from __future__ import annotations
import math, struct, tempfile, wave
from pathlib import Path
from att_engine.output_writer import OutputWriter
from att_engine.recognizer import FasterWhisperRecognizer, RecognitionOptions

def main() -> None:
    with tempfile.TemporaryDirectory(prefix="ATT 日本語 path ") as folder:
        root=Path(folder); audio=root/"短い 無音.wav"
        with wave.open(str(audio),"wb") as wav:
            wav.setnchannels(1);wav.setsampwidth(2);wav.setframerate(16000)
            # 完全無音ではデコーダ経路が省略される場合があるため、権利問題のない生成トーンを使う
            wav.writeframes(b"".join(struct.pack("<h",int(1200*math.sin(2*math.pi*440*i/16000))) for i in range(16000)))
        options=RecognitionOptions("tiny",None,"cpu","int8",True,Path("runtime/models"),False,False)
        segments,language=FasterWhisperRecognizer(options).recognize(audio,1.0)
        result=OutputWriter(30).build_result(audio.name,language,"tiny",segments,1.0,"cpu","int8")
        writer=OutputWriter(30);writer.write_json(root/"smoke.json",result);writer.write_text(root/"smoke.txt",result["segments"]);writer.write_srt(root/"smoke.srt",result["segments"])
        assert all((root/name).exists() for name in ("smoke.json","smoke.txt","smoke.srt"))
        print(f"OK language={language} segments={len(result['segments'])}")

if __name__=="__main__": main()
