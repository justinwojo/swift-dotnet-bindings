import Nuke
import Foundation
import CoreFoundation
import CoreGraphics
import CoreImage
import UIKit

@frozen
public struct SBW_Utf8Slice {
    public var ptr: UnsafeMutablePointer<UInt8>
    public var len: Int
}
// Static empty buffer for empty string slices (required for @convention(c) compatibility)
fileprivate var _sbw_emptyBuffer: UInt8 = 0
@_silgen_name("SBW_Free_Nuke")
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
// Vtable for ImageProcessing protocol - stores function pointers to C# implementations
fileprivate struct ImageProcessing_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_identifier_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_hashableIdentifier_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_process_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_process_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _imageProcessing_vtable = ImageProcessing_vtable()

// EveryProtocol conformance to ImageProcessing
extension EveryProtocol: Nuke.ImageProcessing {
    public var identifier: Swift.String {
        get {
            var selfProto: Nuke.ImageProcessing = self
            let resultPtr = _imageProcessing_vtable.func_identifier_get!(
                _imageProcessing_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.String.self).pointee
        }
    }
    
    public var hashableIdentifier: Swift.AnyHashable {
        get {
            var selfProto: Nuke.ImageProcessing = self
            let resultPtr = _imageProcessing_vtable.func_hashableIdentifier_get!(
                _imageProcessing_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.AnyHashable.self).pointee
        }
    }
    
    public func process(_ arg0: UIKit.UIImage) -> (UIKit.UIImage)? {
            var selfProto: Nuke.ImageProcessing = self
            var arg0Copy = arg0
                let resultPtr = _imageProcessing_vtable.func_process_0!(
                _imageProcessing_vtable.csVTHandle, &selfProto, &arg0Copy)
            return resultPtr.assumingMemoryBound(to: (UIKit.UIImage)?.self).pointee
    }
    
    public func process(_ arg0: Nuke.ImageContainer, context: Nuke.ImageProcessingContext) throws -> Nuke.ImageContainer {
            var selfProto: Nuke.ImageProcessing = self
            var arg0Copy = arg0
                var contextCopy = context
                let resultPtr = _imageProcessing_vtable.func_process_1!(
                _imageProcessing_vtable.csVTHandle, &selfProto, &arg0Copy, &contextCopy)
            return resultPtr.assumingMemoryBound(to: Nuke.ImageContainer.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetImageProcessing_vtable")
public func setImageProcessing_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<ImageProcessing_vtable> = uvt.assumingMemoryBound(to: ImageProcessing_vtable.self)
    _imageProcessing_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to ImageProcessing.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_ImageProcessing_WitnessTable")
public func getEveryProtocolImageProcessingWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Nuke.ImageProcessing = instance
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
// Witness dispatch accessors for ImageProcessing
@_silgen_name("SBW_ImageProcessing_get_identifier_0")
public func SBW_ImageProcessing_get_identifier_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any Nuke.ImageProcessing).self)
    let result: String = existential.identifier
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
@_silgen_name("SBW_ImageProcessing_free_get_identifier_0")
public func SBW_ImageProcessing_free_get_identifier_0(_ ptr: UnsafeMutableRawPointer) {
    let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
    slicePtr.pointee.ptr.deallocate()
    slicePtr.deinitialize(count: 1)
    slicePtr.deallocate()
}

// Vtable for ImageEncoding protocol - stores function pointers to C# implementations
fileprivate struct ImageEncoding_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_encode_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_encode_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _imageEncoding_vtable = ImageEncoding_vtable()

// EveryProtocol conformance to ImageEncoding
extension EveryProtocol: Nuke.ImageEncoding {
    public func encode(_ arg0: UIKit.UIImage) -> (Foundation.Data)? {
            var selfProto: Nuke.ImageEncoding = self
            var arg0Copy = arg0
                let resultPtr = _imageEncoding_vtable.func_encode_0!(
                _imageEncoding_vtable.csVTHandle, &selfProto, &arg0Copy)
            return resultPtr.assumingMemoryBound(to: (Foundation.Data)?.self).pointee
    }
    
