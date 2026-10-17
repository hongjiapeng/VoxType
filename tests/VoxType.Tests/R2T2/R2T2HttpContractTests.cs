using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Infrastructure.Transcription;
using VoxType.R2T2.Server;

namespace VoxType.Tests.R2T2;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class R2T2HttpContractTests
{
    [TestMethod]
    public async Task HealthPublic_ModelsAuthenticated_AndReadinessTracksUpstream()
    {
        await using var host = await Host.StartAsync();
        using var health = await host.Client.GetAsync("/health"); Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.AreEqual("{\"status\":\"ok\"}", await health.Content.ReadAsStringAsync());
        using var denied = await host.Client.GetAsync("/v1/models"); await AssertError(denied, HttpStatusCode.Unauthorized, "authentication");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models"); request.Headers.Authorization = new("Bearer", "contract-key");
        using var models = await host.Client.SendAsync(request); Assert.AreEqual(HttpStatusCode.OK, models.StatusCode);
        using var json = JsonDocument.Parse(await models.Content.ReadAsStringAsync()); Assert.AreEqual("r2t2-q4", json.RootElement.GetProperty("data")[0].GetProperty("id").GetString());
        host.HealthStatus = 503;
        using var unavailable = await host.Client.GetAsync("/health"); Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.AreEqual("{\"status\":\"unavailable\"}", await unavailable.Content.ReadAsStringAsync()); Assert.AreEqual(0, host.InferenceCalls);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("wrong-key")]
    public async Task MissingOrWrongKey_RejectedBeforeUpstream(string? key)
    {
        await using var host = await Host.StartAsync(); using var form = Form(); using var response = await host.PostAsync(form, key);
        await AssertError(response, HttpStatusCode.Unauthorized, "authentication"); Assert.AreEqual(0, host.InferenceCalls);
    }

    [TestMethod]
    [DataRow("model", "wrong-model", "model")]
    [DataRow("language", "invalid", "language")]
    [DataRow("response_format", "verbose_json", "response-format")]
    [DataRow("stream", "invalid", "stream")]
    [DataRow("file", "wrong-name", "file")]
    public async Task InvalidFields_RejectedBeforeUpstream(string field, string value, string errorCode)
    {
        await using var host = await Host.StartAsync(); using var form = Form(field, value); using var response = await host.PostAsync(form);
        await AssertError(response, HttpStatusCode.BadRequest, errorCode); Assert.AreEqual(0, host.InferenceCalls);
    }

    [TestMethod]
    public async Task InvalidWavAndUnsupportedMedia_RejectedBeforeUpstream()
    {
        await using var host = await Host.StartAsync(); using var form = Form(wav: [1, 2, 3]); using var invalid = await host.PostAsync(form);
        await AssertError(invalid, HttpStatusCode.BadRequest, "audio");
        using var plain = new StringContent("private-audio private-key"); using var unsupported = await host.PostAsync(plain);
        await AssertError(unsupported, HttpStatusCode.BadRequest, "request"); Assert.AreEqual(0, host.InferenceCalls);
    }

    [TestMethod]
    public async Task DuplicateModelAndOversizedAudio_RejectedBeforeUpstream()
    {
        await using var host = await Host.StartAsync();
        using var duplicate = Form(); duplicate.Add(new StringContent("r2t2-q4"), "model");
        using var duplicateResponse = await host.PostAsync(duplicate); await AssertError(duplicateResponse, HttpStatusCode.BadRequest, "model");
        using var oversize = Form(wav: WavValidatorTests.CreateWav(1920002));
        using var oversizeResponse = await host.PostAsync(oversize); await AssertError(oversizeResponse, HttpStatusCode.RequestEntityTooLarge, "audio-size");
        Assert.AreEqual(0, host.InferenceCalls);
    }

