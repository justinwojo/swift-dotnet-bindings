// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Read-only `mutating get` requirement (no setter)
//
// Empirically validates that the WitnessDispatchEmitter's
// RequiresMutableExistentialBinding gate detects `var foo: T { mutating get }`
// on a non-class-bound protocol even when no setter is declared. The gate
// reads the parser's IsMutating bit (set via the dual SwiftABIParser signal:
// DeclAttributes "Mutating" OR funcSelfKind == "Mutating"); if that signal
// were lost, the generated witness-dispatch wrapper would emit
// `let boxed = containerPtr.load(as: (any …).self)` and swiftc would reject
// the subsequent `boxed.snapshot` call (`cannot use mutating getter on
// immutable value: 'boxed' is a 'let' constant`). The mere presence of this
// fixture in the test library — combined with the static strip-count gate
// staying at baseline after regen — is the empirical proof.

/// Witness-dispatched protocol with a read-only `mutating get` requirement
/// and no setter. Forces the gate's `getMutating` branch to fire on its own.
public protocol BugReproMutatingGetterOnlyProvider {
    var snapshot: Int32 { mutating get }
}

/// Value-type conformer whose `mutating get` mutates a backing counter.
public struct BugReproMutatingGetterOnlyStruct: BugReproMutatingGetterOnlyProvider {
    private var calls: Int32

    public init(initial: Int32 = 0) { self.calls = initial }

    public var snapshot: Int32 {
        mutating get {
            calls += 1
            return calls
        }
    }
}

/// Exposes a fresh existential conformer so the C# side can exercise the
/// generated witness wrapper at runtime if a runtime test is added later.
public func bugReproMakeMutatingGetterOnly() -> any BugReproMutatingGetterOnlyProvider {
    return BugReproMutatingGetterOnlyStruct()
}
