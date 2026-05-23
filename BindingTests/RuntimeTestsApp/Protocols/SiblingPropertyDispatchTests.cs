// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression tests for sibling-protocol property dispatch.
///
/// Shape: two or more class-bound protocols declare the same property name+type
/// with different accessor sets. The EveryProtocol emitter picks the protocol
/// with the fattest accessor set as the OWNER, emits the property body on its
/// extension, and emits empty extensions for the other siblings — Swift's
/// cross-extension witness resolution stitches them together.
///
/// Pre-fix bug: the owner's body always called its OWN vtable, even when the
/// dispatched-through proxy populated a SIBLING's vtable. A C# class that
/// implemented only the smaller sibling left the owner's vtable nil; the
/// owner's body force-unwrapped that nil function pointer and crashed
/// (SIGSEGV) the moment Swift read the property through the smaller existential.
///
/// Fix: the owner's body fans out across all sibling vtables, checking each
/// for a non-nil function pointer and dispatching through whichever vtable
/// the registered proxy populated. Symmetric for setters, but the setter
/// fan-out only inspects siblings whose accessor set includes a setter.
/// </summary>
public class SiblingPropertyDispatchTests : TestBase
{
    public SiblingPropertyDispatchTests(TestResults results) : base(results) { }

    // -- 2-sibling group: get-only + get+set --

    /// <summary>
    /// The pre-fix crash: a C# class that implements only the SMALLER sibling
    /// (ISiblingNamed, get-only). Swift reads the property through the smaller
    /// existential; before the fix this routes to the owner (SiblingMutableNamed)
    /// body which force-unwraps a nil _siblingMutableNamed_vtable pointer.
    /// </summary>
    public void TestGetThroughSmallerSiblingExistential()
    {
        var impl = new SiblingNamedOnlyImpl("alpha");
        var result = Functions.ReadSiblingNameViaGet(impl);
        AssertEqual("alpha", result, "Reading via smaller sibling existential returns C# impl value");
    }

    /// <summary>
    /// Control: a C# class that implements only the LARGER sibling (get+set).
    /// Owner-body dispatch through the owner's own vtable. Existed before fix,
    /// must continue to work post-fix.
    /// </summary>
    public void TestGetThroughOwnerSiblingExistential()
    {
        var impl = new SiblingMutableNamedOnlyImpl("beta");
        var result = Functions.ReadSiblingNameViaGetSet(impl);
        AssertEqual("beta", result, "Reading via larger sibling existential returns C# impl value");
    }

    /// <summary>
    /// Setter through the owner. Only the owner carries a setter in this group,
    /// so the setter body has no sibling fan-out — same single-branch path as
    /// before the fix.
    /// </summary>
    public void TestSetThroughOwnerSiblingExistential()
    {
        var impl = new SiblingMutableNamedOnlyImpl("initial");
        Functions.WriteSiblingNameViaGetSet(impl, "updated");
        AssertEqual("updated", impl.SiblingName, "Setter wrote through owner sibling existential");
    }

    // -- 3-sibling group: get-only + two get+set --

    /// <summary>
    /// 3-sibling group: C# implements only the GET-ONLY sibling (ISiblingTagged).
    /// Read via that smaller existential. Pre-fix: same SIGSEGV pattern as the
    /// 2-sibling case, owner is one of the two has-setter siblings.
    /// </summary>
    public void TestGetThroughGetOnlyInThreeSiblingGroup()
    {
        var impl = new SiblingTaggedOnlyImpl("tag-only");
        var result = Functions.ReadSiblingTagViaGet(impl);
        AssertEqual("tag-only", result, "Reading via get-only sibling in 3-group");
    }

