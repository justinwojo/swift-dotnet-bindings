// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Darwin
import Foundation

// These counters live in the fixture module rather than the generated wrapper. A rejected call
// must leave them at zero, proving that the actual Swift declaration was never entered.
nonisolated(unsafe) internal var _mlgCollectionEntryCount: Int32 = 0
nonisolated(unsafe) internal var _mlgHashEntryCount: Int32 = 0
nonisolated(unsafe) internal var _mlgAnimalRosterEntryCount: Int32 = 0
nonisolated(unsafe) internal var _mlgSuperclassEntryCount: Int32 = 0

private let mlgProbeRefused = UInt64(1) << 0
private let mlgProbeResultUntouched = UInt64(1) << 1
private let mlgProbeErrorUntouched = UInt64(1) << 2
private let mlgProbeNoEntry = UInt64(1) << 3
private let mlgProbeNoMutation = UInt64(1) << 4
private let mlgProbeProtectedStorage = UInt64(1) << 5
private let mlgProbeDirectSentinel = UInt64(1) << 6

private let mlgResultCanary0: UInt64 = 0xC0FF_EE00_1234_5678
private let mlgResultCanary1: UInt64 = 0xA55A_0FF0_8765_4321

private typealias MlgParameterizedWrapper = @convention(c) (
    UnsafeMutableRawPointer,
    UnsafeRawPointer,
    UnsafeRawPointer,
    UnsafeMutableRawPointer,
    UnsafeMutablePointer<UInt8>
) -> Void

private typealias MlgAssociatedProtocolWrapper = @convention(c) (
    UnsafeRawPointer,
    UnsafeRawPointer,
    UnsafeRawPointer,
    UnsafeMutablePointer<UnsafeMutableRawPointer?>,
    UnsafeMutablePointer<UInt8>
) -> Int

private typealias MlgAssociatedSuperclassWrapper = @convention(c) (
    UnsafeRawPointer,
    Int,
    UnsafeRawPointer,
    UnsafeMutableRawPointer,
    UnsafeMutablePointer<UInt8>
) -> Void

private typealias MlgDirectSuperclassWrapper = @convention(c) (
    UnsafeMutableRawPointer,
    UnsafeRawPointer,
    UnsafeRawPointer,
    UnsafeMutableRawPointer,
    UnsafeMutablePointer<UInt8>
) -> Void

private func mlgResolve<T>(_ symbol: String, as type: T.Type) -> T? {
    guard let handle = dlopen(nil, RTLD_NOW) else { return nil }
    defer { dlclose(handle) }
    guard let address = dlsym(handle, symbol) else { return nil }
    return unsafeBitCast(address, to: T.self)
}

private func mlgMetadata(_ type: Any.Type) -> UnsafeRawPointer {
    unsafeBitCast(type, to: UnsafeRawPointer.self)
}

/// Runs a closure with a real mapped page that has no read or write permission. Reaching a payload
/// load, receiver reconstruction, VWT destroy, or result write through this pointer faults the
/// isolated runtime-test class instead of accidentally succeeding against benign dummy bytes.
private func mlgWithInaccessiblePage<T>(_ body: (UnsafeMutableRawPointer) -> T) -> T? {
    let byteCount = Int(getpagesize())
    let pointer = mmap(nil, byteCount, PROT_NONE, MAP_PRIVATE | MAP_ANON, -1, 0)
    guard pointer != MAP_FAILED else { return nil }
    defer { munmap(pointer, byteCount) }
    return body(pointer!)
}

/// Calls the actual generated CollectionHost.joinItems wrapper with Collection<Int32> metadata.
/// Both payload and class receiver are inaccessible, while the writable result buffer is a canary.
@_cdecl("SBW_Test_MlgParameterizedRefusalProbe")
public func sbwTestMlgParameterizedRefusalProbe() -> UInt64 {
    _mlgCollectionEntryCount = 0
    guard let wrapper = mlgResolve(
        "SBW_SwiftBindingsTestLib_CollectionHost_joinItems_9B4513F0",
        as: MlgParameterizedWrapper.self
    ) else { return 0 }

    return mlgWithInaccessiblePage { inaccessible in
        var report = mlgProbeProtectedStorage
        var refusal: UInt8 = 0x7F
        var resultCanary = (mlgResultCanary0, mlgResultCanary1)
        withUnsafeMutableBytes(of: &resultCanary) { result in
            wrapper(
                result.baseAddress!, inaccessible, mlgMetadata(MlgIntCollection.self),
                inaccessible, &refusal
            )
        }
        if refusal == 1 { report |= mlgProbeRefused }
        if resultCanary.0 == mlgResultCanary0 && resultCanary.1 == mlgResultCanary1 {
            report |= mlgProbeResultUntouched
        }
        if _mlgCollectionEntryCount == 0 { report |= mlgProbeNoEntry }
        return report
    } ?? 0
}

