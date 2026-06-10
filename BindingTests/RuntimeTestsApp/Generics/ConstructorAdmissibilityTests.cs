// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// End-to-end coverage for the unified constructor-admissibility predicate.
/// The fixture <c>CtorAdmBox&lt;Value: CtorAdmValue&gt;</c> reproduces every init-erasure
/// facet that independently broke the AppIntents <c>EntityProperty&lt;Value&gt;</c> Swift
/// wrapper compile. Two complementary layers:
///
/// <list type="bullet">
///   <item><b>Functional round-trips</b> — the binding ASSEMBLY LINKS and the admissible
///     surfaces actually call across the ABI. Pre-fix the synthesized Swift wrappers failed
///     <c>swiftc</c> and nothing linked, so reaching these assertions at all is the headline.</item>
///   <item><b>Structural absence</b> — Stage 2 per-conformer correctness: a constrained-extension
///     init is emitted as a CSM closed form ONLY for the satisfying conformer, the open
///     (unconstrained) constructor is suppressed for inadmissible inits, and the <c>_const</c>
///     init has no surface anywhere.</item>
/// </list>
///
/// Tag semantics from the fixture: <c>init(tag:salt:)</c> stores <c>tag</c>;
/// <c>init(intMarker:)</c> stores <c>"int"</c>; <c>init(ropeFlag:)</c> stores <c>"rope"</c>.
/// </summary>
public class ConstructorAdmissibilityTests : TestBase
{
    public ConstructorAdmissibilityTests(TestResults results) : base(results) { }

    // ── Functional: admissible designated init (open _SBW_CI_ erasure) ────────────

    public void TestOpenDesignatedInit_RoundTripsTag()
    {
        using var box = new CtorAdmBox<CtorAdmIntValue>("open-designated", (nint)42);
        AssertEqual("open-designated", box.Tag, "open (tag:salt:) erasure round-trips tag");
    }

    // ── Functional: CSM closed forms (concrete per-conformer wrappers) ────────────

    public void TestCsmIntValue_DesignatedInit_RoundTripsTag()
    {
        using var box = CtorAdmBoxSwiftBindingsTestLib_CtorAdmIntValueCsmExtensions
            .FromSwiftBindingsTestLib_CtorAdmIntValue("csm-int", (nint)1);
        AssertEqual("csm-int", box.Tag, "CSM<CtorAdmIntValue> (tag:salt:) round-trips tag");
    }

    public void TestCsmIntValue_IntMarkerConstrainedInit_Emits()
    {
        // `extension CtorAdmBox where Value.Element == Int { init(intMarker:) }` —
        // CtorAdmIntValue.Element == Int satisfies it; init stores tag "int".
        using var box = CtorAdmBoxSwiftBindingsTestLib_CtorAdmIntValueCsmExtensions
            .FromSwiftBindingsTestLib_CtorAdmIntValue((nint)7);
        AssertEqual("int", box.Tag, "CSM<CtorAdmIntValue> (intMarker:) constrained init round-trips");
    }

    public void TestCsmRopeValue_DesignatedInit_RoundTripsTag()
    {
        using var box = CtorAdmBoxSwiftBindingsTestLib_CtorAdmRopeValueCsmExtensions
            .FromSwiftBindingsTestLib_CtorAdmRopeValue("csm-rope", (nint)2);
        AssertEqual("csm-rope", box.Tag, "CSM<CtorAdmRopeValue> (tag:salt:) round-trips tag");
    }

    public void TestCsmRopeValue_RopeFlagConstrainedInit_Emits()
    {
        // `extension CtorAdmBox where Value.Element: CtorAdmCollectionish { init(ropeFlag:) }` —
        // CtorAdmRope: CtorAdmCollectionish satisfies it; init stores tag "rope".
        using var box = CtorAdmBoxSwiftBindingsTestLib_CtorAdmRopeValueCsmExtensions
            .FromSwiftBindingsTestLib_CtorAdmRopeValue(true);
        AssertEqual("rope", box.Tag, "CSM<CtorAdmRopeValue> (ropeFlag:) constrained init round-trips");
    }

    // ── Structural: Stage 2 per-conformer correctness + Stage 1 suppression ───────

