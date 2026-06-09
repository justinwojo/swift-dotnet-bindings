// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Threading;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

public class RuntimeLimitationsTests
{
    /// <summary>
    /// Verifies the three-way runtime taxonomy on desktop CoreCLR.
    /// On desktop: IsMonoRuntime=false, IsDynamicCodeSupported=true, IsNativeAotRuntime=false.
    /// Critical: static constructors in SwiftArray/SwiftString use IsNativeAotRuntime (not
    /// IsDynamicCodeSupported) to gate NativeAOT-only code, because Mono AOT on iOS simulator
    /// also has IsDynamicCodeSupported=false but must NOT run NativeAOT init.
    /// </summary>
    [Fact]
    public void DesktopCoreClr_IsNativeAotRuntime_IsFalse()
    {
        Assert.False(SwiftRuntimeInfo.IsNativeAotRuntime,
            "Desktop CoreCLR should not be detected as NativeAOT");
    }

    [Fact]
    public void DesktopCoreClr_IsDynamicCodeSupported_IsTrue()
    {
        Assert.True(RuntimeFeature.IsDynamicCodeSupported,
            "Desktop CoreCLR supports dynamic code generation");
    }

    [Fact]
    public void IsNativeAotRuntime_RequiresBothConditions()
    {
        // IsNativeAotRuntime = !IsMonoRuntime && !IsDynamicCodeSupported
        // On desktop: IsMonoRuntime=false, IsDynamicCodeSupported=true → IsNativeAotRuntime=false
        // This ensures NativeAOT-only code (SwiftArray init, SwiftString conformance registration)
        // does NOT run on Mono AOT (where IsDynamicCodeSupported is also false).
        Assert.False(SwiftRuntimeInfo.IsNativeAotRuntime);
        Assert.False(SwiftRuntimeInfo.IsMonoRuntime);
        Assert.True(RuntimeFeature.IsDynamicCodeSupported);
    }

