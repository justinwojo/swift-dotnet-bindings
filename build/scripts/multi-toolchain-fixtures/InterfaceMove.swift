// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A control fixture of the multi-toolchain package gate (build/scripts/multi-toolchain-package.py,
// control M10): members of a C# interface whose name a newer sibling takes. The two subscripts of
// `Indexed` both project to the indexer `this[nint]`, so only one is emitted; the newer build declares
// `a:` ahead of `b:`, and the indexer that stood for `subscript(b:)` stands for `subscript(a:)`. The
// `Int` requirement of `Skipping` gets an `int` default-interface forwarder `Skip(int)`; the newer
// build adds an `Int32` requirement that projects to `Skip(int)` and takes the name from the
// forwarder. Both copies carry the same API, so only their Swift declaration records tell them apart.

public protocol Indexed {
#if compiler(>=6.4)
    subscript(a a: Int) -> String { get }
#endif
    subscript(b b: Int) -> String { get }
}

public protocol Skipping {
#if compiler(>=6.4)
    func skip(count: Int32) -> Int
#endif
    func skip(count: Int) -> Int
}
