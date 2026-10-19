using System.Net;
using VoxType.R2T2;

namespace VoxType.R2T2.Server;

public sealed record ServerOptions : R2T2Options
{
    public string ListenUrl { get; init; } = "http://127.0.0.1:8272";
    public string ApiKey { get; init; } = "";

    public override void Validate()
    {
        base.Validate();
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var listen) ||
            listen.Scheme != "http" || !IPAddress.TryParse(listen.Host.Trim('[', ']'), out var ip) ||
            listen.AbsolutePath != "/" || listen.UserInfo.Length != 0 || listen.Query.Length != 0 || listen.Fragment.Length != 0)
            throw new ArgumentException("R2T2:ListenUrl 必须是 HTTP IP 地址，例如 http://127.0.0.1:8272。HTTPS 可通过反向代理提供。");
        if (!IPAddress.IsLoopback(ip) && string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException("局域网监听必须通过 R2T2__ApiKey 配置访问密钥。");
        if (ApiKey.Length > 16384 || ApiKey.Any(char.IsControl))
            throw new ArgumentException("模型别名或 API Key 无效。");
    }
}
