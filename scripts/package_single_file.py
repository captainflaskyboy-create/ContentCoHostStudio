"""Build one self-contained Windows EXE, including data, tools, and notices.

Uses local native/model assets and existing NuGet packages. No GitHub operations.
"""
import argparse
import json
import re
import shutil
import subprocess
from pathlib import Path

from package_windows_release import ROOT, SPOUT_HASH, package_notices, require, sha


def publish(profile, destination, version, lock, documentation=None):
    arguments = [
        "dotnet", "publish", str(ROOT / "src/ContentCoHostStudio/ContentCoHostStudio.csproj"),
        "-p:PublishProfile=" + profile, "-p:Version=" + version,
        "-p:NuGetLockFilePath=" + str(lock), "-p:UseSharedCompilation=false",
        "-o", str(destination), "--no-restore",
    ]
    if documentation:
        arguments.append("-p:BundledDocumentationDirectory=" + str(documentation))
    subprocess.run(arguments, cwd=ROOT, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    require(re.fullmatch(r"\d+\.\d+\.\d+", args.version), "Use major.minor.patch")
    require(not subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip(),
            "Commit the release source before building")
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    output = (args.output_dir or ROOT / "artifacts/releases" / ("v" + args.version) / "single-file").resolve()
    output.mkdir(parents=True, exist_ok=True)
    executable = output / f"ContentCoHostStudio-v{args.version}-win-x64.exe"
    require(not executable.exists(), "Release executable already exists; use a fresh output directory")
    lock = output / "packages.publish.lock.json"
    staging = output / "staging"
    publish("WindowsPortable", staging, args.version, lock)
    require(sha(staging / "native/Spout2/SpoutLibrary.dll").upper() == SPOUT_HASH, "Spout SDK hash mismatch")
    record = (ROOT / "licenses/FFmpeg-FFMPEG_BUNDLE.txt").read_text(encoding="utf-8")
    binaries = re.findall(r"\| `([^`]+\.(?:exe|dll))` \| `([0-9A-F]{64})` \|", record)
    require(len(binaries) == 8, "Expected the eight recorded FFmpeg binaries")
    for name, expected in binaries:
        require(sha(staging / "native/FFmpeg" / name).upper() == expected, "FFmpeg binary hash mismatch")
    require((staging / "models/vosk-model-small-en-us-0.15/am/final.mdl").is_file(), "Missing English model")
    documentation = output / "bundle-content"
    documentation.mkdir()
    for name in ("LICENSE", "README.md", "CLI.md", "RELEASE.md", "THIRD_PARTY_NOTICES.md"):
        shutil.copy2(ROOT / name, documentation / name)
    shutil.copytree(ROOT / "examples", documentation / "examples")
    model = documentation / "models/vosk-model-small-en-us-0.15"
    model.mkdir(parents=True)
    shutil.copy2(ROOT / "licenses/Vosk-Apache-2.0.txt", model / "LICENSE.txt")
    assets = json.loads((ROOT / "src/ContentCoHostStudio/obj/project.assets.json").read_text(encoding="utf-8-sig"))
    package_notices(documentation, assets)
    (documentation / "release.json").write_text(json.dumps({
        "version": args.version, "sourceCommit": commit, "rid": "win-x64", "runtime": "10.0.15",
        "singleFile": True, "automaticExtraction": True,
        "ffmpegSources": "https://github.com/captainflaskyboy-create/ContentCoHostStudio/releases/download/v1.0.0/ContentCoHostStudio-v1.0.0-ffmpeg-sources.zip",
    }, indent=2) + "\n", encoding="utf-8")
    publish("WindowsSingleFile", output / "publish", args.version, lock, documentation)
    shutil.copy2(output / "publish/ContentCoHostStudio.exe", executable)
    digest = sha(executable)
    (output / "SHA256SUMS.txt").write_text(f"{digest}  {executable.name}\n", encoding="utf-8")
    print(json.dumps({"executable": str(executable), "bytes": executable.stat().st_size, "sha256": digest, "commit": commit}))


if __name__ == "__main__":
    main()
