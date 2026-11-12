"""Package an already published Windows build and its verified native source kit.

No downloads, credentials, or GitHub operations. Requires Python 3.11+.
"""
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE_HASHES = {
    "ffmpeg-9.0.2.tar.xz": "8C3850283EB25FA026482078A04051E0BE17347B09EF81A0849BEC15A96E002E",
    "mingw-w64-gcc-16.2.0-4.src.tar.zst": "A1792A44764405BC365746E0E60F0DAA122076CEC0DDDD35A781D141F94273CF",
    "mingw-w64-libvpx-1.17.0-1.src.tar.zst": "7B322FFFE50501D26BBBB489135E5001F6096DA2529AD992C712C00C94BB38DD",
    "mingw-w64-openh264-2.6.0-2.src.tar.zst": "7ED237E1B2E8F8A4A1542E294414C9C557AD690E60140C8540A9B1D55588B850",
    "mingw-w64-opus-1.6.1-1.src.tar.zst": "F9F4F73A700EDCD35E11D7F47BCB164C850B781FA2CCCB48CD30D505262B43A2",
    "mingw-w64-winpthreads-14.0.0.r426.g4564ee4b5-1.src.tar.zst": "5493832DE1B7E24F09EDB556ED409091B0844F9677212C4865618D6F93C508BE",
}
SPOUT_HASH = "81BC61C29872CBAA6CE7442C9B2A94765F31EB6563A526587FA158ADA4748546"


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def normalized_record(data):
    text = data.decode("utf-8-sig", errors="replace")
    text = re.sub(r"(?i)(/c/Users/|C:[\\/]Users[\\/])[^/\\\s]+", r"\1build", text)
    return text.encode("utf-8")


def source_archive(source_kit, publish, destination):
    entries = {}
    with zipfile.ZipFile(source_kit) as source:
        for name, expected in SOURCE_HASHES.items():
            data = source.read(name)
            require(hashlib.sha256(data).hexdigest().upper() == expected, f"Source hash mismatch: {name}")
            entries[name] = data
        checksums = source.read("FFmpeg-Windows-binary-SHA256SUMS.txt").decode("utf-8-sig")
        binaries = re.findall(r"([0-9A-Fa-f]{64})\s+([^\s]+)", checksums)
        require(len(binaries) == 8, "Expected the eight recorded Windows FFmpeg binaries")
        for expected, name in binaries:
            require(Path(name).name == name, "Unsafe binary record")
            require(sha(publish / "native" / "FFmpeg" / name).upper() == expected.upper(), f"Binary mismatch: {name}")
        for name in ["ffmpeg-config.mak", "FFMPEG_WINDOWS_BUILD_STEPS.md", "COPYING.LGPLv2.1", "COPYING.LGPLv3"]:
            entries[name] = normalized_record(source.read(name))
        entries["FFmpeg-Windows-binary-SHA256SUMS.txt"] = checksums.encode("utf-8")
        for name in source.namelist():
            if name.startswith("windows-dll-notices/") and not name.endswith("/"):
                entries[name] = normalized_record(source.read(name))
    entries["README.md"] = (
        "# Content CoHost Studio: FFmpeg source materials\n\n"
        "These are the exact FFmpeg 9.0.2 and MSYS2 dependency source archives for the "
        "separate Windows export tools distributed with Content CoHost Studio. "
        "The build configuration and recorded binary hashes match the shipped tools. "
        "FFmpeg is LGPL-3.0-or-later; dependency licenses and build recipes are retained.\n\n"
        "The build originally served Stream Avatar Studio. Local-user paths in historical "
        "build records have been normalized to 'build'; the source archives and binaries "
        "are unchanged. SoundFlow and unrelated application components are excluded. "
        "See FFMPEG_WINDOWS_BUILD_STEPS.md, ffmpeg-config.mak, and windows-dll-notices.\n"
    ).encode()
    entries["MANIFEST.sha256"] = "".join(
        f"{hashlib.sha256(data).hexdigest()}  {name}\n" for name, data in sorted(entries.items())
    ).encode()
    with zipfile.ZipFile(destination, "x", compression=zipfile.ZIP_STORED) as output:
        for name, data in entries.items():
            output.writestr(name, data)


