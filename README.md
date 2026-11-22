# CDBurner10 dvd archiver and saver

Modernization; .NET 10** and **WinUI 3 (Windows App SDK 1.7)** with full functional parity for optical media authoring, burning, ripping, and ISO tooling.

## Architecture

- **`src/CDBurner10.Core`** — UI-independent `.NET 10` (`win-x86`) core library:
  - Optical burning via **StarBurnX 15.7** (`StarBurn.dll` / `StarBurnX15.dll`) using Registration-Free COM (`Native.CsLib.manifest`).
  - CSS-protected DVD-Video support via the bundled FOSS **libdvdcss** (`native/libdvdcss-2-32bit.dll`); Copy/Grab writes a decrypted, playable ISO.
  - **DVD → MP4** conversion of the longest/main title via **ffmpeg** (menus, warnings and advert titles are skipped), with audio/subtitle track selection and optional burned-in subtitles.
  - Audio playback, CDDA ripping, ReplayGain, crossfading, and CUE/M3U/WPL/NRA support via **BASS 2.4** (`ManagedBass` + native `bass*.dll` plugins).
  - Pure-managed `BIN` / `NRG` / `B5I` → `ISO` converters, `.dxp` / `.axp` project compatibility, disc spanning, cover printing, and JSON settings (with portable mode & legacy `UserSettings.ini` import).
- **`src/CDBurner10.Cli`** (`cdbxpcmd.exe`) — Full CLI parity (`--list-drives`, `--eject`, `--load`, `--wait-for-disc`, `--erase`, `--burn-data`, `--burn-audio`, `--burn-video`, `--burn-iso`, `--dvd-to-mp4`, `-sptd`).
- **`src/CDBurner10.App`** (`CDBurner10.exe`) — Unpackaged Windows 11 **WinUI 3 + Mica** desktop app featuring 12 pages (Home, Data Disc, Audio Disc, Video DVD, Burn ISO, Copy Disc, Erase Disc, Convert to ISO, DVD to MP4, Disc & Drive Info, Print Cover, Settings), floating DropBox window, and 13 Fluent `ContentDialog`s.
- **`tests/CDBurner10.Tests`** — Automated xUnit test suite covering compilations, ISO converters, disc spanning, settings, BASS initialization, and CLI execution.

## Build & Run

Requires **.NET 10 SDK** on Windows. Projects target `win-x86` to load the 32-bit native StarBurnX and BASS libraries in-process.

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
