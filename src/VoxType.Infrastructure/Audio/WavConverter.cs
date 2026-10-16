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
        return Encode(Resample(mono, format.SampleRate), Rms(mono));
    }

    public static AudioData ConvertForSpeech(byte[] nativeBytes, WaveFormat format)
    {
        var mono = DecodeMono(nativeBytes, nativeBytes.Length, format);
        var output = Resample(mono, format.SampleRate);
        // Remove DC and low-frequency rumble before measuring or boosting sound.
        // Keep every sample: trimming/gating can cut quiet syllables and word endings.
        float previousInput = output.Length == 0 ? 0 : output[0], previousOutput = 0;
        float alpha = MathF.Exp(-2 * MathF.PI * 60 / AudioData.SampleRate);
        for (int i = 0; i < output.Length; i++)
        {
            float input = output[i];
            output[i] = alpha * (previousOutput + input - previousInput);
            previousInput = input;
            previousOutput = output[i];
        }

        const int windowSize = AudioData.SampleRate / 50; // 20 ms
        const int minimumActiveSamples = AudioData.SampleRate / 5; // 200 ms
        const float minimumWindowRms = 0.0016f; // about -56 dBFS
        int activeSamples = 0;
        double activeEnergy = 0;
        float peak = 0;
        for (int offset = 0; offset < output.Length; offset += windowSize)
        {
            var window = output.AsSpan(offset, Math.Min(windowSize, output.Length - offset));
            double energy = 0;
            foreach (float sample in window)
            {
                energy += (double)sample * sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }
            if (energy >= (double)minimumWindowRms * minimumWindowRms * window.Length)
            {
                activeSamples += window.Length;
                activeEnergy += energy;
            }
        }
        bool sustainedSignal = activeSamples >= minimumActiveSamples;
        if (sustainedSignal && peak > 0)
        {
            // A single gain for the recording avoids pumping during pauses.
            // Cap boost at 18 dB and leave peak headroom to prevent clipping.
            float activeRms = (float)Math.Sqrt(activeEnergy / activeSamples);
            float gain = Math.Min(Math.Clamp(0.1f / activeRms, 1, 8), 0.95f / peak);
            for (int i = 0; i < output.Length; i++) output[i] *= gain;
        }
        return Encode(output, Rms(output)) with { SustainedSignal = sustainedSignal };
    }

    private static float[] Resample(float[] mono, int sampleRate)
    {
        int count = checked((int)((long)mono.Length * AudioData.SampleRate / sampleRate));
        float[] output;
        if (sampleRate == AudioData.SampleRate) output = mono;
        else
        {
            output = new float[count];
            var resampler = new WdlResamplingSampleProvider(new MonoSource(mono, sampleRate), AudioData.SampleRate);
            int position = 0;
            while (position < count)
            {
                int read = resampler.Read(output.AsSpan(position));
                if (read == 0) break;
                position += read;
            }
        }
        return output;
    }

    private static AudioData Encode(float[] output, float rms)
    {
        int count = output.Length;
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
        return new(stream.ToArray(), TimeSpan.FromSeconds((double)count / AudioData.SampleRate), rms);
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
