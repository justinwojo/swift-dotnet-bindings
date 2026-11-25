// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A control fixture of the multi-toolchain package gate (build/scripts/multi-toolchain-package.py,
// control M9): the binding renames a nested type named like its container (`Outer.outer` becomes
// `Outer.OuterInfo`, since C# forbids a member named like its enclosing type), and the newer build adds
// a sibling whose own name is `OuterInfo`. That sibling takes the name and `outer` moves to
// `OuterInfo2`, so the two copies have the same C# names standing for different Swift declarations,
// which the merge must refuse.

public final class Outer {
    public init() {}

    public final class outer {
        public init() {}

        public var ping: String { "outer" }
    }

#if compiler(>=6.4)
    public final class outerInfo {
        public init() {}

        public var ping: String { "outerInfo" }
    }
#endif
}
