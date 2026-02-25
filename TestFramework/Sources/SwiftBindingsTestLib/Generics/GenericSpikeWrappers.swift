// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Session 7 ABI Spike: Generic @_silgen_name + TypeMetadata Passing
//
// Approach: Use explicit `_ t: T.Type` parameters instead of implicit generic
// metadata. Swift 6 errors on generic params not used in function signatures,
// and `T.Type` is ABI-equivalent to `TypeMetadata*` — C# passes `TypeMetadata`
// in the position of the metatype param.
//
// From C# with CallConvSwift:
//   func f<T>(_ x: UnsafeMutableRawPointer, _ t: T.Type)
//   maps to: f(IntPtr x, TypeMetadata tMetadata)

import Foundation

// MARK: - S1: Basic Identity + SizeOf — Prove TypeMetadata Passing Works

/// Identity function: receives a pointer and returns it.
/// The T.Type parameter proves TypeMetadata passing works.
@_silgen_name("SBW_Spike_identity")
public func SBW_Spike_identity<T>(_ value: UnsafeMutableRawPointer, _ t: T.Type) -> UnsafeMutableRawPointer {
    return value
}

/// Non-generic version: receives metadata as UnsafeRawPointer and uses
/// swift_getTypeByMangledNameInContext or similar to get size.
/// Actually, we can use _swift_getTypeMetadataLayoutSize at the ABI level.
///
/// Simpler approach: we already proved identity works (S1a).
/// Now test if the issue is Mono JIT with TypeMetadata+void return.
///
/// Test A: non-generic, takes raw pointer (should work — no CallConvSwift issues)
@_cdecl("SBW_Spike_sizeOfT_cdecl")
public func SBW_Spike_sizeOfT_cdecl(_ metadataPtr: UnsafeRawPointer, _ resultBuf: UnsafeMutablePointer<Int>) {
    // Use the metadata pointer to look up the value witness table
    // TypeMetadata points to the full metadata record; VWT is at offset -1
    let vwtPtr = metadataPtr.load(fromByteOffset: -MemoryLayout<UnsafeRawPointer>.size, as: UnsafeRawPointer.self)
    // VWT.size is at offset 64 (0x40) in the value witness table on arm64
    // Actually, let's use a simpler approach: just verify the metadata pointer is valid
    // by checking the kind field at offset 0 of the metadata
    let kind = metadataPtr.load(as: Int.self)
    // For now, just write the kind value — this proves we can read from TypeMetadata
    resultBuf.pointee = kind
}

/// Generic sizeOfT — uses T.Type explicit param.
/// The key question: does Swift add IMPLICIT metadata on top of explicit T.Type?
@_silgen_name("SBW_Spike_sizeOfT")
public func SBW_Spike_sizeOfT<T>(
    _ self_: UnsafeMutableRawPointer,
    _ resultBuf: UnsafeMutablePointer<Int>,
    _ t: T.Type
) {
    resultBuf.pointee = MemoryLayout<T>.size
}

/// Generic strideOfT
@_silgen_name("SBW_Spike_strideOfT")
public func SBW_Spike_strideOfT<T>(
    _ self_: UnsafeMutableRawPointer,
    _ resultBuf: UnsafeMutablePointer<Int>,
    _ t: T.Type
) {
    resultBuf.pointee = MemoryLayout<T>.stride
}

// MARK: - S2: Closure Callback Round-Trip

/// A simple generic container class for testing (mimics Observable<Element>).
public class SpikeBox<Element> {
    public let value: Element

    public init(_ value: Element) {
        self.value = value
    }

    /// Applies a predicate to the contained value.
    public func test(_ predicate: (Element) -> Bool) -> Bool {
        return predicate(value)
    }

    /// Transforms the contained value.
    public func transform<R>(_ f: (Element) -> R) -> SpikeBox<R> {
        return SpikeBox<R>(f(value))
    }
}

/// Filter-like spike: calls a C callback with the element from a SpikeBox<Element>.
/// The callback receives the element as a raw pointer.
/// Result written to resultBuf to avoid Mono JIT crash on Bool return from CallConvSwift.
///
/// C# signature: SBW_Spike_filter(IntPtr self_, IntPtr funcPtr, IntPtr ctx,
///                                IntPtr resultBuf, TypeMetadata elementType)
@_silgen_name("SBW_Spike_filter")
public func SBW_Spike_filter<Element>(
    _ self_: UnsafeMutableRawPointer,
    _ predicateFuncPtr: UnsafeMutableRawPointer,
    _ predicateContext: UnsafeMutableRawPointer?,
    _ resultBuf: UnsafeMutablePointer<Bool>,
    _ elementType: Element.Type
) {
    let box = unsafeBitCast(self_, to: SpikeBox<Element>.self)
    let cdecl = unsafeBitCast(predicateFuncPtr, to:
        (@convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer?) -> Bool).self)

    let element = box.value
    let elementSize = MemoryLayout<Element>.size
    let elementAlignment = MemoryLayout<Element>.alignment
    let buf = UnsafeMutableRawPointer.allocate(byteCount: max(elementSize, 1), alignment: elementAlignment)
    defer { buf.deallocate() }

    withUnsafePointer(to: element) { src in
        buf.copyMemory(from: UnsafeRawPointer(src), byteCount: elementSize)
    }

    resultBuf.pointee = cdecl(buf, predicateContext)
}