    /// <summary>
    /// 3-sibling group: C# implements only the NON-OWNER has-setter sibling
    /// (ISiblingMutableTaggedAlt — lex tie-break makes SiblingMutableTagged the
    /// owner). Read via the non-owner existential. Pre-fix this crashes for
    /// the same reason — owner-body calls its own nil vtable.
    /// </summary>
    public void TestGetThroughNonOwnerHasSetterSibling()
    {
        var impl = new SiblingMutableTaggedAltOnlyImpl("alt-get");
        var result = Functions.ReadSiblingTagViaGetSetAlt(impl);
        AssertEqual("alt-get", result, "Reading via non-owner has-setter sibling in 3-group");
    }

    /// <summary>
    /// 3-sibling group, setter fan-out: C# implements only the NON-OWNER
    /// has-setter sibling. Write through the non-owner existential. The owner
    /// body's setter fan-out must dispatch through the populated alt vtable,
    /// not its own nil one.
    /// </summary>
    public void TestSetThroughNonOwnerHasSetterSibling()
    {
        var impl = new SiblingMutableTaggedAltOnlyImpl("before");
        Functions.WriteSiblingTagViaGetSetAlt(impl, "after");
        AssertEqual("after", impl.SiblingTag, "Setter wrote through non-owner has-setter sibling");
    }

    /// <summary>
    /// 3-sibling group, owner setter still works: C# implements only the OWNER
    /// has-setter sibling. Write through the owner existential. Owner body's
    /// setter fan-out picks its own populated vtable.
    /// </summary>
    public void TestSetThroughOwnerHasSetterSibling()
    {
        var impl = new SiblingMutableTaggedOnlyImpl("before");
        Functions.WriteSiblingTagViaGetSet(impl, "after");
        AssertEqual("after", impl.SiblingTag, "Setter wrote through owner has-setter sibling");
    }

    /// <summary>
    /// 3-sibling group, owner getter: C# implements only the OWNER has-setter
    /// sibling. Read via owner existential. Owner body's getter fan-out picks
    /// its own populated vtable.
    /// </summary>
    public void TestGetThroughOwnerHasSetterSibling()
    {
        var impl = new SiblingMutableTaggedOnlyImpl("owner-read");
        var result = Functions.ReadSiblingTagViaGetSet(impl);
        AssertEqual("owner-read", result, "Reading via owner has-setter sibling in 3-group");
    }

    // -- Reverse-order regression: dispatch through smaller sibling AFTER the
    //    larger sibling's proxy has already populated its vtable globally. The
    //    sibling vtables are process-wide `fileprivate var`s — once any proxy
    //    of the larger sibling has initialized, the owner branch of the fan-out
    //    is always taken. Without per-instance dispatch the owner receiver
    //    cannot locate a smaller-sibling proxy and silently returns "" (getter)
    //    or no-ops (setter). These tests prime each group's larger-sibling
    //    vtable first, then dispatch through the smaller sibling. --

    /// <summary>
    /// 2-sibling group, reverse order: prime MutableNamed vtable, then read
    /// through the smaller sibling. Must return the smaller impl's value, not
    /// fall through the owner branch and return "".
    /// </summary>
    public void TestGetThroughSmallerSiblingAfterLargerProxyRegistered()
    {
        // Prime: instantiating any SiblingMutableNamedOnlyImpl triggers
        // SiblingMutableNamedProxy.cctor → SetSiblingMutableNamed_vtable(...).
        // Read once to ensure the registration completes and is hot.
        var primer = new SiblingMutableNamedOnlyImpl("primer");
        _ = Functions.ReadSiblingNameViaGetSet(primer);

        var smaller = new SiblingNamedOnlyImpl("smaller-after-larger");
        var result = Functions.ReadSiblingNameViaGet(smaller);
        AssertEqual("smaller-after-larger", result,
            "Smaller sibling read must succeed after larger sibling's vtable was registered globally");
    }

