// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Parameter names that spell a generated scratch local
//
// Marshalling a member mints scratch locals into the emitted C# wrapper body. Some carry a
// fixed spelling (`tag`, `optionalMetadata`, `resultBuffer`, `existentialResult`, `resultPtr`,
// `returnMetadata`, `swiftIndirectResult`). The rest are derived from one parameter's own C#
// name plus a suffix — `{p}Buffer`, `{p}Swift`, `{p}Disposable`, `{p}Converted`, `{p}Handle`,
// and the ObjC container owners `{p}NSArray` / `{p}NSDict` / `{p}NSSet` / `{p}Pairs`.
//
// Both families share one hazard: a Swift signature is free to spell any of those identifiers.
// A parameter projected onto the same identifier as a generated local redeclares it, and the
// emitted C# does not compile (CS0128 for a sibling parameter, CS0136 for a nested scope).
// The derived family is the sharper one, because the collision is between a local derived from
// ONE parameter and the projected name of a DIFFERENT parameter, so deduplicating parameters
// against each other never sees it.
//
// Two more families sit past the plain wrapper body. An asynchronous member's signature gains a
// cancellation token the emitter appends itself, and its body holds the callback context in a GC
// handle — both are names a Swift parameter can already occupy, and the token's own escape can
// land on a spelling a moved parameter just took. And a protocol-typed value handed back FROM
// Swift is held as a generated proxy, whose forwarding members re-declare each requirement's own
// parameter names and then mint the dispatch body's scratch locals right beside them.
//
// Every member below pairs a parameter that drives one of those locals into existence with a
// sibling parameter named exactly what that local is spelled, and folds both values into its
// answer so a run proves the two stayed distinct rather than one silently shadowing the other.

/// Existential shape returned by the `existentialResult` collision member.
public protocol LocalNameCollisionShape {
    var shapeValue: Int32 { get }
}

/// Concrete conformer handed back through the existential.
public struct LocalNameCollisionBox: LocalNameCollisionShape {
    public let shapeValue: Int32
    public init(shapeValue: Int32) { self.shapeValue = shapeValue }
}

/// Large-enough non-frozen struct to push a return onto the indirect-result path, where the
/// `resultPtr` / `returnMetadata` / `swiftIndirectResult` scratch locals are minted.
public struct LocalNameCollisionWide {
    public let a: Int64
    public let b: Int64
    public let c: Int64
    public let d: Int64

    public init(a: Int64, b: Int64, c: Int64, d: Int64) {
        self.a = a
        self.b = b
        self.c = c
        self.d = d
    }

    public var total: Int64 { a + b + c + d }
}

// MARK: - Locals derived from a sibling parameter's name

/// Each method takes a parameter whose marshalling mints `{p}<Suffix>` scratch locals, beside a
/// sibling parameter named exactly one of those spellings.
public class DerivedLocalNameCollider {
    public init() {}

    /// `[URL]?` bridges through an `NSArray` owner local named `itemsNSArray`.
    public func arrayOwner(items: [URL]?, itemsNSArray: Int32) -> Int32 {
        return Int32(items?.count ?? 0) * 100 + itemsNSArray
    }

    /// A native `[Int32]` mints `valuesBuffer` (and `valuesSwift`, `valuesDisposable`).
    public func arrayBuffer(values: [Int32], valuesBuffer: Int32) -> Int32 {
        return values.reduce(0, +) * 100 + valuesBuffer
    }

    /// The same native-array path, colliding on the `{p}Swift` container local instead.
    public func arrayContainer(numbers: [Int32], numbersSwift: Int32) -> Int32 {
        return numbers.reduce(0, +) * 100 + numbersSwift
    }

    /// The same native-array path, colliding on the `{p}Disposable` payload-buffer local.
    public func arrayDisposable(scores: [Int32], scoresDisposable: Int32) -> Int32 {
        return scores.reduce(0, +) * 100 + scoresDisposable
    }

