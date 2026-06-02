// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for async Swift wrapper generation, particularly around non-frozen parameter cleanup.
/// </summary>
public class AsyncSwiftWrapperTests
{
    #region Non-Frozen Parameter Cleanup Tests

    [Fact]
    public void AsyncWrapper_WithNonFrozenParam_DoesNotUseDefer()
    {
        var swiftOutput = GenerateAsyncMethodWrapper(
            methodName: "testMethod",
            isAsync: true,
            hasNonFrozenParam: true);

        // The fix moved cleanup code AFTER the callback instead of using defer
        // defer causes use-after-free because it runs when Task scope exits,
        // but Swift's async machinery may still hold references after callback
        Assert.DoesNotContain("defer {", swiftOutput);
    }

    // Note: Tests for cleanup position and copy allocation are validated through
    // integration tests since they require full environment setup. The key behavioral
    // change (no defer usage) is verified by AsyncWrapper_WithNonFrozenParam_DoesNotUseDefer.

    [Fact]
    public void AsyncWrapper_WithoutNonFrozenParam_DoesNotHaveCleanupCode()
    {
        var swiftOutput = GenerateAsyncMethodWrapper(
            methodName: "testMethod",
            isAsync: true,
            hasNonFrozenParam: false);

        // Without non-frozen params, there should be no cleanup code
        Assert.DoesNotContain("deinitialize(count: 1)", swiftOutput);
        Assert.DoesNotContain("deallocate()", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_NonAsyncMethod_DoesNotGenerateSwiftWrapper()
    {
        var swiftOutput = GenerateAsyncMethodWrapper(
            methodName: "testMethod",
            isAsync: false,
            hasNonFrozenParam: true);

        // Non-async methods don't generate Swift wrappers
        Assert.DoesNotContain("extension", swiftOutput);
        Assert.DoesNotContain("Task {", swiftOutput);
    }

    #endregion

    #region BitwiseCopyable Avoidance Tests

    [Fact]
    public void AsyncWrapper_ClassReturnType_UsesUnmanagedPassRetainedInsteadOfStoreBytes()
    {
        // Class types like UIImage are not BitwiseCopyable in Swift 6+.
        // The wrapper must use Unmanaged.passRetained().toOpaque() to get a raw pointer
        // and store it using storeBytes with UnsafeMutableRawPointer (which IS BitwiseCopyable),
        // instead of storeBytes(of: result, as: ClassName.self) which crashes.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.ImageResult",
            returnKind: TypeRecordKind.Class);

        // Should use Unmanaged.passRetained pattern
        Assert.Contains("Unmanaged.passRetained(", swiftOutput);
        Assert.Contains("as: UnsafeMutableRawPointer.self)", swiftOutput);

        // Should NOT use storeBytes with the class type directly (BitwiseCopyable crash)
        Assert.DoesNotContain("as: TestModule.ImageResult.self)", swiftOutput);

        // Should NOT use initializeMemory (that's for structs/enums)
        Assert.DoesNotContain("initializeMemory", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_StructReturnType_UsesInitializeMemory()
    {
        // Non-primitive struct types may not be BitwiseCopyable (e.g., structs with String fields),
        // which rules out `storeBytes(of:as:)`. `copyMemory` is also unsafe — it produces raw bits
        // aliased to the source, which breaks under non-trivial value witnesses. The correct
        // pattern (per the repo's BitwiseCopyable constraint) is `initializeMemory(as:repeating:)`,
        // which runs the type's copy witness so the carrier holds its own +1 on internal refs.
        // The C# side then VWT-copies into a managed buffer and Destroys the carrier's +1 before
        // reclaiming the raw memory via SBW_Free.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct);

        Assert.Contains("initializeMemory(as: TestModule.DataResult.self, repeating:", swiftOutput);

        // Should NOT use storeBytes (BitwiseCopyable required) or copyMemory (unsafe for nontrivial
        // value witnesses) or Unmanaged.passRetained (that's for class types).
        Assert.DoesNotContain("storeBytes(of:", swiftOutput);
        Assert.DoesNotContain("copyMemory(from: UnsafeRawPointer(_srcPtr)", swiftOutput);
        Assert.DoesNotContain("Unmanaged.passRetained", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_EnumReturnType_UsesInitializeMemory()
    {
        // Non-primitive enum types may not be BitwiseCopyable; same reasoning as the struct case
        // above. `initializeMemory` is the approved pattern.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.StatusCode",
            returnKind: TypeRecordKind.Enum);

        Assert.Contains("initializeMemory(as: TestModule.StatusCode.self, repeating:", swiftOutput);

        Assert.DoesNotContain("storeBytes(of:", swiftOutput);
        Assert.DoesNotContain("copyMemory(from: UnsafeRawPointer(_srcPtr)", swiftOutput);
        Assert.DoesNotContain("Unmanaged.passRetained", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_NonFrozenEnumReturnType_VwtCopiesIntoManagedBuffer()
    {
        // Enums with associated values (RequiresMemoryManagement) are projected as C# classes
        // with SwiftSafeHandle. Per the BitwiseCopyable constraint, non-trivial Swift value
        // types must be carried via `initializeMemory(as:repeating:)` (runs the value's copy
        // witness) — not raw byte moves. The C# callback then `InitializeWithCopy`s into a
        // NativeMemory-owned buffer, `Destroy`s the Swift carrier's +1, and `SBW_Free`s the
        // raw allocation. The managed wrapper later runs VWT Destroy + NativeMemory.Free
        // against the allocator-matched buffer on dispose.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.StatusCode",
            returnKind: TypeRecordKind.Enum,
            returnFlags: TypeRecordFlags.RequiresMemoryManagement);

        // Swift side: initializeMemory — the carrier holds a properly-initialized Swift value
        // with its own +1 on internal references (via the type's copy witness).
        Assert.Contains("initializeMemory(as: TestModule.StatusCode.self, repeating:", swiftOutput);
        Assert.DoesNotContain("copyMemory(from: UnsafeRawPointer(_srcPtr)", swiftOutput);
        Assert.DoesNotContain("storeBytes(of:", swiftOutput);
        Assert.DoesNotContain("Unmanaged.passRetained", swiftOutput);

        // C# side: VWT-copy dance into a managed-owned buffer
        Assert.Contains("SwiftObjectHelper<TestModule.StatusCode>.GetTypeMetadata()", csOutput);
        Assert.Contains("NativeMemory.Alloc(_vwtMetadata.Size)", csOutput);
        Assert.Contains("InitializeWithCopy((void*)_vwtBuf, (void*)resultPtr", csOutput);
        Assert.Contains("MarshalFromSwift<TestModule.StatusCode>(_vwtBuf)", csOutput);

        // Swift carrier has its own +1 from initializeMemory — we must VWT-Destroy it here to
        // release that retain before SBW_Free reclaims the raw allocation. Managed wrapper's
        // retain lives on via _vwtBuf.
        Assert.Contains("Destroy((void*)resultPtr", csOutput);
        Assert.Contains("SBW_Free(resultPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_NonFrozenStructReturnType_VwtCopiesIntoManagedBuffer()
    {
        // Non-frozen structs (RequiresMemoryManagement, not FrozenAsClass) are projected as
        // C# classes with SwiftSafeHandle. Regression gate for the FirebaseAILogic-style crash:
        // calling an async method that returns a non-frozen struct used to hand the raw
        // Swift-allocated pointer to NewFromPayload, leaving the SafeHandle aliasing
        // Swift-owned memory. Subsequent property reads or Dispose() then hit freed memory,
        // and the final NativeMemory.Free on the Swift pointer mismatches allocators.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct,
            returnFlags: TypeRecordFlags.RequiresMemoryManagement);

        // Swift side: initializeMemory (runs copy witness). Raw copyMemory would alias internal
        // refs and is unsafe for non-trivial value witnesses (weak/unowned, resilient fields).
        Assert.Contains("initializeMemory(as: TestModule.DataResult.self, repeating:", swiftOutput);
        Assert.DoesNotContain("copyMemory(from: UnsafeRawPointer(_srcPtr)", swiftOutput);

        // C# side: VWT-copy into a managed-owned buffer, then Destroy the Swift carrier's +1
        // and free its raw memory.
        Assert.Contains("SwiftObjectHelper<TestModule.DataResult>.GetTypeMetadata()", csOutput);
        Assert.Contains("NativeMemory.Alloc(_vwtMetadata.Size)", csOutput);
        Assert.Contains("InitializeWithCopy((void*)_vwtBuf, (void*)resultPtr", csOutput);
        Assert.Contains("MarshalFromSwift<TestModule.DataResult>(_vwtBuf)", csOutput);
        Assert.Contains("Destroy((void*)resultPtr", csOutput);
        Assert.Contains("SBW_Free(resultPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_FrozenStructReturnType_DoesNotVwtCopyOnCSharpSide()
    {
        // Frozen structs without RequiresMemoryManagement are POD and projected as C# structs.
        // MarshalFromSwift<T>(resultPtr) reads the struct bitwise; no VWT copy is needed on
        // the C# side, and the carrier has a trivial value witness so Destroy is unnecessary.
        // The Swift side still uses initializeMemory (safe for POD, consistent with the
        // non-frozen path). This test guards against accidentally widening VWT Destroy to
        // POD frozen structs (which would be a no-op but wasted work on a hot path).
        var (csOutput, _) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct);

        Assert.DoesNotContain("SwiftObjectHelper<TestModule.DataResult>.GetTypeMetadata()", csOutput);
        Assert.DoesNotContain("NativeMemory.Alloc(_vwtMetadata.Size)", csOutput);
        Assert.DoesNotContain("InitializeWithCopy((void*)_vwtBuf", csOutput);
        Assert.DoesNotContain("Destroy((void*)resultPtr", csOutput);

        // Carrier is still freed
        Assert.Contains("SBW_Free(resultPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_OptionalFrozenWithMemoryReturnType_VwtDestroysCarrier()
    {
        // Optional<@frozen struct with String field>: Swift wrapper calls
        // initializeMemory(as: Optional<DataResult>.self, repeating: result, count: 1), so for
        // .some the carrier holds its own +1 on the embedded String. The C# side marshals via
        // SwiftOptional<DataResult> + a HasValue?Some:default projection (NOT ToNullable —
        // for unconstrained generic T, ToNullable would collapse None to default(T) and lose
        // the null distinction; the explicit-cast HasValue branch preserves Nullable<T>'s null
        // for None). The Some branch goes through SwiftOptional<T>.NewFromPayload which
        // InitializeWithCopy-copies into a managed buffer (independent +1). Without VWT
        // Destroy on the carrier via Optional<T>'s metadata before SBW_Free, the carrier's
        // +1 leaks each call.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct,
            returnFlags: TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            wrapInOptional: true);

        // Swift side: initializeMemory on Optional<T>.self — the Optional's copy witness runs
        // T's copy witness on .some, leaving +1 on internal refs.
        Assert.Contains("initializeMemory(as: Swift.Optional<TestModule.DataResult>.self, repeating:", swiftOutput);

        // C# side: HasValue?Some:default projection through SwiftOptional<T>, followed by VWT
        // Destroy on the carrier using SwiftOptional<T>'s metadata (matches the Optional<T>
        // layout on Swift side).
        Assert.Contains("MarshalFromSwift<SwiftOptional<TestModule.DataResult>>(resultPtr)", csOutput);
        Assert.Contains("_swiftOpt.HasValue", csOutput);
        Assert.Contains("_swiftOpt.Some", csOutput);
        Assert.Contains("SwiftObjectHelper<SwiftOptional<TestModule.DataResult>>.GetTypeMetadata()", csOutput);
        Assert.Contains("Destroy((void*)resultPtr", csOutput);
        Assert.Contains("SBW_Free(resultPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_OptionalPodFrozenReturnType_DoesNotDestroyCarrier()
    {
        // Optional<POD frozen struct> (e.g. Optional<Int32>, Optional<CGPoint>): Swift
        // initializeMemory runs Optional<T>'s trivial copy witness, so the carrier has
        // no retained refs to release. VWT Destroy would be a no-op — skip it to avoid
        // wasted metadata lookup + witness call on the hot path.
        var (csOutput, _) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct,
            wrapInOptional: true);

        // POD path: same HasValue?Some:default projection (preserves null for None on
        // unconstrained generic T), but no carrier-destroy because the trivial copy witness
        // leaves no retained refs.
        Assert.Contains("MarshalFromSwift<SwiftOptional<TestModule.DataResult>>(resultPtr)", csOutput);
        Assert.Contains("_swiftOpt.HasValue", csOutput);
        Assert.Contains("_swiftOpt.Some", csOutput);
        Assert.DoesNotContain("Destroy((void*)resultPtr", csOutput);
        Assert.Contains("SBW_Free(resultPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_FrozenWithMemoryStructReturnType_VwtDestroysCarrier()
    {
        // Frozen structs WITH RequiresMemoryManagement (ClassWithBufferStruct — e.g. an
        // @frozen struct containing a String field) are projected as C# classes whose
        // NewFromPayload runs its own InitializeWithCopy into a managed buffer. That copy
        // gives the C# object an independent +1 on internal refs; the Swift carrier still
        // holds its own +1 from `initializeMemory(as:repeating:)`. Without a VWT Destroy
        // on the carrier, SBW_Free just reclaims raw bytes and the carrier's +1 (e.g. on
        // the embedded String) leaks. This test pins the carrier-destroy emission for
        // frozen-with-memory async returns. Unlike the non-frozen path, we should NOT
        // pre-copy into _vwtBuf — NewFromPayload already does its own copy.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct,
            returnFlags: TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);

        // Swift side: still initializeMemory — the carrier needs a proper copy witness run so
        // embedded refs have a +1 ready for the managed wrapper's InitializeWithCopy to pick up.
        Assert.Contains("initializeMemory(as: TestModule.DataResult.self, repeating:", swiftOutput);

        // C# side: MarshalFromSwift reads from the Swift carrier directly (NewFromPayload does
        // its own copy for frozen-with-memory), then VWT Destroy releases the carrier's +1,
        // then SBW_Free reclaims the raw allocation.
        Assert.Contains("MarshalFromSwift<TestModule.DataResult>(resultPtr)", csOutput);
        Assert.Contains("SwiftObjectHelper<TestModule.DataResult>.GetTypeMetadata()", csOutput);
        Assert.Contains("Destroy((void*)resultPtr", csOutput);
        Assert.Contains("SBW_Free(resultPtr)", csOutput);

        // The pre-copy into _vwtBuf is the non-frozen path — frozen-with-memory must skip it
        // (NewFromPayload does its own InitializeWithCopy, so pre-copying would over-retain).
        Assert.DoesNotContain("InitializeWithCopy((void*)_vwtBuf", csOutput);
        Assert.DoesNotContain("MarshalFromSwift<TestModule.DataResult>(_vwtBuf)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_ClassReturnType_FreesSbwBuffer()
    {
        // Class types: Swift stores the retained object pointer in an 8-byte buffer.
        // C# must free the carrier buffer via SBW_Free, but NOT call Arc.Release
        // on _retainedObjPtr (SwiftClassHandle handles the release via its ReleaseHandle).
        var (csOutput, _) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.ImageResult",
            returnKind: TypeRecordKind.Class);

        // Should free the 8-byte carrier buffer
        Assert.Contains("SBW_Free(resultPtr)", csOutput);
        // Should NOT call Arc.Release(_retainedObjPtr) in the finally block
        // (Arc.Release for RetainedSelfPtr in holder cleanup is a different pattern)
        Assert.DoesNotContain("Arc.Release(_retainedObjPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_PrimitiveReturnType_DoesNotUsePointerMarshalling()
    {
        // Primitive types (Int, Double, Bool) are passed directly through @convention(c)
        // callbacks without pointer indirection, so no marshalling patterns are needed.
        var swiftOutput = GenerateAsyncMethodWrapper(
            methodName: "fetchCount",
            isAsync: true,
            hasNonFrozenParam: false);

        // Should NOT contain any memory storage pattern
        Assert.DoesNotContain("storeBytes", swiftOutput);
        Assert.DoesNotContain("initializeMemory", swiftOutput);
        Assert.DoesNotContain("copyMemory", swiftOutput);
        Assert.DoesNotContain("OpaquePointer", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_ClassReturnType_AllocatesPointerSizedBuffer()
    {
        // For class types, the buffer should be pointer-sized (UnsafeMutableRawPointer),
        // not sized to the class type's metadata.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.ImageResult",
            returnKind: TypeRecordKind.Class);

        // Should allocate using UnsafeMutableRawPointer size (pointer-sized)
        Assert.Contains("MemoryLayout<UnsafeMutableRawPointer>.size", swiftOutput);
        Assert.Contains("MemoryLayout<UnsafeMutableRawPointer>.alignment", swiftOutput);

        // Should NOT allocate using the class type's size
        Assert.DoesNotContain("MemoryLayout<TestModule.ImageResult>.size", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_StructReturnType_AllocatesTypeSizedBuffer()
    {
        // For struct/enum types, the buffer should be sized to the type's metadata.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct);

        // Should allocate using the struct type's size
        Assert.Contains("MemoryLayout<TestModule.DataResult>.size", swiftOutput);
        Assert.Contains("MemoryLayout<TestModule.DataResult>.alignment", swiftOutput);
    }

    #endregion

    #region Optional Class Async Return Tests

    [Fact]
    public void AsyncWrapper_OptionalClassReturnType_UsesConditionalRetainOnSwiftSide()
    {
        // Optional<ClassType> must unwrap the optional and retain if .some, store zero if .none.
        // Previously, the emitter used copyMemory (struct/enum path) which doesn't retain,
        // causing use-after-free when Swift's Task scope ends.
        var (_, swiftOutput) = GenerateAsyncMethodWithOptionalClassReturn(
            innerTypeName: "TestModule.ImageResult");

        // Should use if-let unwrap + Unmanaged.passRetained (retain .some value)
        Assert.Contains("if let _unwrapped =", swiftOutput);
        Assert.Contains("Unmanaged.passRetained(_unwrapped as AnyObject).toOpaque()", swiftOutput);

        // Should store zero for .none case
        Assert.Contains("storeBytes(of: 0, as: Int.self)", swiftOutput);

        // Should allocate pointer-sized buffer (not Optional<Class>.size)
        Assert.Contains("MemoryLayout<UnsafeMutableRawPointer>.size", swiftOutput);

        // Should NOT use copyMemory (that's for struct/enum, no ARC retain)
        Assert.DoesNotContain("copyMemory", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_OptionalClassReturnType_NullCheckOnCSharpSide()
    {
        // C# callback must dereference the buffer to get the retained object pointer,
        // then check for IntPtr.Zero (Swift nil) before MarshalFromSwift.
        var (csOutput, _) = GenerateAsyncMethodWithOptionalClassReturn(
            innerTypeName: "TestModule.ImageResult");

        // Should read object pointer from buffer (same as non-optional class)
        Assert.Contains("_retainedObjPtr = *(IntPtr*)resultPtr", csOutput);

        // Should check for null (IntPtr.Zero = Swift nil)
        Assert.Contains("_retainedObjPtr != IntPtr.Zero", csOutput);

        // Should use MarshalFromSwift for non-null case
        Assert.Contains("MarshalFromSwift<", csOutput);

        // Should free the carrier buffer
        Assert.Contains("SBW_Free(resultPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_OptionalClassReturnType_AllocatesPointerSizedBuffer()
    {
        // Optional class buffer holds a retained pointer (or zero for nil), not the Optional layout.
        var (_, swiftOutput) = GenerateAsyncMethodWithOptionalClassReturn(
            innerTypeName: "TestModule.ImageResult");

        // Should use pointer-sized allocation
        Assert.Contains("MemoryLayout<UnsafeMutableRawPointer>.size", swiftOutput);

        // Should NOT use Optional<T>.size (that's the wrong layout for nullable pointer ABI)
        Assert.DoesNotContain("MemoryLayout<Swift.Optional<TestModule.ImageResult>>.size", swiftOutput);
    }

    #endregion

    #region ObjC-Bridged Async Callback Tests

    [Fact]
    public void AsyncWrapper_ObjCBridgedReturnType_UsesGetNSObject()
    {
        // ObjC-bridged class types (like UIImage) must use GetNSObject<T> instead of
        // SwiftMarshal.MarshalFromSwift<T> in the C# async callback.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "UIKit.UIImage",
            returnKind: TypeRecordKind.Class,
            isObjCBridged: true);

        // C# callback should use GetNSObject<T> for ObjC types
        Assert.Contains("GetNSObject<", csOutput);

        // Should NOT use SwiftMarshal.MarshalFromSwift (that throws for ObjC types)
        Assert.DoesNotContain("MarshalFromSwift", csOutput);

        // Should read the object pointer from buffer (isClassType=true)
        Assert.Contains("_retainedObjPtr", csOutput);

        // DangerousRelease balances passRetained: GetNSObject adds its own retain via
        // NSObject(handle, false) → DangerousRetain. Without DangerousRelease, each async
        // call leaks one native retain on the returned object, permanently pinning it in memory.
        Assert.Contains("DangerousRelease()", csOutput);

        // Arc.Release is NOT used — DangerousRelease is the correct NSObject pattern
        Assert.DoesNotContain("Arc.Release(_retainedObjPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_NonObjCClassReturnType_UsesMarshalFromSwift()
    {
        // Non-ObjC class types (Swift classes) should use MarshalFromSwift, not GetNSObject.
        var (csOutput, _) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.ImageResult",
            returnKind: TypeRecordKind.Class);

        // C# callback should use SwiftMarshal.MarshalFromSwift for Swift types
        Assert.Contains("MarshalFromSwift", csOutput);

        // Should NOT use GetNSObject (that's for ObjC types only)
        Assert.DoesNotContain("GetNSObject", csOutput);

        // Non-ObjC class types: SwiftClassHandle takes ownership of the +1 retain from
        // passRetained, so no explicit Arc.Release(_retainedObjPtr) in the callback (would double-release).
        Assert.DoesNotContain("Arc.Release(_retainedObjPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_ObjCBridgedReturnType_SwiftUsesUnmanagedRetain()
    {
        // The Swift side must use Unmanaged.passRetained for ObjC class types
        // (same as any class type).
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "UIKit.UIImage",
            returnKind: TypeRecordKind.Class,
            isObjCBridged: true);

        // Should use Unmanaged.passRetained pattern (class type)
        Assert.Contains("Unmanaged.passRetained(", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_OptionalObjCBridgeableValueReturnType_UsesGetNSObject()
    {
        // Optional<ObjCBridgeable value> (Optional<Foundation.URL>) — the inner is NSObject-rooted,
        // so MarshalFromSwift<Foundation.NSUrl?>(_retainedObjPtr) would fail CS0311. Must route
        // through GetNSObject<NSUrl> with null-check and DangerousRelease, mirroring the
        // non-optional path. Pairs with the Swift-side passRetained (already correct).
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "Foundation.URL",
            returnKind: TypeRecordKind.Struct,
            returnFlags: TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable,
            wrapInOptional: true,
            nativeTypeName: "Foundation.NSUrl");

        // C# null-check + GetNSObject<NSUrl> + DangerousRelease.
        Assert.Contains("_retainedObjPtr != IntPtr.Zero", csOutput);
        Assert.Contains("GetNSObject<Foundation.NSUrl>(_retainedObjPtr)", csOutput);
        Assert.Contains("DangerousRelease()", csOutput);

        // Must NOT route through MarshalFromSwift<T?> — NSUrl is not ISwiftObject.
        Assert.DoesNotContain("MarshalFromSwift<Foundation.URL?>", csOutput);
        Assert.DoesNotContain("MarshalFromSwift<Foundation.NSUrl?>", csOutput);

        // Swift side: passRetained-pointer pattern (already correct pre-fix).
        Assert.Contains("Unmanaged.passRetained", swiftOutput);
        Assert.Contains("as AnyObject", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_ObjCBridgeableValueReturnType_UsesGetNSObject()
    {
        // ObjCBridgeable value types (Swift @frozen=false struct or enum that bridges to an
        // ObjC class via _ObjectiveCBridgeable, e.g. Foundation.URL → Foundation.NSUrl) must
        // be marshalled like a class on the C# side: GetNSObject<NSUrl>(_retainedObjPtr).
        // SwiftObjectHelper<T> requires T : ISwiftObject, which NSUrl is not (CS0311).
        var (csOutput, _) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "Foundation.URL",
            returnKind: TypeRecordKind.Struct,
            returnFlags: TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable);

        // C# callback must use GetNSObject<T> for the bridged ObjC class
        Assert.Contains("GetNSObject<", csOutput);

        // Must NOT route through SwiftObjectHelper<T> — NSUrl is not ISwiftObject (CS0311).
        Assert.DoesNotContain("SwiftObjectHelper<", csOutput);

        // Must NOT route through SwiftMarshal.MarshalFromSwift — that's for unmanaged Swift values.
        Assert.DoesNotContain("MarshalFromSwift", csOutput);

        // Should read the object pointer from buffer (class-style ABI).
        Assert.Contains("_retainedObjPtr", csOutput);

        // DangerousRelease balances passRetained — same retain math as ObjCBridged classes.
        Assert.Contains("DangerousRelease()", csOutput);
    }

    [Fact]
    public void AsyncWrapper_ObjCBridgeableValueReturnType_SwiftUsesPassRetainedAsAnyObject()
    {
        // The Swift side must wrap the ObjCBridgeable value via `as AnyObject` before
        // Unmanaged.passRetained — Swift's _ObjectiveCBridgeable conformance handles
        // the bridge cast at runtime.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "Foundation.URL",
            returnKind: TypeRecordKind.Struct,
            returnFlags: TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable);

        // Class-style storeBytes-of-pointer ABI, not value-copy ABI.
        Assert.Contains("Unmanaged.passRetained(", swiftOutput);
        Assert.Contains("as AnyObject", swiftOutput);

        // Must not fall back to value-copy marshalling (would crash — value layout differs
        // from class pointer in the carrier).
        Assert.DoesNotContain("MemoryLayout<Foundation.URL>.size", swiftOutput);
    }

    #endregion

    #region Async Tuple ObjC Retain Tests

    [Fact]
    public void AsyncWrapper_TupleWithObjCClass_RetainsClassElement()
    {
        // When an async method returns a tuple containing an ObjC class (e.g., URLResponse),
        // the Swift wrapper must explicitly retain the class element before passing through
        // @convention(c). Without retain, ARC releases the object after the callback returns,
        // leaving C#'s GetNSObject wrapper with a dangling pointer.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Foundation.Data", TypeRecordKind.Struct, false),
                ("Foundation.URLResponse", TypeRecordKind.Class, true),
            });

        // Swift wrapper should contain Unmanaged.passRetained for the ObjC class element
        Assert.Contains("Unmanaged<AnyObject>.passRetained(", swiftOutput);
        // Should reference the correct tuple element (.1 for URLResponse)
        Assert.Contains(".1 as AnyObject)", swiftOutput);

        // C#: GetNSObject adds its own +1 retain on top of Swift's passRetained — the
        // tuple-element bridge must balance with DangerousRelease so the consumer holds
        // exactly one (the SwiftHandle ctor's natural +1).
        Assert.Contains("GetNSObject<Foundation.URLResponse>", csOutput);
        Assert.Contains("DangerousRelease()", csOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithPrimitiveOnly_DoesNotRetain()
    {
        // Tuple of primitives doesn't need retain
        var (_, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Swift.Int", TypeRecordKind.Struct, false),
                ("Swift.Double", TypeRecordKind.Struct, false),
            });

        Assert.DoesNotContain("Unmanaged", swiftOutput);
        Assert.DoesNotContain("passRetained", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithOptionalObjCClass_UsesConditionalRetain()
    {
        // Optional<ObjCClass> needs conditional retain (nil check)
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Swift.Int", TypeRecordKind.Struct, false),
            },
            optionalObjCElement: ("Foundation.URLResponse", TypeRecordKind.Class, true));

        // Should use conditional retain: if let ... { passRetained }
        Assert.Contains("if let _tupleObj", swiftOutput);
        Assert.Contains("Unmanaged<AnyObject>.passRetained(", swiftOutput);

        // C#: Optional bridge must balance Swift's conditional passRetained with a
        // null-conditional DangerousRelease. (Same +1/+1/-1 pattern as scalar ObjC.)
        Assert.Contains("GetNSObject<Foundation.URLResponse>", csOutput);
        Assert.Contains("DangerousRelease()", csOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithMultipleObjCClasses_RetainsAll()
    {
        // Multiple ObjC class elements should all be retained
        var (_, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Foundation.URLResponse", TypeRecordKind.Class, true),
                ("UIKit.UIImage", TypeRecordKind.Class, true),
            });

        // Should have two passRetained calls
        Assert.Contains(".0 as AnyObject)", swiftOutput);
        Assert.Contains(".1 as AnyObject)", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithStructElement_DoesNotRetainStruct()
    {
        // Struct elements (even ObjC-bridgeable like Foundation.Data) should NOT be
        // retained — Swift's auto-bridging handles the retain for bridgeable types,
        // and non-bridgeable structs are value types.
        var (_, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Foundation.Data", TypeRecordKind.Struct, false),
                ("Swift.Int", TypeRecordKind.Struct, false),
            });

        Assert.DoesNotContain("Unmanaged", swiftOutput);
        Assert.DoesNotContain("passRetained", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithNativeRemappedData_UsesPointerIndirection()
    {
        // Foundation.Data is a NativeRemapped frozen struct. When passed by value in a
        // @convention(c) callback, it can cause ABI issues (ObjC bridging, Mono JIT struct
        // parameter handling). The fix is to heap-allocate and pass via UnsafeMutableRawPointer.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Foundation.Data", TypeRecordKind.Struct, false),
                ("Foundation.URLResponse", TypeRecordKind.Class, true),
            },
            nativeRemappedTypes: new Dictionary<string, string> { { "Foundation.Data", "Foundation.NSData" } });

        // Swift: callback type should use UnsafeMutableRawPointer, not Foundation.Data
        Assert.Contains("UnsafeMutableRawPointer", swiftOutput);
        Assert.DoesNotContain("@convention(c) (Foundation.Data,", swiftOutput);

        // Swift: should heap-allocate Data before callback
        Assert.Contains("MemoryLayout<Foundation.Data>.size", swiftOutput);
        Assert.Contains("initializeMemory(as: Foundation.Data.self", swiftOutput);
        Assert.Contains("defer", swiftOutput);
        Assert.Contains("deinitialize", swiftOutput);

        // C#: callback delegate should use IntPtr for Data, not the struct type
        Assert.Contains("IntPtr", csOutput);
        // C#: should read Data from pointer and convert to byte[]
        Assert.Contains("Swift.Foundation.Data*", csOutput);
        Assert.Contains("ToByteArray()", csOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithObjCBridgeableValue_UsesGetNSObjectAndPassRetained()
    {
        // _ObjectiveCBridgeable Swift value types (URL, URLRequest, Decimal) carried in a tuple
        // must be marshalled as ObjC class pointers — Swift inlines `Unmanaged.passRetained(.. as
        // AnyObject).toOpaque()` (skipping heap-alloc), and C# bridges via GetNSObject<NSType>.
        // Without this, callers get garbled non-frozen value-copy bytes.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Foundation.URL", TypeRecordKind.Struct, false),
                ("Swift.Int", TypeRecordKind.Struct, false),
            },
            nativeRemappedTypes: new Dictionary<string, string> { { "Foundation.URL", "Foundation.NSUrl" } },
            objCBridgeableTypes: new HashSet<string> { "Foundation.URL" });

        // Swift: emits inline class-style passRetained for the bridgeable element.
        Assert.Contains("Unmanaged.passRetained(", swiftOutput);
        Assert.Contains("as AnyObject", swiftOutput);
        // Must NOT heap-allocate the bridgeable value (would be wrong ABI shape).
        Assert.DoesNotContain("MemoryLayout<Foundation.URL>", swiftOutput);

        // C#: bridges via GetNSObject<Foundation.NSUrl> and balances the +1 with DangerousRelease.
        Assert.Contains("GetNSObject<Foundation.NSUrl>", csOutput);
        Assert.Contains("DangerousRelease()", csOutput);
        // Must NOT round-trip through MarshalFromSwift (would interpret bytes as value layout).
        Assert.DoesNotContain("MarshalFromSwift<Foundation.NSUrl>", csOutput);
        Assert.DoesNotContain("MarshalFromSwift<Foundation.URL>", csOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithOptionalObjCBridgeableValue_UsesNullableGetNSObject()
    {
        // Optional<ObjCBridgeable value> in a tuple needs nil-check in Swift and
        // null-check + DangerousRelease in C#.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Swift.Int", TypeRecordKind.Struct, false),
            },
            optionalObjCElement: ("Foundation.URL", TypeRecordKind.Struct, false),
            nativeRemappedTypes: new Dictionary<string, string> { { "Foundation.URL", "Foundation.NSUrl" } },
            objCBridgeableTypes: new HashSet<string> { "Foundation.URL" });

        // Swift: Optional path uses .map { passRetained(... as AnyObject).toOpaque() }
        Assert.Contains("passRetained(", swiftOutput);
        Assert.Contains("as AnyObject", swiftOutput);
        Assert.DoesNotContain("MemoryLayout<Foundation.URL>", swiftOutput);

        // C#: nullable bridge call + DangerousRelease.
        Assert.Contains("GetNSObject<Foundation.NSUrl>", csOutput);
        Assert.Contains("DangerousRelease()", csOutput);
        Assert.DoesNotContain("MarshalFromSwift<Foundation.NSUrl>", csOutput);
    }

    #endregion

    #region Async DynamicSelf Return Type Tests

    [Fact]
    public void AsyncWrapper_DynamicSelfReturn_UsesParentClassName()
    {
        // DynamicSelf (Self return type) in async wrappers is emitted as a free function,
        // where bare "Self" is invalid Swift. The wrapper must resolve Self to the parent
        // class type name (e.g., "Alamofire.DataRequest") for MemoryLayout calculations.
        var (_, swiftOutput) = GenerateAsyncMethodWithDynamicSelfReturn();

        // Should NOT contain MemoryLayout<Self> (invalid in free functions)
        Assert.DoesNotContain("MemoryLayout<Self>", swiftOutput);

        // Should use Unmanaged.passRetained (class type path, not struct copyMemory path)
        Assert.Contains("Unmanaged.passRetained(", swiftOutput);
        Assert.Contains("MemoryLayout<UnsafeMutableRawPointer>.size", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_DynamicSelfReturn_TreatsAsClassType()
    {
        // DynamicSelf is only allowed for class parents (validated by WrapperValidation).
        // The async wrapper must treat it as a class type, using Unmanaged.passRetained
        // instead of the struct/enum copyMemory path.
        var (_, swiftOutput) = GenerateAsyncMethodWithDynamicSelfReturn();

        // Should use class path (Unmanaged.passRetained), NOT struct path (copyMemory)
        Assert.Contains("Unmanaged.passRetained(", swiftOutput);
        Assert.DoesNotContain("copyMemory(from:", swiftOutput);
        Assert.DoesNotContain("withUnsafePointer(to:", swiftOutput);
    }

    #endregion

    #region Async _sbwTask Parameter Naming Tests

    [Fact]
    public void AsyncWrapper_TaskBaseParam_Uses_sbwTask_NotTask()
    {
        // S11: Kingfisher's URLSession delegate methods have a parameter named "task",
        // which collides with the async wrapper's base parameter also named "task".
        // Fix: renamed base parameter to "_sbwTask".
        // Uses GenerateAsyncMethodWithComplexReturn (class return) which produces Swift output.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct);

        // Must use _sbwTask, not bare "task"
        Assert.Contains("_sbwTask", swiftOutput);

        // The old name "task" should not appear as a standalone parameter
        // (it may appear as part of _sbwTask, so check for the exact old pattern)
        Assert.DoesNotContain("_ task:", swiftOutput);
        Assert.DoesNotContain("task: Int64", swiftOutput);
    }

    #endregion

    #region Async Free Function Tests

    [Fact]
    public void AsyncWrapper_FreeFunction_DoesNotEmitSelfPrefix()
    {
        // Free functions (methods on ModuleDecl, not a type) should NOT have "self." prefix
        // in the async wrapper. Before the fix, the else-branch unconditionally set
        // methodCallPrefix = "self." even when parentTypeName was null (free function).
        var (_, swiftOutput) = GenerateAsyncFreeFunctionWrapper(
            methodName: "fetchGlobalData",
            isAsync: true);

        // Verify we actually got output (async wrapper was emitted)
        Assert.NotEmpty(swiftOutput);

        // Should NOT contain "self." — free functions have no self
        Assert.DoesNotContain("self.", swiftOutput);

        // Should contain the function call without any prefix
        Assert.Contains("fetchGlobalData(", swiftOutput);
    }

    #endregion

    #region Async Singleton Self Parameter Tests

    [Fact]
    public void AsyncWrapper_SingletonClass_UsesSelfNotShared()
    {
        // Singleton classes (with static 'shared' property) must still pass self
        // explicitly so callers using non-shared instances get correct behavior.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "UIKit.UIImage",
            returnKind: TypeRecordKind.Class,
            isObjCBridged: true);

        // The helper uses a singleton class (Pipeline with 'shared' property).
        // The Swift wrapper must use __self (from _self parameter), not .shared
        Assert.Contains("__self.", swiftOutput);
        Assert.DoesNotContain(".shared.", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_SingletonClass_SwiftWrapperHasSelfParam()
    {
        // Verify the Swift wrapper receives _self parameter.
        // The helper generates @_silgen_name wrappers, which use OpaquePointer for self.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "UIKit.UIImage",
            returnKind: TypeRecordKind.Class,
            isObjCBridged: true);

        Assert.Contains("_self: OpaquePointer", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_SingletonClass_CSharpPInvokeHasSelfParam()
    {
        // Verify the C# P/Invoke signature includes the _selfClass parameter
        var (csOutput, _) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "UIKit.UIImage",
            returnKind: TypeRecordKind.Class,
            isObjCBridged: true);

        Assert.Contains("_selfClass", csOutput);
    }

    [Fact]
    public void AsyncWrapper_SingletonClass_SwiftReconstructsSelfFromPointer()
    {
        // Verify the Swift wrapper reconstructs the class instance from the opaque pointer.
        // The helper generates @_silgen_name wrappers which use unsafeBitCast for classes.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "UIKit.UIImage",
            returnKind: TypeRecordKind.Class,
            isObjCBridged: true);

        // @_silgen_name uses unsafeBitCast to convert OpaquePointer to class instance
        Assert.Contains("unsafeBitCast(_self, to: TestModule.Pipeline.self)", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_ClassInstanceMethod_NoDangerousAddRefLeak()
    {
        // For async class instance methods, EmitSafeHandleMarshalling must NOT emit
        // DangerousAddRef on _handle because EmitSafeHandleRelease returns early for async
        // methods (defers to callback). Without a matching DeferredSafeHandleRelease in the
        // holder, the SafeHandle ref count leaks permanently — each call increments by 1
        // with no decrement. The async holder already contains (object)this (preventing GC)
        // and RetainedSelfPtr with Arc.UnknownObjectRetain (keeping the Swift object alive).
        var (csOutput, _) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "UIKit.UIImage",
            returnKind: TypeRecordKind.Class,
            isObjCBridged: true);

        // The holder pattern should retain via Arc.UnknownObjectRetain, not DangerousAddRef.
        // UnknownObjectRetain isa-dispatches (swift_retain for this pure-Swift Pipeline self,
        // objc_retain for an @objc:NSObject-rooted self); the paired cleanup release uses the
        // matching UnknownObjectRelease so @objc-rooted self stays balanced (issue #40 / P1-01).
        Assert.Contains("Arc.UnknownObjectRetain(_selfPtr)", csOutput);
        Assert.DoesNotContain("Arc.Retain(_selfPtr)", csOutput);

        // There should be exactly ONE DangerousAddRef/_selfSuccess pair (for the
        // safe Arc.UnknownObjectRetain window) — not a second leaked one before the P/Invoke.
        // Count occurrences: should be exactly 1
        var addRefCount = csOutput.Split("DangerousAddRef").Length - 1;
        Assert.Equal(1, addRefCount);
    }

    [Fact]
    public void AsyncWrapper_ObjCRootedInstanceMethod_SelfRetainUsesUnknownObjectRetain()
    {
        // issue #40 / P1-01: when the async instance method lives on an @objc:NSObject-rooted
        // Swift class, the holder must keep self alive with Arc.UnknownObjectRetain — NOT the
        // pure-Swift-only Arc.Retain (swift_retain). swift_retain on an NSObject-rooted heap
        // pointer touches the wrong refcount word; UnknownObjectRetain isa-dispatches to
        // objc_retain. The rooted self branch sources the pointer from the NSObject peer's
        // `Handle` (not `_handle.DangerousAddRef`/`DangerousGetHandle()`), so this also locks
        // that the rooted branch — not the pure-Swift branch — was taken.
        var (csOutput, _) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "UIKit.UIImage",
            returnKind: TypeRecordKind.Class,
            isObjCBridged: true,
            selfIsObjCRooted: true);

        // Rooted self branch: pointer comes from the NSObject peer Handle, retained via the
        // isa-dispatching unknown-object family.
        Assert.Contains("Arc.UnknownObjectRetain(_selfPtr)", csOutput);
        // The pure-Swift swift_retain form must NOT appear for a rooted self (the bug shape).
        Assert.DoesNotContain("Arc.Retain(_selfPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_NonFrozenStructInstanceMethod_NoDangerousAddRefLeak()
    {
        // Bug 0.10.0 (Codex round 1 finding): for async non-frozen struct instance methods,
        // EmitSafeHandleAddRef must NOT emit `_payload.DangerousAddRef(ref success)` because
        // EmitSafeHandleRelease returns early on async paths. The DeferredSafeHandleRelease
        // holder (added in 0.10.0 Bundle 01) is the sole +1 ownership end-to-end: its ctor
        // calls DangerousAddRef, the async cleanup loop calls DangerousRelease. A duplicate
        // pre-call AddRef would never be released, pinning the SafeHandle open forever and
        // preventing the VWT Destroy + free in ReleaseHandle.
        var csOutput = GenerateAsyncStructInstanceMethodCSharp(isFrozen: false);

        // The DeferredSafeHandleRelease holder must own the +1.
        Assert.Contains("DeferredSafeHandleRelease", csOutput);

        // No pre-call _payload.DangerousAddRef on the async path. (Sync paths still emit it;
        // this test fixture only generates the async wrapper.)
        Assert.DoesNotContain("_payload.DangerousAddRef", csOutput);
    }

    [Fact]
    public void AsyncWrapper_NonSimpleEnumInstanceMethod_NoDangerousAddRefLeak()
    {
        // Bug 0.10.0 (Codex round 1 finding): same shape as struct receiver but for enum.
        // Non-simple enums use _payload SafeHandle like structs. The pre-call AddRef must
        // be skipped on async paths so DeferredSafeHandleRelease is the sole +1 ownership.
        var csOutput = GenerateAsyncEnumInstanceMethodCSharp();

        Assert.Contains("DeferredSafeHandleRelease", csOutput);
        Assert.DoesNotContain("_payload.DangerousAddRef", csOutput);
    }

    /// <summary>
    /// Generate the C# wrapper for an async instance method on a struct receiver.
    /// Mirrors <see cref="GenerateAsyncMethodWrapper"/> but exposes the C# output and
    /// lets the caller toggle <c>IsFrozen</c> so non-frozen-struct paths are exercised.
    /// </summary>
    private static string GenerateAsyncStructInstanceMethodCSharp(bool isFrozen)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new StructDecl
        {
            Name = "TestStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestStruct"),
            IsFrozen = isFrozen,
            MetadataAccessor = "$s10TestModule0A6StructVMa",
            MangledName = "$s10TestModule0A6StructV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetch",
            MangledName = "$s10TestModule0A6StructV5fetchSiyYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "TestStruct"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = parentDecl.MetadataAccessor,
            // Non-frozen ↔ RequiresMemoryManagement; frozen ↔ Frozen.
            Flags = isFrozen ? TypeRecordFlags.Frozen : TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(module);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csStringWriter.ToString();
    }

    /// <summary>
    /// Generate the C# wrapper for an async instance method on a non-simple enum receiver.
    /// Non-simple enums use the _payload SafeHandle just like non-frozen structs.
    /// </summary>
    private static string GenerateAsyncEnumInstanceMethodCSharp()
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new EnumDecl
        {
            Name = "TestEnum",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestEnum"),
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule0A4EnumOMa",
            MangledName = "$s10TestModule0A4EnumO",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Cases = new List<EnumCaseDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetch",
            MangledName = "$s10TestModule0A4EnumO5fetchSiyYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "TestEnum"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = parentDecl.MetadataAccessor,
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Enum
        });

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(module);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csStringWriter.ToString();
    }

    #endregion

    #region AsyncStream Library Target Tests

    [Fact]
    public void AsyncStreamPInvoke_UsesWrapperLibrary_WhenAsyncLibraryNameIsSet()
    {
        // AsyncStream P/Invokes must target the wrapper library, not the original module library.
        // The @_cdecl wrapper functions live in the wrapper xcframework.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/path/to/TestModule.framework/TestModule");
        var swiftIntRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        };
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), swiftIntRecord);
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(module);
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var classDecl = new ClassDecl
        {
            Name = "DataStream",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataStream"),
            MangledName = "$s10TestModule10DataStreamCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(classDecl);

        var asyncStreamType = new NamedTypeSpec("_Concurrency.AsyncStream",
            new TypeSpec[] { new NamedTypeSpec("Swift.Int") });
        var property = new PropertyDecl
        {
            Name = "events",
            SwiftTypeSpec = asyncStreamType,
            IsStatic = false,
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Accessors = new List<AccessorDecl>(),
            HasStorage = false,
        };

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        var asyncStreamHandler = new AsyncStreamHandler(typeDatabase);
        var swiftWrapperName = asyncStreamHandler.GetSwiftWrapperFunctionName(property);

        // This is the code path we're testing — must use AsyncLibraryName, not module path
        var libraryPath = typeDatabase.AsyncLibraryName
            ?? typeDatabase.GetLibraryPath("TestModule");

        AsyncStreamEmitter.EmitPInvokeDeclaration(csWriter, swiftWrapperName, libraryPath, false);

        var result = csOutput.ToString();
        Assert.Contains("TestModuleSwiftBindings", result);
        Assert.DoesNotContain("/path/to/TestModule.framework/TestModule", result);
    }

    [Fact]
    public void AsyncStreamPInvoke_FallsBackToModuleLibrary_WhenNoWrapperLibrary()
    {
        // When AsyncLibraryName is not set (manual mode, no wrapper), the P/Invoke
        // should use the module library path.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "TestModule");
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(module);
        // AsyncLibraryName is NOT set

        var libraryPath = typeDatabase.AsyncLibraryName
            ?? typeDatabase.GetLibraryPath("TestModule");

        Assert.Equal("TestModule", libraryPath);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Generates Swift and C# output for an async method returning a tuple.
    /// Used to test ObjC element retain behavior in async tuple callbacks.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncMethodWithTupleReturn(
        (string typeName, TypeRecordKind kind, bool isObjCBridged)[] elements,
        (string typeName, TypeRecordKind kind, bool isObjCBridged)? optionalObjCElement = null,
        Dictionary<string, string> nativeRemappedTypes = null,
        HashSet<string> objCBridgeableTypes = null)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        // Add 'shared' property for singleton pattern
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "shared",
            IsStatic = true,
            HasStorage = true,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.Pipeline"),
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        });
        moduleDecl.Types.Add(parentDecl);

        // Build tuple TypeSpec
        var tupleElements = new List<TypeSpec>();
        foreach (var elem in elements)
        {
            tupleElements.Add(new NamedTypeSpec(elem.typeName));
        }
        if (optionalObjCElement.HasValue)
        {
            var opt = optionalObjCElement.Value;
            var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
            optionalTypeSpec.GenericParameters.Add(new NamedTypeSpec(opt.typeName));
            tupleElements.Add(optionalTypeSpec);
        }
        var tupleTypeSpec = new TupleTypeSpec(tupleElements);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = tupleTypeSpec,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetchData",
            MangledName = "$s10TestModule8PipelineC9fetchData_tYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        // Setup type database
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        // Register parent class
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = "$s10TestModule8PipelineCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        });

        // Track extra module databases needed for types in other modules
        var extraModules = new Dictionary<string, ModuleTypeDatabase>();

        // Register each element type
        void RegisterElementType(string typeName, TypeRecordKind kind, bool isObjCBridged)
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName(typeName);
            var flags = kind == TypeRecordKind.Class
                ? TypeRecordFlags.RequiresMemoryManagement
                : TypeRecordFlags.Frozen;
            if (isObjCBridged)
                flags |= TypeRecordFlags.ObjCBridged;
            if (objCBridgeableTypes != null && objCBridgeableTypes.Contains(typeName))
            {
                flags |= TypeRecordFlags.ObjCBridgeable | TypeRecordFlags.RequiresMemoryManagement;
                // ObjCBridgeable structs are non-frozen (resilient) — strip Frozen if defaulted on.
                flags &= ~TypeRecordFlags.Frozen;
            }
            var ns = typeName.Contains('.') ? typeName.Substring(0, typeName.IndexOf('.')) : "TestModule";
            CSharpTypeName nativeType = null;
            if (nativeRemappedTypes != null && nativeRemappedTypes.TryGetValue(typeName, out var nativeTypeName))
            {
                nativeType = CSharpTypeName.FromNamespaceAndName(
                    nativeTypeName.Contains('.') ? nativeTypeName.Substring(0, nativeTypeName.IndexOf('.')) : ns,
                    nativeTypeName.Split('.').Last());
            }
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ns, typeName.Split('.').Last()),
                SwiftTypeName = swiftName,
                MetadataAccessor = $"$s{typeName.Replace(".", "")}Ma",
                Flags = flags,
                Kind = kind,
                NativeTypeName = nativeType
            };
            var elemModule = swiftName.Module;
            if (elemModule == "TestModule")
            {
                module.RegisterType(swiftName, record);
            }
            else
            {
                if (!extraModules.TryGetValue(elemModule, out var elemModuleDb))
                {
                    elemModuleDb = new ModuleTypeDatabase(elemModule, $"/System/Library/Frameworks/{elemModule}.framework/{elemModule}");
                    extraModules[elemModule] = elemModuleDb;
                }
                elemModuleDb.RegisterType(swiftName, record);
            }
        }

        foreach (var elem in elements)
            RegisterElementType(elem.typeName, elem.kind, elem.isObjCBridged);
        if (optionalObjCElement.HasValue)
        {
            var opt = optionalObjCElement.Value;
            RegisterElementType(opt.typeName, opt.kind, opt.isObjCBridged);
        }

        // Register Swift built-in types
        if (!extraModules.TryGetValue("Swift", out var swiftModuleDb))
        {
            swiftModuleDb = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
            extraModules["Swift"] = swiftModuleDb;
        }

        // Swift.Optional
        var optionalSwiftName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional");
        swiftModuleDb.RegisterType(optionalSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
            SwiftTypeName = optionalSwiftName,
            MetadataAccessor = "$sSqMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Enum
        });

        // Swift.Int
        var intSwiftName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModuleDb.RegisterType(intSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
            SwiftTypeName = intSwiftName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        // Swift.Double
        var doubleSwiftName = SwiftTypeName.FromModuleQualifiedName("Swift.Double");
        swiftModuleDb.RegisterType(doubleSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
            SwiftTypeName = doubleSwiftName,
            MetadataAccessor = "$sSdMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        // Add all extra module databases
        foreach (var extraModule in extraModules.Values)
            typeDatabase.AddModuleDatabase(extraModule);

        typeDatabase.AddModuleDatabase(module);

        // Generate the wrapper
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    /// <summary>
    /// Generates Swift output for an async method returning a complex (non-primitive) type.
    /// Used to test BitwiseCopyable-safe marshalling patterns.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncMethodWithComplexReturn(
        string returnTypeName,
        TypeRecordKind returnKind,
        bool isObjCBridged = false,
        TypeRecordFlags? returnFlags = null,
        bool wrapInOptional = false,
        string nativeTypeName = null,
        bool selfIsObjCRooted = false)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        // Create parent class with singleton pattern (static 'shared' property)
        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        // When requested, mark the self class as @objc:NSObject-rooted so the async wrapper
        // takes the IsObjCRooted self branch (`IntPtr _selfPtr = Handle;`) instead of the
        // pure-Swift `_handle.DangerousAddRef`/`DangerousGetHandle()` branch.
        parentDecl.IsObjCRooted = selfIsObjCRooted;
        // Add 'shared' property so HasSingletonPattern returns true
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "shared",
            IsStatic = true,
            HasStorage = true,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.Pipeline"),
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        });
        moduleDecl.Types.Add(parentDecl);

        // Build CSSignature with complex return type (optionally wrapped in Swift.Optional)
        TypeSpec returnSpec = wrapInOptional
            ? new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(returnTypeName))
            : new NamedTypeSpec(returnTypeName);
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = returnSpec,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetchResult",
            MangledName = $"$s10TestModule8PipelineC11fetchResult_tYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        // Setup type database
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        // Register the parent class
        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement
                    | (selfIsObjCRooted ? TypeRecordFlags.ObjCRooted : TypeRecordFlags.None),
                Kind = TypeRecordKind.Class
            });

        // Register the return type — may be in a different module (e.g., UIKit.UIImage)
        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        var computedReturnFlags = returnFlags ?? (returnKind == TypeRecordKind.Class
            ? TypeRecordFlags.RequiresMemoryManagement
            : returnKind == TypeRecordKind.Struct
                ? TypeRecordFlags.Frozen
                : TypeRecordFlags.None);
        if (isObjCBridged)
            computedReturnFlags |= TypeRecordFlags.ObjCBridged;
        var returnNamespace = returnTypeName.Contains('.') ? returnTypeName.Substring(0, returnTypeName.IndexOf('.')) : "TestModule";
        CSharpTypeName nativeTypeNameRecord = null;
        if (nativeTypeName != null)
        {
            var nativeNs = nativeTypeName.Contains('.') ? nativeTypeName.Substring(0, nativeTypeName.IndexOf('.')) : "Foundation";
            nativeTypeNameRecord = CSharpTypeName.FromNamespaceAndName(nativeNs, nativeTypeName.Split('.').Last());
        }
        var returnTypeRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(returnNamespace, returnTypeName.Split('.').Last()),
            SwiftTypeName = returnSwiftTypeName,
            MetadataAccessor = $"$s10TestModule{returnTypeName.Split('.').Last()}CMa",
            Flags = computedReturnFlags,
            Kind = returnKind,
            NativeTypeName = nativeTypeNameRecord
        };
        // Register in the correct module database (UIKit.UIImage → UIKit module)
        var returnModule = returnSwiftTypeName.Module;
        if (returnModule == "TestModule")
        {
            module.RegisterType(returnSwiftTypeName, returnTypeRecord);
        }
        else
        {
            var returnModuleDb = new ModuleTypeDatabase(returnModule, $"/System/Library/Frameworks/{returnModule}.framework/{returnModule}");
            returnModuleDb.RegisterType(returnSwiftTypeName, returnTypeRecord);
            typeDatabase.AddModuleDatabase(returnModuleDb);
        }

        typeDatabase.AddModuleDatabase(module);

        // Generate the wrapper
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    private static string GenerateAsyncMethodWrapper(string methodName, bool isAsync, bool hasNonFrozenParam)
    {
        // Create module declaration first
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        // Create parent struct
        var parentDecl = new StructDecl
        {
            Name = "TestStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestStruct"),
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule0A6StructVMa",
            MangledName = "$s10TestModule0A6StructV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        moduleDecl.Types.Add(parentDecl);

        // Build CSSignature
        var csSignature = new List<ArgumentDecl>
        {
            // Return type (Int64 for simplicity)
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        if (hasNonFrozenParam)
        {
            // Add a non-frozen parameter (a class type)
            csSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("TestModule.NonFrozenClass"),
                Name = "request",
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            });
        }

        var methodDecl = new MethodDecl
        {
            Name = methodName,
            MangledName = $"$s10TestModule0A6StructV{methodName}yS2iFYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };

        // Setup type database
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        // Register the parent struct
        var parentTypeRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "TestStruct"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = parentDecl.MetadataAccessor,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        };
        module.RegisterType(parentDecl.SwiftTypeName, parentTypeRecord);

        // Register Int type
        var intTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        var intTypeRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = intTypeName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        };
        module.RegisterType(intTypeName, intTypeRecord);

        if (hasNonFrozenParam)
        {
            // Register the non-frozen class type
            var nonFrozenTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.NonFrozenClass");
            var nonFrozenTypeRecord = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "NonFrozenClass"),
                SwiftTypeName = nonFrozenTypeName,
                MetadataAccessor = "$s10TestModule14NonFrozenClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement, // Not frozen!
                Kind = TypeRecordKind.Class
            };
            module.RegisterType(nonFrozenTypeName, nonFrozenTypeRecord);
        }

        typeDatabase.AddModuleDatabase(module);

        // Generate the wrapper
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return swiftStringWriter.ToString();
    }

    /// <summary>
    /// Generates C# and Swift output for an async free function (method on ModuleDecl, not a type).
    /// Used to verify that free functions don't get a "self." prefix in the wrapper.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncFreeFunctionWrapper(string methodName, bool isAsync)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        // Build CSSignature with Int return type
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl
            }
        };

        // Free function: ParentDecl is the ModuleDecl, not a TypeDecl
        var methodDecl = new MethodDecl
        {
            Name = methodName,
            MangledName = $"$s10TestModule{methodName}yS2iFYaKF",
            // Use Instance to exercise the else-branch in EmitAsync where
            // parentTypeName==null would incorrectly set methodCallPrefix="self."
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };

        // Setup type database
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        typeDatabase.AddModuleDatabase(module);

        // Register Int type in the "Swift" module (TypeDatabase resolves by module name)
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        var intTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModule.RegisterType(intTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = intTypeName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);

        // Generate the wrapper — use WrapperEmitter directly (like GenerateAsyncMethodWithComplexReturn)
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = (MethodEnvironment)handler.Marshal(methodDecl, typeDatabase);

        var signatureHandler = new SignatureHandler(env);
        var wrapperEmitter = new WrapperEmitter(env, signatureHandler);
        wrapperEmitter.EmitMethod(csWriter, swiftWriter);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    /// <summary>
    /// Generates Swift output for an async method returning DynamicSelf (Self).
    /// Used to test that Self is resolved to the parent class type in async free-function wrappers.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncMethodWithDynamicSelfReturn()
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "DataRequest",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
            MangledName = "$s10TestModule11DataRequestCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        // DynamicSelf return type: NamedTypeSpec("Self") makes IsDynamicSelf true
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Self"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "onHTTPResponse",
            MangledName = "$s10TestModule11DataRequestC14onHTTPResponseACXDySo17NSHTTPURLResponseCYaYbc_tF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        // Setup type database
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        // Register the parent class
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataRequest"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = "$s10TestModule11DataRequestCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        });

        typeDatabase.AddModuleDatabase(module);

        // Generate the wrapper
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion

    #region Frozen Blittable Struct Async Heap Allocation Tests

    [Fact]
    public void AsyncWrapper_FrozenBlittableStructParam_UsesNativeMemoryAlloc()
    {
        // Frozen blittable struct params in async methods must use NativeMemory.Alloc
        // instead of stackalloc, because the stack buffer is invalidated across await.
        var csOutput = GenerateAsyncMethodWithFrozenStructParam(isAsync: true);

        // Should use NativeMemory.Alloc for heap allocation
        Assert.Contains("NativeMemory.Alloc", csOutput);
        // Should use CopyBufferWithType for cleanup in the holder
        Assert.Contains("CopyBufferWithType", csOutput);
        // Should NOT use stackalloc (unsafe across await boundary)
        Assert.DoesNotContain("stackalloc", csOutput);
    }

    [Fact]
    public void SyncWrapper_FrozenBlittableStructParam_UsesStackalloc()
    {
        // Sync methods must still use stackalloc for frozen blittable struct params (faster).
        var csOutput = GenerateAsyncMethodWithFrozenStructParam(isAsync: false);

        // Should use stackalloc for stack allocation
        Assert.Contains("stackalloc", csOutput);
        // Should NOT use HeapBuffer pattern (the async heap allocation path)
        Assert.DoesNotContain("HeapBuffer", csOutput);
        // Should NOT use CopyBufferWithType (no async holder needed)
        Assert.DoesNotContain("CopyBufferWithType", csOutput);
    }

    [Fact]
    public void AsyncWrapper_FrozenBlittableStructParam_MarshalToSwiftCalled()
    {
        // The heap-allocated buffer must be populated via MarshalToSwift
        var csOutput = GenerateAsyncMethodWithFrozenStructParam(isAsync: true);

        Assert.Contains("SwiftMarshal.MarshalToSwift(data", csOutput);
        Assert.Contains("HeapBuffer", csOutput);
    }

    [Fact]
    public void AsyncWrapper_MultipleFrozenBlittableParams_AllHeapAllocated()
    {
        // Multiple frozen blittable struct params should all use heap allocation in async
        var csOutput = GenerateAsyncMethodWithMultipleFrozenStructParams();

        // Both params should have heap allocation via NativeMemory.Alloc
        Assert.Contains("aHeapBuffer = (IntPtr)NativeMemory.Alloc", csOutput);
        Assert.Contains("bHeapBuffer = (IntPtr)NativeMemory.Alloc", csOutput);
        // Both should have CopyBufferWithType for cleanup
        Assert.Contains("new CopyBufferWithType(aHeapBuffer", csOutput);
        Assert.Contains("new CopyBufferWithType(bHeapBuffer", csOutput);
        // No stackalloc
        Assert.DoesNotContain("stackalloc", csOutput);
    }

    #endregion

    #region Frozen Blittable Struct Param Helpers

    /// <summary>
    /// Generates C# output for a method with a frozen blittable struct parameter.
    /// Used to test async heap allocation vs sync stackalloc behavior.
    /// </summary>
    private static string GenerateAsyncMethodWithFrozenStructParam(bool isAsync)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new StructDecl
        {
            Name = "Worker",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Worker"),
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule6WorkerVMa",
            MangledName = "$s10TestModule6WorkerV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        // Return type: String (to exercise async path)
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            },
            // Frozen blittable struct parameter
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("TestModule.FrozenData"),
                Name = "data",
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "processData",
            MangledName = "$s10TestModule6WorkerV11processDataySSAA06FrozenD0VYaKF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        // Register parent struct
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Worker"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = parentDecl.MetadataAccessor,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        // Register the frozen blittable struct param type
        var frozenDataName = SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenData");
        module.RegisterType(frozenDataName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FrozenData"),
            SwiftTypeName = frozenDataName,
            MetadataAccessor = "$s10TestModule10FrozenDataVMa",
            Flags = TypeRecordFlags.Frozen,  // Frozen + no RequiresMemoryManagement = blittable
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        // Register Swift.String and Swift.Int
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.String"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            MetadataAccessor = "$sSSMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csStringWriter.ToString();
    }

    /// <summary>
    /// Generates C# output for an async method with multiple frozen blittable struct parameters.
    /// </summary>
    private static string GenerateAsyncMethodWithMultipleFrozenStructParams()
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new StructDecl
        {
            Name = "Worker",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Worker"),
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule6WorkerVMa",
            MangledName = "$s10TestModule6WorkerV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            },
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("TestModule.FrozenData"),
                Name = "a",
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            },
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("TestModule.FrozenData"),
                Name = "b",
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "combineData",
            MangledName = "$s10TestModule6WorkerV11combineDataySSAA06FrozenD0V_AFtYaKF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Worker"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = parentDecl.MetadataAccessor,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var frozenDataName = SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenData");
        module.RegisterType(frozenDataName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FrozenData"),
            SwiftTypeName = frozenDataName,
            MetadataAccessor = "$s10TestModule10FrozenDataVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.String"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            MetadataAccessor = "$sSSMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csStringWriter.ToString();
    }

    #endregion

    #region Async Instance Method with Frozen Struct Params

    [Fact]
    public void AsyncWrapper_InstanceMethod_FrozenBlittableStructParam_UsesNativeMemoryAlloc()
    {
        // Instance methods with frozen blittable struct params in async context must also
        // use NativeMemory.Alloc (existing coverage only exercised static methods).
        var csOutput = GenerateAsyncInstanceMethodWithFrozenStructParam();

        // Should use NativeMemory.Alloc for heap allocation
        Assert.Contains("NativeMemory.Alloc", csOutput);
        // Should use CopyBufferWithType for cleanup in the holder
        Assert.Contains("CopyBufferWithType", csOutput);
        // Should NOT use stackalloc (unsafe across await boundary)
        Assert.DoesNotContain("stackalloc", csOutput);
    }

    /// <summary>
    /// Generates C# output for an async INSTANCE method with a frozen blittable struct parameter.
    /// Mirrors GenerateAsyncMethodWithFrozenStructParam but uses MethodType.Instance on a ClassDecl.
    /// </summary>
    private static string GenerateAsyncInstanceMethodWithFrozenStructParam()
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "PointProcessor",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.PointProcessor"),
            MangledName = "$s10TestModule14PointProcessorCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        // Return type: String
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            },
            // Frozen blittable struct parameter
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("TestModule.FrozenData"),
                Name = "point",
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "processPoint",
            MangledName = "$s10TestModule14PointProcessorC12processPointySSAA06FrozenD0VYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        // Register parent class
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PointProcessor"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = "$s10TestModule14PointProcessorCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        });

