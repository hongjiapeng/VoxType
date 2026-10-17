using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using VoxType.Streaming;

namespace VoxType.R2T2.Server;

public sealed class ServiceException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed partial class LlamaTranscriber(HttpClient client, ServerOptions options)
{
    public const int MaximumResponseBytes = 1_048_576;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly IReadOnlyDictionary<string, string> Languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "Chinese", ["en"] = "English", ["ja"] = "Japanese", ["ko"] = "Korean",
        ["de"] = "German", ["fr"] = "French", ["es"] = "Spanish", ["yue"] = "Cantonese"
    };

    public static string? ResolveLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "auto") return null;
        return Languages.TryGetValue(language, out var name) ? name : throw new ServiceException(400, "language", "不支持的语言代码。");
    }

    public async Task<bool> IsReadyAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = Request(HttpMethod.Get, "/health");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return false; }
    }

    public Task<string> TranscribeAsync(byte[] wav, string language, string context, CancellationToken token) =>
        TranscribeCoreAsync(wav, language, context, null, token);

    public Task<string> TranscribeStreamingAsync(byte[] wav, string language, string context,
        Func<string, CancellationToken, Task> onDelta, CancellationToken token) =>
        TranscribeCoreAsync(wav, language, context, onDelta, token);

    private async Task<string> TranscribeCoreAsync(byte[] wav, string language, string context,
        Func<string, CancellationToken, Task>? onDelta, CancellationToken token)
    {
        var resolvedLanguage = ResolveLanguage(language);
        if (context.Length > 4000) throw new ServiceException(400, "prompt", "热词提示不能超过 4000 字符。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        bool acquired = false;
        try
        {
            await gate.WaitAsync(deadline.Token);
            acquired = true;
            var messages = new List<object>
            {
                new { role = "system", content = context },
                new { role = "user", content = new object[] {
                    new { type = "input_audio", input_audio = new { data = Convert.ToBase64String(wav), format = "wav" } }
                } }
            };
            if (resolvedLanguage is not null)
                messages.Add(new { role = "assistant", content = $"language {resolvedLanguage}<asr_text>" });
            using var request = Request(HttpMethod.Post, "/v1/chat/completions");
            request.Content = JsonContent.Create(new { model = options.Model, messages, temperature = 0, max_tokens = 4096, stream = onDelta is not null });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new ServiceException(502, "upstream", "llama-server 拒绝识别请求，请检查模型和运行日志。");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw InvalidResponse();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            if (onDelta is not null)
            {
                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw InvalidResponse();
                return await ReadStreamingAsync(stream, onDelta, deadline.Token);
            }
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                int count = await stream.ReadAsync(buffer, deadline.Token);
                if (count == 0) break;
                if (body.Length + count > MaximumResponseBytes) throw InvalidResponse();
                body.Write(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length));
            if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                throw InvalidResponse();
            if (choices[0].TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length")
                throw new ServiceException(502, "truncated", "识别输出达到限制，请缩短录音后重试。");
            var text = CleanText(content.GetString()!);
            if (text.Length == 0) throw new ServiceException(422, "empty", "没有识别到语音。");
            deadline.Token.ThrowIfCancellationRequested();
            return text;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new ServiceException(504, "timeout", "识别超时，请缩短录音或启用 GPU。"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { throw InvalidResponse(); }
        catch (Exception ex) when (ex is SseLimitException or DecoderFallbackException) { throw InvalidResponse(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { throw new ServiceException(503, "unavailable", "无法连接本机 llama-server。"); }
        finally { if (acquired) gate.Release(); }
    }

    private static async Task<string> ReadStreamingAsync(Stream stream, Func<string, CancellationToken, Task> onDelta, CancellationToken token)
    {
        var raw = new StringBuilder();
        string emitted = "";
        bool stopped = false, done = false;
        await foreach (string data in ServerSentEvents.ReadAsync(stream, MaximumResponseBytes, token))
        {
            if (data == "[DONE]") { done = true; break; }
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw InvalidResponse();
            if (choices.GetArrayLength() == 0) continue; // Optional usage-only frame.
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            {
                if (reason.GetString() == "length") throw new ServiceException(502, "truncated", "识别输出达到限制，请缩短录音后重试。");
                if (reason.GetString() != "stop" || stopped) throw InvalidResponse();
                stopped = true;
            }
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
            {
                if (content.ValueKind != JsonValueKind.String) throw InvalidResponse();
                raw.Append(content.GetString());
                string partial = PartialText(raw.ToString());
                if (!partial.StartsWith(emitted, StringComparison.Ordinal)) throw InvalidResponse();
                if (partial.Length > emitted.Length)
                {
                    await onDelta(partial[emitted.Length..], token);
                    emitted = partial;
                }
            }
        }
        token.ThrowIfCancellationRequested();
        if (!done || !stopped) throw InvalidResponse();
        string text = CleanText(raw.ToString());
        if (text.Length == 0) throw new ServiceException(422, "empty", "没有识别到语音。");
        if (!text.StartsWith(emitted, StringComparison.Ordinal)) throw InvalidResponse();
        if (text.Length > emitted.Length) await onDelta(text[emitted.Length..], token);
        return text;
    }

    private static string CleanText(string raw) => ProtocolPrefix().Replace(raw, "")
        .Replace("<|im_end|>", "").Replace("<|endoftext|>", "").Trim();

    private static string PartialText(string raw)
    {
        string start = raw.TrimStart();
        if ("language ".StartsWith(start, StringComparison.Ordinal) ||
            start.StartsWith("language ", StringComparison.Ordinal) && !start.Contains("<asr_text>", StringComparison.Ordinal) ||
            "<asr_text>".StartsWith(start, StringComparison.Ordinal)) return "";
        string text = CleanText(raw);
        // Hold incomplete protocol markers even when split over multiple tokens.
        foreach (string marker in new[] { "<|im_end|>", "<|endoftext|>" })
            for (int length = Math.Min(marker.Length - 1, text.Length); length > 0; length--)
                if (text.EndsWith(marker[..length], StringComparison.Ordinal)) { text = text[..^length]; break; }
        return text.TrimEnd();
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, options.UpstreamUrl.TrimEnd('/') + path);
        if (options.UpstreamApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.UpstreamApiKey);
        return request;
    }
    private static ServiceException InvalidResponse() => new(502, "format", "llama-server 未返回有效的识别结果。");
    [GeneratedRegex(@"^\s*(?:language\s+\S+\s*)?<asr_text>\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolPrefix();
}
