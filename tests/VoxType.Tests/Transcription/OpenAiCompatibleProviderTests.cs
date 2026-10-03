using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;
using VoxType.Infrastructure.Transcription;

namespace VoxType.Tests.Transcription;

[TestClass]
public sealed class OpenAiCompatibleProviderTests
{
    private static readonly AudioData Audio = new([82, 73, 70, 70, 0, 255], TimeSpan.FromSeconds(1), .1f);
    [TestMethod]
    [DataRow("https://example.org/v1", "https://example.org/v1/audio/transcriptions")]
    [DataRow("https://example.org/custom/v1/", "https://example.org/custom/v1/audio/transcriptions")]
    [DataRow("http://localhost:8000/v1", "http://localhost:8000/v1/audio/transcriptions")]
    [DataRow("http://127.0.0.1:9000/", "http://127.0.0.1:9000/audio/transcriptions")]
    [DataRow("http://[::1]:9000/v1", "http://[::1]:9000/v1/audio/transcriptions")]
    public void Endpoint_PreservesBasePath(string configured, string expected) => Assert.AreEqual(expected, OpenAiCompatibleProvider.Endpoint(configured).AbsoluteUri);

    [TestMethod]
    [DataRow("http://remote.example/v1")]
    [DataRow("ftp://localhost/v1")]
    [DataRow("https://user:password@example.org/v1")]
    [DataRow("https://example.org/v1?key=secret")]
    [DataRow("https://example.org/v1#secret")]
    [DataRow("invalid")]
    public void Endpoint_RejectsUnsafeAddresses(string configured)
    {
        var error = Assert.ThrowsExactly<TranscriptionException>(() => OpenAiCompatibleProvider.Endpoint(configured));
        Assert.AreEqual("configuration", error.Code);
        Assert.DoesNotContain("secret", error.Message);
    }

    [TestMethod]
    [DataRow("", "auto")]
    [DataRow("test-key", "zh")]
    public async Task Multipart_SendsExactAudioAndSettings_ParsesChinese(string key, string language)
    {
        var handler = new Handler(async (request, token) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("http://localhost:8000/custom/v1/audio/transcriptions", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(key == "" ? null : "Bearer test-key", request.Headers.Authorization?.ToString());
            var form = (MultipartFormDataContent)request.Content!;
            var fields = form.ToDictionary(x => x.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.HasCount(language == "auto" ? 3 : 4, fields);
            Assert.AreEqual("model-name", await fields["model"].ReadAsStringAsync(token));
            Assert.AreEqual("json", await fields["response_format"].ReadAsStringAsync(token));
            Assert.AreEqual("audio/wav", fields["file"].Headers.ContentType!.MediaType);
            Assert.AreEqual("recording.wav", fields["file"].Headers.ContentDisposition!.FileName!.Trim('"'));
            CollectionAssert.AreEqual(Audio.WavBytes, await fields["file"].ReadAsByteArrayAsync(token));
            if (language == "auto") Assert.IsFalse(fields.ContainsKey("language"));
            else Assert.AreEqual("zh", await fields["language"].ReadAsStringAsync(token));
            return Json("{\"text\":\"  你好，这是语音输入。\\n第二行  \"}");
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(client, new() { BaseUrl = "http://localhost:8000/custom/v1/", Model = "model-name", ApiKey = key });
        var result = await provider.TranscribeAsync(Audio, new(language), default);
        Assert.AreEqual("你好，这是语音输入。\n第二行", result.Text);
        Assert.AreEqual("openai-compatible", result.ProviderId);
        Assert.AreEqual(language == "auto" ? null : language, result.Language);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow(401, "authentication")]
    [DataRow(403, "authentication")]
    [DataRow(429, "rate-limit")]
    [DataRow(503, "server")]
    [DataRow(302, "redirect")]
    [DataRow(400, "request")]
    public async Task StatusErrors_AreSanitizedAndNeverRetried(int status, string code)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("echo test-key secret-audio") }));
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(client, new() { ApiKey = "test-key" });
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => provider.TranscribeAsync(Audio, new("auto"), default));
        Assert.AreEqual(code, error.Code);
        Assert.DoesNotContain("test-key", error.ToString());
        Assert.DoesNotContain("secret-audio", error.ToString());
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow("<html>private test-key</html>", "format")]
    [DataRow("{}", "format")]
    [DataRow("{\"text\":123}", "format")]
    [DataRow("[]", "format")]
    [DataRow("{\"text\":\"  \\n\"}", "empty")]
    public async Task InvalidResponse_IsRejected(string body, string code)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => new OpenAiCompatibleProvider(client, new()).TranscribeAsync(Audio, new("auto"), default));
        Assert.AreEqual(code, error.Code);
        Assert.DoesNotContain("test-key", error.ToString());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OversizeResponse_RejectsDeclaredOrStreamingBody(bool declared)
    {
        var bytes = new byte[OpenAiCompatibleProvider.MaximumResponseBytes + 1];
        HttpContent content = declared ? new ByteArrayContent(bytes) : new StreamBody(new MemoryStream(bytes));
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => new OpenAiCompatibleProvider(client, new()).TranscribeAsync(Audio, new("auto"), default));
        Assert.AreEqual("response-size", error.Code);
    }

    [TestMethod]
    public async Task NetworkException_DoesNotExposeUnderlyingSecrets()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("TLS problem at test-key secret-url"));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => new OpenAiCompatibleProvider(client, new()).TranscribeAsync(Audio, new("auto"), default));
        Assert.AreEqual("network", error.Code);
        Assert.IsNull(error.InnerException);
        Assert.DoesNotContain("test-key", error.ToString());
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Timeout_IncludesBodyAfterHeaders_Uses120SecondDeadline()
    {
        var clock = new FakeTimeProvider();
        using var stream = new WaitingStream();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamBody(stream) })));
        var task = new OpenAiCompatibleProvider(client, new(), clock).TranscribeAsync(Audio, new("auto"), default);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.IsFalse(task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("timeout", error.Code);
    }

    [TestMethod]
    public async Task UserCancellation_RemainsCancellation_NotTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); });
        using var client = new HttpClient(handler);
        var task = new OpenAiCompatibleProvider(client, new()).TranscribeAsync(Audio, new("auto"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, handler.Calls);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return send(request, cancellationToken); }
    }
    private sealed class StreamBody(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }
    private sealed class WaitingStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
