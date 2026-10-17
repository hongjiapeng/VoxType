using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using VoxType.Streaming;
using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;

namespace VoxType.Infrastructure.Transcription;

public sealed class OpenAiCompatibleProvider(HttpClient client, AppSettings settings, TimeProvider? clock = null) : ITranscriptionProvider
{
    public const int MaximumResponseBytes = 1024 * 1024;
    public string Id => settings.ProviderId == "r2t2-server" ? "r2t2-server" : "openai-compatible";
    public string DisplayName => Id == "r2t2-server" ? "R2T2 本机／局域网" : "OpenAI Compatible";
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri Endpoint(string baseUrl, string providerId = "openai-compatible")
    {
        Uri uri;
        try { uri = ServerAddressPolicy.Validate(baseUrl, providerId); }
        catch (ArgumentException ex) { throw new TranscriptionException(ex.Message, "configuration"); }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/audio/transcriptions", UriKind.Absolute);
    }

    public async Task<TranscriptionResult> TranscribeAsync(AudioData audio, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint(settings.BaseUrl, Id);
        if (string.IsNullOrWhiteSpace(settings.Model)) throw new TranscriptionException("请填写在线识别模型名称。", "configuration");
        if (settings.ApiKey.Any(char.IsControl)) throw new TranscriptionException("API Key 含有无效字符，请重新填写。", "configuration");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120), clock ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio.WavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "recording.wav");
        form.Add(new StringContent(settings.Model.Trim()), "model");
        form.Add(new StringContent("json"), "response_format");
        bool streaming = Id == "r2t2-server" && settings.R2T2StreamOutput && options.PartialText is not null;
        if (streaming) form.Add(new StringContent("true"), "stream");
        if (!string.IsNullOrWhiteSpace(options.Language) && options.Language != "auto") form.Add(new StringContent(options.Language), "language");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw streaming && response.StatusCode == HttpStatusCode.BadRequest
                    ? new TranscriptionException("R2T2 服务拒绝流式请求，请检查模型和语言；若使用旧版服务，请更新服务或关闭“实时识别预览”。", "request")
                    : StatusError(response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw Oversize();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            if (streaming && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                return new(await ReadStreamingAsync(stream, options.PartialText!, token).ConfigureAwait(false), Id, options.Language == "auto" ? null : options.Language);
            using var body = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                if (body.Length + count > MaximumResponseBytes) throw Oversize();
                body.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)));
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new TranscriptionException("服务器未返回有效的 JSON text 字段，请检查兼容接口。", "format");
            var value = text.GetString()!.Trim();
            if (value.Length == 0) throw new TranscriptionException("未识别到语音，请重新录音。", "empty");
            return new(value, Id, options.Language == "auto" ? null : options.Language);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new TranscriptionException("在线识别超过 120 秒，请检查服务器后重试。", "timeout"); }
        catch (JsonException) { throw new TranscriptionException("服务器响应不是有效的 JSON，请检查兼容接口。", "format"); }
        catch (SseLimitException) { throw Oversize(); }
        catch (Exception ex) when (ex is InvalidOperationException or DecoderFallbackException or KeyNotFoundException)
        { throw new TranscriptionException("服务器返回的流式结果格式无效，请检查服务版本。", "format"); }
        catch (HttpRequestException) { throw new TranscriptionException("无法连接识别服务器，请检查网络、地址和 TLS 证书。", "network"); }
        catch (IOException) { throw new TranscriptionException("读取服务器响应失败，请检查网络后重试。", "network"); }
    }

    private async Task<string> ReadStreamingAsync(Stream stream, Action<string> onPartial, CancellationToken token)
    {
        var text = new StringBuilder();
        var time = clock ?? TimeProvider.System;
        long? lastPreview = null;
        await foreach (string data in ServerSentEvents.ReadAsync(stream, MaximumResponseBytes, token).ConfigureAwait(false))
        {
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            string? type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "transcript.text.delta":
                    string delta = root.GetProperty("delta").GetString() ?? throw InvalidStream();
                    text.Append(delta);
                    if (delta.Length > 0 && (lastPreview is null || time.GetElapsedTime(lastPreview.Value).TotalMilliseconds >= 50))
                    {
                        token.ThrowIfCancellationRequested();
                        onPartial(text.ToString());
                        lastPreview = time.GetTimestamp();
                    }
                    break;
                case "transcript.text.done":
                    string final = root.GetProperty("text").GetString() ?? throw InvalidStream();
                    if (final.Length == 0 || final != final.Trim() || final != text.ToString()) throw InvalidStream();
                    token.ThrowIfCancellationRequested();
                    onPartial(final);
                    return final;
                case "error":
                    string? code = root.GetProperty("error").GetProperty("code").GetString();
                    throw code switch
                    {
                        "timeout" => new TranscriptionException("识别超时，请缩短录音或启用 GPU。", "timeout"),
                        "truncated" => new TranscriptionException("识别输出达到限制，请缩短录音后重试。", "truncated"),
                        "empty" => new TranscriptionException("未识别到语音，请重新录音。", "empty"),
                        _ => new TranscriptionException("流式识别中断，未输入未完成的文字，请重试。", "server")
                    };
                default: throw InvalidStream();
            }
        }
        throw new TranscriptionException("流式识别连接已中断，未收到完整结果，请重试。", "incomplete");
    }

    private static TranscriptionException InvalidStream() => new("服务器返回的流式结果不完整或格式无效。", "format");
    private static TranscriptionException Oversize() => new("服务器响应过大，已停止读取。", "response-size");
    private static TranscriptionException StatusError(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => new("服务器拒绝访问，请检查 API Key 和权限。", "authentication"),
        429 => new("服务器限流或额度不足，请稍后重试。", "rate-limit"),
        >= 300 and < 400 => new("服务器要求重定向，请直接配置最终服务器地址。", "redirect"),
        >= 500 => new("识别服务器暂时不可用，请稍后重试。", "server"),
        _ => new("识别请求被拒绝，请检查服务器地址和模型名称。", "request")
    };
}
