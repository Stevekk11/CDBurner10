# CDBurner10 — DVD Archiver & Optical Media Suite

Modernization of **CDBurnerXP 4.5.8** to **.NET 10** and **WinUI 3 (Windows App SDK 1.7)** with full functional parity for optical media authoring, burning, ripping, DVD archiving, and ISO tooling.

## Architecture

- **`src/CDBurner10.Core`** — UI-independent `.NET 10` (`win-x86`) core library:
  - Optical burning via **StarBurnX 15.7** (`StarBurn.dll` / `StarBurnX15.dll`) using Registration-Free COM (`Native.CsLib.manifest`).
  - Navigation-driven DVD reading via **`libdvdnav` + `libdvdread`** (`DvdNavigationWalker`, `DiscReadMode.Navigated`) and CSS decryption via **`libdvdcss`**, following the disc's own PGC/cell playback path and automatically selecting the longest main movie title.
  - **Unfinalized DVD-R / DVD+R / DVD-RW recovery** (`UnfinalizedDvdReader`), scanning and streaming raw MPEG-2 Program Stream sectors when no UDF / `VIDEO_TS.IFO` filesystem was finalized.
  - **Single-pass DVD → MP4** conversion (`DvdVideoConverter`) directly from an optical drive, `.iso`, or `VIDEO_TS` folder via **ffmpeg**, with automatic GPU hardware H.264 encoding (`h264_nvenc` / `h264_qsv` / `h264_amf` / `h264_mf` → `libx264` fallback), `bwdif` deinterlacing, title selection, audio/subtitle tracks, and optional burned-in subtitles.
  - Open-source audio playback, CD-DA ripping/CD-Text, ReplayGain, crossfading, and CUE/M3U/WPL/NRA support via **NAudio** (MIT), **TagLibSharp**, **FFmpeg**, and Win32 CD-DA IOCTLs (zero BASS DLLs).
  - Pure-managed `BIN` / `NRG` / `B5I` → `ISO` converters, `.dxp` / `.axp` project compatibility, disc spanning, cover printing, and JSON settings (with portable mode & legacy `UserSettings.ini` import).
- **`src/CDBurner10.Cli`** (`cdbxpcmd.exe`) — Full CLI parity (`--list-drives`, `--eject`, `--load`, `--wait-for-disc`, `--erase`, `--burn-data`, `--burn-audio`, `--burn-video`, `--burn-iso`, `--dvd-to-mp4`, `-sptd`).
- **`src/CDBurner10.App`** (`CDBurner10.exe`) — Unpackaged Windows 11 **WinUI 3 + Mica** desktop app featuring 12 cached pages (Home, Data Disc, Audio Disc, Video DVD, Burn ISO, Copy Disc, Erase Disc, Convert to ISO, DVD to MP4 with live Event Log, Disc & Drive Info, Print Cover, Settings), floating DropBox window, 13 Fluent `ContentDialog`s (including **Rip Audio CD** with **Export Tracks to...**), and automatic Audio Compilation / temp-rip session persistence across restarts.
- **`tests/CDBurner10.Tests`** — Automated xUnit test suite (71 tests) covering compilations, session persistence, ISO converters, `libdvdnav` / `libdvdcss`, DVD-to-MP4, unfinalized DVD recovery, disc spanning, settings, BASS initialization, and CLI execution.

## Build & Run

Requires **.NET 10 SDK** on Windows. Projects target `win-x86` to load the 32-bit native StarBurnX, BASS, and `libdvdnav`/`libdvdread`/`libdvdcss` libraries in-process.

DVD-to-MP4 conversion additionally requires **ffmpeg** and **ffprobe** on `PATH` (or placed next to `CDBurner10.exe`). Install with `winget install Gyan.FFmpeg`.

```powershell
# Build entire solution (Release, x86)
dotnet build CDBurner10.sln -c Release -p:Platform=x86

# Run tests
dotnet test tests/CDBurner10.Tests/CDBurner10.Tests.csproj -c Release -p:Platform=x86

# Publish self-contained GUI & CLI to ./deploy
dotnet publish src/CDBurner10.App/CDBurner10.App.csproj -c Release -p:Platform=x86 -o deploy
dotnet publish src/CDBurner10.Cli/CDBurner10.Cli.csproj -c Release -p:Platform=x86 -o deploy
```
