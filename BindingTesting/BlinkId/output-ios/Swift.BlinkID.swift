import BlinkID
import Foundation
import UIKit

@frozen
public struct SBW_Utf8Slice {
    public var ptr: UnsafeMutablePointer<UInt8>
    public var len: Int
}
// Static empty buffer for empty string slices (required for @convention(c) compatibility)
fileprivate var _sbw_emptyBuffer: UInt8 = 0
@_silgen_name("SBW_Free_BlinkID")
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
// Vtable for InputImageResultProtocol protocol - stores function pointers to C# implementations
fileprivate struct InputImageResultProtocol_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_rawData_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_uiImage_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _inputImageResultProtocol_vtable = InputImageResultProtocol_vtable()

// EveryProtocol conformance to InputImageResultProtocol
extension EveryProtocol: BlinkID.InputImageResultProtocol {
    public var rawData: Foundation.Data {
        get {
            var selfProto: BlinkID.InputImageResultProtocol = self
            let resultPtr = _inputImageResultProtocol_vtable.func_rawData_get!(
                _inputImageResultProtocol_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Foundation.Data.self).pointee
        }
    }
    
    public var uiImage: (UIKit.UIImage)? {
        get {
            var selfProto: BlinkID.InputImageResultProtocol = self
            let resultPtr = _inputImageResultProtocol_vtable.func_uiImage_get!(
                _inputImageResultProtocol_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: (UIKit.UIImage)?.self).pointee
        }
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetInputImageResultProtocol_vtable")
public func setInputImageResultProtocol_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<InputImageResultProtocol_vtable> = uvt.assumingMemoryBound(to: InputImageResultProtocol_vtable.self)
    _inputImageResultProtocol_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to InputImageResultProtocol.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_InputImageResultProtocol_WitnessTable")
public func getEveryProtocolInputImageResultProtocolWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any BlinkID.InputImageResultProtocol = instance
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
// Vtable for SdkSettings protocol - stores function pointers to C# implementations
fileprivate struct SdkSettings_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_licenseKey_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_licenseKey_set: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_licensee_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_licensee_set: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_helloLogEnabled_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_helloLogEnabled_set: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_downloadResources_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_downloadResources_set: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_resourceDownloadUrl_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_resourceDownloadUrl_set: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_resourceLocalFolder_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_resourceLocalFolder_set: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_bundleURL_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_bundleURL_set: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_resourceRequestTimeout_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_resourceRequestTimeout_set: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
}

private var _sdkSettings_vtable = SdkSettings_vtable()

// EveryProtocol conformance to SdkSettings
extension EveryProtocol: BlinkID.SdkSettings {
    public var licenseKey: Swift.String {
        get {
            var selfProto: BlinkID.SdkSettings = self
            let resultPtr = _sdkSettings_vtable.func_licenseKey_get!(
                _sdkSettings_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.String.self).pointee
        }
        set {
            var selfProto: BlinkID.SdkSettings = self
            var newValueCopy = newValue
            _sdkSettings_vtable.func_licenseKey_set!(
                _sdkSettings_vtable.csVTHandle, &selfProto, &newValueCopy)
        }
    }
    
    public var licensee: (Swift.String)? {
        get {
            var selfProto: BlinkID.SdkSettings = self
            let resultPtr = _sdkSettings_vtable.func_licensee_get!(
                _sdkSettings_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: (Swift.String)?.self).pointee
        }
        set {
            var selfProto: BlinkID.SdkSettings = self
            var newValueCopy = newValue
            _sdkSettings_vtable.func_licensee_set!(
                _sdkSettings_vtable.csVTHandle, &selfProto, &newValueCopy)
        }
    }
    
    public var helloLogEnabled: Swift.Bool {
        get {
            var selfProto: BlinkID.SdkSettings = self
            let resultPtr = _sdkSettings_vtable.func_helloLogEnabled_get!(
                _sdkSettings_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.Bool.self).pointee
        }
        set {
            var selfProto: BlinkID.SdkSettings = self
            var newValueCopy = newValue
            _sdkSettings_vtable.func_helloLogEnabled_set!(
                _sdkSettings_vtable.csVTHandle, &selfProto, &newValueCopy)
        }
    }
    
