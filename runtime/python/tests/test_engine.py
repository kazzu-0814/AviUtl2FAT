import io
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch, Mock

from att_engine.audio_converter import AudioConverter
from att_engine.errors import AttError
from att_engine.output_writer import OutputWriter, seconds_to_frame, timestamp
from att_engine.progress import emit
from att_engine.recognizer import FasterWhisperRecognizer, RecognitionOptions, clean_text
from att_engine.speech_pipeline import TranscriptPostProcessor, build_initial_prompt, select_profile, warning_for


class FakeWord:
    def __init__(self, start, end):
        self.start = start
        self.end = end


class FakeSegment:
    def __init__(self, start, end, text, words):
        self.start = start
        self.end = end
        self.text = text
        self.words = words
        self.avg_logprob = -0.2
        self.no_speech_prob = 0.0


class EngineTests(unittest.TestCase):
    def test_timestamp_and_frame(self):
        self.assertEqual("00:00:02.840", timestamp(2.84))
        self.assertEqual(16, seconds_to_frame(.52, 30))

    def test_json_and_text_output(self):
        with tempfile.TemporaryDirectory() as folder:
            writer = OutputWriter(30)
            result = writer.build_result("sample.mp4", "ja", "small", [{"id": 1, "start": .5, "end": 1.5, "text": "本文"}])
            json_path, text_path = Path(folder) / "out.json", Path(folder) / "out.txt"
            writer.write_json(json_path, result); writer.write_text(text_path, result["segments"])
            self.assertEqual("本文", json.loads(json_path.read_text(encoding="utf-8"))["segments"][0]["text"])
            self.assertIn("[00:00:00.500 - 00:00:01.500]", text_path.read_text(encoding="utf-8"))
            srt_path = Path(folder) / "out.srt"; writer.write_srt(srt_path, result["segments"])
            self.assertIn("00:00:00,500 --> 00:00:01,500", srt_path.read_text(encoding="utf-8"))

    def test_filler_removal(self):
        self.assertEqual("こんにちは", clean_text(" えー こんにちは ", True, True))

    def test_progress_json(self):
        output = io.StringIO()
        with redirect_stdout(output): emit("progress", value=25, message="認識中")
        self.assertEqual("progress", json.loads(output.getvalue())["type"])

    def test_missing_ffmpeg(self):
        with self.assertRaises(AttError): AudioConverter(Path("missing.exe")).convert(Path("in.mp4"), Path("out.wav"))

    @patch("subprocess.Popen")
    def test_ffmpeg_failure(self, popen):
        process=Mock();process.poll.side_effect=[None,1,1];process.stdout.readline.return_value="";process.stderr.read.return_value="failed";process.returncode=1;popen.return_value=process
        with tempfile.TemporaryDirectory() as folder:
            exe = Path(folder) / "ffmpeg.exe"; exe.touch()
            with self.assertRaises(AttError): AudioConverter(exe).convert(Path("in.mp4"), Path("out.wav"))

    def test_model_validation_incomplete_and_safe_delete(self):
        from att_engine.model_service import validate, safe_delete
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);target=root/"tiny";target.mkdir();(target/".incomplete").touch()
            self.assertFalse(validate(root,"tiny",load=False)["valid"])
            safe_delete(root,"tiny");self.assertFalse(target.exists())

    def test_cancel_file(self):
        from att_engine.cancellation import throw_if_cancelled, OperationCancelledError
        with tempfile.TemporaryDirectory() as folder:
            marker=Path(folder)/"cancel.request";marker.touch()
            with self.assertRaises(OperationCancelledError):throw_if_cancelled(marker)

    def test_speech_profile_and_dictionary_prompt(self):
        profile = select_profile("auto", logical_cores=4, available_ram_bytes=4 * 1024 ** 3, cuda=False)
        self.assertEqual("low", profile.name)
        self.assertEqual("base", profile.model)
        self.assertEqual("int8", profile.compute_type)
        self.assertEqual("AviUtl2、Codex", build_initial_prompt(["AviUtl2", "Codex", "AviUtl2"]))

    def test_recognizer_rejects_missing_model_without_download(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            options = RecognitionOptions("base", "ja", "cpu", "int8", True, root, False, False)
            with self.assertRaises(AttError) as error:
                FasterWhisperRecognizer(options).recognize(root / "audio.wav")
            self.assertEqual("FAT_SPEECH_MODEL_NOT_INSTALLED", error.exception.code)

    def test_word_timestamps_preserve_silent_gap_when_segment_boundaries_touch(self):
        first = FakeSegment(0.0, 7.0, "こんにちは", [FakeWord(0.2, 1.8)])
        second = FakeSegment(7.0, 9.0, "次です", [FakeWord(7.1, 8.7)])
        start1, end1 = FasterWhisperRecognizer._word_boundary(first)
        start2, end2 = FasterWhisperRecognizer._word_boundary(second)
        self.assertEqual((0.2, 1.8), (start1, end1))
        self.assertEqual((7.1, 8.7), (start2, end2))
        self.assertGreater(start2 - end1, 5.0)

    def test_post_processor_filters_silence_duplicates_and_repairs_time(self):
        values = TranscriptPostProcessor().process([
            {"text": "えー、 こんにちは！！", "start": -1, "end": 1, "no_speech_probability": 0.1},
            {"text": "こんにちは！", "start": 1, "end": 2, "no_speech_probability": 0.1},
            {"text": "うーん", "start": 2, "end": 3, "no_speech_probability": 0.9},
        ], 3, "ja", "organize")
        self.assertEqual(1, len(values))
        self.assertEqual("こんにちは！", values[0]["text"])
        self.assertEqual(0.0, values[0]["start"])

    def test_low_confidence_and_long_caption_warn(self):
        self.assertIn("精度", warning_for({"text": "本文", "confidence": 0.2}))
        self.assertIn("文字", warning_for({"text": "a" * 43}))


if __name__ == "__main__": unittest.main()
