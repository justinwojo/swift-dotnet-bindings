// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// A simple enum crossing by case identity that is handed over in Swift's own memory rather than
/// as an argument or result scalar: container elements, an <c>Optional</c>'s payload, a tuple
/// element, an async result, a slot a C# conformer reads or fills, and a frozen struct's field.
/// The binary's tags of <see cref="CaseShift"/> run one ahead of its constants (an <c>@_spi</c>
/// case comes first), so a site that reads or writes the constant as the tag names the wrong case;
/// each Swift function names the case it holds. <see cref="PayloadShift"/> also hides a payload
/// case, so its values (and Optionals) are wider than a tag byte and not trivially copyable.
/// Fixture: Types/CaseIdentityMemory.swift.
/// </summary>
public class CaseIdentityMemoryTests : TestBase
{
    public CaseIdentityMemoryTests(TestResults results) : base(results) { }

    #region Containers

    public void TestArrayElementsCrossByCase()
    {
        var hidden = TestLibFunctions.GetCaseShiftHidden();
        var list = TestLibFunctions.GetCaseShiftList();
        AssertEqual(3, list.Count, "three elements");
        AssertEqual(hidden, list[0], "element 0 is the hidden case");
        AssertEqual(CaseShift.First, list[1], "element 1 is First");
        AssertEqual(CaseShift.Second, list[2], "element 2 is Second");
        AssertEqual("second,first,hidden",
            TestLibFunctions.CaseShiftListNames(new[] { CaseShift.Second, CaseShift.First, hidden }),
            "array argument elements");
    }

    public void TestOptionalArrayElementsCrossByCase()
    {
        var hidden = TestLibFunctions.GetCaseShiftHidden();
        var list = TestLibFunctions.GetCaseShiftOptionalList();
        AssertEqual(4, list.Count, "four elements");
        AssertEqual<CaseShift?>(CaseShift.First, list[0], "element 0 is First");
        AssertEqual<CaseShift?>(null, list[1], "element 1 is nil");
        AssertEqual<CaseShift?>(hidden, list[2], "element 2 is the hidden case");
        AssertEqual<CaseShift?>(CaseShift.Second, list[3], "element 3 is Second");
        AssertEqual("first,none,hidden",
            TestLibFunctions.CaseShiftOptionalListNames(new CaseShift?[] { CaseShift.First, null, hidden }),
            "optional array argument elements");
    }

    public void TestDictionaryValuesCrossByCase()
    {
        var hidden = TestLibFunctions.GetCaseShiftHidden();
        var byName = TestLibFunctions.GetCaseShiftByName();
        AssertEqual(hidden, byName["hidden"], "value for hidden");
        AssertEqual(CaseShift.First, byName["first"], "value for first");
        AssertEqual(CaseShift.Second, byName["second"], "value for second");
        AssertEqual("a=first,b=hidden",
            TestLibFunctions.CaseShiftDictionaryNames(new Dictionary<string, CaseShift> { ["a"] = CaseShift.First, ["b"] = hidden }),
            "dictionary argument values");
    }

    public void TestSetElementsCrossByCase()
    {
        var set = TestLibFunctions.GetCaseShiftSet();
        AssertEqual(2, set.Count, "two elements");
        AssertTrue(set.Contains(CaseShift.First) && set.Contains(CaseShift.Second),
            $"the set holds First and Second (got {string.Join(",", set.Select(v => (int)v))})");
        AssertEqual("first,second",
            TestLibFunctions.CaseShiftSetNames(new HashSet<CaseShift> { CaseShift.First, CaseShift.Second }),
            "set argument elements");
    }

    #endregion

    #region Results in Swift memory

    public void TestTupleElementCrossesByCase()
    {
        var hidden = TestLibFunctions.GetCaseShiftHidden();
        var (zero, zeroCount) = TestLibFunctions.CaseShiftPair(0);
        AssertEqual(hidden, zero, "element 0 of (hidden, 0)");
        AssertEqual((nint)0, zeroCount, "element 1 of (hidden, 0)");
        var (one, oneCount) = TestLibFunctions.CaseShiftPair(5);
        AssertEqual(CaseShift.First, one, "element 0 of (first, 5)");
        AssertEqual((nint)5, oneCount, "element 1 of (first, 5)");
    }