def package_notices(stage, assets):
    package_roots = [Path(path) for path in assets["packageFolders"]]
    mit = (ROOT / "LICENSE").read_text(encoding="utf-8")
    for key, library in assets["libraries"].items():
        if library.get("type") != "package":
            continue
        package = next(root / library["path"] for root in package_roots if (root / library["path"]).is_dir())
        target = stage / "licenses" / "packages" / key.replace("/", "-")
        target.mkdir(parents=True)
        for nuspec in package.glob("*.nuspec"):
            shutil.copy2(nuspec, target / nuspec.name)
            metadata = ET.parse(nuspec).getroot().find("{*}metadata")
            license_node = metadata.find("{*}license")
            if license_node is not None and license_node.text == "MIT":
                copyright_text = metadata.findtext("{*}copyright") or metadata.findtext("{*}authors")
                require(bool(copyright_text), f"Missing MIT attribution: {key}")
                (target / "MIT-LICENSE.txt").write_text(
                    mit.replace("Copyright (c) 2026 Captain Flasky Boy", copyright_text), encoding="utf-8"
                )
        for file in package.rglob("*"):
            if file.is_file() and any(word in file.name.lower() for word in ("license", "notice", "copying")):
                if file.suffix.lower() in (".dll", ".so", ".dylib", ".nupkg"):
                    continue
                relative = file.relative_to(package)
                (target / relative).parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(file, target / relative)
    runtime = next(root / "microsoft.netcore.app.runtime.win-x64" / "10.0.15" for root in package_roots
                   if (root / "microsoft.netcore.app.runtime.win-x64" / "10.0.15").is_dir())
    for name in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
        shutil.copy2(runtime / name, stage / "licenses" / ("DotNet-10.0.15-" + name))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--publish-dir", required=True, type=Path)
    parser.add_argument("--source-kit", required=True, type=Path)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    require(re.fullmatch(r"\d+\.\d+\.\d+", args.version), "Use a numeric major.minor.patch version")
    require(not subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip(),
            "Commit the release source before packaging")
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    publish = args.publish_dir.resolve()
    require((publish / "ContentCoHostStudio.exe").is_file(), "Publish the application first")
    runtime = json.loads((publish / "ContentCoHostStudio.runtimeconfig.json").read_text(encoding="utf-8"))
    require(runtime["runtimeOptions"]["includedFrameworks"][0]["version"] == "10.0.15", "Use the pinned release runtime")
    require(sha(publish / "native/Spout2/SpoutLibrary.dll").upper() == SPOUT_HASH, "Spout SDK hash mismatch")
    require((publish / "models/vosk-model-small-en-us-0.15/am/final.mdl").is_file(), "Missing bundled English model")
    require((publish / "libvosk.dll").is_file(), "Missing Windows Vosk native library")
    output = (args.output_dir or ROOT / "artifacts/releases" / ("v" + args.version) / "package").resolve()
    output.mkdir(parents=True, exist_ok=True)
    stem = f"ContentCoHostStudio-v{args.version}"
    sources = output / f"{stem}-ffmpeg-sources.zip"
    source_archive(args.source_kit, publish, sources)
    stage = output / f"{stem}-win-x64"
    require(not stage.exists(), "Staging folder already exists; use a fresh output directory")
    shutil.copytree(publish, stage, ignore=shutil.ignore_patterns("*.pdb"))
    for path in (ROOT / "licenses").iterdir():
        if path.is_file():
            shutil.copy2(path, stage / "licenses" / path.name)
    for name in ("LICENSE", "README.md", "CLI.md", "RELEASE.md", "THIRD_PARTY_NOTICES.md"):
        shutil.copy2(ROOT / name, stage / name)
    shutil.copytree(ROOT / "examples", stage / "examples")
    shutil.copy2(ROOT / "licenses/Vosk-Apache-2.0.txt", stage / "models/vosk-model-small-en-us-0.15/LICENSE.txt")
    assets = json.loads((ROOT / "src/ContentCoHostStudio/obj/project.assets.json").read_text(encoding="utf-8-sig"))
    package_notices(stage, assets)
    (stage / "README-FIRST.txt").write_text(
        "Content CoHost Studio " + args.version + " - Windows x64 portable\n\n"
        "Extract the complete ZIP before running ContentCoHostStudio.exe. No .NET installation is needed.\n"
        "Keep all DLLs, models, native tools, and licenses alongside the app. This build is unsigned.\n"
        "Desktop: double-click the EXE. CLI: see CLI.md and examples/.\n"
        "Supply your own Gemini key; cloud usage incurs account charges. No key is included.\n"
        "Automatic visits are off at startup. Enable Live, configure interval/duration/length, then Start Automatic Visits.\n"
        "The English caption model, Captain Flasky Boy, Spout sender, and FFmpeg export tools are included.\n"
        "OBS and its Spout2 receiver plugin are installed separately. Use headphones with microphone input.\n"
        "FFmpeg is a separate LGPL tool. Matching source materials are provided beside this download:\n"
        f"https://github.com/captainflaskyboy-create/ContentCoHostStudio/releases/download/v{args.version}/{sources.name}\n"
        "See RELEASE.md and THIRD_PARTY_NOTICES.md for dependency terms and usage limitations.\n", encoding="utf-8"
    )
    (stage / "release.json").write_text(json.dumps({
        "version": args.version, "sourceCommit": commit, "runtime": "10.0.15", "rid": "win-x64",
        "selfContained": True, "ffmpegSources": sources.name,
    }, indent=2) + "\n", encoding="utf-8")
    files = sorted(path for path in stage.rglob("*") if path.is_file())
    require(not any(path.is_symlink() for path in files), "Linked files are not allowed in releases")
    (stage / "FILES.sha256").write_text("".join(f"{sha(p)}  {p.relative_to(stage).as_posix()}\n" for p in files), encoding="utf-8")
    app_zip = output / (stage.name + ".zip")
    with zipfile.ZipFile(app_zip, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(stage.rglob("*")):
            if file.is_file():
                archive.write(file, (Path(stage.name) / file.relative_to(stage)).as_posix())
    for path in (app_zip, sources):
        with zipfile.ZipFile(path) as archive:
            require(archive.testzip() is None, "ZIP integrity check failed")
    (output / "SHA256SUMS.txt").write_text("".join(f"{sha(p)}  {p.name}\n" for p in (app_zip, sources)), encoding="utf-8")
    print(json.dumps({"commit": commit, "assets": [{"name": p.name, "bytes": p.stat().st_size, "sha256": sha(p)}
                                                  for p in (app_zip, sources, output / "SHA256SUMS.txt")]}))


if __name__ == "__main__":
    main()
