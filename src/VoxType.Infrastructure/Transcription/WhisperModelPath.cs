using System.Buffers.Binary;
using VoxType.Core.Transcription;

namespace VoxType.Infrastructure.Transcription;

public static class WhisperModelPath
{
    public static string Default
    {
        get
        {
            string directory;
            try { directory = global::Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path; }
            catch { directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
            return Path.Combine(directory, "VoxType", "models", "ggml-base.bin");
        }
    }
    public static string Resolve(string configured)
    {
        try { return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Default : configured.Trim()); }
        catch { throw new TranscriptionException("本地模型路径无效，请指定多语言 ggml-base.bin。", "model-path"); }
    }
    public static void Validate(string path)
    {
        try
        {
            if (!File.Exists(path)) throw new TranscriptionException("找不到本地模型，请在语音识别设置中指定多语言 ggml-base.bin；应用不会自动下载。", "model-missing");
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[48];
            if (file.Length < 48 || file.Read(header) != 48 || Read(header, 0) != 0x67676d6c)
                throw new TranscriptionException("模型文件无效或损坏，请使用 GGML 格式的多语言 base 模型。", "model-format");
            if (Read(header, 1) < 51865) throw new TranscriptionException("英文专用模型不支持此版本，请使用多语言 base 模型。", "model-language");
            if (Read(header, 2) != 1500 || Read(header, 3) != 512 || Read(header, 4) != 8 || Read(header, 5) != 6 ||
                Read(header, 6) != 448 || Read(header, 7) != 512 || Read(header, 8) != 8 || Read(header, 9) != 6 || Read(header, 10) != 80)
                throw new TranscriptionException("第一版仅支持多语言 base 模型，请检查模型文件。", "model-type");
        }
        catch (TranscriptionException) { throw; }
        catch { throw new TranscriptionException("无法读取模型文件，请检查路径和读取权限。", "model-read"); }
    }
    private static int Read(ReadOnlySpan<byte> header, int index) => BinaryPrimitives.ReadInt32LittleEndian(header.Slice(index * 4, 4));
}
