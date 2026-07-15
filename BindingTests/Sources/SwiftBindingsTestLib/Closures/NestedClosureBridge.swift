// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Fixtures for the NestedClosureBridge (NCB) emitter: methods whose outer-closure parameters
// take an inner closure. These exercise the multi-outer path — a single Swift wrapper must
// cover all outer closures (not one wrapper per outer).

// Lock-guarded live count for the escaping inner-box ownership canary. The canary is
// captured by an inner Swift closure whose only post-call retain is the +1 AnyObject box
// minted in the generated outer adapter, so a deinit here fires exactly when that box's
// last retain is dropped.
private final class NCBInnerCanaryCount {
    static let lock = NSLock()
    static var live: Int32 = 0
}

private final class NCBInnerCanary {
    init() {
        NCBInnerCanaryCount.lock.lock()
        NCBInnerCanaryCount.live += 1
        NCBInnerCanaryCount.lock.unlock()
    }

    deinit {
        NCBInnerCanaryCount.lock.lock()
        NCBInnerCanaryCount.live -= 1
        NCBInnerCanaryCount.lock.unlock()
    }
}

public class NestedClosureHost {
    public init() {}

    // Single outer closure with a nested completion — baseline.
    public func runOne(handler: @escaping (Int32, @escaping (Int32) -> Void) -> Void) {
        handler(7) { inner in
            _ = inner
        }
    }

    // Non-escaping OUTER closure (drop the outer `@escaping`; the inner completion stays
    // `@escaping`). Exercises the NCB non-escaping-outer GCHandle-free regression (Theme C):
    // the outer delegate's GCHandle must be freed in `finally` even though the outer isn't
    // escaping. Pre-fix the try/finally was gated on `anyEscaping`, so a method whose only
    // outer closure is non-escaping emitted no finally and leaked the handle. The handle is
    // freed synchronously in C# (no owner-token box), so this verifies on the simulator too.
    public func runNonEscapingOuter(handler: (Int32, @escaping (Int32) -> Void) -> Void) {
        handler(7) { inner in
            _ = inner
        }
    }

    // Two outer closures, each with its own nested completion — exercises multi-outer support.
    // Each outer fires its nested completion with a distinct value so the test can verify
    // both paths reach the managed side, with the correct arguments, in the correct order.
    public func runTwo(
        first: @escaping (Int32, @escaping (Int32) -> Void) -> Void,
        second: @escaping (Int32, @escaping (Int32) -> Void) -> Void
    ) {
        first(10) { firstInner in
            _ = firstInner
        }
        second(20) { secondInner in
            _ = secondInner
        }
    }

    // Number of inner-closure deinit canaries currently alive (see NCBInnerCanary below).
    public static func countLiveInnerCanaries() -> Int32 {
        NCBInnerCanaryCount.lock.lock()
        defer { NCBInnerCanaryCount.lock.unlock() }
        return NCBInnerCanaryCount.live
    }

    // Same shape as runOne, but the ESCAPING inner completion captures a deinit canary.
    // After this method returns, the only retain keeping the inner Swift closure — and
    // therefore the canary — alive is the +1 AnyObject box the generated outer adapter
    // minted (Unmanaged.passRetained). The managed side adopts that +1 in a finalizable
    // owner captured by the inner delegate, so countLiveInnerCanaries() dropping back to
    // baseline after the delegate is collected is the observable proof the ownership
    // transfer released the box instead of leaking it.
    public func runEscapingInnerCanary(handler: @escaping (Int32, @escaping (Int32) -> Void) -> Void) {
        let canary = NCBInnerCanary()
        handler(7) { inner in
            _ = inner
            _ = canary
        }
    }

    // Three outer closures, three nested completions — stress the wrapper's shared shape.
    public func runThree(
        first: @escaping (Int32, @escaping (Int32) -> Void) -> Void,
        second: @escaping (Int32, @escaping (Int32) -> Void) -> Void,
        third: @escaping (Int32, @escaping (Int32) -> Void) -> Void
    ) {
        first(100) { firstInner in
            _ = firstInner
        }
        second(200) { secondInner in
            _ = secondInner
        }
        third(300) { thirdInner in
            _ = thirdInner
        }
    }
}
