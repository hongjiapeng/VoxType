using System.Text;
using Microsoft.Extensions.Time.Testing;
using VoxType.Core.TextInjection;
using VoxType.Infrastructure.TextInjection;

namespace VoxType.Tests.TextInjection;

[TestClass]
public sealed class ClipboardServiceTests
{
    [TestMethod]
    public async Task ClosePublishesFinalSequence_ReceiptOwnsTextAndRestoresOriginal()
    {
        var native = new Access { CloseSequenceDelta = 3 };
        byte[] original = Encoding.Unicode.GetBytes("原内容\0"); native.Data[13] = original;
        var service = new ClipboardService(native);
        var result = await service.WriteAsync("识别文本", true, default);
        Assert.AreEqual(ClipboardWriteStatus.Written, result.Status);
        Assert.AreEqual(native.Sequence, result.Receipt!.Sequence, "The receipt must include sequence changes published by CloseClipboard.");
        Assert.IsTrue(await service.IsOwnedAsync(result.Receipt, default));
        Assert.AreEqual(ClipboardRestoreStatus.Restored, await service.RestoreAsync(result.Receipt, default));
        CollectionAssert.AreEqual(original, native.Data[13]);
        Assert.IsFalse(native.Open);
    }
    [TestMethod]
    public async Task NewCopyAfterClose_PreservingMarker_StillPreventsRestore()
    {
        var native = new Access { CloseSequenceDelta = 3 }; var service = new ClipboardService(native);
        var result = await service.WriteAsync("识别文本", true, default);
        native.Sequence++;
        byte[] newCopy = Encoding.Unicode.GetBytes("用户新复制\0"); native.Data[13] = newCopy;
        Assert.IsFalse(await service.IsOwnedAsync(result.Receipt!, default));
        Assert.AreEqual(ClipboardRestoreStatus.OwnershipChanged, await service.RestoreAsync(result.Receipt!, default));
        CollectionAssert.AreEqual(newCopy, native.Data[13]);
        Assert.AreEqual(1, native.Writes);
    }
    [TestMethod]
    public async Task OwnershipTemporarilyBusy_RetriesWithoutRewriting_ThenRecognizesReceipt()
    {
        var clock = new FakeTimeProvider(); var native = new Access(); var service = new ClipboardService(native, clock);
        var written = await service.WriteAsync("识别文本", true, default);
        native.Busy = true;
        var owned = service.IsOwnedAsync(written.Receipt!, default);
        Assert.IsFalse(owned.IsCompleted); Assert.AreEqual(2, native.OpenAttempts);
        native.Busy = false; clock.Advance(TimeSpan.FromMilliseconds(25));
        Assert.IsTrue(await owned.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(3, native.OpenAttempts); Assert.AreEqual(1, native.Writes); Assert.IsFalse(native.Open);
    }
    [TestMethod]
    public async Task OwnershipPersistentlyBusy_StopsAtOneSecond_WithoutRewriting()
    {
        var clock = new FakeTimeProvider(); var native = new Access(); var service = new ClipboardService(native, clock);
        var written = await service.WriteAsync("识别文本", true, default);
        native.Busy = true;
        var owned = service.IsOwnedAsync(written.Receipt!, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsFalse(await owned.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(3, native.OpenAttempts); Assert.AreEqual(1, native.Writes); Assert.IsFalse(native.Open);
    }
    [TestMethod]
    public async Task PreviousVoxTypeText_RestoresAllFormats_WithoutRevivingOldReceipt()
    {
        var native = new Access { CloseSequenceDelta = 3 }; var service = new ClipboardService(native);
        var previous = await service.WriteAsync("之前复制的识别文本", false, default);
        var original = native.Data.ToDictionary(x => x.Key, x => x.Value.ToArray());
        var current = await service.WriteAsync("新识别文本", true, default);
        Assert.IsTrue(current.Receipt!.Snapshot.CanRestore);
        Assert.AreEqual(ClipboardRestoreStatus.Restored, await service.RestoreAsync(current.Receipt, default));
        Assert.HasCount(original.Count, native.Data);
        foreach (var item in original) CollectionAssert.AreEqual(item.Value, native.Data[item.Key]);
        Assert.IsFalse(await service.IsOwnedAsync(previous.Receipt!, default));
        Assert.IsFalse(await service.IsOwnedAsync(current.Receipt, default));
        Assert.AreEqual(3, native.Writes);
    }
    [TestMethod]
    [DataRow("empty")]
    [DataRow("text")]
    [DataRow("html-rtf")]
    public async Task SupportedSnapshot_RoundTripsExactFormats_ExcludesHistoryAndCloud(string kind)
    {
        var native = new Access();
        if (kind != "empty") native.Data[13] = Encoding.Unicode.GetBytes("原文字\0");
        if (kind == "html-rtf") { native.Data[native.Register("HTML Format")] = Encoding.UTF8.GetBytes("<p>original</p>\0"); native.Data[native.Register("Rich Text Format")] = Encoding.ASCII.GetBytes("{\\rtf1 original}\0"); }
        var original = native.Data.ToDictionary(x => x.Key, x => x.Value.ToArray());
        var service = new ClipboardService(native);
        var result = await service.WriteAsync("新文本\n第二行", true, default);
        Assert.AreEqual(ClipboardWriteStatus.Written, result.Status);
        Assert.IsNotNull(result.Receipt); Assert.IsTrue(result.Receipt.Snapshot.CanRestore);
        CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("新文本\n第二行\0"), native.Data[13]);
        CollectionAssert.AreEqual(BitConverter.GetBytes(0u), native.Data[native.Register("CanIncludeInClipboardHistory")]);
        CollectionAssert.AreEqual(BitConverter.GetBytes(0u), native.Data[native.Register("CanUploadToCloudClipboard")]);
        Assert.IsTrue(await service.IsOwnedAsync(result.Receipt, default));
        Assert.AreEqual(ClipboardRestoreStatus.Restored, await service.RestoreAsync(result.Receipt, default));
        Assert.HasCount(original.Count, native.Data);
        foreach (var item in original) CollectionAssert.AreEqual(item.Value, native.Data[item.Key]);
        Assert.IsFalse(native.Open);
    }
    [TestMethod]
    [DataRow(2u)]
    [DataRow(0xC999u)]
    public async Task ComplexFormat_DoesNotPretendToRestore(uint format)
    {
        var native = new Access(); native.Data[format] = [1, 2, 3];
        var service = new ClipboardService(native);
        var result = await service.WriteAsync("新文本", true, default);
        Assert.AreEqual(ClipboardWriteStatus.Written, result.Status);
        Assert.IsFalse(result.Receipt!.Snapshot.CanRestore);
        Assert.AreEqual(ClipboardRestoreStatus.Unsupported, await service.RestoreAsync(result.Receipt, default));
        Assert.AreEqual(1, native.Writes);
    }
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task NewCopyOrLostMarker_DoesNotRestoreOrRewrite(bool changeSequence)
    {
        var native = new Access(); var service = new ClipboardService(native);
        var result = await service.WriteAsync("识别文本", true, default);
        native.Data.Clear(); native.Data[13] = Encoding.Unicode.GetBytes("用户新复制\0");
        if (changeSequence) native.Sequence++;
        Assert.IsFalse(await service.IsOwnedAsync(result.Receipt!, default));
        Assert.AreEqual(ClipboardRestoreStatus.OwnershipChanged, await service.RestoreAsync(result.Receipt!, default));
        Assert.AreEqual(1, native.Writes);
        CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("用户新复制\0"), native.Data[13]);
    }
    [TestMethod]
    public async Task LaterSessionOwnership_PreventsEarlierRestore()
    {
        var native = new Access(); var service = new ClipboardService(native);
        var first = await service.WriteAsync("第一次", true, default);
        var second = await service.WriteAsync("第二次", true, default);
        Assert.AreEqual(ClipboardRestoreStatus.OwnershipChanged, await service.RestoreAsync(first.Receipt!, default));
        Assert.IsTrue(await service.IsOwnedAsync(second.Receipt!, default));
        Assert.AreEqual(2, native.Writes);
    }
    [TestMethod]
    public async Task BusyClipboard_RetriesWithinOneSecond_ThenStops()
    {
        var clock = new FakeTimeProvider(); var native = new Access { Busy = true }; var service = new ClipboardService(native, clock);
        var task = service.WriteAsync("文本", true, default);
        Assert.AreEqual(1, native.OpenAttempts);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(ClipboardWriteStatus.Unavailable, (await task.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.AreEqual(2, native.OpenAttempts); Assert.AreEqual(0, native.Writes);
    }
    [TestMethod]
    public async Task UserCopyDuringBusyRetry_IsPreserved()
    {
        var clock = new FakeTimeProvider(); var native = new Access { Busy = true }; var service = new ClipboardService(native, clock);
        var task = service.WriteAsync("文本", true, default);
        native.Sequence++; native.Busy = false; native.Data[13] = Encoding.Unicode.GetBytes("新复制\0");
        clock.Advance(TimeSpan.FromMilliseconds(25));
        Assert.AreEqual(ClipboardWriteStatus.Changed, (await task.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.AreEqual(0, native.Writes); Assert.IsFalse(native.Open);
    }
    [TestMethod]
    public async Task CancellationDuringRetry_DoesNotWrite()
    {
        using var cancellation = new CancellationTokenSource(); var native = new Access { Busy = true };
        var task = new ClipboardService(native).WriteAsync("文本", true, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.AreEqual(0, native.Writes); Assert.IsFalse(native.Open);
    }
    [TestMethod]
    public async Task FailedReplace_RollsBackSupportedSnapshotUnderSameOpen()
    {
        var native = new Access { FailNextReplace = true }; native.Data[13] = Encoding.Unicode.GetBytes("原内容\0");
        var result = await new ClipboardService(native).WriteAsync("新文本", true, default);
        Assert.AreEqual(ClipboardWriteStatus.Unavailable, result.Status);
        CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("原内容\0"), native.Data[13]);
        Assert.AreEqual(2, native.Writes); Assert.IsFalse(native.Open);
    }
    private sealed class Access : IClipboardAccess, IClipboardExecutor
    {
        private readonly Dictionary<string, uint> names = new();
        public Dictionary<uint, byte[]> Data { get; } = new(); public uint Sequence { get; set; } = 1;
        public bool Busy, Open, FailNextReplace; public int OpenAttempts, Writes; public uint CloseSequenceDelta;
        private bool changed;
        public Task<T> ExecuteAsync<T>(Func<IClipboardAccess, T> operation, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(operation(this)); }
        public bool TryOpen() { OpenAttempts++; if (Busy) return false; Assert.IsFalse(Open); return Open = true; }
        public void Close() { Assert.IsTrue(Open); Open = false; if (changed) Sequence += CloseSequenceDelta; changed = false; }
        public IReadOnlyList<uint> Formats() => Data.Keys.ToArray();
        public byte[]? Read(uint format) => Data.TryGetValue(format, out var value) ? value.ToArray() : null;
        public uint Register(string name) { if (!names.TryGetValue(name, out uint value)) names[name] = value = (uint)(0xC000 + names.Count); return value; }
        public void Replace(IReadOnlyList<ClipboardFormatData> formats)
        {
            Assert.IsTrue(Open); Writes++; Data.Clear(); Sequence++; changed = true;
            if (FailNextReplace) { FailNextReplace = false; throw new IOException("allocation failure"); }
            foreach (var format in formats) Data[format.Format] = format.Data.ToArray();
        }
    }
}
