using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using VoxType.Core.Transcription;
using VoxType.Infrastructure.Transcription;

namespace VoxType.Tests.Transcription;

[TestClass]
public sealed class LocalModelManagerTests
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "VoxType-download-tests-" + Guid.NewGuid().ToString("N"));
    private static byte[] ModelBytes()
    {
        int[] values = [0x67676d6c, 51865, 1500, 512, 8, 6, 448, 512, 8, 6, 80, 1];
        byte[] bytes = new byte[96];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }
    private LocalModelManager Create(HttpClient client, byte[]? bytes = null, Func<string, Task>? delete = null)
    {
        bytes ??= ModelBytes();
        var model = LocalModelManager.BaseModel with { SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        return new(client, Path.Combine(directory, "models", "ggml-base.bin"), delete ?? (path => { File.Delete(path); return Task.CompletedTask; }), model);
    }
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

    [TestMethod]
    public async Task Download_VerifiesPublishesAndReportsProgress_FromSelectedSourceWithoutCredentials()
    {
        var bytes = ModelBytes();
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("hf-mirror.com", request.RequestUri!.Host);
            Assert.IsNull(request.Headers.Authorization); Assert.IsNull(request.Content);
            Assert.AreEqual(HttpMethod.Get, request.Method);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var service = Create(client); var reports = new List<ModelDownloadProgress>();
        await service.DownloadAsync(service.Sources[1], new ProgressSink(value =>
        {
            reports.Add(value); Assert.IsFalse(File.Exists(service.ModelPath), "Final path must remain absent until verification finishes.");
        }), default);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(service.ModelPath));
        Assert.AreEqual(ModelInstallationState.Installed, await service.InspectAsync());
        Assert.AreEqual(0L, reports[0].BytesReceived); Assert.AreEqual(100d, reports[^1].Percent); Assert.IsTrue(reports[^1].Verifying);
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(service.ModelPath)!, "*.download"));
    }

    [TestMethod]
    [DataRow("short", "model-size")]
    [DataRow("large", "model-size")]
    [DataRow("hash", "model-hash")]
    [DataRow("header", "model-format")]
    public async Task InvalidDownloads_NeverInstall_AndRemoveTemporaryFiles(string kind, string code)
    {
        var expected = ModelBytes(); var actual = expected.ToArray();
        if (kind == "short") actual = actual[..^1];
        if (kind == "large") actual = [.. actual, 0];
        if (kind == "hash") actual[^1] = 1;
        if (kind == "header") { actual[0] = 0; expected = actual; }
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(actual)) }));
        var service = Create(client, expected);
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => service.DownloadAsync(service.Sources[0], null, default));
        Assert.AreEqual(code, error.Code); Assert.IsFalse(File.Exists(service.ModelPath));
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(service.ModelPath)!, "*.download"));
    }

    [TestMethod]
    public async Task ExistingFile_IsNeverOverwritten_AndInvalidStateCanBeDeleted()
    {
        int requests = 0; string? deletedPath = null;
        using var client = new HttpClient(new Handler(_ => { requests++; return new(HttpStatusCode.OK); }));
        var service = Create(client, delete: path => { deletedPath = path; File.Delete(path); return Task.CompletedTask; });
        Directory.CreateDirectory(Path.GetDirectoryName(service.ModelPath)!); await File.WriteAllBytesAsync(service.ModelPath, [1, 2, 3]);
        Assert.AreEqual(ModelInstallationState.Invalid, await service.InspectAsync());
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => service.DownloadAsync(service.Sources[0], null, default));
        Assert.AreEqual("model-exists", error.Code); Assert.AreEqual(0, requests);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(service.ModelPath));
        await service.DeleteAsync(); Assert.AreEqual(service.ModelPath, deletedPath);
        Assert.AreEqual(ModelInstallationState.Missing, await service.InspectAsync());
    }

    [TestMethod]
    public async Task Cancellation_CleansPartialFile_AllowsRetry_AndRejectsConcurrentInstall()
    {
        var bytes = ModelBytes(); var stream = new BlockedStream(bytes); int requests = 0;
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        { Content = ++requests == 1 ? new StreamContent(stream) : new ByteArrayContent(bytes) }));
        var service = Create(client); using var cancellation = new CancellationTokenSource();
        var task = service.DownloadAsync(service.Sources[0], null, cancellation.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => service.DownloadAsync(service.Sources[0], null, default));
        Assert.AreEqual("model-busy", error.Code);
        Assert.IsFalse(File.Exists(service.ModelPath));
        Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(service.ModelPath)!, "*.download"));
        cancellation.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.IsTrue(stream.Disposed); Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(service.ModelPath)!, "*.download"));
        await service.DownloadAsync(service.Sources[0], null, default);
        Assert.AreEqual(2, requests); Assert.AreEqual(ModelInstallationState.Installed, await service.InspectAsync());
    }

    [TestMethod]
    public async Task Import_CopiesVerifiedModel_PreservesExternalSource_AndDoesNotUseNetwork()
    {
        Directory.CreateDirectory(directory); var bytes = ModelBytes(); string source = Path.Combine(directory, "external.bin");
        await File.WriteAllBytesAsync(source, bytes);
        using var client = new HttpClient(new Handler(_ => throw new AssertFailedException("Import must remain offline.")));
        var service = Create(client);
        await service.ImportAsync(source, null, default); await service.DeleteAsync();
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source));
        Assert.IsFalse(File.Exists(service.ModelPath));
    }

    [TestMethod]
    public async Task NetworkFailure_CanRetry_AndUnknownSourceIsRejectedBeforeRequest()
    {
        int requests = 0;
        using var client = new HttpClient(new Handler(_ => { requests++; if (requests == 1) throw new HttpRequestException("network"); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(ModelBytes()) }; }));
        var service = Create(client);
        Assert.ThrowsExactly<ArgumentException>(() => service.DownloadAsync(new("unknown", new("https://example.com/model")), null, default));
        Assert.AreEqual(0, requests);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => service.DownloadAsync(service.Sources[0], null, default));
        await service.DownloadAsync(service.Sources[0], null, default);
        Assert.AreEqual(2, requests); Assert.AreEqual(ModelInstallationState.Installed, await service.InspectAsync());
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var response = respond(request); response.RequestMessage = request; return Task.FromResult(response); }
    }
    private sealed class ProgressSink(Action<ModelDownloadProgress> action) : IProgress<ModelDownloadProgress>
    { public void Report(ModelDownloadProgress value) => action(value); }
    private sealed class BlockedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (++calls == 1) return await base.ReadAsync(buffer[..16], token);
            Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
