// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import simd

// MARK: - Swift Concurrency Interop Hook
//
// Swift's cooperative concurrency model uses a dedicated thread pool that .NET
// threads don't participate in. When C# calls a Swift async method via P/Invoke,
// the Swift task is enqueued on the cooperative pool but never executes because
// no .NET thread runs Swift's executor loop.
//
// This library hooks swift_task_enqueueGlobal_hook to redirect all globally-
// enqueued Swift tasks to GCD, where they will actually run.
//
// Known limitations:
//   - @MainActor tasks are NOT intercepted (swift_task_enqueueMainExecutor_hook
//     is buggy in Swift 5.5-6.0 and often not invoked by the runtime)
//   - Task cancellation does not propagate through GCD dispatch
//   - Custom actor executors are not intercepted — only plain Task {} and
//     Task.detached {} go through the global hook

fileprivate typealias EnqueueOriginal = @convention(thin) (UnownedJob) -> Void
fileprivate typealias EnqueueHook = @convention(thin) (UnownedJob, EnqueueOriginal) -> Void

/// A minimal executor that runs Swift jobs on GCD.
@available(macOS 10.15, iOS 13.0, tvOS 13.0, watchOS 6.0, *)
final class GCDExecutor: SerialExecutor {
    static let shared = GCDExecutor()
    private let queue = DispatchQueue(label: "swift-bindings.executor", qos: .userInitiated)

    func enqueue(_ job: UnownedJob) {
        let executor = asUnownedSerialExecutor()
        queue.async {
            job.runSynchronously(on: executor)
        }
    }

    func asUnownedSerialExecutor() -> UnownedSerialExecutor {
        UnownedSerialExecutor(ordinary: self)
    }
}

// MARK: - Initialization

private var _isInitialized = false

/// Initialize Swift concurrency for interop with C#/.NET.
///
/// Hooks `swift_task_enqueueGlobal_hook` to redirect Swift tasks to GCD
/// instead of Swift's cooperative thread pool. Call once before any async
/// Swift calls from C#.
@_cdecl("SwiftBindings_InitializeConcurrency")
public func initializeConcurrency() {
    guard !_isInitialized else { return }

    guard let handle = dlopen(nil, 0),
          let hookPtr = dlsym(handle, "swift_task_enqueueGlobal_hook") else {
        return
    }

    let hook = hookPtr.assumingMemoryBound(to: EnqueueHook?.self)
    hook.pointee = { job, _ in
        GCDExecutor.shared.enqueue(job)
    }

    _isInitialized = true
}

/// Check if concurrency has been initialized.
@_cdecl("SwiftBindings_IsConcurrencyInitialized")
public func isConcurrencyInitialized() -> Bool {
    return _isInitialized
}

// MARK: - Existential Type Metadata

// Marker protocols used to construct existential metadata with N witness table slots.
// The specific protocols don't matter — only N does, since the VWT layout depends
// solely on the number of protocol witness table slots.
private protocol _EP0 {}
private protocol _EP1 {}
private protocol _EP2 {}
private protocol _EP3 {}
private protocol _EP4 {}
private protocol _EP5 {}
private protocol _EP6 {}
private protocol _EP7 {}

