# Windows portable releases

Download the Windows x64 **EXE** from [GitHub Releases](https://github.com/captainflaskyboy-create/ContentCoHostStudio/releases) and double-click it. Starting with v1.0.1, everything needed by the application is bundled in that file: the .NET runtime, libraries, English model, Spout2 sender, FFmpeg/ffprobe, documentation, and dependency notices. No supporting folder needs to stay beside the EXE. This is an unsigned portable application, not an installer. v1.0.0's ZIP is the older folder-based alternative.

This is a single-file **distribution**, not a zero-disk-use application. Native libraries, the Vosk model, and child executables require filesystem paths. .NET automatically unpacks the embedded files under `%TEMP%/.net/<executable>/<bundle-id>` (or `DOTNET_BUNDLE_EXTRACT_BASE_DIR` when explicitly configured). First launch needs time and disk space to extract them; subsequent launches reuse the cache. Deleting the cache while the app is closed is safe; it is recreated from the EXE. Do not delete it while the app or a CLI job is running. The EXE can be moved or renamed and does not require administrator installation. Imported avatar artwork still uses the existing app-data library; credentials and conversations remain memory-only.

## Building the single-file download

With local native/model assets and the .NET 10.0.15 release runtime package already restored, commit the release source and run:

```powershell
python scripts/package_single_file.py --version 1.0.1
```

The helper prepares dependency notices, builds with `WindowsSingleFile`, and writes the EXE and `SHA256SUMS.txt` under ignored release artifacts. Only the EXE is needed to run; checksums and the FFmpeg source archive are optional downloads for verification/rebuilding. The FFmpeg binaries are unchanged from v1.0.0, whose matching source archive remains available at the link in the embedded `release.json` and release notes.

The profile uses .NET 10's `IncludeAllContentForSelfExtract` compatibility mode because the existing model/native-tool paths use `AppContext.BaseDirectory`. Do not enable trimming. On future runtime upgrades, recheck this mode and the filesystem-based components. Test the EXE alone in a clean folder, cold/warm cache starts, a moved/renamed EXE, concurrent CLI jobs, native dependency hashes, model loading, and desktop startup. The cache must be writable by the calling user; report filesystem/startup errors without exposing credentials or request text.

The download includes the English Vosk model, the verified Spout2 sender, and FFmpeg/ffprobe for CLI green-screen exports. OBS itself and its Spout2 receiver plugin are installed separately. Supply your own Gemini key; cloud features incur normal account usage charges. No credentials, transcripts, recordings, imported user avatars, or personal configuration are packaged. The bundled Captain Flasky Boy avatar is included.

The application code is MIT. Native tools and other dependencies retain their own licenses. FFmpeg 9.0.2 is a separate LGPL-3.0-or-later command-line tool; its matching source archives, build configuration, dependency source packages, and binary hashes are supplied in the release's `ffmpeg-sources.zip` asset. You can replace FFmpeg with a compatible build using the CLI request's `ffmpegPath`. See the included notices and [FFmpeg guidance](https://ffmpeg.org/legal.html).

## Producing a folder-based ZIP (legacy alternative)

Use the existing local native tools/model assets. Nothing in the packager downloads models or installs software. Publish a clean source commit using the WindowsPortable profile, which pins the embedded .NET 10 runtime patch independently of the development SDK:

```powershell
dotnet publish src/ContentCoHostStudio/ContentCoHostStudio.csproj -p:PublishProfile=WindowsPortable -p:Version=1.0.0 -p:NuGetLockFilePath=../../artifacts/releases/v1.0.0/packages.publish.lock.json -o artifacts/releases/v1.0.0/publish
python scripts/package_windows_release.py --version 1.0.0 --publish-dir artifacts/releases/v1.0.0/publish --source-kit PATH_TO_EXISTING_VERIFIED_WINDOWS_SOURCE_ZIP
```

The source-kit input is the existing Stream Avatar Studio Windows source bundle containing the exact FFmpeg 9.0.2 archives/configuration. The packager checks source and binary hashes, retains only the FFmpeg-related materials, and normalizes historical local-user paths in build records. It excludes SoundFlow and other unrelated project code. Native files, source archives, publish output, and final ZIPs remain ignored by Git.

Run Release build, tests, and formatting verification. Extract the ZIP into a fresh directory with spaces in its path; check the same executable's `--help`, `avatars list`, desktop startup, and native dependencies without relying on a global .NET installation. Verify the WebM encoder is present. Validate ZIP contents, manifests, and SHA-256 checksums before uploading a draft release. Publish the draft only after uploads finish and asset hashes match.

SHA256SUMS.txt covers the application and FFmpeg source ZIPs. The application ZIP additionally contains a per-file manifest and release metadata identifying its source commit and runtime version. GitHub supplies the application source ZIP/tarball for the release tag separately.
