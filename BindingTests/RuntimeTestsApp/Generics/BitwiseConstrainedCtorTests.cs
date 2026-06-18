// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// End-to-end fail-closed gate for the ONE stdlib marker the GSF (generic-static-factory)
/// constructor path cannot honour in its open erased form: <c>BitwiseCopyable</c>.
///
/// The fixture <c>BitwiseCtorBox&lt;Value&gt;</c> is an unconstrained generic struct. Its base
/// <c>init(tag:)</c> is admissible and round-trips through unconditional GSF dispatch. Its
/// <c>init(bitwiseCount:)</c> lives in <c>extension BitwiseCtorBox where Value: BitwiseCopyable</c>.
///
/// Unlike the four erasure-SAFE markers (Sendable/Copyable/Escapable/SendableMetatype) covered by
/// <c>MarkerConstrainedCtorTests</c> — where dropping the where clause yields a legal unconditional
/// GSF conformance — <c>BitwiseCopyable</c> is a real layout requirement with NO legal open erased
/// form. An unconditional <c>_SBW_GSF</c> body calling <c>Self(bitwiseCount:)</c> fails <c>swiftc</c>
/// ("requires that 'Value' conform to 'BitwiseCopyable'"), and the marker cannot be re-stated as a
/// conditional conformance. So <c>ConstructorAdmissibility.HasUnerasableParentMarkerConstraint</c>
/// must refuse the constructor entirely — it is absent from the C# surface, not emitted with a
/// dangling/ABI-incorrect P/Invoke. The base <c>(string)</c> init must still round-trip.
///
/// Reverting the refusal makes <c>TestBitwiseConstrainedInit_NotEmitted</c> go red — the dangling
/// <c>(nint bitwiseCount)</c> ctor reappears (previously bound to a direct CallConvSwift P/Invoke
/// against the raw generic init symbol).
/// </summary>
public class BitwiseConstrainedCtorTests : TestBase
{
    public BitwiseConstrainedCtorTests(TestResults results) : base(results) { }

    public void TestBaseInit_RoundTripsTag()
    {
        using var box = new BitwiseCtorBox<CtorAdmIntValue>("base-tag");
        AssertEqual("base-tag", box.Tag, "base (tag:) GSF init round-trips for an unconstrained generic struct");
    }

    public void TestBitwiseConstrainedInit_NotEmitted()
    {
        // The base (string) init is admissible and must survive; the marker-constrained
        // (nint bitwiseCount) init has no legal open erased form and must fail closed (be absent).
        var boxType = typeof(BitwiseCtorBox<CtorAdmIntValue>);

        AssertNotNull(
            boxType.GetConstructor(new[] { typeof(string) }),
            "base (string tag) init is emitted");

        var bitwiseCtor = boxType.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            new[] { typeof(nint) },
            modifiers: null);
        AssertNull(
            bitwiseCtor,
            "marker-constrained (nint bitwiseCount) init is refused (BitwiseCopyable has no open erased form → fail closed)");
    }

    public void TestMemberBitwiseConstrainedInit_NotEmitted()
    {
        // The associated-type MEMBER form: `extension MemberBitwiseCtorBox where Value.Item:
        // BitwiseCopyable { init(bitwiseItemCount:) }`. The marker constrains an associated-type
        // member, not the parent param directly, so a direct-conformance-only scan would miss it — but
        // the unconditional open GSF body still fails to compile, so the init MUST fail closed. The
        // base `init(tag:)` (which inherits only the NORMAL `Value: BitwiseItemCarrier` constraint)
        // stays emitted. Reflection-only: constructing the base init needs a Swift `BitwiseItemCarrier`
        // conformer + witness table, which is orthogonal to what this gate proves.
        var boxType = typeof(MemberBitwiseCtorBox<CtorAdmIntValue>);

        AssertNotNull(
            boxType.GetConstructor(new[] { typeof(string) }),
            "base (string tag) init is emitted (normal-protocol parent constraint is erasure-safe)");

        var memberBitwiseCtor = boxType.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            new[] { typeof(nint) },
            modifiers: null);
        AssertNull(
            memberBitwiseCtor,
            "member-marker-constrained (nint bitwiseItemCount) init is refused (Value.Item: BitwiseCopyable has no open erased form → fail closed)");
    }
}
