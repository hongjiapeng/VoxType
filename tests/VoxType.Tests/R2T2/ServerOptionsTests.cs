using VoxType.R2T2.Server;

namespace VoxType.Tests.R2T2;

[TestClass]
public sealed class ServerOptionsTests
{
    [TestMethod]
    [DataRow("http://127.0.0.1:8272", "")]
    [DataRow("http://[::1]:8272", "")]
    [DataRow("http://0.0.0.0:8272", "test-key")]
    [DataRow("http://192.168.1.3:8272", "test-key")]
    public void Listener_LoopbackOrAuthenticatedLan_Accepted(string url, string key)
    {
        var options = new ServerOptions { ListenUrl = url, ApiKey = key };
        options.Validate(); Assert.AreEqual(url, options.ListenUrl);
    }

    [TestMethod]
    [DataRow("http://0.0.0.0:8272", "")]
    [DataRow("http://192.168.1.3:8272", " ")]
    [DataRow("https://127.0.0.1:8272", "key")]
    [DataRow("http://localhost:8272", "")]
    [DataRow("http://127.0.0.1:8272/v1", "")]
    [DataRow("http://user:secret@127.0.0.1:8272", "")]
    public void UnsafeListener_Rejected(string url, string key) =>
        Assert.ThrowsExactly<ArgumentException>(() => new ServerOptions { ListenUrl = url, ApiKey = key }.Validate());

    [TestMethod]
    [DataRow("http://192.168.1.3:8273")]
    [DataRow("http://example.org:8273")]
    [DataRow("http://127.0.0.1:8273/v1")]
    [DataRow("http://127.0.0.1:8273?secret=key")]
    public void Upstream_MustRemainLocalRoot(string url) =>
        Assert.ThrowsExactly<ArgumentException>(() => new ServerOptions { UpstreamUrl = url }.Validate());

    [TestMethod]
    [DataRow(1)]
    [DataRow(115)]
    public void Timeout_AllowedBoundaries_Accepted(int seconds)
    {
        var options = new ServerOptions { TimeoutSeconds = seconds }; options.Validate(); Assert.AreEqual(seconds, options.TimeoutSeconds);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(116)]
    public void Timeout_OutsideBoundaries_Rejected(int seconds) =>
        Assert.ThrowsExactly<ArgumentException>(() => new ServerOptions { TimeoutSeconds = seconds }.Validate());

    [TestMethod]
    [DataRow("", "key")]
    [DataRow("model\n", "key")]
    [DataRow("model", "key\r\n")]
    public void InvalidModelOrKey_Rejected(string model, string key) =>
        Assert.ThrowsExactly<ArgumentException>(() => new ServerOptions { Model = model, ApiKey = key }.Validate());
}
