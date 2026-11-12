using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class NativeAudioInputTests
{
    [Fact]
    public void MutedLivePlaybackNeverQueuesAudioOrOpensAnOutputDevice()
    {
        using var playback = new LiveAudioPlayback(muted: true);
        playback.Append(new byte[24000 * 2]);
        Assert.Equal(0, playback.BufferedBytes);
        playback.Interrupt();
        Assert.Throws<LiveSessionException>(() => playback.Append(new byte[1]));
    }

    [Fact]
    [Trait("Category", "NativeAudioInput")]
    public async Task OptInNativeExplicitMicrophoneAndLoopbackCaptureWithQuietTestTone()
    {
        if (Environment.GetEnvironmentVariable("CONTENT_COHOST_RUN_AUDIO_INPUT_TEST") != "1")
            Assert.Skip("Set CONTENT_COHOST_RUN_AUDIO_INPUT_TEST=1 for native microphone/loopback capture and a quiet test tone. No network is used.");
        var devices = await new WindowsAudioDeviceCatalog().ListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(devices, device => device.Kind == AudioInputKind.Microphone);
        var microphone = devices.First(device => device.Kind == AudioInputKind.Microphone);
        var settings = new AudioInputSettings { Current = new(AudioInputKind.Microphone, microphone.Id) };
        var source = new MicrophoneCaptureService(new WindowsMicrophoneCapture(settings));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await using (var capture = await source.SubscribeAsync(timeout.Token))
        {
            await using var reader = capture.Audio.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await reader.MoveNextAsync());
            Assert.NotEmpty(reader.Current);
        }
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        settings.Current = new(AudioInputKind.ComputerAudio, endpoint.ID);
        await using var loopback = await source.SubscribeAsync(timeout.Token);
        // Generate a short, quiet signal in memory; capture is never saved to disk.
        byte[] tone = new byte[48000 * 2 * 2];
        for (int frame = 0; frame < 48000; frame++)
        {
            short sample = (short)(Math.Sin(frame * 2 * Math.PI * 440 / 48000) * 160);
            for (int channel = 0; channel < 2; channel++)
            {
                int offset = (frame * 2 + channel) * 2;
                tone[offset] = (byte)sample;
                tone[offset + 1] = (byte)(sample >> 8);
            }
        }
        using var stream = new MemoryStream(tone);
        using var wave = new RawSourceWaveStream(stream, new WaveFormat(48000, 16, 2));
        using var output = new WasapiOut(endpoint, AudioClientShareMode.Shared, false, 100);
        output.Init(wave);
        output.Play();
        await foreach (byte[] pcm in loopback.Audio.ReadAllAsync(timeout.Token))
        {
            Assert.Equal(0, pcm.Length % 2);
            if (pcm.Any(value => value != 0)) return;
        }
        Assert.Fail("Loopback returned no PCM signal from the selected playback endpoint.");
    }
}