/// Calls the throwing associated-protocol wrapper with Sequence metadata whose Element does not
/// conform to HashLike. The root proof succeeds, the conditional carrier proof refuses, and the
/// payload/receiver remain inaccessible. The error slot starts non-null to prove refusal does not
/// retain or publish a Swift error.
@_cdecl("SBW_Test_MlgAssociatedProtocolRefusalProbe")
public func sbwTestMlgAssociatedProtocolRefusalProbe() -> UInt64 {
    _mlgHashEntryCount = 0
    guard let wrapper = mlgResolve(
        "SBW_SwiftBindingsTestLib_HashSink_sumHashesOrThrow_22D3018E",
        as: MlgAssociatedProtocolWrapper.self
    ) else { return 0 }

    return mlgWithInaccessiblePage { inaccessible in
        var report = mlgProbeProtectedStorage
        var refusal: UInt8 = 0x7F
        let errorCanary = UnsafeMutableRawPointer(bitPattern: 0x1357_9BDF)!
        var error: UnsafeMutableRawPointer? = errorCanary
        let result = wrapper(
            inaccessible, mlgMetadata(MlgNonHashSequence.self), inaccessible, &error, &refusal
        )
        if refusal == 1 { report |= mlgProbeRefused }
        if result == 0 { report |= mlgProbeDirectSentinel }
        if error == errorCanary { report |= mlgProbeErrorUntouched }
        if _mlgHashEntryCount == 0 { report |= mlgProbeNoEntry }
        return report
    } ?? 0
}

/// Calls the associated-superclass wrapper with a valid mutable receiver but inaccessible payload.
/// The Sequence root proof succeeds and Element: Animal fails, so the receiver must remain intact.
@_cdecl("SBW_Test_MlgAssociatedSuperclassRefusalProbe")
public func sbwTestMlgAssociatedSuperclassRefusalProbe() -> UInt64 {
    _mlgAnimalRosterEntryCount = 0
    guard let wrapper = mlgResolve(
        "SBW_SwiftBindingsTestLib_AnimalRoster_insert_0DFA7613",
        as: MlgAssociatedSuperclassWrapper.self
    ) else { return 0 }

    return mlgWithInaccessiblePage { inaccessible in
        var report = mlgProbeProtectedStorage
        var refusal: UInt8 = 0x7F
        var roster = AnimalRoster([
            Animal(name: "before", sound: "one"),
            Animal(name: "after", sound: "two"),
        ])
        let originalCount = roster.count
        withUnsafeMutablePointer(to: &roster) { receiver in
            wrapper(
                inaccessible, 1, mlgMetadata(MlgNonAnimalSequence.self),
                UnsafeMutableRawPointer(receiver), &refusal
            )
        }
        if refusal == 1 { report |= mlgProbeRefused }
        if _mlgAnimalRosterEntryCount == 0 { report |= mlgProbeNoEntry }
        if roster.count == originalCount { report |= mlgProbeNoMutation }
        return report
    } ?? 0
}

/// Calls the actual direct-superclass instance wrapper with valid metadata for a non-Animal Swift
/// type. C# cannot express this call because its public generic constraint is `where T: Animal`.
/// Both payload and class receiver remain protected and the indirect String result stays a canary.
@_cdecl("SBW_Test_MlgDirectSuperclassRefusalProbe")
public func sbwTestMlgDirectSuperclassRefusalProbe() -> UInt64 {
    _mlgSuperclassEntryCount = 0
    guard let wrapper = mlgResolve(
        "SBW_SwiftBindingsTestLib_ClassBoundGenericHost_inspect_42BF34CE",
        as: MlgDirectSuperclassWrapper.self
    ) else { return 0 }

    return mlgWithInaccessiblePage { inaccessible in
        var report = mlgProbeProtectedStorage
        var refusal: UInt8 = 0x7F
        var resultCanary = (mlgResultCanary0, mlgResultCanary1)
        withUnsafeMutableBytes(of: &resultCanary) { result in
            wrapper(
                result.baseAddress!, inaccessible, mlgMetadata(MlgIntCollection.self),
                inaccessible, &refusal
            )
        }
        if refusal == 1 { report |= mlgProbeRefused }
        if resultCanary.0 == mlgResultCanary0 && resultCanary.1 == mlgResultCanary1 {
            report |= mlgProbeResultUntouched
        }
        if _mlgSuperclassEntryCount == 0 { report |= mlgProbeNoEntry }
        return report
    } ?? 0
}