    public func encode(_ arg0: Nuke.ImageContainer, context: Nuke.ImageEncodingContext) -> (Foundation.Data)? {
            var selfProto: Nuke.ImageEncoding = self
            var arg0Copy = arg0
                var contextCopy = context
                let resultPtr = _imageEncoding_vtable.func_encode_1!(
                _imageEncoding_vtable.csVTHandle, &selfProto, &arg0Copy, &contextCopy)
            return resultPtr.assumingMemoryBound(to: (Foundation.Data)?.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetImageEncoding_vtable")
public func setImageEncoding_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<ImageEncoding_vtable> = uvt.assumingMemoryBound(to: ImageEncoding_vtable.self)
    _imageEncoding_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to ImageEncoding.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_ImageEncoding_WitnessTable")
public func getEveryProtocolImageEncodingWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Nuke.ImageEncoding = instance
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
// Vtable for DataLoading protocol - stores function pointers to C# implementations
fileprivate struct DataLoading_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_loadData_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _dataLoading_vtable = DataLoading_vtable()

// EveryProtocol conformance to DataLoading
extension EveryProtocol: Nuke.DataLoading {
    public func loadData(with: Foundation.URLRequest, didReceiveData: (Foundation.Data, Foundation.URLResponse) -> Void, completion: ((any Swift.Error)?) -> Void) -> any Nuke.Cancellable {
            var selfProto: Nuke.DataLoading = self
            var withCopy = with
                var didReceiveDataCopy = didReceiveData
                var completionCopy = completion
                let resultPtr = _dataLoading_vtable.func_loadData_0!(
                _dataLoading_vtable.csVTHandle, &selfProto, &withCopy, &didReceiveDataCopy, &completionCopy)
            return resultPtr.assumingMemoryBound(to: (any Nuke.Cancellable).self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetDataLoading_vtable")
public func setDataLoading_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<DataLoading_vtable> = uvt.assumingMemoryBound(to: DataLoading_vtable.self)
    _dataLoading_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to DataLoading.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_DataLoading_WitnessTable")
public func getEveryProtocolDataLoadingWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Nuke.DataLoading = instance
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
// Vtable for Cancellable protocol - stores function pointers to C# implementations
fileprivate struct Cancellable_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_cancel_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> Void)?
}

private var _cancellable_vtable = Cancellable_vtable()

// EveryProtocol conformance to Cancellable
extension EveryProtocol: Nuke.Cancellable {
    public func cancel() {
            var selfProto: Nuke.Cancellable = self
            _cancellable_vtable.func_cancel_0!(
                _cancellable_vtable.csVTHandle, &selfProto)
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetCancellable_vtable")
public func setCancellable_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<Cancellable_vtable> = uvt.assumingMemoryBound(to: Cancellable_vtable.self)
    _cancellable_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to Cancellable.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_Cancellable_WitnessTable")
public func getEveryProtocolCancellableWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Nuke.Cancellable = instance
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
// Witness dispatch accessors for Cancellable
@_silgen_name("SBW_Cancellable_method_cancel_0")
public func SBW_Cancellable_method_cancel_0(_ containerPtr: UnsafeRawPointer) {
    let existential = containerPtr.load(as: (any Nuke.Cancellable).self)
    existential.cancel()
}


// Vtable for DataCaching protocol - stores function pointers to C# implementations
fileprivate struct DataCaching_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_cachedData_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_containsData_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_storeData_2: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_removeData_3: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_removeAll_4: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> Void)?
}

private var _dataCaching_vtable = DataCaching_vtable()

// EveryProtocol conformance to DataCaching
extension EveryProtocol: Nuke.DataCaching {
    public func cachedData(for forValue: Swift.String) -> (Foundation.Data)? {
            var selfProto: Nuke.DataCaching = self
            var forValueCopy = forValue
                let resultPtr = _dataCaching_vtable.func_cachedData_0!(
                _dataCaching_vtable.csVTHandle, &selfProto, &forValueCopy)
            return resultPtr.assumingMemoryBound(to: (Foundation.Data)?.self).pointee
    }
    
    public func containsData(for forValue: Swift.String) -> Swift.Bool {
            var selfProto: Nuke.DataCaching = self
            var forValueCopy = forValue
                let resultPtr = _dataCaching_vtable.func_containsData_1!(
                _dataCaching_vtable.csVTHandle, &selfProto, &forValueCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Bool.self).pointee
    }
    
    public func storeData(_ arg0: Foundation.Data, for forValue: Swift.String) {
            var selfProto: Nuke.DataCaching = self
            var arg0Copy = arg0
                var forValueCopy = forValue
                _dataCaching_vtable.func_storeData_2!(
                _dataCaching_vtable.csVTHandle, &selfProto, &arg0Copy, &forValueCopy)
    }
    
    public func removeData(for forValue: Swift.String) {
            var selfProto: Nuke.DataCaching = self
            var forValueCopy = forValue
                _dataCaching_vtable.func_removeData_3!(
                _dataCaching_vtable.csVTHandle, &selfProto, &forValueCopy)
    }
    
    public func removeAll() {
            var selfProto: Nuke.DataCaching = self
            _dataCaching_vtable.func_removeAll_4!(
                _dataCaching_vtable.csVTHandle, &selfProto)
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetDataCaching_vtable")
public func setDataCaching_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<DataCaching_vtable> = uvt.assumingMemoryBound(to: DataCaching_vtable.self)
    _dataCaching_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to DataCaching.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_DataCaching_WitnessTable")
public func getEveryProtocolDataCachingWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Nuke.DataCaching = instance
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
// Witness dispatch accessors for DataCaching
@_silgen_name("SBW_DataCaching_method_containsData_1")
public func SBW_DataCaching_method_containsData_1(_ containerPtr: UnsafeRawPointer, _ arg0Ptr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any Nuke.DataCaching).self)
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
    let result = existential.containsData(for: arg0)
    let ptr = UnsafeMutablePointer<Bool>.allocate(capacity: 1)
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}

@_silgen_name("SBW_DataCaching_free_method_containsData_1")
public func SBW_DataCaching_free_method_containsData_1(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Bool.self).deinitialize(count: 1)
    ptr.deallocate()
}
@_silgen_name("SBW_DataCaching_method_removeData_3")
public func SBW_DataCaching_method_removeData_3(_ containerPtr: UnsafeRawPointer, _ arg0Ptr: UnsafeRawPointer) {
    let existential = containerPtr.load(as: (any Nuke.DataCaching).self)
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
    existential.removeData(for: arg0)
}

@_silgen_name("SBW_DataCaching_method_removeAll_4")
public func SBW_DataCaching_method_removeAll_4(_ containerPtr: UnsafeRawPointer) {
    let existential = containerPtr.load(as: (any Nuke.DataCaching).self)
    existential.removeAll()
}


// Vtable for ImagePipelineDelegate protocol - stores function pointers to C# implementations
fileprivate struct ImagePipelineDelegate_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_dataLoader_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_imageDecoder_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_imageEncoder_2: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_imageCache_3: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_dataCache_4: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_cacheKey_5: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_willCache_6: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_shouldDecompress_7: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_decompress_8: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_imageTaskCreated_9: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_imageTask_10: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_imageTaskDidStart_11: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_imageTask_12: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_imageTask_13: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_imageTaskDidCancel_14: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_imageTask_15: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
}

private var _imagePipelineDelegate_vtable = ImagePipelineDelegate_vtable()

// EveryProtocol conformance to ImagePipelineDelegate
extension EveryProtocol: Nuke.ImagePipelineDelegate {
    public func dataLoader(for forValue: Nuke.ImageRequest, pipeline: Nuke.ImagePipeline) -> any Nuke.DataLoading {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var forValueCopy = forValue
                var pipelineCopy = pipeline
                let resultPtr = _imagePipelineDelegate_vtable.func_dataLoader_0!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &forValueCopy, &pipelineCopy)
            return resultPtr.assumingMemoryBound(to: (any Nuke.DataLoading).self).pointee
    }
    
    public func imageDecoder(for forValue: Nuke.ImageDecodingContext, pipeline: Nuke.ImagePipeline) -> (any Nuke.ImageDecoding)? {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var forValueCopy = forValue
                var pipelineCopy = pipeline
                let resultPtr = _imagePipelineDelegate_vtable.func_imageDecoder_1!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &forValueCopy, &pipelineCopy)
            return resultPtr.assumingMemoryBound(to: (any Nuke.ImageDecoding)?.self).pointee
    }
    