    public var downloadResources: Swift.Bool {
        get {
            var selfProto: BlinkID.SdkSettings = self
            let resultPtr = _sdkSettings_vtable.func_downloadResources_get!(
                _sdkSettings_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.Bool.self).pointee
        }
        set {
            var selfProto: BlinkID.SdkSettings = self
            var newValueCopy = newValue
            _sdkSettings_vtable.func_downloadResources_set!(
                _sdkSettings_vtable.csVTHandle, &selfProto, &newValueCopy)
        }
    }
    
    public var resourceDownloadUrl: Swift.String {
        get {
            var selfProto: BlinkID.SdkSettings = self
            let resultPtr = _sdkSettings_vtable.func_resourceDownloadUrl_get!(
                _sdkSettings_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.String.self).pointee
        }
        set {
            var selfProto: BlinkID.SdkSettings = self
            var newValueCopy = newValue
            _sdkSettings_vtable.func_resourceDownloadUrl_set!(
                _sdkSettings_vtable.csVTHandle, &selfProto, &newValueCopy)
        }
    }
    
    public var resourceLocalFolder: Swift.String {
        get {
            var selfProto: BlinkID.SdkSettings = self
            let resultPtr = _sdkSettings_vtable.func_resourceLocalFolder_get!(
                _sdkSettings_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.String.self).pointee
        }
        set {
            var selfProto: BlinkID.SdkSettings = self
            var newValueCopy = newValue
            _sdkSettings_vtable.func_resourceLocalFolder_set!(
                _sdkSettings_vtable.csVTHandle, &selfProto, &newValueCopy)
        }
    }
    
    public var bundleURL: (Foundation.URL)? {
        get {
            var selfProto: BlinkID.SdkSettings = self
            let resultPtr = _sdkSettings_vtable.func_bundleURL_get!(
                _sdkSettings_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: (Foundation.URL)?.self).pointee
        }
        set {
            var selfProto: BlinkID.SdkSettings = self
            var newValueCopy = newValue
            _sdkSettings_vtable.func_bundleURL_set!(
                _sdkSettings_vtable.csVTHandle, &selfProto, &newValueCopy)
        }
    }
    