    public async Task TestAsyncResultsCrossByCase()
    {
        var hidden = TestLibFunctions.GetCaseShiftHidden();
        AssertEqual(hidden, await TestLibFunctions.CaseShiftAsync(true), "async hidden");
        AssertEqual(CaseShift.First, await TestLibFunctions.CaseShiftAsync(false), "async First");
        AssertEqual<CaseShift?>(CaseShift.Second, await TestLibFunctions.CaseShiftOptionalAsync(true), "async optional Second");
        AssertEqual<CaseShift?>(null, await TestLibFunctions.CaseShiftOptionalAsync(false), "async optional nil");
    }

    public void TestOptionalPropertyAndSubscriptCrossByCase()
    {
        var hidden = TestLibFunctions.GetCaseShiftHidden();
        using var box = new CaseShiftOptionalBox(CaseShift.First);
        AssertEqual<CaseShift?>(CaseShift.First, box.Mode, "getter reads First");
        AssertEqual("first", box.ModeName, "init stored First");
        box.Mode = CaseShift.Second;
        AssertEqual("second", box.ModeName, "setter stored Second");
        AssertEqual<CaseShift?>(CaseShift.Second, box.Mode, "getter reads Second");
        box.Mode = hidden;
        AssertEqual("hidden", box.ModeName, "setter stored the hidden case");
        AssertEqual<CaseShift?>(hidden, box.Mode, "getter reads the hidden case");
        box.Mode = null;
        AssertEqual("none", box.ModeName, "setter stored nil");
        AssertEqual<CaseShift?>(null, box.Mode, "getter reads nil");

        AssertEqual<CaseShift?>(hidden, box[0], "subscript 0 is the hidden case");
        AssertEqual<CaseShift?>(CaseShift.First, box[1], "subscript 1 is First");
        AssertEqual<CaseShift?>(null, box[2], "subscript 2 is nil");
    }

    public void TestAFrozenStructFieldCrossesByCase()
    {
        var hidden = TestLibFunctions.GetCaseShiftHidden();
        using var fromSwift = TestLibFunctions.GetCaseShiftHolderHidden();
        AssertEqual(hidden, fromSwift.Mode, "field read of the hidden case");
        AssertEqual("hidden", fromSwift.ModeName, "Swift reads the hidden case");
        using var fromCSharp = new CaseShiftHolder(CaseShift.Second, 3);
        AssertEqual(CaseShift.Second, fromCSharp.Mode, "field read of Second");
        AssertEqual("secondx3", TestLibFunctions.CaseShiftHolderName(fromCSharp), "Swift reads Second from the struct");
        fromCSharp.Mode = CaseShift.First;
        AssertEqual("firstx3", TestLibFunctions.CaseShiftHolderName(fromCSharp), "field write of First");
    }

    #endregion

    #region A C# conformer Swift calls

    private sealed class Responder : ICaseShiftResponder
    {
        public CaseShift CaseShiftCurrent { get; init; }
        public List<CaseShift> Received { get; } = new();
        public CaseShift Respond(CaseShift value) { Received.Add(value); return CaseShiftCurrent; }
        public string Describe(CaseShift value) { Received.Add(value); return $"saw:{(int)value}"; }
        public void Adjust(ref CaseShift value) { Received.Add(value); value = CaseShift.Second; }
    }

    public void TestAConformerReceivesAndReturnsCases()
    {
        var hidden = TestLibFunctions.GetCaseShiftHidden();
        var responder = new Responder { CaseShiftCurrent = CaseShift.First };
        // Swift reads `caseShiftCurrent`, `respond`'s result and `adjust`'s inout value from the conformer.
        var seen = TestLibFunctions.CaseShiftDriveResponder(responder, CaseShift.First);
        AssertEqual($"first,first,saw:{(int)CaseShift.First},second", seen, "Swift saw First, First, the described First and the adjusted Second");
        AssertEqual(3, responder.Received.Count, "three requirements received a value");
        AssertTrue(responder.Received.All(v => v == CaseShift.First),
            $"every requirement received First (got {string.Join(",", responder.Received.Select(v => (int)v))})");

        var hiddenResponder = new Responder { CaseShiftCurrent = hidden };
        var seenHidden = TestLibFunctions.CaseShiftDriveResponderHidden(hiddenResponder);
        AssertEqual($"hidden,hidden,saw:{(int)hidden},second", seenHidden, "Swift saw the hidden case come back");
        AssertTrue(hiddenResponder.Received.All(v => v == hidden),
            $"every requirement received the hidden case (got {string.Join(",", hiddenResponder.Received.Select(v => (int)v))})");
    }

