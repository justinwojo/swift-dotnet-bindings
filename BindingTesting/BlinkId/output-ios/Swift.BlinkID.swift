import BlinkID
import Foundation

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
// Vtable for Pinglet protocol - stores function pointers to C# implementations
fileprivate struct Pinglet_vtable {
    var csVTHandle: OpaquePointer? = nil
    var func_schemaName_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
    var func_schemaVersion_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
}

private var _pinglet_vtable = Pinglet_vtable()

// EveryProtocol conformance to Pinglet
extension EveryProtocol: BlinkID.Pinglet {
    public var schemaName: Swift.String {
        get {
            var selfProto: BlinkID.Pinglet = self
            let resultPtr = _pinglet_vtable.func_schemaName_get!(
                _pinglet_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.String.self).pointee
        }
    }
    
    public var schemaVersion: Swift.String {
        get {
            var selfProto: BlinkID.Pinglet = self
            let resultPtr = _pinglet_vtable.func_schemaVersion_get!(
                _pinglet_vtable.csVTHandle, &selfProto)
            return resultPtr.assumingMemoryBound(to: Swift.String.self).pointee
        }
    }
    
}

// Called by C# to register the protocol vtable
@_silgen_name("SetPinglet_vtable")
public func setPinglet_vtable(uvt: UnsafeRawPointer) {
    let vt: UnsafePointer<Pinglet_vtable> = uvt.assumingMemoryBound(to: Pinglet_vtable.self)
    _pinglet_vtable = vt.pointee
}
// Returns the protocol witness table pointer for EveryProtocol conforming to Pinglet.
// C# calls this via P/Invoke to obtain the witness table for existential container construction.
@_silgen_name("Get_EveryProtocol_Pinglet_WitnessTable")
public func getEveryProtocolPingletWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any BlinkID.Pinglet = instance
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
@_silgen_name("$s7BlinkID0A5IDSdkC21createScanningSession15sessionSettingsAA0A9IDSessionCAA0aiH0V_tYaKF_async")
public func PInvoke_createScanningSession_12234505(callback: @escaping @convention(c) (BlinkID.BlinkIDSession, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, sessionSettings: UnsafeRawPointer, _self: OpaquePointer){
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
            callback(resultcreateScanningSession, task)
        } catch {
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, task) }
        }
    }
}
extension BlinkID.BlinkIDSdk {
    @_silgen_name("$s7BlinkID0A5IDSdkC06createaC012withSettingsAcA0acF0V_tYaKFZ_async")
    public static func PInvoke_createBlinkIDSdk_1A53915F(callback: @escaping @convention(c) (BlinkID.BlinkIDSdk, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, withSettings: UnsafeRawPointer){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let withSettingsValue = withSettings.assumingMemoryBound(to: BlinkID.BlinkIDSdkSettings.self).pointee
        

        Task {
            do {
                let resultcreateBlinkIDSdk = try await BlinkID.BlinkIDSdk.createBlinkIDSdk(
                    withSettings: withSettingsValue
                )
                callback(resultcreateBlinkIDSdk, task)
            } catch {
                let errorMessage = String(describing: error)
                errorMessage.withCString { errorCallback($0, task) }
            }
        }
    }
}
extension BlinkID.BlinkIDSdk {
    @_silgen_name("$s7BlinkID0A5IDSdkC19refreshLicenseLeaseyyYaKFZ_async")
    public static func PInvoke_refreshLicenseLease_4394A71C(callback: @escaping @convention(c) (Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64){
        
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
@_silgen_name("$s7BlinkID11PingManagerC10addPinglet7pinglet13sessionNumberyx_SitYaAA0F0RzlF_async")
public func PInvoke_addPinglet_277550EB<P>(callback: @escaping @convention(c) (Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64, pinglet: P, sessionNumber: Swift.Int) where P : Pinglet{
    
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
public func PInvoke_sendPinglets_567DBDD8(callback: @escaping @convention(c) (BlinkID.PingStatus, Int64) -> Void, errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void, task: Int64){
    
    // selfInstance is safe - C# called Arc.Retain before invoking this method
    Task {
        do {
            let resultsendPinglets = try await BlinkID.PingManager.shared.sendPinglets(
                
            )
            callback(resultsendPinglets, task)
        } catch {
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, task) }
        }
    }
}
