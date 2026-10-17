using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoxType.Core.Hotkeys;

namespace VoxType.Tests.Hotkeys;

[TestClass]
public sealed class HotkeyMatcherTests
{
    private const int Alt = 0xa4, Space = 0x20;
    private static readonly HotkeyBinding AltSpace = new(KeyModifiers.Alt, Space);
    private static KeyDecision Down(HotkeyMatcher matcher, int key, bool cancel = false) => matcher.Process(new(key, true), cancel);
    private static KeyDecision Up(HotkeyMatcher matcher, int key, bool cancel = false) => matcher.Process(new(key, false), cancel);

    [TestMethod]
    [DataRow(0xa2, 0xa0)]
    [DataRow(0xa3, 0xa1)]
    public void Default_CtrlShiftSpace_StartsOnceAndReleasesOnEitherModifier(int control, int shift)
    {
        Assert.AreEqual(new HotkeyBinding(KeyModifiers.Control | KeyModifiers.Shift, Space), HotkeyBinding.Default);
        Assert.AreEqual("Ctrl + Shift + Space", HotkeyBinding.Default.DisplayName);
        foreach (int first in new[] { control, shift, Space })
        {
            var matcher = new HotkeyMatcher(HotkeyBinding.Default);
            Down(matcher, control); Down(matcher, shift);
            Assert.AreEqual(new KeyDecision(true, HotkeyAction.Pressed), Down(matcher, Space));
            Assert.AreEqual(new KeyDecision(true), Down(matcher, Space));
            Assert.AreEqual(HotkeyAction.Released, Up(matcher, first).Action);
            foreach (int remaining in new[] { control, shift, Space }.Where(k => k != first)) Assert.IsNull(Up(matcher, remaining).Action);
            Assert.IsEmpty(matcher.DownKeys);
            Down(matcher, control); Down(matcher, shift);
            Assert.AreEqual(HotkeyAction.Pressed, Down(matcher, Space).Action);
        }
    }

    [TestMethod]
    public void SwallowedMainAbsentFromAsyncState_DoesNotCancelHold_AndKeyUpStillEnds()
    {
        var matcher = new HotkeyMatcher(HotkeyBinding.Default);
        Down(matcher, 0xa2); Down(matcher, 0xa0); Down(matcher, Space);
        for (int tick = 0; tick < 30; tick++)
        {
            Assert.IsNull(matcher.ReconcileAsyncState(new HashSet<int> { 0xa2, 0xa0 }));
            Assert.IsTrue(matcher.IsActive, "A swallowed Space is absent from Windows async state even while held.");
            Assert.AreEqual(new KeyDecision(true), Down(matcher, Space));
        }
        Assert.AreEqual(new KeyDecision(true, HotkeyAction.Released), Up(matcher, Space));
        Assert.IsFalse(matcher.IsActive);
        Up(matcher, 0xa2); Up(matcher, 0xa0);
        Down(matcher, 0xa2); Down(matcher, 0xa0);
        Assert.AreEqual(HotkeyAction.Pressed, Down(matcher, Space).Action);
    }

    [TestMethod]
    public void AsyncStateLosesRequiredModifier_CancelsOnce_AndPreservesMainPairUntilUp()
    {
        var matcher = new HotkeyMatcher(HotkeyBinding.Default);
        Down(matcher, 0xa2); Down(matcher, 0xa0); Down(matcher, Space);
        Assert.AreEqual(HotkeyAction.Interrupted, matcher.ReconcileAsyncState(new HashSet<int> { 0xa0 }));
        Assert.IsFalse(matcher.IsActive);
        Assert.IsNull(matcher.ReconcileAsyncState(new HashSet<int>()));
        Down(matcher, 0xa2); Down(matcher, 0xa0);
        Assert.AreEqual(new KeyDecision(true), Down(matcher, Space), "Modifier recovery must not restart a held main key.");
        Assert.AreEqual(new KeyDecision(true), Up(matcher, Space));
        Up(matcher, 0xa2); Up(matcher, 0xa0);
        Down(matcher, 0xa2); Down(matcher, 0xa0);
        Assert.AreEqual(HotkeyAction.Pressed, Down(matcher, Space).Action);
    }

    [TestMethod]
    public void SwallowedEscapeAbsentFromAsyncState_StillSwallowsRepeatAndKeyUp()
    {
        var matcher = new HotkeyMatcher(HotkeyBinding.Default);
        Assert.AreEqual(new KeyDecision(true, HotkeyAction.Cancelled), Down(matcher, 0x1b, true));
        Assert.IsNull(matcher.ReconcileAsyncState(new HashSet<int>()));
        Assert.AreEqual(new KeyDecision(true), Down(matcher, 0x1b, true));
        Assert.AreEqual(new KeyDecision(true), Up(matcher, 0x1b));
        Assert.AreEqual(new KeyDecision(false), Down(matcher, 0x1b));
    }

