from __future__ import annotations

import argparse
import sys
from pathlib import Path

from att_engine.audio_converter import AudioConverter
from att_engine.errors import AttError
from att_engine.output_writer import OutputWriter
from att_engine.progress import emit
from att_engine.recognizer import FasterWhisperRecognizer, RecognitionOptions
from att_engine.media_probe import probe
from att_engine.cancellation import OperationCancelledError, throw_if_cancelled
from att_engine.speech_pipeline import TranscriptPostProcessor, build_initial_prompt, select_profile, warning_for
from att_engine.model_service import validate as validate_model
from att_engine.speech_recovery import SpeechRecoverySettings, find_candidates, is_recoverable, merge_recovered, recovery_window


def parser() -> argparse.ArgumentParser:
    value = argparse.ArgumentParser(description="AviUtl2 ATT recognition worker")
    value.add_argument("--input", required=True, type=Path)
    value.add_argument("--output", required=True, type=Path)
    value.add_argument("--model", default="small")
    value.add_argument("--language", default="auto")
    value.add_argument("--device", default="auto")
    value.add_argument("--compute-type", default="int8")
    value.add_argument("--fps", type=float, default=30.0)
    value.add_argument("--max-characters", type=int, default=24)
    value.add_argument("--max-display-seconds", type=float, default=8.0)
    value.add_argument("--ffmpeg", required=True, type=Path)
    value.add_argument("--ffprobe", type=Path)
    value.add_argument("--model-dir", required=True, type=Path)
    value.add_argument("--vad", action="store_true")
    value.add_argument("--remove-spaces", action="store_true")
    value.add_argument("--remove-fillers", action="store_true")
    value.add_argument("--split-punctuation", action="store_true")
    value.add_argument("--session-dir", type=Path)
    value.add_argument("--cancel-file", type=Path)
    value.add_argument("--keep-wav", action="store_true")
    value.add_argument("--beam-size", type=int, default=0)
    value.add_argument("--best-of", type=int, default=5)
    value.add_argument("--temperature", type=float, default=0.0)
    value.add_argument("--word-timestamps", action="store_true")
    value.add_argument("--condition-on-previous-text", action="store_true")
    value.add_argument("--initial-prompt", default="")
    value.add_argument("--cpu-threads", type=int, default=0)
    value.add_argument("--profile", choices=("auto", "low", "standard", "high"), default="auto")
    value.add_argument("--audio-enhancement", choices=("auto", "none", "weak", "standard"), default="auto")
    value.add_argument("--filler-mode", choices=("keep", "auto", "organize"), default="auto")
    value.add_argument("--dictionary", default="")
    value.add_argument("--speech-recovery", choices=("none", "auto", "speech_priority"), default="auto")
    return value


