// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// What a generated surface contributes to the binary interface — the half of the safe-to-drop
/// question that asks "does removing this change anything the other side already agreed to?".
/// </summary>
/// <remarks>
/// <para>
/// The complementary half is the consumer capability the surface promises, which lives on the
/// recovery graph as <c>Requires</c> edges. A removal is safe only when it alters no retained
/// footprint <em>and</em> leaves no retained capability with an unsatisfied obligation.
/// </para>
/// <para>
/// These bits describe what a surface contributes, not who owns it. Owning a positional slot is
/// fine — a reverse-conformance capability owns all of its vtable slots and can be withdrawn whole.
/// Contributing bytes or an index to <em>someone else's</em> layout is the unsafe case, and that is
/// recorded separately as <see cref="RecoveryClassification.ContributesToParentLayout"/>, because
/// the bits alone cannot tell the two apart.
/// </para>
/// </remarks>
[Flags]
public enum AbiFootprint
{
    /// <summary>Contributes nothing to the binary interface — pure managed convenience.</summary>
    None = 0,

    /// <summary>Exports or imports a native symbol (a P/Invoke entry point, a wrapper function).</summary>
    Symbol = 1 << 0,

    /// <summary>Contributes bytes to a type's in-memory layout — size, alignment, or field offset.</summary>
    Representation = 1 << 1,

    /// <summary>Occupies a positional witness-table or vtable index.</summary>
    VtableSlot = 1 << 2,

    /// <summary>Contributes type metadata or a conformance descriptor.</summary>
    Metadata = 1 << 3,

    /// <summary>Contributes retain/release, ownership transfer, or destruction semantics.</summary>
    Ownership = 1 << 4,

    /// <summary>
    /// The footprint is not declared. Reserved for artifact kinds with no recovery rule; treated as
    /// "assume it contributes something" so an unclassified surface is never quietly dropped.
    /// </summary>
    Unknown = 1 << 5,
}
