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
    public void RegistryContainsExactlyFiveUpstreamBugs()
    {
        var all = RuntimeLimitations.GetAllLimitations();
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public void RegistryContainsAllExpectedLimitations()
    {
        var all = RuntimeLimitations.GetAllLimitations();
        Assert.Contains(RuntimeLimitations.Limitation.MonoCallConvSwiftJitAssertion, all);
        Assert.Contains(RuntimeLimitations.Limitation.NonBlittableCallConvSwiftRejection, all);
        Assert.Contains(RuntimeLimitations.Limitation.NativeAotFloatStructParam, all);
        Assert.Contains(RuntimeLimitations.Limitation.NativeAotFloatStructReturn, all);
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
            RuntimeLimitations.Limitation.NativeAotFloatStructParam),
            "NativeAOT float param is iOS device only, not desktop CoreCLR");
        Assert.False(RuntimeLimitations.IsAffected(
            RuntimeLimitations.Limitation.NativeAotFloatStructReturn),
            "NativeAOT float return is iOS device only, not desktop CoreCLR");
        Assert.False(RuntimeLimitations.IsAffected(
            RuntimeLimitations.Limitation.MonoAsyncSafeHandleLifetime),
            "Mono async SafeHandle is iOS simulator only, not desktop CoreCLR");
    }

    [Theory]
    [InlineData(RuntimeLimitations.Limitation.MonoCallConvSwiftJitAssertion, "jit-info.c:918")]
    [InlineData(RuntimeLimitations.Limitation.NonBlittableCallConvSwiftRejection, "marshal.c:3729")]
    [InlineData(RuntimeLimitations.Limitation.NativeAotFloatStructParam, "GPR instead of FPR")]
    [InlineData(RuntimeLimitations.Limitation.NativeAotFloatStructReturn, "GPR instead of FPR")]
    [InlineData(RuntimeLimitations.Limitation.MonoAsyncSafeHandleLifetime, "SafeHandle")]
    public void DescribeContainsKeyDiagnosticInfo(
        RuntimeLimitations.Limitation limitation, string expectedSubstring)
    {
        var description = RuntimeLimitations.Describe(limitation);
        Assert.Contains(expectedSubstring, description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RuntimeLimitations.Limitation.MonoCallConvSwiftJitAssertion, "Issue 1")]
    [InlineData(RuntimeLimitations.Limitation.NonBlittableCallConvSwiftRejection, "Issue 2")]
    [InlineData(RuntimeLimitations.Limitation.NativeAotFloatStructParam, "Issue 5")]
    [InlineData(RuntimeLimitations.Limitation.NativeAotFloatStructReturn, "Issue 6")]
    [InlineData(RuntimeLimitations.Limitation.MonoAsyncSafeHandleLifetime, "Issue 3")]
    public void DescribeReferencesUpstreamIssueNumber(
        RuntimeLimitations.Limitation limitation, string expectedIssueRef)
    {
        var description = RuntimeLimitations.Describe(limitation);
        Assert.Contains(expectedIssueRef, description, StringComparison.OrdinalIgnoreCase);
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
    [InlineData(RuntimeLimitations.Limitation.MonoCallConvSwiftJitAssertion, "Workaround")]
    [InlineData(RuntimeLimitations.Limitation.NonBlittableCallConvSwiftRejection, "Workaround")]
    [InlineData(RuntimeLimitations.Limitation.NativeAotFloatStructParam, "Workaround")]
    [InlineData(RuntimeLimitations.Limitation.NativeAotFloatStructReturn, "Workaround")]
    [InlineData(RuntimeLimitations.Limitation.MonoAsyncSafeHandleLifetime, "Workaround")]
    public void DescribeIncludesWorkaround(
        RuntimeLimitations.Limitation limitation, string expectedSubstring)
    {
        var description = RuntimeLimitations.Describe(limitation);
        Assert.Contains(expectedSubstring, description, StringComparison.OrdinalIgnoreCase);
    }
}