    public var resourceRequestTimeout: BlinkID.RequestTimeout {
        get {
            var selfProto: BlinkID.SdkSettings = self
            let resultPtr = _sdkSettings_vtable.func_resourceRequestTimeout_get!(
                _sdkSettings_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: BlinkID.RequestTimeout.self).pointee
        }
        set {
            var selfProto: BlinkID.SdkSettings = self
            var newValueCopy = newValue
            _sdkSettings_vtable.func_resourceRequestTimeout_set!(
                _sdkSettings_vtable.csVTHandle, &selfProto, &newValueCopy)
        }
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetSdkSettings_vtable")
public func setSdkSettings_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<SdkSettings_vtable> = uvt.assumingMemoryBound(to: SdkSettings_vtable.self)
    _sdkSettings_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to SdkSettings.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_SdkSettings_WitnessTable")
public func getEveryProtocolSdkSettingsWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any BlinkID.SdkSettings = instance
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
// Witness dispatch accessors for SdkSettings
@_silgen_name("SBW_SdkSettings_get_licenseKey_0")
public func SBW_SdkSettings_get_licenseKey_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any BlinkID.SdkSettings).self)
    let result: String = existential.licenseKey
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
@_silgen_name("SBW_SdkSettings_free_get_licenseKey_0")
public func SBW_SdkSettings_free_get_licenseKey_0(_ ptr: UnsafeMutableRawPointer) {
    let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
    slicePtr.pointee.ptr.deallocate()
    slicePtr.deinitialize(count: 1)
    slicePtr.deallocate()
}
@_silgen_name("SBW_SdkSettings_get_helloLogEnabled_0")
public func SBW_SdkSettings_get_helloLogEnabled_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any BlinkID.SdkSettings).self)
    let result = existential.helloLogEnabled
    let ptr = UnsafeMutablePointer<Bool>.allocate(capacity: 1)
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}
@_silgen_name("SBW_SdkSettings_free_get_helloLogEnabled_0")
public func SBW_SdkSettings_free_get_helloLogEnabled_0(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Bool.self).deinitialize(count: 1)
    ptr.deallocate()
}
@_silgen_name("SBW_SdkSettings_get_downloadResources_0")
public func SBW_SdkSettings_get_downloadResources_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any BlinkID.SdkSettings).self)
    let result = existential.downloadResources
    let ptr = UnsafeMutablePointer<Bool>.allocate(capacity: 1)
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}
@_silgen_name("SBW_SdkSettings_free_get_downloadResources_0")
public func SBW_SdkSettings_free_get_downloadResources_0(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Bool.self).deinitialize(count: 1)
    ptr.deallocate()
}
@_silgen_name("SBW_SdkSettings_get_resourceDownloadUrl_0")
public func SBW_SdkSettings_get_resourceDownloadUrl_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any BlinkID.SdkSettings).self)
    let result: String = existential.resourceDownloadUrl
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
@_silgen_name("SBW_SdkSettings_free_get_resourceDownloadUrl_0")
public func SBW_SdkSettings_free_get_resourceDownloadUrl_0(_ ptr: UnsafeMutableRawPointer) {
    let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
    slicePtr.pointee.ptr.deallocate()
    slicePtr.deinitialize(count: 1)
    slicePtr.deallocate()
}
@_silgen_name("SBW_SdkSettings_get_resourceLocalFolder_0")
public func SBW_SdkSettings_get_resourceLocalFolder_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any BlinkID.SdkSettings).self)
    let result: String = existential.resourceLocalFolder
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
@_silgen_name("SBW_SdkSettings_free_get_resourceLocalFolder_0")
public func SBW_SdkSettings_free_get_resourceLocalFolder_0(_ ptr: UnsafeMutableRawPointer) {
    let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
    slicePtr.pointee.ptr.deallocate()
    slicePtr.deinitialize(count: 1)
    slicePtr.deallocate()
}
@_silgen_name("SBW_SdkSettings_set_licenseKey_0")
public func SBW_SdkSettings_set_licenseKey_0(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
    let typedPtr = containerPtr.assumingMemoryBound(to: (any BlinkID.SdkSettings).self)
    var existential = typedPtr.pointee
    let slice = valuePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    existential.licenseKey = str
    typedPtr.pointee = existential
}
@_silgen_name("SBW_SdkSettings_set_helloLogEnabled_0")
public func SBW_SdkSettings_set_helloLogEnabled_0(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
    let typedPtr = containerPtr.assumingMemoryBound(to: (any BlinkID.SdkSettings).self)
    var existential = typedPtr.pointee
    existential.helloLogEnabled = valuePtr.load(as: Bool.self)
    typedPtr.pointee = existential
}
@_silgen_name("SBW_SdkSettings_set_downloadResources_0")
public func SBW_SdkSettings_set_downloadResources_0(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
    let typedPtr = containerPtr.assumingMemoryBound(to: (any BlinkID.SdkSettings).self)
    var existential = typedPtr.pointee
    existential.downloadResources = valuePtr.load(as: Bool.self)
    typedPtr.pointee = existential
}
@_silgen_name("SBW_SdkSettings_set_resourceDownloadUrl_0")
public func SBW_SdkSettings_set_resourceDownloadUrl_0(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
    let typedPtr = containerPtr.assumingMemoryBound(to: (any BlinkID.SdkSettings).self)
    var existential = typedPtr.pointee
    let slice = valuePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    existential.resourceDownloadUrl = str
    typedPtr.pointee = existential
}
@_silgen_name("SBW_SdkSettings_set_resourceLocalFolder_0")
public func SBW_SdkSettings_set_resourceLocalFolder_0(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
    let typedPtr = containerPtr.assumingMemoryBound(to: (any BlinkID.SdkSettings).self)
    var existential = typedPtr.pointee
    let slice = valuePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    existential.resourceLocalFolder = str
    typedPtr.pointee = existential
}

