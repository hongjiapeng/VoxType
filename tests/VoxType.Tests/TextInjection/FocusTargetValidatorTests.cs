using VoxType.Core.TextInjection;
using VoxType.Infrastructure.TextInjection;

namespace VoxType.Tests.TextInjection;

[TestClass]
public sealed class FocusTargetValidatorTests
{
    private static readonly FocusContext Original = new(10, 20, 30, 40, 50);
    [TestMethod]
    public void SameValidWindow_NoRestoreNeeded()
    {
        var windows = new Windows();
        Assert.IsNull(new FocusTargetValidator(windows).Validate(Original, true));
        Assert.AreEqual(0, windows.RestoreCalls);
        Assert.AreEqual(40, windows.Info!.FocusWindow);
        Assert.AreEqual(40, NativeInputTargetPlatform.InputSize, "x64 INPUT includes the full mouse/keyboard union.");
    }
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ChangedForeground_TriesRestoreOnlyOnce(bool succeeds)
    {
        var windows = new Windows { ForegroundWindow = 99, RestoreSucceeds = succeeds };
        string? error = new FocusTargetValidator(windows).Validate(Original, true);
        Assert.AreEqual(1, windows.RestoreCalls);
        if (succeeds) { Assert.IsNull(error); Assert.AreEqual(10, windows.ForegroundWindow); }
        else { Assert.IsNotNull(error); Assert.AreEqual(99, windows.ForegroundWindow); }
    }
    [TestMethod]
    public void FinalValidation_DoesNotForceForegroundAgain()
    {
        var windows = new Windows { ForegroundWindow = 99 };
        Assert.IsNotNull(new FocusTargetValidator(windows).Validate(Original, false));
        Assert.AreEqual(0, windows.RestoreCalls);
    }
    [TestMethod]
    [DataRow("missing")]
    [DataRow("pid")]
    [DataRow("tid")]
    [DataRow("reused")]
    [DataRow("unknown-start")]
    [DataRow("minimized")]
    [DataRow("own-window")]
    [DataRow("child-focus")]
    [DataRow("unknown-focus")]
    [DataRow("elevated")]
    [DataRow("unknown-integrity")]
    public void UnsafeTarget_IsRejected(string change)
    {
        var windows = new Windows();
        windows.Info = change switch
        {
            "missing" => null, "pid" => windows.Info! with { ProcessId = 21 }, "tid" => windows.Info! with { ThreadId = 31 },
            "reused" => windows.Info! with { ProcessStartTicks = 51 }, "unknown-start" => windows.Info! with { ProcessStartTicks = null },
            "minimized" => windows.Info! with { IsMinimized = true }, "own-window" => windows.Info! with { IsExternal = false },
            "child-focus" => windows.Info! with { FocusWindow = 41 }, "unknown-focus" => windows.Info! with { FocusWindow = 0 },
            "elevated" => windows.Info! with { Integrity = 0x3000 }, "unknown-integrity" => windows.Info! with { Integrity = null },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.IsNotNull(new FocusTargetValidator(windows).Validate(Original, true));
        Assert.AreEqual(0, windows.RestoreCalls);
    }
    [TestMethod]
    public void IdentityChangedDuringRestore_IsRevalidated()
    {
        var windows = new Windows { ForegroundWindow = 99, MutateOnRestore = true };
        Assert.IsNotNull(new FocusTargetValidator(windows).Validate(Original, true));
        Assert.AreEqual(1, windows.RestoreCalls);
    }
    private sealed class Windows : IWindowTargetAccess
    {
        public nint ForegroundWindow { get; set; } = 10;
        public int? OwnIntegrity => 0x2000;
        public TargetWindowInfo? Info { get; set; } = new(20, 30, 50, 40, false, true, 0x2000);
        public bool RestoreSucceeds { get; set; } = true;
        public bool MutateOnRestore { get; set; }
        public int RestoreCalls { get; private set; }
        public TargetWindowInfo? Inspect(nint window) => Info;
        public bool TrySetForeground(nint window)
        {
            RestoreCalls++;
            if (RestoreSucceeds) ForegroundWindow = window;
            if (MutateOnRestore) Info = Info! with { ProcessId = 999 };
            return RestoreSucceeds;
        }
    }
}
