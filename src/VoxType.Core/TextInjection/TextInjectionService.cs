namespace VoxType.Core.TextInjection;

public sealed class TextInjectionService(IInputTargetPlatform platform, IClipboardService clipboard, TimeProvider? timeProvider = null) : ITextInjectionService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task<InjectionResult> InjectAsync(string text, FocusContext focus, long sessionId, CancellationToken cancellationToken)
    {
        ClipboardReceipt? receipt = null;
        bool committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!platform.IsInteractiveDesktop) return new(InjectionStatus.PendingCopy, "当前桌面不可交互，请解锁后在设置中复制结果。");
            string? reason = platform.ValidateTarget(focus, tryRestore: true);
            if (reason is not null) return await CopyFallback(text, reason, cancellationToken).ConfigureAwait(false);
            long started = clock.GetTimestamp();
            while (platform.AnyPhysicalModifiersDown)
            {
                if (clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(2))
                    return await CopyFallback(text, "快捷键仍被按住。", cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(25), clock, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            reason = platform.ValidateTarget(focus, tryRestore: false);
            if (reason is not null) return await CopyFallback(text, reason, cancellationToken).ConfigureAwait(false);
            var write = await clipboard.WriteAsync(text, captureSnapshot: true, cancellationToken).ConfigureAwait(false);
            if (write.Status != ClipboardWriteStatus.Written || write.Receipt is null) return Pending(write.Status);
            receipt = write.Receipt;
            cancellationToken.ThrowIfCancellationRequested();
            if (!await clipboard.IsOwnedAsync(receipt, cancellationToken).ConfigureAwait(false))
                return new(InjectionStatus.PendingCopy, "剪贴板有新内容或暂时占用，已停止自动输入。请在设置中复制结果。");
            if (!platform.IsInteractiveDesktop) return new(InjectionStatus.CopiedOnly, "桌面状态已改变，文本已复制，请手动粘贴。");
            reason = platform.ValidateTarget(focus, tryRestore: false);
            if (reason is not null || platform.AnyPhysicalModifiersDown)
                return new(InjectionStatus.CopiedOnly, "输入目标或按键状态已改变，文本已复制，请手动粘贴。");
            // Commit point: cancellation cannot roll back events already delivered by SendInput.
            cancellationToken.ThrowIfCancellationRequested();
            uint sent = platform.SendPaste();
            committed = sent == 4;
            if (sent != 4)
            {
                if (sent > 0) platform.ReleaseSyntheticKeys(sent);
                return new(InjectionStatus.CopiedOnly, sent == 0 ? "无法自动输入，文本已复制，请手动粘贴。" : "按键只发送了一部分，文本已复制，请检查目标后手动粘贴；不会重复发送。");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(750), clock, CancellationToken.None).ConfigureAwait(false);
            var restored = await clipboard.RestoreAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            return new(InjectionStatus.Dispatched, restored switch
            {
                ClipboardRestoreStatus.Restored => "文本已发送。",
                ClipboardRestoreStatus.OwnershipChanged => "文本已发送；剪贴板有新内容，已保留。",
                ClipboardRestoreStatus.Unsupported => "文本已发送；原剪贴板含复杂格式，未恢复。",
                _ => "文本已发送；剪贴板暂时占用，未恢复。"
            }, restored == ClipboardRestoreStatus.Restored);
        }
        catch (OperationCanceledException) when (!committed)
        {
            if (receipt is not null) { try { await clipboard.RestoreAsync(receipt, CancellationToken.None).ConfigureAwait(false); } catch { } }
            return new(InjectionStatus.Cancelled, "已取消。");
        }
        catch when (committed) { return new(InjectionStatus.Dispatched, "文本已发送；剪贴板未能恢复，请检查当前内容。"); }
    }
    public async Task<bool> CopyAsync(string text, CancellationToken cancellationToken = default) =>
        !string.IsNullOrWhiteSpace(text) && (await clipboard.WriteAsync(text, false, cancellationToken).ConfigureAwait(false)).Status == ClipboardWriteStatus.Written;
    private async Task<InjectionResult> CopyFallback(string text, string reason, CancellationToken token)
    {
        var write = await clipboard.WriteAsync(text, false, token).ConfigureAwait(false);
        return write.Status == ClipboardWriteStatus.Written ? new(InjectionStatus.CopiedOnly, reason + " 文本已复制，请手动粘贴。") : Pending(write.Status);
    }
    private static InjectionResult Pending(ClipboardWriteStatus status) => new(InjectionStatus.PendingCopy,
        status == ClipboardWriteStatus.Changed ? "剪贴板有新内容，已保留。请在设置中复制结果。" : "剪贴板持续被占用，复制失败。请在设置中复制结果。");
}
