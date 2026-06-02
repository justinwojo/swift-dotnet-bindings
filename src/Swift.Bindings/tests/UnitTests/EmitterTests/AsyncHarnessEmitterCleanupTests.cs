// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the cleanup loop emitted by <see cref="AsyncHarnessEmitter.BuildHolderCleanupCode"/>.
///
/// sdk-0.11.0-residual-gaps.md S-5 (`authenticationContextHeap` leak): the async-callback
/// cleanup loop must own all transient native allocations that flow through
/// <c>_asyncCallHolder</c>. Existential containers handed to async Swift methods
/// live in a <c>NativeMemory.Alloc</c> buffer (e.g. for <c>any Protocol</c> params
/// like <c>validator: any SkipPolicyValidator</c>). The foreground wrapper has no
/// finally block on instance-async paths, and even on the static-async / async-init
/// paths the foreground finally would free while Swift still reads from the buffer
/// on its continuation thread (UAF). The only safe owner is the callback's
/// holder-cleanup loop — which is what the <c>ExistentialContainerHeap</c> branch
/// emitted here provides.
/// </summary>
public class AsyncHarnessEmitterCleanupTests
{
    [Fact]
    public void BuildHolderCleanupCode_EmitsExistentialContainerHeapBranch()
    {
        var code = AsyncHarnessEmitter.BuildHolderCleanupCode("_asyncCallHolder", indent: "    ");

        Assert.Contains("ExistentialContainerHeap existentialHeap", code);
        Assert.Contains("existentialHeap.Ptr != IntPtr.Zero", code);
        Assert.Contains("NativeMemory.Free((void*)existentialHeap.Ptr);", code);
    }

    [Fact]
    public void BuildHolderCleanupCode_RetainsPreExistingBranches()
    {
        // Regression sanity: the S-5 fix added one branch — it must not have
        // disturbed the rest of the holder-cleanup contract.
        var code = AsyncHarnessEmitter.BuildHolderCleanupCode("_asyncCallHolder", indent: "    ");

        Assert.Contains("RetainedSelfPtr retained", code);
        // The async-self retain is Arc.UnknownObjectRetain (isa-dispatch), so its paired
        // cleanup release MUST be the matching UnknownObjectRelease — otherwise an
        // @objc:NSObject-rooted self is objc_retain'd but swift_release'd, corrupting its
        // refcount (issue #40 / P1-01). UnknownObjectRelease (vs Arc.Release) also skips the
        // swift_isDeallocating pre-check, which is the safer choice inside a cleanup walk.
        Assert.Contains("Arc.UnknownObjectRelease(retained.Ptr);", code);
        Assert.DoesNotContain("Arc.Release(retained.Ptr);", code);
        Assert.Contains("DeferredSafeHandleRelease deferred", code);
        Assert.Contains("deferred.Handle.DangerousRelease();", code);
        Assert.Contains("CopyBufferWithType copyBuffer", code);
        Assert.Contains("copyBuffer.Metadata.ValueWitnessTable->Destroy", code);
        Assert.Contains("NativeMemory.Free((void*)copyBuffer.Buffer);", code);
        Assert.Contains("AsyncDeferredDisposeList __deferredList", code);
        Assert.Contains("CancellationRegistrationHolder cancelReg", code);
    }

    [Fact]
    public void BuildHolderCleanupCode_ExistentialBranchSitsBetweenCopyBufferAndDeferredList()
    {
        // The branch order matters: ExistentialContainerHeap is its own readonly
        // struct type and won't collide with the others, but the holder slots
        // are reserved by EmitAsync between the copy-buffer payload and the
        // AsyncDeferredDisposeList tail, so keep the cleanup walk in the same
        // order to make future audits easier.
        var code = AsyncHarnessEmitter.BuildHolderCleanupCode("_asyncCallHolder", indent: "    ");

        int copyBufferIdx = code.IndexOf("CopyBufferWithType", System.StringComparison.Ordinal);
        int existentialIdx = code.IndexOf("ExistentialContainerHeap", System.StringComparison.Ordinal);
        int deferredListIdx = code.IndexOf("AsyncDeferredDisposeList", System.StringComparison.Ordinal);

        Assert.True(copyBufferIdx >= 0, "CopyBufferWithType branch missing");
        Assert.True(existentialIdx > copyBufferIdx, "ExistentialContainerHeap must come after CopyBufferWithType");
        Assert.True(deferredListIdx > existentialIdx, "AsyncDeferredDisposeList must come after ExistentialContainerHeap");
    }

    [Fact]
    public void BuildHolderCleanupCode_CancellationBranchOmittedWhenRequested()
    {
        // includeCancellationReg=false is used in async-init paths where the TCS
        // owns cancellation registration directly. The existential branch must
        // still emit even when cancellation cleanup is skipped.
        var code = AsyncHarnessEmitter.BuildHolderCleanupCode("_asyncCallHolder", indent: "    ", includeCancellationReg: false);

        Assert.DoesNotContain("CancellationRegistrationHolder", code);
        Assert.Contains("ExistentialContainerHeap existentialHeap", code);
    }

