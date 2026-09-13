// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using System.Runtime.CompilerServices;

namespace RuntimeTestsApp.Generics;

public class GenericValuePropertyTests : TestBase
{
    public GenericValuePropertyTests(TestResults results) : base(results) { }

    public void TestScalarProperties_ReadWriteAndMutatingGetter()
    {
        var value = Functions.MakeGvpScalarLight(count: 7, current: 0);
        AssertEqual(7, value.Count, "initial scalar getter");
        value.Count = 11;
        AssertEqual(11, value.Count, "scalar setter writes through caller storage");
        AssertEqual(1, value.Next, "first mutating getter");
        AssertEqual(2, value.Next, "second mutating getter persists native mutation");
        AssertTrue(value.Valid, "Bool getter uses byte ABI");
    }

    public void TestScalarProperties_CopiesRemainIndependent()
    {
        var original = Functions.MakeGvpScalarLight(count: 7, current: 0);
        var copy = original;
        original.Count = 11;
        _ = original.Next;

        AssertEqual(7, copy.Count, "value copy keeps independent count storage");
        AssertEqual(1, copy.Next, "value copy keeps independent mutating state");
        AssertEqual(11, original.Count, "original retains its own write");
        AssertEqual(2, original.Next, "original retains its own prior mutation");
    }

    public void TestScalarProperties_RefArrayElementWritesBack()
    {
        var values = new[] { Functions.MakeGvpScalarHeavy(count: 3, current: 8) };
        ref var element = ref values[0];
        element.Count = 9;
        AssertEqual(9, values[0].Count, "setter writes through ref array element");
        AssertEqual(9, element.Next, "mutating getter writes through ref array element");
        AssertEqual(10, values[0].Next, "array storage observes prior native mutation");
    }

    public void TestScalarProperties_ConcreteAndStringResults()
    {
        var value = Functions.MakeGvpScalarLight(count: 4, current: 6);
        var pair = value.Pair;
        AssertEqual(4, pair.First, "concrete result-buffer first field");
        AssertEqual(6, pair.Second, "concrete result-buffer second field");
        AssertEqual("4:6", value.Label, "UTF-8 getter result");
    }

    public void TestScalarProperties_ReadonlyTemporaryMutatesOnlyDefensiveCopy()
    {
        AssertEqual(2, Functions.MakeGvpScalarLight(count: 1, current: 1).Next,
            "temporary may mutate only its defensive copy");
    }

    public void TestScalarWeighedBox_OnePwtInlineGetter()
    {
        using var slot = new LightSlot(weightUnits: 13, slotName: "light");
        var value = new ScalarWeighedBox<LightSlot>(slot: slot, tare: 5);

        AssertEqual(8, value.NetUnitsValue,
            "one-PWT property uses constructed inline receiver");
    }

