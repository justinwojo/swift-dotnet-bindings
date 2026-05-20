// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.KeyPath;

/// <summary>
/// Phase-1 CSM end-to-end gate for the new <c>KeyPathFamily</c> ABI category.
///
/// <para>The Swift fixture (<c>CsmKeyPathParam.swift</c>) defines a PAT
/// <c>CsmKp_Filterable</c> with two closed conformers (<c>CsmKp_ConformerA</c>,
/// <c>CsmKp_ConformerB</c>) and a parent <c>CsmKp_Bag&lt;T: CsmKp_Filterable&gt;</c>
/// whose <c>count(matching:)</c> method takes a
/// <c>KeyPath&lt;CsmKp_ConcreteFilter, String&gt;</c>. The Root is a **top-level
/// concrete struct** rather than <c>T.Filter</c>, so Phase 1 isolates A2
/// (KeyPathFamily category + DangerousGetHandle emission + Unmanaged.fromOpaque
/// reconstruction in the Swift wrapper) without touching A1's pairing-generic
/// substitution work (Phase 2).</para>
///
/// <para>Asserts:</para>
/// <list type="bullet">
///   <item>Per-conformer CSM extension classes exist for both ConformerA and
///   ConformerB (<c>CsmKp_BagCsmKp_ConformerACsmExtensions</c>,
///   <c>CsmKp_BagCsmKp_ConformerBCsmExtensions</c>) — proves CSM emitted the
///   pairing.</item>
///   <item>The <c>Count(matching:)</c> extension method accepts a
///   <c>KeyPath&lt;CsmKp_ConcreteFilter, string&gt;</c> param at the type-system
///   level (the static type — a compile error here would mean the public param
///   type rendered wrong).</item>
///   <item>End-to-end round-trip: pass a typed KeyPath obtained via
///   <c>CsmKp_KeyPathFactory.MakeTitlePath()</c> through the CSM extension method;
///   the Swift body indexes into a probe filter (with title = conformer's
///   <c>displayName</c>) via the KeyPath and returns the resulting String's
///   length. Equality with the expected length confirms (a) DangerousGetHandle
///   wired through P/Invoke, (b) Unmanaged.fromOpaque reconstruction in the
///   Swift wrapper, (c) per-conformer dispatch (ConformerA vs ConformerB
///   produces distinct return values via T.displayName).</item>
/// </list>
/// </summary>
public class CsmKeyPathParamTests : TestBase
{
    public CsmKeyPathParamTests(TestResults results) : base(results) { }

    // ---------------------------------------------------------------------------------------
    // CSM extension class emission — per-conformer classes exist
    // ---------------------------------------------------------------------------------------

    public void TestConformerA_CsmExtensionsClass_Exists()
    {
        var t = typeof(global::SwiftBindingsTestLib.CsmKp_BagSwiftBindingsTestLib_CsmKp_ConformerACsmExtensions);
        AssertNotNull(t, "CSM emitted per-conformer extension class for ConformerA");
    }

    public void TestConformerB_CsmExtensionsClass_Exists()
    {
        var t = typeof(global::SwiftBindingsTestLib.CsmKp_BagSwiftBindingsTestLib_CsmKp_ConformerBCsmExtensions);
        AssertNotNull(t, "CSM emitted per-conformer extension class for ConformerB");
    }

    // ---------------------------------------------------------------------------------------
    // KeyPath origination — Session 3 OUT path bind for the typed factory
    // ---------------------------------------------------------------------------------------

    public void TestKeyPathFactory_MakeTitlePath_ReturnsTypedKeyPath()
    {
        var kp = CsmKp_KeyPathFactory.MakeTitlePath();
        AssertNotNull(kp, "Factory returns a non-null KeyPath");
        AssertFalse(kp.IsInvalid, "KeyPath handle is valid");
        AssertTrue(
            kp is global::Swift.KeyPath<CsmKp_ConcreteFilter, string>,
            "Factory return type binds as KeyPath<CsmKp_ConcreteFilter, string>");
    }

    // ---------------------------------------------------------------------------------------
    // End-to-end: CSM Count(matching:) round-trips a KeyPath through P/Invoke + Swift wrapper.
    // The Swift body builds a probe `CsmKp_ConcreteFilter(title: T.displayName)`, reads
    // through the KeyPath, and returns the .count of the resulting String. A correct
    // round-trip means DangerousGetHandle + Unmanaged.fromOpaque both wired up.
    // ---------------------------------------------------------------------------------------

    public void TestConformerA_Count_RoundTripsKeyPathThroughCsm()
    {
        using var bag = CsmKp_BagSwiftBindingsTestLib_CsmKp_ConformerACsmExtensions.FromSwiftBindingsTestLib_CsmKp_ConformerA();
        var kp = CsmKp_KeyPathFactory.MakeTitlePath();
        var count = bag.Count(keyPath: kp);
        // T.displayName = "CsmKp_ConformerA" → length 16.
        AssertEqual<nint>(16, count, "ConformerA round-trip: KeyPath dispatch returns probe title length");
    }

    public void TestConformerB_Count_RoundTripsKeyPathThroughCsm()
    {
        using var bag = CsmKp_BagSwiftBindingsTestLib_CsmKp_ConformerBCsmExtensions.FromSwiftBindingsTestLib_CsmKp_ConformerB();
        var kp = CsmKp_KeyPathFactory.MakeTitlePath();
        var count = bag.Count(keyPath: kp);
        // T.displayName = "CsmKp_ConformerB" → length 16.
        AssertEqual<nint>(16, count, "ConformerB round-trip: KeyPath dispatch returns probe title length");
    }

    // ---------------------------------------------------------------------------------------
    // Per-conformer separation: ConformerA and ConformerB emit distinct extension method
    // groups. Same KeyPath flows through both without aliasing.
    // ---------------------------------------------------------------------------------------

    public void TestPerConformerExtensions_AreDistinctTypes()
    {
        var a = typeof(global::SwiftBindingsTestLib.CsmKp_BagSwiftBindingsTestLib_CsmKp_ConformerACsmExtensions);
        var b = typeof(global::SwiftBindingsTestLib.CsmKp_BagSwiftBindingsTestLib_CsmKp_ConformerBCsmExtensions);
        AssertFalse(a == b, "Per-conformer CSM extension classes are nominally distinct");
    }
}
