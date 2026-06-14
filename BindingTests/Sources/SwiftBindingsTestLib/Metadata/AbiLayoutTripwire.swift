// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// ABI-layout tripwire fixture (architecture review 2026-06, Findings 4 & 59).
//
// Swift.Runtime hand-mirrors a set of Swift ABI layout facts as C# constants and struct
// layouts: value-witness-table field offsets, existential-container sizes for arity 0-8,
// metadata-kind discriminators (plus the > 0x7ff class heuristic), tuple element-vector
// offsets, and frozen-struct sizes. None of those mirrors had a test comparing them to the
// *live* Swift runtime; the only guards compared C# constants to other C# constants, so they
// catch our own edits but never an Apple ABI drift. Every drift mode is silent memory
// corruption that surfaces as a "Mono/NativeAOT bug".
//
// This file exports the GROUND TRUTH for those facts via @_cdecl functions, computed from
// MemoryLayout and the live type metadata of real Swift values. The matching C# test
// (RuntimeTestsApp/Metadata/AbiLayoutTripwireTests.cs) asserts the runtime mirrors equal this
// truth. The truth is *observed* from real values (MemoryLayout queries, live metadata
// pointers, pointer subtraction) and is never a constant re-typed on the Swift side, so the
// two sides cannot drift together. Plain @_cdecl globals are not touched by the harness's
// source stripper, so no PreservedProtocols registration is required.

// MARK: - Probe types
//
// These exist only to be probed via the @_cdecl functions below — they are never consumed as
// generated C# bindings — so they are `internal` to stay out of the binding surface. They
// appear only inside function bodies, never in a C-exported signature.

/// A struct with a deliberately non-trivial field layout (1-byte, then 8-byte, then 1-byte) so
/// it has interior padding (size 17, stride 24, alignment 8). Exercises the value-witness
/// size/stride/alignment reads and the InitializeWithCopy slot behaviorally. (`@frozen` is not
/// needed: a struct is laid out concretely within its defining module, and the layout is read
/// live via the value witness table and MemoryLayout, not assumed.)
struct AbiTripwireProbeStruct {
    var a: Int8
    var b: Int
    var c: Bool
}

/// A no-payload enum, used to confirm the Enum metadata-kind discriminator.
enum AbiTripwireProbeEnum {
    case alpha
    case beta
    case gamma
}

/// A reference type, used to confirm the class metadata-kind heuristic (the first metadata
/// word for a class is an isa/address greater than 0x7ff, not a small kind value).
final class AbiTripwireProbeClass {
    var value: Int = 0
}

// Empty marker protocols composed to build existentials of arity 1-8. Each protocol in the
// composition adds one witness-table word to the opaque existential container, so an arity-N
// existential is (4 + N) machine words — the layout ExistentialContainerN mirrors.
protocol AbiTW1 {}
protocol AbiTW2 {}
protocol AbiTW3 {}
protocol AbiTW4 {}
protocol AbiTW5 {}
protocol AbiTW6 {}
protocol AbiTW7 {}
protocol AbiTW8 {}

// MARK: - Type-id dispatch
// Keep these ids in lockstep with the constants in AbiLayoutTripwireTests.cs:
// 0=Int 1=Bool 2=Double 3=String 4=ProbeStruct 5=ProbeEnum 6=Optional<Int> 7=ProbeClass 8=(Int8,Int,Bool)

@inline(never)
private func metadataPointer<T>(_ type: T.Type) -> UnsafeRawPointer {
    // A concrete metatype value is the type's metadata pointer; bit-casting exposes it.
    return unsafeBitCast(type, to: UnsafeRawPointer.self)
}

private func metadataPointer(forTypeId typeId: Int32) -> UnsafeRawPointer {
    switch typeId {
    case 0: return metadataPointer(Int.self)
    case 1: return metadataPointer(Bool.self)
    case 2: return metadataPointer(Double.self)
    case 3: return metadataPointer(String.self)
    case 4: return metadataPointer(AbiTripwireProbeStruct.self)
    case 5: return metadataPointer(AbiTripwireProbeEnum.self)
    case 6: return metadataPointer(Optional<Int>.self)
    case 7: return metadataPointer(AbiTripwireProbeClass.self)
    case 8: return metadataPointer((Int8, Int, Bool).self)
    default: return metadataPointer(Int.self)
    }
}

// MARK: - Live layout facts (MemoryLayout)

@_cdecl("abi_layout_size")
public func abi_layout_size(_ typeId: Int32) -> Int {
    switch typeId {
    case 0: return MemoryLayout<Int>.size
    case 1: return MemoryLayout<Bool>.size
    case 2: return MemoryLayout<Double>.size
    case 3: return MemoryLayout<String>.size
    case 4: return MemoryLayout<AbiTripwireProbeStruct>.size
    case 5: return MemoryLayout<AbiTripwireProbeEnum>.size
    case 6: return MemoryLayout<Optional<Int>>.size
    case 7: return MemoryLayout<AbiTripwireProbeClass>.size
    case 8: return MemoryLayout<(Int8, Int, Bool)>.size
    default: return -1
    }
}