    #endregion

    #region An enum whose binary hides a payload case

    public void TestHiddenPayloadEnumVisibleCasesCross()
    {
        AssertEqual(PayloadShift.Other, TestLibFunctions.GetPayloadShiftOther(), "return Other");
        AssertEqual("plain", TestLibFunctions.PayloadShiftName(PayloadShift.Plain), "argument Plain");
        AssertEqual<PayloadShift?>(PayloadShift.Other, TestLibFunctions.PayloadShiftOptional(true), "optional return Other");
        AssertEqual<PayloadShift?>(null, TestLibFunctions.PayloadShiftOptional(false), "optional return nil");
        AssertEqual("other", TestLibFunctions.PayloadShiftNameOrNone(PayloadShift.Other), "optional argument Other");
        AssertEqual("none", TestLibFunctions.PayloadShiftNameOrNone(null), "optional argument nil");
    }

    public void TestHiddenPayloadEnumContainersCross()
    {
        var list = TestLibFunctions.GetPayloadShiftList();
        AssertEqual("Plain,Other,Plain", string.Join(",", list), "array elements");
        AssertEqual("other,plain", TestLibFunctions.PayloadShiftListNames(new[] { PayloadShift.Other, PayloadShift.Plain }), "array argument");
        var optionals = TestLibFunctions.GetPayloadShiftOptionalList();
        AssertEqual<PayloadShift?>(PayloadShift.Other, optionals[0], "optional element Other");
        AssertEqual<PayloadShift?>(null, optionals[1], "optional element nil");
        AssertEqual<PayloadShift?>(PayloadShift.Plain, optionals[2], "optional element Plain");
    }

    public void TestTheHiddenPayloadCaseIsReadAndReleasedAsItself()
    {
        // The hidden case holds a heap String. Reading it copies the value (retaining the payload)
        // and the read is released; repeating it many times must neither crash nor read a visible case.
        for (var i = 0; i < 200; i++)
        {
            var list = TestLibFunctions.GetPayloadShiftListWithNamed();
            AssertEqual(3, list.Count, "three elements");
            AssertEqual(PayloadShift.Plain, list[0], "element 0 is Plain");
            AssertFalse(Enum.IsDefined(list[1]), $"element 1 is no published case (got {(long)list[1]})");
            AssertEqual(PayloadShift.Other, list[2], "element 2 is Other");
            var named = TestLibFunctions.GetPayloadShiftOptionalNamed();
            AssertTrue(named.HasValue && !Enum.IsDefined(named.Value), "the optional hidden case is a value but no published case");
        }
    }

    public async Task TestAHiddenPayloadCaseMovedOutOfSwiftMemoryIsReleased()
    {
        // Each of these results is a slot C# owns: it reads the case, and must then destroy the
        // slot, or the hidden case's class payload keeps one retain per read.
        var baseline = (long)TestLibFunctions.GetPayloadShiftTokenRetains();
        for (var i = 0; i < 10; i++)
        {
            var (tracked, count) = TestLibFunctions.PayloadShiftTrackedPair(i);
            AssertFalse(Enum.IsDefined(tracked), $"tuple element is no published case (got {(long)tracked})");
            AssertEqual((nint)i, count, "tuple count");
        }
        AssertEqual(baseline, (long)TestLibFunctions.GetPayloadShiftTokenRetains(), "tuple reads release the payload");

        for (var i = 0; i < 10; i++)
            AssertFalse(Enum.IsDefined(await TestLibFunctions.PayloadShiftTrackedAsync(true)), "async result is no published case");
        // The Swift task releases its own copy of the result only after the completion callback
        // returns, which can be after this continuation runs; a retain the binding leaked never goes.
        for (var wait = 0; wait < 100 && TestLibFunctions.GetPayloadShiftTokenRetains() != baseline; wait++)
            await Task.Delay(10);
        AssertEqual(baseline, (long)TestLibFunctions.GetPayloadShiftTokenRetains(), "async results release the payload");

        var list = TestLibFunctions.GetPayloadShiftTrackedList();
        var withList = (long)TestLibFunctions.GetPayloadShiftTokenRetains();
        for (var i = 0; i < 10; i++)
            AssertFalse(Enum.IsDefined(list[1]), "element 1 is no published case");
        AssertEqual(PayloadShift.Other, list[2], "element 2 is Other");
        AssertEqual(withList, (long)TestLibFunctions.GetPayloadShiftTokenRetains(), "element reads release the payload");
        GC.KeepAlive(list);
    }

