using System.Runtime.Intrinsics.X86;
using System.Text;
using VoxType.Core.Transcription;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace VoxType.Infrastructure.Transcription;

public interface IWhisperModel : IDisposable
{
    Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken cancellationToken);
}
public interface IWhisperBackend { IWhisperModel Load(string path, string inferenceBackend); }

public sealed class WhisperBackend : IWhisperBackend
{
    public IWhisperModel Load(string path, string inferenceBackend)
    {
        WhisperModelPath.Validate(path);
        if (inferenceBackend is not ("cpu" or "vulkan"))
            throw new TranscriptionException("本地推理设备无效。", "inference-backend");
        if (!Avx.IsSupported || !Avx2.IsSupported || !Fma.IsSupported || !F16cSupported())
            throw new TranscriptionException("此 CPU 不满足本地识别运行时的 AVX/AVX2/FMA/F16C 要求，可改用在线服务。", "cpu");
        var runtime = inferenceBackend == "vulkan" ? RuntimeLibrary.Vulkan : RuntimeLibrary.Cpu;
        if (RuntimeOptions.LoadedLibrary is { } loaded && loaded != runtime)
            throw new TranscriptionException("Whisper 推理设备已在本次运行中加载。请重启 VoxType 后使用新设备。", "backend-restart");
        try
        {
            RuntimeOptions.RuntimeLibraryOrder = [runtime];
            var factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = inferenceBackend == "vulkan" });
            if (RuntimeOptions.LoadedLibrary != runtime)
            {
                factory.Dispose();
                throw new TranscriptionException("所选 Whisper 推理运行时不可用。请检查设备与驱动。", "inference-backend");
            }
            return new Model(factory);
        }
        catch (DllNotFoundException) { throw NativeMissing(inferenceBackend); }
        catch (BadImageFormatException) { throw NativeMissing(inferenceBackend); }
        catch (TranscriptionException) { throw; }
        catch { throw inferenceBackend == "vulkan"
            ? new TranscriptionException("无法使用 Vulkan 加载本地模型，请检查显卡驱动与显存；可在设置中选择 cpu 并重启 VoxType。", "gpu-load")
            : new TranscriptionException("无法加载本地模型，请检查文件是否完整及 x64 VC++ 2022 runtime。", "model-load"); }
    }
    private static bool F16cSupported() => X86Base.IsSupported && (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0;
    private static TranscriptionException NativeMissing(string inferenceBackend) => inferenceBackend == "vulkan"
        ? new("Vulkan 推理运行时未能加载，请检查显卡驱动及 x64 VC++ 2022 runtime；可选择 cpu 并重启 VoxType。", "gpu-runtime")
        : new("本地识别原生 DLL 未能加载，请修复应用并安装 x64 VC++ 2022 runtime。", "native-runtime");
    private sealed class Model(WhisperFactory factory) : IWhisperModel
    {
        public async Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken cancellationToken)
        {
            var builder = factory.CreateBuilder().WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8)).WithNoContext();
            if (language == "auto") builder.WithLanguageDetection(); else builder.WithLanguage(language);
            await using var processor = builder.Build();
            using var stream = new MemoryStream(wav, writable: false);
            var result = new StringBuilder();
            // Finish the native call before releasing its processor/context. ESC discards the result;
            // this MVP deliberately does not promise immediate native inference interruption.
            await foreach (var segment in processor.ProcessAsync(stream, CancellationToken.None).ConfigureAwait(false))
                if (!cancellationToken.IsCancellationRequested) result.Append(segment.Text);
            cancellationToken.ThrowIfCancellationRequested();
            return result.ToString().Trim();
        }
        public void Dispose() => factory.Dispose();
    }
}
