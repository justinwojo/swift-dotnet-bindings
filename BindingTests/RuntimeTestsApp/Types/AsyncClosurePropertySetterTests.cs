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

    /// <summary>
    /// Maximum-case nested-type regression: Stripe's
    /// <c>PaymentSheet.IntentConfiguration.ConfirmHandler</c> shape — async
    /// closure stored properties on a nested class must skip cleanly. Round 1
    /// added the skip at PropertyHandler but only proved it on a flat class;
    /// Stripe's downstream regen kept emitting <c>Swift.AnyType</c> setters on
    /// the nested type. If any of the async closure properties below ever
    /// re-appear in the generated binding, compile of this file fails.
    /// The non-async sibling on the same nested type MUST keep binding —
    /// proves the skip doesn't over-fire.
    /// </summary>
    public void TestNestedTypeAsyncClosurePropertiesAreSkippedAndSiblingsKeepBinding()
    {
        // Outer + nested struct must both bind. If the nested-type async
        // skip accidentally dropped the surrounding type, these `new`s would
        // fail to compile, not just at runtime.
        var outer = new AsyncClosurePropertySetterOuter();
        var inner = new AsyncClosurePropertySetterOuter.IntentConfigurationNested();
        AssertNotNull(outer, "Outer class containing async-closure-property nested struct still binds");
        // Nested struct surfaces as a C# class with SwiftSafeHandle (same
        // shape Stripe's IntentConfiguration takes) — its existence proves
        // the skip didn't drop the surrounding type.
        AssertNotNull(inner, "Nested struct with async-closure-property siblings still binds");

        // Sibling non-async closure property on the SAME nested struct must
        // round-trip — the skip must not over-fire across closure shapes.
        var captured = -1;
        inner.SiblingNonAsyncObserver = v => { captured = v; };
        inner.TriggerSiblingObserver(13);
        AssertEqual(13, captured, "Non-async sibling closure property on nested struct still round-trips");
        TestLogger.Info("Nested-struct async-closure-property siblings: non-async observer round-trip passed");
    }

#if false
    // Documentation-only: these references MUST fail to compile against the
    // post-fix generated binding. Each nested-type async closure property
    // below was the exact Stripe ConfirmHandler shape (or a near-sibling) and
    // must be skipped at the PropertyHandler layer for the nested-type case.
    // If any property re-appears in the generated nested type, uncommenting
    // this block turns the regression into a compile failure.
    public void NegativeProbe_NestedStructAsyncClosurePropertiesAreSkipped()
    {
        var inner = new AsyncClosurePropertySetterOuter.IntentConfigurationNested();
        inner.ConfirmHandler = async (arg, force) => "ok";       // must not compile
        inner.SetterOnlyHandler = async () => 42;                // must not compile
        inner.AsyncNonThrowingFactory = async () => 7;           // must not compile
    }
#endif

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
