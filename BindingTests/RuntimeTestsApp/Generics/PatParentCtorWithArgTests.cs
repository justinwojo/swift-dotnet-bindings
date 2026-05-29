// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime regression for the parent-generic constructor-with-args CSM path.
/// <para>
/// <c>KeyedBag&lt;Item: KeyTag&gt;</c> declares two non-throwing <c>init</c>s
/// whose only parameters are concrete (non-generic) Swift types — a single
/// <c>Swift.String</c> arg and the <c>String + Int32</c> mixed pair. This
/// mirrors the CryptoKit <c>HMAC&lt;H : HashFunction&gt;(key: SymmetricKey)</c>
/// shape that the apple-framework gap campaign needs to confirm working.
/// </para>
/// <para>
/// The CSM emitter routes parent-generic non-throwing ctors through
/// <c>TryEmitConcreteOverload</c> as static factories on the per-conformer
/// <c>{Type}{Conformer}CsmExtensions</c> partial class, named
/// <c>From{Conformer}(<i>concrete args</i>)</c>. Each factory dispatches to a
/// dedicated <c>SBW_CSM_..._init_*</c> <c>@_cdecl</c> wrapper with no
/// PAT-witness metadata — the same closed-form ABI as the existing zero-arg
/// factories (<c>FromStringTagger()</c>, <c>FromStringCubby()</c>). These
/// tests lock that emission contract: both conformers, both arities, plus
/// witness reads via <c>length()</c> to prove ctor args actually landed in
/// the per-conformer-specialized payload.
/// </para>
/// </summary>
public class PatParentCtorWithArgTests : TestBase
{
    public PatParentCtorWithArgTests(TestResults results) : base(results) { }

    public void TestKeyedBagStringKeyTag_SingleArgCtorFactoryRoundTrips()
    {
        // FromStringKeyTag(string) is the canonical HMAC<H>(SymmetricKey)
        // shape: a non-throwing parent-generic ctor with one concrete arg.
        // The factory must route through SBW_CSM_..._init_* (not the open
        // generic BoundGenericsHandler path) and the resulting payload must
        // reflect the seed length.
        using var bag = KeyedBagStringKeyTagCsmExtensions.FromStringKeyTag("alpha");
        AssertEqual(5, bag.Length(), "FromStringKeyTag(\"alpha\") seedLength is 5");
    }

    public void TestKeyedBagStringKeyTag_TwoArgCtorFactoryRoundTrips()
    {
        // FromStringKeyTag(string, int) closes the multi-arg threading path:
        // String + Int32 mixed-category args through one CSM @_cdecl shim.
        // length() returns seedLength + bonus, so a correct ctor round-trip
        // produces 5 (length of "alpha") + 7 = 12.
        using var bag = KeyedBagStringKeyTagCsmExtensions.FromStringKeyTag("alpha", 7);
        AssertEqual(12, bag.Length(), "FromStringKeyTag(\"alpha\", 7) length() is 12");
    }

    public void TestKeyedBagIntKeyTag_SecondConformerEmitsIndependently()
    {
        // Per-closed-conformer CSM emission: IntKeyTag must produce its own
        // KeyedBagIntKeyTagCsmExtensions class with the same factory shape,
        // independently of the StringKeyTag specialization. Each closed
        // conformer's ctor must dispatch to its own @_cdecl wrapper.
        using var bag = KeyedBagIntKeyTagCsmExtensions.FromIntKeyTag("beta", 4);
        AssertEqual(8, bag.Length(), "FromIntKeyTag(\"beta\", 4) length() is 8");
    }

    public void TestKeyedBag_CrossConformerInstancesAreIndependent()
    {
        // Two different closed conformers must not alias each other; the
        // separate CSM extension classes must produce instances with
        // independent backing storage. Catches a regression where two
        // conformers' factories could share a stale specialization payload.
        using var s = KeyedBagStringKeyTagCsmExtensions.FromStringKeyTag("xx", 1);
        using var i = KeyedBagIntKeyTagCsmExtensions.FromIntKeyTag("yyyy", 2);
        AssertEqual(3, s.Length(), "StringKeyTag bag length() is 3");
        AssertEqual(6, i.Length(), "IntKeyTag bag length() is 6");
    }

    public void TestKeyedBag_MultipleInstancesPerConformerAreInstanceLocal()
    {
        // Two instances of the same closed conformer must not share state —
        // each FromStringKeyTag() must allocate its own payload through the
        // CSM ctor wrapper, not return aliased storage.
        using var a = KeyedBagStringKeyTagCsmExtensions.FromStringKeyTag("abc");
        using var b = KeyedBagStringKeyTagCsmExtensions.FromStringKeyTag("longer");
        AssertEqual(3, a.Length(), "Instance A length() is 3");
        AssertEqual(6, b.Length(), "Instance B length() is 6");
    }
}
