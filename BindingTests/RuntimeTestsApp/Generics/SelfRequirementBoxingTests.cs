// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime coverage for <c>HasSelfRequirement</c> protocols boxed through an
/// <c>any SelfReqAnchored</c> existential parameter. Companion to
/// <c>PATFallbackBoundaryTests</c>: that fixture pins the
/// <c>HasAssociatedTypes</c>-only branch (TaggedAssociator), this one pins
/// the <c>HasSelfRequirement</c> branch via <c>SelfReqAnchored</c> whose
/// <c>associatedtype Stamp where Stamp == Self</c> plants both "Self." and
/// "Self ==" in the protocol's generic signature so the parser's tighter
/// pattern in <c>SwiftABIParser</c> sets both flags.
///
/// <para>
/// The lowering branch in
/// <c>ExistentialHandler.GetPublicExistentialType()</c> is shared between
/// PAT and Self-requirement, but the conformance dictionary registration in
/// <c>TypeHandlerHelpers.GenerateProtocolConformanceDictionaryEntries</c>
/// skips Self-requirement protocols from the standard
/// <c>typeof(I{Proto})</c> entry, so the runtime
/// <c>GetOrCreate&lt;object&gt;</c> lookup must rely on the
/// <c>typeof(object)</c> entry emitted on the HasAssociatedTypes branch.
/// The runtime tests here verify that lookup actually round-trips a
/// concrete conformer through the existential boundary.
/// </para>
/// </summary>
public class SelfRequirementBoxingTests : TestBase
{
    public SelfRequirementBoxingTests(TestResults results) : base(results) { }

    /// <summary>
    /// Compile-time half: pins the lowered parameter type so a regression
    /// in <c>GetPublicExistentialType()</c> is visible without running the
    /// runtime dispatch path. The Self-requirement branch must lower to the
    /// literal <c>object</c> C# type — the generic interface
    /// <c>ISelfReqAnchored&lt;TStamp&gt;</c> can't be referenced without type
    /// arguments at a free-function call site, and the same-module
    /// conformer indexing in the SpecializationEngine does not cover this
    /// protocol so the alternative <c>ExistentialUnion</c> path doesn't fire.
    /// </summary>
    public void TestReadSelfReqAnchoredParameterShape()
    {
        var method = typeof(TestLibFunctions).GetMethod(
            "ReadSelfReqAnchored",
            BindingFlags.Public | BindingFlags.Static);
        AssertTrue(method is not null,
            "TestLibFunctions.ReadSelfReqAnchored must exist on the generated " +
            "binding. If missing, the free function with `any SelfReqAnchored` " +
            "parameter was skipped during emission — the HasSelfRequirement " +
            "lowering branch in ExistentialHandler should produce an `object` " +
            "parameter, not skip the function.");

        var parameters = method!.GetParameters();
        AssertEqual(1, parameters.Length,
            "ReadSelfReqAnchored must have exactly one parameter.");

        var paramType = parameters[0].ParameterType;
        TestLogger.Info($"ReadSelfReqAnchored parameter[0] type = {paramType.FullName}");

        AssertEqual(typeof(object), paramType,
            "ReadSelfReqAnchored's `any SelfReqAnchored` parameter must lower " +
            "to the literal `object` C# type. The HasSelfRequirement branch " +
            "in ExistentialHandler.GetPublicExistentialType() falls through " +
            "to `object` because the generic interface " +
            "`ISelfReqAnchored<TStamp>` has no type argument in scope at the " +
            "call site. A regression here means the emitted signature " +
            "references that generic interface (CS0305) or has been switched " +
            "to ExistentialUnion — both indicate the boxing path under test " +
            "no longer fires.");
    }

    /// <summary>
    /// Runtime-dispatch half (Alpha): passes a same-module conformer through
    /// the existential boundary and verifies the dispatched
    /// <c>anchorTag</c> property reflects the concrete type.
    /// </summary>
    public void TestReadSelfReqAnchoredDispatchAlpha()
    {
        using var alpha = new StampedAlpha("alpha-anchor");

        // Direct-dispatch control: if `AnchorTag` doesn't round-trip on the
        // concrete type the existential assertion below would be measuring
        // a lower-layer bug rather than the boxing path.
        AssertEqual("alpha-anchor", alpha.AnchorTag.ToString(),
            "StampedAlpha.AnchorTag must round-trip directly before the " +
            "existential boundary path is exercised. If this fails the " +
            "failure is below the boxing layer and the dispatch assertion " +
            "below is noise.");

        var dispatched = TestLibFunctions.ReadSelfReqAnchored(alpha);
        TestLogger.Info($"ReadSelfReqAnchored(StampedAlpha) returned \"{dispatched}\"");

        AssertEqual("stamp:alpha-anchor", dispatched,
            "ReadSelfReqAnchored must dispatch `.anchorTag` through the " +
            "Self-requirement existential container to the concrete " +
            "StampedAlpha conformer. If this returns an empty tag or a " +
            "different value, the runtime descriptor lookup at " +
            "`GetOrCreate<object>` failed to locate the witness table " +
            "registered in StampedAlpha's `_protocolConformanceSymbols` " +
            "static — likely because " +
            "TypeHandlerHelpers.GenerateProtocolConformanceDictionaryEntries " +
            "skipped the Self-requirement entry without emitting a " +
            "compensating `typeof(object)` key.");
    }

    /// <summary>
    /// Runtime-dispatch half (Omega): a second conformer with a distinct
    /// tag proves the witness table actually carries the concrete type
    /// rather than landing on a shared default.
    /// </summary>
    public void TestReadSelfReqAnchoredDispatchOmega()
    {
        using var omega = new StampedOmega("omega-anchor");

        // Direct-dispatch control mirrors the Alpha test: if this getter
        // doesn't round-trip directly, the existential assertion below would
        // be measuring a lower-layer issue unrelated to the boxing path.
        AssertEqual("omega-anchor", omega.AnchorTag.ToString(),
            "StampedOmega.AnchorTag must round-trip directly before the " +
            "existential boundary path is exercised — a failure here points " +
            "below the boxing layer.");

        var dispatched = TestLibFunctions.ReadSelfReqAnchored(omega);
        TestLogger.Info($"ReadSelfReqAnchored(StampedOmega) returned \"{dispatched}\"");

        AssertEqual("stamp:omega-anchor", dispatched,
            "ReadSelfReqAnchored must route to StampedOmega's witness table, " +
            "not StampedAlpha's. A regression here means the typeof(object) " +
            "key in the conformance dictionary is shared between conformers " +
            "or pointing at the wrong descriptor symbol.");
    }
}