    [Fact]
    public void WrapperEmitterAsync_BuildHolderCleanupCode_EmitsExistentialContainerHeapBranch()
    {
        // Sibling helper to AsyncHarnessEmitter.BuildHolderCleanupCode used by the
        // user-facing async wrapper bodies (foreground pre-cancel + foreground catch).
        // Codex round 1 caught the original S-5 fix landing only in AsyncHarnessEmitter
        // — this variant's omission caused the existential heap to leak on every
        // foreground-throw and synchronous-cancel path even though the success
        // callback freed it correctly. Kept as a regression invariant so any future
        // holder slot type added to one helper must be mirrored in the other.
        var code = BindingsGeneration.WrapperEmitter.BuildHolderCleanupCode("_asyncCallHolder", indent: "    ");

        Assert.Contains("ExistentialContainerHeap existentialHeap", code);
        Assert.Contains("existentialHeap.Ptr != IntPtr.Zero", code);
        Assert.Contains("NativeMemory.Free((void*)existentialHeap.Ptr);", code);
    }

    [Fact]
    public void WrapperEmitterAsync_BuildHolderCleanupCode_MatchesAsyncHarnessEmitterBranchSet()
    {
        // The two helpers emit semantically identical cleanup loops (only the loop
        // variable differs: `i` vs `__cleanupIdx`). Lock the branch set in step so
        // that adding a holder slot to one without the other is a build break.
        var harness = AsyncHarnessEmitter.BuildHolderCleanupCode("_asyncCallHolder", indent: "    ");
        var wrapper = BindingsGeneration.WrapperEmitter.BuildHolderCleanupCode("_asyncCallHolder", indent: "    ");

        foreach (var slotType in HolderSlotTypes)
        {
            Assert.Contains(slotType, harness);
            Assert.Contains(slotType, wrapper);
        }
    }

    [Fact]
    public void BuildCancellationCleanupLoop_EmitsAllHolderSlotBranches()
    {
        // The cancellation-path walk is the third site that must mirror the holder
        // slot set — Codex round 2 caught WrapperEmitter.Async's hand-rolled block
        // omitting AsyncDeferredDisposeList. Both BuildErrorCallbackBlock helpers
        // now delegate to BuildCancellationCleanupLoop, so this single assertion
        // covers all four cleanup sites (two BuildHolderCleanupCode helpers + two
        // hand-rolled cancellation blocks that now share this loop body).
        var cancellation = AsyncHarnessEmitter.BuildCancellationCleanupLoop("holder", "i", indent: "    ");

        foreach (var slotType in HolderSlotTypes)
        {
            Assert.Contains(slotType, cancellation);
        }

        // CancellationRegistrationHolder must come FIRST in the cancellation walk
        // so the loop captures `cancelToken` before disposing the registration.
        int cancelRegIdx = cancellation.IndexOf("CancellationRegistrationHolder", System.StringComparison.Ordinal);
        int retainedIdx = cancellation.IndexOf("RetainedSelfPtr", System.StringComparison.Ordinal);
        Assert.True(cancelRegIdx > 0 && cancelRegIdx < retainedIdx,
            "CancellationRegistrationHolder must be the first branch in BuildCancellationCleanupLoop");
        Assert.Contains("cancelToken = cancelReg.Token;", cancellation);
    }

    [Fact]
    public void BuildCancellationCleanupLoop_RespectsLoopVariableName()
    {
        // Both call sites pass their own loop variable name to avoid CS0136
        // shadowing (WrapperEmitter.Async uses __cleanupIdx; AsyncHarnessEmitter
        // uses i). The helper must wire the supplied name through to every
        // holder[X] indexing site.
        var cancellation = AsyncHarnessEmitter.BuildCancellationCleanupLoop("holder", "__cleanupIdx", indent: "    ");

        Assert.Contains("for (int __cleanupIdx = 1; __cleanupIdx < holder.Length; __cleanupIdx++)", cancellation);
        Assert.Contains("holder[__cleanupIdx] is CancellationRegistrationHolder", cancellation);
        Assert.Contains("holder[__cleanupIdx] is AsyncDeferredDisposeList", cancellation);
        Assert.DoesNotContain("holder[i]", cancellation);
    }

    /// <summary>
    /// The full set of holder slot types whose cleanup is required on every
    /// async termination path (success / exception / cancellation / pre-cancel).
    /// Adding a new slot here is the build break that forces all four emission
    /// sites to be updated together.
    /// </summary>
    private static readonly string[] HolderSlotTypes = new[]
    {
        "RetainedSelfPtr",
        "DeferredSafeHandleRelease",
        "CopyBufferWithType",
        "ExistentialContainerHeap",
        "AsyncDeferredDisposeList",
        "CancellationRegistrationHolder",
    };
}
