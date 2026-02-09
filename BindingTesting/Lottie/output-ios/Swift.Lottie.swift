import Lottie
import Foundation
import CoreFoundation
import CoreGraphics
import CoreText
import QuartzCore

@frozen
public struct SBW_Utf8Slice {
    public var ptr: UnsafeMutablePointer<UInt8>
    public var len: Int
}
// Static empty buffer for empty string slices (required for @convention(c) compatibility)
fileprivate var _sbw_emptyBuffer: UInt8 = 0
@_silgen_name("SBW_Free_Lottie")
public func SBW_Free(_ ptr: UnsafeMutableRawPointer?) {
    ptr?.deallocate()
}
// EveryProtocol is a Swift class that can conform to any protocol.
// Protocol method implementations call back to C# via vtable function pointers.
// This class is used by generated proxy classes to implement Swift protocols from C#.
public final class EveryProtocol {
    // Store a handle back to the C# proxy object
    // This is used by vtable functions to find the C# implementation
    public var handle: UnsafeRawPointer?
    public init() {
        self.handle = nil
    }
    public init(handle: UnsafeRawPointer) {
        self.handle = handle
    }
}
// Vtable for AnimationFontProvider protocol - stores function pointers to C# implementations
fileprivate struct AnimationFontProvider_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_fontFor_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _animationFontProvider_vtable = AnimationFontProvider_vtable()

// EveryProtocol conformance to AnimationFontProvider
extension EveryProtocol: Lottie.AnimationFontProvider {
    public func fontFor(family: Swift.String, size: CoreGraphics.CGFloat) -> (CoreText.CTFont)? {
            var selfProto: Lottie.AnimationFontProvider = self
            var familyCopy = family
                var sizeCopy = size
                let resultPtr = _animationFontProvider_vtable.func_fontFor_0!(
                _animationFontProvider_vtable.csVTHandle, &selfProto, &familyCopy, &sizeCopy)
            return resultPtr.assumingMemoryBound(to: (CoreText.CTFont)?.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetAnimationFontProvider_vtable")
public func setAnimationFontProvider_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<AnimationFontProvider_vtable> = uvt.assumingMemoryBound(to: AnimationFontProvider_vtable.self)
    _animationFontProvider_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to AnimationFontProvider.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_AnimationFontProvider_WitnessTable")
public func getEveryProtocolAnimationFontProviderWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.AnimationFontProvider = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout for class-bound protocols:
            // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
            // For a single-protocol existential, witness table is at offset 4 * pointer size
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
// Vtable for AnimationKeypathTextProvider protocol - stores function pointers to C# implementations
fileprivate struct AnimationKeypathTextProvider_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_text_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _animationKeypathTextProvider_vtable = AnimationKeypathTextProvider_vtable()

// EveryProtocol conformance to AnimationKeypathTextProvider
extension EveryProtocol: Lottie.AnimationKeypathTextProvider {
    public func text(for forValue: Lottie.AnimationKeypath, sourceText: Swift.String) -> (Swift.String)? {
            var selfProto: Lottie.AnimationKeypathTextProvider = self
            var forValueCopy = forValue
                var sourceTextCopy = sourceText
                let resultPtr = _animationKeypathTextProvider_vtable.func_text_0!(
                _animationKeypathTextProvider_vtable.csVTHandle, &selfProto, &forValueCopy, &sourceTextCopy)
            return resultPtr.assumingMemoryBound(to: (Swift.String)?.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetAnimationKeypathTextProvider_vtable")
public func setAnimationKeypathTextProvider_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<AnimationKeypathTextProvider_vtable> = uvt.assumingMemoryBound(to: AnimationKeypathTextProvider_vtable.self)
    _animationKeypathTextProvider_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to AnimationKeypathTextProvider.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_AnimationKeypathTextProvider_WitnessTable")
public func getEveryProtocolAnimationKeypathTextProviderWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.AnimationKeypathTextProvider = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout for class-bound protocols:
            // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
            // For a single-protocol existential, witness table is at offset 4 * pointer size
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
// Vtable for LegacyAnimationTextProvider protocol - stores function pointers to C# implementations
fileprivate struct LegacyAnimationTextProvider_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_textFor_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_text_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _legacyAnimationTextProvider_vtable = LegacyAnimationTextProvider_vtable()

// EveryProtocol conformance to LegacyAnimationTextProvider
extension EveryProtocol: Lottie.LegacyAnimationTextProvider {
    public func textFor(keypathName: Swift.String, sourceText: Swift.String) -> Swift.String {
            var selfProto: Lottie.LegacyAnimationTextProvider = self
            var keypathNameCopy = keypathName
                var sourceTextCopy = sourceText
                let resultPtr = _legacyAnimationTextProvider_vtable.func_textFor_0!(
                _legacyAnimationTextProvider_vtable.csVTHandle, &selfProto, &keypathNameCopy, &sourceTextCopy)
            return resultPtr.assumingMemoryBound(to: Swift.String.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetLegacyAnimationTextProvider_vtable")
public func setLegacyAnimationTextProvider_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<LegacyAnimationTextProvider_vtable> = uvt.assumingMemoryBound(to: LegacyAnimationTextProvider_vtable.self)
    _legacyAnimationTextProvider_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to LegacyAnimationTextProvider.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_LegacyAnimationTextProvider_WitnessTable")
public func getEveryProtocolLegacyAnimationTextProviderWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.LegacyAnimationTextProvider = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout for class-bound protocols:
            // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
            // For a single-protocol existential, witness table is at offset 4 * pointer size
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
// Witness dispatch accessors for LegacyAnimationTextProvider
@_silgen_name("SBW_LegacyAnimationTextProvider_method_textFor_0")
public func SBW_LegacyAnimationTextProvider_method_textFor_0(_ containerPtr: UnsafeRawPointer, _ arg0Ptr: UnsafeRawPointer, _ arg1Ptr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any Lottie.LegacyAnimationTextProvider).self)
    let arg0Slice = arg0Ptr.load(as: SBW_Utf8Slice.self)
    let arg0: String
    if arg0Slice.len > 0 {
        arg0 = String(unsafeUninitializedCapacity: arg0Slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: arg0Slice.ptr, byteCount: arg0Slice.len)
            return arg0Slice.len
        }
    } else {
        arg0 = ""
    }
    let arg1Slice = arg1Ptr.load(as: SBW_Utf8Slice.self)
    let arg1: String
    if arg1Slice.len > 0 {
        arg1 = String(unsafeUninitializedCapacity: arg1Slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: arg1Slice.ptr, byteCount: arg1Slice.len)
            return arg1Slice.len
        }
    } else {
        arg1 = ""
    }
    let result: String = existential.textFor(keypathName: arg0, sourceText: arg1)
    let utf8 = Array(result.utf8)
    let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))
    if !utf8.isEmpty {
        utf8.withUnsafeBufferPointer { src in
            bufferPtr.initialize(from: src.baseAddress!, count: src.count)
        }
    }
    let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)
    slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))
    return UnsafeMutableRawPointer(slicePtr)
}

