# FFmpeg binary provenance — v1.0.0 staging

The development runtime currently contains the following Windows x64 executables:

| File | Embedded version banner | Distributor / build line |
| --- | --- | --- |
| `runtime/ffmpeg/ffmpeg.exe` | `ffmpeg version 8.1.1-essentials_build-www.gyan.dev` | Gyan.dev essentials build |
| `runtime/ffmpeg/ffprobe.exe` | `ffprobe version 8.1.1-essentials_build-www.gyan.dev` | Gyan.dev essentials build |

SHA-256 at the time of v1.0.0 installer staging:

* `ffmpeg.exe`: `228D7A8556258DE907FDB55F36850078EBC7680B84EC30D84EA02E99BEC1D1EB`
* `ffprobe.exe`: `0FDE260F5ABD35C9CAFD96F594CC76365A780C1B73A90E35B6A3409EA1DB1BF0`

The embedded configure line contains both `--enable-gpl` and `--enable-version3`.
Gyan.dev describes its Windows builds as static GPLv3 builds. See
<https://www.gyan.dev/ffmpeg/builds/>.

The upstream FFmpeg source release for the matching FFmpeg release series is
available at <https://ffmpeg.org/releases/ffmpeg-8.1.1.tar.xz>.  This is a
source-access link only; because the distributor build also enables external
libraries, recipients must use the distributor's complete build configuration
and corresponding-source information when rebuilding the exact executable.

## Release gate

The GPLv3 text included in `release/licenses/GPL-3.0.txt` is copied into every
installer payload.  This project does not modify FFmpeg itself.
