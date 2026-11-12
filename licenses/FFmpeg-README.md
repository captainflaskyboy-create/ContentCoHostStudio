# Notices for the locally built Windows FFmpeg bundle

These files accompany the six non-system DLLs in the MSYS2 UCRT64 FFmpeg build recorded in `FFMPEG_BUNDLE.md`. They were copied from the **installed package versions** on 2026-10-03, except for libvpx, whose package does not install a license file; its license is from the upstream `v1.17.0` source tag. They are embedded in Windows development builds. They are not by themselves a complete corresponding-source package or a clearance to distribute the build.

| DLL | MSYS2 UCRT64 package | Notice in this folder |
| --- | --- | --- |
| `libopenh264-7.dll` | `mingw-w64-ucrt-x86_64-openh264` 2.6.0-2 | `OPENH264-LICENSE.txt` |
| `libopus-0.dll` | `mingw-w64-ucrt-x86_64-opus` 1.6.1-1 | `OPUS-LICENSE.txt` |
| `libvpx-1.dll` | `mingw-w64-ucrt-x86_64-libvpx` 1.17.0-1 | `LIBVPX-LICENSE.txt` |
| `libwinpthread-1.dll` | `mingw-w64-ucrt-x86_64-libwinpthread` 14.0.0.r426.g4564ee4b5-1 | `LIBWINPTHREAD-LICENSE.txt` |
| `libgcc_s_seh-1.dll` | `mingw-w64-ucrt-x86_64-libgcc` 16.2.0-4 | `LIBGCC-GPL3.txt`, `LIBGCC-RUNTIME-EXCEPTION.txt` |
| `libstdc++-6.dll` | `mingw-w64-ucrt-x86_64-libstdc++` 16.2.0-4 | `LIBSTDCXX-GPL3.txt`, `LIBSTDCXX-RUNTIME-EXCEPTION.txt` |

The installed notice source paths are `/ucrt64/share/licenses/{openh264,opus,libwinpthread,libgcc,libstdc++}`. The libvpx notice came from `https://raw.githubusercontent.com/webmproject/libvpx/v1.17.0/LICENSE`. The MSYS2 package pages provide each package's source-only tarball and build recipe: `https://packages.msys2.org/packages/`.
