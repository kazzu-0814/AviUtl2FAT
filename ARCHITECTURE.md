# FAT v0.7 アーキテクチャ

## プロセス境界

```text
AviUtl2
  └─ aviutl2-fat-plugin (.aux2 / Rust)
       └─ AviUtl2FAT.App.exe (WPF)
            └─ Named Pipe JSON Lines
                 └─ AviUtl2FAT.Worker.exe
                      └─ Python recognize.py / faster-whisper / FFmpeg
```

プラグインは FAT App を別プロセスで起動するだけです。Worker、Python、FFmpeg、Provider の失敗は IPC の `error` メッセージとして App に返され、AviUtl2 プロセスには未処理例外を伝播させません。

## データ境界

1. Python は ATT 互換の認識 JSON を出力します。
2. Worker/App は `TranscriptSegment` へ読み込みます。
3. `IAIProvider.GenerateCaptionsAsync` が `FATCaption` のみを返します。
4. App は `FATCaption.Text` をユーザーが編集できる DataGrid に表示します。
5. App は有効な字幕だけを ATT 互換の placement JSON へ書き出します。

`PassthroughProvider` は認識テキストと元トランスクリプトを保持します。将来の Provider は `IAIProvider` 実装を追加するだけで、Worker や UI の字幕編集契約を変更しません。
