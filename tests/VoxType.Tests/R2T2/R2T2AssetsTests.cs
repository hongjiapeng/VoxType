using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VoxType.Infrastructure.Transcription;

namespace VoxType.Tests.R2T2;

[TestClass]
public sealed class R2T2AssetsTests
{
    [TestMethod]
    public async Task Assets_RejectsCorruptDownloadAndCleansPartialFile()
    {
        using var root = new TestDirectory();
        using var client = new HttpClient(new AssetHandler("bad!"u8.ToArray()));
        var asset = new R2T2Asset("model.gguf", 4, Convert.ToHexString(SHA256.HashData("GGUF"u8)));
        string path = Path.Combine(root.Path, asset.Name);
        await Assert.ThrowsExactlyAsync<IOException>(() => R2T2Assets.DownloadAsync(client, new("https://example.test/model"), path, asset, true, null, default));
        Assert.IsEmpty(Directory.GetFiles(root.Path));
    }

    [TestMethod]
    public async Task Assets_ReusesValidatedModelWithoutNetwork()
    {
        using var root = new TestDirectory();
        var handler = new AssetHandler([]);
        using var client = new HttpClient(handler);
        byte[] bytes = "GGUFexisting-model"u8.ToArray();
        var asset = new R2T2Asset("model.gguf", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        string path = Path.Combine(root.Path, asset.Name);
        await File.WriteAllBytesAsync(path, bytes);
        await R2T2Assets.DownloadAsync(client, new("https://example.test/model"), path, asset, true, null, default);
        Assert.AreEqual(0, handler.Requests);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    [DataRow("../escaped.dll")]
    [DataRow("llama-server.exe")]
    public async Task Runtime_RejectsTraversalAndDuplicateEntriesBeforeWriting(string extra)
    {
        using var root = new TestDirectory();
        string archive = Path.Combine(root.Path, "runtime.zip"), target = Path.Combine(root.Path, "runtime");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntry("llama-server.exe"); zip.CreateEntry(extra);
        }
        await Assert.ThrowsExactlyAsync<IOException>(() => R2T2Assets.ExpandRuntimeAsync(archive, target, default));
        Assert.IsFalse(Directory.Exists(target));
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "escaped.dll")));
    }

    [TestMethod]
    public async Task Runtime_ReusesLockedValidExecutableAndRepairsDamagedDll()
    {
        using var root = new TestDirectory();
        string source = Path.Combine(root.Path, "source"), target = Path.Combine(root.Path, "runtime"), archive = Path.Combine(root.Path, "runtime.zip");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "llama-server.exe"), "server-v1");
        await File.WriteAllTextAsync(Path.Combine(source, "ggml.dll"), "library-v1");
        ZipFile.CreateFromDirectory(source, archive);
        await R2T2Assets.ExpandRuntimeAsync(archive, target, default);
        await File.WriteAllTextAsync(Path.Combine(target, "ggml.dll"), "broken");
        using var held = File.Open(Path.Combine(target, "llama-server.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        await R2T2Assets.ExpandRuntimeAsync(archive, target, default);
        Assert.AreEqual("library-v1", await File.ReadAllTextAsync(Path.Combine(target, "ggml.dll")));
    }

    [TestMethod]
    public void PortProbe_RejectsOccupiedPortAndReleasesProbe()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.ThrowsExactly<IOException>(() => R2T2LocalService.AssertPortAvailable(port));
        listener.Stop();
        R2T2LocalService.AssertPortAvailable(port);
        listener.Start();
    }

    private sealed class AssetHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VoxType assets " + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
