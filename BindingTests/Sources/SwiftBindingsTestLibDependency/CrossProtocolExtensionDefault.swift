// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// `extension P where Self : Q { ... }` is how Swift supplies a default witness for one of Q's
// requirements to every type that conforms to both P and Q. The member is spelled on P, which does
// not inherit Q, so keying the default by the extended protocol alone leaves Q's requirement
// looking unsatisfiable: it is emitted as an abstract C# interface member, and a conforming type
// that relies on the default — having no member of its own in the ABI — cannot implement it.
//
// Nothing in THIS module conforms to `SectionTagging`. That is the point: the phantom-default
// detector infers a hidden default only when a same-module conformer exists and cannot emit the
// member, so with no conformer at all it stays inert and the requirement reaches C# abstract. The
// conformers live downstream, in the main test library, which is where the missing member lands.

/// Mirrors the "capability" protocol whose single requirement is satisfied for every conformer by a
/// constrained extension on a *different* protocol.
public protocol SectionTagging {
    var sectionTag: Int32 { get }
}

/// Mirrors the "modeled" protocol that carries the default. Conforming to it is what earns a type
/// the `sectionTag` witness. It carries a requirement of its own so that it is not a
/// requirement-less protocol, which is a separate emission shape with its own known divergence.
public protocol TaggedModel {
    var modelLabel: Int32 { get }
}

extension TaggedModel where Self: SectionTagging {
    public var sectionTag: Int32 { 7 }
}

/// Reverse dispatch across the module boundary: proves the downstream conformer still claims
/// `SectionTagging` and that Swift dispatches the constrained-extension default for it.
public func readSectionTag(_ value: any SectionTagging) -> Int32 {
    value.sectionTag
}
