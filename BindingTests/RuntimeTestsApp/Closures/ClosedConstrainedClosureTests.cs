// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime coverage for escaping-closure methods that live inside an
/// inheritance-constrained extension on a generic class
/// (<c>extension HostWrapper where Base: PixelHost { func loadPixels(...) }</c>).
///
/// These are skipped today with reason <c>GenericTypeCallback</c> — a
/// <c>[UnmanagedCallersOnly]</c> reverse thunk cannot be emitted inside a generic
/// C# type. The closed-instantiation emitter surfaces them as static extension
/// methods on the fully concrete receiver <c>HostWrapper&lt;PixelHost&gt;</c>, whose
/// non-generic Swift <c>@_cdecl</c> wrapper produces a real, callable symbol.
///
/// Asserts the callbacks fire with the correct marshalled values, that success and
/// failure paths route to distinct closures, that a stored (truly escaping) closure
/// invoked after the call returns still fires, that a primitive non-closure argument
/// round-trips, and that two distinct concrete anchors (PixelHost, GlyphHost) do not
/// collide.
/// </summary>
public class ClosedConstrainedClosureTests : TestBase
{
    public ClosedConstrainedClosureTests(TestResults results) : base(results) { }

    public void TestLoadPixels_SuccessPath_FiresWithScaledValue()
    {
        var host = new HostWrapper<PixelHost>(new PixelHost(10));

        bool successFired = false, failureFired = false;
        int successValue = 0;
        host.LoadPixels(3, v => { successFired = true; successValue = v; }, _ => failureFired = true);

        AssertTrue(successFired, "onSuccess fired for positive scale factor");
        AssertEqual(30, successValue, "onSuccess received pixels(10) * factor(3)");
        AssertFalse(failureFired, "onFailure did not fire on the success path");
        TestLogger.Info("Closed constrained-extension closure success path passed");
    }

    public void TestLoadPixels_FailurePath_FiresDistinctClosure()
    {
        var host = new HostWrapper<PixelHost>(new PixelHost(10));

        bool successFired = false, failureFired = false;
        int failureValue = 0;
        host.LoadPixels(0, _ => successFired = true, v => { failureFired = true; failureValue = v; });

        AssertFalse(successFired, "onSuccess did not fire when factor <= 0");
        AssertTrue(failureFired, "onFailure fired for non-positive scale factor");
        AssertEqual(-1, failureValue, "onFailure received the sentinel value");
        TestLogger.Info("Closed constrained-extension closure failure path passed");
    }

    public void TestDescribe_SingleClosureWithPrimitiveArg_RoundTrips()
    {
        var host = new HostWrapper<PixelHost>(new PixelHost(42));

        bool doneFired = false;
        int doneValue = 0;
        host.Describe(5, v => { doneFired = true; doneValue = v; });

        AssertTrue(doneFired, "onDone fired for describe");
        AssertEqual(47, doneValue, "onDone received base.pixels(42) + bump(5)");
        TestLogger.Info("Closed constrained-extension single-closure path passed");
    }

    public void TestArmedCallback_StoredThenInvokedLater_EscapesCorrectly()
    {
        // The strongest escaping test: ArmCallback stores the closure and returns; its
        // GCHandle must survive past that return so FireArmed can invoke it afterward.
        var host = new HostWrapper<PixelHost>(new PixelHost(8));

        bool eventFired = false;
        int eventValue = 0;
        host.ArmCallback(v => { eventFired = true; eventValue = v; });
        AssertFalse(eventFired, "stored callback has not fired immediately after ArmCallback");

        bool ackFired = false;
        host.FireArmed(4, _ => ackFired = true);

        AssertTrue(eventFired, "stored (escaped) callback fired when FireArmed ran");
        AssertEqual(32, eventValue, "stored callback received pixels(8) * factor(4) after escaping");
        AssertTrue(ackFired, "ack callback fired");
        TestLogger.Info("Closed constrained-extension escaping-lifetime path passed");
    }

    public void TestLoadPixels_DistinctConcreteAnchor_DoesNotCollide()
    {
        // The GlyphHost extension shares the method name loadPixels but binds a
        // different concrete receiver (HostWrapper<GlyphHost>). Its wrapper symbol
        // and C# extension overload must be independent of the PixelHost one.
        var host = new HostWrapper<GlyphHost>(new GlyphHost(5));

        bool successFired = false;
        int successValue = 0;
        host.LoadPixels(7, v => { successFired = true; successValue = v; }, _ => { });

        AssertTrue(successFired, "GlyphHost onSuccess fired");
        AssertEqual(12, successValue, "GlyphHost onSuccess received glyphs(5) + factor(7)");
        TestLogger.Info("Closed constrained-extension distinct-anchor path passed");
    }

    public void TestLoadPixels_ViaFactory_RoundTrips()
    {
        // The non-generic factory path is an alternate way to obtain the closed
        // receiver; the extension method must work identically on it.
        var host = HostWrapperFactory.Wrap(new PixelHost(4));

        bool successFired = false;
        int successValue = 0;
        host.LoadPixels(5, v => { successFired = true; successValue = v; }, _ => { });

        AssertTrue(successFired, "factory-obtained receiver onSuccess fired");
        AssertEqual(20, successValue, "factory receiver onSuccess received 4 * 5");
        TestLogger.Info("Closed constrained-extension factory-receiver path passed");
    }
}
