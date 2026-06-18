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

/// A struct whose only field is a `weak` reference. A weak reference is registered in the
/// runtime's weak-reference side table, so moving the value requires a runtime fixup — making
/// this type both non-POD (needs a release on destroy) AND non-bitwise-takable (cannot be moved
/// with a plain memcpy). It is the ONLY probe type that exercises the IsNonBitwiseTakable flag
/// bit against a `true` value: Int/Bool/Double/String/class/enum/tuple are all bitwise-takable,
/// so without this type a wrong IsNonBitwiseTakable mask would never be caught. (Cross-checks
/// the value-witness POD / bitwise-takable flags in AbiLayoutTripwireTests.cs.)
struct AbiTripwireWeakBox {
    weak var ref: AbiTripwireProbeClass?
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
// 9=WeakBox (value-witness POD/bitwise-takable flag probe only — see abi_is_pod / abi_is_bitwise_takable)

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
    case 9: return metadataPointer(AbiTripwireWeakBox.self)
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

// MARK: - Live value-witness POD / bitwise-takable predicates (corner 1)
//
// The C# mirror (Swift.Runtime ValueWitnessTable) decodes two flag bits of the value-witness
// table: IsNonPOD = 0x00010000 and IsNonBitwiseTakable = 0x00100000. Reading those bits on BOTH
// sides would be tautological — it would only prove C# and Swift made the same offset/mask
// assumption, never that the assumption matches Apple's runtime. Instead the Swift side asks the
// compiler/stdlib for the SEMANTIC predicate via the underscored `_isPOD` / `_isBitwiseTakable`
// intrinsics (the same source of truth `MemoryLayout` draws on), which never touch the VWT flag
// word. The C# test then asserts `vwt->IsNonPOD == !abi_is_pod(...)` and
// `vwt->IsNonBitwiseTakable == !abi_is_bitwise_takable(...)`: an Apple change to either bit
// position fails the comparison instead of corrupting memory silently. The polarity is inverted
// because the C# flags are the NEGATIVE form (IsNon…) of the Swift predicates.

@inline(never)
private func isPOD<T>(_ type: T.Type) -> Bool {
    return _isPOD(type)
}

@inline(never)
private func isBitwiseTakable<T>(_ type: T.Type) -> Bool {
    return _isBitwiseTakable(type)
}

@_cdecl("abi_is_pod")
public func abi_is_pod(_ typeId: Int32) -> Int32 {
    switch typeId {
    case 0: return isPOD(Int.self) ? 1 : 0
    case 1: return isPOD(Bool.self) ? 1 : 0
    case 2: return isPOD(Double.self) ? 1 : 0
    case 3: return isPOD(String.self) ? 1 : 0
    case 4: return isPOD(AbiTripwireProbeStruct.self) ? 1 : 0
    case 5: return isPOD(AbiTripwireProbeEnum.self) ? 1 : 0
    case 6: return isPOD(Optional<Int>.self) ? 1 : 0
    case 7: return isPOD(AbiTripwireProbeClass.self) ? 1 : 0
    case 8: return isPOD((Int8, Int, Bool).self) ? 1 : 0
    case 9: return isPOD(AbiTripwireWeakBox.self) ? 1 : 0
    default: return -1
    }
}

@_cdecl("abi_is_bitwise_takable")
public func abi_is_bitwise_takable(_ typeId: Int32) -> Int32 {
    switch typeId {
    case 0: return isBitwiseTakable(Int.self) ? 1 : 0
    case 1: return isBitwiseTakable(Bool.self) ? 1 : 0
    case 2: return isBitwiseTakable(Double.self) ? 1 : 0
    case 3: return isBitwiseTakable(String.self) ? 1 : 0
    case 4: return isBitwiseTakable(AbiTripwireProbeStruct.self) ? 1 : 0
    case 5: return isBitwiseTakable(AbiTripwireProbeEnum.self) ? 1 : 0
    case 6: return isBitwiseTakable(Optional<Int>.self) ? 1 : 0
    case 7: return isBitwiseTakable(AbiTripwireProbeClass.self) ? 1 : 0
    case 8: return isBitwiseTakable((Int8, Int, Bool).self) ? 1 : 0
    case 9: return isBitwiseTakable(AbiTripwireWeakBox.self) ? 1 : 0
    default: return -1
    }
}

// MARK: - Live Optional size rule (corner 3)
//
// SwiftOptional<T> (Swift.Runtime) decides how to encode the Some/None discriminator from a
// single live fact: whether Optional<T> is LARGER than T. When `Optional<T>.size > T.size` the
// payload has no spare bit patterns, so Swift appends a tag byte at offset `T.size`
// (GetTagByteOffset returns it). When `Optional<T>.size == T.size` the payload has extra
// inhabitants (a class's nil pointer, a Bool's 2…255, a String's spare bits) that encode None
// in-place, so there is no tag byte (GetTagByteOffset returns -1). `Optional<Bool>` is the
// footgun the production code special-cases: it is size-equal like a class (1 == 1, NOT 1 + 1),
// so it must take the extra-inhabitant path, not the tag-byte path. This probe exports the two
// live sizes per payload type so the C# test can assert that relationship from observed layout
// rather than from a constant re-typed on the Swift side.

@_cdecl("abi_optional_layout_facts")
public func abi_optional_layout_facts(_ payloadTypeId: Int32, _ out: UnsafeMutablePointer<Int>) {
    // out[0] = MemoryLayout<Optional<T>>.size, out[1] = MemoryLayout<T>.size, for the payload T.
    switch payloadTypeId {
    case 0:
        out[0] = MemoryLayout<Optional<Int>>.size
        out[1] = MemoryLayout<Int>.size
    case 1:
        out[0] = MemoryLayout<Optional<Bool>>.size
        out[1] = MemoryLayout<Bool>.size
    case 3:
        out[0] = MemoryLayout<Optional<String>>.size
        out[1] = MemoryLayout<String>.size
    case 7:
        out[0] = MemoryLayout<Optional<AbiTripwireProbeClass>>.size
        out[1] = MemoryLayout<AbiTripwireProbeClass>.size
    default:
        out[0] = -1
        out[1] = -1
    }
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
