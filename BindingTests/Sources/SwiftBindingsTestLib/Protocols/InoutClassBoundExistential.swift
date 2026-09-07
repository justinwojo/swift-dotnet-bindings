// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - `inout` existential parameters
//
// A class-constrained protocol passed BY VALUE lowers to a loadable two-word
// [classRef][witnessTable] pair in two registers, which is what the emitter's
// ClassExistentialContainer1 carrier models. Passed `inout`, it lowers to something
// different: a SINGLE pointer to the caller's mutable storage. `swiftc -emit-ir` on
// this exact shape shows the contrast directly —
//
//   takesByValue(any ClassBoundP)  -> define swiftcc void @…(ptr %0, ptr %1)
//   takesInout(inout any ClassBoundP) -> define swiftcc void @…(ptr %0)
//
// — and the opaque (non-class-constrained) existential behaves the same way under
// `inout`. Neither the two-word pair nor the five-word opaque container is the right
// thing to hand a callee that expects a storage address, and the mismatch is invisible
// to both compilers: the emitted C# compiles, and the Swift side is never rebuilt
// against it. Beyond the wire shape there is no write-back path either — Swift may
// store a different object into the slot, which a correct binding would have to read
// back and re-project without destroying the caller's reference identity when it
// didn't.
//
// So these members are refused rather than bound, and this fixture is what keeps the
// refusal honest: it puts every `inout` existential flavor permanently in the corpus so
// the decision is re-checked on every generation instead of resting on the absence of
// an input.

/// Class-constrained (AnyObject) protocol — the flavor whose by-value lowering is the
/// compact two-word pair, and whose `inout` lowering is not.
public protocol MutableSlotHolder: AnyObject {
    var slotLabel: String { get }
}

/// Unconstrained protocol — the opaque-container flavor, present so the refusal is
/// pinned across both existential layouts rather than only the class-bound one.
public protocol OpaqueSlotHolder {
    var slotLabel: String { get }
}

public final class NamedSlotHolder: MutableSlotHolder {
    public let slotLabel: String
    public init(slotLabel: String) { self.slotLabel = slotLabel }
}

@frozen
public struct TaggedSlot: OpaqueSlotHolder {
    public let slotLabel: String
    public init(slotLabel: String) { self.slotLabel = slotLabel }
}

/// Free function taking a class-bound existential `inout`. The member the reviewer's
/// repro named: `MethodWrapperEmitter` refuses it a `@_cdecl` wrapper (the inout ABI
/// mismatch gate), so before the refusal it fell through to the direct CallConvSwift
/// P/Invoke and declared the two-word pair by value.
public func retagSlotHolder(_ holder: inout any MutableSlotHolder) {
    holder = NamedSlotHolder(slotLabel: "retagged:" + holder.slotLabel)
}

/// Opaque-existential sibling of the above.
public func retagOpaqueSlotHolder(_ holder: inout any OpaqueSlotHolder) {
    holder = TaggedSlot(slotLabel: "retagged:" + holder.slotLabel)
}

/// Instance-method carrier — the free-function and instance paths build their P/Invoke
/// parameter lists through the same emitter, but the instance form is the one a real
/// framework surfaces, so both are pinned.
public final class SlotRetagger {
    public let prefix: String
    public init(prefix: String) { self.prefix = prefix }

    public func retag(_ holder: inout any MutableSlotHolder) {
        holder = NamedSlotHolder(slotLabel: prefix + holder.slotLabel)
    }

    /// Positive control: the SAME protocol in a by-value position on the SAME type must
    /// keep binding. Without this the fixture could go green by the whole type dropping
    /// out, which would hide a refusal that had grown too wide.
    public func describe(_ holder: any MutableSlotHolder) -> String {
        return prefix + holder.slotLabel
    }
}
