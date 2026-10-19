using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;
using VoxType.Infrastructure.Transcription;

namespace VoxType.Tests.R2T2;

[TestClass]
[TestCategory("Integration")]
public sealed class R2T2LocalRuntimeTests
{
    [TestMethod]
    public async Task NotStarted_RejectsProviderBeforeOpeningMicrophone()
    {
        using var client = new HttpClient();
        await using var runtime = new R2T2LocalRuntime(AppContext.BaseDirectory, client);
        var error = Assert.ThrowsExactly<TranscriptionException>(() => new R2T2LocalProvider(runtime, new()));
        Assert.AreEqual("configuration", error.Code);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectInference_UsesSharedProtocolAndIgnoresRemoteSettings(bool streaming)
    {
        await using var fixture = await Fixture.StartAsync();
        var provider = fixture.Provider(streaming);
        var partials = new List<string>();
        var result = await provider.TranscribeAsync(Audio(), new("zh", PartialText: partials.Add), default);
        Assert.AreEqual("r2t2-local", provider.Id);
        Assert.AreEqual("r2t2-local", result.ProviderId);
        Assert.AreEqual("本机结果。", result.Text);
        if (streaming) Assert.AreEqual("本机结果。", partials[^1]);
        else Assert.IsEmpty(partials);
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => provider.TranscribeAsync(new([1, 2], TimeSpan.FromSeconds(1), .1f), new("zh"), default));
        Assert.AreEqual("audio", error.Code);
    }

    [TestMethod]
    public async Task Stop_CancelsPendingInferenceAndAllowsImmediateRestart()
    {
        await using var fixture = await Fixture.StartAsync();
        var pending = fixture.Provider(false).TranscribeAsync(Audio(), new("de"), default);
        await fixture.WaitForInferenceAsync();
        await fixture.Runtime.StopAsync();
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("unavailable", error.Code);
        R2T2LocalRuntime.AssertPortAvailable(fixture.Port);
        await fixture.StartRuntimeAsync();
        Assert.AreEqual("本机结果。", (await fixture.Provider(false).TranscribeAsync(Audio(), new("zh"), default)).Text);
    }

    [TestMethod]
    public async Task CallerCancellation_ReleasesInferenceGateWithoutStoppingEngine()
    {
        await using var fixture = await Fixture.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Provider(false).TranscribeAsync(Audio(), new("de"), cancellation.Token);
        await fixture.WaitForInferenceAsync();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("本机结果。", (await fixture.Provider(false).TranscribeAsync(Audio(), new("zh"), default)).Text);
    }

    [TestMethod]
    public async Task ChangedDeviceOrDirectory_RequiresRestart()
    {
        await using var fixture = await Fixture.StartAsync();
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Runtime.ValidateConfiguration("vulkan", fixture.Root));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Runtime.ValidateConfiguration("cpu", Path.Combine(fixture.Root, "other")));
    }

    private static AudioData Audio() => new(WavValidatorTests.CreateWav(), TimeSpan.FromMilliseconds(250), .1f);

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "voxtype-local-" + Guid.NewGuid().ToString("N"));
        public int Port { get; }
        public R2T2LocalRuntime Runtime { get; }
        private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };

        public Fixture()
        {
            using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            Directory.CreateDirectory(Path.Combine(Root, "models"));
            string runtime = Path.Combine(Root, "runtime", "Cpu");
            Directory.CreateDirectory(runtime);
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "r2t2-testhost"))) File.Copy(file, Path.Combine(runtime, Path.GetFileName(file)));
            var models = new[] { "model.gguf", "projector.gguf" }.Select(name =>
            {
                byte[] bytes = "GGUFtest-fixture"u8.ToArray();
                File.WriteAllBytes(Path.Combine(Root, "models", name), bytes);
                return new R2T2Asset(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            }).ToArray();
            Runtime = new(Root, client, new("unused", "unused", models, "unused", []), Port);
        }

        public static async Task<Fixture> StartAsync()
        {
            var fixture = new Fixture();
            try { await fixture.StartRuntimeAsync(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public Task StartRuntimeAsync() => Runtime.RunAsync("cpu", Root, false, null, default).WaitAsync(TimeSpan.FromSeconds(15));
        public R2T2LocalProvider Provider(bool streaming) => new(Runtime, new AppSettings
        {
            ProviderId = "r2t2-local", R2T2InferenceBackend = "cpu", R2T2DataDirectory = Root, R2T2StreamOutput = streaming,
            BaseUrl = "https://remote.invalid/v1", Model = "remote-model", ApiKey = "remote-key"
        });
        public async Task WaitForInferenceAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{Port}/entered", timeout.Token);
                if (response.IsSuccessStatusCode) return;
                await Task.Delay(25, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            client.Dispose();
            Directory.Delete(Root, true);
        }
    }
}
