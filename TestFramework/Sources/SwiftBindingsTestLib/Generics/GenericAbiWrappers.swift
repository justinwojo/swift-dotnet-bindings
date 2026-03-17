// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Generic ABI Wrappers: @_silgen_name + TypeMetadata Passing
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

// MARK: - Basic Identity + SizeOf — Prove TypeMetadata Passing Works

/// Identity function: receives a pointer and returns it.
/// The T.Type parameter proves TypeMetadata passing works.
@_silgen_name("SBW_GenericAbi_identity")
public func SBW_GenericAbi_identity<T>(_ value: UnsafeMutableRawPointer, _ t: T.Type) -> UnsafeMutableRawPointer {
    return value
}

/// Non-generic version: receives metadata as UnsafeRawPointer.
/// Takes raw pointer (no CallConvSwift issues).
@_cdecl("SBW_GenericAbi_sizeOfT_cdecl")
public func SBW_GenericAbi_sizeOfT_cdecl(_ metadataPtr: UnsafeRawPointer, _ resultBuf: UnsafeMutablePointer<Int>) {
    let kind = metadataPtr.load(as: Int.self)
    resultBuf.pointee = kind
}

/// Generic sizeOfT — uses T.Type explicit param.
@_silgen_name("SBW_GenericAbi_sizeOfT")
public func SBW_GenericAbi_sizeOfT<T>(
    _ self_: UnsafeMutableRawPointer,
    _ resultBuf: UnsafeMutablePointer<Int>,
    _ t: T.Type
) {
    resultBuf.pointee = MemoryLayout<T>.size
}

/// Generic strideOfT
@_silgen_name("SBW_GenericAbi_strideOfT")
public func SBW_GenericAbi_strideOfT<T>(
    _ self_: UnsafeMutableRawPointer,
    _ resultBuf: UnsafeMutablePointer<Int>,
    _ t: T.Type
) {
    resultBuf.pointee = MemoryLayout<T>.stride
}

// MARK: - Closure Callback Round-Trip

/// A simple generic container class for testing (mimics Observable<Element>).
public class GenericAbiBox<Element> {
    public let value: Element

    public init(_ value: Element) {
        self.value = value
    }

    /// Applies a predicate to the contained value.
    public func test(_ predicate: (Element) -> Bool) -> Bool {
        return predicate(value)
    }

    /// Transforms the contained value.
    public func transform<R>(_ f: (Element) -> R) -> GenericAbiBox<R> {
        return GenericAbiBox<R>(f(value))
    }
}

/// Filter: calls a C callback with the element from a GenericAbiBox<Element>.
/// The callback receives the element as a raw pointer.
/// Result written to resultBuf to avoid Mono JIT crash on Bool return from CallConvSwift.
@_silgen_name("SBW_GenericAbi_filter")
public func SBW_GenericAbi_filter<Element>(
    _ self_: UnsafeMutableRawPointer,
    _ predicateFuncPtr: UnsafeMutableRawPointer,
    _ predicateContext: UnsafeMutableRawPointer?,
    _ resultBuf: UnsafeMutablePointer<Bool>,
    _ elementType: Element.Type
) {
    let box = unsafeBitCast(self_, to: GenericAbiBox<Element>.self)
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

// MARK: - Map with Two Generic Parameters (Element + Result)

/// Map: transforms the element using a C callback.
/// Two metatype params prove two-metadata passing works.
@_silgen_name("SBW_GenericAbi_map")
public func SBW_GenericAbi_map<Element, Result>(
    _ self_: UnsafeMutableRawPointer,
    _ transformFuncPtr: UnsafeMutableRawPointer,
    _ transformContext: UnsafeMutableRawPointer?,
    _ resultBuf: UnsafeMutableRawPointer,
    _ elementType: Element.Type,
    _ resultType: Result.Type
) {
    let box = unsafeBitCast(self_, to: GenericAbiBox<Element>.self)
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

// MARK: - Error Propagation with Closure

/// Filter with error propagation: the callback can signal an error via errorOut.
/// Both result (Bool) and error are written via out-params.
@_silgen_name("SBW_GenericAbi_filterThrows")
public func SBW_GenericAbi_filterThrows<Element>(
    _ self_: UnsafeMutableRawPointer,
    _ predicateFuncPtr: UnsafeMutableRawPointer,
    _ predicateContext: UnsafeMutableRawPointer?,
    _ resultBuf: UnsafeMutablePointer<Bool>,
    _ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>,
    _ elementType: Element.Type
) {
    let box = unsafeBitCast(self_, to: GenericAbiBox<Element>.self)
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

/// Creates a GenericAbiBox containing an Int value.
@_silgen_name("SBW_GenericAbi_createIntBox")
public func SBW_GenericAbi_createIntBox(_ value: Int) -> UnsafeMutableRawPointer {
    let box = GenericAbiBox(value)
    return Unmanaged.passRetained(box).toOpaque()
}

/// Releases a retained GenericAbiBox.
@_silgen_name("SBW_GenericAbi_releaseBox")
public func SBW_GenericAbi_releaseBox(_ ptr: UnsafeMutableRawPointer) {
    Unmanaged<AnyObject>.fromOpaque(ptr).release()
}

// MARK: - Error Helpers

/// Creates an NSError from a C string message. Used by C# callbacks to propagate errors.
@_silgen_name("SBW_GenericAbi_createError")
public func SBW_GenericAbi_createError(_ message: UnsafePointer<CChar>) -> UnsafeMutableRawPointer {
    let msg = String(cString: message)
    let error = NSError(domain: "SwiftBindingsGenericAbi", code: -1, userInfo: [NSLocalizedDescriptionKey: msg])
    return Unmanaged.passRetained(error as AnyObject).toOpaque()
}

/// Gets the error description as a C string (caller must free with free()).
@_silgen_name("SBW_GenericAbi_getErrorDescription")
public func SBW_GenericAbi_getErrorDescription(_ errorPtr: UnsafeMutableRawPointer) -> UnsafeMutablePointer<CChar>? {
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
@_silgen_name("SBW_GenericAbi_releaseError")
public func SBW_GenericAbi_releaseError(_ errorPtr: UnsafeMutableRawPointer) {
    Unmanaged<AnyObject>.fromOpaque(errorPtr).release()
}
