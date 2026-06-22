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

    // ---- Async collection result-carrier release (BuildCollectionCarrierMarshalLines) ----
    //
    // An async function returning a frozen stdlib container (`[Class]`, `[ResilientStruct]`,
    // `[K:V]`, `Set<…>`) writes its result into the carrier via
    // `initializeMemory(as: <Container>.self, repeating: result, count: 1)` — running the container's
    // copy witness, a +1 on the CoW storage. The C# completion callback revives an INDEPENDENT copy
    // (`MarshalFromSwift` → NewFromPayload → InitializeWithCopy), so the carrier's +1 must be released
    // by a value-witness Destroy before SBW_Free reclaims the raw allocation, or the backing storage
    // leaks every call. This arm bypasses AsyncResultPlanner (which only classifies scalar struct/enum
    // carriers), so these tests are its only unit-level coverage. Runtime proof is
    // AsyncCollectionCarrierLeakProbeTests in BindingTests.

    private const string Destroy = "ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata)";
    private const string MarshalCollection = "SwiftMarshal.MarshalFromSwift<";
    private const string Conversion = "var result = _proj.Convert(_collection);";
    private const string IdentityConversion = "var result = _collection;";
    private const string Indent = "                                ";

    [Fact]
    public void BuildCollectionCarrierMarshalLines_PlainCollection_DestroysCarrierBeforeConversion()
    {
        var code = AsyncHarnessEmitter.BuildCollectionCarrierMarshalLines(
            runtimeType: "global::Swift.Runtime.SwiftArray<TestModule.Widget>",
            conversionExpr: "_proj.Convert(_collection)",
            returnType: "global::System.Collections.Generic.IReadOnlyList<TestModule.Widget>",
            usesObjCContainerBridge: false, proxySuppressed: false, continuationIndent: Indent);

        // The carrier's value-witness +1 is released (the leak fix).
        Assert.Contains(MarshalCollection, code);
        Assert.Contains(Destroy, code);

        // Destroy must precede the projection conversion: the conversion reads only the independent
        // _collection copy, so destroying first keeps the +1 balanced even if the conversion throws.
        var destroyIdx = code.IndexOf(Destroy, System.StringComparison.Ordinal);
        var conversionIdx = code.IndexOf(Conversion, System.StringComparison.Ordinal);
        Assert.True(conversionIdx >= 0, "plain collection arm must convert the marshalled container");
        Assert.True(destroyIdx < conversionIdx, "VWT Destroy must run before the conversion (error-path safety)");
    }

    [Fact]
    public void BuildCollectionCarrierMarshalLines_SetIdentityReturn_DestroysCarrierBeforeIdentityAssignment()
    {
        // A Set<T> whose element needs no projection (SwiftSet<T> already IS IReadOnlySet<T>) takes the
        // IDENTITY conversion: TryGetCollectionAsyncInfo passes conversionExpr "_collection", so the
        // assembled body ends `var result = _collection;` — `result` ALIASES the independent
        // MarshalFromSwift/NewFromPayload copy directly, with no intervening Convert(). The carrier's +1
        // must still be released, and the VWT Destroy must still run BEFORE that assignment. Destroy
        // targets the CARRIER (`resultPtr`) — a distinct allocation from the managed `_collection` copy —
        // so releasing it does not disturb the value `result` aliases. This is the leak fix's identity
        // path, which the projecting-conversion test above does not exercise.
        var code = AsyncHarnessEmitter.BuildCollectionCarrierMarshalLines(
            runtimeType: "global::Swift.Runtime.SwiftSet<int>",
            conversionExpr: "_collection",
            returnType: "global::System.Collections.Generic.IReadOnlySet<int>",
            usesObjCContainerBridge: false, proxySuppressed: false, continuationIndent: Indent);

        Assert.Contains(MarshalCollection, code);
        Assert.Contains(Destroy, code);
        Assert.Contains(IdentityConversion, code);

        var destroyIdx = code.IndexOf(Destroy, System.StringComparison.Ordinal);
        var identityIdx = code.IndexOf(IdentityConversion, System.StringComparison.Ordinal);
        Assert.True(identityIdx >= 0, "set identity arm must assign the marshalled container directly");
        Assert.True(destroyIdx < identityIdx, "VWT Destroy must run before the identity assignment (carrier +1 release)");
    }

    [Fact]
    public void BuildCollectionCarrierMarshalLines_ObjCContainerBridge_DoesNotDestroy()
    {
        // The ObjC-bridge carrier is a +1-retained NS-collection POINTER (an 8-byte holder), not a
        // Swift container value — a value-witness Destroy would use the wrong metadata/allocator and
        // is a double-free hazard. The +1 is released through the bridge conversion instead.
        var code = AsyncHarnessEmitter.BuildCollectionCarrierMarshalLines(
            runtimeType: "global::Swift.Runtime.SwiftArray<Foundation.NSURL>",
            conversionExpr: "ConvertNSArray(_ptr)",
            returnType: "global::System.Collections.Generic.IReadOnlyList<Foundation.NSUrl>",
            usesObjCContainerBridge: true, proxySuppressed: false, continuationIndent: Indent);

        Assert.Contains("IntPtr _ptr = *(IntPtr*)resultPtr;", code);
        Assert.DoesNotContain(Destroy, code);
        Assert.DoesNotContain(MarshalCollection, code);
    }

    [Fact]
    public void BuildCollectionCarrierMarshalLines_ProxySuppressed_FaultsWithoutMarshalOrDestroy()
    {
        // No per-element proxy → cannot marshal; fault the awaiting Task. The Swift wrapper still
        // initializeMemory'd the container into the carrier, so this arm currently does NOT release
        // that +1 — a known, pre-existing leak on a fault-only path (the bound method always faults).
        // It is deliberately NOT closed by a C#-side VWT Destroy: the runtime's existential metadata
        // is arity/marker-based (not protocol-identity-based), so a Destroy through container/shim
        // metadata can over-read or mis-release a class-bound 16-byte existential cell — strictly
        // worse than the leak. The robust release is a Swift-side typed destroy entry (tracked). This
        // test pins the current emitted shape: a fault, with no marshal and no Destroy.
        var code = AsyncHarnessEmitter.BuildCollectionCarrierMarshalLines(
            runtimeType: "global::Swift.Runtime.SwiftArray<TestModule.IThing>",
            conversionExpr: "UNUSED",
            returnType: "global::System.Collections.Generic.IReadOnlyList<TestModule.IThing>",
            usesObjCContainerBridge: false, proxySuppressed: true, continuationIndent: Indent);

        Assert.Contains("throw new global::System.NotSupportedException", code);
        Assert.DoesNotContain(Destroy, code);
        Assert.DoesNotContain(MarshalCollection, code);
    }
}
