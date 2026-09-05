// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// An optional subscript index, driven from Swift with an absent index and with a present zero.
///
/// <para>
/// A subscript index is an ordinary reverse-dispatch input and has to be read the way a method
/// parameter is. Read as a raw <c>SwiftOptional&lt;int&gt;</c> carrier and handed straight to the
/// managed indexer, an absent index takes that type's <c>implicit operator T?</c> — declared on an
/// unconstrained <c>T</c>, so just <c>T</c> in IL — and a <c>.none</c> arrives as
/// <c>default(int)</c> widened into an <c>int?</c> whose <c>HasValue</c> is <c>true</c>. Swift's
/// <c>nil</c> then reaches the implementation as a present <c>0</c>, which is exactly what
/// <c>.some(0)</c> looks like.
/// </para>
///
/// <para>
/// Every assertion here is written so those two cannot share a passing result: the implementation
/// records the key it actually received and answers each distinct key with a distinct value, and a
/// nonzero index sits alongside both so a receiver that lost the index entirely fails too.
/// </para>
/// </summary>
public class OptionalIndexSubscriptTests : TestBase
{
    public OptionalIndexSubscriptTests(TestResults results) : base(results) { }

    private const int NilAnswer = 111;
    private const int ZeroAnswer = 222;
    private const int NonZeroKey = 7;
    private const int NonZeroAnswer = 333;

    /// <summary>
    /// The getter. All three index shapes are driven through the same subscript and each must come
    /// back with its own answer — the nil case and the zero case are the pair the defect merged.
    /// </summary>
    public void TestGetterDistinguishesNilFromPresentZero()
    {
        var host = new OptionalIndexSubscriptHost();
        var del = new OptionalIndexSubscriptDelegateImpl();
        host.Delegate = del;

        AssertTrue(host.HasDelegate, "the weak delegate slot still resolves after assignment");

        var nilResult = host.ReadNil();
        var zeroResult = host.ReadSome(0);
        var nonZeroResult = host.ReadSome(NonZeroKey);

        AssertEqual(NilAnswer, nilResult,
            "Swift's nil index must reach the implementation as a null int?, not as a present 0 — " +
            "answering with the zero-key value is the collapse this gate exists for.");
        AssertEqual(ZeroAnswer, zeroResult, "a present 0 still means index 0");
        AssertEqual(NonZeroAnswer, nonZeroResult, "a present nonzero index is unaffected");

        AssertEqual(3, del.ObservedGetKeys.Count, "each getter call reached the implementation once");
        AssertFalse(del.ObservedGetKeys[0].HasValue, "the first call carried an ABSENT index");
        AssertEqual(0, del.ObservedGetKeys[1] ?? -1, "the second carried a present 0");
        AssertEqual(NonZeroKey, del.ObservedGetKeys[2] ?? -1, "the third carried a present 7");

        GC.KeepAlive(del);
    }

    /// <summary>
    /// The setter — a separate emission site, and the one whose VALUE already ran through the
    /// conversion pipeline while its INDEX did not. Both halves of one subscript have to agree
    /// about what nil means or a round trip through it cannot hold.
    /// </summary>
    public void TestSetterDistinguishesNilFromPresentZero()
    {
        var host = new OptionalIndexSubscriptHost();
        var del = new OptionalIndexSubscriptDelegateImpl();
        host.Delegate = del;

        host.WriteNil(11);
        host.WriteSome(0, 22);
        host.WriteSome(NonZeroKey, 33);

        AssertEqual(3, del.ObservedSets.Count, "each setter call reached the implementation once");

        AssertFalse(del.ObservedSets[0].Key.HasValue,
            "Swift's nil index must arrive as a null int? on the setter too; a present 0 here is " +
            "indistinguishable from writing at index 0.");
        AssertEqual(11, del.ObservedSets[0].Value, "and it carried its own value");

        AssertEqual(0, del.ObservedSets[1].Key ?? -1, "the present 0 stayed a present 0");
        AssertEqual(22, del.ObservedSets[1].Value, "with its own value");

        AssertEqual(NonZeroKey, del.ObservedSets[2].Key ?? -1, "the present 7 arrived intact");
        AssertEqual(33, del.ObservedSets[2].Value, "with its own value");

        GC.KeepAlive(del);
    }

    /// <summary>
    /// Getter and setter over one implementation: what the setter stored under nil must be what the
    /// getter reads back under nil, and never what index 0 holds. This is the assertion a consumer
    /// would actually write, and it fails whichever accessor loses the distinction.
    /// </summary>
    public void TestNilKeyedWriteIsReadBackUnderNilOnly()
    {
        var host = new OptionalIndexSubscriptHost();
        var del = new OptionalIndexSubscriptDelegateImpl();
        host.Delegate = del;

        host.WriteNil(910);
        host.WriteSome(0, 920);

        AssertEqual(910, del.StoredForNil, "the nil-keyed write landed in the nil slot");
        AssertEqual(920, del.StoredForZero, "the zero-keyed write landed in the zero slot");

        del.AnswerFromStore = true;
        AssertEqual(910, host.ReadNil(), "reading under nil returns what nil was written with");
        AssertEqual(920, host.ReadSome(0), "reading under 0 returns what 0 was written with");

        GC.KeepAlive(del);
    }

    /// <summary>
    /// Records exactly what each accessor received, and answers distinct keys with distinct values
    /// so no two index shapes can share a result.
    /// </summary>
    private sealed class OptionalIndexSubscriptDelegateImpl : IOptionalIndexSubscriptDelegate
    {
        public List<int?> ObservedGetKeys { get; } = new List<int?>();

        public List<(int? Key, int Value)> ObservedSets { get; } = new List<(int?, int)>();

        public int StoredForNil { get; private set; }

        public int StoredForZero { get; private set; }

        /// <summary>When set, the getter replays the stored values instead of the fixed answers.</summary>
        public bool AnswerFromStore { get; set; }

        public int this[int? index0]
        {
            get
            {
                ObservedGetKeys.Add(index0);
                if (AnswerFromStore)
                    return index0.HasValue ? (index0.Value == 0 ? StoredForZero : 0) : StoredForNil;
                if (!index0.HasValue)
                    return NilAnswer;
                return index0.Value == 0 ? ZeroAnswer : NonZeroAnswer;
            }
            set
            {
                ObservedSets.Add((index0, value));
                if (!index0.HasValue)
                    StoredForNil = value;
                else if (index0.Value == 0)
                    StoredForZero = value;
            }
        }
    }
}
