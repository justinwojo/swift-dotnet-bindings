// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A control fixture of the multi-toolchain package gate (build/scripts/multi-toolchain-package.py,
// control M14): a conformer's member that shadows a protocol extension's. `Host` conforms to
// `Greeter`, whose extension declares `greet()`, so the older binding emits `Host.GetGreet()` calling
// the extension's declaration. The newer build gives `Host` its own `greet()`, which projects to the
// same `Host.GetGreet()` and calls another Swift declaration. The newer copy also drops the older
// copy's wrapper export for the extension member, which the merge refuses on its own; the control
// requires the Swift declaration record's moved-name diagnostic, which only the record can give.

public protocol Greeter {
    var name: String { get }
}

extension Greeter {
    public func greet() -> Int32 { Int32(name.count) }
}

public final class Host: Greeter {
    public init() {}

    public var name: String { "host" }

#if compiler(>=6.4)
    public func greet() -> Int32 { Int32(name.count) * 2 }
#endif
}
