using System.Buffers.Binary;
using NAudio.Wave;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class AvatarPlaybackTests
{
    [Fact]
    public void MeterUsesOutputPositionInsteadOfQueuedOrReadAudio()
    {
        var bytes = new byte[24000 / 50 * 2 * 2];
        for (int i = bytes.Length / 2; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), 16000);
        using var stream = new MemoryStream(bytes);
        using var raw = new RawSourceWaveStream(stream, new WaveFormat(24000, 16, 1));
        var meter = new PlaybackMeter(raw);
        var output = new byte[bytes.Length];
        Assert.Equal(0, meter.LevelAt(0));
        Assert.Equal(bytes.Length, meter.Read(output, 0, output.Length));
        Assert.Equal(0, meter.LevelAt(0)); // Loud second window has been read but not played.
        Assert.True(meter.LevelAt(bytes.Length / 2) > .4f);
        Assert.Equal(0, meter.LevelAt(bytes.Length));
    }

    [Fact]
    public void StopReplacementAndLateActivityCannotReopenMouth()
    {
        var hub = new PlaybackActivityHub();
        long first = hub.Begin(); hub.Report(first, .4f);
        Assert.True(hub.Current.Level > 0);
        hub.Stop(first); hub.Report(first, .8f);
        Assert.False(hub.Current.Active); Assert.Equal(0, hub.Current.Level);
        long next = hub.Begin(); hub.Report(next, .2f); hub.Stop(first); hub.Report(first, 1);
        Assert.Equal(next, hub.Current.Id); Assert.Equal(.2f, hub.Current.Level);
        hub.Reset(); hub.Report(next, 1);
        Assert.False(hub.Current.Active); Assert.Equal(0, hub.Current.Level);
    }

    [Fact]
    public void AnimationUsesCoHostPlaybackAndResetsImmediatelyOnInterruptionAndMute()
    {
        var pack = new AvatarPackLibrary().Starter;
        var animator = new AvatarAnimator();
        var idle = animator.Next(pack, new(1, 1, false), "Normal", 50, 0, 0, 0);
        Assert.False(animator.Talking);
        var talking = animator.Next(pack, new(2, .5f, true), "Normal", 50, 0, 0, 33);
        Assert.True(animator.Talking); Assert.Contains("_talk", talking.Sprite);
        var stopped = animator.Next(pack, new(2, 0, false), "Normal", 50, 0, 0, 66);
        Assert.False(animator.Talking); Assert.Contains("_idle", stopped.Sprite);
        Assert.True(stopped.Sequence > idle.Sequence);
        var preparing = animator.Next(pack, new(3, 0, true), "Normal", 50, 0, 0, 99);
        Assert.False(animator.Talking);
        var pose = animator.Next(pack, new(3, .5f, true), "Sad", 50, 0, 0, 132);
        Assert.Contains("sad_talk", pose.Sprite);
    }

    [Fact]
    public void IdleBlinkStillRunsWithoutPlaybackAndMissingPoseFallsBackToNormal()
    {
        var pack = new AvatarPackLibrary().Starter;
        var animator = new AvatarAnimator();
        bool blink = false;
        for (int now = 0; now < 7000; now += 33)
            blink |= animator.Next(pack, new(0, 0, false), "Unknown", 50, 0, 0, now).Sprite.Contains("blink");
        Assert.True(blink);
    }

    [Fact]
    public void MutedLivePlaybackDoesNotPublishAnyActivity()
    {
        var hub = new PlaybackActivityHub();
        using var output = new LiveAudioPlayback(true, hub);
        output.Append(new byte[48000]);
        Assert.False(hub.Current.Active);
        Assert.Equal(0, output.BufferedBytes);
        output.Interrupt();
        Assert.False(hub.Current.Active);
    }
}
