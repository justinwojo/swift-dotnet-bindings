// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Reads of a bare Swift <c>Any</c>, which crosses as an <c>ExistentialContainer0</c> that C#
/// unboxes into a plain value. Wherever Swift hands the container over at +1 (a return, an
/// Optional return, an async result, a stored property) or C# copies it out of a value it owns (an
/// enum payload), the read must destroy that container once the value is out.
///
/// Each probe stores a <see cref="LifetimeTracker"/>-counted <c>TrackedRef</c> in the <c>Any</c>.
/// <c>ExistentialContainer0</c> cannot unbox a class, so every read of one throws
/// <see cref="NotSupportedException"/>, and the probe checks that the container was released all the
/// same. The string reads are the positive controls of the same shapes.
/// </summary>
public class BareAnyOwnershipLeakTests : TestBase
{
    public BareAnyOwnershipLeakTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    private void AssertNoLeaksAfter(Action churn, string context)
    {
        DrainFinalizers();
        LifetimeTracker.Reset();
        LifetimeTracker.RunIsolated(churn);
        DrainFinalizers();
        LifetimeTracker.AssertNoLeaks(context);
    }

    private void AssertNoLeaksAfterAsync(Func<Task> churn, string context)
    {
        DrainFinalizers();
        LifetimeTracker.Reset();
        LifetimeTracker.RunIsolated(churn);
        DrainFinalizers();
        LifetimeTracker.AssertNoLeaks(context);
    }

    /// <summary>An enum payload typed <c>Any</c>, alone, labeled and Optional, releases its copy when read.</summary>
    public void TestEnumAnyPayloadReadsReleaseTheContainer()
    {
        AssertNoLeaksAfter(() => BuildTrackedAnyPayloads(10),
            "making and disposing Any-payload enums releases every payload (control)");
        AssertNoLeaksAfter(() => ReadTrackedAnyPayloads(10),
            "reading an Any enum payload must destroy the payload copy it unboxed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BuildTrackedAnyPayloads(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            using var single = TestLibFunctions.MakeTrackedAnyPayloadSingle(i);
            using var labeled = TestLibFunctions.MakeTrackedAnyPayloadLabeled(i);
            using var optional = TestLibFunctions.MakeTrackedAnyPayloadOptional(i);
            AssertEqual(TrackedAnyPayload.CaseTag.Single, single.Tag, "single case");
            AssertEqual(TrackedAnyPayload.CaseTag.Labeled, labeled.Tag, "labeled case");
            AssertEqual(TrackedAnyPayload.CaseTag.Optional, optional.Tag, "optional case");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReadTrackedAnyPayloads(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            using var single = TestLibFunctions.MakeTrackedAnyPayloadSingle(i);
            using var labeled = TestLibFunctions.MakeTrackedAnyPayloadLabeled(i);
            using var optional = TestLibFunctions.MakeTrackedAnyPayloadOptional(i);
            AssertThrows<NotSupportedException>(() => single.TryGetSingle(out _),
                "a class in an Any payload does not unbox");
            AssertThrows<NotSupportedException>(() => labeled.TryGetLabeled(out _, out _),
                "a class in a labeled Any payload does not unbox");
            AssertThrows<NotSupportedException>(() => optional.TryGetOptional(out _),
                "a class in an Optional Any payload does not unbox");
        }
    }

    /// <summary>The value an Any enum payload holds reads back.</summary>
    public void TestEnumAnyPayloadReadsTheValue()
    {
        using var fromSwift = TestLibFunctions.MakeStringAnyPayloadOptional("swift");
        AssertTrue(fromSwift.TryGetOptional(out var swiftValue), "the optional case reads");
        AssertEqual("swift", swiftValue as string, "the Optional Any payload unboxes the string Swift stored");

        using var fromCSharp = TrackedAnyPayload.Single("csharp");
        AssertTrue(fromCSharp.TryGetSingle(out var csharpValue), "the single case reads");
        AssertEqual("csharp", csharpValue as string, "the Any payload unboxes the string C# stored");

        using var labeled = TrackedAnyPayload.Labeled("name", 42L);
        AssertTrue(labeled.TryGetLabeled(out var name, out var labeledValue), "the labeled case reads");
        AssertEqual("name", name, "the label beside the Any reads back");
        AssertEqual(42L, labeledValue is long l ? l : -1L, "the labeled Any payload unboxes the Int");
    }

    /// <summary>A returned <c>Any</c> and <c>Any?</c> are released when read.</summary>
    public void TestReturnedAnyReleasesTheContainer()
    {
        AssertNoLeaksAfter(() => ReadReturnedTrackedAny(10),
            "reading a returned Any or Any? must destroy the container Swift returned at +1");
        AssertEqual("text", TestLibFunctions.MakeStringOptionalAny("text") as string,
            "a returned Any? unboxes the string Swift returned");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReadReturnedTrackedAny(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            AssertThrows<NotSupportedException>(() => _ = TestLibFunctions.MakeTrackedAny(i),
                "a class returned as Any does not unbox");
            AssertThrows<NotSupportedException>(() => _ = TestLibFunctions.MakeTrackedOptionalAny(i),
                "a class returned as Any? does not unbox");
        }
    }

    /// <summary>A stored <c>Any</c> read through its getter is released.</summary>
    public void TestStoredAnyGetterReleasesTheContainer()
    {
        AssertNoLeaksAfter(() => BuildTrackedAnyHolders(10),
            "making and disposing an Any holder releases its value (control)");
        AssertNoLeaksAfter(() => ReadTrackedAnyHolders(10),
            "reading a stored Any must destroy the copy its getter returned");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BuildTrackedAnyHolders(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            using var holder = new TrackedAnyHolder(i);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReadTrackedAnyHolders(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            using var holder = new TrackedAnyHolder(i);
            AssertThrows<NotSupportedException>(() => _ = holder.Value,
                "a class stored as Any does not unbox");
        }
    }

    /// <summary>
    /// An async <c>Any?</c> result is unboxed, not handed to the awaiter as a raw container, and
    /// both async <c>Any</c> shapes release the container Swift returned.
    /// </summary>
    public async Task TestAsyncAnyResultsUnboxAndReleaseTheContainer()
    {
        AssertEqual("text", await TestLibFunctions.MakeStringOptionalAnyAsync("text") as string,
            "an async Any? unboxes the string Swift returned");
        AssertNull(await TestLibFunctions.MakeNoOptionalAnyAsync(), "an async Any? nil is null");

        AssertNoLeaksAfterAsync(() => ReadAsyncTrackedAny(10),
            "reading an async Any or Any? result must destroy the container Swift returned at +1");
    }

    private async Task ReadAsyncTrackedAny(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            await AssertThrowsAsync(() => TestLibFunctions.MakeTrackedAnyAsync(i),
                "a class returned as async Any does not unbox");
            await AssertThrowsAsync(() => TestLibFunctions.MakeTrackedOptionalAnyAsync(i),
                "a class returned as async Any? does not unbox");
        }
    }

    private static async Task AssertThrowsAsync<T>(Func<Task<T>> call, string message)
    {
        object? unexpected;
        try
        {
            unexpected = await call();
        }
        catch (NotSupportedException)
        {
            return;
        }
        throw new AssertionException(
            $"{message}: expected NotSupportedException, got {unexpected?.GetType().FullName ?? "null"}");
    }
}