    /// A `[String]` element conversion mints `labelsConverted` / `labelsContainers`.
    public func arrayConverted(labels: [String], labelsConverted: Int32) -> Int32 {
        return Int32(labels.count) * 100 + labelsConverted
    }

    /// A `String` parameter mints `textSwift` for its transient Swift string.
    public func stringSwift(text: String, textSwift: Int32) -> Int32 {
        return Int32(text.count) * 100 + textSwift
    }

    /// `[String: URL]` bridges through an `NSDictionary` owner named `mapNSDict`.
    public func dictOwner(map: [String: URL], mapNSDict: Int32) -> Int32 {
        return Int32(map.count) * 100 + mapNSDict
    }

    /// The same bridged-dictionary path, colliding on the `{p}Pairs` staging local.
    public func dictPairs(entries: [String: URL], entriesPairs: Int32) -> Int32 {
        return Int32(entries.count) * 100 + entriesPairs
    }

    /// `Set<URL>` bridges through an `NSSet` owner named `uniqueNSSet`.
    public func setOwner(unique: Set<URL>, uniqueNSSet: Int32) -> Int32 {
        return Int32(unique.count) * 100 + uniqueNSSet
    }

    /// A closure parameter mints a `GCHandle` local named `callbackHandle`.
    public func closureHandle(callback: (Int32) -> Int32, callbackHandle: Int32) -> Int32 {
        return callback(callbackHandle)
    }
}

// MARK: - Locals with a fixed spelling

/// Methods whose own scratch locals carry a fixed spelling, each shadowed by a parameter.
public class FixedLocalNameCollider {
    public init() {}

    /// `existentialResult` is the fixed local the existential return path reads through.
    public func existentialReturn(existentialResult: Int32) -> any LocalNameCollisionShape {
        return LocalNameCollisionBox(shapeValue: existentialResult)
    }

    /// `_swiftResult` is the fixed local a `Result` return is marshalled into.
    public func resultReturn(_swiftResult: Int32) -> Result<Int32, ResultTestError> {
        return _swiftResult >= 0 ? .success(_swiftResult * 2) : .failure(.notFound)
    }

    /// An indirect (large-struct) return mints `resultPtr`.
    public func indirectResultPtr(resultPtr: Int32) -> LocalNameCollisionWide {
        let v = Int64(resultPtr)
        return LocalNameCollisionWide(a: v, b: v + 1, c: v + 2, d: v + 3)
    }

    /// The same indirect return, shadowing the `returnMetadata` sizing local.
    public func indirectReturnMetadata(returnMetadata: Int32) -> LocalNameCollisionWide {
        let v = Int64(returnMetadata)
        return LocalNameCollisionWide(a: v, b: v + 1, c: v + 2, d: v + 3)
    }

    /// The same indirect return, shadowing the `swiftIndirectResult` register local.
    public func indirectRegister(swiftIndirectResult: Int32) -> LocalNameCollisionWide {
        let v = Int64(swiftIndirectResult)
        return LocalNameCollisionWide(a: v, b: v + 1, c: v + 2, d: v + 3)
    }

    /// Every wrapper body opens by pinning the receiver handle through a local named `success`.
    public func successFlag(success: Bool) -> Int32 {
        return success ? 1 : 0
    }

    /// A Swift argument LABEL spelled `self`, with a different internal name. The emitted C#
    /// parameter takes the internal name, so nothing here lands on the wrapper's own receiver
    /// binding — the negative control for the label-versus-parameter-name split.
    public func selfRegister(`self` value: Int32) -> Int32 {
        return value * 7
    }

    /// The native call's own return value is held in a local named `result`.
    public func resultLocal(result: Int32) -> Int32 {
        return result * 11
    }

}

// MARK: - Locals minted by the asynchronous lanes

/// An `async` member carries a cancellation token the emitter appends to its own signature rather
/// than one the Swift signature declared. A parameter spelled the same way puts two parameters of
/// one name on the member — a declaration error, and one that keeps the compilation from binding
/// any method body at all, so it also hides every other diagnostic in the same compile.
public class AsyncLocalNameCollider {
    public init() {}