/// Returns existential type metadata for the given number of protocol constraints.
///
/// Uses Swift's type system to construct existential metadata directly, which is
/// both simpler and more correct than calling swift_getExistentialTypeMetadata
/// with raw protocol descriptor pointers (which require ProtocolDescriptorRef format).
///
/// - Parameter numProtocols: Number of protocol constraints (0 for 'Any', 1-8 for typed).
/// - Returns: Metadata pointer, or nil if numProtocols is out of range.
@_cdecl("SwiftBindings_GetExistentialTypeMetadata")
public func getExistentialTypeMetadata(_ numProtocols: Int) -> UnsafeMutableRawPointer? {
    let type: Any.Type?
    switch numProtocols {
    case 0: type = Any.self
    case 1: type = (any _EP0).self
    case 2: type = (any _EP0 & _EP1).self
    case 3: type = (any _EP0 & _EP1 & _EP2).self
    case 4: type = (any _EP0 & _EP1 & _EP2 & _EP3).self
    case 5: type = (any _EP0 & _EP1 & _EP2 & _EP3 & _EP4).self
    case 6: type = (any _EP0 & _EP1 & _EP2 & _EP3 & _EP4 & _EP5).self
    case 7: type = (any _EP0 & _EP1 & _EP2 & _EP3 & _EP4 & _EP5 & _EP6).self
    case 8: type = (any _EP0 & _EP1 & _EP2 & _EP3 & _EP4 & _EP5 & _EP6 & _EP7).self
    default: type = nil
    }
    guard let type else { return nil }
    return unsafeBitCast(type, to: UnsafeMutableRawPointer.self)
}

/// Returns the type metadata for `any Swift.Error` existential type.
///
/// Unlike generic existential metadata (N-protocol slot), `any Error` is a specific
/// well-known existential type whose metadata includes the Swift.Error protocol
/// descriptor. Used by C# `SwiftResult<TSuccess, TFailure>` to construct Result
/// metadata when TFailure = AnyError.
@_cdecl("SBW_AnyError_TypeMetadata")
public func sbw_anyErrorTypeMetadata() -> UnsafeMutableRawPointer {
    return unsafeBitCast((any Error).self, to: UnsafeMutableRawPointer.self)
}

/// Extracts a human-readable description from a Swift `any Error` existential container.
///
/// Takes a pointer to the 5-word existential container (3 payload + metadata + witness table)
/// that C# `AnyError` wraps, loads it as `any Error`, and returns a heap-allocated C string
/// via `String(describing:)`. The caller (C#) is responsible for freeing the returned buffer
/// via `NativeMemory.Free`.
@_cdecl("SBW_AnyError_GetDescription")
public func sbw_anyErrorGetDescription(_ containerPtr: UnsafeRawPointer) -> UnsafeMutablePointer<CChar>? {
    let error = containerPtr.load(as: (any Error).self)
    let desc = String(describing: error)
    return desc.withCString { cStr in
        let len = strlen(cStr) + 1
        let buf = UnsafeMutablePointer<CChar>.allocate(capacity: len)
        buf.initialize(from: cStr, count: len)
        return buf
    }
}

// MARK: - SwiftString Wrapper Functions
//
// SwiftString.cs uses CallConvSwift P/Invokes for ToString() and Length,
// which trigger the Mono JIT assertion at jit-info.c:918. These @_cdecl
// wrappers perform string operations entirely on the Swift side, returning
// results via C-compatible types callable with CallingConvention.Cdecl.
//
// The buffer pointer points to the 16-byte raw representation of a
// Swift.String (2 words on arm64), which is the same layout as
// SwiftString.Buffer on the C# side.

/// Converts a Swift String buffer to UTF-8 bytes.
///
/// Reads a `String` from the raw 2-word buffer at `bufferPtr`, extracts its
/// UTF-8 representation, and returns an allocated byte buffer that the caller
/// must free with `SBW_SwiftString_FreeUtf8`.
///
/// - Parameters:
///   - bufferPtr: Pointer to the 16-byte Swift.String raw representation.
///   - outPtr: On return, pointer to the allocated UTF-8 byte buffer (nil if empty).
///   - outLen: On return, the number of UTF-8 bytes.
@_cdecl("SBW_SwiftString_ToUtf8")
public func sbw_swiftStringToUtf8(
    _ bufferPtr: UnsafeRawPointer,
    _ outPtr: UnsafeMutablePointer<UnsafeMutablePointer<UInt8>?>,
    _ outLen: UnsafeMutablePointer<Int>
) {
    // assumingMemoryBound + .pointee creates a retain-balanced copy:
    // increments refcount on read, decrements when `str` goes out of scope.
    // The original buffer at bufferPtr is unaffected.
    let str = bufferPtr.assumingMemoryBound(to: String.self).pointee

    let utf8Array = Array(str.utf8)
    if utf8Array.isEmpty {
        outPtr.pointee = nil
        outLen.pointee = 0
        return
    }

    let count = utf8Array.count
    let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: count)
    utf8Array.withUnsafeBufferPointer { buf in
        ptr.initialize(from: buf.baseAddress!, count: count)
    }
    outPtr.pointee = ptr
    outLen.pointee = count
}

