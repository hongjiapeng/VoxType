using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using VoxType.R2T2;

namespace VoxType.Infrastructure.Transcription;

public sealed class R2T2LocalRuntime : IR2T2LocalRuntime, IAsyncDisposable
{
    private readonly string appDirectory;
    private readonly HttpClient downloads;
    private readonly HttpClient health = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private readonly HttpClient inference = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly R2T2AssetManifest manifest;
    private readonly int port;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private Session? session;
    private bool disposed;
    public event Action<string, bool>? StatusChanged;

    public R2T2LocalRuntime(string appDirectory, HttpClient downloads)
        : this(appDirectory, downloads, R2T2AssetManifest.Load(), 8273) { }

    internal R2T2LocalRuntime(string appDirectory, HttpClient downloads, R2T2AssetManifest manifest, int port)
    {
        this.appDirectory = appDirectory;
        this.downloads = downloads;
        this.manifest = manifest;
        this.port = port;
    }

    public async Task RunAsync(string backend, string directory, bool prepare, IProgress<string>? progress, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (backend is not ("cpu" or "vulkan")) throw new ArgumentException("R2T2 推理设备无效。");
            if (session is not null) throw new InvalidOperationException("请先停止正在运行的本机引擎，再准备资源或切换设备。");
            string root = R2T2LocalLauncher.ResolveDataDirectory(directory, appDirectory);
            string device = backend == "cpu" ? "Cpu" : "Vulkan";
            if (prepare)
            {
                foreach (var model in manifest.Models)
                    await R2T2Assets.DownloadAsync(downloads,
                        new($"https://huggingface.co/{manifest.ModelRepository}/resolve/{manifest.ModelRevision}/{model.Name}"),
                        Path.Combine(root, "models", model.Name), model, true, progress, token).ConfigureAwait(false);
                var runtime = manifest.Runtimes[device];
                string runtimeRoot = Path.Combine(root, "runtime", device);
                string archive = Path.Combine(runtimeRoot, runtime.Name);
                await R2T2Assets.DownloadAsync(downloads,
                    new($"https://github.com/ggml-org/llama.cpp/releases/download/{manifest.RuntimeVersion}/{runtime.Name}"),
                    archive, runtime, false, progress, token).ConfigureAwait(false);
                progress?.Report("正在校验并解压推理运行时…");
                await R2T2Assets.ExpandRuntimeAsync(archive, runtimeRoot, token).ConfigureAwait(false);
                return;
            }
            progress?.Report("正在校验本机资源…");
            foreach (var model in manifest.Models)
                if (!await R2T2Assets.VerifyAsync(Path.Combine(root, "models", model.Name), model, true, token).ConfigureAwait(false))
                    throw new IOException("模型未准备好或校验失败，请先点击“准备本机资源”。");
            string llamaPath = Path.Combine(root, "runtime", device, "llama-server.exe");
            if (!File.Exists(llamaPath)) throw new IOException("推理运行时未准备好，请先点击“准备本机资源”。");
            AssertPortAvailable(port);
            var arguments = new List<string> { "-m", Path.Combine(root, "models", manifest.Models[0].Name), "--mmproj", Path.Combine(root, "models", manifest.Models[1].Name),
                "--host", "127.0.0.1", "--port", port.ToString(), "--alias", "r2t2-q4", "-c", "8192", "-np", "1", "-t", Math.Min(8, Environment.ProcessorCount).ToString(),
                "--temp", "0", "--jinja", "--chat-template-file", Path.Combine(appDirectory, "services", "r2t2-chat-template.jinja"), "--log-disable" };
            arguments.AddRange(backend == "cpu" ? ["-ngl", "0", "--device", "none", "--no-mmproj-offload"] : ["-ngl", "99"]);
            var owned = new Session(backend, root, port, new LlamaTranscriber(inference, new R2T2Options { UpstreamUrl = $"http://127.0.0.1:{port}" }));
            try
            {
                owned.Llama = StartProcess(llamaPath, arguments, owned);
                progress?.Report("正在加载模型，首次启动可能需要几分钟…");
                var timer = Stopwatch.StartNew();
                bool ready = false;
                while (timer.Elapsed < TimeSpan.FromMinutes(4))
                {
                    token.ThrowIfCancellationRequested();
                    if (owned.Llama.HasExited) throw new IOException(ExitMessage(owned, backend));
                    try
                    {
                        using var response = await health.GetAsync($"http://127.0.0.1:{port}/health", token).ConfigureAwait(false);
                        ready = response.IsSuccessStatusCode;
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    if (ready) break;
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
                if (!ready) throw new IOException("模型加载超时，请检查可用内存，或切换 cpu 后重试。");
                token.ThrowIfCancellationRequested();
                owned.Ready = true;
                session = owned;
                StatusChanged?.Invoke("本机引擎已就绪，可以在外部输入框按住快捷键说话。", true);
                _ = MonitorAsync(owned, backend);
            }
            catch { await owned.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch (Win32Exception ex) { throw new IOException("本机引擎无法启动，请重新准备资源；使用 vulkan 时请检查显卡驱动或改用 cpu。", ex); }
        finally { gate.Release(); }
    }

    private async Task MonitorAsync(Session owned, string backend)
    {
        await owned.Llama!.WaitForExitAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(session, owned)) return;
            string message = ExitMessage(owned, backend);
            session = null;
            await owned.DisposeAsync().ConfigureAwait(false);
            StatusChanged?.Invoke(message, false);
        }
        finally { gate.Release(); }
    }

    private static string ExitMessage(Session owned, string backend)
    {
        string diagnostic = owned.StartupError;
        if (diagnostic.Contains("ErrorOutOfDeviceMemory", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
            return "本机推理内存分配失败。请释放内存；使用 vulkan 时可切换 cpu 后重试。";
        string code = $"推理进程退出码 {owned.Llama!.ExitCode}";
        return $"本机引擎已退出（{code}）。请检查内存和运行时" + (backend == "vulkan" ? "，或切换 cpu 后重试。" : "后重试。");
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var owned = session;
            session = null;
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
            StatusChanged?.Invoke("本机引擎已停止。", false);
        }
        finally { gate.Release(); }
    }

    public void ValidateConfiguration(string backend, string directory) => _ = GetSession(backend, directory);

    private Session GetSession(string backend, string directory)
    {
        var current = Volatile.Read(ref session);
        if (current is null || current.Token.IsCancellationRequested || current.Llama!.HasExited)
            throw new InvalidOperationException("请先在设置中准备资源并启动本机 R2T2 引擎。");
        if (current.Backend != backend || !current.Directory.Equals(R2T2LocalLauncher.ResolveDataDirectory(directory, appDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本机 R2T2 的设备或目录已变化，请停止引擎后重新启动。");
        return current;
    }

    public async Task<string> TranscribeAsync(string backend, string directory, byte[] wav, string language, Action<string>? partialText, CancellationToken token)
    {
        Session current;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { current = GetSession(backend, directory); }
        finally { gate.Release(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, current.Token);
        try
        {
            if (wav.Length > R2T2Options.MaximumAudioBytes) throw new R2T2Exception(413, "audio-size", "录音超过大小限制。");
            WavValidator.Validate(wav);
            if (partialText is null) return await current.Transcriber.TranscribeAsync(wav, language, "", linked.Token).ConfigureAwait(false);
            var preview = new StringBuilder();
            return await current.Transcriber.TranscribeStreamingAsync(wav, language, "", (delta, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                preview.Append(delta); partialText(preview.ToString()); return Task.CompletedTask;
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && current.Token.IsCancellationRequested)
        { throw new R2T2Exception(503, "unavailable", "本机 R2T2 引擎已停止，请重新启动后重试。"); }
    }

    public static void AssertPortAvailable(int port)
    {
        // Binding a temporary listener can reject TIME_WAIT connections after a clean stop.
        // Check active listeners instead; the actual server still owns the final bind/race.
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port &&
            (endpoint.Address.Equals(IPAddress.Loopback) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))))
            throw new IOException($"本机端口 {port} 已被占用。若旧版 R2T2 窗口仍开着，请先在旧窗口按 Ctrl+C 停止服务。");
    }

    private static Process StartProcess(string executable, IEnumerable<string> arguments, Session session)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var name in info.Environment.Keys.ToArray())
            if (name.StartsWith("LLAMA_ARG_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("R2T2__", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)) info.Environment.Remove(name);
        var process = Process.Start(info) ?? throw new IOException("本机引擎进程未能启动。");
        try { session.Job.Add(process); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); throw; }
        // Drain both pipes without retaining audio, transcripts or unbounded console output.
        _ = DrainAsync(process.StandardOutput.BaseStream);
        _ = ReadStartupErrorsAsync(process.StandardError, session);
        return process;
    }

    private static async Task DrainAsync(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task ReadStartupErrorsAsync(StreamReader reader, Session session)
    {
        try
        {
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                session.AppendStartupError(new string(buffer, 0, count));
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        health.Dispose(); inference.Dispose(); lifetime.Dispose();
    }

    private sealed class Session : IAsyncDisposable
    {
        public string Backend { get; }
        public string Directory { get; }
        public LlamaTranscriber Transcriber { get; }
        private readonly CancellationTokenSource cancellation = new();
        public CancellationToken Token { get; }
        private readonly int port;
        public Session(string backend, string directory, int port, LlamaTranscriber transcriber)
        { Backend = backend; Directory = directory; this.port = port; Transcriber = transcriber; Token = cancellation.Token; }
        public R2T2ProcessJob Job { get; } = new();
        public Process? Llama;
        public volatile bool Ready;
        private readonly object errorLock = new();
        private string startupError = "";
        public string StartupError { get { lock (errorLock) return startupError; } }
        public void AppendStartupError(string text)
        {
            // Only retain a bounded startup diagnostic in memory, before audio requests.
            if (Ready) return;
            lock (errorLock) { startupError += text; if (startupError.Length > 16384) startupError = startupError[^16384..]; }
        }
        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            Job.Dispose();
            foreach (var process in new[] { Llama })
                if (process is not null)
                {
                    try { await process.WaitForExitAsync().ConfigureAwait(false); }
                    finally { process.Dispose(); }
                }
            // Windows can retain a terminated process's listening entry briefly.
            // Do not advertise "stopped" until it has disappeared from the TCP table.
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(2))
            {
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                if (!listeners.Any(endpoint => endpoint.Port == port)) break;
                await Task.Delay(25).ConfigureAwait(false);
            }
            cancellation.Dispose();
        }
    }
}