    public func imageEncoder(for forValue: Nuke.ImageEncodingContext, pipeline: Nuke.ImagePipeline) -> any Nuke.ImageEncoding {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var forValueCopy = forValue
                var pipelineCopy = pipeline
                let resultPtr = _imagePipelineDelegate_vtable.func_imageEncoder_2!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &forValueCopy, &pipelineCopy)
            return resultPtr.assumingMemoryBound(to: (any Nuke.ImageEncoding).self).pointee
    }
    
    public func imageCache(for forValue: Nuke.ImageRequest, pipeline: Nuke.ImagePipeline) -> (any Nuke.ImageCaching)? {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var forValueCopy = forValue
                var pipelineCopy = pipeline
                let resultPtr = _imagePipelineDelegate_vtable.func_imageCache_3!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &forValueCopy, &pipelineCopy)
            return resultPtr.assumingMemoryBound(to: (any Nuke.ImageCaching)?.self).pointee
    }
    
    public func dataCache(for forValue: Nuke.ImageRequest, pipeline: Nuke.ImagePipeline) -> (any Nuke.DataCaching)? {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var forValueCopy = forValue
                var pipelineCopy = pipeline
                let resultPtr = _imagePipelineDelegate_vtable.func_dataCache_4!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &forValueCopy, &pipelineCopy)
            return resultPtr.assumingMemoryBound(to: (any Nuke.DataCaching)?.self).pointee
    }
    
    public func cacheKey(for forValue: Nuke.ImageRequest, pipeline: Nuke.ImagePipeline) -> (Swift.String)? {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var forValueCopy = forValue
                var pipelineCopy = pipeline
                let resultPtr = _imagePipelineDelegate_vtable.func_cacheKey_5!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &forValueCopy, &pipelineCopy)
            return resultPtr.assumingMemoryBound(to: (Swift.String)?.self).pointee
    }
    
    public func willCache(data: Foundation.Data, image: (Nuke.ImageContainer)?, for forValue: Nuke.ImageRequest, pipeline: Nuke.ImagePipeline, completion: ((Foundation.Data)?) -> Void) {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var dataCopy = data
                var imageCopy = image
                var forValueCopy = forValue
                var pipelineCopy = pipeline
                var completionCopy = completion
                _imagePipelineDelegate_vtable.func_willCache_6!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &dataCopy, &imageCopy, &forValueCopy, &pipelineCopy, &completionCopy)
    }
    
    public func shouldDecompress(response: Nuke.ImageResponse, for forValue: Nuke.ImageRequest, pipeline: Nuke.ImagePipeline) -> Swift.Bool {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var responseCopy = response
                var forValueCopy = forValue
                var pipelineCopy = pipeline
                let resultPtr = _imagePipelineDelegate_vtable.func_shouldDecompress_7!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &responseCopy, &forValueCopy, &pipelineCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Bool.self).pointee
    }
    
    public func decompress(response: Nuke.ImageResponse, request: Nuke.ImageRequest, pipeline: Nuke.ImagePipeline) -> Nuke.ImageResponse {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var responseCopy = response
                var requestCopy = request
                var pipelineCopy = pipeline
                let resultPtr = _imagePipelineDelegate_vtable.func_decompress_8!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &responseCopy, &requestCopy, &pipelineCopy)
            return resultPtr.assumingMemoryBound(to: Nuke.ImageResponse.self).pointee
    }
    
    public func imageTaskCreated(_ arg0: Nuke.ImageTask, pipeline: Nuke.ImagePipeline) {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var arg0Copy = arg0
                var pipelineCopy = pipeline
                _imagePipelineDelegate_vtable.func_imageTaskCreated_9!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &arg0Copy, &pipelineCopy)
    }
    
    public func imageTask(_ arg0: Nuke.ImageTask, didReceiveEvent: Nuke.ImageTask.Event, pipeline: Nuke.ImagePipeline) {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var arg0Copy = arg0
                var didReceiveEventCopy = didReceiveEvent
                var pipelineCopy = pipeline
                _imagePipelineDelegate_vtable.func_imageTask_10!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &arg0Copy, &didReceiveEventCopy, &pipelineCopy)
    }
    
    public func imageTaskDidStart(_ arg0: Nuke.ImageTask, pipeline: Nuke.ImagePipeline) {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var arg0Copy = arg0
                var pipelineCopy = pipeline
                _imagePipelineDelegate_vtable.func_imageTaskDidStart_11!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &arg0Copy, &pipelineCopy)
    }
    
    public func imageTask(_ arg0: Nuke.ImageTask, didUpdateProgress: Nuke.ImageTask.Progress, pipeline: Nuke.ImagePipeline) {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var arg0Copy = arg0
                var didUpdateProgressCopy = didUpdateProgress
                var pipelineCopy = pipeline
                _imagePipelineDelegate_vtable.func_imageTask_12!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &arg0Copy, &didUpdateProgressCopy, &pipelineCopy)
    }
    
    public func imageTask(_ arg0: Nuke.ImageTask, didReceivePreview: Nuke.ImageResponse, pipeline: Nuke.ImagePipeline) {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var arg0Copy = arg0
                var didReceivePreviewCopy = didReceivePreview
                var pipelineCopy = pipeline
                _imagePipelineDelegate_vtable.func_imageTask_13!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &arg0Copy, &didReceivePreviewCopy, &pipelineCopy)
    }
    
    public func imageTaskDidCancel(_ arg0: Nuke.ImageTask, pipeline: Nuke.ImagePipeline) {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var arg0Copy = arg0
                var pipelineCopy = pipeline
                _imagePipelineDelegate_vtable.func_imageTaskDidCancel_14!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &arg0Copy, &pipelineCopy)
    }
    
    public func imageTask(_ arg0: Nuke.ImageTask, didCompleteWithResult: Swift.Result<Nuke.ImageResponse, Nuke.ImagePipeline.Error>, pipeline: Nuke.ImagePipeline) {
            var selfProto: Nuke.ImagePipelineDelegate = self
            var arg0Copy = arg0
                var didCompleteWithResultCopy = didCompleteWithResult
                var pipelineCopy = pipeline
                _imagePipelineDelegate_vtable.func_imageTask_15!(
                _imagePipelineDelegate_vtable.csVTHandle, &selfProto, &arg0Copy, &didCompleteWithResultCopy, &pipelineCopy)
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetImagePipelineDelegate_vtable")
public func setImagePipelineDelegate_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<ImagePipelineDelegate_vtable> = uvt.assumingMemoryBound(to: ImagePipelineDelegate_vtable.self)
    _imagePipelineDelegate_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to ImagePipelineDelegate.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_ImagePipelineDelegate_WitnessTable")
public func getEveryProtocolImagePipelineDelegateWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Nuke.ImagePipelineDelegate = instance
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
// Vtable for ImageCaching protocol - stores function pointers to C# implementations
fileprivate struct ImageCaching_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_subscript_0_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_subscript_0_set: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_removeAll_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> Void)?
}