    public void TestAPayloadEnumReadsACaseIdentityPayloadByCase()
    {
        using var first = CaseShiftCarrier.Shift(CaseShift.First);
        AssertEqual("shift:first", TestLibFunctions.CaseShiftCarrierName(first), "Swift holds First");
        AssertTrue(first.TryGetShift(out var shift), "first is the shift case");
        AssertEqual(CaseShift.First, shift, "the payload reads back as First");

        using var hidden = TestLibFunctions.GetCaseShiftCarrierHidden();
        AssertTrue(hidden.TryGetShift(out var hiddenShift), "hidden is the shift case");
        AssertFalse(Enum.IsDefined(hiddenShift), $"the hidden case is no published case (got {(long)hiddenShift})");

        using var pair = TestLibFunctions.GetCaseShiftCarrierPair();
        AssertTrue(pair.TryGetPair(out var count, out var pairShift), "pair is the pair case");
        AssertEqual((byte)7, count, "pair count");
        AssertEqual(CaseShift.Second, pairShift, "the tuple payload reads back as Second");

        var baseline = (long)TestLibFunctions.GetPayloadShiftTokenRetains();
        using (var tracked = TestLibFunctions.GetCaseShiftCarrierTracked())
        {
            var withCarrier = (long)TestLibFunctions.GetPayloadShiftTokenRetains();
            for (var i = 0; i < 10; i++)
            {
                AssertTrue(tracked.TryGetHeld(out var payload), "tracked is the held case");
                AssertFalse(Enum.IsDefined(payload), "the hidden payload case is no published case");
            }
            AssertEqual(withCarrier, (long)TestLibFunctions.GetPayloadShiftTokenRetains(), "payload reads release the enum copy");
        }
        AssertEqual(baseline, (long)TestLibFunctions.GetPayloadShiftTokenRetains(), "the carrier releases its payload");
    }

    public void TestAHiddenPayloadCaseInAGenericResultIsReleased()
    {
        // A generic result buffer C# reads only the case out of, as T or as a tuple element of T,
        // is destroyed with the value still in it.
        var baseline = (long)TestLibFunctions.GetPayloadShiftTokenRetains();
        for (var i = 0; i < 10; i++)
            AssertFalse(Enum.IsDefined(TestLibFunctions.PayloadShiftTrackedAs(PayloadShift.Plain)), "T result is no published case");
        AssertEqual(baseline, (long)TestLibFunctions.GetPayloadShiftTokenRetains(), "T results release the payload");

        for (var i = 0; i < 10; i++)
        {
            var (tracked, count) = TestLibFunctions.PayloadShiftTrackedAs((PayloadShift.Plain, (nint)7));
            AssertFalse(Enum.IsDefined(tracked), $"tuple element is no published case (got {(long)tracked})");
            AssertEqual((nint)0, count, "Swift built the tuple");
        }
        AssertEqual(baseline, (long)TestLibFunctions.GetPayloadShiftTokenRetains(), "tuple T results release the payload");
    }

    public void TestHiddenPayloadEnumOptionalStorageCrosses()
    {
        using var box = new PayloadShiftBox(PayloadShift.Plain);
        AssertEqual("plain", box.ModeName, "init stored Plain");
        AssertEqual<PayloadShift?>(PayloadShift.Plain, box.Mode, "getter reads Plain");
        box.Mode = PayloadShift.Other;
        AssertEqual("other", box.ModeName, "setter stored Other");
        box.StoreNamed();
        for (var i = 0; i < 200; i++)
        {
            var mode = box.Mode;
            AssertTrue(mode.HasValue && !Enum.IsDefined(mode.Value), "getter reads the hidden payload case as an unnamed value");
        }
        AssertEqual("named:" + string.Concat(Enumerable.Repeat("stored-", 4)), box.ModeName, "Swift still holds its payload");
        box.Mode = null;
        AssertEqual("none", box.ModeName, "setter stored nil");
    }

    #endregion
}
