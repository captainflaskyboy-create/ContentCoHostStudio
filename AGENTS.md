# Content CoHost Studio

This is a small Windows-first proof of concept: microphone → local Vosk captions → editable transcript → manually requested Gemini reply.

- Use C# targeting .NET 10 LTS and Avalonia. Windows audio uses NAudio; Gemini uses HttpClient. Keep native Vosk calls on one background worker and UI changes on Avalonia's dispatcher.
- Prefer short, descriptive functions and ordinary control flow. Keep a few clearly named files, with brief comments for threading and unfamiliar APIs. No DI framework, plugin system, or speculative architecture.
- Keep API keys and session text in memory. Never log keys, headers, transcripts, raw server responses, or microphone data. Do not persist settings or recordings. The avatar library may persist explicitly imported artwork and pack metadata only.
- Partial captions replace each other. Only finalized results append. Clear invalidates queued results and resets recognition; repeated speech is legitimate.
- Generate is manual and snapshots the current personality and saved transcript. Keep system instructions separate from user content and retain Gemini safety defaults.
- Stop/restart and closing must release capture and native resources without blocking the UI. Bound the audio queue and report overflow.
- Expressive Gemini TTS is supported: keep voice style separate from personality, keep audio in memory, and allow microphone capture to continue during playback. Cancel superseded speech and release playback resources on close.
- Text and speech models are selected independently. Refresh the account catalog only on request, keep selections in memory, snapshot each request's model, and never silently substitute models. Use monotonic UI timings; do not log timing measurements.
- Live has its own independent model selector. Snapshot and freeze it during the session; display the session's actual model afterward. Use model-specific supported Live settings, explain proactive-audio differences, and disable unsupported Live protocols instead of guessing or falling back.
- Gemini Live is an explicit, optional experiment: unsolicited replies are allowed only within a manually started Live audio-input session (microphone or selected computer playback device) capped at 30 seconds. Standard generation stays manual. Never reconnect automatically or keep a background Live connection.
- Audio input is selected explicitly: microphone or Windows WASAPI loopback, never an automatic mix. Share one capture stream with local captions and Live, freeze device selection while capture is active, and never silently fall back from an unavailable explicit device. Keep loopback co-host playback muted by default to avoid feeding its output back into Live; keep all audio and device selections in memory.
- PNGtuber animation, a transparent desktop popout, and explicit Windows x64 Spout2 output to OBS are supported. Animate only from actual co-host playback; mute, Stop, and interruption must reset speech activity. Keep native graphics work on its dedicated worker and share one animation snapshot across outputs.
- The same executable supports explicit CLI narration/reply export jobs before Avalonia startup. CLI jobs may persist requested green-screen WebM artifacts and use cleaned-up temporary generated audio/video files. Keep credentials in GEMINI_API_KEY only and emit sanitized machine-readable results/progress; spoken text is returned only on explicit request. Headless jobs must not capture, play audio, create windows, or start Spout2.
- No accounts, installers, voice cloning, or local LLM/TTS backend in this POC. Live-session recording, automatic long-script chunking, and timeline editing are outside the CLI export feature.
- Run `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes`. Use the solution file explicitly if needed. Document unperformed microphone/network checks honestly.
- Do not download models or install system software automatically. Keep model assets, native overrides, and build output out of source control.