    /// <summary>
    /// 3-sibling group, reverse order: prime BOTH has-setter siblings (owner
    /// MutableTagged + non-owner MutableTaggedAlt), then read through the
    /// get-only sibling.
    /// </summary>
    public void TestGetThroughGetOnlySiblingAfterLargerProxiesRegistered()
    {
        var primerOwner = new SiblingMutableTaggedOnlyImpl("owner-primer");
        _ = Functions.ReadSiblingTagViaGetSet(primerOwner);
        var primerAlt = new SiblingMutableTaggedAltOnlyImpl("alt-primer");
        _ = Functions.ReadSiblingTagViaGetSetAlt(primerAlt);

        var smaller = new SiblingTaggedOnlyImpl("get-only-after-larger");
        var result = Functions.ReadSiblingTagViaGet(smaller);
        AssertEqual("get-only-after-larger", result,
            "Get-only sibling read must succeed after has-setter siblings registered their vtables");
    }

    /// <summary>
    /// 3-sibling group, reverse order setter: prime the OWNER (MutableTagged),
    /// then write through the non-owner (MutableTaggedAlt). The owner's setter
    /// vtable is populated, so the fan-out's owner branch fires first. Without
    /// per-instance dispatch the owner setter receiver cannot find a
    /// MutableTaggedAlt proxy and silently no-ops the write.
    /// </summary>
    public void TestSetThroughNonOwnerSiblingAfterOwnerProxyRegistered()
    {
        var primer = new SiblingMutableTaggedOnlyImpl("primer");
        Functions.WriteSiblingTagViaGetSet(primer, "primer-write");

        var altImpl = new SiblingMutableTaggedAltOnlyImpl("before");
        Functions.WriteSiblingTagViaGetSetAlt(altImpl, "after");
        AssertEqual("after", altImpl.SiblingTag,
            "Non-owner sibling write must reach its own impl after owner's setter vtable was registered");
    }

    // -- Inheritance variant: Child refines Parent get-only requirement to
    //    get+set. The parser-level question is whether the inherited
    //    PropertyDecl is duplicated into Child's .Properties; if not, the
    //    sibling group has only one entry and the fan-out never triggers,
    //    leaving the original SIGSEGV shape exposed. --

    public void TestGetInheritedSiblingViaParentExistential()
    {
        var impl = new SiblingInheritedParentOnlyImpl("parent-only");
        var result = Functions.ReadInheritedSiblingViaParent(impl);
        AssertEqual("parent-only", result,
            "Read via parent (get-only) sibling existential of an inheritance group");
    }

    public void TestGetInheritedSiblingViaChildExistential()
    {
        var impl = new SiblingInheritedChildOnlyImpl("child-read");
        var result = Functions.ReadInheritedSiblingViaChild(impl);
        AssertEqual("child-read", result,
            "Read via child (get+set) sibling existential of an inheritance group");
    }

    public void TestSetInheritedSiblingViaChildExistential()
    {
        var impl = new SiblingInheritedChildOnlyImpl("before");
        Functions.WriteInheritedSiblingViaChild(impl, "after");
        AssertEqual("after", impl.InheritedSiblingValue,
            "Setter wrote through child sibling existential of inheritance group");
    }

    public void TestGetInheritedSiblingViaParentAfterChildRegistered()
    {
        var primer = new SiblingInheritedChildOnlyImpl("child-primer");
        _ = Functions.ReadInheritedSiblingViaChild(primer);

        var smaller = new SiblingInheritedParentOnlyImpl("parent-after-child");
        var result = Functions.ReadInheritedSiblingViaParent(smaller);
        AssertEqual("parent-after-child", result,
            "Parent read must succeed after child's vtable populated globally (reverse-order)");
    }

    // -- Multi-sibling impl: a single C# class implementing TWO sibling
    //    interfaces in the same group. Both vtables get populated for the
    //    same handle. The receiver fallback chain must locate the impl
    //    regardless of which branch Swift's fan-out picks first. --

    public void TestReadThroughBothSiblingsOnMultiImpl()
    {
        var impl = new SiblingFullImpl("both");
        var viaSmaller = Functions.ReadSiblingNameViaGet(impl);
        var viaLarger = Functions.ReadSiblingNameViaGetSet(impl);
        AssertEqual("both", viaSmaller, "Multi-sibling impl: read via smaller existential");
        AssertEqual("both", viaLarger, "Multi-sibling impl: read via larger existential");
    }

