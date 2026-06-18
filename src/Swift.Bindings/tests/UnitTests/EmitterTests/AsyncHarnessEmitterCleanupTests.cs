// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the holder-cleanup code emitted by
/// <see cref="AsyncHarnessEmitter.BuildHolderCleanupCode"/> and its siblings.
///
/// The field walk itself lives in the typed holder's instance method
/// <c>global::Swift.Runtime.SwiftAsyncCallHolder.Cleanup()</c> (exception-safe + idempotent), so
/// the emitters only need to delegate. These tests lock the emitted call shape; the behavioural
/// invariants (every holder field is freed exactly once, the second pass is a no-op, a
/// throwing release does not escape) are covered by the runtime test
/// <c>SwiftAsyncCallHolderTests</c>. The typed holder also collapsed the former three-way mirror
/// (AsyncHarnessEmitter, WrapperEmitter.Async, BuildCancellationCleanupLoop), removing the
/// hand-maintained drift hazard the previous string-match tests guarded against.
/// </summary>
public class AsyncHarnessEmitterCleanupTests
{
    private const string CleanupCall = ".Cleanup()";
    private const string CaptureCall = ".CaptureCancellationToken()";

    [Fact]
    public void BuildHolderCleanupCode_EmitsTypedHolderInstanceCall()
    {
        var code = AsyncHarnessEmitter.BuildHolderCleanupCode("_asyncCallHolder", indent: "    ");

        Assert.Equal("    _asyncCallHolder.Cleanup();", code);
        // The field walk is no longer inlined — it belongs to the typed holder's Cleanup().
        Assert.DoesNotContain("for (", code);
        Assert.DoesNotContain("RetainedSelfPtr", code);
        Assert.DoesNotContain("ExistentialContainerHeap", code);
    }

    [Fact]
    public void BuildHolderCleanupCode_HonorsHolderVariableAndIndent()
    {
        var code = AsyncHarnessEmitter.BuildHolderCleanupCode("holder", indent: "        ");

        Assert.Equal("        holder.Cleanup();", code);
    }

    [Fact]
    public void WrapperEmitterAsync_BuildHolderCleanupCode_DelegatesToSameRuntimeHelper()
    {
        // The user-facing async wrapper bodies (foreground pre-cancel + foreground catch) and the
        // harness callbacks now emit byte-for-byte identical cleanup, because both route through
        // the single AsyncHarnessEmitter.BuildHolderCleanupCode → typed holder Cleanup(). Lock that
        // so the two async emission paths cannot diverge (the gap that originally hid the async leak).
        var harness = AsyncHarnessEmitter.BuildHolderCleanupCode("_asyncCallHolder", indent: "    ");
        var wrapper = BindingsGeneration.WrapperEmitter.BuildHolderCleanupCode("_asyncCallHolder", indent: "    ");

        Assert.Equal(harness, wrapper);
        Assert.Contains(CleanupCall, wrapper);
    }

    [Fact]
    public void BuildCancellationCleanupLoop_CapturesTokenBeforeCleanup()
    {
        // The cancellation path must read the registered token (read-only) BEFORE cleanup disposes
        // the registration, so TrySetCanceled propagates the right token. The emitted code assigns
        // a pre-declared `cancelToken` local from CaptureCancellationToken(), then runs Cleanup().
        var code = AsyncHarnessEmitter.BuildCancellationCleanupLoop("holder", indent: "    ");

        var captureIdx = code.IndexOf(CaptureCall, System.StringComparison.Ordinal);
        var cleanupIdx = code.IndexOf(CleanupCall, System.StringComparison.Ordinal);
        Assert.True(captureIdx >= 0, "cancellation path must capture the token");
        Assert.True(cleanupIdx > captureIdx, "token capture must precede cleanup (cleanup disposes the registration)");
        Assert.Contains("cancelToken = holder.CaptureCancellationToken();", code);
        // Assigns the pre-declared local (does not redeclare it).
        Assert.DoesNotContain("CancellationToken cancelToken", code);
    }
}
