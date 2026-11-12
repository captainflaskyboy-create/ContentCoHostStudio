# Content CoHost Studio

**Windows download:** get the portable ZIP from [GitHub Releases](https://github.com/captainflaskyboy-create/ContentCoHostStudio/releases), extract it, and run `ContentCoHostStudio.exe`. The .NET runtime, English caption model, and local export tools are included. See [RELEASE.md](RELEASE.md) for packaging and dependency details.

A minimal **C# / .NET 10 LTS / Avalonia** desktop proof of concept. Speak into the default Windows microphone, see offline live captions, edit the finalized transcript, and click **Generate Response** for a personality-driven Gemini reply with expressive speech.

The co-host now has a PNGtuber avatar, compatible imported avatar-pack library, draggable transparent desktop popout, and optional Windows x64 Spout2 output for OBS.

The **same executable** also supports headless `narrate` and `reply` commands that create narrated green-screen WebM clips for AI-assisted pipelines. Run without arguments for the desktop app; see [CLI.md](CLI.md) for JSON requests, environment-key authentication, exit codes, examples, and Windows process-waiting guidance.

## PNGtuber avatar and pack library

The **Co-host Avatar** controls are at the top of the main window. Captain Flasky Boy is bundled and selected on each launch. The mouth follows **co-host audio actually playing** from Standard TTS, cached Replay, or Gemini Live. Microphone and YouTube input do not animate it. Preparing speech stays idle; Stop, interruption, expiry, playback failure, and mute reset mouth activity. Computer-audio feedback mute also keeps the mouth idle: Standard speech is suppressed, and muted Live audio is discarded while reply text remains visible.

Select a **Pose** from complete expressions in the chosen pack. Optional idle/blink and small/normal/large mouth sequences are used when available. **Mouth sensitivity**, **Idle bob**, and **Talking shake** affect the shared animation; movement starts off. The in-app preview, desktop avatar, and Spout2 output follow the same immutable snapshot. Playback metering uses the output device's played-byte position rather than queued audio. Device buffering and UI scheduling still impose a small visual delay.

**Import Avatar Pack** accepts either an original AvatarPackBuilder-style folder or a selected installed Stream Avatar Studio pack folder containing `pack.json` and its current `revisions/<revision>` artwork. It copies validated PNGs and optional ICO into this app's separate `%APPDATA%\ContentCoHostStudio\Avatars` library. It never edits the source folder or Stream Avatar Studio's library. Choose installed avatars from the dropdown; **Rename** changes library metadata, **Remove** removes this app's copy, and re-importing the same pack ID offers confirmed replacement. Failed imports leave the previous avatar active. The bundled starter cannot be renamed or removed. Import and replacement select the imported pack; pose selection is retained where available.

Required PNGs are `normal_idle001.png` and `normal_talk001.png`. Additional contiguous `001`–`099` frames, `<pose>_idle_blink001.png`, `<pose>_talk_small001.png`, `<pose>_talk_large001.png`, and complete `<pose>_idle001.png`/`<pose>_talk001.png` sets are recognized. No user-authored manifest is needed for raw folders. Folder IDs use letters, numbers, hyphens, and underscores; linked folders/files are rejected. Limits are 2,000 frames, 20 MB per artwork file, 500 MB total, and 4096×4096 PNG dimensions. Decoded image caches are limited to 64 MiB per renderer. A pack can contain an optional ICO, preferably `<pack-folder-name>_app_icon.ico`; ICO data is preserved rather than changing this app's identity. GIF events, a pack editor, automatic emotion detection, and `.pstproj` projects are not included.

Imported artwork and avatar metadata persist, and explicit CLI export jobs write their requested WebM artifacts. Selected avatar, pose, movement, sensitivity, desktop position/size, and Spout2 enable state remain session-only. Credentials, transcripts, generated text, and audio are never included in pack metadata. CLI export uses cleaned-up temporary generated audio/video files as documented in [CLI.md](CLI.md).

## Desktop avatar and OBS Spout2

Click **Show Desktop Avatar** for a 420×420 transparent, borderless, initially always-on-top window. Drag the visible character to move it. Right-click for Larger, Smaller, Reset Size, Always on Top, and Close; Escape also closes the popout. Size is bounded to 120–1800 pixels. Transparent areas use a Win32 opaque-pixel region so clicks can reach applications beneath them. Unsupported transparency/region behavior is reported through the popout tooltip with a rectangular fallback. Closing/reopening the popout does not stop speech or Spout2. Closing the main app releases both outputs and playback; graphics cleanup runs off the UI thread with a bounded wait.

**Enable Spout2 output to OBS** starts a separate **512×512, 30 FPS, straight-alpha BGRA** sender named `ContentCoHostStudio` (the SDK may disambiguate the name if another instance is running). Desktop size does not affect sender dimensions. Spout2 starts off each launch, can operate with the popout closed, and shares video only. Its status confirms the sender is running, not whether OBS is receiving it. Configure audio capture separately in OBS. Missing or failed Spout2 leaves preview, popout, captions, and speech available.

The local verified sender is the x64 MT `SpoutLibrary.dll` from [Spout SDK 2.007.017](https://github.com/leadedge/Spout2/tree/2.007.017), SHA-256 `81BC61C29872CBAA6CE7442C9B2A94765F31EB6563A526587FA158ADA4748546`. A local copy has been placed at the ignored `native/Spout2/win-x64/SpoutLibrary.dll` and is copied beside builds at `native/Spout2/SpoutLibrary.dll`. The application verifies this hash before loading; other binaries are rejected. The SDK DLL is not committed. On a fresh checkout, manually supply that tested DLL in the ignored source location and rebuild. Nothing downloads or installs automatically. License notices are in `licenses/` and accompany build/publish output.

OBS needs the separately installed [Spout2 receiver plugin](https://github.com/Off-World-Live/obs-spout2-plugin). Add/select a **Spout2 Capture** source, choose the displayed sender, and set **Composite mode → Default** for transparency; Opaque hides alpha. Keep both applications on the same GPU. If the source is absent, verify the plugin matches your OBS version. No browser-output server, OBS remote control, streaming, or recording is started by this app.

Verification during implementation: real native sender/receiver pixel comparisons passed for transparency, BGRA orientation, flips/rotations, and stop/restart. OBS 31.0.3 displayed the avatar through an existing Spout2 source while the popout was closed. Native desktop transparency, dragging to a new screen position, context-menu sizing, and closing/reopening were inspected. A native audio check confirmed WAV and streamed PCM output-clock activity and Stop reset. Opaque-pixel geometry was tested at 100%, 150%, and 200% scales in software; cross-monitor DPI changes and manual transparent-area click-through still require user testing. The symbolic-link creation test is skipped when Windows does not permit creating test links.

Offline tests use temporary pack libraries, fake senders, and silent playback. The opt-in `CONTENT_COHOST_SPOUT_HARDWARE_TESTS=1` check runs with `dotnet test ContentCoHostStudio.slnx -c Release --filter Category=NativeSpout`; `CONTENT_COHOST_AVATAR_AUDIO_TEST=1` with `--filter Category=NativeAvatar` plays two short quiet in-memory tones to verify real playback activity. Neither uses the network or microphone. Clear opt-in variables afterward.

Clicking Generate sends the saved transcript and personality prompt to Google. Auto-speak or Replay / Speak sends the reply, selected voice, and voice style to Google's speech API. Local recognition uses Vosk on your computer. The optional **Use Live API** experiment can stream your selected microphone or computer-audio input to Google, only during a manually started session or an explicitly armed automatic visit, with a configurable 1–600 second deadline (30 seconds by default). Audio stays in memory; the app does not record audio files or persist the API key, transcript, personality, voice settings, or response.

## 1. Windows prerequisites

- Windows 10/11 **x64**, a microphone, and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). An SDK builds the app; installing only the runtime is insufficient. Any supported stable .NET 10 SDK feature band can be used through `global.json`.
- Internet access to NuGet for the first package restore, to download the English model, and for Gemini requests.
- Allow microphone access and **Let desktop apps access your microphone** in Windows Settings → Privacy & security → Microphone. Choose your input device under Settings → System → Sound.

Visual Studio is optional. Neither Rust nor the Visual Studio C++ build toolchain is required for the normal NuGet-based setup.

From PowerShell in this repository:

```powershell
dotnet --version
dotnet restore ContentCoHostStudio.slnx
dotnet build ContentCoHostStudio.slnx
```

Use an x64 terminal/SDK. This POC does not support Windows ARM64 or x86 native Vosk execution.

## 2. Install the offline English model

Download **vosk-model-small-en-us-0.15** from the [official Vosk model list](https://alphacephei.com/vosk/models). Its download is about 40 MB and the model is Apache 2.0 licensed. Small models typically need a few hundred MB of RAM.

Extract the ZIP into a `models` folder in this repository. Avoid accidentally creating two nested folders with the same name. The layout must include:

```text
ContentCoHostStudio/
  models/
    vosk-model-small-en-us-0.15/
      am/final.mdl
      conf/
      graph/
      ivector/
```

The project automatically copies this local `models` folder beside the executable when building or publishing. After extraction, simply run:

```powershell
dotnet run --project src/ContentCoHostStudio
```

To use a model in a different folder, optionally override its location in the same PowerShell terminal:

```powershell
$env:CONTENT_COHOST_VOSK_MODEL = (Resolve-Path '.\models\vosk-model-small-en-us-0.15').Path
dotnet run --project src/ContentCoHostStudio
```

This environment variable lasts only for that terminal session. The app never downloads or installs the model for you.

If the environment variable is absent, the app looks for `models/vosk-model-small-en-us-0.15` **beside the executable**, not in the shell's working directory. Normal builds copy the model there from the repository's ignored `models` folder. You can also place the model beside an existing executable manually.

### Native Vosk libraries

The pinned official `Vosk` NuGet package includes Windows x64 `libvosk.dll`, `libgcc_s_seh-1.dll`, `libstdc++-6.dll`, and `libwinpthread-1.dll`. Its build target copies them beside the application. The speech model is separate and is not included in NuGet.

If native loading fails, confirm that you built on Windows with an x64 SDK and that **all four DLLs** are present beside `ContentCoHostStudio.exe`. Restore and rebuild if any are missing. If you need to replace native binaries manually, use the Windows x64 archive linked from the [official Vosk releases](https://github.com/alphacep/vosk-api/releases) and keep its DLLs together; do not mix architectures or versions. See the [Vosk C# examples](https://github.com/alphacep/vosk-api/tree/master/csharp).

This project uses the official C# wrapper's published version 0.3.38. It is older than some other Vosk language bindings; native compatibility and packaging must be checked when upgrading it. No custom C/C++ wrapper or application-written unsafe code is used.

## 3. Get a Gemini key

Create a key in [Google AI Studio](https://aistudio.google.com/apikey), then paste it into the app's masked **Gemini API Key** field. Do not put the key in source code, screenshots, configuration files, or Git. The app keeps it in memory only; it does not provide a secure OS credential store.

Gemini availability, quotas, and charges depend on your Google account and region. Check Google's [pricing](https://aistudio.google.com/docs/pricing) and service terms before use.

The initial **text response** model is **`gemini-3.6-flash`**. The initial **speech** model is **`gemini-3.8-flash-tts`**. These are two separate API calls and can appear separately in Google's usage report. Select them independently in **Text Response Model** near Generate and **TTS Model** under Co-host Voice. The Python diagnostic retains `gemini-3.6-flash` as its default.

Text replies use stateless REST `generateContent`, with the personality in `systemInstruction` and the saved transcript as user content. Gemini 3.8 TTS models use Interactions with `store=false` and separate style metadata. Available Gemini 3.1/2.5 TTS models use their older generateContent speech schema: delivery directions are included in the speech prompt, and PCM audio is wrapped in a WAV container in memory for playback. Existing voice presets work through either path, though delivery can vary by model. See the [text REST reference](https://ai.google.dev/api/generate-content) and [speech-generation guide](https://aistudio.google.com/docs/speech-generation).

### Refresh and choose models

Enter your key and click **Refresh Models** beside it. The app retrieves every page of Google's model catalog using header authentication, without generating content or interrupting listening. It separates compatible text and TTS models, includes preview/experimental entries, and excludes embedding, image-generation, transcription, and Live-only models. Exact identifiers are displayed. Unknown speech protocols or models without a compatible generation method remain visible but disabled, with an explanatory tooltip.

The two existing defaults work before a refresh. Refresh preserves your selections. If a selection disappears from the returned catalog, it stays selected with a **not in latest catalog** label; no replacement is chosen silently. A failed refresh keeps the prior lists. Editing the key cancels any outstanding refresh and marks the lists unverified until refreshed. A catalog listing does not guarantee that a generation request has permission or quota to succeed.

Changing the text model affects the next Generate request; an in-flight request keeps its snapshot. Changing the TTS model stops pending or active speech and affects subsequent playback. The **Last text model** and **Last audio model** labels identify the models that actually produced the current results, even if you subsequently change a selection. Selections and catalogs are never saved between sessions.

### Compare response speed

**Text generation** measures Generate click until the reply is displayed. **Voice preparation** measures speech preparation until validated audio is ready; it does not include the time spent speaking. The labels update while requests run and retain completed, failed, or cancelled durations. Replay using cached audio is labelled **cached audio (no API request)** instead of reporting a misleading network duration. Durations are displayed in the UI and are not logged.

Standard-mode speech is still prepared after the text reply completes, and Vosk still needs an utterance boundary to finalize speech. Model selection can help compare speed/quality tradeoffs, but service load, response length, and network latency also affect the wait. Standard mode does not stream responses or benchmark models automatically.

### Select a microphone or test computer audio

Under **Audio Input**, choose **Microphone** and pick the desired microphone by name. **Windows default microphone** follows the Windows default at the next capture start. **Refresh Audio Devices** discovers connected microphones and playback devices; it preserves the current selection, including a visibly unavailable device, without silently substituting another endpoint. Device lists load when the window opens. Stop local listening and Live before changing the source or device. Selections are not saved between launches.

To test existing YouTube content:

1. Choose **Computer audio (loopback)** and the **Playback device to listen to** through which your browser plays sound. This uses [NAudio's WASAPI loopback capture](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/WasapiLoopbackCapture.cs), requires no virtual audio driver, and captures all applications playing through that endpoint. It selects computer audio instead of the microphone; the sources are not mixed.
2. Play the video. **Start Listening** transcribes that audio locally into Saved Transcript; Standard Generate remains manual. Loopback may produce no input while the playback device is silent. If captions do not appear, check that the video is playing, the browser uses the selected output device, and audio is not muted or protected from capture.
3. For direct real-time testing, enable **Use Live API**, leave **Selected audio input** checked, and click **Start Live**. The prominent notice changes to **COMPUTER AUDIO STREAMING TO GOOGLE**. The same configurable Live deadline and shared local-caption capture apply. Start manually or explicitly enable automatic visits. Saved-transcript Live mode still uploads text only.
4. **Mute co-host audio to prevent feedback** is checked by default for computer audio. It suppresses Standard speech requests/playback and discards Live output audio while retaining response transcription. This avoids the co-host hearing itself. To hear spoken replies, route the browser to a different physical/virtual playback endpoint from the co-host's Windows default output, select that browser endpoint here, then uncheck mute. Headphones alone do not prevent digital loopback feedback. This app does not change Windows routing for you.

Live's default behavior deliberately stays quiet during ordinary narration. For a prerecorded-video experiment, edit **Live Behavior** before Start, for example: “Treat the video's speaker as the creator. Occasionally offer one brief friendly observation at a natural pause, even without direct address. Avoid talking over narration.” Proactive behavior remains model-guided; a quiet session is not necessarily an input failure. Computer audio can include notification sounds and other applications on the chosen device; manually started Live sessions and explicitly armed automatic visits upload it to Google.

### Optional Gemini Live experiment

The **Gemini Live Model** dropdown is directly below **Use Live API** and remains visible in Standard mode. It is independent of Text Response Model and TTS Model. It initially offers documented starting choices: `gemini-3.8-live` (default), `gemini-3.1-flash-live-preview`, and `gemini-2.5-flash-native-audio-preview-12-2025`. These built-in choices are unverified for your key until you click **Refresh Models**. Refresh now separates Live entries using `bidiGenerateContent` support; it preserves your selection if absent, marks it as not in the latest catalog, and retains the previous list on failure. A key change marks all catalogs unverified. Unknown or incompatible Live models remain disabled with an explanatory tooltip. Extended Thinking is disabled because this POC does not implement its asynchronous interaction-status lifecycle.

The dropdown is locked from Start through session cleanup. Each session snapshots the exact selected model and displays it in its status and response heading; changing the selection afterward does not relabel the previous session. No model is silently substituted. Gemini 3.8 Live keeps proactive audio enabled. Gemini 3.1 Flash Live does not support proactive audio and may respond more often to narration; Gemini 2.5 native-audio sessions explicitly enable it. The model-specific notice below the selector explains the difference. See Google's [Live model comparison](https://aistudio.google.com/docs/live-api/capabilities#model-comparison). Availability, quota, response speed, and voice behavior depend on the selected model and account.

Standard text generation plus TTS remains the startup default. Enable **Use Live API** for the separate `gemini-3.8-live` WebSocket experiment. It generates its own native speech; the text and TTS model selectors and Auto-speak are inactive in this mode. Select the voice, edit Voice Style, personality, and Live Behavior before Start. These settings and the API key are frozen until the session ends. The initial voice remains **Puck** with the approved **Sarcastic Droid** directions.

- **Selected audio input** is checked initially and defaults to the microphone. **Start Live** begins continuous streaming after connection/setup, using automatic voice activity detection for turn-taking and interruption. The bold **MICROPHONE STREAMING TO GOOGLE** or **COMPUTER AUDIO STREAMING TO GOOGLE** notice distinguishes this from local offline captions. No Vosk model is needed when local listening is off. If local listening was already on, captions continue through the same capture stream; Live Stop does not stop them. The local listening button is disabled during a Live audio-input session.
- Turn **Selected audio input** off to use **Saved transcript**. Start sends a snapshot after setup. When the spoken reply finishes, **Send Transcript** sends another snapshot as a manual turn. Live never subscribes to audio in this mode; independently enabled local captions remain local. Output transcription appears in the response area with separate turns and interrupted/incomplete markers; it never appends to the saved input transcript.
- **Stop Live**, switching input, returning to Standard, or closing stops the session. The countdown starts at Start, including connection/setup. At the configured deadline (30 seconds by default; 1–600 seconds allowed), uploading and playback stop and the socket is aborted, including mid-reply. There is no reconnect or history restoration; every Start creates a fresh session. Graceful shutdown is limited to one second before aborting. Buffer overflow stops with an error: microphone buffering is limited to two seconds and playback to thirty seconds.

The editable **Live Behavior** asks the co-host to stay quiet during narration, breaths, and short pauses; reply when directly addressed or after a clear stumble inviting a brief joke; prefer one sentence; and yield when the creator speaks again. Gemini 3.8 Live's proactive audio stays enabled. These directions guide behavior, rather than guaranteeing silence or identical TTS voice performance. Use headphones: speaker leakage can cause self-interruption or appear in local captions. Voice similarity, monologue restraint, and barge-in require listening tests.

Connection timing includes setup. Saved-transcript mode additionally shows text-send-to-first-audio time for the latest turn. Microphone mode shows elapsed time/countdown, without treating deliberate silence as response latency. Sub-second latency is not guaranteed.

The **configured Live duration bounds connection duration, not an exact token or spending budget**. Google's proactive audio listening can incur charges while the model remains quiet, and context/output also affect usage. Consult the [Live billing guidance](https://aistudio.google.com/docs/live-api/best-practices#pricing-billing), [WebSocket guide](https://aistudio.google.com/docs/live-api/get-started-websocket), and [interruption guidance](https://aistudio.google.com/docs/live-api/capabilities#interruptions). No tools, search, session resumption, video, or automatic reconnection are configured. Unsupported model access, invalid audio, disconnects, and server errors stop with sanitized messages; check the key, Live model access, quota, network, and default input/output devices, then explicitly Start again or Resume Automatic Visits.

### Timed Live co-host visits

For a recording session, enable **Use Live API**, keep **Selected audio input** checked, choose your microphone or computer-audio source, and click **Start Automatic Visits**. Defaults are **five minutes between visits**, **30 seconds per visit**, and **One sentence per reply**. Automation is off each launch and is not tied to OBS recording detection. It runs only while this desktop application is open.

- **Interval** is the wait after a visit finishes or is dismissed. The first visit waits the full interval too; **Visit Now** starts one sooner. Use seconds or minutes, from one second to 24 hours. No visits overlap and missed time is never queued. Interval edits affect the next wait; other edits while waiting affect the next visit.
- **Live Duration** is the entire conversation window, including connection/setup, listening, and all replies. Enter 60 seconds or one minute for a minute-long visit. The allowed range is 1–600 seconds and applies to manual Live too. The deadline is strict: audio uploads/playback stop even mid-sentence. There is no extra reply allowance beyond it.
- Within that window, the co-host can have multiple brief exchanges, hear questions, and yield when interrupted. Each visit has fresh context and hears only audio within that visit, not the preceding five minutes. Selective reactions and silence are model-guided; a visit may remain quiet.
- **Live reply length** offers One word, One sentence, or Custom maximum words (1–100, initially 20). These instructions take precedence over conflicting length directions in personality/Live Behavior. They guide the model and do not guarantee exact word counts. They apply to timed and manual Live only; Standard text/TTS and CLI jobs are unchanged.
- During automatic visits, the desktop character appears when reply PCM actually plays, remains through pauses in that reply, and hides after its completed turn drains from the output device. It can reappear for another reply in the same window. The in-app preview remains available. Muted loopback output stays hidden.
- The floating **×** above the desktop character stops and hides the current visit immediately. **Stop Live** during a visit has the same effect. The next full interval begins after cleanup. Use **Stop Automatic Visits** to disarm the schedule and restore the prior manual avatar visibility. Desktop position/size are retained; popups do not take focus when shown. You can still move a speaking character by dragging it.
- Spout2 follows the same automatic visibility and sends transparent frames between replies/visits. Its sender stays running if you enabled it; automation does not enable it for you. The × is desktop-only and is never rendered in Spout2. No OBS connection is inferred from sender status.
- Switching to Saved transcript, returning to Standard, or closing disarms automation. Network, input, playback, or buffer errors pause it and show **Resume Automatic Visits**; resuming waits the full interval again. No automatic reconnect or retry occurs inside a visit.

**Waiting** means there is no cloud connection or audio upload. Local captions may continue independently. **Connecting / Listening / Speaking / Stopping** and the countdown describe the current visit; the selected input, model, key, voice, behavior, brevity, and duration remain frozen during it. Starting the schedule explicitly authorizes repeated cloud sessions until stopped. Quiet listening can incur charges, and short intervals or longer visits increase usage. No schedule settings, credentials, conversations, or audio recordings are saved.

### Live verification

Timed-visit verification (2026-11-12): 211 ordinary tests passed, including fake-clock 30/60/600-second deadlines, repeat scheduling, dismissal, failure/pause/resume, shutdown, settings snapshots, and playback-driven visibility. Two opt-in Windows hardware checks passed for actual output-clock start/drain/reset and native Spout visible/transparent transitions. A native UI preview using simulated Live input/playback (no network or microphone capture) verified 30- and 60-second settings, focus-preserving appearance, disappearance between replies, and a real × click returning to the interval countdown. The preview helper is an ignored test artifact, not another shipped application. Real Gemini word-count adherence, perceived reply quality, and microphone barge-in were not re-auditioned for this change. OBS's receiver UI was not reconfigured; native receiver pixel checks verified transparency independently.

Ordinary tests use fake WebSockets, shared microphone devices, and silent playback. They do not transmit audio or make paid requests. For a separate paid two-turn transcript check, set `GEMINI_API_KEY` in your terminal, then run:

```powershell
$env:CONTENT_COHOST_RUN_LIVE_API_TEST = '1'
dotnet test ContentCoHostStudio.slnx -c Release --filter Category=LiveApi
Remove-Item Env:CONTENT_COHOST_RUN_LIVE_API_TEST
```

Optionally set `CONTENT_COHOST_LIVE_API_MODEL` to a supported identifier to run this opt-in check against a different Live model. It defaults to `gemini-3.8-live`; clear the variable after testing. Ordinary tests exercise all supported model setup paths with fake transports, including legacy proactive-audio settings, dropdown selection/freezing, and retained model labels.

Live-selector verification: the key's catalog returned the supported 3.8, 3.1, and 2.5 native-audio options. Separate real two-turn checks passed with `gemini-3.1-flash-live-preview` and `gemini-2.5-flash-native-audio-preview-12-2025`, using silent playback and no microphone input. The default 3.8 path was verified during the original Live implementation. Subjective voice quality and selective microphone behavior still require listening tests for each chosen model.

For the **native 30-second microphone/headphone trial**, use the app or opt in with `CONTENT_COHOST_RUN_NATIVE_LIVE_TEST=1` and `dotnet test ContentCoHostStudio.slnx -c Release --filter Category=NativeLive`. This test streams the default microphone to Google, plays any replies, keeps Vosk captions running alongside it, and checks that expiry preserves local listening. During the window: narrate normally for several seconds; address the co-host directly; wait for its short reply; ask another brief question; speak over the next reply to test barge-in; then observe expiry. Repeat using explicit Start as needed. These subjective behavior checks need a human listener; the automated check verifies lifecycle and devices. Clear the opt-in variables and `GEMINI_API_KEY` after testing.

Implementation verification: the real two-turn Live transcript check passed, returning streamed PCM and output transcription. The native microphone trial also passed, reaching automatic expiry after 30 seconds while local Vosk listening remained running. Both input variants were visually inspected in the desktop window. Monologue restraint, direct-address quality, perceived voice similarity, and audible barge-in have **not** been judged by a human listener during these automated checks; use the spoken trial above to assess them.

## 4. Use the app

1. Enter your Gemini key and edit the personality prompt, or keep the friendly sarcastic robot example.
2. Click **Start Listening**. Initial model loading can take several seconds; the window remains usable.
3. Speak English. Live captions change as Vosk refines the current utterance. A pause usually finalizes the utterance and appends it to **Saved Transcript**.
4. Edit the saved transcript if needed. **Clear Transcript** also resets unfinished recognition and rejects queued results from before the clear.
5. Click **Generate Response**. The app sends a snapshot of the saved transcript and personality, excluding live partial captions. Listening and editing can continue while Gemini works. An in-flight request is unaffected by subsequent edits or Clear.
6. The response appears immediately. **Auto-speak** is on by default and prepares a spoken version; switch it off for silent replies. Select response text and press Ctrl+C to copy it.
7. Click **Stop Listening** to release the microphone and finalize remaining buffered speech. Start again without restarting the app.

You can also type a transcript and use Gemini without a microphone or model. Text and speech requests each have a 60-second timeout, do not automatically retry, and show readable errors without printing keys or raw remote error bodies. Closing the window cancels outstanding requests and waits for microphone and playback cleanup.

### Expressive voice controls

Scroll below the Gemini response to **Co-host Voice**. The approved startup setup is **Puck**, **`gemini-3.8-flash-tts`**, **Auto-speak on**, and the **Sarcastic Droid** preset. Its voice style is:

> A crisp, articulate male baritone with a dry, slightly metallic resonance and a refined mid-Atlantic accent. The tone is deadpan, unhurried, and emotionally detached—sounding like an exhausted, hyper-intelligent droid who finds everything mildly beneath him.
> Clear enunciation, low vocal warmth, and a naturally flat, matter-of-fact cadence.

This is a built-in default, not saved session settings. You can still edit the style, choose Kore, Charon, or Fenrir, or select the earlier **Sarcastic Robot** and **Melancholy Documentary** presets. Choosing **Sarcastic Droid** restores the approved text. Personality controls what the co-host says; voice style controls how it says it. Metallic resonance is a performance direction for TTS, not an added audio effect.

- **Auto-speak:** speak each successful new reply. Switching it off cancels pending or active speech. Switching it on does not speak an old reply until you press Replay / Speak.
- **Replay / Speak:** speak the displayed reply with the current TTS model, voice, and style. The latest audio is cached in memory and reused only when the model, reply, voice, and style all match. Changing any of them prepares fresh audio and can incur additional usage charges.
- **Stop Voice:** cancel speech preparation or stop playback without affecting the reply text or microphone. Starting Generate also stops previous speech. Replies never overlap or queue for later playback.
- **Preparing Voice / Speaking / Stopped:** speech status is independent of the listening and text-generation status. A speech error keeps the text reply available and allows you to try again.

Microphone capture **continues during speech**. Use headphones to avoid transcribing the co-host's voice through your speakers. Output goes to the default Windows playback device; there is no output-device selector, echo cancellation, audio export, or disk cache.

Cloud speech uses your existing Gemini key and has **separate usage charges and quota** from text generation. It does not require a local TTS model or GPU. If speech is unavailable, check key/model access, network connectivity, and Google quota; for playback errors, check the default Windows output device. TTS latency and expressive delivery depend on the service. Audition and edit both presets to suit your taste; exact performance is not guaranteed.

## Build, test, and format

```powershell
dotnet restore ContentCoHostStudio.slnx --locked-mode
dotnet build ContentCoHostStudio.slnx -c Release --no-restore
dotnet test ContentCoHostStudio.slnx -c Release --no-build --no-restore
dotnet format ContentCoHostStudio.slnx --no-restore --verify-no-changes
```

Commit the NuGet lock files. When deliberately upgrading package references, restore without `--locked-mode`, review the updated locks, and rerun all checks. The test project uses xUnit v3 to match Avalonia 12's headless test support.

Tests cover transcript handling, resampling, text/speech payloads and errors, WAV/PCM validation, headless window controls, missing-model restart, and Windows native Vosk loading. Model tests cover pagination, classification, preview/disabled/missing entries, refresh failure, key changes, stale results, independent request snapshots, model-aware cache invalidation, and timings. Ordinary test runs use silent fake playback, do not use your microphone, and make no paid API calls. All three live tests are skipped unless explicitly enabled.

Implementation verification on Windows x64: Release build passed with zero warnings/errors and all 106 offline tests passed. A real v1beta catalog refresh and explicit UI-flow comparison succeeded with `gemini-3.1-flash-lite` / `gemini-3.5-flash-lite` for text and `gemini-3.8-flash-lite-tts` / `gemini-3.1-flash-tts-preview` for speech, validating modern WAV and older PCM formats. An earlier comparison attempt hit the 60-second text-generation timeout; the app defaults were retained, and no automatic retry or fallback was introduced. Earlier native UI testing verified speech playback while the microphone remained Listening, Stop Voice, and closing during playback/capture. Subjective voice quality remains a listening check for the user.

To repeat the live UI integration test, set `GEMINI_API_KEY` in your current process environment without saving it to a file, then opt in explicitly:

```powershell
$env:CONTENT_COHOST_RUN_LIVE_TEST = '1'
dotnet test ContentCoHostStudio.slnx -c Release --no-build --no-restore --filter 'Category=LiveGemini'
Remove-Item Env:CONTENT_COHOST_RUN_LIVE_TEST
Remove-Item Env:GEMINI_API_KEY
```

This text-only test makes one real, potentially billable request with Auto-speak disabled. It neither logs the live reply/key nor saves credentials. To test real speech generation for both presets without using the speakers, set `GEMINI_API_KEY` in process memory and run:

```powershell
$env:CONTENT_COHOST_RUN_LIVE_TTS_TEST = '1'
dotnet test ContentCoHostStudio.slnx -c Release --no-build --no-restore --filter 'Category=LiveSpeech'
Remove-Item Env:CONTENT_COHOST_RUN_LIVE_TTS_TEST
Remove-Item Env:GEMINI_API_KEY
```

The speech check makes two potentially billable requests, validates WAV audio in memory, and saves no files. The desktop app still requires manual key entry; Generate initiates text replies, and Auto-speak or Replay / Speak initiates speech requests.

To explicitly compare two catalog-listed text models and two speech models through the UI flow (silent playback, Flash-Lite preferred when available):

```powershell
$env:CONTENT_COHOST_RUN_LIVE_MODEL_TEST = '1'
dotnet test ContentCoHostStudio.slnx -c Release --no-build --no-restore --filter 'Category=LiveModels'
Remove-Item Env:CONTENT_COHOST_RUN_LIVE_MODEL_TEST
Remove-Item Env:GEMINI_API_KEY
```

Set `GEMINI_API_KEY` in the process environment first. This opt-in check loads the catalog and makes four potentially billable requests. It does not log the generated text, key, audio, or measured durations. A catalog-listed model can still fail due to access restrictions, quota, or temporary service load; the test does not silently substitute a different model after such a failure.

To choose the exact pair for each stage in this opt-in check, optionally set comma-separated `CONTENT_COHOST_LIVE_TEXT_MODELS` and `CONTENT_COHOST_LIVE_SPEECH_MODELS` before running it. For example, the verified pairs above can be supplied explicitly. Each pair must contain two supported identifiers returned by the catalog. Clear these optional variables after testing. They affect the test only, not the desktop app's defaults or selections.

On a restricted automation host, compiler servers, test-host communication, and formatter named pipes may be blocked. Run checks in a normal local terminal. To avoid a compiler server specifically:

```powershell
dotnet build ContentCoHostStudio.slnx --disable-build-servers -m:1 -p:UseSharedCompilation=false
```

An optional folder publish on Windows (no installer, trimming, or single-file packaging):

```powershell
dotnet publish src/ContentCoHostStudio -c Release -r win-x64 --self-contained false -o artifacts/windows-x64
```

Keep the entire output folder together. A framework-dependent publish needs the .NET 10 runtime on the destination computer. Point `CONTENT_COHOST_VOSK_MODEL` to your extracted model or place it under the executable's `models` folder.

## How the code is organized

### Optional Python connection diagnostic

The desktop app remains C#/.NET. To independently test Google's official Python SDK, use the standalone script below. Python 3.10 or newer is required; this implementation was checked with Python 3.13 and `google-genai` 3.2.0.

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -U google-genai
.\.venv\Scripts\python.exe scripts/test_gemini_connection.py
```

Paste the API key into the hidden prompt. Alternatively the script reads an existing `GEMINI_API_KEY` environment variable. It never saves the key. By default it tests the Interactions API with `store=False`, a 30-second timeout, and one request attempt. A successful request can incur normal Gemini usage charges.

To test the same REST API family as the desktop app, or the alias from Google's curl example:

```powershell
.\.venv\Scripts\python.exe scripts/test_gemini_connection.py --api generate-content
.\.venv\Scripts\python.exe scripts/test_gemini_connection.py --model gemini-flash-latest
```

The diagnostic prints a short generated reply on success, or a sanitized error and nonzero exit code on failure. `pip install` is a terminal command, not a line inside a Python program.

Connection-test results with SDK 3.2.0: **generateContent with `gemini-3.6-flash` succeeded and returned a real text reply**, confirming the supplied key works with that model. Earlier tests with `gemini-3.8-flash` timed out on Interactions and returned HTTP 503 on generateContent; `gemini-flash-latest` returned HTTP 404. The diagnostic prints UTF-8 so model replies also work in Windows terminals with a different default code page. No credentials are stored in this script or repository.

### Desktop source

- `MainWindow.axaml` and its code-behind: controls, snapshots, dispatcher updates, and shutdown.
- `TranscriptState.cs`: append finals, replace partials, and invalidate old results after Clear.
- `TranscriptionService.cs`: one sequential recognition worker owns the Vosk model and recognizer; capture timestamps reject speech queued before Clear.
- `MicrophoneCapture.cs`: one selected microphone or WASAPI loopback capture and converter shared by independent Vosk/Live subscriptions; bounded queues stop on overflow and release the device after the final subscriber leaves.
- `AudioInputDevices.cs` and `MainWindow.Audio.cs`: asynchronous Windows endpoint discovery, independent microphone/playback-device selections, active-capture locking, and optional loopback speech muting.
- `AudioConverter.cs`: decode float/PCM samples, mix to mono, and use NAudio's stateful filtered WDL resampler to produce 16 kHz PCM16, including a short filter tail on Stop.
- `GeminiService.cs`: asynchronous HttpClient calls, separate system/user JSON, authentication headers, response parsing, and safe errors.
- `GeminiSpeechService.cs`: expressive single-speaker speech requests, separate style metadata, WAV validation, and safe speech errors.
- `MainWindow.Speech.cs` and `SpeechPlayback.cs`: voice controls, single-entry audio cache, cancellable playback, and a silent-playback seam for tests.
- `GeminiModelCatalog.cs`, `MainWindow.Models.cs`, and `OperationTiming.cs`: paginated model discovery, independent selectors, refresh lifecycle, and monotonic UI timings.
- `GeminiLiveSession.cs`, `LiveTransport.cs`, `LiveAudioPlayback.cs`, and `MainWindow.Live.cs`: a fresh bounded WebSocket session per Start, fragmented message parsing, all-part audio/transcription handling, interruption, deadline, streamed PCM playback, and separate Live controls.

## Limitations and manual Windows acceptance

The UI and non-audio logic target portable `net10.0`. **Microphone capture is Windows-only in this POC**. macOS/Linux capture needs a new platform implementation and testing; the app reports this limitation rather than attempting Windows APIs. Vosk's native package also has platform-specific packaging constraints. Do not treat an Avalonia cross-platform UI as proof that microphone capture is supported everywhere.

Recognition accuracy varies with accent, noise, microphone, and the small English model. Punctuation is limited. Vosk finalizes on utterance boundaries rather than every spoken word; Stop commits its remaining recognition. Model loading cannot be interrupted inside the native load call, so stopping or closing during startup waits for it to finish. A busy CPU can overflow the bounded audio queue. Gemini response quality, latency, safety filtering, and model access are external service behavior. Personality prompts influence model output but cannot alter app permissions or cause it to run tools.

There is no voice cloning, metallic audio effect, local LLM/TTS backend, microphone/Live recording, account management, installer, or updater. Imported avatar artwork/metadata and explicitly requested CLI WebM artifacts may persist; other settings and session data remain in memory. Unsolicited replies are permitted only inside an explicitly started Live audio-input session; Standard generation and each CLI export require explicit requests.

The opt-in `NativeAudioInput` check (`CONTENT_COHOST_RUN_AUDIO_INPUT_TEST=1`) verifies an explicitly selected microphone and playback endpoint using a quiet, one-second in-memory test tone. It passed on Windows during implementation. It does not make network requests or save audio. Run with `dotnet test ContentCoHostStudio.slnx -c Release --filter Category=NativeAudioInput` and clear the opt-in variable afterward. Actual YouTube-to-Live response quality still needs your trial; no YouTube audio was uploaded during this change's verification.

The supplied English model ZIP has been extracted into `models/vosk-model-small-en-us-0.15` and is copied into app folders during builds. Native microphone startup and coexistence with both speech presets were verified. Real Gemini replies were verified through both the Python diagnostic and the POC's C# Generate flow using `gemini-3.6-flash`. Further checks for your own microphone, room, and voice preferences are:

- [ ] Launch the native window and resize it; all fields remain reachable by scrolling.
- [ ] Start, speak, observe changing partial captions, then pause and see one finalized segment.
- [ ] Edit a saved segment while listening and confirm later finals preserve the edit.
- [ ] Clear during an utterance and confirm old queued results do not restore the cleared text.
- [ ] Stop mid-sentence, inspect the final text, then restart several times.
- [ ] Unplug/disable the microphone and confirm a readable error and successful recovery after reconnecting.
- [ ] Generate with a real key and personality while continuing to speak; verify the reply appears and follows the requested style.
- [ ] Listen to both voice presets, edit the style, and try the other voices. Verify cached replay, changed-style synthesis, Stop Voice, and Auto-speak off.
- [ ] Refresh models, select text and TTS models independently, and compare their separate timings. Confirm last-used labels and cached replay remain accurate after selection changes.
- [ ] Try an invalid key, an offline network, and an account with exhausted quota if available; verify readable errors and recovery.
- [ ] Close while loading, listening, and generating; confirm the process exits and the microphone is released.

Project license: [MIT](LICENSE). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependency sources and their separate licenses.
