// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Fixtures for the NestedClosureBridge (NCB) emitter: methods whose outer-closure parameters
// take an inner closure. These exercise the multi-outer path — a single Swift wrapper must
// cover all outer closures (not one wrapper per outer).

public class NestedClosureHost {
    public init() {}

    // Single outer closure with a nested completion — baseline.
    public func runOne(handler: @escaping (Int32, @escaping (Int32) -> Void) -> Void) {
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
