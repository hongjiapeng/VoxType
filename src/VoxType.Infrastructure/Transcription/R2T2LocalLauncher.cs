using System.ComponentModel;
using System.Diagnostics;

namespace VoxType.Infrastructure.Transcription;

public static class R2T2LocalLauncher
{
    public static string DefaultDataDirectory
    {
        get
        {
            string root;
            try { root = global::Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path; }
            catch { root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
            return Path.Combine(root, "VoxType", "r2t2");
        }
    }

    public static string GetDefaultDataDirectory(string appDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(appDirectory));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "r2t2-data");
            if (Directory.Exists(Path.Combine(candidate, "models"))) return candidate;
            current = current.Parent;
        }
        return DefaultDataDirectory;
    }

    public static string ResolveDataDirectory(string configured, string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured)) return GetDefaultDataDirectory(appDirectory);
        var path = configured.Trim();
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("R2T2 数据目录需要填写完整路径。");
        try { return Path.GetFullPath(path); }
        catch { throw new ArgumentException("R2T2 数据目录无效。"); }
    }

    public static ProcessStartInfo CreateStartInfo(string appDirectory, string backend, string dataDirectory, bool prepare)
    {
        if (backend is not ("cpu" or "vulkan")) throw new ArgumentException("R2T2 推理设备无效。");
        var script = Path.Combine(appDirectory, "scripts", prepare ? "prepare-r2t2.ps1" : "start-r2t2.ps1");
        if (!File.Exists(script)) throw new ArgumentException("找不到 R2T2 本机脚本，请修复 VoxType 安装。");
        var resolvedDataDirectory = ResolveDataDirectory(dataDirectory, appDirectory);
        var info = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = resolvedDataDirectory
        };
        foreach (var argument in new[] { "-NoProfile", "-NoExit", "-File", script, "-Backend", backend == "cpu" ? "Cpu" : "Vulkan", "-DataDirectory", resolvedDataDirectory })
            info.ArgumentList.Add(argument);
        return info;
    }

    public static Task LaunchAsync(string appDirectory, string backend, string dataDirectory, bool prepare)
    {
        var info = CreateStartInfo(appDirectory, backend, dataDirectory, prepare);
        try
        {
            Directory.CreateDirectory(info.WorkingDirectory);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell 7 未能启动。");
        }
        catch (Win32Exception) { throw new ArgumentException("无法打开 PowerShell 7。请先安装 pwsh。"); }
        return Task.CompletedTask;
    }
}