@_cdecl("abi_layout_stride")
public func abi_layout_stride(_ typeId: Int32) -> Int {
    switch typeId {
    case 0: return MemoryLayout<Int>.stride
    case 1: return MemoryLayout<Bool>.stride
    case 2: return MemoryLayout<Double>.stride
    case 3: return MemoryLayout<String>.stride
    case 4: return MemoryLayout<AbiTripwireProbeStruct>.stride
    case 5: return MemoryLayout<AbiTripwireProbeEnum>.stride
    case 6: return MemoryLayout<Optional<Int>>.stride
    case 7: return MemoryLayout<AbiTripwireProbeClass>.stride
    case 8: return MemoryLayout<(Int8, Int, Bool)>.stride
    default: return -1
    }
}

@_cdecl("abi_layout_alignment")
public func abi_layout_alignment(_ typeId: Int32) -> Int {
    switch typeId {
    case 0: return MemoryLayout<Int>.alignment
    case 1: return MemoryLayout<Bool>.alignment
    case 2: return MemoryLayout<Double>.alignment
    case 3: return MemoryLayout<String>.alignment
    case 4: return MemoryLayout<AbiTripwireProbeStruct>.alignment
    case 5: return MemoryLayout<AbiTripwireProbeEnum>.alignment
    case 6: return MemoryLayout<Optional<Int>>.alignment
    case 7: return MemoryLayout<AbiTripwireProbeClass>.alignment
    case 8: return MemoryLayout<(Int8, Int, Bool)>.alignment
    default: return -1
    }
}

// MARK: - Live type metadata

@_cdecl("abi_type_metadata")
public func abi_type_metadata(_ typeId: Int32) -> UnsafeRawPointer {
    return metadataPointer(forTypeId: typeId)
}

@_cdecl("abi_metadata_kind_word")
public func abi_metadata_kind_word(_ typeId: Int32) -> Int {
    // The first pointer-sized word of any type metadata is its metadata-kind discriminator:
    // for non-class kinds it is exactly the kind value (e.g. Struct = 0x200, Tuple = 0x301);
    // for classes it is an isa/address greater than 0x7ff.
    return metadataPointer(forTypeId: typeId).load(as: Int.self)
}

// MARK: - Live existential-container sizes (arity 0-8)

@_cdecl("abi_existential_size")
public func abi_existential_size(_ arity: Int32) -> Int {
    switch arity {
    case 0: return MemoryLayout<Any>.size
    case 1: return MemoryLayout<any AbiTW1>.size
    case 2: return MemoryLayout<any AbiTW1 & AbiTW2>.size
    case 3: return MemoryLayout<any AbiTW1 & AbiTW2 & AbiTW3>.size
    case 4: return MemoryLayout<any AbiTW1 & AbiTW2 & AbiTW3 & AbiTW4>.size
    case 5: return MemoryLayout<any AbiTW1 & AbiTW2 & AbiTW3 & AbiTW4 & AbiTW5>.size
    case 6: return MemoryLayout<any AbiTW1 & AbiTW2 & AbiTW3 & AbiTW4 & AbiTW5 & AbiTW6>.size
    case 7: return MemoryLayout<any AbiTW1 & AbiTW2 & AbiTW3 & AbiTW4 & AbiTW5 & AbiTW6 & AbiTW7>.size
    case 8: return MemoryLayout<any AbiTW1 & AbiTW2 & AbiTW3 & AbiTW4 & AbiTW5 & AbiTW6 & AbiTW7 & AbiTW8>.size
    default: return -1
    }
}

// MARK: - Live tuple element offsets

@_cdecl("abi_tuple_element_offsets")
public func abi_tuple_element_offsets(_ out: UnsafeMutablePointer<Int>) {
    // Observe the true byte offsets of the (Int8, Int, Bool) tuple elements directly from a
    // live value. Each element's address is captured as an integer *inside* its own
    // `withUnsafePointer` closure, so no pointer escapes the closure's guaranteed lifetime —
    // only the resulting `Int` bit patterns cross the boundary. The accesses are sequential
    // (each closure completes before the next begins), so there is no overlapping exclusive
    // access to `tuple`. Offsets are measured relative to the tuple base, which makes
    // "element 0 lives at offset 0" an observed fact rather than a hardcoded assumption.
    var tuple: (Int8, Int, Bool) = (0, 0, false)
    let base = withUnsafePointer(to: &tuple) { Int(bitPattern: UnsafeRawPointer($0)) }
    out[0] = withUnsafePointer(to: &tuple.0) { Int(bitPattern: UnsafeRawPointer($0)) - base }
    out[1] = withUnsafePointer(to: &tuple.1) { Int(bitPattern: UnsafeRawPointer($0)) - base }
    out[2] = withUnsafePointer(to: &tuple.2) { Int(bitPattern: UnsafeRawPointer($0)) - base }
}

// MARK: - Valid probe-value writer

@_cdecl("abi_probe_struct_init")
public func abi_probe_struct_init(_ out: UnsafeMutableRawPointer) {
    // Initialize a fully-formed AbiTripwireProbeStruct value into caller-provided storage so the
    // C# InitializeWithCopy probe exercises the value witness on a *genuine* Swift value (within
    // its ABI preconditions) rather than on arbitrary bytes. The field values are non-zero so a
    // successful copy is observable against a zeroed destination.
    out.assumingMemoryBound(to: AbiTripwireProbeStruct.self)
        .initialize(to: AbiTripwireProbeStruct(a: 0x12, b: 0x0102030405060708, c: true))
}
