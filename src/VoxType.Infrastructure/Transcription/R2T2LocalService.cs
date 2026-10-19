using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace VoxType.Infrastructure.Transcription;

public sealed class R2T2LocalService : IAsyncDisposable
{
    private readonly string appDirectory;
    private readonly HttpClient downloads;
    private readonly HttpClient health = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private readonly R2T2AssetManifest manifest = R2T2AssetManifest.Load();
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private Session? session;
    private bool disposed;
    public event Action<string, bool>? StatusChanged;

    public R2T2LocalService(string appDirectory, HttpClient downloads)
    {
        this.appDirectory = appDirectory;
        this.downloads = downloads;
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
            if (session is not null) throw new InvalidOperationException("请先停止正在运行的本机服务，再准备资源或切换设备。");
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
            string adapter = Path.Combine(appDirectory, "R2T2.Server.exe");
            if (!File.Exists(adapter)) throw new IOException("安装缺少 R2T2 服务，请重新安装完整的 VoxType 安装包。");
            progress?.Report("正在校验本机资源…");
            foreach (var model in manifest.Models)
                if (!await R2T2Assets.VerifyAsync(Path.Combine(root, "models", model.Name), model, true, token).ConfigureAwait(false))
                    throw new IOException("模型未准备好或校验失败，请先点击“准备本机资源”。");
            string llamaPath = Path.Combine(root, "runtime", device, "llama-server.exe");
            if (!File.Exists(llamaPath)) throw new IOException("推理运行时未准备好，请先点击“准备本机资源”。");
            AssertPortAvailable(8272); AssertPortAvailable(8273);
            var arguments = new List<string> { "-m", Path.Combine(root, "models", manifest.Models[0].Name), "--mmproj", Path.Combine(root, "models", manifest.Models[1].Name),
                "--host", "127.0.0.1", "--port", "8273", "--alias", "r2t2-q4", "-c", "8192", "-np", "1", "-t", Math.Min(8, Environment.ProcessorCount).ToString(),
                "--temp", "0", "--jinja", "--chat-template-file", Path.Combine(appDirectory, "services", "r2t2-chat-template.jinja"), "--log-disable" };
            arguments.AddRange(backend == "cpu" ? ["-ngl", "0", "--device", "none", "--no-mmproj-offload"] : ["-ngl", "99"]);
            var owned = new Session();
            try
            {
                owned.Llama = StartProcess(llamaPath, arguments, owned);
                owned.Adapter = StartProcess(adapter, [], owned, new()
                {
                    ["R2T2__ListenUrl"] = "http://127.0.0.1:8272", ["R2T2__UpstreamUrl"] = "http://127.0.0.1:8273",
                    ["R2T2__ApiKey"] = "", ["R2T2__UpstreamApiKey"] = "", ["R2T2__Model"] = "r2t2-q4", ["R2T2__TimeoutSeconds"] = "115",
                    ["Logging__LogLevel__Default"] = "Warning", ["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning"
                });
                progress?.Report("正在加载模型，首次启动可能需要几分钟…");
                var timer = Stopwatch.StartNew();
                bool ready = false;
                while (timer.Elapsed < TimeSpan.FromMinutes(4))
                {
                    token.ThrowIfCancellationRequested();
                    if (owned.Llama.HasExited || owned.Adapter.HasExited) throw new IOException(ExitMessage(owned, backend));
                    try
                    {
                        using var response = await health.GetAsync("http://127.0.0.1:8272/health", token).ConfigureAwait(false);
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
                StatusChanged?.Invoke("本机服务已就绪，可以在外部输入框按住快捷键说话。", true);
                _ = MonitorAsync(owned, backend);
            }
            catch { await owned.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch (Win32Exception ex) { throw new IOException("本机服务无法启动，请重新准备资源；使用 vulkan 时请检查显卡驱动或改用 cpu。", ex); }
        finally { gate.Release(); }
    }

    private async Task MonitorAsync(Session owned, string backend)
    {
        await Task.WhenAny(owned.Llama!.WaitForExitAsync(), owned.Adapter!.WaitForExitAsync()).ConfigureAwait(false);
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
        string code = owned.Llama?.HasExited == true ? $"推理进程退出码 {owned.Llama.ExitCode}" : $"适配服务退出码 {owned.Adapter?.ExitCode}";
        return $"本机服务已退出（{code}）。请检查内存和运行时" + (backend == "vulkan" ? "，或切换 cpu 后重试。" : "后重试。");
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var owned = session;
            session = null;
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
            StatusChanged?.Invoke("本机服务已停止。", false);
        }
        finally { gate.Release(); }
    }

    public static void AssertPortAvailable(int port)
    {
        // Binding a temporary listener can reject TIME_WAIT connections after a clean stop.
        // Check active listeners instead; the actual server still owns the final bind/race.
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port &&
            (endpoint.Address.Equals(IPAddress.Loopback) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))))
            throw new IOException($"本机端口 {port} 已被占用。若旧版 R2T2 窗口仍开着，请先在旧窗口按 Ctrl+C 停止服务。");
    }

    private static Process StartProcess(string executable, IEnumerable<string> arguments, Session session, Dictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var name in info.Environment.Keys.ToArray())
            if (name.StartsWith("LLAMA_ARG_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("R2T2__", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)) info.Environment.Remove(name);
        if (environment is not null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        var process = Process.Start(info) ?? throw new IOException("本机服务进程未能启动。");
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
        health.Dispose(); lifetime.Dispose();
    }

    private sealed class Session : IAsyncDisposable
    {
        public R2T2ProcessJob Job { get; } = new();
        public Process? Llama, Adapter;
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
            Job.Dispose();
            foreach (var process in new[] { Adapter, Llama })
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
                if (!listeners.Any(endpoint => endpoint.Port is 8272 or 8273)) break;
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
    }
}
