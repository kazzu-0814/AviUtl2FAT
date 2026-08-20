# AviUtl2 FAT 1.0.0 release checklist

- [ ] Debug build passes with zero warnings/errors.
- [ ] Release build passes with zero warnings/errors.
- [ ] C# tests, Python tests, and `cargo check --offline` pass.
- [ ] `package-v1.0.ps1 -BuildPortablePython -BuildInstaller` passes.
- [ ] Portable Python imports faster-whisper without `pyvenv.cfg`.
- [ ] No model weight is in the installer payload.
- [ ] No development path is present in the payload.
- [ ] FFmpeg provenance and exact license/source information are included.
- [ ] Exact third-party license texts are included.
- [ ] Fresh install, upgrade, cancel, reinstall, uninstall, and AviUtl2 plugin tests pass.
- [ ] Speech, stop, edit, selected-text split/merge, and object export pass.
- [ ] No worker/python/ffmpeg process remains after stop.
