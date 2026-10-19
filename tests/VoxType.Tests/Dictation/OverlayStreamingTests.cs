using VoxType.Core.Dictation;
using VoxType_App.ViewModels;

namespace VoxType.Tests.Dictation;

[TestClass]
public sealed class OverlayStreamingTests
{
    [TestMethod]
    public void StreamingPreview_DisplaysRecognizedTextAndClearsItOnNextState()
    {
        var model = new OverlayViewModel();
        var processing = new SessionUpdate(1, SessionState.Processing, "R2T2", "R2T2", false, new(123, 456, 789, 124, 1000), PartialText: "正在出现的文字");
        model.Update(processing);
        Assert.AreEqual("识别中…", model.Title);
        Assert.AreEqual("正在出现的文字", model.FullDetail);
        Assert.IsTrue(model.IsProcessing);
        Assert.IsTrue(model.IsStreamingPreview);
        model.Update(processing with { State = SessionState.Cancelling, Message = "已取消" });
        Assert.AreEqual("已取消", model.FullDetail);
        Assert.IsFalse(model.IsStreamingPreview);
        model.Update(processing with { SessionId = 2, PartialText = null });
        Assert.AreEqual("识别中…", model.Title);
        Assert.IsFalse(model.IsStreamingPreview);
    }

    [TestMethod]
    [DataRow("😀", DisplayName = "SimpleEmoji")]
    [DataRow("👩‍💻", DisplayName = "JoinedEmoji")]
    [DataRow("e\u0301", DisplayName = "CombiningAccent")]
    [DataRow("🇨🇳", DisplayName = "FlagEmoji")]
    public void StreamingPreview_LongTextKeepsNewestGraphemes(string grapheme)
    {
        var model = new OverlayViewModel();
        string text = new string('前', 10) + grapheme + new string('后', 19);
        model.Update(new(1, SessionState.Listening, "", "R2T2", false, new(123, 456, 789, 124, 1000), PartialText: text));
        Assert.AreEqual("…" + grapheme + new string('后', 19), model.Title);
        Assert.AreEqual(text, model.FullDetail);
    }

    [TestMethod]
    public void LivePreview_KeepsListeningWaveform_AndCollapsesDetailDuringDelivery()
    {
        var model = new OverlayViewModel();
        var listening = new SessionUpdate(1, SessionState.Listening, "", "R2T2", false, new(123, 456, 789, 124, 1000), PartialText: "说话中已经出现的文字");
        model.Update(listening);
        Assert.IsTrue(model.IsListening);
        Assert.IsFalse(model.IsProcessing);
        Assert.IsTrue(model.IsStreamingPreview);
        Assert.AreEqual("说话中已经出现的文字", model.Title);
        model.Update(listening with { State = SessionState.Processing, PartialText = "校正后的完整文字。" });
        model.Update(listening with { State = SessionState.Injecting, PartialText = null });
        Assert.AreEqual("正在发送文本", model.Title);
        Assert.IsFalse(model.IsStreamingPreview);
        Assert.AreEqual("校正后的完整文字。", model.FullDetail);
        model.Update(listening with { State = SessionState.Completed, PartialText = null });
        Assert.AreEqual("已发送文本", model.Title);
        Assert.AreEqual("校正后的完整文字。", model.FullDetail);
        model.Update(listening with { SessionId = 2, PartialText = null });
        Assert.IsFalse(model.IsStreamingPreview);
        Assert.AreEqual("松开结束", model.Title);
        Assert.AreEqual("正在聆听 · 松开结束 · Esc 取消", model.FullDetail);
    }

    [TestMethod]
    public void TrayPreview_ReservesSpaceForStopButtonAndKeepsNewestPhrase()
    {
        var model = new OverlayViewModel();
        model.Update(new(1, SessionState.Listening, "", "R2T2", false, new(123, 456, 789, 124, 1000),
            FromTray: true, PartialText: "这是一段较长的实时预览文字，需要保留最新的一小段内容"));
        Assert.IsTrue(model.ShowStop);
        Assert.AreEqual("…字，需要保留最新的一小段内容", model.Title);
        Assert.IsTrue(model.ShowMore);
        Assert.AreEqual("这是一段较长的实时预览文字，需要保留最新的一小段内容", model.FullDetail);
    }

