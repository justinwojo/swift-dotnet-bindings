// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Runtime availability guard (TestFlight crash class: weak-linked member symbol)
//
// A Swift symbol whose availability floor is newer than the consuming binary's
// minimum-OS is *weak-linked*: present on a new-enough OS, NULL on an older one.
// The generated `@_cdecl` wrapper for such a member is itself `@available`-gated
// (so it compiles), but at runtime on an older OS the wrapper calls the null
// symbol and the process SIGSEGVs at pc=0 — a native fault no C# `try/catch` can
// intercept. This is the shape of the StoreKit2 TestFlight report: an
// iOS-26-only member reached on iOS 17/18.
//
// The fix emits a managed runtime OS-version guard at the top of every generated
// member whose *own* availability floor exceeds its enclosing type's (the same
// members that carry a per-member `[SupportedOSPlatform]` attribute). The guard
// throws PlatformNotSupportedException BEFORE the P/Invoke, converting the
// uncatchable native crash into a catchable, self-explanatory managed exception.
//
// We cannot reproduce the *null symbol* with our own test-lib types — symbols
// defined in our own dylib are never weak-imported, only external Apple-SDK
// symbols are. But the guard fires purely on the running OS version, independent
// of whether the target symbol is actually null, so a *future* floor (iOS 99)
// lets us observe the guard firing on any real simulator/device, and a
// currently-satisfied floor lets us prove it does NOT over-fire.

/// Non-gated carrier (available everywhere the package is) with members gated to
/// distinct floors. Mirrors the real `AppStore.getAgeRatingCode()` shape: a
/// future-only member on a type the consumer can freely reference, so nothing
/// warns the consumer off the member until it crashes at runtime — exactly the
/// case the runtime guard exists to convert into a managed exception.
public struct RuntimeGuardCarrier {
    public init() {}

    /// Ungated — must NOT receive a runtime guard; always callable.
    public func baseline() -> Int32 {
        return 1
    }

    /// Gated to a floor every current OS already satisfies. The guard is emitted
    /// (the member carries its own floor) but must evaluate false at runtime, so
    /// the call succeeds — proving the guard does not over-fire.
    @available(iOS 15.0, macOS 12.0, tvOS 15.0, macCatalyst 15.0, *)
    public func currentlyAvailable() -> Int32 {
        return 7
    }

    /// Gated to a floor no current OS satisfies. The instance method's runtime
    /// guard must fire and throw before the call — converting what would be a
    /// native crash (against a real weak-linked Apple symbol) into a managed
    /// PlatformNotSupportedException.
    @available(iOS 99.0, macOS 99.0, tvOS 99.0, macCatalyst 99.0, *)
    public func futureOnlyInstance() -> Int32 {
        return 99
    }

    /// Static counterpart. Static members are the highest-risk slice: they are
    /// reachable without first obtaining an instance, so no metadata resolution
    /// stands between the caller and the weak-linked symbol. The runtime guard is
    /// the only thing protecting this path.
    @available(iOS 99.0, macOS 99.0, tvOS 99.0, macCatalyst 99.0, *)
    public static func futureOnlyStatic() -> Int32 {
        return 99
    }
}

// MARK: - Gated TYPE (member floor inherited from the enclosing type)
//
// The members below declare NO availability floor of their own — their effective
// floor is INHERITED from the gated enclosing type. This is the exact case the
// pre-fix emitter mishandled: it deduped the member's floor against the parent's
// and, finding nothing the member added beyond the parent, emitted no guard —
// leaving a type-gated constructor or static member able to reach the weak-linked
// symbol and crash. The guard must key on the member's EFFECTIVE (merged-with-
// ancestors) floor, so each of these gets a guard even though it declares none
// itself.

/// Gated to a floor no current OS satisfies. None of its members declare a floor;
/// each inherits iOS 99 from the type, so the constructor and the static member —
/// both reachable without an instance — must guard and throw.
@available(iOS 99.0, macOS 99.0, tvOS 99.0, macCatalyst 99.0, *)
public struct FutureGatedType {
    public init() {}

    public func instanceValue() -> Int32 {
        return 42
    }

    public static func staticValue() -> Int32 {
        return 99
    }
}

/// Same inheritance shape, but gated to a floor every current OS already satisfies.
/// The inherited guard is still emitted on each member but must evaluate false — the
/// constructor, the instance member, and the static member must all succeed, proving
/// the merged-floor guard does not over-fire on an inherited-but-satisfied floor.
@available(iOS 15.0, macOS 12.0, tvOS 15.0, macCatalyst 15.0, *)
public struct CurrentlyGatedType {
    public init() {}

    public func instanceValue() -> Int32 {
        return 7
    }

    public static func staticValue() -> Int32 {
        return 11
    }
}

