// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Regression lock for Stripe's StripePaymentSheet.ConfirmHandler shape — a
/// setter-only property whose Swift type is Optional&lt;closure-returning-Task&gt;.
/// PropertyHandler now skips properties whose closure type is async; the
/// generator must not emit a P/Invoke body that references an undeclared
/// `valueHandle` or types the parameter as `Swift.AnyType` (asyncBridgeEligible
/// is false on accessor frames). The surrounding type must still bind so that
/// the non-async sibling property keeps working.
///
/// The async closure properties (<c>confirmHandler</c>,
/// <c>primitiveHandler</c>, <c>factory</c>) must NOT exist on the C# side.
/// "Property absent from the generated binding" cannot be expressed via a
/// runtime assertion, so compile success of this file against the generated
/// <c>AsyncClosurePropertySetterHolder</c> wrapper acts as the regression
/// gate. If a future regression re-emits one of the skipped properties, the
/// negative reference below — kept under <c>#if false</c> for documentation —
/// becomes a candidate compile probe.
/// </summary>
public class AsyncClosurePropertySetterTests : TestBase
{
    public AsyncClosurePropertySetterTests(TestResults results) : base(results) { }

    public void TestHolderConstructsAndBindsObserver()
    {
        // The surrounding class must still bind even though three of its
        // properties were skipped. If the type itself were skipped (or its
        // bin emission corrupted), the `new` would fail to compile.
        var holder = new AsyncClosurePropertySetterHolder();
        var captured = -1;
        holder.Observer = v => { captured = v; };
        holder.TriggerObserver(7);
        AssertEqual(7, captured, "Non-async observer property still binds + round-trips");
        TestLogger.Info("AsyncClosurePropertySetterHolder.observer round-trip passed");
    }

    public void TestHolderObserverSetToNull()
    {
        var holder = new AsyncClosurePropertySetterHolder();
        holder.Observer = v => { };
        holder.Observer = null;
        holder.TriggerObserver(99); // no callback, must not crash
        TestLogger.Info("AsyncClosurePropertySetterHolder.observer set-to-null passed (no crash)");
    }

#if false
    // Documentation-only: these references MUST fail to compile against the
    // post-fix generated binding. The async closure properties are skipped at
    // the PropertyHandler layer, so the C# wrapper has no `ConfirmHandler`,
    // `PrimitiveHandler`, or `Factory` member. If any of these properties
    // ever re-appear in the generated binding, uncommenting this block will
    // turn the regression into a compile failure.
    public void NegativeProbe_AsyncClosurePropertiesAreSkipped()
    {
        var holder = new AsyncClosurePropertySetterHolder();
        holder.ConfirmHandler = async (id, force) => "ok";   // must not compile
        holder.PrimitiveHandler = async (n) => n;            // must not compile
        holder.Factory = async () => 42;                     // must not compile
    }
#endif
}
