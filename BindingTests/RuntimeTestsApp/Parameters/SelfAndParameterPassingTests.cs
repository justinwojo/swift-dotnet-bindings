// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Threading;

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// Pairs with <c>BindingTests/Sources/SwiftBindingsTestLib/Parameters/SelfAndParameterPassing.swift</c>.
/// Swift takes self and each argument by a convention chosen per (member, type): the same small
/// frozen struct is borrowed by a getter, passed inout to a setter or mutating method, and consumed
/// by a consuming method; a struct of more than four register pieces is passed by address even to
/// a nonmutating member. Every case checks the value Swift computes from what it received and, for
/// mutating paths, that the change is visible on the caller's copy.
/// <para>The carrier cases hold a class reference, so a missing or extra retain or release on any
/// guaranteed, owned or inout path shows up in the token's per-instance destruction count. The C#
/// token handle is dropped first in each case, so the carrier's copy is the only one left and the
/// count is exact.</para>
/// </summary>
public class SelfAndParameterPassingTests : TestBase
{
    public SelfAndParameterPassingTests(TestResults results) : base(results) { }

    // Ids are unique per case, so a count from one case cannot leak into another.
    private static int s_nextTokenId = 61_000;
    private static int NextTokenId() => Interlocked.Increment(ref s_nextTokenId);

    private static int Deaths(int id) => (int)TestLibFunctions.PassingTokenDeathCount(id);

    /// <summary>A carrier whose token is held by the carrier alone.</summary>
    private static PassingCarrier CarrierOwningToken(int id, int tag)
    {
        using var token = new PassingToken(id);
        return new PassingCarrier(token, tag);
    }

    #region Small frozen self (two pieces)

    public void TestSmallSelfGetterReadsBothFields()
    {
        var pair = new PassingPair(1, 2);
        AssertEqual(1002, pair.Weighted, "getter sees both fields of a lowered self");
        AssertEqual(3, pair.Total, "second getter on the same self");
    }

    public void TestSmallSelfMethodTakesArgumentThenSelf()
    {
        var pair = new PassingPair(4, 5);
        AssertEqual((nint)4012, pair.WeightedPlus(7), "nonmutating method with an argument before self");
    }

    public void TestSmallSelfSetterWritesBackToCaller()
    {
        var pair = new PassingPair(1, 2);
        pair.Total = 10;
        AssertEqual(1, pair.First, "setter keeps the field it does not assign");
        AssertEqual(9, pair.Second, "setter's write through the inout self reaches the caller");
        AssertEqual(10, pair.Total, "getter reads the setter's result");
    }

    public void TestSmallSelfMutatingMethodWritesBackToCaller()
    {
        var pair = new PassingPair(1, 2);
        pair.Shift(3);
        AssertEqual(4, pair.First, "mutating method's first write reaches the caller");
        AssertEqual(-1, pair.Second, "mutating method's second write reaches the caller");
        pair.Shift(3);
        AssertEqual(7, pair.First, "a second mutation starts from the first one's result");
        AssertEqual(-4, pair.Second, "a second mutation starts from the first one's result");
    }

    public void TestSmallSelfConsumingMethodLeavesCallerCopy()
    {
        var pair = new PassingPair(4, 6);
        AssertEqual((nint)4013, pair.Drain(7), "consuming method sees self and its argument");
        AssertEqual(4, pair.First, "consuming a copy leaves the caller's value intact");
        AssertEqual(6, pair.Second, "consuming a copy leaves the caller's value intact");
        AssertEqual((nint)4013, pair.Drain(7), "the caller's value can be consumed again");
    }

    #endregion

    #region Wide frozen self (five pieces)

    public void TestWideSelfGetterReadsEveryField()
    {
        var wide = new PassingWide(1, 2, 3, 4, 5);
        AssertEqual(54321, wide.Checksum, "getter reads all five fields of an indirect self");
        AssertEqual((nint)54330, wide.ChecksumPlus(9), "nonmutating method with an argument and an indirect self");
    }

    public void TestWideSelfSetterAndMutatingWriteBack()
    {
        var wide = new PassingWide(1, 2, 3, 4, 5);
        wide.Head = 7;
        AssertEqual(7, wide.A, "setter writes through the indirect self");
        AssertEqual(54327, wide.Checksum, "setter leaves the other fields");
        wide.Scale(2);
        AssertEqual(14, wide.A, "mutating method scales the first field");
        AssertEqual(10, wide.E, "mutating method scales the last field");
        AssertEqual(108654, wide.Checksum, "mutating method scales every field");
    }

