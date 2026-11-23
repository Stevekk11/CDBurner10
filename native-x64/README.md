# 64-bit (`win-x64`) GPL Native Libraries (Staged for x64 Migration)

This folder stages official 64-bit (`pei-x86-64`, MSYS2 `mingw64`) builds of the open-source optical & DVD native libraries for the upcoming **Phase 3 / `win-x64` GPLv3** migration. It is not yet referenced by the active `win-x86` build (`native/`).

| Binary | Package Version | License | Role |
| :--- | :--- | :--- | :--- |
| `libdvdnav-4.dll` | `7.0.0-2` | GPL-2.0-or-later | DVD-Video PGC/cell navigation walker |
| `libdvdread-8.dll` | `7.1.1-1` | GPL-2.0-or-later | DVD-Video UDF/IFO reader |
| `libdvdcss-2.dll` | `1.6.0-1` | GPL-2.0-or-later | CSS decryption & raw unfinalized DVD-R sector reads |
| `libcdio-21.dll` | `2.4.0-1` | GPL-3.0-or-later | Optical disc & CD-DA access |
| `libcdio_cdda-2.dll` | `10.2+2.0.2-2` | GPL-3.0-or-later | Audio CD track interface |
| `libcdio_paranoia-2.dll` | `10.2+2.0.2-2` | GPL-3.0-or-later | Jitter-corrected paranoid Audio CD ripping |
| `libiso9660-13.dll` | `2.4.0-1` | GPL-3.0-or-later | ISO9660 image reading |
| `libudf-0.dll` | `2.4.0-1` | GPL-3.0-or-later | UDF filesystem reading |
| `libiconv-2.dll` | MSYS2 `mingw64` | LGPL-2.1-or-later | Character set conversion (`libcdio` dependency) |
| `libgcc_s_seh-1.dll` | `16.2.0-7` | GPL-3.0 with GCC Runtime Exception | MinGW-w64 SEH runtime |
| `libwinpthread-1.dll` | `12.0.0` | MIT | MinGW-w64 POSIX threads runtime |