private var _imageCaching_vtable = ImageCaching_vtable()

// EveryProtocol conformance to ImageCaching
extension EveryProtocol: Nuke.ImageCaching {
    public subscript(index0: Nuke.ImageCacheKey) -> (Nuke.ImageContainer)? {
        get {
            var selfProto: Nuke.ImageCaching = self
            var index0Copy = index0
            let resultPtr = _imageCaching_vtable.func_subscript_0_get!(
                _imageCaching_vtable.csVTHandle, &selfProto, &index0Copy)
            return resultPtr.assumingMemoryBound(to: (Nuke.ImageContainer)?.self).pointee
        }
        set {
            var selfProto: Nuke.ImageCaching = self
            var newValueCopy = newValue
            var index0Copy = index0
            _imageCaching_vtable.func_subscript_0_set!(
                _imageCaching_vtable.csVTHandle, &selfProto, &newValueCopy, &index0Copy)
        }
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetImageCaching_vtable")
public func setImageCaching_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<ImageCaching_vtable> = uvt.assumingMemoryBound(to: ImageCaching_vtable.self)
    _imageCaching_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to ImageCaching.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_ImageCaching_WitnessTable")
public func getEveryProtocolImageCachingWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Nuke.ImageCaching = instance
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
// Witness dispatch accessors for ImageCaching
@_silgen_name("SBW_ImageCaching_method_removeAll_0")
public func SBW_ImageCaching_method_removeAll_0(_ containerPtr: UnsafeRawPointer) {
    let existential = containerPtr.load(as: (any Nuke.ImageCaching).self)
    existential.removeAll()
}


// Vtable for ImageDecoding protocol - stores function pointers to C# implementations
fileprivate struct ImageDecoding_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_isAsynchronous_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_decode_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_decodePartiallyDownloadedData_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _imageDecoding_vtable = ImageDecoding_vtable()

// EveryProtocol conformance to ImageDecoding
extension EveryProtocol: Nuke.ImageDecoding {
    public var isAsynchronous: Swift.Bool {
        get {
            var selfProto: Nuke.ImageDecoding = self
            let resultPtr = _imageDecoding_vtable.func_isAsynchronous_get!(
                _imageDecoding_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.Bool.self).pointee
        }
    }
    
    public func decode(_ arg0: Foundation.Data) throws -> Nuke.ImageContainer {
            var selfProto: Nuke.ImageDecoding = self
            var arg0Copy = arg0
                let resultPtr = _imageDecoding_vtable.func_decode_0!(
                _imageDecoding_vtable.csVTHandle, &selfProto, &arg0Copy)
            return resultPtr.assumingMemoryBound(to: Nuke.ImageContainer.self).pointee
    }
    
    public func decodePartiallyDownloadedData(_ arg0: Foundation.Data) -> (Nuke.ImageContainer)? {
            var selfProto: Nuke.ImageDecoding = self
            var arg0Copy = arg0
                let resultPtr = _imageDecoding_vtable.func_decodePartiallyDownloadedData_1!(
                _imageDecoding_vtable.csVTHandle, &selfProto, &arg0Copy)
            return resultPtr.assumingMemoryBound(to: (Nuke.ImageContainer)?.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetImageDecoding_vtable")
public func setImageDecoding_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<ImageDecoding_vtable> = uvt.assumingMemoryBound(to: ImageDecoding_vtable.self)
    _imageDecoding_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to ImageDecoding.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_ImageDecoding_WitnessTable")
public func getEveryProtocolImageDecodingWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Nuke.ImageDecoding = instance
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
// Witness dispatch accessors for ImageDecoding
@_silgen_name("SBW_ImageDecoding_get_isAsynchronous_0")
public func SBW_ImageDecoding_get_isAsynchronous_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any Nuke.ImageDecoding).self)
    let result = existential.isAsynchronous
    let ptr = UnsafeMutablePointer<Bool>.allocate(capacity: 1)
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}
@_silgen_name("SBW_ImageDecoding_free_get_isAsynchronous_0")
public func SBW_ImageDecoding_free_get_isAsynchronous_0(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Bool.self).deinitialize(count: 1)
    ptr.deallocate()
}


extension Nuke.ImageResponse {
    @_silgen_name("DBW_ImageResponse_init_F8ACDC69_2")
    public static func _dbw_init_F8ACDC69_2(_ container: ImageContainer, _ request: ImageRequest) -> Nuke.ImageResponse {
        return Nuke.ImageResponse(container: container, request: request)
    }
}

extension Nuke.ImageResponse {
    @_silgen_name("DBW_ImageResponse_init_F8ACDC69_1")
    public static func _dbw_init_F8ACDC69_1(_ container: ImageContainer, _ request: ImageRequest, _ urlResponse: Optional<URLResponse>) -> Nuke.ImageResponse {
        return Nuke.ImageResponse(container: container, request: request, urlResponse: urlResponse)
    }
}

extension Nuke.ImageCache {
    @_silgen_name("DBW_ImageCache_init_F7E1ED79_2")
    public static func _dbw_init_F7E1ED79_2() -> Nuke.ImageCache {
        return Nuke.ImageCache()
    }
}

extension Nuke.ImageCache {
    @_silgen_name("DBW_ImageCache_init_F7E1ED79_1")
    public static func _dbw_init_F7E1ED79_1(_ costLimit: Int) -> Nuke.ImageCache {
        return Nuke.ImageCache(costLimit: costLimit)
    }
}

extension Nuke.ImagePipeline.Cache {
    @_silgen_name("DBW_Cache_cachedImage_FF81D467_1")
    public func _dbw_cachedImage_FF81D467_1(_ _for: ImageRequest) -> Optional<ImageContainer> {
        return self.cachedImage(for: _for)
    }
}

extension Nuke.ImagePipeline.Cache {
    @_silgen_name("DBW_Cache_storeCachedImage_B14B9B8A_1")
    public func _dbw_storeCachedImage_B14B9B8A_1(_ arg0: ImageContainer, _ _for: ImageRequest) -> () {
        return self.storeCachedImage(arg0, for: _for)
    }
}

extension Nuke.ImagePipeline.Cache {
    @_silgen_name("DBW_Cache_removeCachedImage_6F11C1F7_1")
    public func _dbw_removeCachedImage_6F11C1F7_1(_ _for: ImageRequest) -> () {
        return self.removeCachedImage(for: _for)
    }
}

extension Nuke.ImagePipeline.Cache {
    @_silgen_name("DBW_Cache_containsCachedImage_5B644858_1")
    public func _dbw_containsCachedImage_5B644858_1(_ _for: ImageRequest) -> Bool {
        return self.containsCachedImage(for: _for)
    }
}

extension Nuke.ImagePipeline.Cache {
    @_silgen_name("DBW_Cache_removeAll_00813104_1")
    public func _dbw_removeAll_00813104_1() -> () {
        return self.removeAll()
    }
}

extension Nuke.ImagePipeline.Configuration {
    @_silgen_name("DBW_Configuration_init_6F4D1A10_1")
    public static func _dbw_init_6F4D1A10_1() -> Nuke.ImagePipeline.Configuration {
        return Nuke.ImagePipeline.Configuration()
    }
}

extension Nuke.ImagePipeline.Configuration {
    @_silgen_name("DBW_Configuration_withDataCache_75148080_2")
    public static func _dbw_withDataCache_75148080_2() -> ImagePipeline.Configuration {
        return Self.withDataCache()
    }
}

extension Nuke.ImagePipeline.Configuration {
    @_silgen_name("DBW_Configuration_withDataCache_75148080_1")
    public static func _dbw_withDataCache_75148080_1(_ name: String) -> ImagePipeline.Configuration {
        return Self.withDataCache(name: name)
    }
}
@_silgen_name("$s4Nuke13ImagePipelineC5image3forSo7UIImageC10Foundation3URLV_tYaKF_async")
public func PInvoke_image_6B90EC1B(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, _for: UnsafeRawPointer){
    // Read non-frozen parameters via .pointee (bitwise copy)
    // C# created copies using InitializeWithCopy (owns a proper reference)
    let _forValue = _for.assumingMemoryBound(to: Foundation.URL.self).pointee
    
    // selfInstance is safe - C# called Arc.Retain before invoking this method

    Task {
        do {
            let resultimage = try await Nuke.ImagePipeline.shared.image(
                for: _forValue
            )
            // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<UIKit.UIImage>.size,
                                alignment: MemoryLayout<UIKit.UIImage>.alignment)
                            _rawPtr.storeBytes(of: resultimage, as: UIKit.UIImage.self)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
            callback(_resultPtr, task)
        } catch {
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, task) }
        }
    }
}
@_silgen_name("$s4Nuke13ImagePipelineC5image3forSo7UIImageCAA0B7RequestV_tYaKF_async")
public func PInvoke_image_734A8A76(callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, _for: UnsafeRawPointer){
    // Read non-frozen parameters via .pointee (bitwise copy)
    // C# created copies using InitializeWithCopy (owns a proper reference)
    let _forValue = _for.assumingMemoryBound(to: Nuke.ImageRequest.self).pointee
    
    // selfInstance is safe - C# called Arc.Retain before invoking this method

    Task {
        do {
            let resultimage = try await Nuke.ImagePipeline.shared.image(
                for: _forValue
            )
            // Marshal complex type to pointer (C# will free via SBW_Free)
                        let _resultPtr: OpaquePointer
                        do {
                            let _rawPtr = UnsafeMutableRawPointer.allocate(
                                byteCount: MemoryLayout<UIKit.UIImage>.size,
                                alignment: MemoryLayout<UIKit.UIImage>.alignment)
                            _rawPtr.storeBytes(of: resultimage, as: UIKit.UIImage.self)
                            _resultPtr = OpaquePointer(_rawPtr)
                        }
            callback(_resultPtr, task)
        } catch {
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, task) }
        }
    }
}
@_silgen_name("$s4Nuke13ImagePipelineC4data3for10Foundation4DataV_So13NSURLResponseCSgtAA0B7RequestV_tYaKF_async")
public func PInvoke_data_20CF2ACE(callback: @escaping @convention(c) (Foundation.Data, Swift.Optional<Foundation.URLResponse>, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, _for: UnsafeRawPointer){
    // Read non-frozen parameters via .pointee (bitwise copy)
    // C# created copies using InitializeWithCopy (owns a proper reference)
    let _forValue = _for.assumingMemoryBound(to: Nuke.ImageRequest.self).pointee
    
    // selfInstance is safe - C# called Arc.Retain before invoking this method

    Task {
        do {
            let resultdata = try await Nuke.ImagePipeline.shared.data(
                for: _forValue
            )
            
            callback(resultdata.0, resultdata.1, task)
        } catch {
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, task) }
        }
    }
}
@_silgen_name("$s4Nuke13ImagePipelineC4data3for10Foundation4DataV_So13NSURLResponseCSgtAF3URLV_tYaKF_async")
public func PInvoke_data_1166B2DF(callback: @escaping @convention(c) (Foundation.Data, Swift.Optional<Foundation.URLResponse>, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, _for: UnsafeRawPointer){
    // Read non-frozen parameters via .pointee (bitwise copy)
    // C# created copies using InitializeWithCopy (owns a proper reference)
    let _forValue = _for.assumingMemoryBound(to: Foundation.URL.self).pointee
    
    // selfInstance is safe - C# called Arc.Retain before invoking this method

    Task {
        do {
            let resultdata = try await Nuke.ImagePipeline.shared.data(
                for: _forValue
            )
            
            callback(resultdata.0, resultdata.1, task)
        } catch {
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, task) }
        }
    }
}