    public void TestWideSelfConsumingMethodLeavesCallerCopy()
    {
        var wide = new PassingWide(1, 2, 3, 4, 5);
        AssertEqual((nint)54328, wide.Drain(7), "consuming method sees every field of an owned indirect self");
        AssertEqual(54321, wide.Checksum, "consuming a copy leaves the caller's value intact");
    }

    public void TestFiveFloatPiecesSelfGetterAndMutating()
    {
        // 24 bytes, but five floating-point pieces: indirect by piece count.
        var floats = new PassingMixedFloats(1, 2, 3, 4, 5);
        AssertEqual(54321.0, floats.Sum, "getter reads all five floating-point fields");
        floats.Bump();
        AssertEqual(2f, floats.X, "mutating method's first write reaches the caller");
        AssertEqual(6.0, floats.T, "mutating method's last write reaches the caller");
        AssertEqual(65432.0, floats.Sum, "every field moved by one");
    }

    #endregion

    #region Non-trivial self: borrowed, inout, consumed

    public void TestNonTrivialSelfBorrowedKeepsTheReference()
    {
        int id = NextTokenId();
        var carrier = CarrierOwningToken(id, 3);
        try
        {
            AssertEqual(0, Deaths(id), "the carrier keeps the token alive");
            for (int i = 0; i < 4; i++)
                AssertEqual((nint)(id * 1000 + 3), carrier.GetDescribe(), $"borrowed self, call {i}");
            AssertEqual(0, Deaths(id), "borrowing self never releases the caller's reference");
        }
        finally { carrier.Dispose(); }
        AssertEqual(1, Deaths(id), "disposing the carrier destroys the token once");
        carrier.Dispose();
        AssertEqual(1, Deaths(id), "repeated disposal is inert");
    }

    public void TestNonTrivialSelfMutatingRetagKeepsTheReference()
    {
        int id = NextTokenId();
        var carrier = CarrierOwningToken(id, 3);
        try
        {
            carrier.Retag(8);
            AssertEqual(8, carrier.Tag, "inout self's mutation reaches the caller");
            AssertEqual((nint)(id * 1000 + 8), carrier.GetDescribe(), "the mutated value is what Swift reads next");
            AssertEqual(0, Deaths(id), "an inout self that keeps its reference releases nothing");
        }
        finally { carrier.Dispose(); }
        AssertEqual(1, Deaths(id), "disposing the carrier destroys the token once");
    }

    public void TestNonTrivialSelfMutatingSwapReleasesTheOldReferenceOnce()
    {
        int oldId = NextTokenId();
        int newId = NextTokenId();
        var carrier = CarrierOwningToken(oldId, 5);
        try
        {
            using (var replacement = new PassingToken(newId))
                carrier.SwapToken(replacement);
            AssertEqual(1, Deaths(oldId), "the replaced token loses its only reference and dies once");
            AssertEqual(0, Deaths(newId), "the inout self now owns the replacement");
            AssertEqual((nint)(newId * 1000 + 5), carrier.GetDescribe(), "Swift reads the replacement");
        }
        finally { carrier.Dispose(); }
        AssertEqual(1, Deaths(oldId), "the old token is not destroyed again");
        AssertEqual(1, Deaths(newId), "disposing the carrier destroys the replacement once");
    }

    public void TestNonTrivialSelfConsumedLeavesCallerReference()
    {
        int id = NextTokenId();
        var carrier = CarrierOwningToken(id, 4);
        try
        {
            for (int i = 0; i < 3; i++)
                AssertEqual((nint)(id * 1000 + 4), carrier.Finish(), $"consuming method, call {i}");
            AssertEqual(0, Deaths(id), "each consumed copy is balanced; the caller's reference survives");
            AssertEqual((nint)(id * 1000 + 4), carrier.GetDescribe(), "the caller's carrier is still usable");
        }
        finally { carrier.Dispose(); }
        AssertEqual(1, Deaths(id), "disposing the carrier destroys the token once");
    }

    #endregion

    #region Non-trivial by-value arguments: guaranteed, owned, inout

    public void TestGuaranteedArgumentKeepsTheReference()
    {
        int id = NextTokenId();
        var carrier = CarrierOwningToken(id, 6);
        try
        {
            for (int i = 0; i < 4; i++)
                AssertEqual((nint)(id * 1000 + 6), TestLibFunctions.PassingCarrierBorrow(carrier), $"guaranteed argument, call {i}");
            AssertEqual(0, Deaths(id), "a guaranteed argument is never released by the callee");
        }
        finally { carrier.Dispose(); }
        AssertEqual(1, Deaths(id), "disposing the carrier destroys the token once");
    }

