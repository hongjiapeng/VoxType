using System.Diagnostics;

namespace VoxType.Tests.R2T2;

[TestClass]
public sealed class R2T2StandaloneBuildTests
{
    [TestMethod]
    public async Task Adapter_BuildsWithoutRepositoryDirectoryBuildProps()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "services", "R2T2.Server", "R2T2.Server.csproj"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        string root = Path.Combine(Path.GetTempPath(), "VoxType isolated service " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string project in new[] { "services/R2T2.Server", "src/VoxType.R2T2.Core" })
            {
                string destination = Path.Combine(root, project);
                Directory.CreateDirectory(destination);
                foreach (string file in Directory.GetFiles(Path.Combine(repository.FullName, project)))
                    if (Path.GetExtension(file) is ".cs" or ".csproj") File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "build", Path.Combine(root, "services", "R2T2.Server", "R2T2.Server.csproj"), "-c", "Release", "--nologo" }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1)); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            Assert.AreEqual(0, process.ExitCode, await output + await error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
