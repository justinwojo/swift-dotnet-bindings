// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;

namespace BindingsGeneration;

/// <summary>
/// The single implementation of the Swift value-type inline-layout algorithm for the generator:
/// the spare-bit / extra-inhabitant decision and the inline byte sizes that the field-layout walk,
/// the register-classification walk, and the frozen-struct Buffer emitter all consult so they can
/// never drift apart.
///
/// <para>
/// <b>The spare-bit truth.</b> Swift only appends a 1-byte discriminator tag to an
/// <c>Optional&lt;T&gt;</c> when the payload uses EVERY bit pattern of its storage — i.e. the
/// fixed-width integer/float scalars (<c>Optional&lt;Int32&gt;</c> is 5 bytes,
/// <c>Optional&lt;Double&gt;</c> is 9). Every other payload — <c>Bool</c>, pointers, class
/// references, enums, structs carrying spare bits — folds <c>.none</c> into a spare inhabitant and
/// keeps the SAME size under <c>Optional</c>, with no tag.
/// </para>
///
/// <para>
/// <b>Two consumer semantics, one oracle.</b> The field-layout / register walks
/// (<see cref="ModuleProcessor.ClassifyFieldType"/>, <c>TypeLowering.LowerOptional</c>) DECLINE on
/// ambiguity: they emit a tag-extended layout only for the proven tag-adding scalar set
/// (<see cref="HasAppendedOptionalTag"/>) and route everything else to a <c>@_cdecl</c> wrapper
/// whose managed marshalling encodes spare inhabitants. The frozen-struct Buffer emitter
/// (<c>FrozenStructHandler</c>) instead ALWAYS answers an inline size — from the language-constant
/// primitive table (<see cref="TryGetOptionalPrimitiveInlineSize"/>), live value-witness metadata,
/// or a reference-bearing heuristic (<see cref="TryComputeOptionalInlineSize"/>) — because a Buffer
/// field must be sized to blit. Both consult this one type; the declining consumers ask only the
/// proven-set question and keep declining outside it.
/// </para>
///
/// <para>
/// Before this consolidation the spare-bit decision lived twice — a hash-set membership test here
/// and a near-duplicate value-witness read + <c>== Bool</c> literal inside <c>FrozenStructHandler</c>
/// — and the two had diverged in representation. The register
/// oracle once fabricated an over-wide <c>{inner}+tag</c> layout for spare-inhabitant payloads,
/// inflating <c>Optional&lt;Bool&gt;</c>/pointer/enum sizes by a slot and a byte.
/// </para>
/// </summary>
internal static class SwiftValueLayout
{
    /// <summary>
    /// Swift fixed-width scalar payloads whose <c>Optional</c> genuinely gains a 1-byte
    /// discriminator tag: they use every bit pattern of their storage, so there is no
    /// spare inhabitant for <c>.none</c> to fold into. <c>Bool</c> and the pointer types
    /// are intentionally EXCLUDED — their <c>Optional</c> keeps the inner type's size via
    /// a spare inhabitant. This set and <see cref="s_spareInhabitantPrimitives"/> encode the
    /// one spare-bit truth from opposite sides (tag-adding vs spare-inhabitant), but they are
    /// NOT literal set-complements: this one is qualified-strict (module-prefixed spellings
    /// only) because it backs the decline-on-ambiguity <see cref="HasAppendedOptionalTag"/>,
    /// whereas the spare-inhabitant set also lists the bare spelling for the always-answer
    /// frozen path, and the frozen path's actual fixed-width recognizer
    /// (<see cref="OptionalMarshalClassifier.GetSwiftTagByteOffset"/>) spans a third domain
    /// (it accepts bare integer/float spellings and <c>CoreFoundation</c>/bare <c>CGFloat</c>
    /// but not <c>CoreGraphics.CGFloat</c>).
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
    /// The fixed-width Swift primitives whose <c>Optional</c> folds <c>.none</c> into a spare bit
    /// pattern instead of appending a tag byte — i.e. <c>Optional&lt;T&gt;.size == T.size</c>.
    /// <c>Bool</c> is the lone such primitive: only the bit patterns <c>0</c> and <c>1</c> are valid,
    /// so <c>2..255</c> are spare inhabitants that <c>nil</c> reuses. Every OTHER fixed-width
    /// primitive (the integers and floats) uses its full bit range and therefore gains a tag — those
    /// are <see cref="s_tagAddingScalars"/>, the same spare-bit truth seen from the tag-adding side
    /// (the two sets are NOT literal set-complements — they differ in spelling domain and consumer;
    /// see that field's remarks). This is the single home of the spare-bit/<c>Bool</c> decision for the
    /// always-answer frozen-struct sizing path; both the bare (<c>"Bool"</c>) and module-qualified
    /// (<c>"Swift.Bool"</c>) spelling are listed because that path sees field type names in either
    /// form, whereas the decline-on-ambiguity consumers read the qualified-strict
    /// <see cref="HasAppendedOptionalTag"/> directly.
    /// </summary>
    private static readonly HashSet<string> s_spareInhabitantPrimitives = new()
    {
        "Swift.Bool", "Bool",
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

    /// <summary>
    /// Resolves the inline byte size of a fixed-width Swift primitive value type (Int32, Bool,
    /// Double, Int, CGFloat, …). These sizes are language constants that the cross-compile
    /// TypeDatabase does not persist (the XML primitive records carry no <c>inlineSize</c>) and
    /// for which no live metadata exists at generate time, yet they are needed to size
    /// <c>Optional&lt;primitive&gt;</c> Buffer fields: without this the reference-field fallback
    /// clamps the inner type to a pointer width and <c>Optional&lt;Int32&gt;</c> is mis-sized to
    /// two words. Returns false for any non-primitive (the caller resolves those via
    /// InlineSize/metadata/reference-field rules). Delegates to the single source of truth in
    /// <see cref="OptionalMarshalClassifier.GetSwiftTagByteOffset"/> (the tag byte offset of an
    /// Optional&lt;primitive&gt; is exactly the primitive's size).
    /// </summary>
    internal static bool TryGetFixedWidthPrimitiveSize(TypeSpec spec, out int byteSize)
    {
        byteSize = 0;
        if (spec is NamedTypeSpec named &&
            OptionalMarshalClassifier.GetSwiftTagByteOffset(named.Name) is int size)
        {
            byteSize = size;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves the inline byte size of an <c>Optional&lt;primitive&gt;</c> Buffer field as a
    /// language constant, or returns false for any non-primitive inner (the caller then resolves it
    /// via InlineSize / live metadata / reference-field rules). Most fixed-width primitives use
    /// their full bit range, so they expose no spare pattern for the optional tag and carry a
    /// separate tag byte: <c>Optional&lt;T&gt;.size == T.size + 1</c> (verified Int8?=2, Int16?=3,
    /// Int32?=5, Int?=9, Float?=5, Double?=9). <c>Bool</c> is the lone exception in this set: only
    /// the bit patterns 0 and 1 are valid, so <c>Optional&lt;Bool&gt;</c> reuses a spare pattern for
    /// nil and <c>Optional&lt;Bool&gt;.size == Bool.size == 1</c> (no tag byte).
    ///
    /// The sole present consumer (<c>FrozenStructHandler.EmitIntPtrFields</c>) rounds every field up
    /// to whole 8-byte words, so the only size distinction it observes is the 8-byte-primitive case
    /// (<c>Int?</c>/<c>Int64?</c>/<c>Double?</c>/<c>CGFloat?</c> = 9 bytes ⇒ two words, vs the
    /// historical one-word clamp that under-sized the Buffer). The sub-word Bool/Int8/…/Float
    /// distinctions are therefore presently masked by that rounding — this helper still reports the
    /// true Swift layout size so the value is correct for any future precision-dependent consumer
    /// and so the Bool extra-inhabitant exception is not silently wrong.
    /// </summary>
    internal static bool TryGetOptionalPrimitiveInlineSize(TypeSpec innerSpec, out int optionalSize)
    {
        optionalSize = 0;
        if (!TryGetFixedWidthPrimitiveSize(innerSpec, out int innerSize))
            return false;

        // The TryGetFixedWidthPrimitiveSize guard above proves innerSpec is a fixed-width primitive,
        // so the only spare-bit distinction left is Bool vs the integers/floats: consult the single
        // s_spareInhabitantPrimitives home of that decision rather than restating a == Bool literal.
        bool hasExtraInhabitants = innerSpec is NamedTypeSpec named &&
                                   s_spareInhabitantPrimitives.Contains(named.Name);
        optionalSize = hasExtraInhabitants ? innerSize : innerSize + 1;
        return true;
    }

    /// <summary>
    /// Computes the inline size of <c>Optional&lt;T&gt;</c> for frozen struct Buffer fields.
    /// - If T has extra inhabitants (String, classes, arrays, Bool): <c>Optional&lt;T&gt;.size == T.size</c>
    /// - If T has no extra inhabitants (Int32, Double): <c>Optional&lt;T&gt;.size == T.size + 1</c>
    /// Returns false when <paramref name="fieldTypeSpec"/> is not an Optional. When it IS an
    /// Optional whose inner size cannot be derived (a generic value-type instantiation with no
    /// persisted size and no live metadata, e.g. ClosedRange&lt;Int&gt;), returns false with
    /// <paramref name="indeterminate"/> set — the caller must then fail closed.
    /// </summary>
    internal static bool TryComputeOptionalInlineSize(TypeSpec fieldTypeSpec, ITypeDatabase typeDatabase, out int optionalSize, out bool indeterminate)
    {
        optionalSize = IntPtr.Size;
        indeterminate = false;

        if (fieldTypeSpec is not NamedTypeSpec optionalSpec ||
            optionalSpec.Name != "Swift.Optional" ||
            optionalSpec.GenericParameters.Count != 1)
            return false;

        var innerTypeSpec = optionalSpec.GenericParameters[0];

        // A fixed-width primitive inner (Int32/Bool/Double/...) has a language-constant Optional
        // size that the cross-compile TypeDatabase does not persist (the XML primitive records
        // carry no inlineSize) and for which no live metadata exists at generate time. Resolve it
        // from the primitive table directly; otherwise TryResolveReferenceFieldSize would clamp the
        // inner to a pointer width and Optional<Int32> would be sized as two words instead of one.
        // This also wins over the live-metadata branch below for Bool, whose extra-inhabitant
        // behaviour the metadata would report but which is absent cross-compile.
        if (TryGetOptionalPrimitiveInlineSize(innerTypeSpec, out optionalSize))
            return true;

        if (!typeDatabase.TryGetTypeRecord(innerTypeSpec, out var innerRecord))
        {
            // Optional<T> where T can't be resolved at all — the Buffer field can't be sized.
            indeterminate = true;
            return false;
        }

        if (!TryResolveReferenceFieldSize(innerRecord, innerTypeSpec, out int innerSize))
        {
            indeterminate = true;
            return false;
        }

        // Determine if the inner type has extra inhabitants the optional tag can reuse.
        bool hasExtraInhabitants;
        if (innerRecord.SwiftTypeInfo.HasValue && innerRecord.SwiftTypeInfo.Value.MetadataPtr != IntPtr.Zero)
        {
            unsafe { hasExtraInhabitants = innerRecord.SwiftTypeInfo.Value.ValueWitnessTable->HasExtraInhabitants; }
        }
        else
        {
            // Heuristic: reference-bearing types (String, classes, arrays) contain pointers whose
            // spare bit patterns the optional tag reuses → Optional<T>.size == T.size.
            hasExtraInhabitants = (innerRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0
                                  || innerRecord.Kind == TypeRecordKind.Class;
        }

        optionalSize = hasExtraInhabitants ? innerSize : innerSize + 1;
        return true;
    }

    /// <summary>
    /// Resolves the inline byte size a reference-managed type occupies when stored inline in a
    /// frozen struct's blitted Buffer. Returns false (indeterminate) when the size is a
    /// per-instantiation property of a generic value type that the cross-compile TypeDatabase
    /// cannot derive: <see cref="SwiftTypeName.FromTypeSpec"/> strips the generic arguments so the
    /// bare record carries no <see cref="TypeRecord.InlineSize"/>, the iOS/device slice exposes no
    /// live metadata accessor, yet the true size depends on the arguments
    /// (MemoryLayout&lt;ClosedRange&lt;Int&gt;&gt; = 16 vs &lt;ClosedRange&lt;Float&gt;&gt; = 8).
    /// Guessing a word would mis-size the Buffer and corrupt the heap, so such fields fail closed.
    /// A class reference is always one pointer regardless of generic arguments, and a non-generic
    /// reference-managed value type keeps the historical single-pointer assumption (Array/Set/
    /// Dictionary already carry InlineSize), so neither regresses.
    /// </summary>
    internal static bool TryResolveReferenceFieldSize(TypeRecord record, TypeSpec spec, out int byteSize)
    {
        byteSize = IntPtr.Size;

        if (record.InlineSize.HasValue)
        {
            byteSize = record.InlineSize.Value;
            return true;
        }
        if (record.SwiftTypeInfo.HasValue && record.SwiftTypeInfo.Value.MetadataPtr != IntPtr.Zero)
        {
            unsafe { byteSize = (int)record.SwiftTypeInfo.Value.ValueWitnessTable->Size; }
            return true;
        }
        // A class reference is exactly one pointer regardless of its generic arguments.
        if (record.Kind == TypeRecordKind.Class)
        {
            byteSize = IntPtr.Size;
            return true;
        }
        // No persisted size, no live metadata, not a class. A generic value-type instantiation's
        // size is not derivable here → fail closed. Non-generic reference-managed value types keep
        // the historical single-pointer clamp (preserves behavior; no per-instantiation ambiguity).
        if (spec.ContainsGenericParameters)
            return false;

        return true; // byteSize stays IntPtr.Size — unchanged clamp for non-generic types
    }
}
