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
        Assert.AreEqual("正在出现的文字", model.Detail);
        Assert.AreEqual("正在识别", model.Title);
        Assert.AreEqual("正在出现的文字", model.FullDetail);
        Assert.IsTrue(model.IsProcessing);
        Assert.IsTrue(model.IsStreamingPreview);
        model.Update(processing with { State = SessionState.Cancelling, Message = "已取消" });
        Assert.AreEqual("", model.Detail);
        Assert.AreEqual("已取消", model.FullDetail);
        Assert.IsFalse(model.IsStreamingPreview);
        model.Update(processing with { SessionId = 2, PartialText = null });
        Assert.AreEqual("Esc 取消", model.Detail);
    }

    [TestMethod]
    public void StreamingPreview_LongTextShowsTailWithoutBreakingSurrogatePair()
    {
        var model = new OverlayViewModel();
        string text = new string('前', 10) + "😀" + new string('后', 15);
        model.Update(new(1, SessionState.Processing, "R2T2", "R2T2", false, new(123, 456, 789, 124, 1000), PartialText: text));
        Assert.AreEqual("…😀" + new string('后', 15), model.Detail);
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
        Assert.AreEqual("正在聆听", model.Title);
        Assert.AreEqual("说话中已经出现的文字", model.Detail);
        model.Update(listening with { State = SessionState.Processing, PartialText = "校正后的完整文字。" });
        model.Update(listening with { State = SessionState.Injecting, PartialText = null });
        Assert.AreEqual("正在发送文本", model.Title);
        Assert.AreEqual("", model.Detail);
        Assert.IsFalse(model.ShowDetail);
        Assert.IsFalse(model.IsStreamingPreview);
        Assert.AreEqual("校正后的完整文字。", model.FullDetail);
        model.Update(listening with { State = SessionState.Completed, PartialText = null });
        Assert.AreEqual("已发送文本", model.Title);
        Assert.AreEqual("", model.Detail);
        Assert.IsFalse(model.ShowDetail);
        Assert.AreEqual("校正后的完整文字。", model.FullDetail);
        model.Update(listening with { SessionId = 2, PartialText = null });
        Assert.IsFalse(model.IsStreamingPreview);
        Assert.AreEqual("松开结束 · Esc 取消", model.Detail);
        Assert.IsTrue(model.ShowDetail);
        Assert.AreEqual("", model.FullDetail);
    }

    [TestMethod]
    public void TrayPreview_ReservesSpaceForStopButtonAndKeepsNewestPhrase()
    {
        var model = new OverlayViewModel();
        model.Update(new(1, SessionState.Listening, "", "R2T2", false, new(123, 456, 789, 124, 1000),
            FromTray: true, PartialText: "这是一段较长的实时预览文字，需要保留最新的一小段内容"));
        Assert.IsTrue(model.ShowStop);
        Assert.AreEqual("…需要保留最新的一小段内容", model.Detail);
        Assert.AreEqual("这是一段较长的实时预览文字，需要保留最新的一小段内容", model.FullDetail);
    }

    [TestMethod]
    [DataRow("没有检测到足够音量，请检查麦克风并重试。", "没有听到声音", "检查麦克风后重试")]
    [DataRow("录音太短，请按住快捷键多说一会儿。", "录音太短", "按住快捷键多说一会儿")]
    [DataRow("无法将识别结果发送到当前应用。文本已复制到剪贴板，请返回输入框后手动粘贴。", "文本已复制", "请回到输入框手动粘贴")]
    [DataRow("识别服务连接失败，请检查网络设置。", "语音输入未完成", "识别服务连接失败，请检查网络设置。")]
    public void Failure_KeepsRecoveryActionAndFullReason(string message, string title, string detail)
    {
        var model = new OverlayViewModel();
        model.Update(new(1, SessionState.Failed, message, "R2T2", false, new(123, 456, 789, 124, 1000)));
        Assert.AreEqual(title, model.Title);
        Assert.AreEqual(detail, model.Detail);
        Assert.AreEqual(message, model.FullDetail);
        Assert.IsTrue(model.ShowDetail);
        Assert.IsFalse(model.IsStreamingPreview);
    }
}
