using System.Text;
using VoxType.Core.Dictation;
using VoxType.Core.TextInjection;
using VoxType.Infrastructure.Settings;
using VoxType_App.ViewModels;

namespace VoxType.Tests.Dictation;

[TestClass]
public sealed class OverlayInteractionTests
{
    [TestMethod]
    public void AutomaticPlacement_BottomInputMovesOverlayAboveProtectedLine()
    {
        var input = new OverlayRect(450, 720, 100, 60);
        var result = OverlayPlacement.Place(new(0, 0, 1000, 800), 320, 56, 1, avoid: input);
        Assert.AreEqual(new OverlayRect(340, 648, 320, 56), result);
        Assert.IsFalse(result.Intersects(input));
    }

    [TestMethod]
    public void SavedPosition_StaysPutWhenInputDoesNotOverlap()
    {
        var result = OverlayPlacement.Place(new(-1600, -100, 1600, 900), 320, 56, 1,
            new("left", 0.25, 0.5), new(0, 600, 30, 30));
        Assert.AreEqual(new OverlayRect(-1280, 322, 320, 56), result);
    }

    [TestMethod]
    public void SavedPosition_IsTemporarilyAvoidedWithoutChangingAnchor()
    {
        var anchor = new OverlayAnchor("screen", 0.5, 1);
        var input = new OverlayRect(400, 730, 200, 70);
        var result = OverlayPlacement.Place(new(0, 0, 1000, 800), 320, 56, 1, anchor, input);
        Assert.AreEqual(new OverlayRect(340, 658, 320, 56), result);
        Assert.AreEqual(new OverlayAnchor("screen", 0.5, 1), anchor);
        Assert.AreEqual(new OverlayRect(340, 744, 320, 56), OverlayPlacement.Place(new(0, 0, 1000, 800), 320, 56, 1, anchor));
    }

    [TestMethod]
    public void Placement_ClampsOversizedOverlayToSmallMonitor()
    {
        Assert.AreEqual(new OverlayRect(-200, 100, 200, 40),
            OverlayPlacement.Place(new(-200, 100, 200, 40), 640, 112, 2, new("small", 1, 1)));
    }

    [TestMethod]
    public void DraggedPosition_RestoresProportionAfterDpiAndWorkAreaChange()
    {
        var saved = OverlayPlacement.Remember("screen", new(-1000, 0, 1000, 800), new(-320, 744, 320, 56));
        Assert.AreEqual(new OverlayAnchor("screen", 1, 1), saved);
        Assert.AreEqual(new OverlayRect(1360, 1338, 640, 112),
            OverlayPlacement.Place(new(0, 50, 2000, 1400), 640, 112, 2, saved));
    }

    [TestMethod]
    public void Drag_ClampsAllEdgesOnNegativeCoordinateMonitor()
    {
        var work = new OverlayRect(-1920, -100, 1920, 1000);
        Assert.AreEqual(new OverlayRect(-1920, -100, 320, 56), OverlayPlacement.Clamp(work, new(-2200, -300, 320, 56)));
        Assert.AreEqual(new OverlayRect(-320, 844, 320, 56), OverlayPlacement.Clamp(work, new(100, 2000, 320, 56)));
    }

    [TestMethod]
    public void Standby_ClearsTranscriptAndNeverLooksLikeRecording()
    {
        var model = new OverlayViewModel();
        model.Update(new(1, SessionState.Listening, "", "", true, FocusContext.Empty, FromTray: true, PartialText: "临时文字"));
        model.Update(new(1, SessionState.Idle, "麦克风未开启", "", true, FocusContext.Empty));
        Assert.AreEqual("麦克风未开启", model.Title);
        Assert.IsTrue(model.IsStandby);
        Assert.IsFalse(model.ShowSession);
        Assert.IsFalse(model.IsListening);
        Assert.IsFalse(model.IsProcessing);
        Assert.IsFalse(model.ShowStop);
        Assert.IsFalse(model.ShowRetry);
        Assert.IsFalse(model.IsStreamingPreview);
    }

    [TestMethod]
    public void Failure_OffersRetryAndNewRecordingClearsIt()
    {
        var model = new OverlayViewModel();
        model.Update(new(1, SessionState.Failed, "网络错误", "", false, FocusContext.Empty));
        Assert.IsTrue(model.ShowRetry);
        Assert.AreEqual("输入未完成", model.Title);
        Assert.AreEqual("网络错误", model.FullDetail);
        model.Update(new(2, SessionState.Starting, "", "", false, FocusContext.Empty));
        Assert.IsFalse(model.ShowRetry);
        Assert.IsFalse(model.IsStandby);
        Assert.IsTrue(model.IsProcessing);
    }

    [TestMethod]
    public async Task Preferences_RoundTripPinnedAndManualPosition_AndResetRetainsPin()
    {
        var file = new MemoryFile();
        var preferences = new OverlayPreferences(true, new("screen", 0.3, 0.8));
        await new OverlayPreferencesStore(file).SaveAsync(preferences);
        Assert.AreEqual(preferences, await new OverlayPreferencesStore(file).LoadAsync());
        await new OverlayPreferencesStore(file).SaveAsync(preferences with { Anchor = null });
        Assert.AreEqual(new OverlayPreferences(true), await new OverlayPreferencesStore(file).LoadAsync());
    }

    [TestMethod]
    public async Task Preferences_InvalidAnchorKeepsPinAndFallsBackToAutomaticPosition()
    {
        var file = new MemoryFile { Bytes = Encoding.UTF8.GetBytes("{\"Pinned\":true,\"Anchor\":{\"Monitor\":\"screen\",\"X\":3,\"Y\":0.5}}") };
        Assert.AreEqual(new OverlayPreferences(true), await new OverlayPreferencesStore(file).LoadAsync());
    }

    [TestMethod]
    public async Task Preferences_MissingOrCorruptFileDefaultsToTemporaryMode()
    {
        var file = new MemoryFile();
        Assert.AreEqual(new OverlayPreferences(), await new OverlayPreferencesStore(file).LoadAsync());
        file.Bytes = Encoding.UTF8.GetBytes("broken");
        Assert.AreEqual(new OverlayPreferences(), await new OverlayPreferencesStore(file).LoadAsync());
    }

    private sealed class MemoryFile : ISettingsFile
    {
        public byte[]? Bytes { get; set; }
        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Bytes);
        public Task WriteAtomicAsync(byte[] bytes, CancellationToken cancellationToken) { Bytes = bytes; return Task.CompletedTask; }
    }
}
