using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxType.Core.Audio;

namespace VoxType.Infrastructure.Audio;

public static class WavConverter
{
    public static float[] DecodeMono(byte[] nativeBytes, int count, WaveFormat format)
    {
        if (count < 0 || count > nativeBytes.Length || count % format.BlockAlign != 0)
            throw new ArgumentException("录音缓冲区不是完整音频帧。");
        int frames = count / format.BlockAlign;
        using var bytes = new MemoryStream(nativeBytes, 0, count, false);
        using var source = new RawSourceWaveStream(bytes, format);
        var samples = source.ToSampleProvider();
        float[] interleaved = new float[frames * format.Channels];
        int received = 0;
        while (received < interleaved.Length)
        {
            int read = samples.Read(interleaved.AsSpan(received));
            if (read == 0) break;
            received += read;
        }
        if (received != interleaved.Length) throw new ArgumentException("录音音频帧不完整。");
        float[] mono = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (int channel = 0; channel < format.Channels; channel++)
            {
                float value = interleaved[frame * format.Channels + channel];
                sum += float.IsFinite(value) ? value : 0;
            }
            mono[frame] = Math.Clamp((float)(sum / format.Channels), -1, 1);
        }
        return mono;
    }

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (float value in samples) sum += (double)value * value;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    public static AudioData Convert(byte[] nativeBytes, WaveFormat format)
    {
        var mono = DecodeMono(nativeBytes, nativeBytes.Length, format);
        int count = checked((int)((long)mono.Length * AudioData.SampleRate / format.SampleRate));
        float[] output;
        if (format.SampleRate == AudioData.SampleRate) output = mono;
        else
        {
            output = new float[count];
            var resampler = new WdlResamplingSampleProvider(new MonoSource(mono, format.SampleRate), AudioData.SampleRate);
            int position = 0;
            while (position < count)
            {
                int read = resampler.Read(output.AsSpan(position));
                if (read == 0) break;
                position += read;
            }
        }
        using var stream = new MemoryStream(44 + count * 2);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVE"u8);
            writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(AudioData.SampleRate); writer.Write(AudioData.SampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(count * 2);
            foreach (float sample in output)
                writer.Write((short)(sample <= -1 ? short.MinValue : sample >= 1 ? short.MaxValue : Math.Round(sample * short.MaxValue)));
        }
        return new(stream.ToArray(), TimeSpan.FromSeconds((double)count / AudioData.SampleRate), Rms(mono));
    }

    private sealed class MonoSource(float[] samples, int rate) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        public int Read(Span<float> buffer)
        {
            int count = Math.Min(buffer.Length, samples.Length - position);
            samples.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
    }
}
