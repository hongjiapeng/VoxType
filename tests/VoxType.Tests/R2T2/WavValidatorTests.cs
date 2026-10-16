using System.Buffers.Binary;
using VoxType.R2T2.Server;

namespace VoxType.Tests.R2T2;

[TestClass]
public sealed class WavValidatorTests
{
    [TestMethod]
    [DataRow(8000)]
    [DataRow(8002)]
    [DataRow(1919998)]
    [DataRow(1920000)]
    public void Duration_ExactAllowedBoundaries_Accepted(int size)
    {
        var wav = CreateWav(size);
        WavValidator.Validate(wav);
        Assert.HasCount(size + 44, wav);
    }

    [TestMethod]
    [DataRow(7998, 400, "audio")]
    [DataRow(1920002, 413, "audio-size")]
    [DataRow(1920004, 413, "audio-size")]
    public void Duration_OutsideAllowedBoundaries_Rejected(int size, int status, string code)
    {
        var error = Assert.ThrowsExactly<ServiceException>(() => WavValidator.Validate(CreateWav(size)));
        Assert.AreEqual(status, error.Status);
        Assert.AreEqual(code, error.Code);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(4, 1)]
    [DataRow(8, 0)]
    [DataRow(16, 255)]
    [DataRow(40, 255)]
    public void MalformedContainer_Rejected(int offset, int value)
    {
        var wav = CreateWav(); wav[offset] = (byte)value;
        var error = Assert.ThrowsExactly<ServiceException>(() => WavValidator.Validate(wav));
        Assert.AreEqual(400, error.Status); Assert.AreEqual("audio", error.Code);
    }

    [TestMethod]
    [DataRow(20, 3)]
    [DataRow(22, 2)]
    [DataRow(24, 1)]
    [DataRow(28, 1)]
    [DataRow(32, 4)]
    [DataRow(34, 8)]
    public void UnsupportedPcmFormat_Rejected(int offset, int value)
    {
        var wav = CreateWav(); wav[offset] = (byte)value;
        var error = Assert.ThrowsExactly<ServiceException>(() => WavValidator.Validate(wav));
        Assert.AreEqual(400, error.Status); Assert.AreEqual("audio-format", error.Code);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TruncatedOrTrailingPartialChunk_Rejected(bool trailing)
    {
        var original = CreateWav();
        var wav = trailing ? original.Concat(new byte[3]).ToArray() : original[..^2];
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)wav.Length - 8);
        Assert.AreEqual("audio", Assert.ThrowsExactly<ServiceException>(() => WavValidator.Validate(wav)).Code);
    }

    [TestMethod]
    [DataRow("fmt ")]
    [DataRow("data")]
    public void DuplicateRequiredChunk_Rejected(string id)
    {
        var wav = CreateWav();
        var duplicate = id == "fmt " ? wav[12..36] : wav[36..];
        var combined = wav.Concat(duplicate).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(combined.AsSpan(4), (uint)combined.Length - 8);
        Assert.AreEqual("audio", Assert.ThrowsExactly<ServiceException>(() => WavValidator.Validate(combined)).Code);
    }

    [TestMethod]
    [DataRow(12)]
    [DataRow(36)]
    public void MissingRequiredChunk_Rejected(int chunkOffset)
    {
        var wav = CreateWav(); "JUNK"u8.CopyTo(wav.AsSpan(chunkOffset));
        Assert.AreEqual("audio", Assert.ThrowsExactly<ServiceException>(() => WavValidator.Validate(wav)).Code);
    }

    [TestMethod]
    public void OddPcmDataSize_Rejected()
    {
        var wav = CreateWav(8001);
        Assert.AreEqual("audio", Assert.ThrowsExactly<ServiceException>(() => WavValidator.Validate(wav)).Code);
    }

    [TestMethod]
    public void OddSizedUnknownChunk_WithPadding_Accepted()
    {
        var original = CreateWav();
        byte[] chunk = [(byte)'J', (byte)'U', (byte)'N', (byte)'K', 1, 0, 0, 0, 42, 0];
        var wav = original[..12].Concat(chunk).Concat(original[12..]).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)wav.Length - 8);
        WavValidator.Validate(wav);
        Assert.HasCount(original.Length + 10, wav);
    }

    internal static byte[] CreateWav(int size = 8000)
    {
        var wav = new byte[size + 44];
        "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 16000); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)size);
        wav[44] = 11; wav[45] = 22;
        return wav;
    }
}
