# One executable: desktop or headless export

`ContentCoHostStudio.exe` opens the Avalonia desktop app when started without arguments. Commands run headlessly **before Avalonia initialization**. The same executable and shared services handle both modes. CLI jobs can run while the desktop app is open; they do not share its in-memory key, selections, playback, or microphone.

```text
ContentCoHostStudio.exe --help
ContentCoHostStudio.exe avatars list
ContentCoHostStudio.exe models list
ContentCoHostStudio.exe narrate --request examples/narrate.json
ContentCoHostStudio.exe reply --request examples/reply.json
ContentCoHostStudio.exe narrate --request -
```

`--request -` reads UTF-8 JSON from stdin until EOF. A file path reads the same JSON from a file. Request and output paths are resolved against the **caller's working directory**, not the request-file folder. Input is bounded to 1,048,576 characters. Unknown fields and credential fields are rejected. A bad command returns a structured error; it does not open the GUI.

## Credentials and discovery

Generation and `models list` read `GEMINI_API_KEY` from the caller's process environment. The app never reads keys from job JSON, command-line flags, desktop memory, `.env` files, or a saved configuration. `--help` and `avatars list` need no key and make no network calls. `models list` explicitly fetches the account's paginated text/TTS catalog, without generating content. A listed model does not guarantee access or quota.

Use `avatars list` to find installed pack IDs and complete poses. The bundled `cfb_avatar_pack` is always available. CLI reads the same imported avatar library as the desktop app without importing or modifying packs. No Vosk model, microphone, speakers, desktop popout, Spout2 DLL, or running desktop instance is needed for CLI exports.

## Job requests

`narrate` speaks `text` as supplied, without invoking text generation. `reply` sends `context` and separate personality instructions to the selected text model, then synthesizes its reply. Supply only the input field for that command. Every job makes one speech request; it does not split scripts, retry automatically, record Live sessions, or silently change models.

| Field | Default / meaning |
| --- | --- |
| `text` | Required for `narrate`; the spoken script. |
| `context` | Required for `reply`; the content to react to. |
| `personality` | Existing friendly sarcastic robot prompt; affects reply wording only. |
| `output` | Required destination ending in `.webm`. Parent folders are created if needed. |
| `avatarId` | `cfb_avatar_pack`; see `avatars list`. |
| `pose` | `Normal`; must be available in the selected pack. |
| `voice` | `Puck`; supported values: Kore, Puck, Charon, Fenrir. |
| `voiceStyle` | Approved Sarcastic Droid directions; affects speech delivery only. |
| `textModel` | `gemini-3.6-flash`; used by `reply`. |
| `speechModel` | `gemini-3.8-flash-tts`; supported TTS protocols match the desktop app. |
| `size` | `1080`; supported square sizes are 1080 and 512. |
| `sensitivity` | `50`; mouth sensitivity, 0–100. |
| `idleBob`, `shake` | `0`; optional movement amounts, 0–100. |
| `overwrite` | `false`; existing output is refused before paid requests unless explicitly enabled. |
| `returnText` | `false`; explicitly include the spoken script/reply in the JSON result. |
| `ffmpegPath` | Optional explicit FFmpeg executable override; adjacent ffprobe is required. |

Voice, avatar, pose, model compatibility, destination writability, and FFmpeg capabilities are checked before paid requests. Personality and style remain separate. Model/service limits still apply, including the existing 60-second request timeouts and maximum validated speech audio size. The model can reject a long script; the tool never truncates it to fit.

## Output video and process protocol

The requested artifact is **30 FPS WebM**, **VP9/yuv420p video**, **Opus mono audio**, with an opaque **#00FF00 green background**. No alpha channel is encoded. Speech levels come from the completed WAV; fixed frame timestamps drive deterministic avatar animation. Nothing is played aloud. Desktop Auto-speak, loopback mute, active Live sessions, and desktop settings do not affect a CLI job. The exported narration is intentionally included in the file.

Key out green in the downstream editor. Green details in avatar artwork can also be keyed out. Video color conversion/compression means decoded green pixels may differ slightly from RGB `00FF00`. The clip follows the generated audio duration with normal frame/codec padding; the result reports the validated container duration. Export freezes the selected artwork revision and job settings.

Stdout contains **one UTF-8 JSON object followed by a newline**:

```json
{"schemaVersion":1,"success":true,"result":{"output":"C:\\clips\\cohost.webm","durationMs":2407,"width":1080,"height":1080,"fps":30,"background":"#00FF00","videoCodec":"vp9","audioCodec":"opus","avatarId":"cfb_avatar_pack","avatarRevision":"bundled","textModel":null,"speechModel":"gemini-3.8-flash-tts"}}
```

The `spokenText` field is omitted unless `returnText` is true. `textModel` is null for `narrate`. Failed commands instead emit:

```json
{"schemaVersion":1,"success":false,"error":{"code":"Generation","message":"A sanitized explanation."}}
```

Stderr carries newline-delimited JSON progress only, for example `{"schemaVersion":1,"stage":"rendering","percent":42}`. Stages include preflight, text-generation, speech-generation, audio-analysis, rendering, validating, completed, and cleanup-incomplete. They never contain scripts, prompts, keys, raw API responses, or raw FFmpeg diagnostics. Do not mix stderr into stdout when parsing results.

