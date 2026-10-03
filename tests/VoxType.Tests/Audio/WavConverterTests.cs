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
    private static byte[] Bytes(float[] samples) => MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    private static short[] Samples(AudioData audio) => MemoryMarshal.Cast<byte, short>(audio.WavBytes.AsSpan(44)).ToArray();
}
