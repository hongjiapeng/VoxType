using System.Text.Json;

// Protocol/process fixture only: no model inference or audio persistence.
int port = int.Parse(args[Array.IndexOf(args, "--port") + 1]);
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
app.MapGet("/health", () => Results.Ok());
app.MapGet("/entered", () => entered.Task.IsCompleted ? Results.Ok() : Results.StatusCode(503));
app.MapPost("/v1/chat/completions", async (HttpContext context) =>
{
    using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
    var root = request.RootElement;
    if (context.Request.Headers.ContainsKey("Authorization") || root.GetProperty("model").GetString() != "r2t2-q4")
    { context.Response.StatusCode = 400; return; }
    var messages = root.GetProperty("messages");
    if (messages.GetArrayLength() == 3 && messages[2].GetProperty("content").GetString()!.Contains("German"))
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, context.RequestAborted);
        return;
    }
    if (!root.GetProperty("stream").GetBoolean())
    {
        await context.Response.WriteAsJsonAsync(new { choices = new[] { new { message = new { content = "language Chinese<asr_text>本机结果。" }, finish_reason = "stop" } } });
        return;
    }
    context.Response.ContentType = "text/event-stream";
    foreach (var delta in new[] { "language Chinese<asr_", "text>本机", "结果。" })
    {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = delta } } } }) + "\n\n");
        await context.Response.Body.FlushAsync();
    }
    await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
});
await app.RunAsync();
