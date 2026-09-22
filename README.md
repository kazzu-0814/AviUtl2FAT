# AviUtl2 AltFactor

**Formerly AviUtl2FAT**

AviUtl2FATは、Local AI Factoryへの進化に伴い「AviUtl2 AltFactor」へ名称を変更しました。別プロジェクトではなく、AviUtl2FATから継続して開発されているプロジェクトです。

## V2.0 Stable — FATからの正式アップデート

AviUtl2 AltFactor V2.0 Stable は、AviUtl2FAT V1.x の後継となる正式アップデートです。既存の設定・モデル設定・保存パス・作業データは、互換性のため維持される %LOCALAPPDATA%\\AviUtl2FAT からそのまま引き継がれます。

V1.x ユーザーへの更新を確実に届けるため、V2.0 の移行期間中は既存の GitHub Releases 更新経路を維持します。新しいインストーラーの表示名とファイル名は AltFactor へ移行しますが、Windows の Upgrade GUID、AviUtl2 Plugin 配置先、内部 Worker／IPC 名は変更しません。

AviUtl2 AltFactor（オルトファクター）は、音声認識、編集可能な字幕、ローカルAI、AviUtl2テキストオブジェクト出力を扱うLocal AI Factoryです。認識結果とAI出力は、常に出力前に編集できます。

## V2.0 Stable distribution

### V2.0.2 bug-fix and stability update

- Gemma 4 downloads now use the `huggingface_hub` library already present in the optional Python runtime instead of assuming that an `hf` command-line executable exists.
- An interrupted Gemma download remains in its model directory and can be retried; access-denied and runtime-missing errors are returned as actionable messages without crashing AltFactor.
- The installer creates the canonical AltFactor shortcuts without numbered duplicates. During an upgrade, it removes only the canonical legacy `AviUtl2 FAT.lnk` in the standard Start Menu or Desktop location, and only after it verifies both that link and its new AltFactor replacement point to this installation.
- Existing FAT plugin folders, AppData, models, and other user data are not renamed, moved, or deleted.

### V2.0.1 transition and performance update

- A clean installation now uses the `AviUtl2-AltFactor` plugin directory and `%LOCALAPPDATA%\AviUtl2AltFactor` for new persistent data.
- An existing `AviUtl2FAT` plugin or AppData directory is detected and used in place without renaming, copying, deleting, or redownloading models.
- The AI / speech model manager renders first and starts model/runtime checks after the window becomes visible.
- AI backends and the Python worker remain inactive until a user invokes an AI, model-management, or recognition operation.
- V2.1 Control Mode foundations are isolated in interfaces and a lightweight orchestration service; V2.0.1 does not start Control Mode or an AI model at startup.
- Compatibility identifiers, including the installer AppId and internal executable names, remain unchanged.

- Windows 10/11 x64 and AviUtl2 are supported.
- The release build is .NET self-contained: users do not need .NET SDK, Rust, Cargo, Node.js, Git, or a system Python installation.
- Whisper and Gemma model weights are intentionally not bundled and can only be acquired by an explicit Model Manager action.
- The installer is named `AviUtl2-AltFactor-Setup-x.x.x-x64.exe`. It preserves an existing `AviUtl2FAT` Plugin folder and uses `AviUtl2-AltFactor` only for a clean installation; it does not modify `.aup2` files, user projects, or other plugins.
- The V2.0 Stable release asset is `AviUtl2-AltFactor-Setup-2.0.0-x64.exe`.
- Existing logs and user settings remain in `%LOCALAPPDATA%\AviUtl2FAT`; a clean V2.0.1 installation uses `%LOCALAPPDATA%\AviUtl2AltFactor`.

For installation and normal use, read [QUICKSTART.md](QUICKSTART.md). For a pre-release gate and known release blockers, read [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) and [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).

## Developer installer verification

The manually triggered GitHub Actions workflow `Build AltFactor Installer` restores and tests the .NET, Python, and Rust components, builds the Windows installer with the existing `package-v1.0.ps1` and Inno Setup pipeline, verifies its Windows metadata, and uploads only the installer plus its SHA-256 file. It does not create a tag or GitHub Release. The downloaded artifact must still pass a manual AviUtl2FAT V1.1.7 to AviUtl2 AltFactor V2.0 upgrade test before a Stable release is published.

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
