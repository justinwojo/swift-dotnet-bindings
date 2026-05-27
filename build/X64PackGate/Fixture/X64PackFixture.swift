// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Fixture library for the x86_64 packaging + Rosetta runtime gate (nuke X64PackGate).
//
// Unlike build/x64-thunk-gate/Fixture.swift (which proves the bare thunk ABI via
// manual cdecl P/Invokes), this fixture is consumed through the *generated*
// bindings + Swift.Runtime, so it must exercise the idiomatic runtime surface the
// thunk gate intentionally skips:
//   - a class round-trip (ARC retain/release of a Swift object handle),
//   - a String round-trip (SBW_Utf8Slice marshalling in both directions),
//   - a by-value integer method,
//   - a top-level function.
// A correct round-trip from an osx-x64 process under Rosetta proves the packaged
// fat wrapper loads and the generated bindings drive it on x86_64.
public final class Greeter {
    private let prefix: String
    public init(prefix: String) { self.prefix = prefix }
    public func greet(_ name: String) -> String { return "\(prefix), \(name)!" }
    public func sum(_ a: Int32, _ b: Int32) -> Int32 { return a &+ b }
}

public func describe(_ n: Int32) -> String { return "n=\(n)" }
