using System.Buffers.Binary;
using NAudio.Wave;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class AudioTests
{
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(16000)]
    public void ResamplingIsIndependentOfCaptureChunkBoundaries(int sampleRate)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        var source = new byte[sampleRate * format.BlockAlign];
        for (int frame = 0; frame < sampleRate; frame++)
        {
            float value = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * frame / sampleRate));
            for (int channel = 0; channel < 2; channel++)
                BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(frame * 8 + channel * 4, 4), BitConverter.SingleToInt32Bits(value));
        }

        var whole = new AudioConverter(format);
        byte[] expected = [.. whole.Convert(source), .. whole.Flush()];
        var chunked = new AudioConverter(format);
        var actual = new List<byte>();
        for (int offset = 0; offset < source.Length; offset += 137 * format.BlockAlign)
        {
            int count = Math.Min(137 * format.BlockAlign, source.Length - offset);
            actual.AddRange(chunked.Convert(source.AsSpan(offset, count).ToArray()));
        }
        actual.AddRange(chunked.Flush());
        Assert.Equal(expected.Length, actual.Count);
        byte[] actualBytes = actual.ToArray();
        for (int offset = 0; offset < expected.Length; offset += 2)
        {
            int difference = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(expected.AsSpan(offset, 2)) -
                BinaryPrimitives.ReadInt16LittleEndian(actualBytes.AsSpan(offset, 2)));
            Assert.InRange(difference, 0, 1);
        }
        // One second of input, plus a small filter tail; never reinterpret 48 kHz as 16 kHz.
        Assert.InRange(actual.Count / 2, 15900, 16200);
        Assert.Empty(chunked.Flush());
    }

    [Fact]
    public void OppositeStereoChannelsMixToSilence()
    {
        var converter = new AudioConverter(new WaveFormat(48000, 16, 2));
        var input = new byte[4800 * 4];
        for (int offset = 0; offset < input.Length; offset += 4)
        {
            BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(offset, 2), 12000);
            BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(offset + 2, 2), -12000);
        }
        byte[] output = [.. converter.Convert(input), .. converter.Flush()];
        Assert.NotEmpty(output);
        Assert.All(output, value => Assert.Equal(0, value));
    }

    [Fact]
    public void UnsupportedFormatsAndIncompleteFramesFailClearly()
    {
        Assert.Throws<NotSupportedException>(() => new AudioConverter(WaveFormat.CreateALawFormat(8000, 1)));
        var converter = new AudioConverter(new WaveFormat(48000, 16, 2));
        Assert.Throws<InvalidOperationException>(() => converter.Convert(new byte[3]));
    }
}