def main() -> int:
    args = parser().parse_args()
    try:
        if not args.input.is_file():
            raise AttError("INPUT_NOT_FOUND", f"入力ファイルが見つかりません: {args.input}")
        if args.fps <= 0:
            raise AttError("INVALID_FPS", "FPSは0より大きくしてください")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.model_dir.mkdir(parents=True, exist_ok=True)
        session_dir=args.session_dir or args.output.parent/".session";session_dir.mkdir(parents=True,exist_ok=True)
        cancel_file=args.cancel_file or session_dir/"cancel.request";throw_if_cancelled(cancel_file)
        ffprobe = args.ffprobe or args.ffmpeg.with_name("ffprobe.exe")
        emit("progress", stage="environment", value=5, message="実行環境を確認しています")
        media = probe(ffprobe, args.input); total_seconds = float(media["duration_seconds"])
        emit("progress", stage="probe", value=8, message="動画と音声を確認しました", audio=media.get("audio"))
        wav_path = session_dir/"audio.wav"
        emit("progress", stage="convert", value=12, message="音声を変換しています", total_seconds=total_seconds)
        AudioConverter(args.ffmpeg).convert(args.input, wav_path,total_seconds,cancel_file,args.audio_enhancement)
        emit("progress", stage="convert", value=20, message="音声変換が完了しました", total_seconds=total_seconds)
        profile = select_profile(args.profile)
        model = args.model if args.model != "auto" else profile.model
        profile_detail = profile.name
        # The automatic low profile prefers the compact base model.  If it has
        # not been installed but an explicitly installed Small model is already
        # available, use it instead of forcing a second download or failing a
        # perfectly usable local runtime.  Never download here.
        if args.model == "auto" and not validate_model(args.model_dir, model, cancel_file, load=False).get("valid"):
            small = validate_model(args.model_dir, "small", cancel_file, load=False)
            if small.get("valid"):
                model = "small"
                profile_detail = f"{profile.name}（導入済み small を使用）"
        device = args.device if args.device != "auto" else profile.device
        compute_type = args.compute_type if args.compute_type != "auto" else profile.compute_type
        beam_size = args.beam_size if args.beam_size > 0 else profile.beam_size
        cpu_threads = args.cpu_threads if args.cpu_threads > 0 else profile.cpu_threads
        initial_prompt = args.initial_prompt or build_initial_prompt(args.dictionary.split(","))
        emit("progress", stage="profile", value=24, message=f"認識プロファイル: {profile_detail}", profile=profile.name, device=device, compute_type=compute_type, model=model)
        options = RecognitionOptions(model, None if args.language == "auto" else args.language,
                                     device, compute_type, args.vad or profile.vad, args.model_dir,
                                     args.remove_spaces, args.remove_fillers,cancel_file,beam_size,args.best_of,args.temperature,
                                     args.word_timestamps,args.condition_on_previous_text,initial_prompt,cpu_threads)
        throw_if_cancelled(cancel_file);segments, language = FasterWhisperRecognizer(options).recognize(wav_path, total_seconds)
        writer = OutputWriter(args.fps)
        collected=[]
        try:
            for segment in segments: collected.append(segment)
        except OperationCancelledError:
            partial=writer.build_result(args.input.name,language,args.model,collected,total_seconds,args.device,args.compute_type)
            partial["version"]="0.3";partial["status"]="cancelled";partial["is_partial"]=True
            partial_path=args.output.with_name(args.output.stem+".partial.json");writer.write_json(partial_path,partial);writer.write_srt(args.output.with_name(args.output.stem+".partial.srt"),partial["segments"])
            emit("cancelled",message="処理をキャンセルしました",partial_result_saved=True,output=str(partial_path));return 2
        throw_if_cancelled(cancel_file)
        filler_mode = "keep" if args.filler_mode == "keep" else args.filler_mode
        collected = TranscriptPostProcessor().process(collected, total_seconds, language, filler_mode)
        recovery_settings = SpeechRecoverySettings.select(args.speech_recovery)
        if recovery_settings.enabled:
            emit("progress", stage="recovery", value=90, message="長い空白区間の認識漏れを確認しています")
            candidates = find_candidates(wav_path, collected, total_seconds, recovery_settings)
            recovered = []
            recognizer = FasterWhisperRecognizer(options)
            for index, candidate in enumerate(candidates, start=1):
                throw_if_cancelled(cancel_file)
                emit("status", message=f"認識漏れ候補を確認しています ({index}/{len(candidates)})", recovery_candidate={"start": candidate.start, "end": candidate.end, "rms_db": candidate.rms_db, "active_ratio": candidate.active_ratio})
                window_start, window_end = recovery_window(candidate, total_seconds, recovery_settings)
                values, _ = recognizer.recognize_window(wav_path, window_start, window_end)
                # Values have already been shifted into the original media's
                # time axis, so do not clamp them to the short clip duration.
                cleaned = TranscriptPostProcessor().process(values, 0, language, filler_mode)
                # Padding provides context to Whisper, but recovered captions
                # must originate inside the original gap.  This prevents the
                # pass from re-emitting neighbouring accepted speech.
                recovered.extend(item for item in cleaned
                                 if candidate.start <= (float(item["start"]) + float(item["end"])) / 2 <= candidate.end
                                 and is_recoverable(item, recovery_settings))
            collected = merge_recovered(collected, recovered)
            emit("progress", stage="recovery", value=96, message=f"認識漏れ補正を完了しました（追加 {len(recovered)} 件）")
        for index, item in enumerate(collected, start=1):
            # A clip recognizer starts segment ids at one.  Normalize only the
            # output identifiers after merging; timestamps remain untouched.
            item["id"] = index
            item["warning"] = warning_for(item)
        result = writer.build_result(args.input.name, language, model, collected, total_seconds, device, compute_type)
        emit("progress", stage="save", value=98, message="出力を保存しています")
        writer.write_json(args.output, result)
        writer.write_text(args.output.with_suffix(".txt"), result["segments"])
        writer.write_srt(args.output.with_suffix(".srt"), result["segments"])
        emit("completed", value=100, message="完了", output=str(args.output))
        return 0
    except KeyboardInterrupt:
        emit("error", code="CANCELED", message="キャンセルされました")
        return 130
    except OperationCancelledError:
        emit("cancel_acknowledged",message="キャンセル要求を受信しました");emit("cancelled",message="処理をキャンセルしました",partial_result_saved=False);return 2
    except AttError as error:
        emit("error", code=error.code, message=str(error))
        return 1
    except (MemoryError, OSError) as error:
        emit("error", code="SYSTEM_ERROR", message=str(error))
        return 1
    except Exception as error:  # worker境界で未処理例外をJSONへ変換する
        emit("error", code="UNEXPECTED_ERROR", message=str(error))
        return 1
    finally:
        try:
            if "wav_path" in locals() and not getattr(args,"keep_wav",False):
                wav_path.unlink(missing_ok=True)
        except OSError:
            pass


if __name__ == "__main__":
    sys.exit(main())
