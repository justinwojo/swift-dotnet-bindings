import CryptoSwift
import Foundation

// Minimal SwiftBindings wrapper for CryptoSwift runtime testing.
// The full generated Swift.CryptoSwift.swift has compilation issues
// (protocol conformance mismatches, internal access, wrong labels)
// that need generator fixes. This file provides the subset needed
// for runtime validation.

@frozen
public struct SBW_Utf8Slice {
    public var ptr: UnsafeMutablePointer<UInt8>
    public var len: Int
}

fileprivate var _sbw_emptyBuffer: UInt8 = 0

@_silgen_name("SBW_Free_CryptoSwift")
public func SBW_Free(_ ptr: UnsafeMutableRawPointer?) {
    ptr?.deallocate()
}

// MARK: - RSA ArraySlice wrappers

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

// MARK: - ChaCha20 ArraySlice wrappers

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

// MARK: - SHA2 ArraySlice wrapper

extension CryptoSwift.SHA2 {
    @_silgen_name("SBW_SHA2_update_0F871E93")
    public func _sbw_update_0F871E93(_ withBytes: Array<UInt8>, _ isLast: Bool) throws -> Array<UInt8> {
        return try self.update(withBytes: Swift.ArraySlice(withBytes), isLast: isLast)
    }
}

// MARK: - SHA1 ArraySlice wrapper

extension CryptoSwift.SHA1 {
    @_silgen_name("SBW_SHA1_update_5174E2D0")
    public func _sbw_update_5174E2D0(_ withBytes: Array<UInt8>, _ isLast: Bool) throws -> Array<UInt8> {
        return try self.update(withBytes: Swift.ArraySlice(withBytes), isLast: isLast)
    }
}

// MARK: - MD5 ArraySlice wrapper

extension CryptoSwift.MD5 {
    @_silgen_name("SBW_MD5_update_1E4EF80A")
    public func _sbw_update_1E4EF80A(_ withBytes: Array<UInt8>, _ isLast: Bool) throws -> Array<UInt8> {
        return try self.update(withBytes: Swift.ArraySlice(withBytes), isLast: isLast)
    }
}

// MARK: - Rabbit ArraySlice wrappers

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

// MARK: - SHA3 ArraySlice wrapper

extension CryptoSwift.SHA3 {
    @_silgen_name("SBW_SHA3_update_603F8BDE")
    public func _sbw_update_603F8BDE(_ withBytes: Array<UInt8>, _ isLast: Bool) throws -> Array<UInt8> {
        return try self.update(withBytes: Swift.ArraySlice(withBytes), isLast: isLast)
    }
}
