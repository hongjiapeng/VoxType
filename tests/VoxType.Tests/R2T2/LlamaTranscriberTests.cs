using System.Net;
using System.Text;
using System.Text.Json;
using VoxType.R2T2.Server;

namespace VoxType.Tests.R2T2;

[TestClass]
public sealed class LlamaTranscriberTests
{
    [TestMethod]
    [DataRow("zh", "Chinese")]
    [DataRow("EN", "English")]
    [DataRow("auto", null)]
    [DataRow("", null)]
    public async Task Request_ExactAudioContextLanguageAndParameters(string language, string? resolved)
    {
        var wav = WavValidatorTests.CreateWav();
        var handler = new Handler(async (request, token) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("http://127.0.0.1:8273/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Bearer upstream-key", request.Headers.Authorization!.ToString());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = json.RootElement;
            Assert.AreEqual("r2t2-q4", root.GetProperty("model").GetString());
            Assert.AreEqual(0, root.GetProperty("temperature").GetInt32()); Assert.AreEqual(4096, root.GetProperty("max_tokens").GetInt32());
            Assert.IsFalse(root.GetProperty("stream").GetBoolean());
            var messages = root.GetProperty("messages"); Assert.AreEqual(resolved is null ? 2 : 3, messages.GetArrayLength());
            Assert.AreEqual("system", messages[0].GetProperty("role").GetString()); Assert.AreEqual("VoxType 热词", messages[0].GetProperty("content").GetString());
            Assert.AreEqual("user", messages[1].GetProperty("role").GetString()); var audio = messages[1].GetProperty("content")[0];
            Assert.AreEqual("input_audio", audio.GetProperty("type").GetString()); Assert.AreEqual("wav", audio.GetProperty("input_audio").GetProperty("format").GetString());
            CollectionAssert.AreEqual(wav, Convert.FromBase64String(audio.GetProperty("input_audio").GetProperty("data").GetString()!));
            if (resolved is not null) { Assert.AreEqual("assistant", messages[2].GetProperty("role").GetString()); Assert.AreEqual($"language {resolved}<asr_text>", messages[2].GetProperty("content").GetString()); }
            return Completion(" language Chinese<asr_text> 你好。<|im_end|><|endoftext|> ");
        });
        using var client = new HttpClient(handler);
        Assert.AreEqual("你好。", await new LlamaTranscriber(client, new() { UpstreamApiKey = "upstream-key" }).TranscribeAsync(wav, language, "VoxType 热词", default));
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow("<asr_text> hello ", "hello")]
    [DataRow(" 你好。 ", "你好。")]
    public async Task Response_PrefixOptionalAndPlainTextPreserved(string body, string expected)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Completion(body))));
        Assert.AreEqual(expected, await new LlamaTranscriber(client, new()).TranscribeAsync([], "auto", "", default));
    }

    [TestMethod]
    [DataRow("<html>private-secret</html>", 502, "format")]
    [DataRow("[]", 502, "format")]
    [DataRow("{}", 502, "format")]
    [DataRow("{\"choices\":[]}", 502, "format")]
    [DataRow("{\"choices\":[{\"message\":{\"content\":123}}]}", 502, "format")]
    [DataRow("{\"choices\":[{\"message\":{\"content\":\"<asr_text>  \"}}]}", 422, "empty")]
    [DataRow("{\"choices\":[{\"message\":{\"content\":\"partial-private-secret\"},\"finish_reason\":\"length\"}]}", 502, "truncated")]
    public async Task InvalidResponse_ExactStatusCodeAndSanitization(string body, int status, string code)
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(body))); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => new LlamaTranscriber(client, new()).TranscribeAsync([], "auto", "", default));
        Assert.AreEqual(status, error.Status); Assert.AreEqual(code, error.Code); Assert.DoesNotContain("private-secret", error.ToString()); Assert.IsNull(error.InnerException); Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow(400)]
    [DataRow(401)]
    [DataRow(503)]
    [DataRow(302)]
    public async Task UpstreamFailure_SanitizedAndNeverRetried(int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-audio private-key") })); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => new LlamaTranscriber(client, new()).TranscribeAsync([], "auto", "", default));
        Assert.AreEqual(502, error.Status); Assert.AreEqual("upstream", error.Code); Assert.DoesNotContain("private-", error.ToString()); Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task ResponseSize_DeclaredAndUndeclaredBodiesBounded()
    {
        foreach (var declared in new[] { true, false })
        {
            var bytes = new byte[LlamaTranscriber.MaximumResponseBytes + 1];
            using var content = declared ? (HttpContent)new ByteArrayContent(bytes) : new StreamContent(new NonSeekableStream(bytes));
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
            var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => new LlamaTranscriber(client, new()).TranscribeAsync([], "auto", "", default));
            Assert.AreEqual(502, error.Status); Assert.AreEqual("format", error.Code);
        }
    }

    [TestMethod]
    public async Task InvalidLanguageOrOversizePrompt_DoesNotContactUpstream()
    {
        var handler = new Handler((_, _) => Task.FromResult(Completion("hello"))); using var client = new HttpClient(handler); var service = new LlamaTranscriber(client, new());
        var language = await Assert.ThrowsExactlyAsync<ServiceException>(() => service.TranscribeAsync([], "invalid", "", default));
        Assert.AreEqual(400, language.Status); Assert.AreEqual("language", language.Code);
        var prompt = await Assert.ThrowsExactlyAsync<ServiceException>(() => service.TranscribeAsync([], "auto", new string('x', 4001), default));
        Assert.AreEqual(400, prompt.Status); Assert.AreEqual("prompt", prompt.Code); Assert.AreEqual(0, handler.Calls);
        Assert.AreEqual("hello", await service.TranscribeAsync([], "auto", new string('x', 4000), default)); Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task CallerCancellationAndQueuedCancellation_ReleaseGateForNextRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Completion("never"); });
        using var client = new HttpClient(handler); var service = new LlamaTranscriber(client, new());
        using var firstCancellation = new CancellationTokenSource(); using var queuedCancellation = new CancellationTokenSource();
        var first = service.TranscribeAsync([], "auto", "", firstCancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = service.TranscribeAsync([], "auto", "", queuedCancellation.Token); queuedCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued); Assert.AreEqual(1, handler.Calls);
        firstCancellation.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously); using var nextCancellation = new CancellationTokenSource();
        var next = service.TranscribeAsync([], "auto", "", nextCancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, handler.Calls); nextCancellation.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => next);
    }

    [TestMethod]
    public async Task ConcurrentRequests_AreSerialized_AndBothReturnTheirOwnResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requestNumber = 0;
        var handler = new Handler(async (_, token) =>
        {
            int current = Interlocked.Increment(ref requestNumber);
            if (current == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
            return Completion(current == 1 ? "first" : "second");
        });
        using var client = new HttpClient(handler); var service = new LlamaTranscriber(client, new());
        var first = service.TranscribeAsync([], "auto", "", default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.TranscribeAsync([], "auto", "", default);
        Assert.AreEqual(1, handler.Calls); Assert.IsFalse(second.IsCompleted);
        release.SetResult(); Assert.AreEqual("first", await first); Assert.AreEqual("second", await second); Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task ResponseBodyAfterHeaders_IsIncludedInDeadline()
    {
        using var body = new WaitingStream();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        var request = new LlamaTranscriber(client, new() { TimeoutSeconds = 1 }).TranscribeAsync([], "auto", "", default);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(504, error.Status); Assert.AreEqual("timeout", error.Code); Assert.IsTrue(body.ReadCancelled);
    }

    [TestMethod]
    public async Task Response_ExactlyAtByteLimit_Accepted()
    {
        const string json = "{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}";
        var bytes = Encoding.UTF8.GetBytes(json + new string(' ', LlamaTranscriber.MaximumResponseBytes - Encoding.UTF8.GetByteCount(json)));
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        Assert.AreEqual("hello", await new LlamaTranscriber(client, new()).TranscribeAsync([], "auto", "", default));
    }

    [TestMethod]
    public async Task Timeout_ConvertedTo504_AndGateReleased()
    {
        var handler = new Handler((_, token) => Task.FromException<HttpResponseMessage>(new TaskCanceledException())); using var client = new HttpClient(handler);
        var service = new LlamaTranscriber(client, new());
        for (int i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => service.TranscribeAsync([], "auto", "", default));
            Assert.AreEqual(504, error.Status); Assert.AreEqual("timeout", error.Code);
        }
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task NetworkFailure_SanitizedWithoutInnerException()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("private-url private-key")));
        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => new LlamaTranscriber(client, new()).TranscribeAsync([], "auto", "", default));
        Assert.AreEqual(503, error.Status); Assert.AreEqual("unavailable", error.Code); Assert.IsNull(error.InnerException); Assert.DoesNotContain("private-", error.ToString());
    }

    [TestMethod]
    [DataRow(200, true)]
    [DataRow(503, false)]
    public async Task Readiness_UsesLocalHealthAndUpstreamKey(int status, bool ready)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method); Assert.AreEqual("http://127.0.0.1:8273/health", request.RequestUri!.AbsoluteUri); Assert.AreEqual("Bearer upstream-key", request.Headers.Authorization!.ToString());
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        }));
        Assert.AreEqual(ready, await new LlamaTranscriber(client, new() { UpstreamApiKey = "upstream-key" }).IsReadyAsync(default));
    }

    [TestMethod]
    public async Task Readiness_NetworkFailureAndCancellation_ReturnUnavailable()
    {
        using var failing = new HttpClient(new Handler((_, _) => throw new HttpRequestException("private")));
        Assert.IsFalse(await new LlamaTranscriber(failing, new()).IsReadyAsync(default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var waiting = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); }));
        Assert.IsFalse(await new LlamaTranscriber(waiting, new()).IsReadyAsync(cancelled.Token));
    }

    internal static HttpResponseMessage Completion(string text) => Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text }, finish_reason = "stop" } } }));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int calls; public int Calls => calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Interlocked.Increment(ref calls); return send(request, token); }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class WaitingStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReadCancelled { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            catch (OperationCanceledException) { ReadCancelled = true; throw; }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