    public void TestSetThroughLargerOnMultiImpl()
    {
        var impl = new SiblingFullImpl("initial");
        Functions.WriteSiblingNameViaGetSet(impl, "updated");
        AssertEqual("updated", impl.SiblingName, "Multi-sibling impl: setter wrote through larger");
        AssertEqual("updated", Functions.ReadSiblingNameViaGet(impl),
            "Multi-sibling impl: subsequent read via smaller sees the write");
    }

    // -- Closure-property sibling group: dispatchable Optional<() -> Void>
    //    on two protocols (get-only + get+set). Pre-fix the closure-property
    //    emission path force-unwrapped the owner's nil vtable function pointer
    //    when dispatched through the smaller sibling — same SIGSEGV shape as
    //    the value-typed sibling case. --

    public void TestInvokeClosureSiblingThroughSmallerExistential()
    {
        int callCount = 0;
        var impl = new SiblingClosurePropertyOnlyImpl(() => callCount++);
        Functions.InvokeSiblingClosureViaGet(impl);
        AssertEqual(1, callCount,
            "Closure-property sibling: smaller existential invoked C# delegate exactly once");
    }

    public void TestInvokeClosureSiblingThroughLargerExistential()
    {
        int callCount = 0;
        var impl = new SiblingMutableClosurePropertyOnlyImpl(() => callCount++);
        Functions.InvokeSiblingClosureViaGetSet(impl);
        AssertEqual(1, callCount,
            "Closure-property sibling: larger existential invoked C# delegate exactly once");
    }

    public void TestInvokeClosureSiblingSmallerAfterLargerPrimed()
    {
        var primer = new SiblingMutableClosurePropertyOnlyImpl(() => { });
        Functions.InvokeSiblingClosureViaGetSet(primer);

        int callCount = 0;
        var smaller = new SiblingClosurePropertyOnlyImpl(() => callCount++);
        Functions.InvokeSiblingClosureViaGet(smaller);
        AssertEqual(1, callCount,
            "Closure-property sibling: smaller invocation must succeed after larger primed vtable");
    }

    public void TestSetClosureSiblingThroughLargerExistential()
    {
        var impl = new SiblingMutableClosurePropertyOnlyImpl(() => { });
        int swiftAssignedCallCount = 0;
        Functions.SetSiblingClosureViaGetSet(impl, () => swiftAssignedCallCount++);
        impl.SiblingClosure?.Invoke();
        AssertEqual(1, swiftAssignedCallCount,
            "Closure-property sibling: setter through larger replaced the impl delegate");
    }

    // -- Subscript sibling group --
    //
    // Two protocols declare subscript(siblingIndexKey:) with different accessor
    // sets. Same shape as the value-typed property sibling fix but on the
    // EmitSubscriptImplementation path. Pre-fan-out: dispatch through the smaller
    // sibling crashes because the owner body force-unwraps a nil vtable pointer.

    public void TestReadIndexedSiblingThroughSmallerExistential()
    {
        var impl = new SiblingIndexedOnlyImpl();
        impl.Storage[42] = "small";
        var result = Functions.ReadSiblingIndexedViaGet(impl, 42);
        AssertEqual("small", result, "Subscript sibling: read via smaller existential returns C# impl value");
    }

    public void TestReadIndexedSiblingThroughLargerExistential()
    {
        var impl = new SiblingMutableIndexedOnlyImpl();
        impl.Storage[7] = "big";
        var result = Functions.ReadSiblingIndexedViaGetSet(impl, 7);
        AssertEqual("big", result, "Subscript sibling: read via larger existential returns C# impl value");
    }

