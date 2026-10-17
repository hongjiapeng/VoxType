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
        Assert.AreEqual("正在识别 · Esc 取消", model.Title);
        Assert.IsTrue(model.IsProcessing);
        Assert.IsTrue(model.IsStreamingPreview);
        model.Update(processing with { State = SessionState.Cancelling, Message = "已取消" });
        Assert.AreEqual("已取消", model.Detail);
        Assert.IsFalse(model.IsStreamingPreview);
        model.Update(processing with { SessionId = 2, PartialText = null });
        Assert.AreEqual("R2T2", model.Detail);
    }

    [TestMethod]
    public void StreamingPreview_LongTextShowsTailWithoutBreakingSurrogatePair()
    {
        var model = new OverlayViewModel();
        string text = new string('前', 10) + "😀" + new string('后', 63);
        model.Update(new(1, SessionState.Processing, "R2T2", "R2T2", false, new(123, 456, 789, 124, 1000), PartialText: text));
        Assert.AreEqual("…😀" + new string('后', 63), model.Detail);
    }

    [TestMethod]
    public void LivePreview_KeepsListeningWaveformAndText_AndRetainsResultThroughCompletion()
    {
        var model = new OverlayViewModel();
        var listening = new SessionUpdate(1, SessionState.Listening, "", "R2T2", false, new(123, 456, 789, 124, 1000), PartialText: "说话中已经出现的文字");
        model.Update(listening);
        Assert.IsTrue(model.IsListening);
        Assert.IsFalse(model.IsProcessing);
        Assert.IsTrue(model.IsStreamingPreview);
        Assert.AreEqual("正在聆听 · 实时预览", model.Title);
        Assert.AreEqual("说话中已经出现的文字", model.Detail);
        model.Update(listening with { State = SessionState.Processing, PartialText = "校正后的完整文字。" });
        model.Update(listening with { State = SessionState.Injecting, PartialText = null });
        Assert.AreEqual("校正后的完整文字。", model.Detail);
        model.Update(listening with { State = SessionState.Completed, PartialText = null });
        Assert.AreEqual("已发送文本", model.Title);
        Assert.AreEqual("校正后的完整文字。", model.Detail);
        model.Update(listening with { SessionId = 2, PartialText = null });
        Assert.IsFalse(model.IsStreamingPreview);
        Assert.AreEqual("松开快捷键完成 · Esc 取消", model.Detail);
    }
}
