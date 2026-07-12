// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Closures inside an inheritance-constrained extension on a generic class parent.
//
// This is the "closed-instantiation" shape: the enclosing extension is constrained
// `where Base: ConcreteClass`, so the natural closed receiver `Wrapper<ConcreteClass>`
// is a fully concrete type. A non-generic `@_cdecl` wrapper over that concrete
// receiver produces a real, callable symbol — unlike the open-generic
// `@_silgen_name` bridge, whose entry points are not exported for generic parents.
//
// The completion callbacks are modeled as SEPARATE success/failure closures (not a
// `Result<_, ConcreteError>`), which the standard `@_cdecl` closure path already
// marshals — this deliberately mirrors the Kingfisher `setImage` "twin" shape while
// staying inside the supported closure-argument set.

/// A concrete host class used as the inheritance-constraint anchor. Same-module
/// Swift class → resolves as a C# class bound (`Kind == Class`, same module).
public final class PixelHost {
    public var pixels: Int32
    /// Stores a truly-escaping closure so a later call can invoke it — exercises the
    /// GCHandle-survives-past-return ownership transfer. Internal (not public API); an
    /// extension cannot itself add stored properties, so it lives on the base class.
    var pendingCallback: ((Int32) -> Void)?
    public init(pixels: Int32) {
        self.pixels = pixels
    }
}

/// A second concrete anchor, to exercise cross-concrete overloads
/// (`Wrapper<PixelHost>` vs `Wrapper<GlyphHost>` are distinct C# receivers).
public final class GlyphHost {
    public var glyphs: Int32
    public init(glyphs: Int32) {
        self.glyphs = glyphs
    }
}

/// Generic CLASS wrapper — the closed-instantiation parent. A class (not a struct)
/// so the concrete `@_cdecl` reconstructs self via `Unmanaged.fromOpaque`.
public final class HostWrapper<Base> {
    public let base: Base
    public init(_ base: Base) {
        self.base = base
    }
}

/// Non-generic factory so C# can obtain a live `HostWrapper<PixelHost>` /
/// `HostWrapper<GlyphHost>` without constructing a generic class directly.
public enum HostWrapperFactory {
    public static func wrap(_ host: PixelHost) -> HostWrapper<PixelHost> {
        HostWrapper(host)
    }

    public static func wrap(_ host: GlyphHost) -> HostWrapper<GlyphHost> {
        HostWrapper(host)
    }
}

extension HostWrapper where Base: PixelHost {
    /// The `setImage` twin: a primitive parameter plus success/failure completion
    /// closures. Escaping, `(Int32) -> Void`. Skipped today with
    /// `GenericTypeCallback`; the closed-specialization path emits it as a concrete
    /// `@_cdecl` extension method on `HostWrapper<PixelHost>`.
    public func loadPixels(
        scaleBy factor: Int32,
        onSuccess: @escaping (Int32) -> Void,
        onFailure: @escaping (Int32) -> Void
    ) {
        if factor > 0 {
            onSuccess(base.pixels * factor)
        } else {
            onFailure(-1)
        }
    }

    /// A second method on the SAME parent/concrete, a distinct shape (one closure,
    /// a primitive non-closure arg) — exercises the overload-safe wrapper-symbol
    /// identity (mangled-hash, not name-only) and single-closure marshalling.
    public func describe(
        bump: Int32,
        onDone: @escaping (Int32) -> Void
    ) {
        onDone(base.pixels &+ bump)
    }

    /// Stores the escaping closure and returns WITHOUT invoking it. The closure must
    /// survive past this call's return (its GCHandle is transferred to the Swift box, not
    /// freed by the caller) so a later `fireArmed` can invoke it — the real escaping test.
    public func armCallback(onEvent: @escaping (Int32) -> Void) {
        base.pendingCallback = onEvent
    }

    /// Invokes the previously-stored (escaped) closure, then acknowledges via a fresh
    /// closure. Proves the stored GCHandle was still live after `armCallback` returned.
    public func fireArmed(
        scaleBy factor: Int32,
        onAck: @escaping (Int32) -> Void
    ) {
        base.pendingCallback?(base.pixels * factor)
        onAck(1)
    }
}

extension HostWrapper where Base: GlyphHost {
    /// Same method name as the `PixelHost` extension but a different concrete
    /// receiver — proves cross-concrete overloads do not collide.
    public func loadPixels(
        scaleBy factor: Int32,
        onSuccess: @escaping (Int32) -> Void,
        onFailure: @escaping (Int32) -> Void
    ) {
        onSuccess(base.glyphs &+ factor)
    }
}
