using System.Buffers.Binary;

namespace VoxType.R2T2;

public static class WavValidator
{
    public static void Validate(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 44 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(4, 4)) != (uint)wav.Length - 8)
            throw Invalid();
        bool format = false;
        bool data = false;
        int offset = 12;
        long totalData = 0;
        while (offset < wav.Length)
        {
            if (wav.Length - offset < 8) throw Invalid();
            var id = wav.Slice(offset, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4));
            offset += 8;
            if (size > wav.Length - offset) throw Invalid();
            if (id.SequenceEqual("fmt "u8))
            {
                if (format || size < 16) throw Invalid();
                var fmt = wav.Slice(offset, (int)size);
                if (BinaryPrimitives.ReadUInt16LittleEndian(fmt) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]) != 16000 || BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..]) != 32000 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]) != 2 || BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]) != 16)
                    throw new R2T2Exception(400, "audio-format", "请提交 16kHz 单声道 16 位 PCM WAV。");
                format = true;
            }
            if (id.SequenceEqual("data"u8))
            {
                if (data || size % 2 != 0) throw Invalid();
                data = true;
                totalData += size;
            }
            long next = (long)offset + size + (size & 1);
            if (next > wav.Length) throw Invalid();
            offset = (int)next;
        }
        if (!format || !data || totalData < 8000) throw Invalid();
        if (totalData > 60 * 32000) throw new R2T2Exception(413, "audio-size", "录音不能超过 60 秒。");
    }
    private static R2T2Exception Invalid() => new(400, "audio", "WAV 无效、截断或短于 250 毫秒。");
}