/// Gets the character count of a Swift String from its raw buffer.
///
/// Returns `String.count` (Unicode scalar/grapheme cluster count), which
/// may differ from the UTF-8 byte count for multi-byte characters.
///
/// - Parameter bufferPtr: Pointer to the 16-byte Swift.String raw representation.
/// - Returns: The character count.
@_cdecl("SBW_SwiftString_GetCount")
public func sbw_swiftStringGetCount(_ bufferPtr: UnsafeRawPointer) -> Int {
    let str = bufferPtr.assumingMemoryBound(to: String.self).pointee
    return str.count
}

/// Creates a Swift String from UTF-8 bytes and writes its raw representation
/// to the provided output buffer.
///
/// The output buffer must be at least 16 bytes (2 words on arm64), matching
/// the size of `SwiftString.Buffer` on the C# side. The function initializes
/// the buffer with a new String value using proper ARC retain semantics.
///
/// - Parameters:
///   - utf8Ptr: Pointer to the UTF-8 encoded bytes.
///   - utf8Len: Number of UTF-8 bytes.
///   - outBufferPtr: Pointer to the 16-byte output buffer for the String representation.
@_cdecl("SBW_SwiftString_Create")
public func sbw_swiftStringCreate(
    _ utf8Ptr: UnsafePointer<UInt8>,
    _ utf8Len: Int,
    _ outBufferPtr: UnsafeMutableRawPointer
) {
    let data = UnsafeBufferPointer(start: utf8Ptr, count: utf8Len)
    let str = String(decoding: data, as: UTF8.self)
    // initialize(to:) properly retains the String value in the output buffer.
    outBufferPtr.assumingMemoryBound(to: String.self).initialize(to: str)
}

/// Destroys a Swift String stored in the provided buffer.
///
/// Properly deinitializes the String value at the buffer pointer, releasing
/// any ARC-managed storage. Call this instead of ValueWitnessTable->Destroy
/// to avoid the Mono JIT CallConvSwift assertion.
///
/// - Parameter bufferPtr: Pointer to the 16-byte Swift.String raw representation.
@_cdecl("SBW_SwiftString_Destroy")
public func sbw_swiftStringDestroy(_ bufferPtr: UnsafeMutableRawPointer) {
    bufferPtr.assumingMemoryBound(to: String.self).deinitialize(count: 1)
}

/// Frees a UTF-8 buffer previously allocated by `SBW_SwiftString_ToUtf8`.
///
/// - Parameter ptr: The pointer to free, or nil (no-op).
@_cdecl("SBW_SwiftString_FreeUtf8")
public func sbw_swiftStringFreeUtf8(_ ptr: UnsafeMutablePointer<UInt8>?) {
    ptr?.deallocate()
}