    [TestMethod]
    public void Repeats_StartOnlyOnce_AndSwallowMatchingMainPair()
    {
        var matcher = new HotkeyMatcher(AltSpace);
        Assert.AreEqual(new KeyDecision(false), Down(matcher, Alt));
        Assert.AreEqual(new KeyDecision(true, HotkeyAction.Pressed), Down(matcher, Space));
        for (int i = 0; i < 20; i++) Assert.AreEqual(new KeyDecision(true), Down(matcher, Space));
        Assert.IsTrue(matcher.IsActive);
        Assert.AreEqual(new KeyDecision(true, HotkeyAction.Released), Up(matcher, Space));
        Assert.IsFalse(matcher.IsActive);
        Assert.AreEqual(new KeyDecision(false), Up(matcher, Alt));
    }

    [TestMethod]
    [DataRow(Alt, Space)]
    [DataRow(Space, Alt)]
    public void ReleaseOrder_EndsOnce_AndRequiresWholeSequenceRelease(int first, int second)
    {
        var matcher = new HotkeyMatcher(AltSpace);
        Down(matcher, Alt); Down(matcher, Space);
        Assert.AreEqual(HotkeyAction.Released, Up(matcher, first).Action);
        Assert.IsNull(Down(matcher, first).Action, "Repressing one key must not rearm a held sequence.");
        Assert.IsNull(Up(matcher, first).Action);
        Assert.IsNull(Up(matcher, second).Action);
        Down(matcher, Alt);
        Assert.AreEqual(HotkeyAction.Pressed, Down(matcher, Space).Action);
    }

    [TestMethod]
    public void LeftAndRightAlt_ReleaseOneSide_DoesNotEndCombination()
    {
        var matcher = new HotkeyMatcher(AltSpace);
        Down(matcher, Alt); Down(matcher, 0xa5);
        Assert.AreEqual(HotkeyAction.Pressed, Down(matcher, Space).Action);
        Assert.IsNull(Up(matcher, Alt).Action);
        Assert.IsTrue(matcher.IsActive);
        Assert.AreEqual(HotkeyAction.Released, Up(matcher, 0xa5).Action);
        Assert.IsTrue(Up(matcher, Space).Suppress);
    }

    [TestMethod]
    [DataRow(0xa2)]
    [DataRow(0xa0)]
    [DataRow(0x5b)]
    public void ExtraModifier_DoesNotStart(int extra)
    {
        var matcher = new HotkeyMatcher(AltSpace);
        Down(matcher, Alt); Down(matcher, extra);
        Assert.AreEqual(new KeyDecision(false), Down(matcher, Space));
        Assert.IsFalse(matcher.IsActive);
        Assert.IsNull(Up(matcher, extra).Action, "Releasing the extra modifier cannot start a held main key.");
        Assert.IsNull(Down(matcher, Space).Action);
    }

    [TestMethod]
    public void MainFirst_DoesNotRetroactivelyStart()
    {
        var matcher = new HotkeyMatcher(AltSpace);
        Assert.AreEqual(new KeyDecision(false), Down(matcher, Space));
        Assert.IsNull(Down(matcher, Alt).Action);
        Assert.AreEqual(new KeyDecision(false), Down(matcher, Space));
        Up(matcher, Space); Up(matcher, Alt);
        Down(matcher, Alt);
        Assert.AreEqual(HotkeyAction.Pressed, Down(matcher, Space).Action);
    }

    [TestMethod]
    public void Escape_CancelsOnce_AndSwallowsItsPair()
    {
        var matcher = new HotkeyMatcher(AltSpace);
        Down(matcher, Alt); Down(matcher, Space);
        Assert.AreEqual(new KeyDecision(true, HotkeyAction.Cancelled), Down(matcher, 0x1b, true));
        Assert.IsFalse(matcher.IsActive);
        Assert.AreEqual(new KeyDecision(true), Down(matcher, 0x1b, false));
        Assert.AreEqual(new KeyDecision(true), Up(matcher, 0x1b, false));
        Assert.AreEqual(new KeyDecision(true), Up(matcher, Space));
        Assert.IsNull(Up(matcher, Alt).Action);
        Assert.AreEqual(new KeyDecision(false), Down(matcher, 0x1b, false));
    }

    [TestMethod]
    public void Escape_DuringProcessing_CancelsWithoutRecordingKeyState()
    {
        var matcher = new HotkeyMatcher(AltSpace);
        Assert.AreEqual(new KeyDecision(true, HotkeyAction.Cancelled), Down(matcher, 0x1b, true));
        Assert.AreEqual(new KeyDecision(true), Up(matcher, 0x1b));
        Assert.IsFalse(matcher.IsActive);
    }

