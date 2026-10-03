using System.Net;
using System.Net.Sockets;
using System.Text;
using VoxType.Core.Audio;
using VoxType.Core.Settings;
using VoxType.Core.Transcription;
using VoxType.Infrastructure.Transcription;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace VoxType.Tests.Transcription;

[TestClass]
public sealed class WindowsRuntimeTests
{
    [TestMethod]
    [TestCategory("WindowsRuntime")]
    public void PackagedCpuDependency_CanLoadAndInvokeRuntimeExport()
    {
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
        string? information = WhisperFactory.GetRuntimeInfo();
        Assert.IsFalse(string.IsNullOrWhiteSpace(information));
        Assert.AreEqual(RuntimeLibrary.Cpu, RuntimeOptions.LoadedLibrary);
    }

    [TestMethod]
    public async Task ProductionHttpClient_RejectsRealLoopbackRedirectWithoutFollowing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = connection.GetStream();
            var header = new List<byte>(); byte[] one = new byte[1];
            while (header.Count < 32768)
            {
                await stream.ReadExactlyAsync(one, deadline.Token); header.Add(one[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            string requestHeader = Encoding.ASCII.GetString(header.ToArray());
            string lengthLine = requestHeader.Split("\r\n").Single(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            int length = int.Parse(lengthLine.Split(':')[1].Trim());
            await stream.ReadExactlyAsync(new byte[length], deadline.Token);
            byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, deadline.Token); listener.Stop();
            return requestHeader.Split("\r\n")[0];
        }, deadline.Token);
        using var client = OpenAiCompatibleProvider.CreateClient();
        var provider = new OpenAiCompatibleProvider(client, new AppSettings { BaseUrl = $"http://127.0.0.1:{port}/v1" });
        var error = await Assert.ThrowsExactlyAsync<TranscriptionException>(() => provider.TranscribeAsync(new AudioData([1, 2, 3], TimeSpan.FromSeconds(1), .1f), new("auto"), deadline.Token));
        Assert.AreEqual("redirect", error.Code);
        Assert.AreEqual("POST /v1/audio/transcriptions HTTP/1.1", await server.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
