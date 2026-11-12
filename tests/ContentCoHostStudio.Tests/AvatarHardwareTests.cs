using System.Buffers.Binary;
using NAudio.Wave;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class AvatarHardwareTests
{
    [Fact]
    [Trait("Category", "NativeAvatar")]
    public async Task RealWavAndStreamedPlaybackPublishActivityAndResetOnStop()
    {
        if (Environment.GetEnvironmentVariable("CONTENT_COHOST_AVATAR_AUDIO_TEST") != "1")
            Assert.Skip("Set CONTENT_COHOST_AVATAR_AUDIO_TEST=1 for two short quiet playback tones and real output-clock activity checks.");
        var hub = new PlaybackActivityHub();
        byte[] pcm = new byte[24000 * 2];
        for (int i = 0; i < pcm.Length / 2; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i * 2 * Math.PI * 440 / 24000) * 1200));
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(24000, 16, 1))) writer.Write(pcm, 0, pcm.Length);
        bool standardActivity = false;
        hub.Changed += value => { if (value.Active && value.Level > .01f) standardActivity = true; };
        await new SpeechPlayback(hub).PlayAsync(stream.ToArray(), TestContext.Current.CancellationToken);
        Assert.True(standardActivity);
        Assert.False(hub.Current.Active);
        using var live = new LiveAudioPlayback(false, hub);
        var turns = new System.Collections.Concurrent.ConcurrentQueue<LivePlaybackTurn>();
        live.TurnChanged += turns.Enqueue;
        live.Append(pcm);
        live.CompleteTurn();
        await LiveTestWait.UntilAsync(() => hub.Current.Active && hub.Current.Level > .01f);
        await LiveTestWait.UntilAsync(() => turns.Any(t => t.Speaking));
        await LiveTestWait.UntilAsync(() => turns.Any(t => !t.Speaking));
        Assert.False(live.IsSpeaking);
        live.Append(pcm);
        await LiveTestWait.UntilAsync(() => live.IsSpeaking);
        live.Interrupt();
        Assert.False(hub.Current.Active);
        Assert.Equal(0, live.BufferedBytes);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(hub.Current.Active);
    }
}