    public func awaitToken(cancellationToken: Int32) async -> Int32 {
        return cancellationToken * 6
    }

    /// The same appended token, but with the collision one level deeper: the sibling shadows the
    /// scratch local derived from the first parameter, which moves that parameter's marshalling
    /// spelling aside — onto exactly the identifier the appended token escapes to when it finds
    /// its own name taken. Two names have to move here, not one.
    public func awaitAliasedToken(cancellationToken: [Int32], cancellationTokenBuffer: Int32) async -> Int32 {
        return Int32(cancellationToken.count) * 100 + cancellationTokenBuffer
    }

    /// The GC handle an asynchronous body hands the native call as its callback context.
    public func awaitHandle(handle: Int32) async -> Int32 {
        return handle * 7
    }
}

/// A member whose trailing parameter is an escaping completion handler additionally emits a
/// Task-returning convenience overload. That overload's body mints its own locals and its
/// forwarding lambda names the callback's value, all in scope with the projected parameters.
public class CompletionHandlerLocalNameCollider {
    public private(set) var lastValue: Int32 = 0

    public init() {}

    /// The task-completion source the overload awaits.
    public func runTcs(tcs: Int32, completion: @escaping (Int32) -> Void) {
        lastValue = tcs
        completion(tcs * 2)
    }

    /// The cancellation registration the overload disposes.
    public func runRegistration(registration: Int32, completion: @escaping (Int32) -> Void) {
        lastValue = registration
        completion(registration * 3)
    }

    /// The forwarding lambda's own parameter, which carries the callback value.
    public func runResult(result: Int32, completion: @escaping (Int32) -> Void) {
        lastValue = result
        completion(result * 4)
    }

    /// The cancellation token the overload appends to its own signature.
    public func runToken(cancellationToken: Int32, completion: @escaping (Int32) -> Void) {
        lastValue = cancellationToken
        completion(cancellationToken * 5)
    }
}

/// A generic parent declines the `@_cdecl` wrapper, so these members marshal their return on the
/// direct path — the one that reads the returned value back through a fixed local spelled
/// `swiftResult`. Each parameter here is spelled exactly that.
public struct DirectReturnLocalNameCollider<Tag> {
    public let seed: Int32

    public init(seed: Int32) {
        self.seed = seed
    }

    /// A `String` return is read back through the fixed local.
    public func stringReturn(swiftResult: Int32) -> String {
        return "s\(swiftResult)"
    }

    /// An optional-container return reads the same fixed local.
    public func optionalReturn(swiftResult: Int32) -> [String]? {
        return swiftResult >= 0 ? ["o\(swiftResult)"] : nil
    }

    /// And an optional-existential return, which reads it too.
    public func optionalExistentialReturn(swiftResult: Int32) -> (any LocalNameCollisionShape)? {
        return swiftResult >= 0 ? LocalNameCollisionBox(shapeValue: swiftResult) : nil
    }
}

/// Failable initializers route through a static `TryCreate` factory whose body mints `tag`,
/// `optionalMetadata` and `resultBuffer`. One struct per spelling, since a single initializer
/// can only shadow one of them.
public struct TagLocalCollider {
    public let value: Int32
    public init?(tag: Int32) {
        guard tag >= 0 else { return nil }
        self.value = tag * 2
    }
}

public struct OptionalMetadataLocalCollider {
    public let value: Int32
    public init?(optionalMetadata: Int32) {
        guard optionalMetadata >= 0 else { return nil }
        self.value = optionalMetadata * 3
    }
}

public struct ResultBufferLocalCollider {
    public let value: Int32
    public init?(resultBuffer: Int32) {
        guard resultBuffer >= 0 else { return nil }
        self.value = resultBuffer * 4
    }
}

// MARK: - Reverse dispatch (negative control)