    public void TestWriteIndexedSiblingThroughLargerExistential()
    {
        var impl = new SiblingMutableIndexedOnlyImpl();
        Functions.WriteSiblingIndexedViaGetSet(impl, 3, "written");
        AssertEqual("written", impl.Storage[3],
            "Subscript sibling: setter through larger writes into C# impl storage");
    }

    public void TestReadIndexedSiblingSmallerAfterLargerProxyRegistered()
    {
        var larger = new SiblingMutableIndexedOnlyImpl();
        Functions.WriteSiblingIndexedViaGetSet(larger, 1, "primed");
        var smaller = new SiblingIndexedOnlyImpl();
        smaller.Storage[9] = "smallpost";
        var result = Functions.ReadSiblingIndexedViaGet(smaller, 9);
        AssertEqual("smallpost", result,
            "Subscript sibling: smaller read must succeed after larger sibling primed vtable");
    }

    // MARK: Divergent argument labels
    //
    // SiblingLabelAt and SiblingLabelBy have identical index type / return type but
    // different external argument labels (`at:` vs `by:`). They must NOT be grouped
    // as siblings — each must own its own subscript body. Pre-fix the keys collided
    // and one extension produced an unimplemented witness, breaking dispatch.

    public void TestReadSiblingLabelAt()
    {
        var impl = new SiblingLabelAtOnlyImpl();
        impl.Storage[3] = "at-three";
        var result = Functions.ReadSiblingLabelAt(impl, 3);
        AssertEqual("at-three", result,
            "Divergent-label subscript: at-label witness dispatches into C# impl");
    }

    public void TestReadSiblingLabelBy()
    {
        var impl = new SiblingLabelByOnlyImpl();
        impl.Storage[5] = "by-five";
        var result = Functions.ReadSiblingLabelBy(impl, 5);
        AssertEqual("by-five", result,
            "Divergent-label subscript: by-label witness dispatches into C# impl");
    }

    public void TestWriteSiblingLabelBy()
    {
        var impl = new SiblingLabelByOnlyImpl();
        Functions.WriteSiblingLabelBy(impl, 7, "by-seven");
        AssertEqual("by-seven", impl.Storage[7],
            "Divergent-label subscript: by-label setter writes into C# impl storage");
    }

    // External-label edge cases (Grok r3 Critical + High):
    //   - `default:` is a Swift keyword → emitter must backtick-escape, otherwise
    //     the witness signature fails to compile.
    //   - `index0:` collides with the parser's synthetic placeholder for unlabeled
    //     params → emitter must dispatch via IsUnlabeledSubscriptIndex flag, NOT
    //     a pattern match on the name, otherwise the witness is emitted unlabeled.

    public void TestReadSiblingLabelKeyword()
    {
        var impl = new SiblingLabelKeywordOnlyImpl();
        impl.Storage[11] = "kw-eleven";
        var result = Functions.ReadSiblingLabelKeyword(impl, 11);
        AssertEqual("kw-eleven", result,
            "Keyword external label (`default:`) is backtick-escaped in emitted witness");
    }

    public void TestReadSiblingLabelLooksLikeSynthetic()
    {
        var impl = new SiblingLabelLooksLikeSyntheticOnlyImpl();
        impl.Storage[13] = "ls-thirteen";
        var result = Functions.ReadSiblingLabelLooksLikeSynthetic(impl, 13);
        AssertEqual("ls-thirteen", result,
            "User-written `index0:` external label is preserved, not mistaken for synthetic placeholder");
    }

    // Free-function keyword-label edge case (Grok r4 Medium): the @_cdecl wrapper's
    // method-call path (CdeclParamMapper.BuildSwiftCallArgLabel) must backtick-escape
    // `default` instead of emitting a bare `default:` argument label, which is a Swift
    // syntax error. The fact that this binding compiles + dispatches proves the escape.

    public void TestFreeFunctionWithKeywordLabel()
    {
        var result = Functions.FreeFunctionWithKeywordLabel(7);
        AssertEqual(21L, (long)result,
            "Free function with Swift-keyword external label (`default:`) dispatches correctly");
    }

