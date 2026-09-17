// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// One Swift declaration may use the same external argument label twice, as long as the internal
// names differ — `inSection source:` alongside `inSection destination:`. The ABI JSON records only
// the labels, so both parameters reached the emitters under one name: the generated Swift
// introduced `inSection` twice (and derived two `inSectionCopy` locals from it), which is an
// invalid redeclaration that fails the whole wrapper module rather than one member.
//
// The repeats are spelled label-only (`inSection _:`) on purpose. That is the shape the emitters
// see for any library we ingest from its ABI JSON alone, and unlike `inSection source:` it stays
// nameless even when a `.swiftinterface` supplement is available to recover private names — so the
// collision is reproduced here no matter which ingestion path runs.

public protocol SectionMoving {
    func move(item: Int32, inSection _: Int32, toItem: Int32, inSection _: Int32) -> Int32
}

/// Reverse dispatch: Swift calls a C# conformer with two same-labelled arguments. Each one carries
/// a distinguishable magnitude, so a witness that merged the repeats — which would compile once the
/// names were merely made unique — still reports the wrong number.
public func applySectionMove(_ mover: any SectionMoving) -> Int32 {
    mover.move(item: 1, inSection: 20, toItem: 3, inSection: 400)
}

/// Forward dispatch: a method on a GENERIC class routes through a private dispatch protocol, whose
/// requirement is declared by a different emitter from the same parameter facts. That emitter names
/// an unnamed parameter positionally, so this shape does not collide there today — it holds the
/// second emitter to the same outcome, that a declaration repeating a label still presents all
/// three parameters to C# and passes each one through, rather than collapsing the repeats into one.
/// (The collision itself is reachable in that emitter only from ABI JSON that carries no parameter
/// names at all, which no Swift source can produce here; a unit test covers it.)
///
/// Neither repeat can be read in the body — both are unnamed — so the value assertion rides on the
/// middle parameter, and the arity is what proves both repeats still bind.
public final class RepeatedLabelReporter<Item> {
    public init() {}

    public func describe(inSection _: Int32, toItem middle: Int32, inSection _: Int32) -> Int32 {
        middle
    }
}