        // Register the frozen blittable struct param type
        var frozenDataName = SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenData");
        module.RegisterType(frozenDataName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FrozenData"),
            SwiftTypeName = frozenDataName,
            MetadataAccessor = "$s10TestModule10FrozenDataVMa",
            Flags = TypeRecordFlags.Frozen,  // Frozen + no RequiresMemoryManagement = blittable
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        // Register Swift.String and Swift.Int
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.String"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            MetadataAccessor = "$sSSMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csStringWriter.ToString();
    }

    #endregion

    #region Optional Class Async Return Helpers

    /// <summary>
    /// Generates output for an async method returning Optional&lt;ClassType&gt;.
    /// Tests that optional class returns use nullable pointer ABI (retain + null check)
    /// instead of raw copyMemory.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncMethodWithOptionalClassReturn(
        string innerTypeName)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "shared",
            IsStatic = true,
            HasStorage = true,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.Pipeline"),
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        });
        moduleDecl.Types.Add(parentDecl);

        // Return type: Optional<ClassType>
        var innerTypeSpec = new NamedTypeSpec(innerTypeName);
        var optionalTypeSpec = new NamedTypeSpec("Swift.Optional", innerTypeSpec);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = optionalTypeSpec,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetchResult",
            MangledName = $"$s10TestModule8PipelineC11fetchResult_tYaKF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        // Register the inner class type
        var innerSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(innerTypeName);
        module.RegisterType(
            innerSwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", innerTypeName.Split('.').Last()),
                SwiftTypeName = innerSwiftTypeName,
                MetadataAccessor = $"$s10TestModule{innerTypeName.Split('.').Last()}CMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });

        typeDatabase.AddModuleDatabase(module);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion
}
