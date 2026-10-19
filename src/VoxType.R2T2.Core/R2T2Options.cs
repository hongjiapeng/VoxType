namespace VoxType.R2T2;

// Only the inference protocol belongs here. HTTP hosting and LAN credentials stay in Server.
public record R2T2Options
{
    public const int MaximumAudioBytes = 2_000_000;
    public const int MaximumRequestBytes = MaximumAudioBytes + 65_536;
    public string UpstreamUrl { get; init; } = "http://127.0.0.1:8273";
    public string Model { get; init; } = "r2t2-q4";
    public string UpstreamApiKey { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 115;

    public virtual void Validate()
    {
        if (!Uri.TryCreate(UpstreamUrl, UriKind.Absolute, out var upstream) ||
            upstream.Scheme is not ("http" or "https") || !upstream.IsLoopback ||
            upstream.UserInfo.Length != 0 || upstream.Query.Length != 0 || upstream.Fragment.Length != 0 || upstream.AbsolutePath != "/")
            throw new ArgumentException("UpstreamUrl 必须指向本机 llama-server 的根地址。");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 512 || Model.Any(char.IsControl) ||
            UpstreamApiKey.Length > 16384 || UpstreamApiKey.Any(char.IsControl))
            throw new ArgumentException("模型别名或推理凭据无效。");
        if (TimeoutSeconds is < 1 or > 115) throw new ArgumentException("推理超时必须在 1–115 秒之间。");
    }
}
