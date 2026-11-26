// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A control fixture of the multi-toolchain package gate (build/scripts/multi-toolchain-package.py,
// control M12): a protocol nested in a type beside a top-level protocol of the same name. `Outer.Listener`
// emits as the nested interface `Outer.IListener` and `Listener` as `IListener`, and each `Int`
// requirement gets an `int` default-interface forwarder `OnEvent(int)`. The newer build adds an
// `Int32` requirement to the nested protocol only, which projects to `OnEvent(int)` and takes the name
// from its forwarder. The record must key the nested interface by its nesting: keyed by its leaf, both
// interfaces write `IListener.OnEvent(int)`, and the top-level forwarder can hide the nested move.

public final class Outer {
    public init() {}

    public protocol Listener {
#if compiler(>=6.4)
        func onEvent(value: Int32) -> Int
#endif
        func onEvent(value: Int) -> Int
    }
}

public protocol Listener {
    func onEvent(value: Int) -> Int
}
