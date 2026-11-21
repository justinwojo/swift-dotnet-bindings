// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A borrowed class-bound existential must outlive the call
//
// A Swift function borrows its `any P` argument: it takes no reference of its own, so the caller
// has to keep the container's payload alive until the call returns. A plain C# implementation of
// the protocol reaches Swift through the proxy the binding wraps it in, and that proxy holds the
// conformer box's only reference. If nothing managed refers to the proxy during the call, a
// collection there finalizes it, the box is released, and Swift goes on using a freed object.
//
// `midCall()` dispatches back into the conformer, so a C# conformer can force a collection while
// Swift is still inside the call. The weak reference taken before it reports whether the box
// survived: it reads nil once the box has been destroyed, rather than leaving the test to depend on
// a use-after-free crashing.
//
// The `String?` and `(any Error)?` keep the function on the optional-argument wrapper, which stays on
// the Swift convention: the direct call the binding has to pin itself. The two-argument sibling goes
// through the C-convention wrapper and is the parity control.

/// A class-bound protocol a C# conformer can implement, with a call back into the conformer.
public protocol CallLifetimeProbe: AnyObject {
    var probeTag: Int32 { get }
    func midCall()
}

/// A Swift conformer, which C# boxes fresh for each call (a box the borrowing callee never releases).
public final class CallLifetimeProbeBox: CallLifetimeProbe {
    public let probeTag: Int32

    public init(probeTag: Int32) {
        self.probeTag = probeTag
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }

    public func midCall() {}
}

/// Reads the probe after calling back into it. Returns -1 when the conformer box was destroyed during
/// the call back.
public func probeTagAcrossCall(_ probe: any CallLifetimeProbe, label: String?, underlying: (any Error)?) -> Int32 {
    weak let watch: (any CallLifetimeProbe)? = probe
    probe.midCall()
    guard let alive = watch else { return -1 }
    return alive.probeTag + Int32(label?.count ?? 0) + ((underlying as? TrackedError)?.tag ?? 0)
}

/// The same read through the C-convention wrapper.
public func probeTagAcrossCall(_ probe: any CallLifetimeProbe, label: String) -> Int32 {
    weak let watch: (any CallLifetimeProbe)? = probe
    probe.midCall()
    guard let alive = watch else { return -1 }
    return alive.probeTag + Int32(label.count)
}

/// The same read as an instance method, which passes `self` beside the borrowed probe.
public final class CallLifetimeProbeReader {
    public let offset: Int32

    public init(offset: Int32) {
        self.offset = offset
    }

    public func read(_ probe: any CallLifetimeProbe, label: String?, underlying: (any Error)?) -> Int32 {
        weak let watch: (any CallLifetimeProbe)? = probe
        probe.midCall()
        guard let alive = watch else { return -1 }
        return offset + alive.probeTag + Int32(label?.count ?? 0)
    }
}
