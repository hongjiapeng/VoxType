using System.Buffers.Binary;
using VoxType.Infrastructure.Windows;

namespace VoxType.Tests.TextInjection;

[TestClass]
public sealed class NativeDesktopTests
{
    [TestMethod]
    [DataRow(1, 0, 1, 20, true)]
    [DataRow(1, 0, 0, 20, false)]
    [DataRow(1, 0, -1, 20, false)]
    [DataRow(1, 4, 1, 20, false)]
    [DataRow(2, 0, 1, 20, false)]
    [DataRow(1, 0, 1, 19, false)]
    public void SessionHeader_OnlyKnownActiveUnlockedStateIsAccepted(int level, int state, int flags, int length, bool expected)
    {
        byte[] header = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(header, level);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), state);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), flags);
        Assert.AreEqual(expected, NativeDesktop.IsUnlockedSessionHeader(header.AsSpan(0, length)));
    }
}