extension Nuke.ImageProcessingOptions.Border {
    @_silgen_name("DBW_Border_init_46D2AA2C_2")
    public static func _dbw_init_46D2AA2C_2(_ color: UIColor) -> Nuke.ImageProcessingOptions.Border {
        return Nuke.ImageProcessingOptions.Border(color: color)
    }
}

extension Nuke.ImageProcessingOptions.Border {
    @_silgen_name("DBW_Border_init_46D2AA2C_1")
    public static func _dbw_init_46D2AA2C_1(_ color: UIColor, _ width: CGFloat) -> Nuke.ImageProcessingOptions.Border {
        return Nuke.ImageProcessingOptions.Border(color: color, width: width)
    }
}

extension Nuke.DataCache {
    @_silgen_name("DBW_DataCache_init_AB279AAF_1")
    public static func _dbw_init_AB279AAF_1(_ name: String) throws -> Nuke.DataCache {
        return try Nuke.DataCache(name: name)
    }
}

extension Nuke.DataCache {
    @_silgen_name("DBW_DataCache_init_CF2F43D1_1")
    public static func _dbw_init_CF2F43D1_1(_ path: URL) throws -> Nuke.DataCache {
        return try Nuke.DataCache(path: path)
    }
}

extension Nuke.ImageDecodingContext {
    @_silgen_name("DBW_ImageDecodingContext_init_6CA396CF_3")
    public static func _dbw_init_6CA396CF_3(_ request: ImageRequest, _ data: Data) -> Nuke.ImageDecodingContext {
        return Nuke.ImageDecodingContext(request: request, data: data)
    }
}

