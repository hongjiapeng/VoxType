using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;

namespace VoxType.Infrastructure.Transcription;

public sealed class OpenAiCompatibleProvider(HttpClient client, AppSettings settings, TimeProvider? clock = null) : ITranscriptionProvider
{
    public const int MaximumResponseBytes = 1024 * 1024;
    public string Id => "openai-compatible";
    public string DisplayName => "OpenAI Compatible";
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri Endpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new TranscriptionException("服务器地址需为 HTTPS；本机服务器可使用 HTTP。地址不能含账号、查询参数或片段。", "configuration");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/audio/transcriptions", UriKind.Absolute);
    }

    public async Task<TranscriptionResult> TranscribeAsync(AudioData audio, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint(settings.BaseUrl);
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
        if (!string.IsNullOrWhiteSpace(options.Language) && options.Language != "auto") form.Add(new StringContent(options.Language), "language");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw StatusError(response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw Oversize();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
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
        catch (HttpRequestException) { throw new TranscriptionException("无法连接识别服务器，请检查网络、地址和 TLS 证书。", "network"); }
        catch (IOException) { throw new TranscriptionException("读取服务器响应失败，请检查网络后重试。", "network"); }
    }
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