    [TestMethod]
    [DataRow("json", "application/json")]
    [DataRow("text", "text/plain")]
    public async Task SuccessfulAudio_ExactUpstreamPayloadAndResponseContract(string format, string mediaType)
    {
        await using var host = await Host.StartAsync(); using var form = Form("response_format", format); using var response = await host.PostAsync(form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode); Assert.AreEqual(mediaType, response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(format == "json" ? "{\"text\":\"你好，局域网。\"}" : "你好，局域网。", body);
        Assert.AreEqual(1, host.InferenceCalls);
        using var payload = JsonDocument.Parse(host.LastPayload!); var root = payload.RootElement;
        Assert.AreEqual("r2t2-q4", root.GetProperty("model").GetString()); Assert.AreEqual("Bearer upstream-contract-key", host.LastAuthorization);
        var messages = root.GetProperty("messages"); Assert.AreEqual("VoxType", messages[0].GetProperty("content").GetString());
        Assert.AreEqual("language Chinese<asr_text>", messages[2].GetProperty("content").GetString());
        CollectionAssert.AreEqual(WavValidatorTests.CreateWav(), Convert.FromBase64String(messages[1].GetProperty("content")[0].GetProperty("input_audio").GetProperty("data").GetString()!));
    }

    [TestMethod]
    public async Task UpstreamErrorBody_NeverExposedByHttpAdapter()
    {
        await using var host = await Host.StartAsync(); host.InferenceStatus = 401;
        using var form = Form(); using var response = await host.PostAsync(form); await AssertError(response, HttpStatusCode.BadGateway, "upstream");
        Assert.DoesNotContain("private-", await response.Content.ReadAsStringAsync()); Assert.AreEqual(1, host.InferenceCalls);
    }

    [TestMethod]
    public async Task VoxTypeProvider_UsesActualAdapterAndPreservesR2T2Identity()
    {
        await using var host = await Host.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        var provider = new OpenAiCompatibleProvider(client, new AppSettings { ProviderId = "r2t2-server", BaseUrl = host.Client.BaseAddress!.AbsoluteUri.TrimEnd('/') + "/v1", Model = "r2t2-q4", ApiKey = "contract-key" });
        var result = await provider.TranscribeAsync(new AudioData(WavValidatorTests.CreateWav(), TimeSpan.FromMilliseconds(250), .1f), new("zh"), default);
        Assert.AreEqual("你好，局域网。", result.Text); Assert.AreEqual("r2t2-server", result.ProviderId); Assert.AreEqual("zh", result.Language); Assert.AreEqual(1, host.InferenceCalls);
    }

    [TestMethod]
    public async Task StreamingHttp_PreviewArrivesBeforeUpstreamCompletes()
    {
        await using var host = await Host.StartAsync();
        host.GateStreaming = true;
        var preview = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new OpenAiCompatibleProvider(host.Client, new AppSettings { ProviderId = "r2t2-server", BaseUrl = host.Client.BaseAddress + "v1", Model = "r2t2-q4", ApiKey = "contract-key" });
        var pending = provider.TranscribeAsync(new(WavValidatorTests.CreateWav(), TimeSpan.FromMilliseconds(250), .1f), new("zh", text => preview.TrySetResult(text)), default);
        try
        {
            Assert.AreEqual("你好", await preview.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(pending.IsCompleted, "The preview must be visible while upstream decoding is still blocked.");
        }
        finally { host.ContinueStreaming.TrySetResult(); }
        Assert.AreEqual("你好，局域网。", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Text);
        using var payload = JsonDocument.Parse(host.LastPayload!);
        Assert.IsTrue(payload.RootElement.GetProperty("stream").GetBoolean());
    }

    [TestMethod]
    public async Task StreamingHttp_ErrorAfterPreviewDoesNotReturnFinalResult()
    {
        await using var host = await Host.StartAsync();
        host.TruncateStream = true;
        var previews = new List<string>();
        var provider = new OpenAiCompatibleProvider(host.Client, new AppSettings { ProviderId = "r2t2-server", BaseUrl = host.Client.BaseAddress + "v1", Model = "r2t2-q4", ApiKey = "contract-key" });
        var error = await Assert.ThrowsExactlyAsync<VoxType.Core.Transcription.TranscriptionException>(() => provider.TranscribeAsync(new(WavValidatorTests.CreateWav(), TimeSpan.FromMilliseconds(250), .1f), new("zh", previews.Add), default));
        Assert.AreEqual("truncated", error.Code);
        Assert.AreEqual("你好", previews[0]);
        Assert.DoesNotContain("private-", error.ToString());
    }

    private static MultipartFormDataContent Form(string? overrideField = null, string? value = null, byte[]? wav = null)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, original) in new[] { ("model", "r2t2-q4"), ("language", "zh"), ("prompt", "VoxType"), ("response_format", "json") })
            form.Add(new StringContent(overrideField == name ? value! : original), name);
        if (overrideField == "stream") form.Add(new StringContent(value!), "stream");
        var file = new ByteArrayContent(wav ?? WavValidatorTests.CreateWav()); file.Headers.ContentType = new("audio/wav");
        form.Add(file, overrideField == "file" ? value! : "file", "recording.wav"); return form;
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.AreEqual(status, response.StatusCode); Assert.AreEqual("application/json", response.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.AreEqual(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class Host : IAsyncDisposable
    {
        private WebApplication upstream = null!;
        private Process? adapter;
        private Task<string>? standardOutput;
        private Task<string>? standardError;
        private int inferenceCalls;
        public int InferenceCalls => Volatile.Read(ref inferenceCalls);
        public int HealthStatus { get; set; } = 200;
        public int InferenceStatus { get; set; } = 200;
        public string? LastPayload { get; private set; }
        public string? LastAuthorization { get; private set; }
        public bool GateStreaming { get; set; }
        public bool TruncateStream { get; set; }
        public TaskCompletionSource ContinueStreaming { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient Client { get; private set; } = null!;

        public static async Task<Host> StartAsync()
        {
            var host = new Host();
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
                builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                host.upstream = builder.Build();
                host.upstream.MapGet("/health", () => Results.Json(new { status = "ok" }, statusCode: host.HealthStatus));
                host.upstream.MapPost("/v1/chat/completions", async (HttpRequest request, HttpResponse response) =>
                {
                    Interlocked.Increment(ref host.inferenceCalls);
                    host.LastAuthorization = request.Headers.Authorization.ToString();
                    using var reader = new StreamReader(request.Body); host.LastPayload = await reader.ReadToEndAsync();
                    using var payload = JsonDocument.Parse(host.LastPayload);
                    if (host.InferenceStatus == 200 && payload.RootElement.GetProperty("stream").GetBoolean())
                    {
                        response.ContentType = "text/event-stream";
                        await response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"language Chinese<asr_text>你好\"}}]}\n\n", request.HttpContext.RequestAborted);
                        await response.Body.FlushAsync(request.HttpContext.RequestAborted);
                        if (host.GateStreaming) await host.ContinueStreaming.Task.WaitAsync(request.HttpContext.RequestAborted);
                        string tail = host.TruncateStream
                            ? "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n"
                            : "data: {\"choices\":[{\"delta\":{\"content\":\"，局域网。<|im_end|>\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
                        await response.WriteAsync(tail, request.HttpContext.RequestAborted);
                        return (IResult)Results.Empty;
                    }
                    return host.InferenceStatus == 200
                        ? Results.Json(new { choices = new[] { new { message = new { content = "language Chinese<asr_text>你好，局域网。<|im_end|>" }, finish_reason = "stop" } } })
                        : Results.Text("private-audio private-key", statusCode: host.InferenceStatus);
                });
                await host.upstream.StartAsync();
                var upstreamUrl = host.upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                var adapterUrl = $"http://127.0.0.1:{port}";
                var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
                foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "VoxType.Tests.runtimeconfig.json"), "--depsfile", Path.Combine(AppContext.BaseDirectory, "VoxType.Tests.deps.json"), typeof(LlamaTranscriber).Assembly.Location }) info.ArgumentList.Add(argument);
                info.Environment["R2T2__ListenUrl"] = adapterUrl; info.Environment["R2T2__UpstreamUrl"] = upstreamUrl;
                info.Environment["R2T2__ApiKey"] = "contract-key"; info.Environment["R2T2__UpstreamApiKey"] = "upstream-contract-key";
                info.Environment["R2T2__Model"] = "r2t2-q4"; info.Environment["Logging__LogLevel__Default"] = "Warning";
                host.adapter = Process.Start(info)!; host.standardOutput = host.adapter.StandardOutput.ReadToEndAsync(); host.standardError = host.adapter.StandardError.ReadToEndAsync();
                host.Client = new HttpClient { BaseAddress = new(adapterUrl), Timeout = TimeSpan.FromSeconds(10) };
                var stop = Stopwatch.StartNew();
                while (stop.Elapsed < TimeSpan.FromSeconds(15))
                {
                    if (host.adapter.HasExited) Assert.Fail("Adapter exited during startup: " + await host.standardError + await host.standardOutput);
                    try { using var response = await host.Client.GetAsync("/v1/models"); if (response.StatusCode == HttpStatusCode.Unauthorized) return host; }
                    catch (HttpRequestException) { }
                    await Task.Delay(50);
                }
                Assert.Fail("Adapter did not start within 15 seconds."); return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        public Task<HttpResponseMessage> PostAsync(HttpContent content, string? key = "contract-key")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/transcriptions") { Content = content };
            if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return SendAsync(request);
        }
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            using (request) return await Client.SendAsync(request);
        }
        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (adapter is not null)
            {
                if (!adapter.HasExited) adapter.Kill(entireProcessTree: true);
                await adapter.WaitForExitAsync(); adapter.Dispose();
                if (standardOutput is not null) await standardOutput; if (standardError is not null) await standardError;
            }
            if (upstream is not null) await upstream.DisposeAsync();
        }
    }
}