@_silgen_name("SBW_LegacyAnimationTextProvider_free_method_textFor_0")
public func SBW_LegacyAnimationTextProvider_free_method_textFor_0(_ ptr: UnsafeMutableRawPointer) {
    let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
    slicePtr.pointee.ptr.deallocate()
    slicePtr.deinitialize(count: 1)
    slicePtr.deallocate()
}

// Vtable for TextContentsScaleProvider protocol - stores function pointers to C# implementations
fileprivate struct TextContentsScaleProvider_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_contentsScale_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _textContentsScaleProvider_vtable = TextContentsScaleProvider_vtable()

// EveryProtocol conformance to TextContentsScaleProvider
extension EveryProtocol: Lottie.TextContentsScaleProvider {
    public func contentsScale(for forValue: Lottie.AnimationKeypath) -> (CoreGraphics.CGFloat)? {
            var selfProto: Lottie.TextContentsScaleProvider = self
            var forValueCopy = forValue
                let resultPtr = _textContentsScaleProvider_vtable.func_contentsScale_0!(
                _textContentsScaleProvider_vtable.csVTHandle, &selfProto, &forValueCopy)
            return resultPtr.assumingMemoryBound(to: (CoreGraphics.CGFloat)?.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetTextContentsScaleProvider_vtable")
public func setTextContentsScaleProvider_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<TextContentsScaleProvider_vtable> = uvt.assumingMemoryBound(to: TextContentsScaleProvider_vtable.self)
    _textContentsScaleProvider_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to TextContentsScaleProvider.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_TextContentsScaleProvider_WitnessTable")
public func getEveryProtocolTextContentsScaleProviderWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.TextContentsScaleProvider = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout for class-bound protocols:
            // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
            // For a single-protocol existential, witness table is at offset 4 * pointer size
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
// Vtable for AnimationCacheProvider protocol - stores function pointers to C# implementations
fileprivate struct AnimationCacheProvider_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_animation_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_setAnimation_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_clearCache_2: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> Void)?
}

private var _animationCacheProvider_vtable = AnimationCacheProvider_vtable()

// EveryProtocol conformance to AnimationCacheProvider
extension EveryProtocol: Lottie.AnimationCacheProvider {
    public func animation(forKey: Swift.String) -> (Lottie.LottieAnimation)? {
            var selfProto: Lottie.AnimationCacheProvider = self
            var forKeyCopy = forKey
                let resultPtr = _animationCacheProvider_vtable.func_animation_0!(
                _animationCacheProvider_vtable.csVTHandle, &selfProto, &forKeyCopy)
            return resultPtr.assumingMemoryBound(to: (Lottie.LottieAnimation)?.self).pointee
    }
    