    [TestMethod]
    [DataRow("没有检测到足够音量，请检查麦克风并重试。", "没有听到声音", DisplayName = "NoAudio")]
    [DataRow("录音太短，请按住快捷键多说一会儿。", "录音太短", DisplayName = "ShortRecording")]
    [DataRow("无法将识别结果发送到当前应用。文本已复制到剪贴板，请返回输入框后手动粘贴。", "请手动粘贴", DisplayName = "ManualPaste")]
    [DataRow("识别服务连接失败，请检查网络设置。", "连接失败", DisplayName = "ConnectionFailure")]
    [DataRow("识别请求被拒绝，请检查服务器地址和模型配置。", "请求被拒绝", DisplayName = "RejectedRequest")]
    [DataRow("未知服务异常：这是一段很长的错误描述，不应该把浮窗撑成横幅。", "输入未完成", DisplayName = "UnknownFailure")]
    public void Failure_KeepsRecoveryActionAndFullReason(string message, string title)
    {
        var model = new OverlayViewModel();
        model.Update(new(1, SessionState.Failed, message, "R2T2", false, new(123, 456, 789, 124, 1000)));
        Assert.AreEqual(title, model.Title);
        Assert.AreEqual(message, model.FullDetail);
        Assert.IsFalse(model.IsStreamingPreview);
        Assert.IsTrue(model.ShowMore);
        Assert.IsTrue(model.ShowRetry);
        Assert.IsFalse(model.ShowStop);
    }

    [TestMethod]
    public void ShortcutRecording_HidesActionsAndUsesSingleLine()
    {
        var model = new OverlayViewModel();
        model.Update(new(1, SessionState.Listening, "", "Whisper", true, new(123, 0, 0, 0, null)));
        Assert.AreEqual("松开结束", model.Title);
        Assert.IsFalse(model.ShowStop);
        Assert.IsFalse(model.ShowMore);
        Assert.IsFalse(model.ShowActions);
        Assert.IsFalse(model.DelayProcessing);
    }

    [TestMethod]
    public void TriggerOrigin_SwitchesFromClickToShortcutWithoutLeavingActions()
    {
        var model = new OverlayViewModel();
        var update = new SessionUpdate(1, SessionState.Listening, "", "Whisper", true, new(123, 0, 0, 0, null), FromTray: true);
        model.Update(update);
        Assert.AreEqual("开始说话…", model.Title);
        Assert.IsTrue(model.ShowStop);
        Assert.IsTrue(model.ShowMore);
        Assert.IsTrue(model.ShowActions);
        model.Update(update with { SessionId = 2, FromTray = false });
        Assert.IsFalse(model.ShowStop);
        Assert.IsFalse(model.ShowMore);
        Assert.IsFalse(model.ShowActions);
    }

    [TestMethod]
    [DataRow(SessionState.Stopping)]
    [DataRow(SessionState.Processing)]
    public void Processing_DelaysShortcutFeedbackAndKeepsClickControls(SessionState state)
    {
        var model = new OverlayViewModel();
        var update = new SessionUpdate(1, state, "", "R2T2", true, new(123, 0, 0, 0, null), PartialText: "临时文字");
        model.Update(update);
        Assert.IsTrue(model.DelayProcessing);
        Assert.IsFalse(model.ShowActions);
        Assert.IsFalse(model.ShowStop);
        Assert.AreEqual("识别中…", model.Title);
        model.Update(update with { FromTray = true });
        Assert.IsFalse(model.DelayProcessing);
        Assert.IsTrue(model.ShowMore);
        Assert.IsFalse(model.ShowStop);
    }
}