extension Nuke.ImageDecodingContext {
    @_silgen_name("DBW_ImageDecodingContext_init_6CA396CF_2")
    public static func _dbw_init_6CA396CF_2(_ request: ImageRequest, _ data: Data, _ isCompleted: Bool) -> Nuke.ImageDecodingContext {
        return Nuke.ImageDecodingContext(request: request, data: data, isCompleted: isCompleted)
    }
}

extension Nuke.ImageDecodingContext {
    @_silgen_name("DBW_ImageDecodingContext_init_6CA396CF_1")
    public static func _dbw_init_6CA396CF_1(_ request: ImageRequest, _ data: Data, _ isCompleted: Bool, _ urlResponse: Optional<URLResponse>) -> Nuke.ImageDecodingContext {
        return Nuke.ImageDecodingContext(request: request, data: data, isCompleted: isCompleted, urlResponse: urlResponse)
    }
}

extension Nuke.ImageProcessors.RoundedCorners {
    @_silgen_name("DBW_RoundedCorners_init_DD16F922_2")
    public static func _dbw_init_DD16F922_2(_ radius: CGFloat) -> Nuke.ImageProcessors.RoundedCorners {
        return Nuke.ImageProcessors.RoundedCorners(radius: radius)
    }
}

extension Nuke.ImageProcessors.RoundedCorners {
    @_silgen_name("DBW_RoundedCorners_init_DD16F922_1")
    public static func _dbw_init_DD16F922_1(_ radius: CGFloat, _ unit: ImageProcessingOptions.Unit) -> Nuke.ImageProcessors.RoundedCorners {
        return Nuke.ImageProcessors.RoundedCorners(radius: radius, unit: unit)
    }
}

