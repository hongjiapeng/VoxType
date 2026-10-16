using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;
using VoxType.Core.Audio;
using VoxType.Infrastructure.Audio;

namespace VoxType.Tests.Audio;

[TestClass]
public sealed class WavConverterTests
{
    [TestMethod]
    public void Float48kStereo_Produces16kMonoPcm16_AndPreservesTail()
    {
        float[] samples = new float[48000 * 2];
        for (int frame = 0; frame < 48000; frame++)
            samples[frame * 2] = samples[frame * 2 + 1] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * frame / 48000);
        var audio = WavConverter.Convert(Bytes(samples), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(audio.WavBytes, 0, 4));
        Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(audio.WavBytes, 8, 4));
        Assert.AreEqual("data", System.Text.Encoding.ASCII.GetString(audio.WavBytes, 36, 4));
        Assert.HasCount(32044, audio.WavBytes);
        Assert.AreEqual(32036, BinaryPrimitives.ReadInt32LittleEndian(audio.WavBytes.AsSpan(4)));
        Assert.AreEqual((short)1, BinaryPrimitives.ReadInt16LittleEndian(audio.WavBytes.AsSpan(20)));
        Assert.AreEqual((short)1, BinaryPrimitives.ReadInt16LittleEndian(audio.WavBytes.AsSpan(22)));
        Assert.AreEqual(16000, BinaryPrimitives.ReadInt32LittleEndian(audio.WavBytes.AsSpan(24)));
        Assert.AreEqual((short)16, BinaryPrimitives.ReadInt16LittleEndian(audio.WavBytes.AsSpan(34)));
        Assert.AreEqual(TimeSpan.FromSeconds(1), audio.Duration);
        Assert.AreEqual(0.353553f, audio.Rms, 0.0001f);
        Assert.IsTrue(audio.HasSignal);
        var pcm = Samples(audio);
        double tailRms = Math.Sqrt(pcm[^320..].Average(x => (double)x * x)) / 32768;
        Assert.AreEqual(0.3535, tailRms, 0.025, "The last captured audio must survive downsampling.");
    }

    [TestMethod]
    public void ExtensibleStereo_DownmixesBothChannels()
    {
        var samples = new float[4800 * 2];
        for (int i = 0; i < 4800; i++) { samples[2 * i] = 0.6f; samples[2 * i + 1] = 0.2f; }
        var audio = WavConverter.Convert(Bytes(samples), new WaveFormatExtensible(48000, 32, 2));
        Assert.AreEqual(0.4f, audio.Rms, 0.00001f);
        Assert.HasCount(1600, Samples(audio));
        Assert.AreEqual(0.4, Samples(audio)[800] / 32767d, 0.001);
    }

    [TestMethod]
    public void Pcm16Mono_PreservesSampleValuesAndDuration()
    {
        short[] samples = [short.MinValue, -10000, 0, 10000, short.MaxValue];
        var audio = WavConverter.Convert(MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), new WaveFormat(16000, 16, 1));
        short[] actual = Samples(audio);
        Assert.HasCount(samples.Length, actual);
        for (int i = 0; i < samples.Length; i++) Assert.AreEqual((double)samples[i], actual[i], 1);
        Assert.AreEqual(TimeSpan.FromSeconds(5d / 16000), audio.Duration);
    }

    [TestMethod]
    public void FloatOutOfRangeAndNonFinite_AreClampedOrSilenced()
    {
        float[] input = [-2, -1, 0, 0.5f, 1, 2, float.NaN, float.PositiveInfinity];
        var audio = WavConverter.Convert(Bytes(input), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        CollectionAssert.AreEqual(new short[] { -32768, -32768, 0, 16384, 32767, 32767, 0, 0 }, Samples(audio));
        Assert.IsTrue(float.IsFinite(audio.Rms));
    }

    [TestMethod]
    public void SilenceAndBriefTransient_UseWholeClipRms()
    {
        float[] samples = new float[80000];
        samples[100] = 0.1f;
        var audio = WavConverter.Convert(Bytes(samples), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        Assert.AreEqual(TimeSpan.FromSeconds(5), audio.Duration);
        Assert.AreEqual(0.000353553f, audio.Rms, 0.0000001f);
        Assert.IsFalse(audio.HasSignal, "A brief peak must not make a silent clip pass the global RMS threshold.");
    }

    [TestMethod]
    [DataRow(249, 0.01f, false)]
    [DataRow(250, 0.0031623f, true)]
    [DataRow(251, 0.0031622f, false)]
    [DataRow(251, 0.01f, true)]
    public void SignalFilter_RespectsDurationAndRmsBoundaries(int milliseconds, float rms, bool expected)
        => Assert.AreEqual(expected, new AudioData([], TimeSpan.FromMilliseconds(milliseconds), rms).HasSignal);

    [TestMethod]
    public void IncompleteFrame_IsRejected()
        => Assert.ThrowsExactly<ArgumentException>(() => WavConverter.Convert(new byte[5], new WaveFormat(16000, 16, 2)));

    [TestMethod]
    public void EmptyAudio_HasValidEmptyHeaderAndNoSignal()
    {
        var audio = WavConverter.Convert([], WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        Assert.HasCount(44, audio.WavBytes);
        Assert.AreEqual(0, BinaryPrimitives.ReadInt32LittleEndian(audio.WavBytes.AsSpan(40)));
        Assert.AreEqual(TimeSpan.Zero, audio.Duration);
        Assert.AreEqual(0f, audio.Rms);
        Assert.IsFalse(audio.HasSignal);
    }

    [TestMethod]
    public void Speech_QuietVoiceWithLongPauses_IsBoostedAndAccepted()
    {
        var input = Tone(16000 * 60, 16000 * 30, 6400, 0.004f);
        byte[] bytes = Bytes(input);
        byte[] original = (byte[])bytes.Clone();
        var audio = WavConverter.ConvertForSpeech(bytes, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));

        Assert.IsFalse(WavConverter.Convert(bytes, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1)).HasSignal,
            "This quiet phrase surrounded by pauses was rejected by the previous whole-clip check.");
        Assert.IsTrue(audio.HasSignal);
        Assert.IsLessThan(0.0031623f, audio.Rms, "The new check must work even when whole-clip RMS stays low.");
        Assert.AreEqual(TimeSpan.FromSeconds(60), audio.Duration);
        var pcm = Samples(audio);
        Assert.HasCount(input.Length, pcm);
        double phraseRms = PcmRms(pcm.AsSpan(16000 * 30 + 320, 6000));
        Assert.IsInRange(0.021, 0.023, phraseRms, "Quiet speech should receive the bounded 8x boost.");
        Assert.IsInRange(-1, 1, pcm[0]);
        CollectionAssert.AreEqual(original, bytes, "Preprocessing must not mutate the native recording.");
    }

    [TestMethod]
    [DataRow(0f)]
    [DataRow(0.2f)]
    public void Speech_SilenceOrDcOffset_IsNotBoostedOrAccepted(float offset)
    {
        float[] input = Enumerable.Repeat(offset, 16000).ToArray();
        var audio = WavConverter.ConvertForSpeech(Bytes(input), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        Assert.IsFalse(audio.HasSignal);
        Assert.AreEqual(0f, audio.Rms);
        Assert.AreEqual(TimeSpan.FromSeconds(1), audio.Duration);
        Assert.IsTrue(Samples(audio).All(value => value == 0));
    }

    [TestMethod]
    public void Speech_BriefLoudClick_DoesNotPassDespiteHighWholeClipRms()
    {
        var input = new float[16000];
        input[8000] = 0.9f;
        var audio = WavConverter.ConvertForSpeech(Bytes(input), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        Assert.IsGreaterThan(0.0031623f, audio.Rms);
        Assert.IsFalse(audio.HasSignal, "A click is not 200 ms of sustained sound.");
        Assert.IsLessThan(0.9, Samples(audio).Max(value => Math.Abs((double)value) / 32768));
    }

    [TestMethod]
    [DataRow(180, false)]
    [DataRow(200, true)]
    [DataRow(220, true)]
    public void Speech_SustainedActivity_Requires200Milliseconds(int activeMilliseconds, bool expected)
    {
        var input = Tone(16000, 3200, activeMilliseconds * 16, 0.01f);
        var audio = WavConverter.ConvertForSpeech(Bytes(input), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        Assert.AreEqual(expected, audio.HasSignal);
    }

    [TestMethod]
    [DataRow(249, false)]
    [DataRow(250, true)]
    public void Speech_MinimumRecordingDuration_IsStillEnforced(int milliseconds, bool expected)
    {
        var input = Tone(milliseconds * 16, 0, milliseconds * 16, 0.01f);
        var audio = WavConverter.ConvertForSpeech(Bytes(input), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        Assert.IsTrue(audio.SustainedSignal);
        Assert.AreEqual(expected, audio.HasSignal);
        Assert.AreEqual(TimeSpan.FromMilliseconds(milliseconds), audio.Duration);
    }

    [TestMethod]
    [DataRow(1000, 0.001f)]
    [DataRow(20, 0.004f)]
    public void Speech_WeakNoiseOrLowRumble_IsNotBoostedOrAccepted(int frequency, float amplitude)
    {
        var input = Tone(16000, 0, 16000, amplitude, frequency);
        var audio = WavConverter.ConvertForSpeech(Bytes(input), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        Assert.IsFalse(audio.HasSignal);
        Assert.IsLessThan(0.001f, audio.Rms);
    }

    [TestMethod]
    public void Speech_LoudVoiceAndPeak_KeepHeadroomWithoutClipping()
    {
        var input = Tone(16000, 0, 16000, 0.9f);
        input[8000] = 1;
        var audio = WavConverter.ConvertForSpeech(Bytes(input), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        var pcm = Samples(audio);
        Assert.IsTrue(audio.HasSignal);
        Assert.IsInRange(0.94, 0.951, pcm.Max(value => Math.Abs((double)value) / 32768));
        Assert.IsFalse(pcm.Any(value => value == short.MinValue || value == short.MaxValue));
        Assert.IsInRange(0.4, 0.64, PcmRms(pcm));
    }

    [TestMethod]
    public void Speech_NormalVoice_ReachesTargetLevelAndPreservesTail()
    {
        var input = Tone(16000, 0, 16000, 0.1f);
        var audio = WavConverter.ConvertForSpeech(Bytes(input), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        var pcm = Samples(audio);
        Assert.IsTrue(audio.HasSignal);
        Assert.AreEqual(0.1, PcmRms(pcm), 0.001);
        Assert.AreEqual(0.1, PcmRms(pcm.AsSpan(15680)), 0.002);
        Assert.AreEqual(TimeSpan.FromSeconds(1), audio.Duration);
    }

    [TestMethod]
    public void Speech_48kStereo_Produces16kMonoAndPreservesTail()
    {
        var mono = Tone(48000, 0, 48000, 0.004f, sampleRate: 48000);
        var stereo = mono.SelectMany(value => new[] { value, value }).ToArray();
        var audio = WavConverter.ConvertForSpeech(Bytes(stereo), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        Assert.IsTrue(audio.HasSignal);
        Assert.AreEqual(TimeSpan.FromSeconds(1), audio.Duration);
        Assert.AreEqual(16000, BinaryPrimitives.ReadInt32LittleEndian(audio.WavBytes.AsSpan(24)));
        Assert.AreEqual((short)1, BinaryPrimitives.ReadInt16LittleEndian(audio.WavBytes.AsSpan(22)));
        Assert.AreEqual((short)16, BinaryPrimitives.ReadInt16LittleEndian(audio.WavBytes.AsSpan(34)));
        var pcm = Samples(audio);
        Assert.HasCount(16000, pcm);
        Assert.IsInRange(0.021, 0.023, PcmRms(pcm.AsSpan(15680)));
    }

    [TestMethod]
    public void Speech_EmptyAudio_HasValidEmptyWavAndNoSignal()
    {
        var audio = WavConverter.ConvertForSpeech([], WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        Assert.HasCount(44, audio.WavBytes);
        Assert.AreEqual(0, BinaryPrimitives.ReadInt32LittleEndian(audio.WavBytes.AsSpan(40)));
        Assert.AreEqual(TimeSpan.Zero, audio.Duration);
        Assert.AreEqual(0f, audio.Rms);
        Assert.IsFalse(audio.HasSignal);
    }

    private static float[] Tone(int length, int start, int count, float amplitude, int frequency = 1000, int sampleRate = 16000)
    {
        var samples = new float[length];
        for (int i = start; i < start + count; i++)
            samples[i] = amplitude * MathF.Sin(2 * MathF.PI * frequency * (i - start) / sampleRate);
        return samples;
    }
    private static double PcmRms(ReadOnlySpan<short> samples)
    {
        double energy = 0;
        foreach (short sample in samples) energy += (double)sample * sample;
        return Math.Sqrt(energy / samples.Length) / 32768;
    }
    private static byte[] Bytes(float[] samples) => MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    private static short[] Samples(AudioData audio) => MemoryMarshal.Cast<byte, short>(audio.WavBytes.AsSpan(44)).ToArray();
}
