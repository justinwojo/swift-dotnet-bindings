// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime coverage for the AppIntents 0.12.0 nested-of-parent shape:
/// a generic host whose constructor accepts <c>Outer&lt;T&gt;.Inner</c>.
///
/// <list type="bullet">
///   <item><c>NestedHostStruct&lt;TElement&gt;</c> — non-frozen struct host;
///     mirrors <c>EnumURLRepresentation&lt;TEnum&gt;</c> projection (C# class
///     with SafeHandle).</item>
///   <item><c>NestedHostClass&lt;TElement&gt;</c> — class host; mirrors the
///     small set of class-based AppIntents declarative types.</item>
/// </list>
///
/// <para>Both hosts ship a nested <c>Caption</c>/<c>Tag</c> value-type struct
/// used as the constructor param. These constructors previously emitted
/// with <c>[Obsolete(SB0001)]</c>; the widened static-factory gate
/// (<c>IsNestedTypeOfParentGeneric</c>) routes them through the normalized
/// <c>@_cdecl</c> shim, dropping SB0001 and eliminating the heap-corruption
/// risk documented in the KeyPath crasher.</para>
/// </summary>
public class NestedOfParentTests : TestBase
{
    public NestedOfParentTests(TestResults results) : base(results) { }

    public void TestNestedHostStruct_ConstructorAcceptsNestedCaption()
    {
        var caption = new NestedHostStruct<BoxKP>.Caption("hello");
        using var host = new NestedHostStruct<BoxKP>(caption);
        var text = host.CaptionText.ToString();
        AssertEqual("hello", text, "Caption text round-trips through GSF nested ctor");
    }

    public void TestNestedHostClass_ConstructorAcceptsNestedTag()
    {
        var tag = new NestedHostClass<BoxKP>.Tag("label-A");
        using var host = new NestedHostClass<BoxKP>(tag);
        var label = host.TagLabel.ToString();
        AssertEqual("label-A", label, "Tag label round-trips through GSF nested ctor");
    }

    // Cross-host nested-of-parent (outer != host): the GSF gate REJECTS this shape
    // (predicate `OuterMatchesParent` in `GenericDispatchEmitter.IsNestedTypeOfParentGeneric`).
    // Reason: the foreign outer's value-witness table does not flow through the
    // `any _SBW_GSF_X.Type` existential dispatch, so the host's VWT.destroy walks a
    // layout that does not match the initialized storage and SIGSEGVs in
    // `RefCounts::doDecrementSlow` at SafeHandle.Dispose. Doc 14 hypothesis 1 (Option B
    // reconstruction) was tried and did not change runtime behaviour; SIL emission for
    // same-host vs cross-host is structurally identical, so the divergence is in
    // runtime metadata resolution, not the emitted code. Doc 14 hypothesis 3 keeps
    // cross-host on direct CallConvSwift (SB0001) — the fixtures remain in-tree as
    // durable regression markers; when GSF can carry the foreign outer's witness
    // table the `[Skip]` drops and these tests turn green.

    [Skip("Cross-host nested-of-parent destroy-witness fault (doc 14 hypothesis 3); foreign outer's VWT does not thread through `any _SBW_GSF_X.Type` existential dispatch.")]
    public void TestCrossHostStruct_AcceptsForeignOuterNested()
    {
        var payload = new CrossHostOuter<BoxKP>.Body("crosshost-struct");
        using var sibling = new CrossHostSiblingStruct<BoxKP>(payload);
        var text = sibling.PayloadText.ToString();
        AssertEqual("crosshost-struct", text, "Cross-host nested-of-parent (struct host) round-trips through GSF");
    }

    [Skip("Cross-host nested-of-parent destroy-witness fault (doc 14 hypothesis 3); foreign outer's VWT does not thread through `any _SBW_GSF_X.Type` existential dispatch.")]
    public void TestCrossHostClass_AcceptsForeignOuterNested()
    {
        var payload = new CrossHostOuter<BoxKP>.Body("crosshost-class");
        using var sibling = new CrossHostSiblingClass<BoxKP>(payload);
        var text = sibling.PayloadText.ToString();
        AssertEqual("crosshost-class", text, "Cross-host nested-of-parent (class host) round-trips through GSF");
    }
}
