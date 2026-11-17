// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Existentials with no proxy class returned to C# at +1: a protocol the binding has no record for
/// (C# sees <c>object</c>) and a protocol with associated types whose conformers are known (C# sees
/// an <see cref="ExistentialUnion"/>). The raw container used to be boxed into <c>object</c>, or
/// wrapped in a union that never released it, so every read leaked the value. Each carrier now
/// owns the +1 and releases it once, on Dispose or when collected. Every Swift value here bumps a
/// live count. Closures carry the same values both ways: a closure argument is lent for the call
/// and used to reach the body as raw bytes that dangled once Swift destroyed it, and a closure
/// result is taken by Swift at +1, which the owning box can only lend. Fixture:
/// Lifetime/NoProxyExistentialReturns.swift.
/// </summary>
public class NoProxyExistentialReturnTests : TestBase
{
    public NoProxyExistentialReturnTests(TestResults results) : base(results) { }

    private const int Reads = 8;

    private static int Live() => TestLibFunctions.GetNoProxyExistentialLiveCount();

    /// <summary>Reads <paramref name="read"/> repeatedly on a finished thread, collects, and requires every value released.</summary>
    private void AssertCollectedReadsRelease(string what, Func<int, object> read)
    {
        var before = Live();
        RunOnFinishedThread(() =>
        {
            for (int i = 0; i < Reads; i++)
                AssertNotNull(read(i), $"{what}: read {i} returns a value");
        });
        ForceGCThorough();
        AssertEqual(before, Live(), $"{what}: every read is released once collected");
    }