/// Reverse dispatch — Swift calling back into a C# conformer — re-declares each requirement's
/// parameters POSITIONALLY in the generated receiver (`param0`, `rawArg0`, …), so no
/// user-chosen identifier ever shares a scope with the receiver's own scratch locals. This
/// requirement spells its parameter `swiftResult` to hold that property down: the receiver keeps
/// working, and the value still round-trips.
public protocol SwiftResultParameterReceiver {
    func compute(swiftResult: Int32) -> Int32
    var labels: [String] { get }
}

/// Calls back into a C# conformer so the proxy receiver body is exercised at runtime.
public func invokeSwiftResultReceiver(_ receiver: any SwiftResultParameterReceiver, value: Int32) -> Int32 {
    return receiver.compute(swiftResult: value)
}

/// Reads the container-returning requirement through the same receiver.
public func readSwiftResultReceiverLabels(_ receiver: any SwiftResultParameterReceiver) -> Int32 {
    return Int32(receiver.labels.count)
}

// MARK: - Forward dispatch through the proxy for a Swift-backed existential

/// A protocol-typed value that came FROM Swift is held in C# as a generated proxy. Unlike the
/// reverse-dispatch receiver above, the proxy re-declares every requirement with the
/// requirement's OWN parameter names, and the body that dispatches into the Swift witness
/// declares its scratch locals right beside them. Each requirement below spells a parameter
/// exactly like one of those locals, one per return shape the proxy emits.
public protocol ProxyLocalNameReceiver {
    /// The pointer a dispatched blittable result is read back through.
    func blittableResult(resultPtr: Int32) -> Int32

    /// The decoded view of a returned string.
    func textResult(slice: Int32) -> String

    /// The pinned existential container every dispatch is made on.
    func containerResult(containerPtr: Int32) -> Int32

    /// The error out-parameter every throwing dispatch passes.
    func throwingResult(errorOut: Int32) throws -> Int32

    /// The per-parameter wire slice and the pinned UTF-8 bytes behind a string parameter.
    func slicedResult(arg0Slice: String, arg0Bytes: Int32) -> Int32

    /// The allocation trio an indirect struct return sets up.
    func structResult(buffer: Int32, metadata: Int32, indirectResult: Int32) -> LocalNameCollisionWide

    /// The value read out of a returned existential heap cell.
    func existentialCellResult(container: Int32) -> any LocalNameCollisionShape

    /// A container return, read through the same result pointer.
    func collectionResult(resultPtr: Int32, extra: Int32) -> [String]
}

/// Swift-side conformer. Handing it back as `any ProxyLocalNameReceiver` is what puts the
/// generated proxy — rather than a C# implementation — in front of the caller.
public struct ProxyLocalNameConformer: ProxyLocalNameReceiver {
    public let seed: Int32

    public init(seed: Int32) { self.seed = seed }

    public func blittableResult(resultPtr: Int32) -> Int32 { seed * 100 + resultPtr }

    public func textResult(slice: Int32) -> String { "t\(seed)-\(slice)" }

    public func containerResult(containerPtr: Int32) -> Int32 { seed * 200 + containerPtr }

    public func throwingResult(errorOut: Int32) throws -> Int32 {
        guard errorOut >= 0 else { throw ResultTestError.notFound }
        return seed * 300 + errorOut
    }

    public func slicedResult(arg0Slice: String, arg0Bytes: Int32) -> Int32 {
        Int32(arg0Slice.count) * 100 + arg0Bytes
    }

    public func structResult(buffer: Int32, metadata: Int32, indirectResult: Int32) -> LocalNameCollisionWide {
        LocalNameCollisionWide(a: Int64(seed), b: Int64(buffer), c: Int64(metadata), d: Int64(indirectResult))
    }

    public func existentialCellResult(container: Int32) -> any LocalNameCollisionShape {
        LocalNameCollisionBox(shapeValue: seed * 400 + container)
    }

    public func collectionResult(resultPtr: Int32, extra: Int32) -> [String] {
        ["c\(resultPtr)", "e\(extra)"]
    }
}