    // r6 phantom-owner regression: mixed-generic protocol must not win sibling
    // ownership. Both PhantomOwner protocols declare `phantomName: String { get set }`
    // so the OrderByDescending(HasSetter) keeps both in the tie and lex tie-break
    // (Generic < Regular) made the mixed-generic the pre-r6 owner — and mixed-
    // generic protocols emit fatalError() stubs for every property because the
    // type-projection pipeline can't render non-generic members correctly while
    // method-level generics are in scope. Dispatch through PhantomOwnerRegular
    // thus reached the stub. r6's IsEmittable filter removes mixed-generic from
    // the sibling-plan input so PhantomOwnerRegular owns its own body standalone;
    // PhantomOwnerMixedGeneric's empty extension picks up the witness via Swift's
    // cross-extension resolution, so both conformances succeed and dispatch through
    // either existential lands on the real C# impl.

    public void TestReadPhantomNameThroughRegularSibling()
    {
        var impl = new PhantomOwnerRegularOnlyImpl("phantom-value");
        var result = Functions.ReadPhantomNameViaRegular(impl);
        AssertEqual("phantom-value", result,
            "Mixed-generic protocol must not own sibling group — dispatch through regular sibling hits its real body, not the mixed-generic fatalError stub");
    }

    public void TestWritePhantomNameThroughRegularSibling()
    {
        var impl = new PhantomOwnerRegularOnlyImpl("initial");
        Functions.WritePhantomNameViaRegular(impl, "after-write");
        AssertEqual("after-write", impl.PhantomName,
            "Mixed-generic protocol must not own sibling setter — setter dispatch through regular sibling hits its real body, not the stub");
    }

    // Mixed-generic under-detection (Grok H1). CombinedMixedSelfGeneric's only
    // generic method has BOTH τ_1_* (T) AND Self in the signature. The original
    // IsMixedGenericProtocol predicate routed through HasOnlyMethodLevelGenerics,
    // which short-circuited on Self → false → the protocol was not classified
    // mixed-generic, slipped past IsEmittable, and could win the sibling-group
    // lex tie-break (Mixed < Regular). A C# impl that only implements
    // CombinedRegularSibling would then leave the mixed vtable nil while Swift
    // dispatch through the regular existential routed via CEWR into the mixed-
    // owned body — SIGSEGV. Post-fix HasMethodLevelGenericInSignature ignores
    // Self and catches the τ_1_* leg, so CombinedRegularSibling owns its body
    // standalone and dispatch lands on the C# impl's real value.

    public void TestReadCombinedNameThroughRegularSibling()
    {
        var impl = new CombinedRegularSiblingOnlyImpl("combined-read");
        var result = Functions.ReadCombinedNameViaRegular(impl);
        AssertEqual("combined-read", result,
            "Mixed-generic (Self+τ_1_*) protocol must not own sibling group — read dispatch through regular sibling hits its real body");
    }

    public void TestWriteCombinedNameThroughRegularSibling()
    {
        var impl = new CombinedRegularSiblingOnlyImpl("before");
        Functions.WriteCombinedNameViaRegular(impl, "after-combined");
        AssertEqual("after-combined", impl.CombinedName,
            "Mixed-generic (Self+τ_1_*) protocol must not own sibling setter — setter dispatch hits real body");
    }
}

internal class SiblingNamedOnlyImpl : ISiblingNamed
{
    public SiblingNamedOnlyImpl(string name) { SiblingName = name; }
    public string SiblingName { get; }
}

internal class SiblingMutableNamedOnlyImpl : ISiblingMutableNamed
{
    public SiblingMutableNamedOnlyImpl(string name) { SiblingName = name; }
    public string SiblingName { get; set; }
}

internal class SiblingTaggedOnlyImpl : ISiblingTagged
{
    public SiblingTaggedOnlyImpl(string tag) { SiblingTag = tag; }
    public string SiblingTag { get; }
}

