import Lottie
import Foundation
import CoreFoundation
import CoreGraphics
import CoreText
import QuartzCore

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
// Vtable for Interpolatable protocol - stores function pointers to C# implementations
fileprivate struct Interpolatable_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_interpolate_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func__interpolate_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _interpolatable_vtable = Interpolatable_vtable()

// EveryProtocol conformance to Interpolatable
extension EveryProtocol: Lottie.Interpolatable {
    public func interpolate(to: Any, amount: CoreGraphics.CGFloat) -> Any {
            var selfProto: Lottie.Interpolatable = self
            var toCopy = to
                var amountCopy = amount
                let resultPtr = _interpolatable_vtable.func_interpolate_0!(
                _interpolatable_vtable.csVTHandle, &selfProto, &toCopy, &amountCopy)
            return resultPtr.assumingMemoryBound(to: Any.self).pointee
    }
    
    public func _interpolate(to: Any, amount: CoreGraphics.CGFloat, spatialOutTangent: (CoreFoundation.CGPoint)?, spatialInTangent: (CoreFoundation.CGPoint)?) -> Any {
            var selfProto: Lottie.Interpolatable = self
            var toCopy = to
                var amountCopy = amount
                var spatialOutTangentCopy = spatialOutTangent
                var spatialInTangentCopy = spatialInTangent
                let resultPtr = _interpolatable_vtable.func__interpolate_1!(
                _interpolatable_vtable.csVTHandle, &selfProto, &toCopy, &amountCopy, &spatialOutTangentCopy, &spatialInTangentCopy)
            return resultPtr.assumingMemoryBound(to: Any.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetInterpolatable_vtable")
public func setInterpolatable_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<Interpolatable_vtable> = uvt.assumingMemoryBound(to: Interpolatable_vtable.self)
    _interpolatable_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to Interpolatable.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_Interpolatable_WitnessTable")
public func getEveryProtocolInterpolatableWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.Interpolatable = instance
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
// Vtable for SpatialInterpolatable protocol - stores function pointers to C# implementations
fileprivate struct SpatialInterpolatable_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_interpolate_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_interpolate_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func__interpolate_2: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _spatialInterpolatable_vtable = SpatialInterpolatable_vtable()

// EveryProtocol conformance to SpatialInterpolatable
extension EveryProtocol: Lottie.SpatialInterpolatable {
    public func interpolate(to: Any, amount: CoreGraphics.CGFloat, spatialOutTangent: (CoreFoundation.CGPoint)?, spatialInTangent: (CoreFoundation.CGPoint)?) -> Any {
            var selfProto: Lottie.SpatialInterpolatable = self
            var toCopy = to
                var amountCopy = amount
                var spatialOutTangentCopy = spatialOutTangent
                var spatialInTangentCopy = spatialInTangent
                let resultPtr = _spatialInterpolatable_vtable.func_interpolate_0!(
                _spatialInterpolatable_vtable.csVTHandle, &selfProto, &toCopy, &amountCopy, &spatialOutTangentCopy, &spatialInTangentCopy)
            return resultPtr.assumingMemoryBound(to: Any.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetSpatialInterpolatable_vtable")
public func setSpatialInterpolatable_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<SpatialInterpolatable_vtable> = uvt.assumingMemoryBound(to: SpatialInterpolatable_vtable.self)
    _spatialInterpolatable_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to SpatialInterpolatable.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_SpatialInterpolatable_WitnessTable")
public func getEveryProtocolSpatialInterpolatableWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.SpatialInterpolatable = instance
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
// Vtable for AnyInterpolatable protocol - stores function pointers to C# implementations
fileprivate struct AnyInterpolatable_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func__interpolate_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _anyInterpolatable_vtable = AnyInterpolatable_vtable()

// EveryProtocol conformance to AnyInterpolatable
extension EveryProtocol: Lottie.AnyInterpolatable {
}

// Called by C# to register the protocol vtable
@_silgen_name("SetAnyInterpolatable_vtable")
public func setAnyInterpolatable_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<AnyInterpolatable_vtable> = uvt.assumingMemoryBound(to: AnyInterpolatable_vtable.self)
    _anyInterpolatable_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to AnyInterpolatable.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_AnyInterpolatable_WitnessTable")
public func getEveryProtocolAnyInterpolatableWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Lottie.AnyInterpolatable = instance
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
extension Lottie.DotLottieFile {
    @_silgen_name("$s6Lottie03DotA4FileC10loadedFrom4data8filename13dispatchQueueAC10Foundation4DataV_SSSo03OS_H6_queueCtYaKFZ_async")
    public static func PInvoke_loadedFrom_3720C3CA(callback: @escaping @convention(c) (Lottie.DotLottieFile, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, data: Foundation.Data, filename: Swift.String, dispatchQueue: UnsafeRawPointer){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let dispatchQueueValue = dispatchQueue.assumingMemoryBound(to: Dispatch.DispatchQueue.self).pointee
        

        Task {
            do {
                let resultloadedFrom = try await Lottie.DotLottieFile.loadedFrom(
                    data: data, filename: filename, dispatchQueue: dispatchQueueValue
                )
                callback(resultloadedFrom, task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}