| Exit code | Meaning |
| --- | --- |
| 0 | Success, including discovery/help. |
| 2 | Invalid command/request, missing key, unavailable avatar/pose, or unusable destination. |
| 3 | Dependency or FFmpeg capability failure. |
| 4 | Text/speech generation or account model-list failure. |
| 5 | Encoding, validation, or final publication failure. |
| 6 | Unexpected/internal failure, including an unusable process I/O environment. |
| 130 | Graceful cancellation. |

The encoder writes an unfinished file beside the destination and publishes only after ffprobe validates container, codecs, dimensions, rate, audio, and duration. Existing destinations survive failed rendering and cancellation. An explicitly enabled successful overwrite replaces the file at publication.

Temporary generated WAV data uses a unique `ContentCoHostStudio-export-*` directory under the OS temp folder. It and unfinished videos are removed on normal completion/failure/cancellation. Ctrl+C cancels a job and requests child-process termination and cleanup. Forced termination, machine shutdown, locked files, or crashes can leave temporary artifacts; `cleanup-incomplete` reports cleanup errors while the process is still running. No microphone recordings are created.

## Calling from an AI pipeline

This Python example keeps the key out of shell history and request files, closes stdin after sending JSON, waits for completion, and captures stdout/stderr separately:

```python
import getpass
import json
import os
import subprocess

env = os.environ.copy()
env["GEMINI_API_KEY"] = getpass.getpass("Gemini API key: ")
job = {
    "text": "Well, that went almost to plan.",
    "output": "artifacts/cohost.webm",
    "size": 1080,
}
completed = subprocess.run(
    [r"src\ContentCoHostStudio\bin\Release\net10.0\ContentCoHostStudio.exe",
     "narrate", "--request", "-"],
    input=json.dumps(job), encoding="utf-8", capture_output=True, env=env,
)
result = json.loads(completed.stdout)
if completed.returncode:
    raise RuntimeError(result["error"]["message"])
video_path = result["result"]["output"]
```

Callers must explicitly wait for the process. Windows interactive shells can return early for GUI-subsystem executables. In PowerShell, with the key already in the process environment, use:

```powershell
$exe = (Resolve-Path '.\src\ContentCoHostStudio\bin\Release\net10.0\ContentCoHostStudio.exe').Path
$job = Start-Process -FilePath $exe -ArgumentList @('narrate', '--request', 'examples/narrate.json') -NoNewWindow -Wait -PassThru
$job.ExitCode
```

For automated JSON input/output and paths with spaces, prefer an argument-list process API such as Python `subprocess` or .NET `ProcessStartInfo.ArgumentList`. A plain double-click still opens the desktop app without a console window.

## Local encoder setup and verification

The existing tested Windows FFmpeg 9.0.2 folder has been copied from Stream Avatar Studio to ignored `native/FFmpeg/` and is copied beside the app at build/publish time. It includes ffmpeg, ffprobe, and their runtime DLLs. There is no runtime dependency on that other project. On a fresh checkout, supply a compatible local tool folder or set `ffmpegPath` in the job; no automatic downloads/installations occur. Required encoders are `libvpx-vp9` and `libopus`. Notices and source/build provenance are retained under `licenses/`.

Ordinary tests use fake Gemini HTTP, silent services, and temporary files. They also perform a network-free real encode/decode integration check when local FFmpeg is available. Paid end-to-end checks are opt-in:

```powershell
$env:CONTENT_COHOST_RUN_CLI_EXPORT_TEST = '1'
dotnet test ContentCoHostStudio.slnx -c Release --filter Category=LiveCliExport
Remove-Item Env:CONTENT_COHOST_RUN_CLI_EXPORT_TEST
```

These checks require `GEMINI_API_KEY`, generate one script clip and one reply clip, and remove their test videos afterward. Optionally set `CONTENT_COHOST_CLI_TEST_SPEECH_MODEL` and `CONTENT_COHOST_CLI_TEST_TEXT_MODEL` to supported explicit models for the check; clear them afterward. The application defaults are unaffected. Generation has normal Gemini usage charges. No desktop export button, background server, Live recording, long-script chunking, or timeline editor is included.

### Implementation verification (2026-11-12)

Release build, formatting verification, and all 188 ordinary tests passed (10 opt-in/platform checks skipped). The network-free FFmpeg test encoded and decoded a clip, checked green pixels and changing mouth frames, and verified cleanup and overwrite protection. The same executable passed redirected UTF-8 input/output checks and a native Ctrl+C check with stdin held open: it returned cancellation JSON and exit code 130. Launching without arguments opened the desktop avatar interface.

A paid 1080×1080 narration export passed with explicitly selected `gemini-3.8-flash-lite-tts`; its validated VP9/Opus WebM and a decoded frame were inspected. The initial default TTS check failed after roughly a minute. Paid reply checks reached text generation but failed; a direct check of one listed text model returned HTTP 404. Consequently, a successful paid **reply** export remains unverified. Model discovery succeeded, but listing alone does not prove generation access. Defaults remain unchanged, and no automatic fallback was added. Microphone, Live, OBS, and audible voice checks were not repeated for this CLI change.