@_silgen_name("SBW_BlinkID_Country_InitWithRawValue")
public func SBW_BlinkID_Country_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.Country? = BlinkID.Country(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.Country?.self)
}
@_silgen_name("SBW_BlinkID_Region_InitWithRawValue")
public func SBW_BlinkID_Region_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.Region? = BlinkID.Region(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.Region?.self)
}
@_silgen_name("SBW_BlinkID_DocumentType_InitWithRawValue")
public func SBW_BlinkID_DocumentType_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.DocumentType? = BlinkID.DocumentType(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.DocumentType?.self)
}
@_silgen_name("SBW_BlinkID_DetectionStatus_InitWithRawValue")
public func SBW_BlinkID_DetectionStatus_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.DetectionStatus? = BlinkID.DetectionStatus(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.DetectionStatus?.self)
}
@_silgen_name("SBW_BlinkID_CameraHardwareInfoPinglet_CameraFacing_InitWithRawValue")
public func SBW_BlinkID_CameraHardwareInfoPinglet_CameraFacing_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.CameraHardwareInfoPinglet.CameraFacing? = BlinkID.CameraHardwareInfoPinglet.CameraFacing(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.CameraHardwareInfoPinglet.CameraFacing?.self)
}
@_silgen_name("SBW_BlinkID_CameraHardwareInfoPinglet_Focus_InitWithRawValue")
public func SBW_BlinkID_CameraHardwareInfoPinglet_Focus_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.CameraHardwareInfoPinglet.Focus? = BlinkID.CameraHardwareInfoPinglet.Focus(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.CameraHardwareInfoPinglet.Focus?.self)
}
@_silgen_name("SBW_BlinkID_ScanningConditionsPinglet_UpdateType_InitWithRawValue")
public func SBW_BlinkID_ScanningConditionsPinglet_UpdateType_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.ScanningConditionsPinglet.UpdateType? = BlinkID.ScanningConditionsPinglet.UpdateType(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.ScanningConditionsPinglet.UpdateType?.self)
}
@_silgen_name("SBW_BlinkID_ScanningConditionsPinglet_DeviceOrientation_InitWithRawValue")
public func SBW_BlinkID_ScanningConditionsPinglet_DeviceOrientation_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.ScanningConditionsPinglet.DeviceOrientation? = BlinkID.ScanningConditionsPinglet.DeviceOrientation(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.ScanningConditionsPinglet.DeviceOrientation?.self)
}
@_silgen_name("SBW_BlinkID_WrapperProductInfoPinglet_WrapperProduct_InitWithRawValue")
public func SBW_BlinkID_WrapperProductInfoPinglet_WrapperProduct_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.WrapperProductInfoPinglet.WrapperProduct? = BlinkID.WrapperProductInfoPinglet.WrapperProduct(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.WrapperProductInfoPinglet.WrapperProduct?.self)
}
@_silgen_name("SBW_BlinkID_UxEventPinglet_EventType_InitWithRawValue")
public func SBW_BlinkID_UxEventPinglet_EventType_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.UxEventPinglet.EventType? = BlinkID.UxEventPinglet.EventType(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.UxEventPinglet.EventType?.self)
}
@_silgen_name("SBW_BlinkID_UxEventPinglet_ErrorMessageType_InitWithRawValue")
public func SBW_BlinkID_UxEventPinglet_ErrorMessageType_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.UxEventPinglet.ErrorMessageType? = BlinkID.UxEventPinglet.ErrorMessageType(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.UxEventPinglet.ErrorMessageType?.self)
}
@_silgen_name("SBW_BlinkID_UxEventPinglet_AlertType_InitWithRawValue")
public func SBW_BlinkID_UxEventPinglet_AlertType_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.UxEventPinglet.AlertType? = BlinkID.UxEventPinglet.AlertType(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.UxEventPinglet.AlertType?.self)
}
@_silgen_name("SBW_BlinkID_UxEventPinglet_HelpCloseType_InitWithRawValue")
public func SBW_BlinkID_UxEventPinglet_HelpCloseType_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.UxEventPinglet.HelpCloseType? = BlinkID.UxEventPinglet.HelpCloseType(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.UxEventPinglet.HelpCloseType?.self)
}
@_silgen_name("SBW_BlinkID_LogPinglet_LogLevel_InitWithRawValue")
public func SBW_BlinkID_LogPinglet_LogLevel_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.LogPinglet.LogLevel? = BlinkID.LogPinglet.LogLevel(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.LogPinglet.LogLevel?.self)
}
@_silgen_name("SBW_BlinkID_SdkInitStartPinglet_Product_InitWithRawValue")
public func SBW_BlinkID_SdkInitStartPinglet_Product_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.SdkInitStartPinglet.Product? = BlinkID.SdkInitStartPinglet.Product(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.SdkInitStartPinglet.Product?.self)
}
@_silgen_name("SBW_BlinkID_SdkInitStartPinglet_Platform_InitWithRawValue")
public func SBW_BlinkID_SdkInitStartPinglet_Platform_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.SdkInitStartPinglet.Platform? = BlinkID.SdkInitStartPinglet.Platform(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.SdkInitStartPinglet.Platform?.self)
}
@_silgen_name("SBW_BlinkID_CameraPermissionPinglet_EventType_InitWithRawValue")
public func SBW_BlinkID_CameraPermissionPinglet_EventType_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.CameraPermissionPinglet.EventType? = BlinkID.CameraPermissionPinglet.EventType(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.CameraPermissionPinglet.EventType?.self)
}
@_silgen_name("SBW_BlinkID_ErrorPinglet_ErrorType_InitWithRawValue")
public func SBW_BlinkID_ErrorPinglet_ErrorType_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.ErrorPinglet.ErrorType? = BlinkID.ErrorPinglet.ErrorType(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.ErrorPinglet.ErrorType?.self)
}
@_silgen_name("SBW_BlinkID_CameraInputInfoPinglet_CameraFacing_InitWithRawValue")
public func SBW_BlinkID_CameraInputInfoPinglet_CameraFacing_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.CameraInputInfoPinglet.CameraFacing? = BlinkID.CameraInputInfoPinglet.CameraFacing(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.CameraInputInfoPinglet.CameraFacing?.self)
}
@_silgen_name("SBW_BlinkID_AnonymizationMode_InitWithRawValue")
public func SBW_BlinkID_AnonymizationMode_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.AnonymizationMode? = BlinkID.AnonymizationMode(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.AnonymizationMode?.self)
}
@_silgen_name("SBW_BlinkID_ProcessingStatus_InitWithRawValue")
public func SBW_BlinkID_ProcessingStatus_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.ProcessingStatus? = BlinkID.ProcessingStatus(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.ProcessingStatus?.self)
}
@_silgen_name("$s7BlinkID0A5IDSdkC21createScanningSession15sessionSettingsAA0A9IDSessionCAA0aiH0V_tYaKF_async")
public func PInvoke_createScanningSession_4203E5DD(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, sessionSettings: UnsafeRawPointer, _self: OpaquePointer){
    // Read non-frozen parameters via .pointee (bitwise copy)
    // C# created copies using InitializeWithCopy (owns a proper reference)
    let sessionSettingsValue = sessionSettings.assumingMemoryBound(to: BlinkID.BlinkIDSessionSettings.self).pointee
    let __self = unsafeBitCast(_self, to: BlinkID.BlinkIDSdk.self)
    // selfInstance is safe - C# called Arc.Retain before invoking this method

    Task {
        do {
            let resultcreateScanningSession = try await __self.createScanningSession(
                sessionSettings: sessionSettingsValue
            )
            // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<BlinkID.BlinkIDSession>.size,
                                alignment: MemoryLayout<BlinkID.BlinkIDSession>.alignment)
                            _rawPtr.storeBytes(of: resultcreateScanningSession, as: BlinkID.BlinkIDSession.self)
                            // Retain class to prevent ARC deallocation before C# processes it
                            _ = Unmanaged.passRetained(resultcreateScanningSession as AnyObject)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
            callback(_resultPtr, task)
        } catch {
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, task) }
        }
    }
}
extension BlinkID.BlinkIDSdk {
    @_silgen_name("$s7BlinkID0A5IDSdkC06createaC012withSettingsAcA0acF0V_tYaKFZ_async")
    public static func PInvoke_createBlinkIDSdk_3EF1C001(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, withSettings: UnsafeRawPointer){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let withSettingsValue = withSettings.assumingMemoryBound(to: BlinkID.BlinkIDSdkSettings.self).pointee
        

        Task {
            do {
                let resultcreateBlinkIDSdk = try await BlinkID.BlinkIDSdk.createBlinkIDSdk(
                    withSettings: withSettingsValue
                )
                // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<BlinkID.BlinkIDSdk>.size,
                                alignment: MemoryLayout<BlinkID.BlinkIDSdk>.alignment)
                            _rawPtr.storeBytes(of: resultcreateBlinkIDSdk, as: BlinkID.BlinkIDSdk.self)
                            // Retain class to prevent ARC deallocation before C# processes it
                            _ = Unmanaged.passRetained(resultcreateBlinkIDSdk as AnyObject)
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
extension BlinkID.BlinkIDSdk {
    @_silgen_name("$s7BlinkID0A5IDSdkC19refreshLicenseLeaseyyYaKFZ_async")
    public static func PInvoke_refreshLicenseLease_31E4C4FB(callback: @escaping @convention(c) (Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64){
        
        Task {
            do {
                try await BlinkID.BlinkIDSdk.refreshLicenseLease(
                    
                )
                
                callback(task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}
@_silgen_name("SBW_BlinkID_RecognitionMode_InitWithRawValue")
public func SBW_BlinkID_RecognitionMode_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.RecognitionMode? = BlinkID.RecognitionMode(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.RecognitionMode?.self)
}
@_silgen_name("$s7BlinkID11PingManagerC10addPinglet7pinglet13sessionNumberyx_SitYaAA0F0RzlF_async")
public func PInvoke_addPinglet_1BC72B9E<P>(callback: @escaping @convention(c) (Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, pinglet: P, sessionNumber: Swift.Int) where P : Pinglet{
    
    // selfInstance is safe - C# called Arc.Retain before invoking this method
    Task {
        do {
            try await BlinkID.PingManager.shared.addPinglet(
                pinglet: pinglet, sessionNumber: sessionNumber
            )
            
            callback(task)
        } catch {
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, task) }
        }
    }
}
@_silgen_name("$s7BlinkID11PingManagerC12sendPingletsAA0C6StatusOyYaF_async")
public func PInvoke_sendPinglets_3B2A271C(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64){
    
    // selfInstance is safe - C# called Arc.Retain before invoking this method
    Task {
        do {
            let resultsendPinglets = try await BlinkID.PingManager.shared.sendPinglets(
                
            )
            // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<BlinkID.PingStatus>.size,
                                alignment: MemoryLayout<BlinkID.PingStatus>.alignment)
                            _rawPtr.storeBytes(of: resultsendPinglets, as: BlinkID.PingStatus.self)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
            callback(_resultPtr, task)
        } catch {
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, task) }
        }
    }
}
@_silgen_name("SBW_BlinkID_FieldType_InitWithRawValue")
public func SBW_BlinkID_FieldType_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.FieldType? = BlinkID.FieldType(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.FieldType?.self)
}
@_silgen_name("SBW_BlinkID_AlphabetType_InitWithRawValue")
public func SBW_BlinkID_AlphabetType_InitWithRawValue(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
    let slice = slicePtr.load(as: SBW_Utf8Slice.self)
    let str: String
    if slice.len > 0 {
        str = String(unsafeUninitializedCapacity: slice.len) { buf in
            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
            return slice.len
        }
    } else {
        str = ""
    }
    let result: BlinkID.AlphabetType? = BlinkID.AlphabetType(rawValue: str)
    resultPtr.storeBytes(of: result, as: BlinkID.AlphabetType?.self)
}
