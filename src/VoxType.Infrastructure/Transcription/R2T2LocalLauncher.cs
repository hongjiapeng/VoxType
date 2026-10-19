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

}
