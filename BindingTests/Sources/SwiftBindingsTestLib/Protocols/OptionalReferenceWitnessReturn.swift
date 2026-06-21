// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional reference-type protocol property returns via witness dispatch
//
// End-to-end gate for the convergence of `WitnessDispatchEmitter.IsOptionalClassReturn` onto the
// canonical ABI oracle `WrapperValidation.IsOptionalWithReferenceInner`. The oracle is BROADER than
// the predicate it replaced (`IsSwiftClassType`, which matched only a pure-Swift `Kind == Class`
// with no native remap and outside an ObjC module). The oracle additionally recognises, as a
// nullable-pointer-ABI reference:
//   * an Apple ObjC class (`ObjCModuleType` — e.g. `NSData`, `UIImage`), and
//   * an ObjC-bridgeable VALUE type that bridges to an ObjC class (`URL` → `NSURL`).
//
// Before the convergence, an `Optional<one-of-those>` PROTOCOL PROPERTY GETTER was rejected as
// "not dispatchable via witness table": the proxy member carried an SB0003 `[Obsolete]` and its body
// threw `NotSupportedException("... on a Swift-backed existential container")`. The narrow predicate
// suppressed members the canonical ABI truth says are perfectly dispatchable. After the convergence
// the generator emits a real witness accessor for each: a `@_cdecl` getter that unwraps the optional
// and returns `Unmanaged.passRetained(result as AnyObject).toOpaque()` (+1), read on the C# side via
// `MarshalFromSwift<T>` (which adopts that +1) with an `IntPtr.Zero → null` guard. This is the SAME
// +1/adopt ownership the pre-existing CONCRETE-type optional-reference return path already used — so
// it is a capability expansion (previously-rejected members now work), not a new ABI contract.
//
// The probe is vended as `any OptionalReferenceWitnessProbe` so the C# side reads each property
// through the Swift-backed existential's own witness table — the exact path SB0003 used to reject,
// and the path the real-world libraries (BlinkID `bundleURL`/`uiImage`, Kingfisher `contentURL`/
// `image`, RichTextKit `layoutManagerWrapper`/`textStorageWrapper`) exercise.

/// Protocol whose optional reference-type getters the convergence newly makes dispatchable:
///   * `fileURL: URL?`     — an ObjC-bridgeable VALUE type (the struct-bridges-to-`NSURL` case the
///                           narrow predicate's `Kind == Class` test missed; the doc's specific worry
///                           that a value type might mis-route to the class path — it does route via
///                           the class accessor, but correctly, through the ObjC bridge).
///   * `attachment: NSData?` — an Apple ObjC class (`ObjCModuleType`), which `IsSwiftClassType`
///                           rejected (it excludes ObjC-module types) but the oracle accepts.
public protocol OptionalReferenceWitnessProbe {
    var fileURL: URL? { get }
    var attachment: NSData? { get }
}

/// Swift conformer — the concrete type behind the vended existential.
public final class OptionalReferenceWitnessImpl: OptionalReferenceWitnessProbe {
    private let _url: URL?
    private let _data: NSData?
    public init(url: URL?, data: NSData?) {
        _url = url
        _data = data
    }
    public var fileURL: URL? { _url }
    public var attachment: NSData? { _data }
}

/// Vends the probe as `any OptionalReferenceWitnessProbe`, forcing the C# read through the
/// Swift-backed existential (witness-dispatch) path.
public final class OptionalReferenceWitnessVendor {
    public init() {}

    /// A `urlString` of nil yields `fileURL == nil`; a `dataLength` below zero yields
    /// `attachment == nil`. Both present/absent combinations are reachable so the C# side can pin
    /// both the non-nil round-trip and the nil sentinel for each property independently.
    public func makeProbe(urlString: String?, dataLength: Int32) -> any OptionalReferenceWitnessProbe {
        let url = urlString.flatMap { URL(string: $0) }
        let data: NSData? = dataLength >= 0
            ? NSData(bytes: Array<UInt8>(repeating: 0xAB, count: Int(dataLength)), length: Int(dataLength))
            : nil
        return OptionalReferenceWitnessImpl(url: url, data: data)
    }
}