/// Hands the conformer over as an existential, so C# receives the generated proxy.
public func makeProxyLocalNameReceiver(seed: Int32) -> any ProxyLocalNameReceiver {
    return ProxyLocalNameConformer(seed: seed)
}

// MARK: - Enum case construction, case inspection and operators

/// An enum case's associated-value labels become the parameters of the generated static factory,
/// and the factory body mints the metadata / buffer / indirect-result locals right beside them —
/// including a `result` local the emitter used to move aside with a one-name special case. A
/// String-carrying case additionally appends a per-string pointer/length pair and a trailing
/// result pointer to the extern's OWN parameter list; a duplicate there is a declaration error
/// that stops the whole compilation from binding any method body, not just this one.
public enum EnumCaseLocalNameCollider {
    case metadata(metadata: Int32)
    case buffer(buffer: Int32, indirectResult: Int32)
    case result(result: Int32)
    case labelled(resultPtr: String, indirectResult: Int32)
    /// The extern's appended trailing result pointer lands next to an associated value already
    /// spelled that way, while a sibling String value contributes its own pointer/length pair.
    case mixed(resultPtr: Int32, name: String)

    /// Folds whichever case is present into one number so a run proves the associated values
    /// reached Swift rather than a local shadowing one of them.
    public var folded: Int32 {
        switch self {
        case .metadata(let metadata): return metadata * 2
        case .buffer(let buffer, let indirectResult): return buffer * 10 + indirectResult
        case .result(let result): return result * 3
        case .labelled(let resultPtr, let indirectResult): return Int32(resultPtr.count) * 100 + indirectResult
        case .mixed(let resultPtr, let name): return resultPtr * 1000 + Int32(name.count)
        }
    }
}

/// A case with a multi-element tuple payload projects to a `TryGet` whose out-parameters carry the
/// element labels, while the body mints the metadata / enum-copy / success / tuple-metadata and
/// per-element offset locals into the same scope.
public enum TupleCaseLocalNameCollider {
    case pair(metadata: Int32, success: Int32)
    case triple(enumCopy: Int32, tupleMetadata: Int32, offset0: Int32)
    case none
}

/// A String-returning member on an enum mints a result pointer into a body that already holds the
/// extension's own parameters — one of which is spelled that way. The second parameter is an
/// ordinary neighbour that has to survive the move untouched, checked on both the instance and the
/// static form (the static one carries no receiver, so its parameter list starts where the instance
/// form's self would sit).
extension EnumCaseLocalNameCollider {
    public func describe(resultPtr: Int32, slice: Int32) -> String {
        return "d\(resultPtr)-\(slice)"
    }

    public static func label(resultPtr: Int32, slice: Int32) -> String {
        return "l\(resultPtr)-\(slice)"
    }
}

/// An operator's operand names are derived from the operand TYPE, not from the Swift-authored
/// parameter names, so the collision is reachable through the type name: this one projects its
/// operands onto the very spelling the indirectly-returned result's metadata local carries. The
/// operator returns a wide (indirectly returned) struct, which is what mints that local.
public struct ReturnMetadata {
    public let value: Int64
    public init(value: Int64) { self.value = value }

    public static func + (lhs: ReturnMetadata, rhs: ReturnMetadata) -> LocalNameCollisionWide {
        return LocalNameCollisionWide(a: lhs.value, b: rhs.value, c: 0, d: 0)
    }
}

/// A bound-generic associated value marshals through scratch locals suffixed onto its OWN label
/// (`{label}Swift`, `{label}Buffer`), and a sibling label here is spelled exactly like one of them.
/// The factory is a static member with no receiver and does not go through the wrapper path that
/// resolves parameter names, so the scope those locals land in is the factory's own parameter list.
public enum PayloadPlanLocalNameCollider {
    case packed(items: [Int32], itemsSwift: Int32)
    case empty

    /// Folds the payload so a run proves both values reached Swift rather than a scratch local.
    public var total: Int32 {
        switch self {
        case .packed(let items, let itemsSwift): return items.reduce(0, +) * 10 + itemsSwift
        case .empty: return -1
        }
    }
}
