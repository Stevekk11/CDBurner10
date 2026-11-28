# CDBurner10 CLI Reference (`cdbxpcmd.exe`)

`cdbxpcmd.exe` is the 64-bit command-line interface for **CDBurner10**, designed with syntax compatibility for classic `cdbxpcmd` automation scripts alongside modern optical and DVD-to-MP4 capabilities. It uses the same `CDBurner10.Core` engine as the desktop app (Windows IMAPI2, SPTI SCSI MMC, NAudio, FFmpeg, and 64-bit `libdvdnav`/`libdvdread`/`libdvdcss`).

---

## Table of Contents

- [Installation & Prerequisites](#installation--prerequisites)
- [Command Syntax & Parsing Rules](#command-syntax--parsing-rules)
- [Device & Speed Selection](#device--speed-selection)
- [Command Summary](#command-summary)
- [Command Reference](#command-reference)
  - [`--list-drives`](#--list-drives)
  - [`--eject`](#--eject)
  - [`--load`](#--load)
  - [`--wait-for-disc`](#--wait-for-disc)
  - [`--erase`](#--erase)
  - [`--burn-iso`](#--burn-iso)
  - [`--burn-data`](#--burn-data)
  - [`--burn-audio`](#--burn-audio)
  - [`--burn-video`](#--burn-video)
  - [`--dvd-to-mp4`](#--dvd-to-mp4)
- [Disc Label Variables](#disc-label-variables)
- [Configuration, Portable Mode & Logging](#configuration-portable-mode--logging)
- [Exit Codes](#exit-codes)
- [Automation Examples](#automation-examples)

---

## Installation & Prerequisites

### Where to Find `cdbxpcmd.exe`

- **MSI Installer:** Installs `cdbxpcmd.exe` into `C:\Program Files\CDBurner10\` by default. Enabling the **Add CLI (cdbxpcmd) to System PATH** feature during installation allows running `cdbxpcmd` from any Command Prompt or PowerShell window.
- **Build from Source (.NET 10 SDK, `win-x64`):**
  ```powershell
  dotnet publish src/CDBurner10.Cli/CDBurner10.Cli.csproj -c Release -p:Platform=x64 -o deploy
  ```
  This outputs a self-contained `deploy\cdbxpcmd.exe` with native dependencies included.

### Optional External Tools (`--dvd-to-mp4`)

The `--dvd-to-mp4` command requires **`ffmpeg.exe`** and **`ffprobe.exe`**. CDBurner10 automatically locates them in:
1. The same directory as `cdbxpcmd.exe`, or its `ffmpeg\` / `tools\` subdirectories
2. Any directory in `PATH`
3. WinGet package directory (`%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg*`)
4. `C:\Program Files\ffmpeg\bin` or `C:\ProgramData\chocolatey\bin`

Install easily via WinGet:
```powershell
winget install Gyan.FFmpeg
```

---

## Command Syntax & Parsing Rules

```text
cdbxpcmd <command> [options...] [flags...]
```

### How Arguments Are Parsed

1. **Command (`args[0]`):**
   - Matched case-insensitively after stripping all `-` characters. For example, `--burn-data`, `-burn-data`, and `burndata` are all equivalent.
   - **Important:** If no arguments are supplied, `cdbxpcmd` prints `No command line arguments given.` and exits with `0`. If `args[0]` does not match a known command name, it falls back to `--eject`.
2. **Flags (no `:`):**
   - Any argument after the command without a colon (`:`) is treated as a case-insensitive boolean flag (e.g., `-verify`, `-eject`, `-close`, `-dao`).
3. **Key/Value Arguments (`:`):**
   - Split at the **first** colon into `<key>` and `<value>`, preserving drive-letter colons in Windows paths (e.g., `-iso:C:\out\disc.iso`).
   - Keys are matched case-insensitively.
4. **File & Folder Targets (`[target]`):**
   - `-file` and `-folder` support an optional bracketed target path on the disc:
     ```text
     -file[\DiscFolder]:C:\Local\Path\file.ext
     -folder[\DiscFolder]:C:\Local\Folder
     ```
5. **Quoting Paths with Spaces:**
   - Wrap the entire argument in double quotes when a path or label contains spaces:
     ```powershell
     cdbxpcmd --burn-data -device:0 "-folder[\My Docs]:C:\Users\Alex\My Documents" "-name:My Backup"
     ```

---

## Device & Speed Selection

### `-device:<letter|index>`

Selects the optical drive for all disc commands (and optionally as a source for `--dvd-to-mp4`).

| Value | Meaning |
| --- | --- |
| *Omitted* | Defaults to drive index `0` (first detected optical drive). |
| `0`, `1`, ... | Zero-based drive index as reported by `--list-drives`. |
| `E`, `E:`, `E:\` | Drive letter (case-insensitive; `--dvd-to-mp4` also accepts `\\.\E:`). |

> **Note:** An unrecognized `-device` value causes `cdbxpcmd` to print `The following command line argument is invalid: device` and exit.

### `-speed:<ratio>`

Sets the write speed multiplier for `--burn-iso`, `--burn-data`, `--burn-audio`, and `--burn-video` (e.g., `-speed:4`, `-speed:8`, `-speed:16`, `-speed:2.4`).

- Matches the numeric speed multiplier against `SupportedWriteSpeeds` reported by the drive for the currently inserted disc (using 1× = `150 KB/s` for CD, `1385 KB/s` for DVD, `4500 KB/s` for Blu-ray).
- If omitted or if the requested multiplier is not supported by the inserted media, `cdbxpcmd` defaults to `0` (drive maximum / automatic speed).

---

## Command Summary

| Command | Purpose |
| --- | --- |
| `--list-drives` | List available optical drives (optionally writers only). |
| `--eject` | Open the optical drive tray. |
| `--load` | Close the optical drive tray. |
| `--wait-for-disc` | Wait until the drive reports media ready. |
| `--erase` | Quick-erase a rewritable disc (`CD-RW`, `DVD-RW`, `DVD+RW`, `DVD-RAM`, `BD-RE`). |
| `--burn-iso` | Burn an existing `.iso` image file to disc. |
| `--burn-data` | Burn files, folders, or a `.dxp` layout to a data disc or `.iso` image. |
| `--burn-audio` | Burn an Audio CD (Red Book CD-DA) from audio files or a folder. |
| `--burn-video` | Burn a DVD-Video disc or `.iso` image from a `VIDEO_TS` folder. |
| `--dvd-to-mp4` | Convert a DVD drive, `.iso`, `VIDEO_TS` folder, or `.mpg` stream to `.mp4`. |

---

## Command Reference

### `--list-drives`

Enumerates optical drives detected by Windows IMAPI2.

```text
cdbxpcmd --list-drives [-writeonly]
```

**Flags:**
- `-writeonly` — Show only drives capable of writing optical media (`IsWriter == true`).

**Output format:**
```text
0: HL-DT-ST DVDRAM GH24NSD5 (E:\)
1: ASUS BW-16D1HT (F:\)
```

**Example:**
```powershell
cdbxpcmd --list-drives -writeonly
```

---

### `--eject`

Opens (ejects) the optical drive tray. Uses IMAPI2 `EjectMedia()` with a SCSI SPTI fallback.

```text
cdbxpcmd --eject [-device:<letter|index>]
```

**Options:**
- `-device:<letter|index>` — Target drive (default: `0`).

**Example:**
```powershell
cdbxpcmd --eject -device:E
```

---

### `--load`

Closes (loads) the optical drive tray. Uses IMAPI2 `CloseTray()` with a SCSI SPTI fallback.

```text
cdbxpcmd --load [-device:<letter|index>]
```

**Options:**
- `-device:<letter|index>` — Target drive (default: `0`).

**Example:**
```powershell
cdbxpcmd --load -device:0
```

---

### `--wait-for-disc`

Polls the target drive every 500 ms until the unit reports ready (`DriveModel.State == 0` via SCSI Test Unit Ready).

```text
cdbxpcmd --wait-for-disc [-device:<letter|index>]
```

**Options:**
- `-device:<letter|index>` — Target drive (default: `0`).

**Example:**
```powershell
cdbxpcmd --wait-for-disc -device:E
```

---

### `--erase`

Performs a **quick erase** on an inserted erasable disc (`CD-RW`, `DVD-RW`, `DVD+RW`, `DVD-RAM`, `BD-RE`, or `HD-DVD-RW`). Does not eject the tray after erasing.

```text
cdbxpcmd --erase [-device:<letter|index>]
```

**Options:**
- `-device:<letter|index>` — Target drive (default: `0`).

**Behavior:**
- Validates that a disc is loaded and is erasable.
- Prints erase percentage progress (`0%`–`100%`) and `Finished erasing the disc.`.

**Example:**
```powershell
cdbxpcmd --erase -device:0
```

---

### `--burn-iso`

Burns an existing `.iso` image file directly to optical media via IMAPI2. Automatically enables `ForceOverwrite` for overwritable rewritable media (`DVD+RW`, `BD-RE`, `DVD-RAM`).

```text
cdbxpcmd --burn-iso -file:<isoPath> [-device:<letter|index>] [-speed:<ratio>] [-close] [-eject]
```

**Options:**
- `-file:<isoPath>` *(required)* — Path to the `.iso` file to burn (uses the first valid `-file` argument).
- `-device:<letter|index>` — Target writer drive (default: `0`).
- `-speed:<ratio>` — Write speed multiplier (default: maximum).

**Flags:**
- `-close` — Finalize/close the disc after writing (`FinalizeDisc = true`). Omit to leave the disc unfinalized if supported by the media.
- `-eject` — Eject the tray after burning completes.

**Example:**
```powershell
cdbxpcmd --burn-iso -device:E "-file:D:\Images\ubuntu-24.04.iso" -speed:8 -close -eject
```

---

### `--burn-data`

Burns files, folders, or a `.dxp` compilation layout to a data disc (CD, DVD, or Blu-ray), or builds a standalone `.iso` file without requiring an optical drive.

```text
cdbxpcmd --burn-data [sources...] [options...] [flags...]
```

**Sources (provide `-layout` OR one or more `-file` / `-folder` arguments):**
- `-layout:<path.dxp>` — Loads a CDBurner10 / CDBurnerXP `.dxp` XML compilation (supports both modern and legacy 3.5 `.dxp` formats, including El Torito boot options, file system type, UDF version, and volume metadata).
  > **Note:** When `-layout` is used, `-file`, `-folder`, and `-import` arguments are ignored. Also, filesystem settings stored in the `.dxp` override `-udf` and `-format`, while `-name` overrides the layout's compilation name.
- `-file:<path>` — Adds a single file to the disc root (`\`).
- `-file[<targetFolder>]:<path>` — Adds a single file inside `<targetFolder>` on the disc (e.g., `-file[\Docs\2026]:C:\report.pdf`).
- `-folder:<path>` — Recursively adds a directory as a named subfolder at the disc root (e.g., `-folder:C:\Backup` creates `\Backup\...`).
- `-folder[<targetFolder>]:<path>` — Recursively adds a directory inside `<targetFolder>` (e.g., `-folder[\Archive]:C:\Backup` creates `\Archive\Backup\...`).

**Options:**
- `-device:<letter|index>` — Target writer drive (default: `0`). Ignored when `-iso` is specified.
- `-iso:<outputIsoPath>` — Creates an `.iso` file on disk instead of burning to a physical disc.
- `-name:<label>` — Sets the disc volume label. Supports [Disc Label Variables](#disc-label-variables). Truncated to `32` characters for ISO9660/Joliet or `16` characters for UDF; invalid characters (`\/:*?"<>|;`) are stripped.
- `-format:iso` — Explicitly selects **ISO9660 + Joliet** (default). Individual files are limited to `4 GiB - 1 byte` and filenames to `107` characters.
- `-udf:<version>` — Selects the **UDF** file system (supports files > 4 GiB and filenames up to `127` characters). Supported versions:
  - `1.02` *(default if value is omitted/unrecognized)*
  - `1.5`
  - `2.0`
  - `2.01`
  - `2.5`
  - `2.6` *(Note: Windows IMAPI2FS supports up to UDF 2.50 and falls back from `2.6` to `2.50`)*
- `-speed:<ratio>` — Write speed multiplier (default: maximum).

**Flags:**
- `-import` — Imports the existing session from an appendable multi-session disc on the target drive before adding `-file`/`-folder` items.
- `-changefiledates` — Replaces original file timestamps with the current UTC date/time on the disc.
- `-dao` — Use Disc-At-Once (`WriteMode.DAO96`).
- `-sao` — Use Session-At-Once (`WriteMode.SAO`).
- `-tao` — Use Track-At-Once (`WriteMode.TAO`).
- `-close` — Finalize/close the disc after burning (no further sessions can be appended).
- `-verify` — Perform sector-by-sector verification against the source image stream after burning.
- `-eject` — Eject the tray after burning completes.

**Examples:**
```powershell
# Burn folders to a finalized, verified UDF 2.50 disc
cdbxpcmd --burn-data -device:E "-folder[\Photos]:D:\Pictures\2026" "-file:D:\readme.txt" -udf:2.5 -name:ARCHIVE_$year -close -verify -eject

# Append a new session to an existing multi-session disc
cdbxpcmd --burn-data -device:0 -import "-folder[\Session2]:C:\NewFiles"

# Burn from a saved .dxp layout
cdbxpcmd --burn-data -device:0 "-layout:D:\Projects\Release.dxp" -close -verify

# Build a standalone ISO image without an optical drive
cdbxpcmd --burn-data "-folder:C:\Release\Build" -name:MY_APP -format:iso "-iso:D:\Images\my_app.iso"
```

---

### `--burn-audio`

Burns a Red Book **Audio CD** (44.1 kHz, 16-bit, stereo PCM) from audio files or a directory of audio files.

```text
cdbxpcmd --burn-audio [sources...] [-device:<letter|index>] [-speed:<ratio>] [-dao] [-close] [-eject]
```

**Supported Audio Formats:**
`.flac`, `.mpc`, `.wv`, `.ape`, `.mp3`, `.ogg`, `.wma`, `.wav`, `.aif`, `.aiff`, `.mp2`, `.bwf`, `.opus`, `.m4a`, `.mp4`, `.m4b`, `.m4p`, `.m4v`, `.m4r`, `.3gp`, `.aac`

**Sources:**
- `-file:<audioFilePath>` — Adds a single audio file to the tracklist in the order specified.
- `-folder:<dirPath>` — Scans the specified folder (top-level files only, non-recursive) and adds all supported audio files.

> **Note:** Playlist files (`.m3u`, `.cue`, etc.) are skipped by `--burn-audio`; pass individual audio files or a folder of audio files. Non-WAV files are automatically decoded and resampled to temporary 44.1 kHz 16-bit stereo WAV files before burning.

**Options:**
- `-device:<letter|index>` — Target CD writer drive (default: `0`).
- `-speed:<ratio>` — Write speed multiplier (default: maximum).

**Flags:**
- `-dao` — Burn in **Disc-At-Once (gapless)** mode via SCSI SAO/DAO cue sheet with CD-Text metadata read from audio file tags (falls back to IMAPI2 Track-At-Once if the drive does not support SAO cue sheets). When omitted, defaults to **Track-At-Once (TAO)** with 2-second pauses (with SCSI TAO fallback for open multi-session CD-R/CD-RW discs).
- `-close` — Finalize/close the Audio CD (`FinalizeDisc = true`).
- `-eject` — Eject the tray after burning completes.

**Examples:**
```powershell
# Burn a gapless Audio CD from a folder of FLACs
cdbxpcmd --burn-audio -device:E "-folder:D:\Music\Album" -dao -close -eject

# Burn specific tracks in order
cdbxpcmd --burn-audio -device:0 "-file:C:\Audio\01_intro.mp3" "-file:C:\Audio\02_main.wav" -speed:16 -close
```

---

### `--burn-video`

Burns a compliant **DVD-Video** disc (ISO9660 + UDF 1.02 bridge with `AUDIO_TS`, spec-ordered `VIDEO_TS` files, and consumer DVD compatibility padding) from a `VIDEO_TS` folder, or exports it to a DVD-Video `.iso` file.

```text
cdbxpcmd --burn-video -folder:<videoTsOrParentPath> [-device:<letter|index>] [-iso:<outputIsoPath>] [-name:<label>] [-speed:<ratio>] [-eject]
```

**Sources:**
- `-folder:<path>` *(required)* — Path to a `VIDEO_TS` directory or its parent folder containing `VIDEO_TS`. Uses the first valid `-folder` argument. The folder must contain valid `.IFO` and `.VOB` files (`.BUP` backup files are recommended).

**Options:**
- `-device:<letter|index>` — Target DVD writer drive (default: `0`). Ignored when `-iso` is specified.
- `-iso:<outputIsoPath>` — Creates a DVD-Video `.iso` image file instead of burning to disc.
- `-name:<label>` — Volume label (supports [Disc Label Variables](#disc-label-variables); defaults to `DVD_VIDEO`; truncated to 32 characters).
- `-speed:<ratio>` — Write speed multiplier (default: maximum).

**Flags:**
- `-eject` — Eject the tray after burning completes.

> **Note:** DVD-Video discs are always finalized and padded for standalone player compatibility.

**Examples:**
```powershell
# Burn a DVD-Video folder to disc
cdbxpcmd --burn-video -device:E "-folder:D:\Movies\VacationDVD\VIDEO_TS" -name:VACATION_2026 -speed:8 -eject

# Create a DVD-Video ISO image
cdbxpcmd --burn-video "-folder:D:\Movies\VacationDVD" -name:VACATION_2026 "-iso:D:\Images\vacation.iso"
```

---

### `--dvd-to-mp4`

Converts a DVD-Video source directly to an **H.264/AAC `.mp4`** file (`+faststart`) in a single pass via `ffmpeg` and `ffprobe`.

Capabilities include:
- **CSS & ARccOS resilience:** Automatically falls back to native 64-bit `libdvdnav` + `libdvdread` + `libdvdcss` navigation walking when `ffmpeg`'s `dvdvideo` demuxer cannot open a protected or bad-sector disc directly.
- **Unfinalized DVD recovery:** Automatically detects unfinalized `DVD-R` / `DVD+R` / `DVD-RW` discs lacking `VIDEO_TS.IFO` and streams raw MPEG-2 Program Stream sectors directly into FFmpeg.
- **GPU hardware acceleration:** Probes `h264_nvenc` → `h264_qsv` → `h264_amf` → `h264_mf` automatically, falling back to CPU `libx264`.

```text
cdbxpcmd --dvd-to-mp4 <source> [options...] [flags...]
```

**Source (specify one):**
- `-device:<letter|index>` — Physical optical drive by letter (`E`, `E:`, `E:\`, `\\.\E:`) or 0-based index (`0`).
- `-file:<path>` — DVD `.iso` image file, or an `.mpg` / `.mpeg` / `.m2p` MPEG-2 Program Stream file.
- `-folder:<path>` — `VIDEO_TS` directory or its parent disc root directory.

**Options:**
- `-out:<mp4Path>` — Output `.mp4` file path. If omitted:
  - For an optical drive: `<CurrentDirectory>\<VolumeLabel>.mp4` (falls back to `DVDMovie.mp4` if no volume label is available).
  - For a file or folder: same path/name as the source with extension changed to `.mp4`.
- `-title:<0..99>` — DVD title number to convert (`0` = auto-select longest main movie title via `libdvdnav`, `1..99` = explicit title; default: `0`).
- `-audio:<ordinal>` — Zero-based audio stream index (`0` = first track; default: `0`).
- `-sub:<ordinal>` — Zero-based subtitle stream index to **burn into** the video (hardsub overlay). By default, no subtitles are burned in.
- `-crf:<0..51>` — Video quality factor (`0` = lossless, `51` = lowest quality; default: `21`). Mapped per encoder (`-crf` for `libx264`, `-cq` for `h264_nvenc`, `-global_quality` for `h264_qsv`, `-qp_i`/`-qp_p` for `h264_amf`, quality `85`/`75`/`65` for `h264_mf`).
- `-preset:<name>` — Software `libx264` speed/compression preset (e.g., `ultrafast`, `veryfast`, `fast`, `medium`, `slow`, `veryslow`; default: `medium`).

**Flags:**
- `-sw` — Force CPU software encoding (`libx264`) instead of auto-probing GPU hardware encoders.
- `-no-downmix` — Keep original multi-channel surround audio (encodes AAC at `384k` when > 2 channels). By default, audio is downmixed to stereo (`-ac 2`) at `192k`.
- `-no-deinterlace` — Disable automatic `bwdif=0:-1:1` deinterlacing. By default, interlaced frames are deinterlaced.

**Examples:**
```powershell
# Convert main movie from optical drive E: using auto GPU encoding
cdbxpcmd --dvd-to-mp4 -device:E "-out:D:\Videos\Movie.mp4"

# Convert Title 2 from an ISO with second audio track, burned-in first subtitle, and software x264
cdbxpcmd --dvd-to-mp4 "-file:D:\ISOs\Concert.iso" "-out:D:\Videos\Concert.mp4" -title:2 -audio:1 -sub:0 -sw -crf:18 -preset:slow -no-downmix

# Convert from a VIDEO_TS folder with default output name
cdbxpcmd --dvd-to-mp4 "-folder:D:\Rips\MyDVD\VIDEO_TS"
```

---

## Disc Label Variables

When passing `-name:<label>` to `--burn-data` or `--burn-video`, the following case-insensitive placeholders are expanded using the current local date/time (with invalid filename characters sanitized):

| Variable | Expands To |
| --- | --- |
| `$user` | Current Windows username (`Environment.UserName`) |
| `$year` | 4-digit year (e.g., `2026`) |
| `$yr` | 2-digit year (e.g., `26`) |
| `$month` | 2-digit month (`01`–`12`) |
| `$day` | 2-digit day (`01`–`31`) |
| `$hour` | 2-digit hour (`00`–`23`) |
| `$minute` | 2-digit minute (`00`–`59`) |
| `$date` | Current culture short date string |
| `$time` | Current culture short time string |

If `-name` is omitted or whitespace:
- `--burn-data` expands blank labels to the current short date string (falling back to `Disc` if empty after sanitization).
- `--burn-video` defaults to `DVD_VIDEO`.

---

## Configuration, Portable Mode & Logging

On startup, `cdbxpcmd.exe` loads CDBurner10 settings from `settings.json`:

- **Standard mode:** `%LOCALAPPDATA%\CDBurner10\settings.json`
- **Portable mode:** `<ExeDirectory>\settings.json` (activated automatically if `portable.flag` exists next to `cdbxpcmd.exe`, or if `Config.ini` next to `cdbxpcmd.exe` contains `[CDBurnerXP]` with `Portable=1` or `Portable=true`).
- **Legacy migration:** If `settings.json` does not exist yet, CDBurner10 performs a one-time import from legacy `UserSettings.ini` (`%APPDATA%\Canneverbe Limited\CDBurnerXP\UserSettings.ini`, or `<ExeDirectory>\UserSettings.ini` in portable mode).
- **Diagnostic logging:** Controlled by `LoggingEnabled` and `LoggingLevel` in `settings.json` and initialized when `BurningEngine` starts.

---

## Exit Codes

| Exit Code | Meaning |
| --- | --- |
| `0` | Command succeeded (also returned when no arguments are given, or when a validation/argument message is printed for burning/drive commands). |
| `-1` | `--dvd-to-mp4` failed (missing `ffmpeg`/`ffprobe`, missing source, or FFmpeg/conversion error). |
| `HRESULT` (non-zero) | A Windows IMAPI2 / COM error occurred during drive or burning operations (printed in hex format: `An error (<HEX>) occured while executing the command: <message>`). |

---

## Automation Examples

### PowerShell: Unattended Backup Disc Burn

```powershell
$ErrorActionPreference = "Stop"

# 1. Wait until a disc is inserted and ready in drive E:
cdbxpcmd.exe --wait-for-disc -device:E

# 2. Burn backup folder using UDF 2.50, verify sectors, finalize, and eject
cdbxpcmd.exe --burn-data `
    -device:E `
    "-folder[\Backup]:D:\Backups\Weekly" `
    -udf:2.5 `
    "-name:BACKUP_$year-$month-$day" `
    -close `
    -verify `
    -eject

if ($LASTEXITCODE -ne 0) {
    throw "Burn failed with exit code $LASTEXITCODE"
}
```

### Batch (`.cmd`): Quick-Erase Rewritable Media & Burn ISO

```bat
@echo off
cdbxpcmd.exe --load -device:0
cdbxpcmd.exe --wait-for-disc -device:0
cdbxpcmd.exe --erase -device:0
if errorlevel 1 exit /b %errorlevel%

cdbxpcmd.exe --burn-iso -device:0 "-file:D:\Images\release.iso" -close -eject
if errorlevel 1 exit /b %errorlevel%
```

### PowerShell: Batch Convert a Folder of DVD ISOs to MP4

```powershell
Get-ChildItem "D:\DVD_ISOs\*.iso" | ForEach-Object {
    $outMp4 = [System.IO.Path]::ChangeExtension($_.FullName, ".mp4")
    Write-Host "Converting $($_.Name) -> $outMp4"
    cdbxpcmd.exe --dvd-to-mp4 "-file:$($_.FullName)" "-out:$outMp4" -crf:20
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to convert $($_.Name)"
    }
}
```
