using VoxType.Infrastructure.Transcription;

namespace VoxType.Tests.Transcription;

[TestClass]
public sealed class R2T2LocalLauncherTests
{
    [TestMethod]
    public void ResolveDataDirectory_RequiresAbsolutePathAndPreservesSpaces()
    {
        Assert.AreEqual(@"D:\R2T2 Models", R2T2LocalLauncher.ResolveDataDirectory(@" D:\R2T2 Models ", AppContext.BaseDirectory));
        Assert.ThrowsExactly<ArgumentException>(() => R2T2LocalLauncher.ResolveDataDirectory("relative/path", AppContext.BaseDirectory));
    }
}
