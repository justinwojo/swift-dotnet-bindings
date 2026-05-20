// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime regression for CSM admission of <c>Swift.String</c> parameters on
/// parent-only methods — sibling of <see cref="PatParentOnlyMethodsTests"/>
/// (sync) and <see cref="PatParentAsyncMethodsTests"/> (async).
/// <para>
/// <c>TaggedBag&lt;Item: Tagger&gt;</c> declares three sync instance methods
/// — <c>tag(_:)</c>, <c>tagWithBonus(_:bonus:)</c>, <c>length()</c> — plus
/// one async instance method <c>measure(_:)</c>. Every admission-gated method
/// takes at least one <c>Swift.String</c> parameter (the canonical
/// <c>Utf8Slice</c> ABI category). Before the admission lift, the CSM
/// engine's <c>AreNonGenericParamsCompatible</c> predicate and its sync /
/// async sibling allowlists only accepted <c>Primitive</c>, <c>ObjCHandle</c>,
/// and <c>PayloadHandle</c>; <c>Utf8Slice</c> was rejected and these methods
/// fell back to the BoundGenericsHandler path that crashes Mono JIT on
/// <c>GenericContainer.count()/tagBytes()</c>.
/// </para>
/// <para>
/// After the lift, each closed conformer (<c>StringTagger</c>,
/// <c>IntTagger</c>) gets its own static extension class with a proper
/// <c>@_cdecl</c> wrapper that consumes the <c>SBW_Utf8Slice</c> argument
/// directly. The tests assert the methods emit at all, round-trip the
/// string-length witness through <c>self_</c> mutation, mix <c>Utf8Slice</c>
/// with <c>Primitive</c> in a single signature, span sync and async bridges,
/// and stay independent across conformers.
/// </para>
/// </summary>
public class PatParentStringMethodsTests : TestBase
{
    public PatParentStringMethodsTests(TestResults results) : base(results) { }

    public void TestTaggedBagStringTagger_DefaultLengthIsZero()
    {
        using var bag = Functions.MakeTaggedBagStringTagger();
        AssertEqual(0, bag.Length(), "Default lastTagLength is 0");
    }

    public void TestTaggedBagStringTagger_TagWithStringParamRoundTrips()
    {
        // The minimum-viable Utf8Slice CSM admission case: a parent-only
        // mutating method taking a single Swift.String parameter, with a
        // primitive read-back witness on the same instance. Before the
        // admission lift this method never reached the per-conformer
        // extension emission path; after the lift it dispatches through
        // TaggedBagStringTaggerCsmExtensions with an SBW_Utf8Slice arg.
        using var bag = Functions.MakeTaggedBagStringTagger();
        bag.Tag(new SwiftString("hello"));
        AssertEqual(5, bag.Length(), "Length after Tag(\"hello\") is 5");
        bag.Tag(new SwiftString("hi"));
        AssertEqual(2, bag.Length(), "Length after Tag(\"hi\") is 2");
    }

    public void TestTaggedBagStringTagger_MixedUtf8SliceAndPrimitiveParams()
    {
        // The mixed-category case: TagWithBonus takes a String (Utf8Slice)
        // AND an Int32 (Primitive) and returns Int32. Exercises that the
        // admission allowlist accepts BOTH categories in a single method
        // signature — not just Utf8Slice in isolation. Witnesses both the
        // returned Int32 (recomputed length + bonus) AND the mutated self
        // via Length() on the same instance.
        using var bag = Functions.MakeTaggedBagStringTagger();
        var first = bag.TagWithBonus(new SwiftString("abc"), 10);
        AssertEqual(13, first, "TagWithBonus(\"abc\", 10) returns 13");
        AssertEqual(13, bag.Length(), "Length after TagWithBonus is 13");

        var second = bag.TagWithBonus(new SwiftString("hello world"), 1);
        AssertEqual(12, second, "TagWithBonus(\"hello world\", 1) returns 12");
        AssertEqual(12, bag.Length(), "Length after second TagWithBonus is 12");
    }

    public void TestTaggedBagIntTagger_SecondConformerAdmitsUtf8SliceIndependently()
    {
        // Cross-conformer admission: IntTagger is the second hint-resolved
        // conformer of Tagger and gets its own TaggedBagIntTaggerCsmExtensions
        // class. The Utf8Slice param admission must hold for both conformers,
        // not just StringTagger — confirms the allowlist is structural (it
        // depends on the param's ABI category) and not coincidentally
        // gated on the conformer's own associated-type shape.
        using var bag = Functions.MakeTaggedBagIntTagger();
        AssertEqual(0, bag.Length(), "IntTagger bag default length is 0");
        bag.Tag(new SwiftString("test"));
        AssertEqual(4, bag.Length(), "IntTagger bag length after Tag(\"test\") is 4");
        var ret = bag.TagWithBonus(new SwiftString("ok"), 100);
        AssertEqual(102, ret, "IntTagger TagWithBonus(\"ok\", 100) returns 102");
        AssertEqual(102, bag.Length(), "IntTagger length after TagWithBonus is 102");
    }

    public void TestTaggedBag_CrossConformerInstancesAreIndependent()
    {
        // Different closed conformers — different extension classes — must
        // not alias each other on the Utf8Slice path. Mutating one closed
        // instantiation through a String arg must not perturb the other.
        using var s = Functions.MakeTaggedBagStringTagger();
        using var i = Functions.MakeTaggedBagIntTagger();
        s.Tag(new SwiftString("alpha"));
        i.Tag(new SwiftString("beta-gamma"));
        AssertEqual(5, s.Length(), "StringTagger bag retains 5 independently");
        AssertEqual(10, i.Length(), "IntTagger bag retains 10 independently");
    }

    public async Task TestTaggedBagStringTagger_MeasureAsyncWithUtf8SlicePassthrough()
    {
        // Async sibling of the Utf8Slice admission case: the async
        // method-generic bridge's IsCsmAsyncEligibleForGenericParent shares
        // the same admission allowlist via IsAbiCategoryPassable. The String
        // argument must reach the per-conformer async @_cdecl wrapper and
        // the awaited Int32 result must round-trip the byte length.
        using var bag = Functions.MakeTaggedBagStringTagger();
        var len = await WithTimeout(bag.MeasureAsync(new SwiftString("async-utf8")), DefaultAsyncTimeout);
        AssertEqual((nint)10, (nint)len, "MeasureAsync(\"async-utf8\") returns 10");
    }

    public async Task TestTaggedBagIntTagger_MeasureAsyncOnSecondConformer()
    {
        // Async cross-conformer admission: the second conformer's async
        // extension must emit independently with the same Utf8Slice arg
        // handling. Confirms the async admission predicate is conformer-
        // agnostic on the param ABI category.
        using var bag = Functions.MakeTaggedBagIntTagger();
        var len = await WithTimeout(bag.MeasureAsync(new SwiftString("hi")), DefaultAsyncTimeout);
        AssertEqual((nint)2, (nint)len, "IntTagger MeasureAsync(\"hi\") returns 2");
    }
}