    public func setAnimation(_ arg0: Lottie.LottieAnimation, forKey: Swift.String) {
            var selfProto: Lottie.AnimationCacheProvider = self
            var arg0Copy = arg0
                var forKeyCopy = forKey
                _animationCacheProvider_vtable.func_setAnimation_1!(
                _animationCacheProvider_vtable.csVTHandle, &selfProto, &arg0Copy, &forKeyCopy)
    }
    
    public func clearCache() {
            var selfProto: Lottie.AnimationCacheProvider = self
            _animationCacheProvider_vtable.func_clearCache_2!(
                _animationCacheProvider_vtable.csVTHandle, &selfProto)
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetAnimationCacheProvider_vtable")
public func setAnimationCacheProvider_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<AnimationCacheProvider_vtable> = uvt.assumingMemoryBound(to: AnimationCacheProvider_vtable.self)
    _animationCacheProvider_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to AnimationCacheProvider.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_AnimationCacheProvider_WitnessTable")
public func getEveryProtocolAnimationCacheProviderWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.AnimationCacheProvider = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout for class-bound protocols:
            // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
            // For a single-protocol existential, witness table is at offset 4 * pointer size
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
// Witness dispatch accessors for AnimationCacheProvider
@_silgen_name("SBW_AnimationCacheProvider_method_clearCache_2")
public func SBW_AnimationCacheProvider_method_clearCache_2(_ containerPtr: UnsafeRawPointer) {
    let existential = containerPtr.load(as: (any Lottie.AnimationCacheProvider).self)
    existential.clearCache()
}


// Vtable for ReducedMotionOptionProvider protocol - stores function pointers to C# implementations
fileprivate struct ReducedMotionOptionProvider_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_currentReducedMotionMode_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _reducedMotionOptionProvider_vtable = ReducedMotionOptionProvider_vtable()

// EveryProtocol conformance to ReducedMotionOptionProvider
extension EveryProtocol: Lottie.ReducedMotionOptionProvider {
    public var currentReducedMotionMode: Lottie.ReducedMotionMode {
        get {
            var selfProto: Lottie.ReducedMotionOptionProvider = self
            let resultPtr = _reducedMotionOptionProvider_vtable.func_currentReducedMotionMode_get!(
                _reducedMotionOptionProvider_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Lottie.ReducedMotionMode.self).pointee
        }
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetReducedMotionOptionProvider_vtable")
public func setReducedMotionOptionProvider_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<ReducedMotionOptionProvider_vtable> = uvt.assumingMemoryBound(to: ReducedMotionOptionProvider_vtable.self)
    _reducedMotionOptionProvider_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to ReducedMotionOptionProvider.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_ReducedMotionOptionProvider_WitnessTable")
public func getEveryProtocolReducedMotionOptionProviderWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.ReducedMotionOptionProvider = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout for class-bound protocols:
            // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
            // For a single-protocol existential, witness table is at offset 4 * pointer size
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
// Vtable for LottieURLSession protocol - stores function pointers to C# implementations
fileprivate struct LottieURLSession_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_lottieDataTask_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _lottieURLSession_vtable = LottieURLSession_vtable()

// EveryProtocol conformance to LottieURLSession
extension EveryProtocol: Lottie.LottieURLSession {
    public func lottieDataTask(with: Foundation.URL, completionHandler: ((Foundation.Data)?, (Foundation.URLResponse)?, (any Swift.Error)?) -> Void) -> (Foundation.URLSessionDataTask)? {
            var selfProto: Lottie.LottieURLSession = self
            var withCopy = with
                var completionHandlerCopy = completionHandler
                let resultPtr = _lottieURLSession_vtable.func_lottieDataTask_0!(
                _lottieURLSession_vtable.csVTHandle, &selfProto, &withCopy, &completionHandlerCopy)
            return resultPtr.assumingMemoryBound(to: (Foundation.URLSessionDataTask)?.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetLottieURLSession_vtable")
public func setLottieURLSession_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<LottieURLSession_vtable> = uvt.assumingMemoryBound(to: LottieURLSession_vtable.self)
    _lottieURLSession_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to LottieURLSession.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_LottieURLSession_WitnessTable")
public func getEveryProtocolLottieURLSessionWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.LottieURLSession = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout for class-bound protocols:
            // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
            // For a single-protocol existential, witness table is at offset 4 * pointer size
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
// Vtable for AnyValueProvider protocol - stores function pointers to C# implementations
fileprivate struct AnyValueProvider_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_valueType_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_typeErasedStorage_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_hasUpdate_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_value_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _anyValueProvider_vtable = AnyValueProvider_vtable()

// EveryProtocol conformance to AnyValueProvider
extension EveryProtocol: Lottie.AnyValueProvider {
    public var valueType: Any.Type {
        get {
            var selfProto: Lottie.AnyValueProvider = self
            let resultPtr = _anyValueProvider_vtable.func_valueType_get!(
                _anyValueProvider_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Any.Type.self).pointee
        }
    }
    
    public var typeErasedStorage: Lottie.AnyValueProviderStorage {
        get {
            var selfProto: Lottie.AnyValueProvider = self
            let resultPtr = _anyValueProvider_vtable.func_typeErasedStorage_get!(
                _anyValueProvider_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Lottie.AnyValueProviderStorage.self).pointee
        }
    }
    
    public func hasUpdate(frame: CoreGraphics.CGFloat) -> Swift.Bool {
            var selfProto: Lottie.AnyValueProvider = self
            var frameCopy = frame
                let resultPtr = _anyValueProvider_vtable.func_hasUpdate_0!(
                _anyValueProvider_vtable.csVTHandle, &selfProto, &frameCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Bool.self).pointee
    }
    
    public func value(frame: CoreGraphics.CGFloat) -> Any {
            var selfProto: Lottie.AnyValueProvider = self
            var frameCopy = frame
                let resultPtr = _anyValueProvider_vtable.func_value_1!(
                _anyValueProvider_vtable.csVTHandle, &selfProto, &frameCopy)
            return resultPtr.assumingMemoryBound(to: Any.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetAnyValueProvider_vtable")
public func setAnyValueProvider_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<AnyValueProvider_vtable> = uvt.assumingMemoryBound(to: AnyValueProvider_vtable.self)
    _anyValueProvider_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to AnyValueProvider.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_AnyValueProvider_WitnessTable")
public func getEveryProtocolAnyValueProviderWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.AnyValueProvider = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout for class-bound protocols:
            // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
            // For a single-protocol existential, witness table is at offset 4 * pointer size
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
// Witness dispatch accessors for AnyValueProvider
@_silgen_name("SBW_AnyValueProvider_method_hasUpdate_0")
public func SBW_AnyValueProvider_method_hasUpdate_0(_ containerPtr: UnsafeRawPointer, _ arg0Ptr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any Lottie.AnyValueProvider).self)
    let arg0 = arg0Ptr.load(as: Double.self)
    let result = existential.hasUpdate(frame: arg0)
    let ptr = UnsafeMutablePointer<Bool>.allocate(capacity: 1)
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}

@_silgen_name("SBW_AnyValueProvider_free_method_hasUpdate_0")
public func SBW_AnyValueProvider_free_method_hasUpdate_0(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Bool.self).deinitialize(count: 1)
    ptr.deallocate()
}

// Vtable for DotLottieCacheProvider protocol - stores function pointers to C# implementations
fileprivate struct DotLottieCacheProvider_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_file_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_setFile_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_clearCache_2: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> Void)?
}

private var _dotLottieCacheProvider_vtable = DotLottieCacheProvider_vtable()

// EveryProtocol conformance to DotLottieCacheProvider
extension EveryProtocol: Lottie.DotLottieCacheProvider {
    public func file(forKey: Swift.String) -> (Lottie.DotLottieFile)? {
            var selfProto: Lottie.DotLottieCacheProvider = self
            var forKeyCopy = forKey
                let resultPtr = _dotLottieCacheProvider_vtable.func_file_0!(
                _dotLottieCacheProvider_vtable.csVTHandle, &selfProto, &forKeyCopy)
            return resultPtr.assumingMemoryBound(to: (Lottie.DotLottieFile)?.self).pointee
    }
    
    public func setFile(_ arg0: Lottie.DotLottieFile, forKey: Swift.String) {
            var selfProto: Lottie.DotLottieCacheProvider = self
            var arg0Copy = arg0
                var forKeyCopy = forKey
                _dotLottieCacheProvider_vtable.func_setFile_1!(
                _dotLottieCacheProvider_vtable.csVTHandle, &selfProto, &arg0Copy, &forKeyCopy)
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetDotLottieCacheProvider_vtable")
public func setDotLottieCacheProvider_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<DotLottieCacheProvider_vtable> = uvt.assumingMemoryBound(to: DotLottieCacheProvider_vtable.self)
    _dotLottieCacheProvider_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to DotLottieCacheProvider.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_DotLottieCacheProvider_WitnessTable")
public func getEveryProtocolDotLottieCacheProviderWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.DotLottieCacheProvider = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout for class-bound protocols:
            // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
            // For a single-protocol existential, witness table is at offset 4 * pointer size
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
// Witness dispatch accessors for DotLottieCacheProvider
@_silgen_name("SBW_DotLottieCacheProvider_method_clearCache_2")
public func SBW_DotLottieCacheProvider_method_clearCache_2(_ containerPtr: UnsafeRawPointer) {
    let existential = containerPtr.load(as: (any Lottie.DotLottieCacheProvider).self)
    existential.clearCache()
}


// Vtable for AnimationImageProvider protocol - stores function pointers to C# implementations
fileprivate struct AnimationImageProvider_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_cacheEligible_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_imageForAsset_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_contentsGravity_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _animationImageProvider_vtable = AnimationImageProvider_vtable()

// EveryProtocol conformance to AnimationImageProvider
extension EveryProtocol: Lottie.AnimationImageProvider {
    public var cacheEligible: Swift.Bool {
        get {
            var selfProto: Lottie.AnimationImageProvider = self
            let resultPtr = _animationImageProvider_vtable.func_cacheEligible_get!(
                _animationImageProvider_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.Bool.self).pointee
        }
    }
    
    public func imageForAsset(asset: Lottie.ImageAsset) -> (CoreGraphics.CGImage)? {
            var selfProto: Lottie.AnimationImageProvider = self
            var assetCopy = asset
                let resultPtr = _animationImageProvider_vtable.func_imageForAsset_0!(
                _animationImageProvider_vtable.csVTHandle, &selfProto, &assetCopy)
            return resultPtr.assumingMemoryBound(to: (CoreGraphics.CGImage)?.self).pointee
    }
    
    public func contentsGravity(for forValue: Lottie.ImageAsset) -> QuartzCore.CALayerContentsGravity {
            var selfProto: Lottie.AnimationImageProvider = self
            var forValueCopy = forValue
                let resultPtr = _animationImageProvider_vtable.func_contentsGravity_1!(
                _animationImageProvider_vtable.csVTHandle, &selfProto, &forValueCopy)
            return resultPtr.assumingMemoryBound(to: QuartzCore.CALayerContentsGravity.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetAnimationImageProvider_vtable")
public func setAnimationImageProvider_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<AnimationImageProvider_vtable> = uvt.assumingMemoryBound(to: AnimationImageProvider_vtable.self)
    _animationImageProvider_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to AnimationImageProvider.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_AnimationImageProvider_WitnessTable")
public func getEveryProtocolAnimationImageProviderWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.AnimationImageProvider = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout for class-bound protocols:
            // [payload0] [payload1] [payload2] [metadata] [witness_tables...]
            // For a single-protocol existential, witness table is at offset 4 * pointer size
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
// Witness dispatch accessors for AnimationImageProvider
@_silgen_name("SBW_AnimationImageProvider_get_cacheEligible_0")
public func SBW_AnimationImageProvider_get_cacheEligible_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any Lottie.AnimationImageProvider).self)
    let result = existential.cacheEligible
    let ptr = UnsafeMutablePointer<Bool>.allocate(capacity: 1)
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}
@_silgen_name("SBW_AnimationImageProvider_free_get_cacheEligible_0")
public func SBW_AnimationImageProvider_free_get_cacheEligible_0(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Bool.self).deinitialize(count: 1)
    ptr.deallocate()
}


extension Lottie.LottieAnimationLayer {
    @_silgen_name("DBW_LottieAnimationLayer_init_B0F42190_1")
    public static func _dbw_init_B0F42190_1(_ configuration: LottieConfiguration) -> Lottie.LottieAnimationLayer {
        return Lottie.LottieAnimationLayer(configuration: configuration)
    }
}

extension Lottie.LottieAnimationLayer {
    @_silgen_name("DBW_LottieAnimationLayer_play_08706907_1")
    public func _dbw_play_08706907_1() -> () {
        return self.play()
    }
}

extension Lottie.LottieAnimationLayer {
    @_silgen_name("DBW_LottieAnimationLayer_setPlaybackMode_C8F59DE9_1")
    public func _dbw_setPlaybackMode_C8F59DE9_1(_ arg0: LottiePlaybackMode) -> () {
        return self.setPlaybackMode(arg0)
    }
}

extension Lottie.LottieAnimation {
    @_silgen_name("DBW_LottieAnimation_filepath_93C25C3D_1")
    public static func _dbw_filepath_93C25C3D_1(_ arg0: String) -> Optional<LottieAnimation> {
        return Self.filepath(arg0)
    }
}

extension Lottie.LottieAnimation {
    @_silgen_name("DBW_LottieAnimation_from_3BFA5D89_1")
    public static func _dbw_from_3BFA5D89_1(_ data: Data) throws -> LottieAnimation {
        return try Self.from(data: data)
    }
}
extension Lottie.LottieAnimation {
    @_silgen_name("$s6Lottie0A9AnimationC10loadedFrom3url7session14animationCacheACSg10Foundation3URLV_AA0A10URLSession_pAA0bH8Provider_pSgtYaFZ_async")
    public static func PInvoke_loadedFrom_744B260C(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, url: UnsafeRawPointer, session: UnsafeRawPointer, animationCache: Swift.Optional<any Lottie.AnimationCacheProvider>){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let urlValue = url.assumingMemoryBound(to: Foundation.URL.self).pointee
        let sessionValue = session.load(as: (any Lottie.LottieURLSession).self)
        

        Task {
            do {
                let resultloadedFrom = try await Lottie.LottieAnimation.loadedFrom(
                    url: urlValue, session: sessionValue, animationCache: animationCache
                )
                // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<Swift.Optional<Lottie.LottieAnimation>>.size,
                                alignment: MemoryLayout<Swift.Optional<Lottie.LottieAnimation>>.alignment)
                            _rawPtr.storeBytes(of: resultloadedFrom, as: Swift.Optional<Lottie.LottieAnimation>.self)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
                callback(_resultPtr, task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}

extension Lottie.LottieAnimation {
    @_silgen_name("DBW_LottieAnimation_loadedFrom_4F9CA3D1_2")
    public static func _dbw_loadedFrom_4F9CA3D1_2(_ url: URL) async -> Optional<LottieAnimation> {
        return await Self.loadedFrom(url: url)
    }
}
extension Lottie.LottieAnimation {
    @_silgen_name("DBW_LottieAnimation_loadedFrom_4F9CA3D1_2_async")
    public static func PInvoke_loadedFrom_46FC101F(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, url: UnsafeRawPointer){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let urlValue = url.assumingMemoryBound(to: Foundation.URL.self).pointee
        

        Task {
            do {
                let resultloadedFrom = try await Lottie.LottieAnimation.loadedFrom(
                    url: urlValue
                )
                // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<Swift.Optional<Lottie.LottieAnimation>>.size,
                                alignment: MemoryLayout<Swift.Optional<Lottie.LottieAnimation>>.alignment)
                            _rawPtr.storeBytes(of: resultloadedFrom, as: Swift.Optional<Lottie.LottieAnimation>.self)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
                callback(_resultPtr, task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}

extension Lottie.LottieAnimation {
    @_silgen_name("DBW_LottieAnimation_loadedFrom_4F9CA3D1_1")
    public static func _dbw_loadedFrom_4F9CA3D1_1(_ url: URL, _ session: LottieURLSession) async -> Optional<LottieAnimation> {
        return await Self.loadedFrom(url: url, session: session)
    }
}
extension Lottie.LottieAnimation {
    @_silgen_name("DBW_LottieAnimation_loadedFrom_4F9CA3D1_1_async")
    public static func PInvoke_loadedFrom_28E71530(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, url: UnsafeRawPointer, session: UnsafeRawPointer){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let urlValue = url.assumingMemoryBound(to: Foundation.URL.self).pointee
        let sessionValue = session.load(as: (any Lottie.LottieURLSession).self)
        

        Task {
            do {
                let resultloadedFrom = try await Lottie.LottieAnimation.loadedFrom(
                    url: urlValue, session: sessionValue
                )
                // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<Swift.Optional<Lottie.LottieAnimation>>.size,
                                alignment: MemoryLayout<Swift.Optional<Lottie.LottieAnimation>>.alignment)
                            _rawPtr.storeBytes(of: resultloadedFrom, as: Swift.Optional<Lottie.LottieAnimation>.self)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
                callback(_resultPtr, task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}

extension Lottie.LottieColor {
    @_silgen_name("DBW_LottieColor_init_5C5A3370_1")
    public static func _dbw_init_5C5A3370_1(_ r: Double, _ g: Double, _ b: Double, _ a: Double) -> Lottie.LottieColor {
        return Lottie.LottieColor(r: r, g: g, b: b, a: a)
    }
}

extension Lottie.LottieConfiguration {
    @_silgen_name("DBW_LottieConfiguration_init_4DF31073_4")
    public static func _dbw_init_4DF31073_4() -> Lottie.LottieConfiguration {
        return Lottie.LottieConfiguration()
    }
}

extension Lottie.LottieConfiguration {
    @_silgen_name("DBW_LottieConfiguration_init_4DF31073_3")
    public static func _dbw_init_4DF31073_3(_ renderingEngine: RenderingEngineOption) -> Lottie.LottieConfiguration {
        return Lottie.LottieConfiguration(renderingEngine: renderingEngine)
    }
}

extension Lottie.LottieConfiguration {
    @_silgen_name("DBW_LottieConfiguration_init_4DF31073_2")
    public static func _dbw_init_4DF31073_2(_ renderingEngine: RenderingEngineOption, _ decodingStrategy: DecodingStrategy) -> Lottie.LottieConfiguration {
        return Lottie.LottieConfiguration(renderingEngine: renderingEngine, decodingStrategy: decodingStrategy)
    }
}

extension Lottie.LottieConfiguration {
    @_silgen_name("DBW_LottieConfiguration_init_4DF31073_1")
    public static func _dbw_init_4DF31073_1(_ renderingEngine: RenderingEngineOption, _ decodingStrategy: DecodingStrategy, _ colorSpace: CGColorSpace) -> Lottie.LottieConfiguration {
        return Lottie.LottieConfiguration(renderingEngine: renderingEngine, decodingStrategy: decodingStrategy, colorSpace: colorSpace)
    }
}

extension Lottie.AnimatedSwitch {
    @_silgen_name("DBW_AnimatedSwitch_setIsOn_FECB6B7F_1")
    public func _dbw_setIsOn_FECB6B7F_1(_ arg0: Bool, _ animated: Bool) -> () {
        return self.setIsOn(arg0, animated: animated)
    }
}

extension Lottie.LottieAnimationView {
    @_silgen_name("DBW_LottieAnimationView_init_4E165D2F_2")
    public static func _dbw_init_4E165D2F_2() -> Lottie.LottieAnimationView {
        return Lottie.LottieAnimationView()
    }
}

extension Lottie.LottieAnimationView {
    @_silgen_name("DBW_LottieAnimationView_play_3438F396_1")
    public func _dbw_play_3438F396_1() -> () {
        return self.play()
    }
}

extension Lottie.LottieAnimationView {
    @_silgen_name("DBW_LottieAnimationView_setPlaybackMode_EAA5C63A_1")
    public func _dbw_setPlaybackMode_EAA5C63A_1(_ arg0: LottiePlaybackMode) -> () {
        return self.setPlaybackMode(arg0)
    }
}

extension Lottie.GradientValueProvider {
    @_silgen_name("DBW_GradientValueProvider_init_09F0DEFF_1")
    public static func _dbw_init_09F0DEFF_1(_ block: @escaping (CGFloat) -> Array<LottieColor>) -> Lottie.GradientValueProvider {
        return Lottie.GradientValueProvider(block: block)
    }
}

extension Lottie.GradientValueProvider {
    @_silgen_name("DBW_GradientValueProvider_init_75F834A4_1")
    public static func _dbw_init_75F834A4_1(_ arg0: Array<LottieColor>) -> Lottie.GradientValueProvider {
        return Lottie.GradientValueProvider(arg0)
    }
}
extension Lottie.DotLottieFile {
    @_silgen_name("$s6Lottie03DotA4FileC10loadedFrom8filepath03dotA5CacheACSS_AA0baH8Provider_pSgtYaKFZ_async")
    public static func PInvoke_loadedFrom_72EB3158(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, filepath: Swift.String, dotLottieCache: Swift.Optional<any Lottie.DotLottieCacheProvider>){
        
        Task {
            do {
                let resultloadedFrom = try await Lottie.DotLottieFile.loadedFrom(
                    filepath: filepath, dotLottieCache: dotLottieCache
                )
                // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<Lottie.DotLottieFile>.size,
                                alignment: MemoryLayout<Lottie.DotLottieFile>.alignment)
                            _rawPtr.storeBytes(of: resultloadedFrom, as: Lottie.DotLottieFile.self)
                            // Retain class to prevent ARC deallocation before C# processes it
                            _ = Unmanaged.passRetained(resultloadedFrom as AnyObject)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
                callback(_resultPtr, task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}

extension Lottie.DotLottieFile {
    @_silgen_name("DBW_DotLottieFile_loadedFrom_2E70B627_1")
    public static func _dbw_loadedFrom_2E70B627_1(_ filepath: String) async throws -> DotLottieFile {
        return try await Self.loadedFrom(filepath: filepath)
    }
}
extension Lottie.DotLottieFile {
    @_silgen_name("DBW_DotLottieFile_loadedFrom_2E70B627_1_async")
    public static func PInvoke_loadedFrom_23603DEF(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, filepath: Swift.String){
        
        Task {
            do {
                let resultloadedFrom = try await Lottie.DotLottieFile.loadedFrom(
                    filepath: filepath
                )
                // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<Lottie.DotLottieFile>.size,
                                alignment: MemoryLayout<Lottie.DotLottieFile>.alignment)
                            _rawPtr.storeBytes(of: resultloadedFrom, as: Lottie.DotLottieFile.self)
                            // Retain class to prevent ARC deallocation before C# processes it
                            _ = Unmanaged.passRetained(resultloadedFrom as AnyObject)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
                callback(_resultPtr, task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}
extension Lottie.DotLottieFile {
    @_silgen_name("$s6Lottie03DotA4FileC10loadedFrom3url7session03dotA5CacheAC10Foundation3URLV_AA0A10URLSession_pAA0baI8Provider_pSgtYaKFZ_async")
    public static func PInvoke_loadedFrom_1BC9C689(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, url: UnsafeRawPointer, session: UnsafeRawPointer, dotLottieCache: Swift.Optional<any Lottie.DotLottieCacheProvider>){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let urlValue = url.assumingMemoryBound(to: Foundation.URL.self).pointee
        let sessionValue = session.load(as: (any Lottie.LottieURLSession).self)
        

        Task {
            do {
                let resultloadedFrom = try await Lottie.DotLottieFile.loadedFrom(
                    url: urlValue, session: sessionValue, dotLottieCache: dotLottieCache
                )
                // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<Lottie.DotLottieFile>.size,
                                alignment: MemoryLayout<Lottie.DotLottieFile>.alignment)
                            _rawPtr.storeBytes(of: resultloadedFrom, as: Lottie.DotLottieFile.self)
                            // Retain class to prevent ARC deallocation before C# processes it
                            _ = Unmanaged.passRetained(resultloadedFrom as AnyObject)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
                callback(_resultPtr, task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}

extension Lottie.DotLottieFile {
    @_silgen_name("DBW_DotLottieFile_loadedFrom_420C0542_2")
    public static func _dbw_loadedFrom_420C0542_2(_ url: URL) async throws -> DotLottieFile {
        return try await Self.loadedFrom(url: url)
    }
}
extension Lottie.DotLottieFile {
    @_silgen_name("DBW_DotLottieFile_loadedFrom_420C0542_2_async")
    public static func PInvoke_loadedFrom_3D9E4130(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, url: UnsafeRawPointer){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let urlValue = url.assumingMemoryBound(to: Foundation.URL.self).pointee
        

        Task {
            do {
                let resultloadedFrom = try await Lottie.DotLottieFile.loadedFrom(
                    url: urlValue
                )
                // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<Lottie.DotLottieFile>.size,
                                alignment: MemoryLayout<Lottie.DotLottieFile>.alignment)
                            _rawPtr.storeBytes(of: resultloadedFrom, as: Lottie.DotLottieFile.self)
                            // Retain class to prevent ARC deallocation before C# processes it
                            _ = Unmanaged.passRetained(resultloadedFrom as AnyObject)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
                callback(_resultPtr, task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}
extension Lottie.DotLottieFile {
    @_silgen_name("$s6Lottie03DotA4FileC10loadedFrom4data8filename13dispatchQueueAC10Foundation4DataV_SSSo03OS_H6_queueCtYaKFZ_async")
    public static func PInvoke_loadedFrom_3ECEC45D(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, data: Foundation.Data, filename: Swift.String, dispatchQueue: UnsafeRawPointer){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let dispatchQueueValue = dispatchQueue.assumingMemoryBound(to: Dispatch.DispatchQueue.self).pointee
        

        Task {
            do {
                let resultloadedFrom = try await Lottie.DotLottieFile.loadedFrom(
                    data: data, filename: filename, dispatchQueue: dispatchQueueValue
                )
                // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<Lottie.DotLottieFile>.size,
                                alignment: MemoryLayout<Lottie.DotLottieFile>.alignment)
                            _rawPtr.storeBytes(of: resultloadedFrom, as: Lottie.DotLottieFile.self)
                            // Retain class to prevent ARC deallocation before C# processes it
                            _ = Unmanaged.passRetained(resultloadedFrom as AnyObject)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
                callback(_resultPtr, task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}

extension Lottie.LottieLogger {
    @_silgen_name("DBW_LottieLogger_info_EE85B5E7_1")
    public func _dbw_info_EE85B5E7_1() -> () {
        return self.info()
    }
}
