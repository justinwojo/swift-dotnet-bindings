// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage for a nested struct that satisfies a protocol's requirements ONLY
/// through a constrained protocol extension
/// (<c>extension P where Self: RawRepresentable, Self.RawValue: Q</c>), where the parent
/// type's property of the nested type forces the nested-type-collision pre-pass to
/// rename the nested type with a <c>Type</c> suffix.
///
/// Pre-fix, <c>ProtocolExtensionDefaultsIndex</c> silently skipped any extension whose
/// <c>WhereConstraints.Count &gt; 0</c>, so <c>CanFullyImplementProtocol</c> saw the
/// property requirement as unprovided and dropped <c>IConstrainedDefaulted</c> from the
/// post-rename type's C# interface list. The witness-table dictionary entry was emitted
/// correctly; the gap was strictly in the interface declaration. The compile gate caught
/// it via CS0311 on the cursor's generic constraint; the runtime gate confirms the
/// concrete-type path still resolves to the constrained-extension default.
/// </summary>
public class ConstrainedExtensionDefaultViaRenameTests : TestBase
{
    public ConstrainedExtensionDefaultViaRenameTests(TestResults results) : base(results) { }

    // --- Rename witness: confirms the nested-type-collision pre-pass renamed `Kind` to
    // `KindInfo` (parent kept its `Kind` property). Independent of any conformance work.

    public void TestParentExposesRenamedNestedType()
    {
        using var rv = new DefaultRawValue(value: 1);
        using var kind = new ConstraintHost.KindInfo(rawValue: rv);
        using var host = new ConstraintHost(kind: kind);
        using var roundTrip = host.Kind;
        AssertEqual(1, roundTrip.RawValue.Value,
            "ConstraintHost.Kind property round-trips a renamed KindInfo payload");
    }

    // --- Interface declaration: the property tested by the runtime gate. Pre-fix this
    // is_check returns false because IConstrainedDefaulted is missing from KindInfo's
    // declared interfaces.

    public void TestRenamedNestedType_DeclaresProtocolInterface()
    {
        using var kind = new ConstraintHost.KindInfo(rawValue: new DefaultRawValue(value: 7));
        AssertTrue(kind is IConstrainedDefaulted,
            "ConstraintHost.KindInfo declares IConstrainedDefaulted in its interface list");
    }

    // --- Generic constraint compile + runtime: DefaultedCursor<T> has
    // `where T : IConstrainedDefaulted` in C#. Pre-fix this method's return type
    // `DefaultedCursor<KindInfo>` fails CS0311 — the binding-tests --compile-only gate
    // never even built this test. Post-fix the closed instantiation compiles and we
    // round-trip the constrained-extension default's value.

    public void TestMakeCursor_ReturnsClosedGenericOverRenamedConformer()
    {
        using var kind = new ConstraintHost.KindInfo(rawValue: new DefaultRawValue(value: 3));
        using var host = new ConstraintHost(kind: kind);
        using var cursor = host.MakeCursor();
        // MakeCursor() hard-codes a DefaultRawValue(value: 1), and the constrained
        // extension default's describe() routes through rawValue.describe(), which
        // prints "DefaultRawValue value=<n>". The `kind` we passed in is dropped by
        // MakeCursor — its value=3 is just to prove the parameter wiring round-trips.
        AssertEqual("via:DefaultRawValue value=1", cursor.Describe(),
            "DefaultedCursor<KindInfo>.Describe() routes through the constrained-extension default");
    }
}