    [Fact]
    public void AllLimitationsHaveDescriptions()
    {
        var all = RuntimeLimitations.GetAllLimitations();
        Assert.True(all.Count > 0, "Registry should contain at least one limitation");

        foreach (var limitation in all)
        {
            var description = RuntimeLimitations.Describe(limitation);
            Assert.False(string.IsNullOrWhiteSpace(description),
                $"Limitation {limitation} has no description");
            Assert.DoesNotContain("Unknown runtime limitation", description,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RegistryContainsExactlyFourLimitations()
    {
        // Three numbered upstream issues (1, 2, 3) plus the SafeHandle async
        // tracking-issue comment item.
        var all = RuntimeLimitations.GetAllLimitations();
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void RegistryContainsAllExpectedLimitations()
    {
        var all = RuntimeLimitations.GetAllLimitations();
        Assert.Contains(RuntimeLimitations.Limitation.MonoCallConvSwiftJitAssertion, all);
        Assert.Contains(RuntimeLimitations.Limitation.NonBlittableCallConvSwiftRejection, all);
        Assert.Contains(RuntimeLimitations.Limitation.MonoSetInsertDoneBlocking, all);
        Assert.Contains(RuntimeLimitations.Limitation.MonoAsyncSafeHandleLifetime, all);
    }

    [Fact]
    public void DesktopCoreClrNotAffectedByAnyLimitation()
    {
        // On desktop CoreCLR (where unit tests run), no runtime-specific limitation applies.
        // Desktop CoreCLR is neither Mono (iOS simulator) nor NativeAOT (iOS device).
        // IsDynamicCodeSupported=true on CoreCLR, false on both NativeAOT and Mono AOT.
        var affected = RuntimeLimitations.GetAffectedLimitations();
        Assert.Empty(affected);
    }

    [Fact]
    public void NoLimitationAffectsDesktopCoreClr()
    {
        // Verify each limitation individually returns false on desktop CoreCLR.
        // Desktop is not Mono (IsMonoRuntime=false) and not NativeAOT
        // (IsDynamicCodeSupported=true), so no limitation should match.
        Assert.False(RuntimeLimitations.IsAffected(
            RuntimeLimitations.Limitation.MonoCallConvSwiftJitAssertion),
            "Mono JIT assertion should not affect desktop CoreCLR");
        Assert.False(RuntimeLimitations.IsAffected(
            RuntimeLimitations.Limitation.NonBlittableCallConvSwiftRejection),
            "Non-blittable rejection is Mono+NativeAOT only, not desktop CoreCLR");
        Assert.False(RuntimeLimitations.IsAffected(
            RuntimeLimitations.Limitation.MonoSetInsertDoneBlocking),
            "Mono Set.insert DONE_BLOCKING is iOS simulator only, not desktop CoreCLR");
        Assert.False(RuntimeLimitations.IsAffected(
            RuntimeLimitations.Limitation.MonoAsyncSafeHandleLifetime),
            "Mono async SafeHandle is iOS simulator only, not desktop CoreCLR");
    }

    // The InlineData strings below are enum value names parsed via Enum.Parse inside
    // each test body. We pass the name (not the enum value) because xUnit 2.9.x test
    // methods must be public, and a public method cannot expose an internal enum as
    // a parameter type (CS0051). Limitation became internal as part of the 1.0
    // surface lock-down.
    [Theory]
    [InlineData(nameof(RuntimeLimitations.Limitation.MonoCallConvSwiftJitAssertion), "jit-info.c:918")]
    [InlineData(nameof(RuntimeLimitations.Limitation.NonBlittableCallConvSwiftRejection), "marshal.c:3729")]
    [InlineData(nameof(RuntimeLimitations.Limitation.MonoSetInsertDoneBlocking), "DONE_BLOCKING")]
    [InlineData(nameof(RuntimeLimitations.Limitation.MonoAsyncSafeHandleLifetime), "SafeHandle")]
    public void DescribeContainsKeyDiagnosticInfo(
        string limitationName, string expectedSubstring)
    {
        var limitation = Enum.Parse<RuntimeLimitations.Limitation>(limitationName);
        var description = RuntimeLimitations.Describe(limitation);
        Assert.Contains(expectedSubstring, description, StringComparison.OrdinalIgnoreCase);
    }

    // Issue 1 = Mono JIT async assert, Issue 2 = non-blittable CallConvSwift,
    // Issue 3 = Mono Set.insert DONE_BLOCKING. The SafeHandle async lifetime is
    // intentionally excluded — it's a tracking-issue comment item, not a numbered
    // filing — and is covered separately by DescribeMarksTrackingCommentItem.
    [Theory]
    [InlineData(nameof(RuntimeLimitations.Limitation.MonoCallConvSwiftJitAssertion), "Issue 1")]
    [InlineData(nameof(RuntimeLimitations.Limitation.NonBlittableCallConvSwiftRejection), "Issue 2")]
    [InlineData(nameof(RuntimeLimitations.Limitation.MonoSetInsertDoneBlocking), "Issue 3")]
    public void DescribeReferencesUpstreamIssueNumber(
        string limitationName, string expectedIssueRef)
    {
        var limitation = Enum.Parse<RuntimeLimitations.Limitation>(limitationName);
        var description = RuntimeLimitations.Describe(limitation);
        Assert.Contains(expectedIssueRef, description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeMarksTrackingCommentItem()
    {
        // SafeHandle async lifetime is not a numbered upstream filing; the registry
        // describes it as a tracking-issue comment item. Pin that wording so future
        // edits don't accidentally re-promote it to a numbered issue.
        var description = RuntimeLimitations.Describe(
            RuntimeLimitations.Limitation.MonoAsyncSafeHandleLifetime);
        Assert.Contains("Tracking-issue comment", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Issue 3", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that SwiftArray&lt;ExistentialContainer1&gt;.TryEagerInitialize() completes
    /// without throwing. On current runtimes, ExistentialContainer1 metadata lookup
    /// succeeds (returns true). The try-catch in TryEagerInitialize() ensures graceful
    /// fallback if metadata becomes unavailable in future runtime versions.
    /// </summary>
    [Fact]
    public void SwiftArrayOfExistentialContainer_TryEagerInitialize_ReturnsGracefully()
    {
        // TryEagerInitialize() should never throw — it either succeeds (true) or
        // catches internally and falls back to lazy init (false).
        var result = SwiftArray<ExistentialContainer1>.TryEagerInitialize();
        // On current runtime, metadata lookup succeeds for ExistentialContainer1
        Assert.True(result, "TryEagerInitialize should succeed for ExistentialContainer1 " +
            "on the current runtime");
    }

    /// <summary>
    /// Verifies that SwiftResult&lt;nint, nint&gt;.TryEagerInitialize() completes
    /// without throwing. Returns false because nint does not conform to Swift.Error
    /// (Result metadata accessor requires the Error witness table for the Failure type).
    /// </summary>
    [Fact]
    public void SwiftResult_TryEagerInitialize_ReturnsGracefully()
    {
        var result = SwiftResult<nint, nint>.TryEagerInitialize();
        // nint doesn't conform to Swift.Error, so metadata resolution fails gracefully
        Assert.False(result, "TryEagerInitialize should return false for SwiftResult<nint, nint> " +
            "(nint does not conform to Swift.Error)");
    }

    /// <summary>
    /// Verifies that SwiftDictionary&lt;nint, nint&gt;.TryEagerInitialize() completes
    /// without throwing. Mirrors the SwiftArray test — both runtime container types must
    /// populate TypeMetadata.Cache during cctor on NativeAOT so that
    /// SwiftOptional&lt;SwiftDictionary&lt;...&gt;&gt;.cctor field initializers can resolve
    /// metadata via TypeMetadata.GetTypeMetadataOrThrow without falling back to reflection
    /// (which fails on NativeAOT for explicit interface implementations on closed generics).
    /// </summary>
    [Fact]
    public void SwiftDictionary_TryEagerInitialize_ReturnsGracefully()
    {
        // TryEagerInitialize() should never throw — it either succeeds (true) or
        // catches internally and falls back to lazy init (false).
        var result = Swift.SwiftDictionary<nint, nint>.TryEagerInitialize();
        Assert.True(result,
            "TryEagerInitialize should succeed for SwiftDictionary<nint, nint> on the current runtime");
    }

    /// <summary>
    /// Verifies that SwiftSet&lt;nint&gt;.TryEagerInitialize() completes without throwing.
    /// Same architectural reason as SwiftDictionary — eager metadata cache population for
    /// NativeAOT explicit-interface-implementation closed generics.
    /// </summary>
    [Fact]
    public void SwiftSet_TryEagerInitialize_ReturnsGracefully()
    {
        var result = Swift.SwiftSet<nint>.TryEagerInitialize();
        Assert.True(result,
            "TryEagerInitialize should succeed for SwiftSet<nint> on the current runtime");
    }

    [Fact]
    public void SwiftArray_CachedMetadata_IsThreadSafe()
    {
        // Access SwiftArray<nint> metadata from multiple threads concurrently.
        // Lazy<T> (ExecutionAndPublication mode) ensures exactly one thread computes
        // the value while others wait. This test verifies no race conditions.
        const int threadCount = 10;
        var barrier = new System.Threading.Barrier(threadCount);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    using var array = new SwiftArray<nint>();
                    _ = array.Count;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
            threads[i].Start();
        }

        foreach (var t in threads)
            t.Join();

        Assert.Empty(exceptions);
    }

    [Theory]
    [InlineData(nameof(RuntimeLimitations.Limitation.MonoCallConvSwiftJitAssertion), "Workaround")]
    [InlineData(nameof(RuntimeLimitations.Limitation.NonBlittableCallConvSwiftRejection), "Workaround")]
    [InlineData(nameof(RuntimeLimitations.Limitation.MonoSetInsertDoneBlocking), "Workaround")]
    [InlineData(nameof(RuntimeLimitations.Limitation.MonoAsyncSafeHandleLifetime), "Workaround")]
    public void DescribeIncludesWorkaround(
        string limitationName, string expectedSubstring)
    {
        var limitation = Enum.Parse<RuntimeLimitations.Limitation>(limitationName);
        var description = RuntimeLimitations.Describe(limitation);
        Assert.Contains(expectedSubstring, description, StringComparison.OrdinalIgnoreCase);
    }
}
