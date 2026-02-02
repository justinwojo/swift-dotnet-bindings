import Nuke
import UIKit
import Foundation

// MARK: - ImageRequest Wrappers (Bypass ExistentialContainer JIT Bug)
// These wrappers avoid the Mono JIT bug with swift_getExistentialTypeMetadata by
// creating [any ImageProcessing] arrays on the Swift side with empty processors.

/// Creates an ImageRequest from a URL string with default options and no processors.
/// This is the simplest constructor - just provide a URL string.
/// Bypasses the SwiftArray<ExistentialContainer> JIT bug by creating ImageRequest on the Swift side.
@_silgen_name("ImageRequest_initWithURLString_simple")
public func imageRequest_initWithURLString_simple(_ urlString: UnsafePointer<CChar>) -> UnsafeMutableRawPointer {
    let urlStr = String(cString: urlString)
    let request = ImageRequest(url: URL(string: urlStr))

    // Allocate and copy the ImageRequest to heap
    let ptr = UnsafeMutablePointer<ImageRequest>.allocate(capacity: 1)
    ptr.initialize(to: request)
    return UnsafeMutableRawPointer(ptr)
}

/// Frees an ImageRequest that was allocated by a wrapper function.
/// Call this when you're done with the ImageRequest to avoid memory leaks.
@_silgen_name("ImageRequest_free")
public func imageRequest_free(_ ptr: UnsafeMutableRawPointer) {
    let typedPtr = ptr.assumingMemoryBound(to: ImageRequest.self)
    typedPtr.deinitialize(count: 1)
    typedPtr.deallocate()
}
