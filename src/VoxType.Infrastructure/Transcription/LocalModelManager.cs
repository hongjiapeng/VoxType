using System.Security.Cryptography;
using VoxType.Core.Transcription;

namespace VoxType.Infrastructure.Transcription;

/// <summary>Installs only the catalogued model, without replacing any existing file.</summary>
public sealed class LocalModelManager : ILocalModelManager
{
    public static LocalModelDefinition BaseModel { get; } = new("whisper-base", "Whisper base · 多语言",
        "ggml-base.bin", 147951465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        "支持中文、英文等多种语言。CPU 运行，兼顾速度与识别质量，适合日常短句输入。",
        new("https://huggingface.co/ggerganov/whisper.cpp/tree/main"));
    public LocalModelDefinition Model { get; }
    public IReadOnlyList<ModelDownloadSource> Sources { get; } = Array.AsReadOnly<ModelDownloadSource>([
        new("Hugging Face（官方源）", new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin")),
        new("HF-Mirror（第三方镜像）", new("https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base.bin"))]);
    public string ModelPath { get; }
    private readonly HttpClient client;
    private readonly Func<string, Task> delete;
    private readonly SemaphoreSlim gate = new(1, 1);

    public LocalModelManager(HttpClient client, string modelPath, Func<string, Task> delete, LocalModelDefinition? model = null)
    {
        this.client = client; this.delete = delete;
        ModelPath = Path.GetFullPath(modelPath); Model = model ?? BaseModel;
    }

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true, MaxAutomaticRedirections = 5, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(20)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ModelInstallationState> InspectAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!File.Exists(ModelPath)) return ModelInstallationState.Missing;
            try
            {
                await VerifyAsync(ModelPath, token).ConfigureAwait(false);
                return ModelInstallationState.Installed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TranscriptionException)
            { return ModelInstallationState.Invalid; }
        }
        finally { gate.Release(); }
    }

    public Task DownloadAsync(ModelDownloadSource source, IProgress<ModelDownloadProgress>? progress, CancellationToken token)
    {
        if (!Sources.Contains(source)) throw new ArgumentException("请选择列表中的下载来源。", nameof(source));
        return InstallAsync(async cancellation =>
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var response = await client.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
            try
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != "https")
                    throw new TranscriptionException("下载地址必须使用 HTTPS。", "model-source");
                if (response.Content.Headers.ContentLength is long length && length != Model.SizeBytes)
                    throw new TranscriptionException("下载文件大小与模型记录不符，请更换来源重试。", "model-size");
                return new OwnedStream(await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false), response);
            }
            catch { response.Dispose(); throw; }
        }, progress, token);
    }

    public Task ImportAsync(string sourcePath, IProgress<ModelDownloadProgress>? progress, CancellationToken token) =>
        InstallAsync(_ => Task.FromResult<Stream>(new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan)), progress, token);

    private async Task InstallAsync(Func<CancellationToken, Task<Stream>> open, IProgress<ModelDownloadProgress>? progress, CancellationToken token)
    {
        // Reject duplicate mutations rather than queueing a second download behind the first.
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new TranscriptionException("模型管理正在处理，请稍后重试。", "model-busy");
        string temporary = ModelPath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            if (File.Exists(ModelPath)) throw new TranscriptionException("模型文件已经存在。请先刷新状态；需要重新安装时先删除此文件。", "model-exists");
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            progress?.Report(new(0, Model.SizeBytes));
            await using (var input = await open(token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[81920]; long received = 0;
                long lastReport = Environment.TickCount64;
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                    int count = await input.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > Model.SizeBytes) throw new TranscriptionException("模型文件超出预期大小，已停止安装。", "model-size");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    if (Environment.TickCount64 - lastReport >= 100 || received == Model.SizeBytes)
                    { progress?.Report(new(received, Model.SizeBytes)); lastReport = Environment.TickCount64; }
                }
                if (received != Model.SizeBytes) throw new TranscriptionException("模型文件下载不完整，请重试。", "model-size");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            progress?.Report(new(Model.SizeBytes, Model.SizeBytes, Verifying: true));
            await VerifyAsync(temporary, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, ModelPath, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }

    private async Task VerifyAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != Model.SizeBytes) throw new TranscriptionException("模型文件大小无效，请重新安装。", "model-size");
        var hash = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
        if (!Convert.ToHexString(hash).Equals(Model.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new TranscriptionException("模型校验失败，请更换下载来源或导入正确的多语言 base 模型。", "model-hash");
        WhisperModelPath.Validate(path);
    }

    public async Task DeleteAsync(CancellationToken token = default)
    {
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new TranscriptionException("模型管理正在处理，请稍后重试。", "model-busy");
        try { token.ThrowIfCancellationRequested(); await delete(ModelPath).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
    }
}
