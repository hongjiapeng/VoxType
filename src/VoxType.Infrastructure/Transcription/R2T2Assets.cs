using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace VoxType.Infrastructure.Transcription;

public sealed record R2T2Asset(string Name, long Size, string Sha256);
public sealed record R2T2AssetManifest(string ModelRepository, string ModelRevision, R2T2Asset[] Models,
    string RuntimeVersion, Dictionary<string, R2T2Asset> Runtimes)
{
    public static R2T2AssetManifest Load()
    {
        using var stream = typeof(R2T2AssetManifest).Assembly.GetManifestResourceStream("VoxType.R2T2.Assets.json")!;
        return JsonSerializer.Deserialize<R2T2AssetManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}

public static class R2T2Assets
{
    public static async Task<bool> VerifyAsync(string path, R2T2Asset asset, bool gguf, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size) return false;
        await using var stream = File.OpenRead(path);
        if (gguf)
        {
            var header = new byte[4];
            if (await stream.ReadAsync(header, token) != 4 || !header.AsSpan().SequenceEqual("GGUF"u8)) return false;
            stream.Position = 0;
        }
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task DownloadAsync(HttpClient client, Uri url, string destination, R2T2Asset asset,
        bool gguf, IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report($"正在校验 {asset.Name}…");
        if (await VerifyAsync(destination, asset, gguf, token)) return;
        if (File.Exists(destination)) throw new IOException($"文件校验失败，请移走后重新下载：{destination}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = File.Create(temporary))
            {
                var buffer = new byte[128 * 1024];
                long total = 0, lastReport = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > asset.Size) throw new IOException("下载文件超过预期大小，请重试。");
                    await target.WriteAsync(buffer.AsMemory(0, count), token);
                    if (Environment.TickCount64 - lastReport >= 250)
                    {
                        progress?.Report($"正在下载 {asset.Name}：{total * 100d / asset.Size:F0}%（{total / 1048576d:F0} / {asset.Size / 1048576d:F0} MiB）");
                        lastReport = Environment.TickCount64;
                    }
                }
            }
            progress?.Report($"正在校验 {asset.Name}…");
            if (!await VerifyAsync(temporary, asset, gguf, token)) throw new IOException($"下载文件完整性校验失败：{asset.Name}，请重试。");
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task ExpandRuntimeAsync(string archive, string destination, CancellationToken token)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repairs = new List<(ZipArchiveEntry Entry, string Target)>();
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0) continue;
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !targets.Add(target))
                throw new IOException("运行时压缩包包含无效或重复路径。");
            await using var source = entry.Open();
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
            if (await VerifyAsync(target, new(entry.Name, entry.Length, hash), false, token)) continue;
            repairs.Add((entry, target));
        }
        if (!targets.Contains(Path.Combine(root, "llama-server.exe"))) throw new IOException("运行时压缩包缺少 llama-server.exe。");
        // Preflight every replacement before changing any file. Valid loaded files are reused.
        foreach (var repair in repairs)
            if (File.Exists(repair.Target))
            {
                try { using var probe = File.Open(repair.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex) { throw new IOException("运行时文件正在使用，请先停止本机服务后重试。", ex); }
            }
        foreach (var repair in repairs)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(repair.Target)!);
            string temporary = repair.Target + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var source = repair.Entry.Open())
                await using (var target = File.Create(temporary)) await source.CopyToAsync(target, token);
                File.Move(temporary, repair.Target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
