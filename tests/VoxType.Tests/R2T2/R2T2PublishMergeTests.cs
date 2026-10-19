using System.Diagnostics;

namespace VoxType.Tests.R2T2;

[TestClass]
public sealed class R2T2PublishMergeTests
{
    [TestMethod]
    [DataRow("identical", 0)]
    [DataRow("conflict", 1)]
    [DataRow("missing-adapter", 1)]
    public async Task PublishMerge_ReusesIdenticalRuntimeAndRejectsConflictsBeforeCopying(string scenario, int expectedExit)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts", "merge-r2t2-publish.ps1"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        string root = Path.Combine(Path.GetTempPath(), "VoxType publish merge " + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "adapter"), target = Path.Combine(root, "app");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        try
        {
            if (scenario != "missing-adapter") await File.WriteAllTextAsync(Path.Combine(source, "R2T2.Server.exe"), "adapter");
            await File.WriteAllTextAsync(Path.Combine(source, "coreclr.dll"), scenario == "conflict" ? "runtime-other" : "runtime-v1");
            await File.WriteAllTextAsync(Path.Combine(source, "Microsoft.AspNetCore.dll"), "aspnet");
            await File.WriteAllTextAsync(Path.Combine(target, "coreclr.dll"), "runtime-v1");
            await File.WriteAllTextAsync(Path.Combine(target, "VoxType.App.exe"), "desktop");
            var stamp = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(target, "coreclr.dll"), stamp);
            using var held = File.Open(Path.Combine(target, "coreclr.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(repository.FullName, "scripts", "merge-r2t2-publish.ps1"), "-SourceDirectory", source, "-PublishDirectory", target }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            Assert.AreEqual(expectedExit, process.ExitCode, await output + await error);
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(Path.Combine(target, "coreclr.dll")));
            Assert.AreEqual("runtime-v1", await File.ReadAllTextAsync(Path.Combine(target, "coreclr.dll")));
            Assert.AreEqual("desktop", await File.ReadAllTextAsync(Path.Combine(target, "VoxType.App.exe")));
            Assert.AreEqual(expectedExit == 0, File.Exists(Path.Combine(target, "R2T2.Server.exe")));
            Assert.AreEqual(expectedExit == 0, File.Exists(Path.Combine(target, "Microsoft.AspNetCore.dll")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