extension Nuke.ImageProcessors.Resize {
    @_silgen_name("DBW_Resize_init_51CAC367_4")
    public static func _dbw_init_51CAC367_4(_ size: CGSize) -> Nuke.ImageProcessors.Resize {
        return Nuke.ImageProcessors.Resize(size: size)
    }
}

extension Nuke.ImageProcessors.Resize {
    @_silgen_name("DBW_Resize_init_51CAC367_3")
    public static func _dbw_init_51CAC367_3(_ size: CGSize, _ unit: ImageProcessingOptions.Unit) -> Nuke.ImageProcessors.Resize {
        return Nuke.ImageProcessors.Resize(size: size, unit: unit)
    }
}

extension Nuke.ImageProcessors.Resize {
    @_silgen_name("DBW_Resize_init_51CAC367_1")
    public static func _dbw_init_51CAC367_1(_ size: CGSize, _ unit: ImageProcessingOptions.Unit, _ contentMode: ImageProcessingOptions.ContentMode, _ crop: Bool) -> Nuke.ImageProcessors.Resize {
        return Nuke.ImageProcessors.Resize(size: size, unit: unit, contentMode: contentMode, crop: crop)
    }
}

extension Nuke.ImageProcessors.Resize {
    @_silgen_name("DBW_Resize_init_62C284BA_2")
    public static func _dbw_init_62C284BA_2(_ width: CGFloat) -> Nuke.ImageProcessors.Resize {
        return Nuke.ImageProcessors.Resize(width: width)
    }
}

extension Nuke.ImageProcessors.Resize {
    @_silgen_name("DBW_Resize_init_62C284BA_1")
    public static func _dbw_init_62C284BA_1(_ width: CGFloat, _ unit: ImageProcessingOptions.Unit) -> Nuke.ImageProcessors.Resize {
        return Nuke.ImageProcessors.Resize(width: width, unit: unit)
    }
}

