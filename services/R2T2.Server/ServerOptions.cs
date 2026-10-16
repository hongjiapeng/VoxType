using System.Net;

namespace VoxType.R2T2.Server;

public sealed record ServerOptions
{
    public const int MaximumAudioBytes = 2_000_000;
    public const int MaximumRequestBytes = MaximumAudioBytes + 65_536;
    public string ListenUrl { get; init; } = "http://127.0.0.1:8272";
    public string UpstreamUrl { get; init; } = "http://127.0.0.1:8273";
    public string Model { get; init; } = "r2t2-q4";
    public string ApiKey { get; init; } = "";
    public string UpstreamApiKey { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 115;

    public void Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var listen) ||
            listen.Scheme != "http" || !IPAddress.TryParse(listen.Host.Trim('[', ']'), out var ip) ||
            listen.AbsolutePath != "/" || listen.UserInfo.Length != 0 || listen.Query.Length != 0 || listen.Fragment.Length != 0)
            throw new ArgumentException("R2T2:ListenUrl 必须是 HTTP IP 地址，例如 http://127.0.0.1:8272。HTTPS 可通过反向代理提供。");
        if (!IPAddress.IsLoopback(ip) && string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException("局域网监听必须通过 R2T2__ApiKey 配置访问密钥。");
        if (!Uri.TryCreate(UpstreamUrl, UriKind.Absolute, out var upstream) ||
            upstream.Scheme is not ("http" or "https") || !upstream.IsLoopback ||
            upstream.UserInfo.Length != 0 || upstream.Query.Length != 0 || upstream.Fragment.Length != 0 || upstream.AbsolutePath != "/")
            throw new ArgumentException("R2T2:UpstreamUrl 必须指向本机 llama-server 的根地址。");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 512 || Model.Any(char.IsControl) ||
            ApiKey.Length > 16384 || UpstreamApiKey.Length > 16384 || ApiKey.Any(char.IsControl) || UpstreamApiKey.Any(char.IsControl))
            throw new ArgumentException("模型别名或 API Key 无效。");
        if (TimeoutSeconds is < 1 or > 115) throw new ArgumentException("推理超时必须在 1–115 秒之间。");
    }
}