internal class SiblingMutableTaggedOnlyImpl : ISiblingMutableTagged
{
    public SiblingMutableTaggedOnlyImpl(string tag) { SiblingTag = tag; }
    public string SiblingTag { get; set; }
}

internal class SiblingMutableTaggedAltOnlyImpl : ISiblingMutableTaggedAlt
{
    public SiblingMutableTaggedAltOnlyImpl(string tag) { SiblingTag = tag; }
    public string SiblingTag { get; set; }
}

internal class SiblingInheritedParentOnlyImpl : ISiblingInheritedParent
{
    public SiblingInheritedParentOnlyImpl(string v) { InheritedSiblingValue = v; }
    public string InheritedSiblingValue { get; }
}

internal class SiblingInheritedChildOnlyImpl : ISiblingInheritedChild
{
    public SiblingInheritedChildOnlyImpl(string v) { InheritedSiblingValue = v; }
    public string InheritedSiblingValue { get; set; }
}

internal class SiblingFullImpl : ISiblingNamed, ISiblingMutableNamed
{
    public SiblingFullImpl(string name) { SiblingName = name; }
    public string SiblingName { get; set; }
}

internal class SiblingClosurePropertyOnlyImpl : ISiblingClosureProperty
{
    public SiblingClosurePropertyOnlyImpl(Action? closure) { SiblingClosure = closure; }
    public Action? SiblingClosure { get; }
}

internal class SiblingMutableClosurePropertyOnlyImpl : ISiblingMutableClosureProperty
{
    public SiblingMutableClosurePropertyOnlyImpl(Action? closure) { SiblingClosure = closure; }
    public Action? SiblingClosure { get; set; }
}

internal class SiblingIndexedOnlyImpl : ISiblingIndexed
{
    public Dictionary<nint, string> Storage { get; } = new();
    public string this[nint siblingIndexKey] => Storage.TryGetValue(siblingIndexKey, out var v) ? v : "";
}

internal class SiblingMutableIndexedOnlyImpl : ISiblingMutableIndexed
{
    public Dictionary<nint, string> Storage { get; } = new();
    public string this[nint siblingIndexKey]
    {
        get => Storage.TryGetValue(siblingIndexKey, out var v) ? v : "";
        set => Storage[siblingIndexKey] = value;
    }
}

internal class SiblingLabelAtOnlyImpl : ISiblingLabelAt
{
    public Dictionary<nint, string> Storage { get; } = new();
    public string this[nint siblingIndexKey] => Storage.TryGetValue(siblingIndexKey, out var v) ? v : "";
}

internal class SiblingLabelByOnlyImpl : ISiblingLabelBy
{
    public Dictionary<nint, string> Storage { get; } = new();
    public string this[nint siblingIndexKey]
    {
        get => Storage.TryGetValue(siblingIndexKey, out var v) ? v : "";
        set => Storage[siblingIndexKey] = value;
    }
}

internal class SiblingLabelKeywordOnlyImpl : ISiblingLabelKeyword
{
    public Dictionary<nint, string> Storage { get; } = new();
    public string this[nint siblingIndexKey] => Storage.TryGetValue(siblingIndexKey, out var v) ? v : "";
}

internal class SiblingLabelLooksLikeSyntheticOnlyImpl : ISiblingLabelLooksLikeSynthetic
{
    public Dictionary<nint, string> Storage { get; } = new();
    public string this[nint siblingIndexKey] => Storage.TryGetValue(siblingIndexKey, out var v) ? v : "";
}

internal class PhantomOwnerRegularOnlyImpl : IPhantomOwnerRegular
{
    public PhantomOwnerRegularOnlyImpl(string name) { PhantomName = name; }
    public string PhantomName { get; set; }
}

internal class CombinedRegularSiblingOnlyImpl : ICombinedRegularSibling
{
    public CombinedRegularSiblingOnlyImpl(string name) { CombinedName = name; }
    public string CombinedName { get; set; }
}
