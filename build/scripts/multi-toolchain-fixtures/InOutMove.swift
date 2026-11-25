// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A control fixture of the multi-toolchain package gate (build/scripts/multi-toolchain-package.py,
// control M11): an `int` forwarder whose overload has an `inout` parameter. `step(count: Int, seed:)`
// projects to `Step(nint, ref nint)` with the forwarder `Step(int, ref nint)`; the newer build adds
// `step(count: Int32, seed:)`, which projects to `Step(int, ref nint)` and takes the name from the
// forwarder. Both copies carry the same API, so only their Swift declaration records tell them apart.

public final class Stepper {
    public init() {}

#if compiler(>=6.4)
    public func step(count: Int32, seed: inout Int) { seed += Int(count) * 2 }
#endif

    public func step(count: Int, seed: inout Int) { seed += count }
}