    [TestMethod]
    public void InjectedSequence_IsIgnored()
    {
        var matcher = new HotkeyMatcher(AltSpace);
        foreach (int key in new[] { Alt, Space, 0x1b })
            Assert.AreEqual(new KeyDecision(false), matcher.Process(new(key, true, true), true));
        Assert.IsEmpty(matcher.DownKeys);
        Assert.IsFalse(matcher.IsActive);
    }

    [TestMethod]
    public void Interrupt_RequiresReleaseBeforeNewPress()
    {
        var matcher = new HotkeyMatcher(AltSpace);
        Down(matcher, Alt); Down(matcher, Space);
        Assert.AreEqual(HotkeyAction.Interrupted, matcher.Interrupt(true));
        Assert.AreEqual(new KeyDecision(true), Down(matcher, Space));
        Assert.IsFalse(matcher.IsActive);
        Assert.IsNull(Up(matcher, Space).Action); Up(matcher, Alt);
        Down(matcher, Alt);
        Assert.AreEqual(HotkeyAction.Pressed, Down(matcher, Space).Action);
    }

    [TestMethod]
    public void MissedRelease_WatchdogCancelsAndRearms()
    {
        var matcher = new HotkeyMatcher(AltSpace);
        Down(matcher, Alt); Down(matcher, Space);
        Assert.AreEqual(HotkeyAction.Interrupted, matcher.Reconcile(new HashSet<int> { Space }));
        Assert.AreEqual(new KeyDecision(true), Down(matcher, Space));
        Assert.IsNull(matcher.Reconcile(new HashSet<int>()));
        Assert.IsEmpty(matcher.DownKeys);
        Down(matcher, Alt);
        Assert.AreEqual(HotkeyAction.Pressed, Down(matcher, Space).Action);
    }

    [TestMethod]
    public void Configure_DuringSequence_AppliesOnlyAfterOldKeysRelease()
    {
        var matcher = new HotkeyMatcher(AltSpace);
        var next = new HotkeyBinding(KeyModifiers.Control, Space);
        Down(matcher, Alt); Down(matcher, Space);
        matcher.Configure(next);
        Assert.AreEqual(AltSpace, matcher.Binding);
        Assert.AreEqual(HotkeyAction.Released, Up(matcher, Alt).Action);
        Assert.AreEqual(AltSpace, matcher.Binding);
        Assert.IsTrue(Up(matcher, Space).Suppress);
        Assert.AreEqual(next, matcher.Binding);
        Down(matcher, Alt);
        Assert.IsNull(Down(matcher, Space).Action);
        Up(matcher, Space); Up(matcher, Alt);
        Down(matcher, 0xa2);
        Assert.AreEqual(HotkeyAction.Pressed, Down(matcher, Space).Action);
    }

    [TestMethod]
    public void Watchdog_OneAltSideLost_OtherSideHeld_DoesNotInterrupt()
    {
        var matcher = new HotkeyMatcher(AltSpace);
        Down(matcher, Alt); Down(matcher, 0xa5); Down(matcher, Space);
        Assert.IsNull(matcher.Reconcile(new HashSet<int> { 0xa5, Space }));
        Assert.IsTrue(matcher.IsActive);
        Assert.AreEqual(HotkeyAction.Released, Up(matcher, 0xa5).Action);
    }

    [TestMethod]
    [DataRow(KeyModifiers.None, Space)]
    [DataRow((KeyModifiers)16, Space)]
    [DataRow(KeyModifiers.Alt, 0x1b)]
    [DataRow(KeyModifiers.Alt, 0x7b)]
    [DataRow(KeyModifiers.Control, 0xa4)]
    public void InvalidBinding_IsRejected(KeyModifiers modifiers, int key) => Assert.ThrowsExactly<ArgumentException>(() => new HotkeyMatcher(new(modifiers, key)));

    [TestMethod]
    public void MultiModifierBinding_RequiresBoth_AndDisplaysCorrectly()
    {
        var binding = new HotkeyBinding(KeyModifiers.Alt | KeyModifiers.Control, Space);
        var matcher = new HotkeyMatcher(binding);
        Assert.AreEqual("Ctrl + Alt + Space", binding.DisplayName);
        Down(matcher, Alt);
        Assert.IsNull(Down(matcher, Space).Action);
        Up(matcher, Space); Down(matcher, 0xa3);
        Assert.AreEqual(HotkeyAction.Pressed, Down(matcher, Space).Action);
        Assert.AreEqual(HotkeyAction.Released, Up(matcher, 0xa3).Action);
    }
}