// MARK: - Operator with a stricter-than-parent floor
//
// The parent struct is ungated; the `+` operator is gated to a future floor. The
// operator's effective floor (iOS 99) exceeds its parent's (none), so the generated
// C# operator must carry a runtime guard that fires — converting what would be a
// native crash on a weak-linked operator symbol into a managed exception. The
// constructor and the `value` getter are ungated and must remain freely callable, so
// the operands can be built and only the operator itself is gated.
@frozen
public struct GuardedOperand {
    public let value: Int32

    public init(_ value: Int32) {
        self.value = value
    }

    @available(iOS 99.0, macOS 99.0, tvOS 99.0, macCatalyst 99.0, *)
    public static func + (lhs: GuardedOperand, rhs: GuardedOperand) -> GuardedOperand {
        return GuardedOperand(lhs.value + rhs.value)
    }
}

// MARK: - Gated GENERIC types (static-cctor `_payloadSize` crash class)
//
// A generic non-frozen struct (and a generic payload enum) cannot use the
// non-generic lazy `_payloadSize` property — `SwiftObjectHelper<Foo<T>>` in a
// static field initializer crashes Mono's generic sharing — so the generic path
// routes `_payloadSize` through the helper-class metadata accessor wrapped in
// `TypeMetadata.RegisterAndGetSize`, as an EAGER static-field initializer.
// NativeAOT relies on that cctor-time call to register both the metadata cache and
// the NewFromPayload factory, so it must stay eager. But the eager initializer runs
// in the static constructor on the FIRST reference to the closed type — before any
// member guard — so on a host OS below the type's @available floor it would resolve
// metadata that does not exist and abort uncatchably. The fix short-circuits ONLY
// the native accessor below the floor while keeping the registration eager.
//
// These fixtures exercise both arms on a real simulator/device:
//   * a SATISFIED floor (iOS 15) must still round-trip — proving the eager
//     registration arm survived the restructuring (no Mono/NativeAOT regression);
//   * a FUTURE floor (iOS 99) reached through a closed generic's static member runs
//     the static constructor on an OS below the floor (the simulator is itself below
//     iOS 99), so the cctor must complete WITHOUT the native call and the member's
//     own guard must then throw a catchable exception rather than the process crashing.

/// Generic non-frozen struct gated to a floor every current OS already satisfies.
/// Built and read back through (also-gated) Swift-side producers/consumers so the
/// closed `CurrentlyGatedBuffer<Int32>` round-trips without C# constructing the
/// generic directly — proving the eager-registration arm of the gated-generic
/// `_payloadSize` still works on the running OS.
@available(iOS 15.0, macOS 12.0, tvOS 15.0, macCatalyst 15.0, *)
public struct CurrentlyGatedBuffer<T> {
    public let value: T
    public init(value: T) {
        self.value = value
    }
}

@available(iOS 15.0, macOS 12.0, tvOS 15.0, macCatalyst 15.0, *)
public func makeCurrentlyGatedInt32Buffer(_ value: Int32) -> CurrentlyGatedBuffer<Int32> {
    return CurrentlyGatedBuffer<Int32>(value: value)
}

@available(iOS 15.0, macOS 12.0, tvOS 15.0, macCatalyst 15.0, *)
public func currentlyGatedInt32BufferValue(_ buffer: CurrentlyGatedBuffer<Int32>) -> Int32 {
    return buffer.value
}

/// Generic payload enum gated to a satisfied floor — the EnumHandler sibling of the
/// struct above (its generic `_payloadSize` flows through the same helper-PInvoke +
/// RegisterAndGetSize path). Round-trips a payload through (gated) producers/consumers.
@available(iOS 15.0, macOS 12.0, tvOS 15.0, macCatalyst 15.0, *)
public enum CurrentlyGatedPayloadBox<T> {
    case empty
    case filled(T)
}

@available(iOS 15.0, macOS 12.0, tvOS 15.0, macCatalyst 15.0, *)
public func makeCurrentlyGatedInt32PayloadBox(_ value: Int32) -> CurrentlyGatedPayloadBox<Int32> {
    return .filled(value)
}

@available(iOS 15.0, macOS 12.0, tvOS 15.0, macCatalyst 15.0, *)
public func currentlyGatedInt32PayloadBoxValue(_ box: CurrentlyGatedPayloadBox<Int32>) -> Int32 {
    if case let .filled(v) = box {
        return v
    }
    return -1
}

/// Generic non-frozen struct gated to a floor no current OS satisfies, with a static
/// member reachable on the closed type. Calling `FutureGatedBuffer<Int32>.futureStatic()`
/// forces the type's static constructor — and thus the `_payloadSize` initializer — to run
/// on a host OS below the floor (every real simulator/device is below iOS 99). The cctor
/// must complete without resolving metadata that does not exist, after which the static
/// member's own runtime guard throws a catchable exception instead of the process crashing.
@available(iOS 99.0, macOS 99.0, tvOS 99.0, macCatalyst 99.0, *)
public struct FutureGatedBuffer<T> {
    public let value: T
    public init(value: T) {
        self.value = value
    }

    public static func futureStatic() -> Int32 {
        return 99
    }
}
