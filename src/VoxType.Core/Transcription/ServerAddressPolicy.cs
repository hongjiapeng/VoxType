using System.Net;
using System.Net.Sockets;

namespace VoxType.Core.Transcription;

public static class ServerAddressPolicy
{
    public static Uri Validate(string baseUrl, string providerId)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp &&
                (uri.IsLoopback || providerId == "r2t2-server" && IsPrivateIp(uri.Host))))
            throw new ArgumentException(providerId == "r2t2-server"
                ? "R2T2 地址需要 HTTPS，或本机／私有 IP 的 HTTP（如 http://192.168.1.100:8272/v1）；不能包含账号、查询或片段。"
                : "Base URL 需要 HTTPS（本机回环可使用 HTTP），且不包含账号、查询或片段。");
        return uri;
    }

    private static bool IsPrivateIp(string host)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc;
    }
}