extension Nuke.ImageProcessors.GaussianBlur {
    @_silgen_name("DBW_GaussianBlur_init_05E0C1C6_1")
    public static func _dbw_init_05E0C1C6_1() -> Nuke.ImageProcessors.GaussianBlur {
        return Nuke.ImageProcessors.GaussianBlur()
    }
}

extension Nuke.ImageProcessors.Circle {
    @_silgen_name("DBW_Circle_init_36B9D008_1")
    public static func _dbw_init_36B9D008_1() -> Nuke.ImageProcessors.Circle {
        return Nuke.ImageProcessors.Circle()
    }
}

extension Nuke.ImageRequest.ThumbnailOptions {
    @_silgen_name("DBW_ThumbnailOptions_init_3D39AF54_1")
    public static func _dbw_init_3D39AF54_1(_ size: CGSize, _ unit: ImageProcessingOptions.Unit) -> Nuke.ImageRequest.ThumbnailOptions {
        return Nuke.ImageRequest.ThumbnailOptions(size: size, unit: unit)
    }
}

extension Nuke.ImageDecoders.Empty {
    @_silgen_name("DBW_Empty_init_B1B9E014_2")
    public static func _dbw_init_B1B9E014_2() -> Nuke.ImageDecoders.Empty {
        return Nuke.ImageDecoders.Empty()
    }
}

extension Nuke.ImageDecoders.Empty {
    @_silgen_name("DBW_Empty_init_B1B9E014_1")
    public static func _dbw_init_B1B9E014_1(_ assetType: Optional<AssetType>) -> Nuke.ImageDecoders.Empty {
        return Nuke.ImageDecoders.Empty(assetType: assetType)
    }
}
@_silgen_name("ImageTask_progress_AsyncStream")
public func ImageTask_progress_AsyncStream(
    _ self: ImageTask, elementCallback: @escaping @convention(c) (UnsafeRawPointer, Int64) -> Bool,
    completionCallback: @escaping @convention(c) (Int64) -> Void,
    context: Int64
) {
    Task {
        for await element in self.progress {
            let shouldContinue = withUnsafePointer(to: element) { ptr in
                elementCallback(UnsafeRawPointer(ptr), context)
            }
            if !shouldContinue { break }
        }
        completionCallback(context)
    }
}
@_silgen_name("ImageTask_previews_AsyncStream")
public func ImageTask_previews_AsyncStream(
    _ self: ImageTask, elementCallback: @escaping @convention(c) (UnsafeRawPointer, Int64) -> Bool,
    completionCallback: @escaping @convention(c) (Int64) -> Void,
    context: Int64
) {
    Task {
        for await element in self.previews {
            let shouldContinue = withUnsafePointer(to: element) { ptr in
                elementCallback(UnsafeRawPointer(ptr), context)
            }
            if !shouldContinue { break }
        }
        completionCallback(context)
    }
}
@_silgen_name("ImageTask_events_AsyncStream")
public func ImageTask_events_AsyncStream(
    _ self: ImageTask, elementCallback: @escaping @convention(c) (UnsafeRawPointer, Int64) -> Bool,
    completionCallback: @escaping @convention(c) (Int64) -> Void,
    context: Int64
) {
    Task {
        for await element in self.events {
            let shouldContinue = withUnsafePointer(to: element) { ptr in
                elementCallback(UnsafeRawPointer(ptr), context)
            }
            if !shouldContinue { break }
        }
        completionCallback(context)
    }
}

extension Nuke.ImageEncoders.ImageIO {
    @_silgen_name("DBW_ImageIO_init_5E8C4DC8_1")
    public static func _dbw_init_5E8C4DC8_1(_ type: AssetType) -> Nuke.ImageEncoders.ImageIO {
        return Nuke.ImageEncoders.ImageIO(type: type)
    }
}

extension Nuke.ImageEncoders.Default {
    @_silgen_name("DBW_Default_init_42CADF88_1")
    public static func _dbw_init_42CADF88_1() -> Nuke.ImageEncoders.Default {
        return Nuke.ImageEncoders.Default()
    }
}

extension Nuke.ImagePrefetcher {
    @_silgen_name("DBW_ImagePrefetcher_init_9862FED5_3")
    public static func _dbw_init_9862FED5_3() -> Nuke.ImagePrefetcher {
        return Nuke.ImagePrefetcher()
    }
}

extension Nuke.ImagePrefetcher {
    @_silgen_name("DBW_ImagePrefetcher_init_9862FED5_2")
    public static func _dbw_init_9862FED5_2(_ pipeline: ImagePipeline) -> Nuke.ImagePrefetcher {
        return Nuke.ImagePrefetcher(pipeline: pipeline)
    }
}

extension Nuke.ImagePrefetcher {
    @_silgen_name("DBW_ImagePrefetcher_init_9862FED5_1")
    public static func _dbw_init_9862FED5_1(_ pipeline: ImagePipeline, _ destination: ImagePrefetcher.Destination) -> Nuke.ImagePrefetcher {
        return Nuke.ImagePrefetcher(pipeline: pipeline, destination: destination)
    }
}
