// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Generic struct with Collection conformance
/// members declared in a separate <c>extension</c> block (matching MusicKit's
/// <c>MusicItemCollection&lt;MusicItemType&gt;</c> shape). Multiple sibling
/// overloads share a base Swift name (<c>index</c>, <c>formIndex</c>) but
/// differ by argument label (<c>before:</c>, <c>after:</c>,
/// <c>_:offsetBy:</c>).
///
/// Pre-fix: the wrapper-emit collision check in
/// <c>MethodWrapperEmitter.WouldGenericStaticDispatchSkipForExtensionCollision</c>
/// compared base Swift names only. Any extension method on a generic parent
/// that shared a base name with another method was dropped with a
/// <c>// Generic static dispatch wrapper skipped</c> comment, leaving the C#
/// P/Invoke as a tombstoned <c>// Unsupported: method 'index' — P/Invoke
/// removed because the Swift wrapper symbol was stripped during wrapper
/// compilation</c> stub. MusicKit's matrix lost all of
/// <c>Index(int)</c>, <c>Index(int,int)</c>, <c>FormIndex(int)</c> this way.
///
/// Post-fix: the gate compares full dispatch identities (base name plus per-slot
/// (label, type, inout) tuples); methods that differ in any of those resolve
/// unambiguously and each emits a real @_cdecl wrapper. This test exercises the
/// runtime path end-to-end.
/// </summary>
public class GenericIndexableCollectionTests : TestBase
{
    public GenericIndexableCollectionTests(TestResults results) : base(results) { }

    public void TestGenericIndexable_StartEndIndices_RoundTrip()
    {
        using var coll = Functions.MakeGenericIndexableCollection(
            firstTag: "a", secondTag: "b", thirdTag: "c");

        AssertEqual(0, coll.StartIndex, "startIndex");
        AssertEqual(3, coll.EndIndex, "endIndex");
    }

    public void TestGenericIndexable_IndexAfter_Increments()
    {
        // index(after:) — selector `index(after:)`. Sibling to index(before:)
        // and index(_:offsetBy:); pre-fix all three were dropped. Post-fix,
        // at least one of them maps to single-arg `Index(int)` in C# (with
        // numeric suffixes disambiguating against the other single-arg
        // overload), and the round-trip succeeds.
        using var coll = Functions.MakeGenericIndexableCollection(
            firstTag: "a", secondTag: "b", thirdTag: "c");

        AssertEqual((nint)1, coll.Index(0), "index(after: 0)");
        AssertEqual((nint)3, coll.Index(2), "index(after: 2)");
    }

    public void TestGenericIndexable_IndexOffsetBy_TwoArgs_RoundTrip()
    {
        // index(_:offsetBy:) — selector `index(_:offsetBy:)`. Sibling to the
        // two single-arg `index` overloads with different selectors. Maps to
        // C# `Index(int, int)`.
        using var coll = Functions.MakeGenericIndexableCollection(
            firstTag: "a", secondTag: "b", thirdTag: "c");

        AssertEqual((nint)2, coll.Index(0, 2), "index(0, offsetBy: 2)");
        AssertEqual((nint)0, coll.Index(3, -3), "index(3, offsetBy: -3)");
    }

    public void TestGenericIndexable_BothSingleArgIndexOverloads_Emitted()
    {
        // Compile-time + reflection proof that BOTH sibling overloads sharing
        // the base name `index` but differing in argument label survive
        // wrapper-emit. Pre-fix: only the natural-C#-selector `Index(int)`
        // would have emitted (and only if no sibling existed); the rest fell
        // through to tombstoned comments. Post-fix: BOTH emit, and one carries
        // a numeric collision suffix (e.g. `Index` + `Index2`).
        var t = typeof(GenericIndexableCollection<IndexableCoin>);
        var singleArgIndex = t.GetMethods()
            .Where(m => (m.Name == "Index" || m.Name.StartsWith("Index"))
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(int))
            .ToArray();
        AssertTrue(singleArgIndex.Length >= 2,
            $"Expected ≥2 single-int Index overloads (before+after siblings); got {singleArgIndex.Length}");
    }

    public void TestGenericIndexable_FormIndexAfter_AdvancesByOne()
    {
        // formIndex(after: inout Int) — selector `formIndex(after:)`.
        // Sibling to formIndex(before:) with a different label. Pre-fix
        // the wrapper-emit dropped both; post-fix both emit their @_cdecl
        // wrapper and the runtime call completes.
        using var coll = Functions.MakeGenericIndexableCollection(
            firstTag: "a", secondTag: "b", thirdTag: "c");

        // C# surfaces the inout via a return-style API (or void-with-write;
        // either way the cross-boundary call must complete without crashing
        // — that's the regression we're guarding). The pass condition here
        // is: we get through the P/Invoke at all on both Mono JIT and
        // NativeAOT, which prior-art `TestMusicItemBag_FormIndex_*` already
        // covers for inline-on-struct shape. This test extends the proof to
        // the extension-block shape where the pre-fix selector-collision
        // gate had been deleting the wrapper.
        coll.FormIndex(1);
        coll.FormIndex(2);
        TestLogger.Info("FormIndex(after: inout Int) round-trip completed without crash");
    }

    public void TestGenericIndexable_BothSingleArgFormIndexOverloads_Emitted()
    {
        // Parallel to the Index reflection check: prove that BOTH sibling
        // formIndex overloads (`formIndex(after: inout Int)` and
        // `formIndex(before: inout Int)`) survive wrapper-emit despite sharing
        // the base name `formIndex`. Pre-fix only the lexically-first sibling
        // (if any) would have emitted; post-fix both emit, with a numeric
        // collision suffix disambiguating the C# names.
        //
        // Count by DISTINCT C# method name (e.g. `FormIndex` + `FormIndex2`)
        // rather than overload count, so a single Swift sibling that gets
        // emitted both as `FormIndex(int)` and `FormIndex(nint)` (the int/nint
        // convenience pairing) can't false-pass this check.
        var t = typeof(GenericIndexableCollection<IndexableCoin>);
        var distinctFormIndexNames = t.GetMethods()
            .Where(m => (m.Name == "FormIndex" || m.Name.StartsWith("FormIndex"))
                && m.GetParameters().Length == 1)
            .Select(m => m.Name)
            .Distinct()
            .ToArray();
        AssertTrue(distinctFormIndexNames.Length >= 2,
            $"Expected ≥2 distinct single-arg FormIndex method names (before+after siblings); got {distinctFormIndexNames.Length}: [{string.Join(", ", distinctFormIndexNames)}]");
    }
}
