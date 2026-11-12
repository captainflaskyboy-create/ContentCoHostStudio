# Windows portable releases

Download the Windows x64 ZIP from [GitHub Releases](https://github.com/captainflaskyboy-create/ContentCoHostStudio/releases), extract the entire folder, and run `ContentCoHostStudio.exe`. The runtime is included; keep the accompanying files beside the executable. This is an unsigned portable application, not an installer.

The download includes the English Vosk model, the verified Spout2 sender, and FFmpeg/ffprobe for CLI green-screen exports. OBS itself and its Spout2 receiver plugin are installed separately. Supply your own Gemini key; cloud features incur normal account usage charges. No credentials, transcripts, recordings, imported user avatars, or personal configuration are packaged. The bundled Captain Flasky Boy avatar is included.

The application code is MIT. Native tools and other dependencies retain their own licenses. FFmpeg 9.0.2 is a separate LGPL-3.0-or-later command-line tool; its matching source archives, build configuration, dependency source packages, and binary hashes are supplied in the release's `ffmpeg-sources.zip` asset. You can replace FFmpeg with a compatible build using the CLI request's `ffmpegPath`. See the included notices and [FFmpeg guidance](https://ffmpeg.org/legal.html).

## Producing a release

Use the existing local native tools/model assets. Nothing in the packager downloads models or installs software. Publish a clean source commit using the WindowsPortable profile, which pins the embedded .NET 10 runtime patch independently of the development SDK:

```powershell
dotnet publish src/ContentCoHostStudio/ContentCoHostStudio.csproj -p:PublishProfile=WindowsPortable -p:Version=1.0.0 -p:NuGetLockFilePath=../../artifacts/releases/v1.0.0/packages.publish.lock.json -o artifacts/releases/v1.0.0/publish
python scripts/package_windows_release.py --version 1.0.0 --publish-dir artifacts/releases/v1.0.0/publish --source-kit PATH_TO_EXISTING_VERIFIED_WINDOWS_SOURCE_ZIP
```

The source-kit input is the existing Stream Avatar Studio Windows source bundle containing the exact FFmpeg 9.0.2 archives/configuration. The packager checks source and binary hashes, retains only the FFmpeg-related materials, and normalizes historical local-user paths in build records. It excludes SoundFlow and other unrelated project code. Native files, source archives, publish output, and final ZIPs remain ignored by Git.

Run Release build, tests, and formatting verification. Extract the ZIP into a fresh directory with spaces in its path; check the same executable's `--help`, `avatars list`, desktop startup, and native dependencies without relying on a global .NET installation. Verify the WebM encoder is present. Validate ZIP contents, manifests, and SHA-256 checksums before uploading a draft release. Publish the draft only after uploads finish and asset hashes match.

SHA256SUMS.txt covers the application and FFmpeg source ZIPs. The application ZIP additionally contains a per-file manifest and release metadata identifying its source commit and runtime version. GitHub supplies the application source ZIP/tarball for the release tag separately.