/// Returns the type metadata pointer for Swift.String.
///
/// Used by SwiftString's ISwiftObject.GetTypeMetadata() implementation
/// to obtain metadata via Cdecl instead of CallConvSwift.
///
/// - Returns: The raw metadata pointer for String.self.
@_cdecl("SBW_SwiftString_GetMetadata")
public func sbw_swiftStringGetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(String.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

// MARK: - Generic VWT Destroy

/// Generic VWT Destroy: deinitializes any Swift value given its type metadata.
/// Called from the .NET GC finalizer thread via CallingConvention.Cdecl.
/// This avoids JIT compilation on the finalizer thread, which crashes Mono
/// when CallConvSwift compilations have contaminated JIT state.
///
/// The VWT pointer is stored at metadata[-1] in Swift's ABI (stable since Swift 5.0).
/// Destroy is the second entry (offset 1) in the VWT.
///
/// - Parameters:
///   - ptr: Pointer to the Swift value to destroy (the SwiftSafeHandle buffer).
///   - metadataPtr: Pointer to the Swift type metadata for the value.
@_cdecl("SBW_VWTDestroy")
public func sbw_vwtDestroy(_ ptr: UnsafeMutableRawPointer, _ metadataPtr: UnsafeRawPointer) {
    let vwtPtr = metadataPtr.advanced(by: -MemoryLayout<UnsafeRawPointer>.size)
        .load(as: UnsafeRawPointer.self)
    let destroy = vwtPtr.advanced(by: MemoryLayout<UnsafeRawPointer>.size)
        .load(as: (@convention(c) (UnsafeMutableRawPointer, UnsafeRawPointer) -> Void).self)
    destroy(ptr, metadataPtr)
}

// MARK: - Bulk ARC (Single Managed→Native Transition)
//
// Per-element `swift_retain` / `swift_release` from C# costs one managed↔native
// transition per pointer. For collection-shaped workloads (the classic case is
// transferring N class-pointer elements between a Swift Array and a C# array)
// that scales linearly with collection size. Routing the loop through these
// `@_cdecl` helpers collapses N transitions to 1 — C# pins a `ReadOnlySpan<IntPtr>`
// once and Swift walks the buffer in-process. C# `Arc.RetainMultiple` /
// `Arc.ReleaseMultiple` are the consumers; collection runtime helpers reach
// these via Arc, not directly.
//
// Null entries are tolerated (skipped) so callers don't have to filter.

/// Bulk-retain a buffer of Swift class pointers. Skips null entries.
///
/// - Parameters:
///   - ptrs: Pointer to a contiguous buffer of `count` class-instance pointers.
///   - count: Number of pointer slots in the buffer (must be ≥ 0).
@_cdecl("SBW_BulkRetain")
public func sbw_bulkRetain(
    _ ptrs: UnsafePointer<UnsafeMutableRawPointer?>,
    _ count: Int
) {
    guard count > 0 else { return }
    for i in 0..<count {
        if let p = ptrs[i] {
            _ = Unmanaged<AnyObject>.fromOpaque(p).retain()
        }
    }
}

/// Bulk-release a buffer of Swift class pointers. Skips null entries.
///
/// As with `SBW_SwiftRelease`, the actual `swift_release` call happens from
/// inside Swift — the C# caller only ever crosses one Cdecl boundary, which
/// avoids the Mono JIT `!ji->async` assertion that fires when `swift_release`
/// is called directly through a per-element P/Invoke loop after CallConvSwift
/// JIT contamination.
///
/// - Parameters:
///   - ptrs: Pointer to a contiguous buffer of `count` class-instance pointers
///     each carrying a +1 retain.
///   - count: Number of pointer slots in the buffer (must be ≥ 0).
@_cdecl("SBW_BulkRelease")
public func sbw_bulkRelease(
    _ ptrs: UnsafePointer<UnsafeMutableRawPointer?>,
    _ count: Int
) {
    guard count > 0 else { return }
    for i in 0..<count {
        if let p = ptrs[i] {
            Unmanaged<AnyObject>.fromOpaque(p).release()
        }
    }
}

// MARK: - Swift Class ARC Release (Finalizer-Safe)

/// Releases a Swift class reference (-1 ARC retain). Called from the .NET GC
/// finalizer thread by `SwiftClassHandle<T>.ReleaseHandle` via
/// `CallingConvention.Cdecl`.
///
/// Why this exists: calling `swift_release` directly via `[DllImport(libswiftCore)]`
/// from the finalizer thread crashes Mono with the `jit-info.c:918 !ji->async`
/// assertion after CallConvSwift JIT state contamination — even when the C# side
/// uses a non-generic helper class with no managed body. The crash happens
/// inside the P/Invoke marshalling stub itself.
///
/// Routing the call through this Swift `@_cdecl` wrapper sidesteps the issue:
/// the C# side only ever crosses one Cdecl boundary (into our own loaded
/// `SwiftBindingsRuntime.dylib`), and the Swift wrapper makes the actual
/// `swift_release` call from inside Swift, where Mono's JIT contamination has
/// no effect.
///
/// This is the same trick `SBW_VWTDestroy` uses for Swift struct/value-type
/// VWT destruction on the finalizer thread.
///
/// - Parameter ptr: A non-null Swift class object pointer carrying a +1 retain.
@_cdecl("SBW_SwiftRelease")
public func sbw_swiftRelease(_ ptr: UnsafeMutableRawPointer) {
    Unmanaged<AnyObject>.fromOpaque(ptr).release()
}

// MARK: - CoreGraphics Type Metadata
//
// CGPoint, CGRect, CGSize are Clang-imported types whose metadata descriptors
// are local symbols (not exported from any system library). These @_cdecl
// wrappers make the metadata accessible from C# via P/Invoke, enabling
// SwiftOptional<CGPoint> and similar generic type construction at runtime.

/// Returns the type metadata pointer for CoreGraphics.CGPoint.
@_cdecl("SBW_CGPoint_GetMetadata")
public func sbw_cgPointGetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(CGPoint.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

/// Returns the type metadata pointer for CoreGraphics.CGRect.
@_cdecl("SBW_CGRect_GetMetadata")
public func sbw_cgRectGetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(CGRect.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

/// Returns the type metadata pointer for CoreGraphics.CGSize.
@_cdecl("SBW_CGSize_GetMetadata")
public func sbw_cgSizeGetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(CGSize.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

// MARK: - Foundation Value-Type Metadata
//
// Foundation value types mapped to .NET primitives in FoundationDatabase.xml
// (e.g. System.Guid ↔ Foundation.UUID) need runtime metadata registration so
// SwiftOptional<T> can obtain the correct Optional layout. Unlike CoreGraphics
// types, Foundation.UUID's metadata accessor IS exported, but we use @_cdecl
// wrappers for consistency with the CG pattern and to avoid CallConvSwift
// complications when calling the metadata accessor directly.

/// Returns the type metadata pointer for Foundation.UUID.
@_cdecl("SBW_UUID_GetMetadata")
public func sbw_uuidGetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(UUID.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

/// Returns the type metadata pointer for Foundation.Date.
@_cdecl("SBW_Date_GetMetadata")
public func sbw_dateGetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(Date.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

/// Returns the type metadata pointer for Foundation.Decimal.
@_cdecl("SBW_Decimal_GetMetadata")
public func sbw_decimalGetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(Decimal.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

// MARK: - simd Type Metadata
//
// simd_float2/3/4 and simd_quatf are Clang-imported value types whose metadata
// descriptors are local symbols (not exported from any system library), the same
// pattern as CGPoint/CGRect/CGSize. These @_cdecl wrappers expose the metadata
// for the canonical Swift counterparts of System.Numerics.Vector2/3/4 and
// Quaternion (mapped through BoundGenericSimdAliases for SIMD2/3/4<Float>, and
// directly through SimdDatabase.xml for simd_quatf). Required for generic types
// instantiated with SIMD args (e.g. RealityKit.MeshBuffer<Vector3>) so the
// metadata accessor can produce TypeMetadata for the inner argument at runtime.

/// Returns the type metadata pointer for simd_float2 (System.Numerics.Vector2).
@_cdecl("SBW_simd_float2_GetMetadata")
public func sbw_simdFloat2GetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(simd_float2.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

/// Returns the type metadata pointer for simd_float3 (System.Numerics.Vector3).
/// Note: simd_float3 has 16-byte stride (4 floats with the last word as padding)
/// to match the Vector3 ABI, not 12 bytes.
@_cdecl("SBW_simd_float3_GetMetadata")
public func sbw_simdFloat3GetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(simd_float3.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

/// Returns the type metadata pointer for simd_float4 (System.Numerics.Vector4).
@_cdecl("SBW_simd_float4_GetMetadata")
public func sbw_simdFloat4GetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(simd_float4.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

/// Returns the type metadata pointer for simd_quatf (System.Numerics.Quaternion).
@_cdecl("SBW_simd_quatf_GetMetadata")
public func sbw_simdQuatfGetMetadata() -> UnsafeMutableRawPointer {
    unsafeBitCast(simd_quatf.self as Any.Type, to: UnsafeMutableRawPointer.self)
}

// MARK: - Foundation.Measurement Generic Metadata
//
// Measurement<UnitType> is a generic struct whose metadata accessor
// $s10Foundation11MeasurementVMa uses CallConvSwift, which triggers the Mono
// JIT !ji->async assertion (upstream Issue 1). This @_cdecl wrapper calls the
// accessor from Swift (no P/Invoke, no Mono issue) and exposes it via C ABI.

@_silgen_name("$s10Foundation11MeasurementVMa")
func _swift_getMeasurementMetadata(_ request: Int, _ unitMetadata: UnsafeMutableRawPointer) -> UnsafeMutableRawPointer

/// Returns the type metadata pointer for Foundation.Measurement<UnitType>.
/// The caller passes the unit type's metadata (e.g. NSUnitTemperature's ObjC class pointer).
@_cdecl("SBW_Measurement_GetMetadata")
public func sbw_measurementGetMetadata(_ unitMetadata: UnsafeMutableRawPointer) -> UnsafeMutableRawPointer {
    let result = _swift_getMeasurementMetadata(0, unitMetadata)
    precondition(result != UnsafeMutableRawPointer(bitPattern: 0), "Measurement metadata accessor returned null for unit metadata \(unitMetadata)")
    return result
}

// MARK: - ManagedSettings.Token<Kind> Generic Metadata
//
// Token<Kind> is a generic struct in ManagedSettings whose metadata accessor uses
// CallConvSwift. ManagedSettings is not available on all platforms (requires iOS 15+/
// macOS 12+ with Family Controls), so we use dlsym to dynamically resolve the
// accessor rather than a compile-time @_silgen_name reference.

/// Returns type metadata for ManagedSettings.Token<Kind>.
/// The caller passes the marker type's metadata (Application, ActivityCategory, or WebDomain).
/// Returns nil if ManagedSettings is not loaded (e.g. tvOS, Catalyst without Family Controls).
@_cdecl("SBW_Token_GetMetadata")
public func sbw_tokenGetMetadata(_ markerMetadata: UnsafeMutableRawPointer) -> UnsafeMutableRawPointer? {
    typealias GenericMetadataAccessor = @convention(c) (Int, UnsafeMutableRawPointer) -> UnsafeMutableRawPointer
    guard let handle = dlopen(nil, RTLD_LAZY),
          let sym = dlsym(handle, "$s15ManagedSettings5TokenVMa") else {
        return nil
    }
    let accessor = unsafeBitCast(sym, to: GenericMetadataAccessor.self)
    return accessor(0, markerMetadata)
}

/// Returns type metadata for a ManagedSettings marker type by index.
/// 0 = Application, 1 = ActivityCategory, 2 = WebDomain.
/// Uses dlsym since ManagedSettings may not be loaded on all platforms.
@_cdecl("SBW_ManagedSettings_MarkerMetadata")
public func sbw_managedSettingsMarkerMetadata(_ markerIndex: Int) -> UnsafeMutableRawPointer? {
    let mangledNames = [
        "$s15ManagedSettings11ApplicationVMa",
        "$s15ManagedSettings16ActivityCategoryVMa",
        "$s15ManagedSettings9WebDomainVMa",
    ]
    guard markerIndex >= 0, markerIndex < mangledNames.count else { return nil }
    typealias MetadataAccessor = @convention(c) (Int) -> UnsafeMutableRawPointer
    guard let handle = dlopen(nil, RTLD_LAZY),
          let sym = dlsym(handle, mangledNames[markerIndex]) else {
        return nil
    }
    let accessor = unsafeBitCast(sym, to: MetadataAccessor.self)
    return accessor(0)
}

// MARK: - Set.insert Wrappers
//
// Swift's `Set.insert(_:)` returns `(inserted: Bool, memberAfterInsert: Element)` —
// a tuple of (direct Bool, @out indirect Element). On ARM64 the `@out` element is
// passed via x0 as a regular indirect parameter (NOT x8/SwiftIndirectResult, because
// one tuple element is direct-return). Mono's CallConvSwift trampoline mishandles
// this `(Bool direct, @out via x0)` shape on iOS Simulator: the call appears to
// return successfully (`inserted=1`, the @out element is written), but a stack
// address from the trampoline's scratch frame is also written into the caller's
// `self` slot. The next VWT Destroy on Dispose then dereferences that stack
// address as a HeapObject* and SIGSEGVs in `_swift_release_dealloc`.
//
// Routing through these Swift `@_cdecl` wrappers avoids Mono's CallConvSwift
// trampoline entirely: C# enters via Cdecl, and the actual `Set.insert` call is
// Swift-to-Swift (no Mono trampoline involvement).
//
// `Dictionary.updateValue(_:forKey:)` returns `Optional<Value>` (pure `@out` via
// x8/SwiftIndirectResult) and does NOT exhibit the same corruption — confirming
// the bug is specific to the tuple-return shape, not generic CallConvSwift.

/// Inserts an `Int64` into a `Set<Int64>`. Equivalent to Swift's `set.insert(element)`.
///
/// Bound to C# `long` (`System.Int64`), which `HashableConformanceRegistry` maps to
/// `Swift.Int64` (mangled `$ss5Int64V`) — distinct from `Swift.Int` (mangled `$sSi`).
/// Although both are 64-bit on arm64, their generic instantiations of `Set` have
/// different metadata, so we keep separate wrappers per Swift element type.
///
/// - Parameters:
///   - setHandle: Pointer to the Set's storage slot (the 8-byte buffer holding the
///     `__RawSetStorage` pointer). Mutated in place.
///   - element: The Int64 value to insert.
///   - outMember: Pointer to a caller-provided 8-byte buffer that receives
///     `memberAfterInsert` (the pre-existing member if `inserted == false`, otherwise
///     the just-inserted element).
/// - Returns: `true` if the element was inserted; `false` if it was already present.
@_cdecl("SBW_SetInt64_Insert")
public func sbw_setInt64Insert(
    _ setHandle: UnsafeMutableRawPointer,
    _ element: Int64,
    _ outMember: UnsafeMutableRawPointer
) -> Bool {
    let setPtr = setHandle.assumingMemoryBound(to: Set<Int64>.self)
    let outPtr = outMember.assumingMemoryBound(to: Int64.self)
    let result = setPtr.pointee.insert(element)
    outPtr.initialize(to: result.memberAfterInsert)
    return result.inserted
}

/// Inserts an `Int` into a `Set<Int>`. Equivalent to Swift's `set.insert(element)`.
///
/// Bound to C# `nint` (`System.IntPtr`), which `HashableConformanceRegistry` maps to
/// `Swift.Int` (mangled `$sSi`) — distinct from `Swift.Int64` (handled by
/// `SBW_SetInt64_Insert`). Same byte layout on 64-bit, different metadata.
///
/// - Parameters:
///   - setHandle: Pointer to the Set's storage slot (the 8-byte buffer holding the
///     `__RawSetStorage` pointer). Mutated in place.
///   - element: The Int value to insert.
///   - outMember: Pointer to a caller-provided 8-byte buffer that receives
///     `memberAfterInsert` (the pre-existing member if `inserted == false`, otherwise
///     the just-inserted element).
/// - Returns: `true` if the element was inserted; `false` if it was already present.
@_cdecl("SBW_SetInt_Insert")
public func sbw_setIntInsert(
    _ setHandle: UnsafeMutableRawPointer,
    _ element: Int,
    _ outMember: UnsafeMutableRawPointer
) -> Bool {
    let setPtr = setHandle.assumingMemoryBound(to: Set<Int>.self)
    let outPtr = outMember.assumingMemoryBound(to: Int.self)
    let result = setPtr.pointee.insert(element)
    outPtr.initialize(to: result.memberAfterInsert)
    return result.inserted
}

/// Inserts a `String` into a `Set<String>`. Equivalent to Swift's `set.insert(element)`.
///
/// - Parameters:
///   - setHandle: Pointer to the Set's storage slot (the 8-byte buffer holding the
///     `__RawSetStorage` pointer). Mutated in place.
///   - elementBuffer: Pointer to a 16-byte (2-word) Swift.String raw representation
///     carrying a +1 retain. **The buffer is moved-from** — its +1 retain is consumed
///     by `insert`, matching the original `@in` ABI semantics. The C# caller must
///     not destroy the buffer after this call returns.
///   - outMember: Pointer to a caller-provided 16-byte buffer that receives
///     `memberAfterInsert`. The caller is responsible for destroying it via the
///     String VWT.
/// - Returns: `true` if the element was inserted; `false` if it was already present.
@_cdecl("SBW_SetString_Insert")
public func sbw_setStringInsert(
    _ setHandle: UnsafeMutableRawPointer,
    _ elementBuffer: UnsafeMutableRawPointer,
    _ outMember: UnsafeMutableRawPointer
) -> Bool {
    let setPtr = setHandle.assumingMemoryBound(to: Set<String>.self)
    // .move() consumes the +1 in elementBuffer, transferring ownership to `element`.
    // After this, elementBuffer is uninitialized — caller must not destroy it.
    let element = elementBuffer.assumingMemoryBound(to: String.self).move()
    let outPtr = outMember.assumingMemoryBound(to: String.self)
    let result = setPtr.pointee.insert(element)
    outPtr.initialize(to: result.memberAfterInsert)
    return result.inserted
}

// MARK: - SwiftUI.Text Construction Bridge

// SwiftUI.Text is not available in the Mac Catalyst SDK interface (macabi swiftinterface
// omits the type). Guard with targetEnvironment to avoid compilation failures on Catalyst.
#if canImport(SwiftUI) && !targetEnvironment(macCatalyst)
import SwiftUI

/// Creates a SwiftUI.Text from a UTF-8 string and writes it into a pre-allocated buffer.
/// The caller allocates the output buffer using Text's type metadata size.
/// Text is a non-frozen struct — the output buffer must be destroyed via VWT Destroy.
@available(macOS 10.15, iOS 13.0, tvOS 13.0, watchOS 6.0, *)
@_cdecl("SBW_SwiftUI_Text_Create")
public func sbw_swiftUITextCreate(
    _ utf8Ptr: UnsafePointer<UInt8>,
    _ utf8Len: Int,
    _ outBufferPtr: UnsafeMutableRawPointer
) {
    let data = UnsafeBufferPointer(start: utf8Ptr, count: utf8Len)
    let str = String(decoding: data, as: UTF8.self)
    let text = SwiftUI.Text(str)
    outBufferPtr.assumingMemoryBound(to: SwiftUI.Text.self).initialize(to: text)
}

/// Destroys a SwiftUI.Text value in a buffer without freeing the buffer itself.
/// Used when the C# side needs explicit cleanup before SafeHandle disposal.
@available(macOS 10.15, iOS 13.0, tvOS 13.0, watchOS 6.0, *)
@_cdecl("SBW_SwiftUI_Text_Destroy")
public func sbw_swiftUITextDestroy(_ bufferPtr: UnsafeMutableRawPointer) {
    bufferPtr.assumingMemoryBound(to: SwiftUI.Text.self).deinitialize(count: 1)
}
#endif
