# AviUtl2 FAT v1.0

Formation Auto Text (FAT) is an AviUtl2 companion for speech recognition, editable captions, and AviUtl2 text-object export. Recognition and optional AI output are always editable before export.

## v1.0 distribution

- Windows 10/11 x64 and AviUtl2 are supported.
- The release build is .NET self-contained: users do not need .NET SDK, Rust, Cargo, Node.js, Git, or a system Python installation.
- Whisper and Gemma model weights are intentionally not bundled and can only be acquired by an explicit Model Manager action.
- The installer only places `AviUtl2FAT` under the user-selected AviUtl2 Plugin folder. It does not modify `.aup2` files, user projects, or other plugins.
- Logs and user settings remain in `%LOCALAPPDATA%\AviUtl2FAT`.

For installation and normal use, read [QUICKSTART.md](QUICKSTART.md). For a pre-release gate and known release blockers, read [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) and [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).

FAT (Formation Auto Text) は、ATT v0.6 の分離実行・Python/faster-whisper・FFmpeg の資産を参照して再構築した AviUtl2 向け字幕基盤です。C# 実行バイナリの逆コンパイルは行っていません。

## v0.7 の処理経路

`AviUtl2 → FAT Rust plugin → FAT App → fat-app-ipc v1 → FAT Worker → fat-python v1 → Python Engine → faster-whisper / PassthroughProvider → editable FATCaption → PlacementAdapter → placement JSON`

`PassthroughProvider` is always available and preserves recognized text. The v0.7.1 Python Engine keeps a persistent JSON Lines command boundary with `speech.recognize`, `ai.generate`, `ai.rewrite`, `ai.shorten`, and `engine.status`. Gemma, Llama, and Japanese AI are safe stubs until their inference backends are intentionally installed.

## 開発

1. 既存 ATT ランタイムを再利用する場合は `./sync-att-assets.ps1` を実行します。既存 ATT の内容は変更しません。
2. `./build.ps1 -SyncAttAssets` を実行します。
3. `dotnet test AviUtl2FAT.sln` を実行します。

この環境の ATT v0.6 には直接の AviUtl2 テキストオブジェクト生成 API が含まれていないため、v0.7 は ATT と互換な `.placement.json` を出力します。AviUtl2 側の正式なオブジェクト作成 API が提供されるまで、これは通常テキストオブジェクトに変換するための安全な中間形式です。字幕そのものは FAT の DataGrid で直接編集できます。

## 安全性

- Rust プラグインは UI を別プロセスとして起動し、AviUtl2 へ例外を伝播させません。
- Worker は Named Pipe 上でエラーを構造化して返します。
- Python の ATT 既存処理は CUDA→CPU フォールバック、FFmpeg/ffprobe 検証、キャンセル、進捗通知を維持します。
- クラウド Provider は v0.7 で呼び出されません。API キーも保持しません。
