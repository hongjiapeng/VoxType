using VoxType.Infrastructure.Transcription;

namespace VoxType.Tests.Transcription;

[TestClass]
public sealed class R2T2LocalLauncherTests
{
    [TestMethod]
    public void CreateStartInfo_UsesVisiblePowerShellAndPassesBackendAndDirectoryAsSeparateArguments()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoxType-r2t2-launch-" + Guid.NewGuid().ToString("N"));
        string scripts = Path.Combine(root, "scripts");
        Directory.CreateDirectory(scripts);
        string prepare = Path.Combine(scripts, "prepare-r2t2.ps1"), start = Path.Combine(scripts, "start-r2t2.ps1");
        try
        {
            File.WriteAllText(prepare, ""); File.WriteAllText(start, "");
            var gpu = R2T2LocalLauncher.CreateStartInfo(root, "vulkan", @"D:\R2T2 Models", prepare: false);
            Assert.AreEqual("pwsh.exe", gpu.FileName);
            Assert.IsTrue(gpu.UseShellExecute);
            Assert.AreEqual(System.Diagnostics.ProcessWindowStyle.Normal, gpu.WindowStyle);
            Assert.AreEqual(@"D:\R2T2 Models", gpu.WorkingDirectory);
            CollectionAssert.AreEqual(new[] { "-NoProfile", "-NoExit", "-File", start, "-Backend", "Vulkan", "-DataDirectory", @"D:\R2T2 Models" }, gpu.ArgumentList.ToArray());
            var cpu = R2T2LocalLauncher.CreateStartInfo(root, "cpu", @"D:\R2T2 Models", prepare: true);
            Assert.AreEqual(prepare, cpu.ArgumentList[3]);
            Assert.AreEqual("Cpu", cpu.ArgumentList[5]);
            Assert.ThrowsExactly<ArgumentException>(() => R2T2LocalLauncher.CreateStartInfo(root, "cuda", @"D:\R2T2 Models", prepare: false));
        }
        finally
        {
            File.Delete(prepare); File.Delete(start);
            Directory.Delete(scripts); Directory.Delete(root);
        }
    }
}
