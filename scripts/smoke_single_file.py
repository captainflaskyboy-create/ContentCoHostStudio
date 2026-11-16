"""Network-free checks of the delivered EXE alone, with a private bundle cache."""
import argparse
import concurrent.futures
import ctypes
import json
import os
import shutil
import subprocess
from pathlib import Path

from package_windows_release import ROOT, SPOUT_HASH, require, sha


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--work-dir", type=Path, required=True)
    args = parser.parse_args()
    work = args.work_dir.resolve()
    require(not work.exists(), "Use a fresh smoke-test directory")
    work.mkdir(parents=True)
    alone = work / "one file only"
    alone.mkdir()
    executable = alone / "CoHost.exe"
    shutil.copy2(args.exe, executable)
    cache = work / "bundle cache"
    env = os.environ.copy()
    for name in ("GEMINI_API_KEY", "CONTENT_COHOST_VOSK_MODEL"):
        env.pop(name, None)
    env.update(DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(cache), DOTNET_ROOT=str(work / "no-runtime"),
               DOTNET_ROOT_X64=str(work / "no-runtime"), DOTNET_MULTILEVEL_LOOKUP="0")

    def run(arguments, text="", expected=0):
        result = subprocess.run([str(executable), *arguments], input=text, capture_output=True,
                                encoding="utf-8", env=env, cwd=work, timeout=60)
        require(result.returncode == expected, "Bundled CLI returned an unexpected exit code")
        require(not result.stderr, "Unexpected startup diagnostic output")
        data = json.loads(result.stdout)
        require(data["schemaVersion"] == 1 and data["success"] == (expected == 0), "Invalid CLI result")
        return data

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        first = pool.submit(run, ["--help"])
        second = pool.submit(run, ["avatars", "list"])
        first.result()
        require(any(avatar["id"] == "cfb_avatar_pack" for avatar in second.result()["avatars"]), "Missing bundled avatar")
    run(["--help"])
    renamed = work / "moved and renamed"
    renamed.mkdir()
    moved = renamed / "My Co Host.exe"
    shutil.move(str(executable), moved)
    executable = moved
    run(["--help"])
    run(["narrate", "--request", "-"], json.dumps({"text": "café 中文 🤖", "output": str(work / "clip.webm")}), expected=2)
    require(list(renamed.iterdir()) == [executable], "Unexpected required sidecar files")
    # These paths were created exclusively for this invocation, outside the real .NET cache.
    require(cache.resolve().is_relative_to(work) and not cache.is_symlink(), "Unexpected cache cleanup path")
    shutil.rmtree(cache)
    run(["--help"])
    models = list(cache.rglob("models/vosk-model-small-en-us-0.15/am/final.mdl"))
    require(len(models) == 1, "The single EXE did not unpack its English model")
    base = models[0].parents[3]
    metadata = json.loads((base / "release.json").read_text(encoding="utf-8"))
    require(metadata["singleFile"] and metadata["automaticExtraction"], "Missing bundle metadata")
    require((base / "licenses/DotNet-10.0.15-THIRD-PARTY-NOTICES.TXT").is_file(), "Missing runtime notices")
    require((base / "models/vosk-model-small-en-us-0.15/LICENSE.txt").is_file(), "Missing model license")
    require(sha(base / "native/Spout2/SpoutLibrary.dll").upper() == SPOUT_HASH, "Incorrect Spout binary")
    for source in (ROOT / "native/FFmpeg").iterdir():
        if source.suffix.lower() in (".exe", ".dll"):
            require(sha(base / "native/FFmpeg" / source.name) == sha(source), "Incorrect bundled export tool")
    result = subprocess.run([str(base / "native/FFmpeg/ffmpeg.exe"), "-hide_banner", "-encoders"],
                            capture_output=True, encoding="utf-8", timeout=30, env=env)
    require(result.returncode == 0 and "libvpx-vp9" in result.stdout and "libopus" in result.stdout, "Missing WebM encoders")
    with os.add_dll_directory(str(base)):
        vosk = ctypes.CDLL(str(base / "libvosk.dll"))
        vosk.vosk_set_log_level(-1)
        vosk.vosk_model_new.argtypes = [ctypes.c_char_p]
        vosk.vosk_model_new.restype = ctypes.c_void_p
        vosk.vosk_recognizer_new.argtypes = [ctypes.c_void_p, ctypes.c_float]
        vosk.vosk_recognizer_new.restype = ctypes.c_void_p
        vosk.vosk_recognizer_free.argtypes = [ctypes.c_void_p]
        vosk.vosk_model_free.argtypes = [ctypes.c_void_p]
        model = vosk.vosk_model_new(str(models[0].parents[1]).encode())
        require(model, "Bundled Vosk model failed to load")
        try:
            recognizer = vosk.vosk_recognizer_new(model, 16000)
            require(recognizer, "Bundled Vosk recognizer failed to initialize")
            vosk.vosk_recognizer_free(recognizer)
        finally:
            vosk.vosk_model_free(model)
    print(json.dumps({"success": True, "version": metadata["version"], "commit": metadata["sourceCommit"],
                      "checks": ["one EXE only", "concurrent cold starts", "warm cache", "move/rename", "Unicode stdin",
                                 "cache recreation", "embedded notices", "native hashes", "Vosk model/recognizer", "VP9/Opus"]}))


if __name__ == "__main__":
    main()
