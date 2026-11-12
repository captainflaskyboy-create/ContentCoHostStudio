# Open-source components

Gemini is the explicitly requested external proprietary API service. The application uses the following open-source libraries; restored packages include their own authoritative notices and license terms.

| Component | License / source |
| --- | --- |
| .NET | [MIT and component notices](https://github.com/dotnet/runtime) |
| Avalonia Desktop and Fluent theme | [MIT](https://github.com/AvaloniaUI/Avalonia) |
| NAudio.Core, NAudio.Wasapi, and NAudio.WinMM | [MIT](https://github.com/naudio/NAudio) |
| WDL resampler included in NAudio | [Cockos WDL permissive license, preserved in NAudio source](https://github.com/naudio/NAudio/blob/master/NAudio.Core/Dsp/WdlResampler.cs) |
| Vosk and its C# bindings | [Apache 2.0](https://github.com/alphacep/vosk-api) |
| vosk-model-small-en-us-0.15 (downloaded separately) | [Apache 2.0, official model list](https://alphacephei.com/vosk/models) |
| xUnit (tests only) | [Apache 2.0](https://github.com/xunit/xunit) |
| SkiaSharp 3.119.4 (avatar PNG validation and offscreen rendering) | [MIT and native Skia notices](https://github.com/mono/SkiaSharp); local license in `licenses/SkiaSharp-LICENSE.txt` |
| Spout2 SDK 2.007.017 (optional local Windows x64 sender) | [BSD-2-Clause](https://github.com/leadedge/Spout2/tree/2.007.017); local license in `licenses/Spout2-LICENSE.txt` |
| FFmpeg / ffprobe 9.0.2 (optional local CLI export tools) | Existing local Stream Avatar Studio build; FFmpeg LGPL notices, dependency licenses, and source/build provenance retained as `licenses/FFmpeg-*`. Native binaries remain ignored. |

The Captain Flasky starter artwork and adapted pack, animation, desktop hit-region, and sender code were reused from the user's existing Stream Avatar Studio project as requested. They are not obtained from an unrelated public avatar marketplace. Its narration editor, GIF events, browser output, accounts, settings store, and other product features are not imported.

Native and transitive components (including Skia/HarfBuzz, Kaldi/OpenFst, and the GCC runtime libraries shipped with Vosk) retain their respective upstream licenses and notices. GCC runtime libraries use GPL terms with the GCC Runtime Library Exception. If redistributing binaries, retain the applicable native and NuGet package notices and review the actual distribution's license files. The repository does not redistribute the separately downloaded Vosk model.
