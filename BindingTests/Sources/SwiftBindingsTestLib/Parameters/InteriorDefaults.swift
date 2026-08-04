// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Interior default parameters
//
// A C# optional parameter must be trailing, so a Swift default that sits BEFORE a required
// parameter can never survive as `= value` in the projected signature. Trimming only the
// trailing run cannot reach it either. The generator therefore emits an extra "all-defaults"
// overload that omits every defaulted parameter, interior ones included; the Swift shim calls
// the real declaration WITHOUT those arguments, so Swift evaluates its own default expressions.
// The tests assert the default's effect is visible in the returned value.

/// One interior default, one required parameter after it.
/// Expected overloads: `FormatInterior(prefix, label, suffix)` and `FormatInterior(prefix, suffix)`.
public func formatInterior(prefix: String, label: String = "mid", suffix: String) -> String {
    return "\(prefix)|\(label)|\(suffix)"
}

/// Two interior defaults interleaved with required parameters, plus a trailing default whose
/// literal IS C#-expressible — so the primary carries `= 9` inline while the all-defaults form
/// still has to omit `b` and `d` together.
public final class InteriorDefaultBox {
    public init() {}

    public func describe(a: Int32, b: Int32 = 7, c: Int32, d: Int32 = 9) -> String {
        return "a=\(a) b=\(b) c=\(c) d=\(d)"
    }
}

/// More trailing defaults than the per-method overload cap allows, none of them C#-expressible.
/// Trimming alone stops well short of the shortest callable form; the all-defaults overload is
/// the only way a consumer can call this with just the one argument Swift actually requires.
public func capStranded(
    root: String,
    t1: String = String(repeating: "auto", count: 1),
    t2: String = String(repeating: "auto", count: 1),
    t3: String = String(repeating: "auto", count: 1),
    t4: String = String(repeating: "auto", count: 1),
    t5: String = String(repeating: "auto", count: 1)
) -> String {
    return "\(root)/\(t1)\(t2)\(t3)\(t4)\(t5)"
}

/// Fail-closed shape on the SWIFT side: two declarations share one base name and differ ONLY in the
/// type of a defaulted parameter — the shape a logging library's `assert(_:_:level:…)` family takes.
/// Both have more trailing defaults than the overload cap, so both would otherwise get an
/// all-defaults form whose shim omits every defaulted argument; that reduced call, `annotate(subject)`,
/// fits BOTH declarations and swiftc rejects it as an ambiguous use. The wrapper library compiles as
/// a unit, so one such shim fails every binding in the module rather than just this member. No
/// all-defaults form is synthesized for either declaration; the trimmed overloads all keep `note`,
/// which is what tells the two apart.
public func annotate(
    _ subject: String,
    note: Int32 = 0,
    t1: String = String(repeating: "auto", count: 1),
    t2: String = String(repeating: "auto", count: 1),
    t3: String = String(repeating: "auto", count: 1),
    t4: String = String(repeating: "auto", count: 1)
) -> String {
    return "n=\(note) \(subject)/\(t1)\(t2)\(t3)\(t4)"
}

public func annotate(
    _ subject: String,
    note: String = "auto",
    t1: String = String(repeating: "auto", count: 1),
    t2: String = String(repeating: "auto", count: 1),
    t3: String = String(repeating: "auto", count: 1),
    t4: String = String(repeating: "auto", count: 1)
) -> String {
    return "s=\(note) \(subject)/\(t1)\(t2)\(t3)\(t4)"
}

/// An UNLABELED interior default followed by a LABELED parameter is still omissible: the label pins
/// its own argument no matter what was dropped ahead of it, so `unlabeledInteriorDefault(second: 6)`
/// resolves and Swift evaluates `first`'s default.
public func unlabeledInteriorDefault(_ first: Int32 = 1, second: Int32) -> String {
    return "first=\(first) second=\(second)"
}

/// Fail-closed shape: the defaulted parameter is UNLABELED and the kept parameter after it is
/// UNLABELED too, so omitting the default leaves the remaining argument to be matched positionally
/// and Swift cannot tell which slot it fills. No all-defaults overload is synthesized for this; the
/// full form is the only one emitted.
public func unlabeledPositionalDefault(_ first: Int32 = 1, _ second: Int32) -> String {
    return "first=\(first) second=\(second)"
}

// MARK: - Overload-set validity (CS0121)
//
// Two members can each be individually valid and still leave a consumer unable to compile a call
// that reaches both. `resolve(id:tag:)` has a non-expressible default, so the generator wants to
// synthesize a trimmed `ResolveAsync(nint, CancellationToken = default)`; `resolve(id:retries:)`
// already projects to `ResolveAsync(nint, nint = 3, CancellationToken = default)`. A call site
// passing one argument binds both equally well — neither supplies every parameter — which is
// CS0121 at the CALLER, invisible to any gate that only compiles the binding itself. The trimmed
// candidate is declined; the test below compiles the one-argument call.

public final class AsyncDefaultLattice {
    public init() {}

    /// Declared first, so its projected shape is reserved before the sibling below synthesizes
    /// its trimmed overload.
    public func resolve(id: Int32, retries: Int32 = 3) async -> String {
        return "id=\(id) retries=\(retries)"
    }

    public func resolve(id: Int32, tag: String = String(repeating: "auto", count: 1)) async -> String {
        return "id=\(id) tag=\(tag)"
    }
}

/// The same set-validity question asked of a DIFFERENT producer: the Task-returning convenience
/// overload synthesized for a completion-handler method. `load(_:completion:)` wants to add
/// `LoadAsync(int, CancellationToken = default)`; the sibling `load(_:_:) async` already projects to
/// `LoadAsync(int, int = 3, CancellationToken = default)`. A caller passing one argument supplies
/// every parameter of neither, so the two bind equally well — CS0121 at the CALLER, which is
/// invisible to a gate that only compiles the binding itself. The convenience overload is the side
/// that yields; both primaries stay.
public final class CompletionAsyncLattice {
    public init() {}

    /// Declared first, so its projected shape is reserved before the completion-handler lane
    /// synthesizes its own `LoadAsync`.
    public func load(_ id: Int32, _ retries: Int32 = 3) async -> Int32 {
        return id + retries
    }

    public func load(_ id: Int32, completion: @escaping (Int32) -> Void) {
        completion(id)
    }
}
