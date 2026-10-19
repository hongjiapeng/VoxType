using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;
using VoxType.Infrastructure.Transcription;
using VoxType.R2T2;

namespace VoxType.Tests.R2T2;

[TestClass]
public sealed class StreamingTranscriptionTests
{
    private static readonly AudioData Audio = new([1], TimeSpan.FromSeconds(1), .1f);
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(4096)]
    public async Task LlamaStreaming_SplitProtocolAndUtf8ProducesCleanDeltas(int readSize)
    {
        string body = "\uFEFF: heartbeat\r\n\r\n" + Frame("lan") + Frame("guage Chinese<asr_") + Frame("text> 你好") +
            Frame("，world ") + Frame("again<|im_") + Frame("end|><|endof") + Frame("text|>", "stop") + "data: [DONE]\n\n";
        using var client = Client(body, readSize);
        var chunks = new List<string>();
        string result = await new LlamaTranscriber(client, new()).TranscribeStreamingAsync([], "auto", "", (text, _) => { chunks.Add(text); return Task.CompletedTask; }, default);
        Assert.AreEqual("你好，world again", result);
        Assert.AreEqual(result, string.Concat(chunks));
        Assert.AreEqual("你好", chunks[0]);
        Assert.DoesNotContain("<", string.Concat(chunks));
        Assert.IsGreaterThan(1, chunks.Count);
    }

    [TestMethod]
    [DataRow("missing-done", "format")]
    [DataRow("missing-stop", "format")]
    [DataRow("truncated", "truncated")]
    [DataRow("empty", "empty")]
    [DataRow("malformed", "format")]
    public async Task LlamaStreaming_UnfinishedOrInvalidOutputNeverSucceeds(string scenario, string code)
    {
        string body = scenario switch
        {
            "missing-done" => Frame("半句话", "stop"),
            "missing-stop" => Frame("半句话") + "data: [DONE]\n\n",
            "truncated" => Frame("半句话", "length") + "data: [DONE]\n\n",
            "empty" => Frame("language Chinese<asr_text>", "stop") + "data: [DONE]\n\n",
            _ => "data: {\"choices\":\"private-key\"}\n\n"
        };
        using var client = Client(body);
        var error = await Assert.ThrowsExactlyAsync<R2T2Exception>(() => new LlamaTranscriber(client, new()).TranscribeStreamingAsync([], "auto", "", (_, _) => Task.CompletedTask, default));
        Assert.AreEqual(code, error.Code);
        Assert.DoesNotContain("private-key", error.ToString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LlamaStreaming_ResponseBudgetChecksExactBoundary(bool oversize)
    {
        string frames = Frame("hello", "stop") + "data: [DONE]\n\n";
        string body = ":" + new string('x', LlamaTranscriber.MaximumResponseBytes - Encoding.UTF8.GetByteCount(frames) - 3 + (oversize ? 1 : 0)) + "\n\n" + frames;
        using var client = Client(body);
        var pending = new LlamaTranscriber(client, new()).TranscribeStreamingAsync([], "auto", "", (_, _) => Task.CompletedTask, default);
        if (oversize) Assert.AreEqual("format", (await Assert.ThrowsExactlyAsync<R2T2Exception>(() => pending)).Code);
        else Assert.AreEqual("hello", await pending);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LlamaStreaming_CancellationOrTimeoutReleasesInferenceGate(bool callerCancels)
    {
        var pipe = new Pipe();
        using var cancellation = new CancellationTokenSource();
        var preview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var client = new HttpClient(new LlamaTranscriberTests.Handler((_, _) => Task.FromResult(++calls == 1
            ? Response(pipe.Reader.AsStream()) : LlamaTranscriberTests.Completion("next"))));
        var transcriber = new LlamaTranscriber(client, new() { TimeoutSeconds = 1 });
        var pending = transcriber.TranscribeStreamingAsync([], "auto", "", (_, _) => { preview.TrySetResult(); return Task.CompletedTask; }, cancellation.Token);
        try
        {
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Frame("半句话")));
            await preview.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (callerCancels)
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else Assert.AreEqual("timeout", (await Assert.ThrowsExactlyAsync<R2T2Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)))).Code);
            Assert.AreEqual("next", await transcriber.TranscribeAsync([], "auto", "", default));
        }
        finally { cancellation.Cancel(); await pipe.Writer.CompleteAsync(); }
    }

    [TestMethod]
    public async Task R2T2Streaming_PreviewPrecedesCompletionAndFinalTextMatches()
    {
        var pipe = new Pipe();
        var preview = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new LlamaTranscriberTests.Handler(async (request, token) =>
        {
            var form = (MultipartFormDataContent)request.Content!;
            var fields = form.ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.AreEqual("true", await fields["stream"].ReadAsStringAsync(token));
            Assert.AreEqual("json", await fields["response_format"].ReadAsStringAsync(token));
            return Response(pipe.Reader.AsStream());
        }));
        var pending = Provider(client).TranscribeAsync(Audio, new("zh", text => preview.TrySetResult(text)), default);
        try
        {
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Event("transcript.text.delta", "你好")));
            Assert.AreEqual("你好", await preview.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(pending.IsCompleted);
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Event("transcript.text.delta", "。") + Event("transcript.text.done", "你好。")));
            Assert.AreEqual("你好。", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Text);
        }
        finally { await pipe.Writer.CompleteAsync(); }
    }

    [TestMethod]
    [DataRow("incomplete", "incomplete")]
    [DataRow("mismatch", "format")]
    [DataRow("malformed", "format")]
    [DataRow("error", "truncated")]
    public async Task R2T2Streaming_FailedStreamCannotBecomeSuccessfulResult(string scenario, string code)
    {
        string body = Event("transcript.text.delta", "你好") + (scenario switch
        {
            "mismatch" => Event("transcript.text.done", "另一个结果"),
            "malformed" => "data: {\"type\":\"transcript.text.delta\"}\n\n",
            "error" => "data: {\"type\":\"error\",\"error\":{\"code\":\"truncated\",\"message\":\"private-audio private-key\"}}\n\n",
            _ => ""
        });
        using var client = Client(body, 1);
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => Provider(client).TranscribeAsync(Audio, new("zh", _ => { }), default));
        Assert.AreEqual(code, error.Code);
        Assert.DoesNotContain("private-", error.ToString());
    }

    [TestMethod]
    [DataRow("r2t2-server", false)]
    [DataRow("openai-compatible", true)]
    public async Task StreamingDisabledOrOtherProviderKeepsJsonRequest(string id, bool enabled)
    {
        using var client = new HttpClient(new LlamaTranscriberTests.Handler((request, _) =>
        {
            var form = (MultipartFormDataContent)request.Content!;
            Assert.IsFalse(form.Any(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "stream"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"完整结果\"}", Encoding.UTF8, "application/json") });
        }));
        var provider = new OpenAiCompatibleProvider(client, new() { ProviderId = id, R2T2StreamOutput = enabled, BaseUrl = "http://127.0.0.1:8272/v1", Model = "r2t2-q4" });
        Assert.AreEqual("完整结果", (await provider.TranscribeAsync(Audio, new("zh", _ => Assert.Fail("JSON responses have no partial preview.")), default)).Text);
    }

    private static OpenAiCompatibleProvider Provider(HttpClient client) => new(client,
        new() { ProviderId = "r2t2-server", BaseUrl = "http://127.0.0.1:8272/v1", Model = "r2t2-q4" });
    private static HttpClient Client(string body, int readSize = 4096) => new(new LlamaTranscriberTests.Handler((_, _) =>
        Task.FromResult(Response(new ChunkedStream(Encoding.UTF8.GetBytes(body), readSize)))));
    private static HttpResponseMessage Response(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }
    private static string Frame(string text, string? reason = null) => "data: " + JsonSerializer.Serialize(
        new { choices = new[] { new { delta = new { content = text }, finish_reason = reason } } }, Json) + "\r\n\r\n";
    private static string Event(string type, string value) => "data: " + (type.EndsWith("delta", StringComparison.Ordinal)
        ? JsonSerializer.Serialize(new { type, delta = value }, Json)
        : JsonSerializer.Serialize(new { type, text = value }, Json)) + "\n\n";
    private sealed class ChunkedStream(byte[] bytes, int readSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, readSize)], cancellationToken);
    }
}