    public void TestOwnedArgumentLeavesCallerReference()
    {
        int id = NextTokenId();
        var carrier = CarrierOwningToken(id, 7);
        try
        {
            for (int i = 0; i < 4; i++)
                AssertEqual((nint)(id * 1000 + 7), TestLibFunctions.PassingCarrierConsume(carrier), $"owned argument, call {i}");
            AssertEqual(0, Deaths(id), "each owned copy the callee consumes was its own +1");
            AssertEqual((nint)(id * 1000 + 7), carrier.GetDescribe(), "the caller's carrier is still usable");
        }
        finally { carrier.Dispose(); }
        AssertEqual(1, Deaths(id), "disposing the carrier destroys the token once");
    }

    public void TestInoutArgumentMutationReachesCaller()
    {
        int id = NextTokenId();
        var carrier = CarrierOwningToken(id, 1);
        try
        {
            TestLibFunctions.PassingCarrierRetag(ref carrier, 12);
            AssertEqual(12, carrier.Tag, "the inout argument's mutation reaches the caller");
            AssertEqual(0, Deaths(id), "an inout argument that keeps its reference releases nothing");
        }
        finally { carrier.Dispose(); }
        AssertEqual(1, Deaths(id), "disposing the carrier destroys the token once");
    }

    public void TestInoutArgumentSwapReleasesTheOldReferenceOnce()
    {
        int oldId = NextTokenId();
        int newId = NextTokenId();
        var carrier = CarrierOwningToken(oldId, 2);
        try
        {
            using (var replacement = new PassingToken(newId))
                TestLibFunctions.PassingCarrierSwapToken(ref carrier, replacement);
            AssertEqual(1, Deaths(oldId), "the replaced token loses its only reference and dies once");
            AssertEqual(0, Deaths(newId), "the caller's carrier now owns the replacement");
            using var token = carrier.Token;
            AssertEqual(newId, token.Id, "the caller sees the replacement");
        }
        finally { carrier.Dispose(); }
        AssertEqual(1, Deaths(oldId), "the old token is not destroyed again");
        AssertEqual(1, Deaths(newId), "disposing the carrier destroys the replacement once");
    }

    #endregion

    #region Owned initializer and setter arguments

    public void TestOwnedInitializerAndSetterArgumentsTransferExactlyOnce()
    {
        int firstId = NextTokenId();
        int secondId = NextTokenId();
        PassingCarrierHolder holder;
        using (var first = CarrierOwningToken(firstId, 1))
            holder = new PassingCarrierHolder(first);
        try
        {
            AssertEqual(0, Deaths(firstId), "the holder's copy keeps the first token alive after the caller's is gone");
            AssertEqual(firstId, holder.CarrierTokenID, "the holder reads the first token");

            using (var second = CarrierOwningToken(secondId, 2))
                holder.Carrier = second;
            AssertEqual(1, Deaths(firstId), "the setter's replacement destroys the first token once");
            AssertEqual(0, Deaths(secondId), "the holder's copy keeps the second token alive after the caller's is gone");
            AssertEqual(secondId, holder.CarrierTokenID, "the holder reads the second token");

            using (var copy = holder.Carrier)
                AssertEqual((nint)(secondId * 1000 + 2), copy.GetDescribe(), "the getter's copy reads the stored value");
            AssertEqual(0, Deaths(secondId), "the getter's copy was its own +1");
        }
        finally { holder.Dispose(); }
        AssertEqual(1, Deaths(firstId), "the first token is not destroyed again");
        AssertEqual(1, Deaths(secondId), "disposing the holder destroys the second token once");
    }

    #endregion

    #region Getter results the caller owns

    public void TestGenericArrayGetterHandsTheCallerOneOwnedArray()
    {
        int id = NextTokenId();
        PassingTokenBox<PassingToken> box;
        using (var token = new PassingToken(id))
            box = new PassingTokenBox<PassingToken>(token);
        try
        {
            for (int read = 0; read < 3; read++)
            {
                var items = box.Items;
                try
                {
                    AssertEqual(1, items.Count, "the box holds one token");
                    using var first = items[0];
                    AssertEqual((nint)id, first.Id, "the element is the boxed token");
                }
                finally { (items as System.IDisposable)?.Dispose(); }
            }
            AssertEqual(0, Deaths(id), "the box keeps the token alive after the caller's handle is gone");
        }
        finally { box.Dispose(); }
        AssertEqual(1, Deaths(id), "every read's array was released, so disposing the box destroys the token once");
    }

    #endregion
}
