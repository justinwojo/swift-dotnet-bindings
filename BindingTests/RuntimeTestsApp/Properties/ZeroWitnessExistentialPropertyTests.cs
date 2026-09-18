// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Pairs with <c>BindingTests/Sources/SwiftBindingsTestLib/Properties/ZeroWitnessExistentialProperties.swift</c>
/// and the <c>AnyHolder</c> fixture in <c>Generics/Existentials.swift</c>. A stored <c>Any</c> or
/// <c>any Sendable</c> property is typed <c>object</c> in C#: the setter boxes the assigned value
/// and the getter hands back the plain C# value Swift holds — never the raw container. Swift's own
/// <c>describe…</c> reports the stored value as <c>&lt;type&gt;:&lt;value&gt;</c>, so each setter
/// assertion also checks which Swift type the value was boxed as.
/// </summary>
public class ZeroWitnessExistentialPropertyTests : TestBase
{
    public ZeroWitnessExistentialPropertyTests(TestResults results) : base(results) { }

    public void TestAnyPropertyReadsSwiftInitialValue()
    {
        using var holder = new ZeroWitnessPropertyHolder();
        AssertEqual(0L, holder.AnyValue, "Swift `0` literal reads back as a C# long");
    }

    public void TestAnyPropertyRoundTripsString()
    {
        using var holder = new ZeroWitnessPropertyHolder();
        holder.AnyValue = "hello";
        AssertEqual("String:hello", holder.GetDescribeAnyValue(), "setter stored a Swift String");
        AssertEqual("hello", holder.AnyValue, "getter reads the string back");
    }

    public void TestAnyPropertyRoundTripsHeapString()
    {
        // Longer than the 15-byte small-string form, so the stored value owns a retained buffer.
        var text = new string('q', 96);
        using var holder = new ZeroWitnessPropertyHolder();
        holder.AnyValue = text;
        for (int i = 0; i < 32; i++)
            AssertEqual(text, holder.AnyValue, $"iter {i}: heap string reads back");
        AssertEqual($"String:{text}", holder.GetDescribeAnyValue(), "Swift still holds the string after the reads");
    }

    public void TestAnyPropertyRoundTripsPrimitives()
    {
        using var holder = new ZeroWitnessPropertyHolder();

        holder.AnyValue = 42L;
        AssertEqual("Int:42", holder.GetDescribeAnyValue(), "long stored as Swift Int");
        AssertEqual(42L, holder.AnyValue, "Swift Int reads back as long");

        holder.AnyValue = 7;
        AssertEqual("Int32:7", holder.GetDescribeAnyValue(), "int stored as Swift Int32");
        AssertEqual(7, holder.AnyValue, "Swift Int32 reads back as int");

        holder.AnyValue = 2.5;
        AssertEqual("Double:2.5", holder.GetDescribeAnyValue(), "double stored as Swift Double");
        AssertEqual(2.5, holder.AnyValue, "Swift Double reads back as double");

        holder.AnyValue = true;
        AssertEqual("Bool:true", holder.GetDescribeAnyValue(), "bool stored as Swift Bool");
        AssertEqual(true, holder.AnyValue, "Swift Bool reads back as bool");
    }

    public void TestSendablePropertyRoundTrips()
    {
        using var holder = new ZeroWitnessPropertyHolder();
        AssertEqual(0L, holder.SendableValue, "Swift `0` literal reads back through any Sendable");

        holder.SendableValue = "sent";
        AssertEqual("String:sent", holder.GetDescribeSendableValue(), "setter stored a Swift String");
        AssertEqual("sent", holder.SendableValue, "getter reads the string back");

        holder.SendableValue = 9L;
        AssertEqual("Int:9", holder.GetDescribeSendableValue(), "long stored as Swift Int");
        AssertEqual(9L, holder.SendableValue, "Swift Int reads back as long");
    }

    public void TestAnyReturningPropertyOnPlainSwiftClass()
    {
        using var fromInt = new AnyHolder(5);
        AssertEqual(5, fromInt.Base, "Swift Int32 reads back as int");

        using var fromString = new AnyHolder("stored");
        AssertEqual("stored", fromString.Base, "Swift String reads back as string");
    }

    private static int StoreAndReadDistinctStrings(ZeroWitnessPropertyHolder holder, int start, int count)
    {
        int matched = 0;
        for (int i = start; i < start + count; i++)
        {
            // A distinct heap string per iteration: Swift frees each one when the next assignment
            // replaces it, unless a getter left a retain on it behind.
            var text = $"zero-witness-property-{i:D8}-{new string('p', 48)}";
            holder.AnyValue = text;
            if (holder.AnyValue is string read && read == text)
                matched++;
        }
        return matched;
    }

    public void TestAnyPropertyGetterReleasesItsCopy()
    {
        const int Warmup = 500;
        const int Iterations = 5000;
        using var holder = new ZeroWitnessPropertyHolder();

        AssertEqual(Warmup, StoreAndReadDistinctStrings(holder, 0, Warmup), "warmup reads return the stored string");
        holder.Reset();
        long before = MallocProbe.LiveBlocksAfterCollection();

        AssertEqual(Iterations, StoreAndReadDistinctStrings(holder, Warmup, Iterations), "every read returns the stored string");
        holder.Reset();
        long after = MallocProbe.LiveBlocksAfterCollection();

        long growth = after - before;
        TestLogger.Info($"Any property get: {Iterations} distinct strings, malloc blocks {before} -> {after} (growth {growth})");
        // A getter that keeps its +1 copy pins one string buffer per iteration; a quarter of that
        // leaves room for unrelated allocator churn while still failing a per-read leak outright.
        AssertTrue(growth < Iterations / 4,
            $"malloc live blocks grew by {growth} over {Iterations} reads; the getter keeps each string alive");
    }
}
