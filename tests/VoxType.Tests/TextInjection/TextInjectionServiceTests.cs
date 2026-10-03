using Microsoft.Extensions.Time.Testing;
using VoxType.Core.TextInjection;

namespace VoxType.Tests.TextInjection;

[TestClass]
public sealed class TextInjectionServiceTests
{
    private static readonly FocusContext Focus = new(10, 20, 30, 40, 50);
    [TestMethod]
    public async Task SuccessfulSend_RestoresAfter750Milliseconds()
    {
        var clock = new FakeTimeProvider(); var clipboard = new Clipboard(); var platform = new Platform();
        var task = new TextInjectionService(platform, clipboard, clock).InjectAsync("你好\n第二行", Focus, 1, default);
        Assert.AreEqual(1, platform.SendCalls);
        Assert.AreEqual("你好\n第二行", clipboard.Text);
        Assert.AreEqual(0, clipboard.RestoreCalls);
        clock.Advance(TimeSpan.FromMilliseconds(749)); Assert.IsFalse(task.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(InjectionStatus.Dispatched, result.Status);
        Assert.IsTrue(result.ClipboardRestored);
        Assert.AreEqual(1, clipboard.RestoreCalls);
        Assert.AreEqual(3, platform.ValidationCalls);
    }
    [TestMethod]
    [DataRow(0u)]
    [DataRow(1u)]
    [DataRow(2u)]
    [DataRow(3u)]
    public async Task FailedOrPartialSend_DoesNotRetryText_OnlyCleansSyntheticKeys(uint sent)
    {
        var platform = new Platform { Sent = sent }; var clipboard = new Clipboard();
        var result = await new TextInjectionService(platform, clipboard).InjectAsync("保留文本", Focus, 1, default);
        Assert.AreEqual(InjectionStatus.CopiedOnly, result.Status);
        Assert.AreEqual(1, platform.SendCalls);
        Assert.AreEqual(sent == 0 ? 0 : 1, platform.CleanupCalls);
        Assert.AreEqual(sent, platform.CleanedCount);
        Assert.AreEqual(0, clipboard.RestoreCalls);
        Assert.AreEqual("保留文本", clipboard.Text);
    }
    [TestMethod]
    public async Task SecureDesktop_SendsAndCopiesNothing()
    {
        var platform = new Platform { IsInteractiveDesktop = false }; var clipboard = new Clipboard();
        var result = await new TextInjectionService(platform, clipboard).InjectAsync("文本", Focus, 1, default);
        Assert.AreEqual(InjectionStatus.PendingCopy, result.Status);
        Assert.AreEqual(0, platform.SendCalls); Assert.AreEqual(0, clipboard.WriteCalls);
    }
    [TestMethod]
    [DataRow(ClipboardWriteStatus.Written, InjectionStatus.CopiedOnly)]
    [DataRow(ClipboardWriteStatus.Unavailable, InjectionStatus.PendingCopy)]
    [DataRow(ClipboardWriteStatus.Changed, InjectionStatus.PendingCopy)]
    public async Task UnsafeTarget_ReturnsAccurateCopyOutcome(ClipboardWriteStatus write, InjectionStatus expected)
    {
        var platform = new Platform { RefuseAt = 1 }; var clipboard = new Clipboard { WriteStatus = write };
        var result = await new TextInjectionService(platform, clipboard).InjectAsync("文本", Focus, 1, default);
        Assert.AreEqual(expected, result.Status); Assert.AreEqual(0, platform.SendCalls);
        Assert.AreEqual(1, clipboard.WriteCalls); Assert.IsFalse(clipboard.Captured);
    }
    [TestMethod]
    public async Task ModifiersHeldAtTwoSeconds_CopiesWithoutSendingOrReleasingUserKeys()
    {
        var clock = new FakeTimeProvider(); var clipboard = new Clipboard(); var platform = new Platform { AnyPhysicalModifiersDown = true };
        var task = new TextInjectionService(platform, clipboard, clock).InjectAsync("文本", Focus, 1, default);
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(InjectionStatus.CopiedOnly, result.Status);
        Assert.AreEqual(0, platform.SendCalls); Assert.AreEqual(0, platform.CleanupCalls);
    }
    [TestMethod]
    public async Task ClipboardChangedBeforeCommit_SendsNothingAndPreservesNewCopy()
    {
        var platform = new Platform(); var clipboard = new Clipboard { Owned = false };
        var result = await new TextInjectionService(platform, clipboard).InjectAsync("文本", Focus, 1, default);
        Assert.AreEqual(InjectionStatus.PendingCopy, result.Status);
        Assert.AreEqual(0, platform.SendCalls); Assert.AreEqual(0, clipboard.RestoreCalls);
    }
    [TestMethod]
    public async Task FocusChangesAfterClipboardWrite_LeavesTextForManualPaste()
    {
        var platform = new Platform { RefuseAt = 3 }; var clipboard = new Clipboard();
        var result = await new TextInjectionService(platform, clipboard).InjectAsync("文本", Focus, 1, default);
        Assert.AreEqual(InjectionStatus.CopiedOnly, result.Status);
        Assert.AreEqual(0, platform.SendCalls); Assert.AreEqual(0, clipboard.RestoreCalls);
    }
    [TestMethod]
    public async Task CancellationBeforeCommit_RestoresOwnedClipboardAndDoesNotSend()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform(); var clipboard = new Clipboard { AfterWrite = cancellation.Cancel };
        var result = await new TextInjectionService(platform, clipboard).InjectAsync("文本", Focus, 1, cancellation.Token);
        Assert.AreEqual(InjectionStatus.Cancelled, result.Status);
        Assert.AreEqual(0, platform.SendCalls); Assert.AreEqual(1, clipboard.RestoreCalls);
    }
    [TestMethod]
    public async Task CancellationAfterCommit_DoesNotClaimRollback()
    {
        using var cancellation = new CancellationTokenSource(); var clock = new FakeTimeProvider();
        var platform = new Platform { AfterSend = cancellation.Cancel }; var clipboard = new Clipboard();
        var task = new TextInjectionService(platform, clipboard, clock).InjectAsync("文本", Focus, 1, cancellation.Token);
        clock.Advance(TimeSpan.FromMilliseconds(750));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(InjectionStatus.Dispatched, result.Status); Assert.AreEqual(1, platform.SendCalls);
    }
    [TestMethod]
    [DataRow(ClipboardRestoreStatus.Unsupported, "复杂格式")]
    [DataRow(ClipboardRestoreStatus.OwnershipChanged, "新内容")]
    [DataRow(ClipboardRestoreStatus.Unavailable, "占用")]
    public async Task RestoreOutcome_IsReflectedInResult(ClipboardRestoreStatus status, string message)
    {
        var clock = new FakeTimeProvider(); var clipboard = new Clipboard { RestoreStatus = status };
        var task = new TextInjectionService(new Platform(), clipboard, clock).InjectAsync("文本", Focus, 1, default);
        clock.Advance(TimeSpan.FromMilliseconds(750));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(InjectionStatus.Dispatched, result.Status); Assert.IsFalse(result.ClipboardRestored); Assert.Contains(message, result.Message);
    }
    [TestMethod]
    public async Task RestoreThrowsAfterCommit_StillReportsDispatchedWithoutRetry()
    {
        var clock = new FakeTimeProvider(); var platform = new Platform(); var clipboard = new Clipboard { ThrowOnRestore = true };
        var task = new TextInjectionService(platform, clipboard, clock).InjectAsync("文本", Focus, 1, default);
        clock.Advance(TimeSpan.FromMilliseconds(750));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(InjectionStatus.Dispatched, result.Status); Assert.IsFalse(result.ClipboardRestored);
        Assert.AreEqual(1, platform.SendCalls); Assert.Contains("未能恢复", result.Message);
    }
    private sealed class Platform : IInputTargetPlatform
    {
        public bool IsInteractiveDesktop { get; set; } = true; public bool AnyPhysicalModifiersDown { get; set; }
        public uint Sent { get; set; } = 4; public int SendCalls, CleanupCalls, ValidationCalls; public uint CleanedCount;
        public int RefuseAt { get; set; } public Action? AfterSend { get; set; }
        public string? ValidateTarget(FocusContext target, bool tryRestore) => ++ValidationCalls == RefuseAt ? "目标不可用。" : null;
        public uint SendPaste() { SendCalls++; AfterSend?.Invoke(); return Sent; }
        public void ReleaseSyntheticKeys(uint sentCount) { CleanupCalls++; CleanedCount = sentCount; }
    }
    private sealed class Clipboard : IClipboardService
    {
        public ClipboardWriteStatus WriteStatus { get; set; } = ClipboardWriteStatus.Written;
        public ClipboardRestoreStatus RestoreStatus { get; set; } = ClipboardRestoreStatus.Restored;
        public bool Owned { get; set; } = true; public Action? AfterWrite { get; set; }
        public bool ThrowOnRestore { get; set; }
        public int WriteCalls, RestoreCalls; public string Text = ""; public bool Captured;
        public Task<ClipboardWriteResult> WriteAsync(string text, bool captureSnapshot, CancellationToken cancellationToken)
        { WriteCalls++; Text = text; Captured = captureSnapshot; AfterWrite?.Invoke(); return Task.FromResult(new ClipboardWriteResult(WriteStatus, new(1, Guid.NewGuid(), new(true, [])))); }
        public Task<ClipboardRestoreStatus> RestoreAsync(ClipboardReceipt receipt, CancellationToken cancellationToken) { RestoreCalls++; if (ThrowOnRestore) throw new IOException("clipboard unavailable"); return Task.FromResult(RestoreStatus); }
        public Task<bool> IsOwnedAsync(ClipboardReceipt receipt, CancellationToken cancellationToken) => Task.FromResult(Owned);
    }
}