    public void TestPayloadBackedParents_ConcreteAndGenericProperties()
    {
        ExercisePayloadBackedProperties();
        ForceGCThorough();

        AssertEqual(0, GvpTrackedRef.LiveCount,
            "all tracked references released after parent/result disposal");
        AssertEqual(4, GvpTrackedRef.DeinitCount,
            "each tracked reference destroyed exactly once");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExercisePayloadBackedProperties()
    {
        GvpTrackedRef.ResetCounters();
        using var first = new GvpTrackedRef(identifier: 1);
        using var second = new GvpTrackedRef(identifier: 2);
        using var opaque = Functions.MakeGvpOpaqueTracked(first: first, second: second, count: 7);
        using var replacement = new GvpTrackedRef(identifier: 3);

        AssertEqual(7, opaque.Count, "opaque concrete getter");
        opaque.Count = 11;
        AssertEqual(11, opaque.Count, "opaque concrete setter");
        AssertEqual("opaque:11", opaque.Label, "opaque String getter");
        AssertEqual(1, opaque.Next, "opaque mutating getter first call");
        AssertEqual(2, opaque.Next, "opaque mutating getter writes back");

        using (var initial = opaque.Item)
            AssertEqual(1, initial.Identifier, "bare T getter");
        opaque.Item = replacement;
        using (var replaced = opaque.Item)
            AssertEqual(3, replaced.Identifier, "bare T replacement setter");

        AssertTrue(opaque.Maybe is null, "Optional<T> begins None");
        opaque.Maybe = second;
        using (var some = opaque.Maybe)
            AssertEqual(2, some!.Identifier, "Optional<T> Some round-trip");
        opaque.Maybe = replacement;
        using (var replacedSome = opaque.Maybe)
            AssertEqual(3, replacedSome!.Identifier, "Optional<T> Some replacement round-trip");
        opaque.Maybe = null;
        AssertTrue(opaque.Maybe is null, "Optional<T> returns to None");

        var items = opaque.Items;
        try
        {
            AssertEqual(2, items.Count, "Array<T> count");
            using var item0 = items[0];
            AssertEqual(1, item0.Identifier, "Array<T> first element");
        }
        finally
        {
            (items as IDisposable)?.Dispose();
        }
        opaque.Items = new[] { second, replacement };
        var replacedItems = opaque.Items;
        try
        {
            AssertEqual(2, replacedItems.Count, "Array<T> replacement count");
            using var replacedItem0 = replacedItems[0];
            using var replacedItem1 = replacedItems[1];
            AssertEqual(2, replacedItem0.Identifier, "Array<T> replacement first element");
            AssertEqual(3, replacedItem1.Identifier, "Array<T> replacement second element");
        }
        finally
        {
            (replacedItems as IDisposable)?.Dispose();
        }

        var byName = opaque.ByName;
        try
        {
            AssertEqual(2, byName.Count, "Dictionary<String,T> count");
            var sawFirst = false;
            var sawSecond = false;
            foreach (var entry in byName)
            {
                using var mapped = entry.Value;
                if (entry.Key == "first")
                {
                    AssertEqual(1, mapped.Identifier, "Dictionary<String,T> first value");
                    sawFirst = true;
                }
                else if (entry.Key == "second")
                {
                    AssertEqual(2, mapped.Identifier, "Dictionary<String,T> second value");
                    sawSecond = true;
                }
                else
                {
                    AssertTrue(false, $"Dictionary<String,T> unexpected key '{entry.Key}'");
                }
            }
            AssertTrue(sawFirst && sawSecond, "Dictionary<String,T> preserves both initial keys");
        }
        finally
        {
            (byName as IDisposable)?.Dispose();
        }
        opaque.ByName = new Dictionary<string, GvpTrackedRef> { ["replacement"] = replacement };
        var replacedByName = opaque.ByName;
        try
        {
            AssertEqual(1, replacedByName.Count, "Dictionary<String,T> replacement count");
            foreach (var entry in replacedByName)
            {
                AssertEqual("replacement", entry.Key, "Dictionary<String,T> replacement key");
                using var mapped = entry.Value;
                AssertEqual(3, mapped.Identifier, "Dictionary<String,T> replacement value");
            }
        }
        finally
        {
            (replacedByName as IDisposable)?.Dispose();
        }

        using var frozen = Functions.MakeGvpRefFrozenTracked(item: first, count: 4, prefix: "frozen");
        frozen.Count = 9;
        AssertEqual(9, frozen.Count, "reference-bearing frozen concrete property");
        AssertEqual("frozen:9", frozen.Label, "reference-bearing frozen String property");
        AssertEqual(1, frozen.Next, "reference-bearing frozen mutating getter");
        using var frozenItem = frozen.Item;
        AssertEqual(1, frozenItem.Identifier, "reference-bearing frozen T getter");

        using var triple = Functions.MakeGvpTripleTracked(
            first: first, second: second, third: replacement);
        AssertEqual(3, triple.Count, "three parent metadata slots remain register-eligible");

        var parentOnly = new GvpTrackedRef(identifier: 4);
        opaque.Item = parentOnly;
        parentOnly.Dispose();
        using var resultAfterParent = opaque.Item;
        opaque.Dispose();
        AssertEqual(4, resultAfterParent.Identifier,
            "owned T getter result survives parent-first disposal");
    }

    public void TestDependencyPat_DescriptorWitnessProperties()
    {
        using var value = SwiftBindingsTestLibDependency.Functions.MakeDependencyGvpPat(count: 5);
        AssertEqual(5, value.Count, "dependency PAT concrete getter");
        value.Count = 8;
        AssertEqual(8, value.Count, "dependency PAT concrete setter");
        AssertEqual(17, value.Marker, "dependency descriptor-PWT open accessor");
    }
}
