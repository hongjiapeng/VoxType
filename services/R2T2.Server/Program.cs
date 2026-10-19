using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using VoxType.R2T2.Server;
using VoxType.R2T2;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("R2T2").Get<ServerOptions>() ?? new();
options.Validate();
// Own the bind address, so ASPNETCORE_URLS cannot accidentally expose a keyless instance.
builder.WebHost.UseUrls(options.ListenUrl);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = ServerOptions.MaximumRequestBytes);
builder.Services.Configure<FormOptions>(f =>
{
    f.MultipartBodyLengthLimit = ServerOptions.MaximumAudioBytes;
    f.MemoryBufferThreshold = ServerOptions.MaximumRequestBytes;
    f.ValueLengthLimit = 4096;
    f.ValueCountLimit = 8;
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton<R2T2Options>(options);
builder.Services.AddSingleton(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton<LlamaTranscriber>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    // Health is public and carries no model, text, or credential data.
    if (context.Request.Path != "/health" && options.ApiKey.Length > 0)
    {
        var expected = Encoding.UTF8.GetBytes("Bearer " + options.ApiKey);
        var actual = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            await Error(context, 401, "authentication", "访问密钥无效。");
            return;
        }
    }
    try { await next(context); }
    catch (R2T2Exception ex) { await Error(context, ex.Status, ex.Code, ex.Message); }
    catch (BadHttpRequestException ex) { await Error(context, ex.StatusCode, "request", "请求无效或超过大小限制。"); }
    catch (InvalidDataException) { await Error(context, 400, "request", "multipart 请求无效或超过大小限制。"); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
    catch (Exception)
    {
        // Avoid echoing framework exceptions that can contain client or upstream data.
        await Error(context, 500, "internal", "服务处理失败，请检查部署配置。");
    }
});
app.MapGet("/health", async (LlamaTranscriber transcriber, CancellationToken token) =>
    await transcriber.IsReadyAsync(token) ? Results.Json(new { status = "ok" }) : Results.Json(new { status = "unavailable" }, statusCode: 503));
app.MapGet("/v1/models", () => Results.Json(new { @object = "list", data = new[] { new { id = options.Model, @object = "model", owned_by = "local" } } }));
app.MapPost("/v1/audio/transcriptions", async (HttpRequest request, HttpResponse response, LlamaTranscriber transcriber, CancellationToken token) =>
{
    if (!request.HasFormContentType || request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
        throw new R2T2Exception(400, "request", "请使用 multipart/form-data 提交录音。");
    var form = await request.ReadFormAsync(token);
    if (form["model"].Count != 1 || form["model"].ToString() != options.Model)
        throw new R2T2Exception(400, "model", "模型别名无效，请使用 /v1/models 中的模型。");
    var responseFormat = form["response_format"].ToString();
    if (responseFormat is not ("" or "json" or "text")) throw new R2T2Exception(400, "response-format", "仅支持 json 或 text 响应。");
    var streamValue = form["stream"].ToString();
    if (streamValue is not ("" or "false" or "true")) throw new R2T2Exception(400, "stream", "stream 仅支持 true 或 false。");
    bool streaming = streamValue == "true";
    if (streaming && responseFormat == "text") throw new R2T2Exception(400, "response-format", "流式输出请使用 json 响应格式。");
    var language = form["language"].ToString();
    _ = LlamaTranscriber.ResolveLanguage(language);
    if (form.Files.Count != 1 || form.Files[0].Name != "file") throw new R2T2Exception(400, "file", "请提交一个名为 file 的 WAV 文件。");
    var file = form.Files[0];
    if (file.Length > ServerOptions.MaximumAudioBytes) throw new R2T2Exception(413, "audio-size", "录音超过大小限制。");
    using var audio = new MemoryStream();
    await file.CopyToAsync(audio, token);
    var bytes = audio.ToArray();
    WavValidator.Validate(bytes);
    if (streaming)
    {
        try
        {
            var finalText = await transcriber.TranscribeStreamingAsync(bytes, language, form["prompt"].ToString(),
                (delta, cancellation) => WriteEvent(response, "transcript.text.delta", new { type = "transcript.text.delta", delta }, cancellation), token);
            await WriteEvent(response, "transcript.text.done", new { type = "transcript.text.done", text = finalText }, token);
        }
        catch (R2T2Exception ex) when (response.HasStarted)
        {
            await WriteEvent(response, "error", new { type = "error", error = new { code = ex.Code, message = ex.Message } }, token);
        }
        return (IResult)Results.Empty;
    }
    var text = await transcriber.TranscribeAsync(bytes, language, form["prompt"].ToString(), token);
    return responseFormat == "text" ? Results.Text(text, "text/plain", Encoding.UTF8) : Results.Json(new { text });
});
app.Run();

static async Task WriteEvent(HttpResponse response, string name, object data, CancellationToken token)
{
    if (!response.HasStarted)
    {
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
    }
    await response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n", token);
    await response.Body.FlushAsync(token);
}

static Task Error(HttpContext context, int status, string code, string message)
{
    if (context.Response.HasStarted) { context.Abort(); return Task.CompletedTask; }
    context.Response.StatusCode = status;
    return context.Response.WriteAsJsonAsync(new { error = new { code, message } }, cancellationToken: context.RequestAborted);
}
