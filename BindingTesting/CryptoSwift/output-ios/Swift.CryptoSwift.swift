import CryptoSwift
import Foundation

@frozen
public struct SBW_Utf8Slice {
    public var ptr: UnsafeMutablePointer<UInt8>
    public var len: Int
}
// Static empty buffer for empty string slices (required for @convention(c) compatibility)
fileprivate var _sbw_emptyBuffer: UInt8 = 0
@_silgen_name("SBW_Free_CryptoSwift")
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
// Vtable for Cryptor protocol - stores function pointers to C# implementations
fileprivate struct Cryptor_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_seek_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
}

private var _cryptor_vtable = Cryptor_vtable()

// EveryProtocol conformance to Cryptor
extension EveryProtocol: CryptoSwift.Cryptor {
    public func seek(to: Swift.Int) throws {
            var selfProto: CryptoSwift.Cryptor = self
            var toCopy = to
                _cryptor_vtable.func_seek_0!(
                _cryptor_vtable.csVTHandle, &selfProto, &toCopy)
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetCryptor_vtable")
public func setCryptor_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<Cryptor_vtable> = uvt.assumingMemoryBound(to: Cryptor_vtable.self)
    _cryptor_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to Cryptor.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_Cryptor_WitnessTable")
public func getEveryProtocolCryptorWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.Cryptor = instance
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
// Vtable for BlockMode protocol - stores function pointers to C# implementations
fileprivate struct BlockMode_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_options_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_customBlockSize_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_worker_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _blockMode_vtable = BlockMode_vtable()

// EveryProtocol conformance to BlockMode
extension EveryProtocol: CryptoSwift.BlockMode {
    public var options: CryptoSwift.BlockModeOption {
        get {
            var selfProto: CryptoSwift.BlockMode = self
            let resultPtr = _blockMode_vtable.func_options_get!(
                _blockMode_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: CryptoSwift.BlockModeOption.self).pointee
        }
    }
    
    public var customBlockSize: (Swift.Int)? {
        get {
            var selfProto: CryptoSwift.BlockMode = self
            let resultPtr = _blockMode_vtable.func_customBlockSize_get!(
                _blockMode_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: (Swift.Int)?.self).pointee
        }
    }
    
    public func worker(blockSize: Swift.Int, cipherOperation: (Swift.ArraySlice<Swift.UInt8>) -> (Swift.Array<Swift.UInt8>)?, encryptionOperation: (Swift.ArraySlice<Swift.UInt8>) -> (Swift.Array<Swift.UInt8>)?) throws -> any CryptoSwift.CipherModeWorker {
            var selfProto: CryptoSwift.BlockMode = self
            var blockSizeCopy = blockSize
                var cipherOperationCopy = cipherOperation
                var encryptionOperationCopy = encryptionOperation
                let resultPtr = _blockMode_vtable.func_worker_0!(
                _blockMode_vtable.csVTHandle, &selfProto, &blockSizeCopy, &cipherOperationCopy, &encryptionOperationCopy)
            return resultPtr.assumingMemoryBound(to: (any CryptoSwift.CipherModeWorker).self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetBlockMode_vtable")
public func setBlockMode_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<BlockMode_vtable> = uvt.assumingMemoryBound(to: BlockMode_vtable.self)
    _blockMode_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to BlockMode.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_BlockMode_WitnessTable")
public func getEveryProtocolBlockModeWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.BlockMode = instance
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
// Vtable for Cipher protocol - stores function pointers to C# implementations
fileprivate struct Cipher_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_keySize_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_encrypt_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_encrypt_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_decrypt_2: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_decrypt_3: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _cipher_vtable = Cipher_vtable()

// EveryProtocol conformance to Cipher
extension EveryProtocol: CryptoSwift.Cipher {
    public var keySize: Swift.Int {
        get {
            var selfProto: CryptoSwift.Cipher = self
            let resultPtr = _cipher_vtable.func_keySize_get!(
                _cipher_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.Int.self).pointee
        }
    }
    
    public func encrypt(_ arg0: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.Cipher = self
            var arg0Copy = arg0
                let resultPtr = _cipher_vtable.func_encrypt_0!(
                _cipher_vtable.csVTHandle, &selfProto, &arg0Copy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
    public func decrypt(_ arg0: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.Cipher = self
            var arg0Copy = arg0
                let resultPtr = _cipher_vtable.func_decrypt_2!(
                _cipher_vtable.csVTHandle, &selfProto, &arg0Copy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetCipher_vtable")
public func setCipher_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<Cipher_vtable> = uvt.assumingMemoryBound(to: Cipher_vtable.self)
    _cipher_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to Cipher.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_Cipher_WitnessTable")
public func getEveryProtocolCipherWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.Cipher = instance
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
// Witness dispatch accessors for Cipher
@_silgen_name("SBW_Cipher_get_keySize_0")
public func SBW_Cipher_get_keySize_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any CryptoSwift.Cipher).self)
    let result = existential.keySize
    let ptr = UnsafeMutablePointer<Int>.allocate(capacity: 1)
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}
@_silgen_name("SBW_Cipher_free_get_keySize_0")
public func SBW_Cipher_free_get_keySize_0(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Int.self).deinitialize(count: 1)
    ptr.deallocate()
}

// Vtable for Updatable protocol - stores function pointers to C# implementations
fileprivate struct Updatable_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_update_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_update_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_update_2: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_update_3: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_finish_4: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_finish_5: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_finish_6: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_finish_7: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_finish_8: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
    var func_finish_9: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
}

private var _updatable_vtable = Updatable_vtable()

// EveryProtocol conformance to Updatable
extension EveryProtocol: CryptoSwift.Updatable {
    public func update(withBytes: Swift.ArraySlice<Swift.UInt8>, isLast: Swift.Bool) throws -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.Updatable = self
            var withBytesCopy = withBytes
                var isLastCopy = isLast
                let resultPtr = _updatable_vtable.func_update_0!(
                _updatable_vtable.csVTHandle, &selfProto, &withBytesCopy, &isLastCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
    public func update(withBytes: Swift.ArraySlice<Swift.UInt8>, isLast: Swift.Bool, output: (Swift.Array<Swift.UInt8>) -> Void) throws {
            var selfProto: CryptoSwift.Updatable = self
            var withBytesCopy = withBytes
                var isLastCopy = isLast
                var outputCopy = output
                _updatable_vtable.func_update_1!(
                _updatable_vtable.csVTHandle, &selfProto, &withBytesCopy, &isLastCopy, &outputCopy)
    }
    
    public func finish(withBytes: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.Updatable = self
            var withBytesCopy = withBytes
                let resultPtr = _updatable_vtable.func_finish_4!(
                _updatable_vtable.csVTHandle, &selfProto, &withBytesCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
    public func finish() throws -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.Updatable = self
            let resultPtr = _updatable_vtable.func_finish_6!(
                _updatable_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
    public func finish(withBytes: Swift.ArraySlice<Swift.UInt8>, output: (Swift.Array<Swift.UInt8>) -> Void) throws {
            var selfProto: CryptoSwift.Updatable = self
            var withBytesCopy = withBytes
                var outputCopy = output
                _updatable_vtable.func_finish_7!(
                _updatable_vtable.csVTHandle, &selfProto, &withBytesCopy, &outputCopy)
    }
    
    public func finish(output: (Swift.Array<Swift.UInt8>) -> Void) throws {
            var selfProto: CryptoSwift.Updatable = self
            var outputCopy = output
                _updatable_vtable.func_finish_9!(
                _updatable_vtable.csVTHandle, &selfProto, &outputCopy)
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetUpdatable_vtable")
public func setUpdatable_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<Updatable_vtable> = uvt.assumingMemoryBound(to: Updatable_vtable.self)
    _updatable_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to Updatable.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_Updatable_WitnessTable")
public func getEveryProtocolUpdatableWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.Updatable = instance
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
// Vtable for Cryptors protocol - stores function pointers to C# implementations
fileprivate struct Cryptors_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_makeEncryptor_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_makeDecryptor_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _cryptors_vtable = Cryptors_vtable()

// EveryProtocol conformance to Cryptors
extension EveryProtocol: CryptoSwift.Cryptors {
    public func makeEncryptor() throws -> Any {
            var selfProto: CryptoSwift.Cryptors = self
            let resultPtr = _cryptors_vtable.func_makeEncryptor_0!(
                _cryptors_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Any.self).pointee
    }
    
    public func makeDecryptor() throws -> Any {
            var selfProto: CryptoSwift.Cryptors = self
            let resultPtr = _cryptors_vtable.func_makeDecryptor_1!(
                _cryptors_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Any.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetCryptors_vtable")
public func setCryptors_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<Cryptors_vtable> = uvt.assumingMemoryBound(to: Cryptors_vtable.self)
    _cryptors_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to Cryptors.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_Cryptors_WitnessTable")
public func getEveryProtocolCryptorsWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.Cryptors = instance
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
// Vtable for CipherModeWorker protocol - stores function pointers to C# implementations
fileprivate struct CipherModeWorker_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_cipherOperation_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_additionalBufferSize_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_encrypt_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_decrypt_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _cipherModeWorker_vtable = CipherModeWorker_vtable()

// EveryProtocol conformance to CipherModeWorker
extension EveryProtocol: CryptoSwift.CipherModeWorker {
    public var cipherOperation: (Swift.ArraySlice<Swift.UInt8>) -> (Swift.Array<Swift.UInt8>)? {
        get {
            var selfProto: CryptoSwift.CipherModeWorker = self
            let resultPtr = _cipherModeWorker_vtable.func_cipherOperation_get!(
                _cipherModeWorker_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: ((Swift.ArraySlice<Swift.UInt8>) -> (Swift.Array<Swift.UInt8>)?).self).pointee
        }
    }
    
    public var additionalBufferSize: Swift.Int {
        get {
            var selfProto: CryptoSwift.CipherModeWorker = self
            let resultPtr = _cipherModeWorker_vtable.func_additionalBufferSize_get!(
                _cipherModeWorker_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.Int.self).pointee
        }
    }
    
    public func encrypt(block: Swift.ArraySlice<Swift.UInt8>) -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.CipherModeWorker = self
            var blockCopy = block
                let resultPtr = _cipherModeWorker_vtable.func_encrypt_0!(
                _cipherModeWorker_vtable.csVTHandle, &selfProto, &blockCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
    public func decrypt(block: Swift.ArraySlice<Swift.UInt8>) -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.CipherModeWorker = self
            var blockCopy = block
                let resultPtr = _cipherModeWorker_vtable.func_decrypt_1!(
                _cipherModeWorker_vtable.csVTHandle, &selfProto, &blockCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetCipherModeWorker_vtable")
public func setCipherModeWorker_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<CipherModeWorker_vtable> = uvt.assumingMemoryBound(to: CipherModeWorker_vtable.self)
    _cipherModeWorker_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to CipherModeWorker.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_CipherModeWorker_WitnessTable")
public func getEveryProtocolCipherModeWorkerWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.CipherModeWorker = instance
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
// Witness dispatch accessors for CipherModeWorker
@_silgen_name("SBW_CipherModeWorker_get_additionalBufferSize_0")
public func SBW_CipherModeWorker_get_additionalBufferSize_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any CryptoSwift.CipherModeWorker).self)
    let result = existential.additionalBufferSize
    let ptr = UnsafeMutablePointer<Int>.allocate(capacity: 1)
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}
@_silgen_name("SBW_CipherModeWorker_free_get_additionalBufferSize_0")
public func SBW_CipherModeWorker_free_get_additionalBufferSize_0(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Int.self).deinitialize(count: 1)
    ptr.deallocate()
}

// Vtable for BlockModeWorker protocol - stores function pointers to C# implementations
fileprivate struct BlockModeWorker_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_blockSize_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _blockModeWorker_vtable = BlockModeWorker_vtable()

// EveryProtocol conformance to BlockModeWorker
extension EveryProtocol: CryptoSwift.BlockModeWorker {
    public var blockSize: Swift.Int {
        get {
            var selfProto: CryptoSwift.BlockModeWorker = self
            let resultPtr = _blockModeWorker_vtable.func_blockSize_get!(
                _blockModeWorker_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.Int.self).pointee
        }
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetBlockModeWorker_vtable")
public func setBlockModeWorker_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<BlockModeWorker_vtable> = uvt.assumingMemoryBound(to: BlockModeWorker_vtable.self)
    _blockModeWorker_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to BlockModeWorker.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_BlockModeWorker_WitnessTable")
public func getEveryProtocolBlockModeWorkerWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.BlockModeWorker = instance
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
// Witness dispatch accessors for BlockModeWorker
@_silgen_name("SBW_BlockModeWorker_get_blockSize_0")
public func SBW_BlockModeWorker_get_blockSize_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any CryptoSwift.BlockModeWorker).self)
    let result = existential.blockSize
    let ptr = UnsafeMutablePointer<Int>.allocate(capacity: 1)
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}
@_silgen_name("SBW_BlockModeWorker_free_get_blockSize_0")
public func SBW_BlockModeWorker_free_get_blockSize_0(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Int.self).deinitialize(count: 1)
    ptr.deallocate()
}

// Vtable for SeekableModeWorker protocol - stores function pointers to C# implementations
fileprivate struct SeekableModeWorker_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_seek_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?
}

private var _seekableModeWorker_vtable = SeekableModeWorker_vtable()

// EveryProtocol conformance to SeekableModeWorker
extension EveryProtocol: CryptoSwift.SeekableModeWorker {
}

// Called by C# to register the protocol vtable
@_silgen_name("SetSeekableModeWorker_vtable")
public func setSeekableModeWorker_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<SeekableModeWorker_vtable> = uvt.assumingMemoryBound(to: SeekableModeWorker_vtable.self)
    _seekableModeWorker_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to SeekableModeWorker.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_SeekableModeWorker_WitnessTable")
public func getEveryProtocolSeekableModeWorkerWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.SeekableModeWorker = instance
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
// Vtable for FinalizingEncryptModeWorker protocol - stores function pointers to C# implementations
fileprivate struct FinalizingEncryptModeWorker_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_finalize_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _finalizingEncryptModeWorker_vtable = FinalizingEncryptModeWorker_vtable()

// EveryProtocol conformance to FinalizingEncryptModeWorker
extension EveryProtocol: CryptoSwift.FinalizingEncryptModeWorker {
    public func finalize(encrypt: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.ArraySlice<Swift.UInt8> {
            var selfProto: CryptoSwift.FinalizingEncryptModeWorker = self
            var encryptCopy = encrypt
                let resultPtr = _finalizingEncryptModeWorker_vtable.func_finalize_0!(
                _finalizingEncryptModeWorker_vtable.csVTHandle, &selfProto, &encryptCopy)
            return resultPtr.assumingMemoryBound(to: Swift.ArraySlice<Swift.UInt8>.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetFinalizingEncryptModeWorker_vtable")
public func setFinalizingEncryptModeWorker_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<FinalizingEncryptModeWorker_vtable> = uvt.assumingMemoryBound(to: FinalizingEncryptModeWorker_vtable.self)
    _finalizingEncryptModeWorker_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to FinalizingEncryptModeWorker.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_FinalizingEncryptModeWorker_WitnessTable")
public func getEveryProtocolFinalizingEncryptModeWorkerWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.FinalizingEncryptModeWorker = instance
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
// Vtable for FinalizingDecryptModeWorker protocol - stores function pointers to C# implementations
fileprivate struct FinalizingDecryptModeWorker_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_willDecryptLast_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_didDecryptLast_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_finalize_2: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _finalizingDecryptModeWorker_vtable = FinalizingDecryptModeWorker_vtable()

// EveryProtocol conformance to FinalizingDecryptModeWorker
extension EveryProtocol: CryptoSwift.FinalizingDecryptModeWorker {
    public func willDecryptLast(bytes: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.ArraySlice<Swift.UInt8> {
            var selfProto: CryptoSwift.FinalizingDecryptModeWorker = self
            var bytesCopy = bytes
                let resultPtr = _finalizingDecryptModeWorker_vtable.func_willDecryptLast_0!(
                _finalizingDecryptModeWorker_vtable.csVTHandle, &selfProto, &bytesCopy)
            return resultPtr.assumingMemoryBound(to: Swift.ArraySlice<Swift.UInt8>.self).pointee
    }
    
    public func didDecryptLast(bytes: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.ArraySlice<Swift.UInt8> {
            var selfProto: CryptoSwift.FinalizingDecryptModeWorker = self
            var bytesCopy = bytes
                let resultPtr = _finalizingDecryptModeWorker_vtable.func_didDecryptLast_1!(
                _finalizingDecryptModeWorker_vtable.csVTHandle, &selfProto, &bytesCopy)
            return resultPtr.assumingMemoryBound(to: Swift.ArraySlice<Swift.UInt8>.self).pointee
    }
    
    public func finalize(decrypt: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.ArraySlice<Swift.UInt8> {
            var selfProto: CryptoSwift.FinalizingDecryptModeWorker = self
            var decryptCopy = decrypt
                let resultPtr = _finalizingDecryptModeWorker_vtable.func_finalize_2!(
                _finalizingDecryptModeWorker_vtable.csVTHandle, &selfProto, &decryptCopy)
            return resultPtr.assumingMemoryBound(to: Swift.ArraySlice<Swift.UInt8>.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetFinalizingDecryptModeWorker_vtable")
public func setFinalizingDecryptModeWorker_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<FinalizingDecryptModeWorker_vtable> = uvt.assumingMemoryBound(to: FinalizingDecryptModeWorker_vtable.self)
    _finalizingDecryptModeWorker_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to FinalizingDecryptModeWorker.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_FinalizingDecryptModeWorker_WitnessTable")
public func getEveryProtocolFinalizingDecryptModeWorkerWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.FinalizingDecryptModeWorker = instance
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
// Vtable for Signature protocol - stores function pointers to C# implementations
fileprivate struct Signature_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_keySize_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_sign_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_sign_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_verify_2: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_verify_3: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _signature_vtable = Signature_vtable()

// EveryProtocol conformance to Signature
extension EveryProtocol: CryptoSwift.Signature {
    public func sign(_ arg0: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.Signature = self
            var arg0Copy = arg0
                let resultPtr = _signature_vtable.func_sign_0!(
                _signature_vtable.csVTHandle, &selfProto, &arg0Copy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
    public func verify(signature: Swift.ArraySlice<Swift.UInt8>, for forValue: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.Bool {
            var selfProto: CryptoSwift.Signature = self
            var signatureCopy = signature
                var forValueCopy = forValue
                let resultPtr = _signature_vtable.func_verify_2!(
                _signature_vtable.csVTHandle, &selfProto, &signatureCopy, &forValueCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Bool.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetSignature_vtable")
public func setSignature_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<Signature_vtable> = uvt.assumingMemoryBound(to: Signature_vtable.self)
    _signature_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to Signature.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_Signature_WitnessTable")
public func getEveryProtocolSignatureWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.Signature = instance
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
// Witness dispatch accessors for Signature
@_silgen_name("SBW_Signature_get_keySize_0")
public func SBW_Signature_get_keySize_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let existential = containerPtr.load(as: (any CryptoSwift.Signature).self)
    let result = existential.keySize
    let ptr = UnsafeMutablePointer<Int>.allocate(capacity: 1)
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}
@_silgen_name("SBW_Signature_free_get_keySize_0")
public func SBW_Signature_free_get_keySize_0(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Int.self).deinitialize(count: 1)
    ptr.deallocate()
}

// Vtable for PaddingProtocol protocol - stores function pointers to C# implementations
fileprivate struct PaddingProtocol_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_add_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_remove_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _paddingProtocol_vtable = PaddingProtocol_vtable()

// EveryProtocol conformance to PaddingProtocol
extension EveryProtocol: CryptoSwift.PaddingProtocol {
    public func add(to: Swift.Array<Swift.UInt8>, blockSize: Swift.Int) -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.PaddingProtocol = self
            var toCopy = to
                var blockSizeCopy = blockSize
                let resultPtr = _paddingProtocol_vtable.func_add_0!(
                _paddingProtocol_vtable.csVTHandle, &selfProto, &toCopy, &blockSizeCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
    public func remove(from: Swift.Array<Swift.UInt8>, blockSize: (Swift.Int)?) -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.PaddingProtocol = self
            var fromCopy = from
                var blockSizeCopy = blockSize
                let resultPtr = _paddingProtocol_vtable.func_remove_1!(
                _paddingProtocol_vtable.csVTHandle, &selfProto, &fromCopy, &blockSizeCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetPaddingProtocol_vtable")
public func setPaddingProtocol_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<PaddingProtocol_vtable> = uvt.assumingMemoryBound(to: PaddingProtocol_vtable.self)
    _paddingProtocol_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to PaddingProtocol.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_PaddingProtocol_WitnessTable")
public func getEveryProtocolPaddingProtocolWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.PaddingProtocol = instance
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
// Vtable for Authenticator protocol - stores function pointers to C# implementations
fileprivate struct Authenticator_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_authenticate_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _authenticator_vtable = Authenticator_vtable()

// EveryProtocol conformance to Authenticator
extension EveryProtocol: CryptoSwift.Authenticator {
    public func authenticate(_ arg0: Swift.Array<Swift.UInt8>) throws -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.Authenticator = self
            var arg0Copy = arg0
                let resultPtr = _authenticator_vtable.func_authenticate_0!(
                _authenticator_vtable.csVTHandle, &selfProto, &arg0Copy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetAuthenticator_vtable")
public func setAuthenticator_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<Authenticator_vtable> = uvt.assumingMemoryBound(to: Authenticator_vtable.self)
    _authenticator_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to Authenticator.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_Authenticator_WitnessTable")
public func getEveryProtocolAuthenticatorWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.Authenticator = instance
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
// Vtable for Collection protocol - stores function pointers to C# implementations
fileprivate struct Collection_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_toUInt32Array_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_toUInt64Array_1: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_batched_2: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _collection_vtable = Collection_vtable()

// EveryProtocol conformance to Collection
extension EveryProtocol: CryptoSwift.Collection {
    public func toUInt32Array() -> Swift.Array<Swift.UInt32> {
            var selfProto: CryptoSwift.Collection = self
            let resultPtr = _collection_vtable.func_toUInt32Array_0!(
                _collection_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt32>.self).pointee
    }
    
    public func toUInt64Array() -> Swift.Array<Swift.UInt64> {
            var selfProto: CryptoSwift.Collection = self
            let resultPtr = _collection_vtable.func_toUInt64Array_1!(
                _collection_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt64>.self).pointee
    }
    
    public func batched(by: Swift.Int) -> CryptoSwift.BatchedCollection<Any> {
            var selfProto: CryptoSwift.Collection = self
            var byCopy = by
                let resultPtr = _collection_vtable.func_batched_2!(
                _collection_vtable.csVTHandle, &selfProto, &byCopy)
            return resultPtr.assumingMemoryBound(to: CryptoSwift.BatchedCollection<Any>.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetCollection_vtable")
public func setCollection_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<Collection_vtable> = uvt.assumingMemoryBound(to: Collection_vtable.self)
    _collection_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to Collection.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_Collection_WitnessTable")
public func getEveryProtocolCollectionWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.Collection = instance
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
// Vtable for FixedWidthInteger protocol - stores function pointers to C# implementations
fileprivate struct FixedWidthInteger_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_bytes_0: (@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _fixedWidthInteger_vtable = FixedWidthInteger_vtable()

// EveryProtocol conformance to FixedWidthInteger
extension EveryProtocol: CryptoSwift.FixedWidthInteger {
    public func bytes(totalBytes: Swift.Int) -> Swift.Array<Swift.UInt8> {
            var selfProto: CryptoSwift.FixedWidthInteger = self
            var totalBytesCopy = totalBytes
                let resultPtr = _fixedWidthInteger_vtable.func_bytes_0!(
                _fixedWidthInteger_vtable.csVTHandle, &selfProto, &totalBytesCopy)
            return resultPtr.assumingMemoryBound(to: Swift.Array<Swift.UInt8>.self).pointee
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetFixedWidthInteger_vtable")
public func setFixedWidthInteger_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<FixedWidthInteger_vtable> = uvt.assumingMemoryBound(to: FixedWidthInteger_vtable.self)
    _fixedWidthInteger_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to FixedWidthInteger.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_FixedWidthInteger_WitnessTable")
public func getEveryProtocolFixedWidthIntegerWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any CryptoSwift.FixedWidthInteger = instance
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

extension CryptoSwift.RSA {
    @_silgen_name("SBW_RSA_sign_239FB4FB")
    public func _sbw_sign_239FB4FB(_ arg0: Array<UInt8>) throws -> Array<UInt8> {
        return try self.sign(Swift.ArraySlice(arg0))
    }
}

extension CryptoSwift.RSA {
    @_silgen_name("SBW_RSA_verify_7BC52380")
    public func _sbw_verify_7BC52380(_ signature: Array<UInt8>, _ _for: Array<UInt8>) throws -> Bool {
        return try self.verify(signature: Swift.ArraySlice(signature), for: Swift.ArraySlice(_for))
    }
}

extension CryptoSwift.RSA {
    @_silgen_name("SBW_RSA_encrypt_7FB8985E")
    public func _sbw_encrypt_7FB8985E(_ arg0: Array<UInt8>) throws -> Array<UInt8> {
        return try self.encrypt(Swift.ArraySlice(arg0))
    }
}

extension CryptoSwift.RSA {
    @_silgen_name("SBW_RSA_decrypt_2180AEB6")
    public func _sbw_decrypt_2180AEB6(_ arg0: Array<UInt8>) throws -> Array<UInt8> {
        return try self.decrypt(Swift.ArraySlice(arg0))
    }
}

extension CryptoSwift.SHA2 {
    @_silgen_name("SBW_SHA2_update_0F871E93")
    public func _sbw_update_0F871E93(_ withBytes: Array<UInt8>, _ isLast: Bool) throws -> Array<UInt8> {
        return try self.update(withBytes: Swift.ArraySlice(withBytes), isLast: isLast)
    }
}

extension CryptoSwift.SHA1 {
    @_silgen_name("SBW_SHA1_update_5174E2D0")
    public func _sbw_update_5174E2D0(_ withBytes: Array<UInt8>, _ isLast: Bool) throws -> Array<UInt8> {
        return try self.update(withBytes: Swift.ArraySlice(withBytes), isLast: isLast)
    }
}

extension CryptoSwift.MD5 {
    @_silgen_name("SBW_MD5_update_1E4EF80A")
    public func _sbw_update_1E4EF80A(_ withBytes: Array<UInt8>, _ isLast: Bool) throws -> Array<UInt8> {
        return try self.update(withBytes: Swift.ArraySlice(withBytes), isLast: isLast)
    }
}

extension CryptoSwift.AES {
    @_silgen_name("SBW_AES_encrypt_AEC9CA63")
    public func _sbw_encrypt_AEC9CA63(_ block: Array<UInt8>) -> Optional<Array<UInt8>> {
        return self.encrypt(block: Swift.ArraySlice(block))
    }
}

extension CryptoSwift.AES {
    @_silgen_name("SBW_AES_decrypt_F50B23AB")
    public func _sbw_decrypt_F50B23AB(_ block: Array<UInt8>) -> Optional<Array<UInt8>> {
        return self.decrypt(block: Swift.ArraySlice(block))
    }
}

extension CryptoSwift.ChaCha20 {
    @_silgen_name("SBW_ChaCha20_encrypt_26BBF911")
    public func _sbw_encrypt_26BBF911(_ arg0: Array<UInt8>) throws -> Array<UInt8> {
        return try self.encrypt(Swift.ArraySlice(arg0))
    }
}

extension CryptoSwift.ChaCha20 {
    @_silgen_name("SBW_ChaCha20_decrypt_20F04D3D")
    public func _sbw_decrypt_20F04D3D(_ arg0: Array<UInt8>) throws -> Array<UInt8> {
        return try self.decrypt(Swift.ArraySlice(arg0))
    }
}

extension CryptoSwift.Rabbit {
    @_silgen_name("SBW_Rabbit_encrypt_82911D05")
    public func _sbw_encrypt_82911D05(_ arg0: Array<UInt8>) throws -> Array<UInt8> {
        return try self.encrypt(Swift.ArraySlice(arg0))
    }
}

extension CryptoSwift.Rabbit {
    @_silgen_name("SBW_Rabbit_decrypt_3A6508F1")
    public func _sbw_decrypt_3A6508F1(_ arg0: Array<UInt8>) throws -> Array<UInt8> {
        return try self.decrypt(Swift.ArraySlice(arg0))
    }
}

extension CryptoSwift.BlockDecryptor {
    @_silgen_name("SBW_BlockDecryptor_update_AF13EE4D")
    public func _sbw_update_AF13EE4D(_ withBytes: Array<UInt8>, _ isLast: Bool) throws -> Array<UInt8> {
        return try self.update(withBytes: Swift.ArraySlice(withBytes), isLast: isLast)
    }
}

extension CryptoSwift.SHA3 {
    @_silgen_name("SBW_SHA3_update_603F8BDE")
    public func _sbw_update_603F8BDE(_ withBytes: Array<UInt8>, _ isLast: Bool) throws -> Array<UInt8> {
        return try self.update(withBytes: Swift.ArraySlice(withBytes), isLast: isLast)
    }
}
