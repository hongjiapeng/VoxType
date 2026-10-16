using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using VoxType.R2T2.Server;

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
builder.Services.AddSingleton(options);
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
    catch (ServiceException ex) { await Error(context, ex.Status, ex.Code, ex.Message); }
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
app.MapPost("/v1/audio/transcriptions", async (HttpRequest request, LlamaTranscriber transcriber, CancellationToken token) =>
{
    if (!request.HasFormContentType || request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
        throw new ServiceException(400, "request", "请使用 multipart/form-data 提交录音。");
    var form = await request.ReadFormAsync(token);
    if (form["model"].Count != 1 || form["model"].ToString() != options.Model)
        throw new ServiceException(400, "model", "模型别名无效，请使用 /v1/models 中的模型。");
    var responseFormat = form["response_format"].ToString();
    if (responseFormat is not ("" or "json" or "text")) throw new ServiceException(400, "response-format", "仅支持 json 或 text 响应。");
    if (form["stream"].ToString() is not ("" or "false")) throw new ServiceException(400, "stream", "当前接口为整段识别，不支持实时音频流。");
    var language = form["language"].ToString();
    _ = LlamaTranscriber.ResolveLanguage(language);
    if (form.Files.Count != 1 || form.Files[0].Name != "file") throw new ServiceException(400, "file", "请提交一个名为 file 的 WAV 文件。");
    var file = form.Files[0];
    if (file.Length > ServerOptions.MaximumAudioBytes) throw new ServiceException(413, "audio-size", "录音超过大小限制。");
    using var audio = new MemoryStream();
    await file.CopyToAsync(audio, token);
    var bytes = audio.ToArray();
    WavValidator.Validate(bytes);
    var text = await transcriber.TranscribeAsync(bytes, language, form["prompt"].ToString(), token);
    return responseFormat == "text" ? Results.Text(text, "text/plain", Encoding.UTF8) : Results.Json(new { text });
});
app.Run();

static Task Error(HttpContext context, int status, string code, string message)
{
    if (context.Response.HasStarted) { context.Abort(); return Task.CompletedTask; }
    context.Response.StatusCode = status;
    return context.Response.WriteAsJsonAsync(new { error = new { code, message } }, cancellationToken: context.RequestAborted);
}
