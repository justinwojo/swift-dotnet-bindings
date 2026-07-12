// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// End-to-end gate for the NSCoding-delegate rescue (RoomPlan.RoomCaptureViewDelegate shape).
///
/// Previously an <c>@objc protocol X: NSCoding</c> was disqualified from the
/// <c>EveryObjCProtocol</c> carrier entirely (NSCoding sat in the same disqualifying
/// set as NSSecureCoding/NSCopying/NSMutableCopying), so the delegate's real dispatch
/// members were suppressed and every consumer of <c>any RenderProgressDelegate</c>
/// degraded. The fix splits NSCoding out: the carrier gains a no-op
/// <c>extension EveryObjCProtocol: NSCoding</c> stub (encode(with:) does nothing,
/// init?(coder:) forwards to the designated init) so the synthesized
/// <c>extension EveryObjCProtocol: RenderProgressDelegate</c> type-checks.
///
/// This test exercises the full reverse-dispatch round-trip: a plain C# class
/// implements the generated <c>IRenderProgressDelegate</c> interface and is passed to
/// Swift functions that invoke each witness method through the existential. The Swift
/// wrapper only compiles if the NSCoding stub + the delegate conformance type-check,
/// so reaching the calls already proves the emitter rescued the carrier; the
/// value assertions prove the vtable callbacks dispatch into the managed methods.
/// </summary>
public class NSCodingDelegateDispatchTests : TestBase
{
    public NSCodingDelegateDispatchTests(TestResults results) : base(results) { }

    public void TestNSCodingDelegateRoundTrips()
    {
        var impl = new RenderProgressDelegateImpl(stage: 7);

        // Auto-wrap constructs an EveryObjCProtocol-backed proxy and hands it to the
        // Swift functions. If NSCoding regressed to disqualifying, IRenderProgressDelegate
        // and/or the wrapper Swift module won't compile and these call sites won't exist.
        TestLibFunctions.DriveRenderProgress(impl, 63);
        AssertEqual(63, impl.LastPercent, "reportProgress reverse-dispatched into the managed implementation");
        AssertTrue(impl.ReportCalled, "Managed reportProgress() actually fired");

        var stage = TestLibFunctions.ReadRenderStage(impl);
        AssertEqual(7, stage, "currentStage reverse-dispatched into the managed implementation");
        AssertTrue(impl.StageCalled, "Managed currentStage() actually fired");
    }
}

/// <summary>
/// Plain managed implementation of the generated <c>IRenderProgressDelegate</c>
/// interface — no proxy subclassing, no manual existential wrapping. The auto-wrap
/// fallback over the NSCoding-rescued EveryObjCProtocol carrier is what makes this work.
/// </summary>
internal class RenderProgressDelegateImpl : IRenderProgressDelegate
{
    private readonly int _stage;

    public RenderProgressDelegateImpl(int stage)
    {
        _stage = stage;
    }

    public bool ReportCalled { get; private set; }
    public bool StageCalled { get; private set; }
    public int LastPercent { get; private set; }

    public void ReportProgress(int percent)
    {
        ReportCalled = true;
        LastPercent = percent;
    }

    public int GetCurrentStage()
    {
        StageCalled = true;
        return _stage;
    }
}