    public void TestOpenConstructor_OnlyAdmissibleDesignatedInitEmitted()
    {
        // The open generic class must expose ONLY the admissible designated init
        // (string tag, nint salt). The `_const` constId(string), intMarker(nint), and
        // ropeFlag(bool) inits are inadmissible for OPEN dispatch and must be suppressed —
        // emitting them against the raw generic init symbol via direct CallConvSwift is
        // not ABI-correct.
        var boxType = typeof(CtorAdmBox<CtorAdmIntValue>);

        AssertNotNull(
            boxType.GetConstructor(new[] { typeof(string), typeof(nint) }),
            "open (string tag, nint salt) designated init is emitted");
        AssertNull(
            boxType.GetConstructor(new[] { typeof(string) }),
            "open (_const constId:) init is suppressed");
        AssertNull(
            boxType.GetConstructor(new[] { typeof(nint) }),
            "open (intMarker:) constrained-extension init is suppressed");
        AssertNull(
            boxType.GetConstructor(new[] { typeof(bool) }),
            "open (ropeFlag:) constrained-extension init is suppressed");
    }

    public void TestCsmIntValue_EmitsOnlySatisfyingConstrainedForm()
    {
        var ext = typeof(CtorAdmBoxSwiftBindingsTestLib_CtorAdmIntValueCsmExtensions);
        const string name = "FromSwiftBindingsTestLib_CtorAdmIntValue";

        AssertNotNull(ext.GetMethod(name, new[] { typeof(string), typeof(nint) }),
            "CSM<CtorAdmIntValue> emits the (tag:salt:) designated form");
        AssertNotNull(ext.GetMethod(name, new[] { typeof(nint) }),
            "CSM<CtorAdmIntValue> emits (intMarker:) — Element == Int is satisfied");
        AssertNull(ext.GetMethod(name, new[] { typeof(bool) }),
            "CSM<CtorAdmIntValue> SKIPS (ropeFlag:) — Int is not CtorAdmCollectionish");
        AssertNull(ext.GetMethod(name, new[] { typeof(string) }),
            "CSM<CtorAdmIntValue> SKIPS (_const constId:) on every closed form");
    }

    public void TestCsmRopeValue_EmitsOnlySatisfyingConstrainedForm()
    {
        var ext = typeof(CtorAdmBoxSwiftBindingsTestLib_CtorAdmRopeValueCsmExtensions);
        const string name = "FromSwiftBindingsTestLib_CtorAdmRopeValue";

        AssertNotNull(ext.GetMethod(name, new[] { typeof(string), typeof(nint) }),
            "CSM<CtorAdmRopeValue> emits the (tag:salt:) designated form");
        AssertNotNull(ext.GetMethod(name, new[] { typeof(bool) }),
            "CSM<CtorAdmRopeValue> emits (ropeFlag:) — CtorAdmRope: CtorAdmCollectionish is satisfied");
        AssertNull(ext.GetMethod(name, new[] { typeof(nint) }),
            "CSM<CtorAdmRopeValue> SKIPS (intMarker:) — Element == CtorAdmRope ≠ Int");
        AssertNull(ext.GetMethod(name, new[] { typeof(string) }),
            "CSM<CtorAdmRopeValue> SKIPS (_const constId:) on every closed form");
    }

    // ── Facet (e): concrete same-type pin on an UNCONSTRAINED generic parent ───────
    //
    // Concrete same-type pin on an unconstrained generic parent regression. `init(pinnedSalt:) where RowDecoder == ()`
    // is confined to CtorAdmVoidPin<Void>; the `== ()` pin's target is unrepresentable so
    // GenericSignatureParser drops the constraint. Pre-fix the now-apparently-unconstrained
    // init flowed to the `_SBW_CI_` open path, emitting
    // `extension CtorAdmVoidPin: _SBW_CI_{hash} {}` requiring init(pinnedSalt:) on EVERY
    // specialization → "type 'CtorAdmVoidPin<RowDecoder>' does not conform", failing the
    // whole binding compile. Post-fix the dropped pin is flagged
    // (HasUnrepresentableConcreteSameTypePin) and the admissibility gate refuses open
    // dispatch. With no PAT for CSM, the pinned init has NO surface; the general init still
    // erases — so reaching these assertions at all is the headline (pre-fix nothing linked).

    public void TestVoidPin_GeneralInit_RoundTripsTag()
    {
        using var pin = new CtorAdmVoidPin<CtorAdmIntValue>("alias");
        AssertEqual("alias", pin.Tag, "void-pin general (tag:) open _SBW_CI_ erasure round-trips tag");
    }

    public void TestVoidPin_PinnedInitSuppressed_GeneralInitEmitted()
    {
        var pinType = typeof(CtorAdmVoidPin<CtorAdmIntValue>);

        AssertNotNull(
            pinType.GetConstructor(new[] { typeof(string) }),
            "void-pin general (tag:) init is emitted (open _SBW_CI_ erasure backs the unconstrained type)");
        AssertNull(
            pinType.GetConstructor(new[] { typeof(nint) }),
            "void-pin (pinnedSalt:) `where RowDecoder == ()` init is suppressed (open dispatch refused)");
    }
}