    public void TestUnrecordedExistentialReturnReleasesOnDispose()
    {
        var before = Live();
        var value = TestLibFunctions.MakeNoProxyDescribed(3);
        AssertEqual(before + 1, Live(), "The returned value holds one token");
        AssertEqual("described3", TestLibFunctions.DescribeNoProxy(value), "The value round-trips back to Swift");
        AssertEqual(before + 1, Live(), "Passing it back borrows it");
        AssertTrue(value is IDisposable, "The carrier owns the value and is disposable");
        ((IDisposable)value).Dispose();
        AssertEqual(before, Live(), "Dispose releases the token exactly once");
        ((IDisposable)value).Dispose();
        AssertEqual(before, Live(), "A second Dispose releases nothing");
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.DescribeNoProxy(value),
            "A released value is refused before it reaches Swift");
    }

    public void TestUnrecordedExistentialReturnsReleaseWhenCollected()
    {
        AssertCollectedReadsRelease("free function", i => TestLibFunctions.MakeNoProxyDescribed(i));
        AssertCollectedReadsRelease("composition", i => TestLibFunctions.MakeNoProxyComposition(i));
    }

    public void TestUnrecordedExistentialPropertyAndMethodRelease()
    {
        var before = Live();
        RunOnFinishedThread(() =>
        {
            using var holder = new NoProxyDescribedHolder(5);
            for (int i = 0; i < Reads; i++)
            {
                AssertEqual("described5", TestLibFunctions.DescribeNoProxy(holder.Described), $"Getter read {i}");
                AssertEqual("described5", TestLibFunctions.DescribeNoProxy(holder.GetCurrent()), $"Method read {i}");
            }
            var replacement = TestLibFunctions.MakeNoProxyDescribed(6);
            holder.Described = replacement;
            AssertEqual("described6", TestLibFunctions.DescribeNoProxy(holder.Described), "The setter stored the value read back");
            ((IDisposable)replacement).Dispose();
            AssertEqual("described6", TestLibFunctions.DescribeNoProxy(holder.Described), "The holder keeps its own reference");
        });
        ForceGCThorough();
        AssertEqual(before, Live(), "Holder, getter, method and setter values are all released");
    }

    public void TestUnrecordedExistentialAsyncReturnReleases()
    {
        var before = Live();
        RunOnFinishedThread(() =>
        {
            for (int i = 0; i < Reads; i++)
            {
                var value = TestLibFunctions.MakeNoProxyDescribedAsync(i).GetAwaiter().GetResult();
                AssertEqual($"described{i}", TestLibFunctions.DescribeNoProxy(value), $"Async read {i} round-trips");
            }
        });
        ForceGCThorough();
        AssertEqual(before, Live(), "Every async read is released once collected");
    }

    public void TestAsyncAnyReturnIsTheValue()
    {
        var value = TestLibFunctions.MakeNoProxyAnyAsync(42).GetAwaiter().GetResult();
        AssertEqual(42L, Convert.ToInt64(value), "An async Any return unboxes to the value, as the synchronous return does");
    }

    public void TestUnionReturnReleasesOnDispose()
    {
        var before = Live();
        ExistentialUnion union = TestLibFunctions.MakeNoProxyKeyed(7);
        AssertEqual(before + 1, Live(), "The union holds one token");
        using (var token = union.As<NoProxyKeyedToken>())
        {
            AssertNotNull(token, "The union casts to its conformer");
            AssertEqual(7, token!.KeyedID, "The cast reads the value");
        }
        AssertTrue(union.As<NoProxyKeyedValue>() is null, "A cast to another conformer is null");
        union.Dispose();
        AssertEqual(before, Live(), "Dispose releases the token exactly once");
        AssertThrows<ObjectDisposedException>(() => union.As<NoProxyKeyedToken>(),
            "A released union is refused before its payload is read");
    }

    public void TestUnionReturnsReleaseWhenCollected()
    {
        AssertCollectedReadsRelease("union", i => TestLibFunctions.MakeNoProxyKeyed(i));
        AssertCollectedReadsRelease("class-bound union", i => TestLibFunctions.MakeNoProxyClassKeyed(i));
    }

    public void TestClassBoundUnionCastsToItsConformer()
    {
        var before = Live();
        using (var union = TestLibFunctions.MakeNoProxyClassKeyed(9))
        {
            using var token = union.As<NoProxyClassKeyedToken>();
            AssertNotNull(token, "A class-bound union reads its dynamic type from the instance");
            AssertEqual(9, token!.ClassKeyedID, "The cast reads the value");
        }
        ForceGCThorough();
        AssertEqual(before, Live(), "The class reference is released once");
    }

    public void TestWitnessOfObjectTypedRequirementReleases()
    {
        // Before the fix the conformer's member returned ExistentialUnion while the interface
        // declared object (CS0738), which failed the whole module.
        var before = Live();
        RunOnFinishedThread(() =>
        {
            using var factory = new NoProxyKeyedFactory();
            INoProxyKeyedProducer producer = factory;
            for (int i = 0; i < Reads; i++)
            {
                AssertNotNull(producer.ProduceKeyed(i), $"Requirement read {i}");
                AssertNotNull(factory.ProducedKeyed, $"Property requirement read {i}");
            }
        });
        ForceGCThorough();
        AssertEqual(before, Live(), "Witness reads are released once collected");
    }

    public void TestClosureArgumentKeptByTheBodyStaysValid()
    {
        var before = Live();
        object? kept = null;
        TestLibFunctions.WithNoProxyDescribed(11, value => kept = value);
        AssertEqual(before + 1, Live(), "The kept value holds its own reference after Swift released the argument");
        AssertEqual("described11", TestLibFunctions.DescribeNoProxy(kept!), "The kept value is still the token Swift made");
        ((IDisposable)kept!).Dispose();
        AssertEqual(before, Live(), "Disposing the kept value releases the token");

        object? keptElement = null;
        TestLibFunctions.WithNoProxyDescribedPair(12, (value, id) => keptElement = value);
        AssertEqual("described12", TestLibFunctions.DescribeNoProxy(keptElement!), "A kept tuple element is still valid");
        ((IDisposable)keptElement!).Dispose();

        object? keptKeyed = null;
        TestLibFunctions.WithNoProxyKeyed(13, value => keptKeyed = value);
        AssertEqual(before + 1, Live(), "A kept associated-type value holds its own reference");
        ((IDisposable)keptKeyed!).Dispose();
        AssertEqual(before, Live(), "Every kept closure argument is released once");
    }

    public void TestClosureArgumentsReleaseWhenCollected()
    {
        var before = Live();
        RunOnFinishedThread(() =>
        {
            for (int i = 0; i < Reads; i++)
                TestLibFunctions.WithNoProxyDescribed(i, value =>
                    AssertEqual($"described{i}", TestLibFunctions.DescribeNoProxy(value), $"Argument {i} reads in the body"));
        });
        ForceGCThorough();
        AssertEqual(before, Live(), "Closure arguments the body did not keep are released once collected");
    }

    public void TestClosureResultHandsSwiftItsOwnReference()
    {
        var before = Live();
        using (var union = TestLibFunctions.MakeNoProxyKeyed(14))
        {
            for (int i = 0; i < 3; i++)
                AssertEqual(14, TestLibFunctions.ReadNoProxyKeyed(() => union), $"Swift reads the returned value, read {i}");
            AssertEqual(before + 1, Live(), "Swift released only the reference it was given; the union keeps its own");
            using var token = union.As<NoProxyKeyedToken>();
            AssertEqual(14, token!.KeyedID, "The union is still valid after Swift dropped each result");
        }
        ForceGCThorough();
        AssertEqual(before, Live(), "The token is released once the union is");
    }

    public void TestDirectClosureResultHandsSwiftItsOwnReference()
    {
        // A direct Swift-convention callback; engines that mishandle it refuse before Swift runs.
        const SwiftTransitionFacts shapes = SwiftTransitionFacts.LoweredSignature | SwiftTransitionFacts.ReverseCallback;
        var before = Live();
        var value = TestLibFunctions.MakeNoProxyDescribed(15);
        try
        {
            if (SwiftTransitionGuard.IsSupported(shapes))
            {
                for (int i = 0; i < 3; i++)
                    AssertEqual("described15", TestLibFunctions.DescribeNoProxyMade(() => value), $"Swift reads the returned value, read {i}");
                AssertEqual(before + 1, Live(), "Swift released only the reference it was given");
                AssertEqual("described15", TestLibFunctions.DescribeNoProxy(value), "The value is still valid");
            }
            else
            {
                SwiftTransitionUnsupportedException? refusal = null;
                try { TestLibFunctions.DescribeNoProxyMade(() => value); }
                catch (SwiftTransitionUnsupportedException ex) { refusal = ex; }
                AssertNotNull(refusal, "An engine that mishandles a direct callback refuses before Swift runs");
            }
        }
        finally
        {
            ((IDisposable)value).Dispose();
        }
        AssertEqual(before, Live(), "The value is released once");
    }

    private sealed class CSharpKeyedProducer : INoProxyKeyedProducer
    {
        public ExistentialUnion? Held;
        public object ProduceKeyed(int id) => Held!;
        public object ProducedKeyed => Held!;
    }

    public void TestCSharpConformerReturnHandsSwiftItsOwnReference()
    {
        // Swift consumes what a C# conformer returns. The union only lends its container, so
        // handing Swift those bytes let Swift release the union's own reference.
        var before = Live();
        var producer = new CSharpKeyedProducer { Held = TestLibFunctions.MakeNoProxyKeyed(16) };
        for (int i = 0; i < 3; i++)
            AssertEqual(32, TestLibFunctions.ReadNoProxyKeyedProducer(producer, 0), $"Swift reads both requirements, read {i}");
        AssertEqual(before + 1, Live(), "Swift released only the references it was given; the union keeps its own");
        using (var token = producer.Held!.As<NoProxyKeyedToken>())
            AssertEqual(16, token!.KeyedID, "The union is still valid");
        producer.Held!.Dispose();
        AssertEqual(before, Live(), "The token is released once the union is");
    }
}
