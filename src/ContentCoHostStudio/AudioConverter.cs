using System.Buffers.Binary;
using NAudio.Dsp;
using NAudio.Wave;

namespace ContentCoHostStudio;

// One instance per listening session. Keeping the filter state avoids seams between chunks.
public sealed class AudioConverter
{
    public const int RecognitionSampleRate = 16000;
    private readonly WaveFormat format;
    private readonly WdlResampler resampler = new();
    private bool flushed;

    public AudioConverter(WaveFormat format)
    {
        this.format = format;
        if (format.Channels < 1 || format.SampleRate < 1 ||
            !(format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ||
              format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 8 or 16 or 24 or 32))
            throw new NotSupportedException("This input device's audio format is not supported. Try another audio device.");

        resampler.SetMode(true, 2, true);
        resampler.SetFeedMode(true);
        resampler.SetRates(format.SampleRate, RecognitionSampleRate);
    }

    public byte[] Convert(byte[] input)
    {
        if (flushed)
            throw new InvalidOperationException("This audio converter has already stopped.");
        if (input.Length % format.BlockAlign != 0)
            throw new InvalidOperationException("The input device returned an incomplete audio frame.");

        int frames = input.Length / format.BlockAlign;
        var mono = new float[frames];
        int sampleBytes = format.BitsPerSample / 8;
        for (int frame = 0; frame < frames; frame++)
        {
            double total = 0;
            for (int channel = 0; channel < format.Channels; channel++)
            {
                int offset = frame * format.BlockAlign + channel * sampleBytes;
                total += ReadSample(input.AsSpan(offset, sampleBytes));
            }
            mono[frame] = (float)(total / format.Channels);
        }
        return Resample(mono);
    }

    public byte[] Flush()
    {
        if (flushed)
            return [];
        flushed = true;
        // Silence releases the sinc filter's short delayed tail; it is never written to disk.
        return Resample(new float[128]);
    }

    private float ReadSample(ReadOnlySpan<byte> bytes)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
            return float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
        }

        return format.BitsPerSample switch
        {
            8 => (bytes[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
            24 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
            _ => 0
        };
    }

    private byte[] Resample(float[] mono)
    {
        if (mono.Length == 0)
            return [];

        int prepared = resampler.ResamplePrepare(mono.Length, 1, out float[] input, out int offset);
        if (prepared != mono.Length)
            throw new InvalidOperationException("Audio conversion could not keep up with the microphone.");
        mono.CopyTo(input, offset);
        var output = new float[(int)Math.Ceiling((mono.Length + 256.0) * RecognitionSampleRate / format.SampleRate) + 256];
        int count = resampler.ResampleOut(output, 0, mono.Length, output.Length, 1);
        var pcm = new byte[count * 2];
        for (int index = 0; index < count; index++)
        {
            short sample = (short)Math.Clamp(Math.Round(output[index] * 32768), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2, 2), sample);
        }
        return pcm;
    }
}