// MARK: - S3: Map with Two Generic Parameters (Element + Result)

/// Map-like spike: transforms the element using a C callback.
/// Two metatype params prove two-metadata passing works.
///
/// C# signature: SBW_Spike_map(IntPtr self_, IntPtr funcPtr, IntPtr ctx,
///                              IntPtr resultBuf, TypeMetadata elementType, TypeMetadata resultType)
@_silgen_name("SBW_Spike_map")
public func SBW_Spike_map<Element, Result>(
    _ self_: UnsafeMutableRawPointer,
    _ transformFuncPtr: UnsafeMutableRawPointer,
    _ transformContext: UnsafeMutableRawPointer?,
    _ resultBuf: UnsafeMutableRawPointer,
    _ elementType: Element.Type,
    _ resultType: Result.Type
) {
    let box = unsafeBitCast(self_, to: SpikeBox<Element>.self)
    let cdecl = unsafeBitCast(transformFuncPtr, to:
        (@convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer, UnsafeMutableRawPointer?) -> Void).self)

    let element = box.value
    let elementSize = MemoryLayout<Element>.size
    let elementAlignment = MemoryLayout<Element>.alignment
    let elementBuf = UnsafeMutableRawPointer.allocate(byteCount: max(elementSize, 1), alignment: elementAlignment)
    defer { elementBuf.deallocate() }

    withUnsafePointer(to: element) { src in
        elementBuf.copyMemory(from: UnsafeRawPointer(src), byteCount: elementSize)
    }

    // Callback writes Result into resultBuf
    cdecl(elementBuf, resultBuf, transformContext)
}

// MARK: - S4: Error Propagation with Closure

/// Filter with error propagation: the callback can signal an error via errorOut.
/// Both result (Bool) and error are written via out-params.
///
/// C# signature: SBW_Spike_filterThrows(IntPtr self_, IntPtr funcPtr, IntPtr ctx,
///                                       IntPtr resultBuf, IntPtr* errorOut, TypeMetadata elementType)
@_silgen_name("SBW_Spike_filterThrows")
public func SBW_Spike_filterThrows<Element>(
    _ self_: UnsafeMutableRawPointer,
    _ predicateFuncPtr: UnsafeMutableRawPointer,
    _ predicateContext: UnsafeMutableRawPointer?,
    _ resultBuf: UnsafeMutablePointer<Bool>,
    _ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>,
    _ elementType: Element.Type
) {
    let box = unsafeBitCast(self_, to: SpikeBox<Element>.self)
    let cdecl = unsafeBitCast(predicateFuncPtr, to:
        (@convention(c) (UnsafeMutableRawPointer, UnsafeMutablePointer<UnsafeMutableRawPointer?>, UnsafeMutableRawPointer?) -> Bool).self)

    let element = box.value
    let elementSize = MemoryLayout<Element>.size
    let elementAlignment = MemoryLayout<Element>.alignment
    let buf = UnsafeMutableRawPointer.allocate(byteCount: max(elementSize, 1), alignment: elementAlignment)
    defer { buf.deallocate() }

    withUnsafePointer(to: element) { src in
        buf.copyMemory(from: UnsafeRawPointer(src), byteCount: elementSize)
    }

    var innerError: UnsafeMutableRawPointer? = nil
    let result = cdecl(buf, &innerError, predicateContext)

    if let err = innerError {
        errorOut.pointee = err
        resultBuf.pointee = false
    } else {
        resultBuf.pointee = result
    }
}

// MARK: - Helper Functions

/// Creates a SpikeBox containing an Int value.
@_silgen_name("SBW_Spike_createIntBox")
public func SBW_Spike_createIntBox(_ value: Int) -> UnsafeMutableRawPointer {
    let box = SpikeBox(value)
    return Unmanaged.passRetained(box).toOpaque()
}

/// Releases a retained SpikeBox.
@_silgen_name("SBW_Spike_releaseBox")
public func SBW_Spike_releaseBox(_ ptr: UnsafeMutableRawPointer) {
    Unmanaged<AnyObject>.fromOpaque(ptr).release()
}

// MARK: - Error Helpers

/// Creates an NSError from a C string message. Used by C# callbacks to propagate errors.
@_silgen_name("SBW_Spike_createError")
public func SBW_Spike_createError(_ message: UnsafePointer<CChar>) -> UnsafeMutableRawPointer {
    let msg = String(cString: message)
    let error = NSError(domain: "SwiftBindingsSpike", code: -1, userInfo: [NSLocalizedDescriptionKey: msg])
    return Unmanaged.passRetained(error as AnyObject).toOpaque()
}

/// Gets the error description as a C string (caller must free with free()).
@_silgen_name("SBW_Spike_getErrorDescription")
public func SBW_Spike_getErrorDescription(_ errorPtr: UnsafeMutableRawPointer) -> UnsafeMutablePointer<CChar>? {
    let error = Unmanaged<AnyObject>.fromOpaque(errorPtr).takeUnretainedValue()
    let description: String
    if let nsError = error as? NSError {
        description = nsError.localizedDescription
    } else {
        description = String(describing: error)
    }
    return strdup(description)
}

/// Releases an error object.
@_silgen_name("SBW_Spike_releaseError")
public func SBW_Spike_releaseError(_ errorPtr: UnsafeMutableRawPointer) {
    Unmanaged<AnyObject>.fromOpaque(errorPtr).release()
}
