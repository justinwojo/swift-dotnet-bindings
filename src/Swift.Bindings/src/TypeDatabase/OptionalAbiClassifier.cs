// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared ABI truth for how Swift lays out <c>Optional&lt;T&gt;</c>: which payloads
/// genuinely gain a 1-byte discriminator tag versus which fold <c>.none</c> into a
/// spare inhabitant and keep the inner type's size.
///
/// <para>
/// Swift only appends a 1-byte tag to an <c>Optional</c> when the payload uses EVERY
/// bit pattern of its storage — i.e. the fixed-width integer/float scalars
/// (<c>Optional&lt;Int32&gt;</c> is 5 bytes, <c>Optional&lt;Double&gt;</c> is 9). Every
/// other payload — <c>Bool</c>, pointers, class references, enums, structs carrying
/// spare bits — keeps the SAME size under <c>Optional</c> via the spare-bit /
/// extra-inhabitant optimization and must NOT have a tag appended.
/// </para>
///
/// <para>
/// Two oracles consume this truth and MUST agree on it:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="ModuleProcessor.ClassifyFieldType"/> — the field-layout oracle: emits
///     <c>{inner},i1</c> for a tag-adding scalar and declines everything else.
///   </description></item>
///   <item><description>
///     <c>TypeLowering.LowerOptional</c> — the register oracle: adds a 1-byte integer
///     tag slot for a tag-adding scalar and declines everything else (routing to a
///     <c>@_cdecl</c> wrapper, whose managed marshalling layer encodes spare inhabitants).
///   </description></item>
/// </list>
///
/// <para>
/// Centralizing the set here keeps the two oracles from drifting (Finding 44 /
/// Regression-R6 finding 4): before this, the register oracle unconditionally
/// fabricated an over-wide <c>{inner}+tag</c> layout for spare-inhabitant payloads,
/// inflating <c>Optional&lt;Bool&gt;</c>/pointer/enum sizes by a slot and a byte.
/// </para>
/// </summary>
internal static class OptionalAbiClassifier
{
    /// <summary>
    /// Swift fixed-width scalar payloads whose <c>Optional</c> genuinely gains a 1-byte
    /// discriminator tag: they use every bit pattern of their storage, so there is no
    /// spare inhabitant for <c>.none</c> to fold into. <c>Bool</c> and the pointer types
    /// are intentionally EXCLUDED — their <c>Optional</c> keeps the inner type's size via
    /// a spare inhabitant.
    /// </summary>
    private static readonly HashSet<string> s_tagAddingScalars = new()
    {
        "Swift.Int", "Swift.UInt", "Swift.Int64", "Swift.UInt64",
        "Swift.Int32", "Swift.UInt32", "Swift.Int16", "Swift.UInt16",
        "Swift.Int8", "Swift.UInt8",
        "Swift.Float", "Swift.Double",
        "CoreFoundation.CGFloat", "CoreGraphics.CGFloat",
    };

    /// <summary>
    /// Returns true when <c>Optional&lt;<paramref name="swiftTypeName"/>&gt;</c> gains a
    /// 1-byte discriminator tag (the inner type is a fixed-width integer/float scalar).
    /// Returns false for spare-inhabitant payloads (<c>Bool</c>, pointers, class refs,
    /// enums, structs) — their <c>Optional</c> keeps the inner type's size with no tag,
    /// so a tag-appending layout would be too wide.
    /// </summary>
    /// <param name="swiftTypeName">The fully-qualified Swift type name of the Optional's payload (e.g. <c>"Swift.Int32"</c>).</param>
    public static bool HasAppendedOptionalTag(string? swiftTypeName) =>
        !string.IsNullOrEmpty(swiftTypeName) && s_tagAddingScalars.Contains(swiftTypeName!);
}
