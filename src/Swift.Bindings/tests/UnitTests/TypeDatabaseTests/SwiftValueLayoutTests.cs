// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Direct unit coverage for <see cref="SwiftValueLayout"/> — the single Swift value-type inline-layout
/// oracle the field-layout walk (ModuleProcessor.ClassifyFieldType), the register walk
/// (TypeLowering.LowerOptional), and the frozen-struct Buffer emitter (FrozenStructHandler) all consult
/// so they cannot drift.
///
/// <para>
/// The behavioral contract: an <c>Optional&lt;T&gt;</c> gains a 1-byte discriminator tag ONLY when T is
/// a fixed-width integer/float scalar (it uses every bit pattern of its storage). Every spare-inhabitant
/// payload — Bool, pointers, class refs, enums, structs — keeps the inner size and must NOT have a tag
/// appended; fabricating one inflates the layout by a byte/slot. The decline-on-ambiguity consumers ask
/// the qualified-strict <see cref="SwiftValueLayout.HasAppendedOptionalTag"/> question and route
/// everything else to a wrapper; the always-answer frozen sizing path reads the same spare-bit truth
/// through the inline-size helpers. The parity tests below pin that the two never disagree.
/// </para>
/// </summary>
public class SwiftValueLayoutTests
{
    #region HasAppendedOptionalTag — the qualified-strict decline oracle

    [Theory]
    // Fixed-width integer scalars — every bit pattern used, no spare inhabitant → tag appended.
    [InlineData("Swift.Int")]
    [InlineData("Swift.UInt")]
    [InlineData("Swift.Int64")]
    [InlineData("Swift.UInt64")]
    [InlineData("Swift.Int32")]
    [InlineData("Swift.UInt32")]
    [InlineData("Swift.Int16")]
    [InlineData("Swift.UInt16")]
    [InlineData("Swift.Int8")]
    [InlineData("Swift.UInt8")]
    // Floating-point scalars.
    [InlineData("Swift.Float")]
    [InlineData("Swift.Double")]
    // CGFloat under both module spellings the type database can surface.
    [InlineData("CoreFoundation.CGFloat")]
    [InlineData("CoreGraphics.CGFloat")]
    public void HasAppendedOptionalTag_TagAddingScalar_ReturnsTrue(string swiftTypeName)
    {
        Assert.True(SwiftValueLayout.HasAppendedOptionalTag(swiftTypeName));
    }

    [Theory]
    // Bool folds .none into a spare bit pattern — Optional<Bool> is 1 byte, NOT 2.
    [InlineData("Swift.Bool")]
    // Pointers reserve the null representation as the spare inhabitant.
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Swift.OpaquePointer")]
    // Class references — Optional<AnyObject> is a single tagged pointer, no extra byte.
    [InlineData("Swift.AnyObject")]
    [InlineData("MyModule.SomeClass")]
    // Enums / structs carry their own spare bits.
    [InlineData("MyModule.SomeEnum")]
    [InlineData("MyModule.SomeStruct")]
    // Unqualified spellings are intentionally NOT recognized by the qualified-strict oracle — the
    // decline consumers only pass module-qualified parsed-ABI names, and declining a bare name is the
    // safe outcome (it routes to the @_cdecl wrapper). Widening this set is a known hazard (it would
    // flip ModuleProcessor.ClassifyFieldType from decline to a fabricated tag layout for bare inners).
    [InlineData("Int32")]
    [InlineData("Bool")]
    public void HasAppendedOptionalTag_SpareInhabitantOrUnqualified_ReturnsFalse(string swiftTypeName)
    {
        Assert.False(SwiftValueLayout.HasAppendedOptionalTag(swiftTypeName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HasAppendedOptionalTag_NullOrEmpty_ReturnsFalse(string swiftTypeName)
    {
        Assert.False(SwiftValueLayout.HasAppendedOptionalTag(swiftTypeName));
    }

    #endregion

    #region Reference field inline sizing (TryResolveReferenceFieldSize)

    [Fact]
    public void TryResolveReferenceFieldSize_PersistedInlineSize_HonoredVerbatim()
    {
        // AnyHashable is a reference-managed struct with a fixed 40-byte existential box. The
        // size is a property of the type (not its use site), persisted as inlineSize in the XML,
        // and must be honored verbatim — clamping it to one pointer (the historical bug) under-
        // sizes the Buffer field and corrupts the heap.
        var record = CreateReferenceTypeRecord("Swift.AnyHashable", TypeRecordKind.Struct, inlineSize: 40);
        var spec = new NamedTypeSpec("Swift.AnyHashable");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out int byteSize);

        Assert.True(resolved);
        Assert.Equal(40, byteSize);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_StringLike_ReturnsTwoWords()
    {
        // Swift.String is 16 bytes (two words) — the original mis-clamp to a single IntPtr is the
        // canonical instance of this bug class.
        var record = CreateReferenceTypeRecord("Swift.String", TypeRecordKind.Struct, inlineSize: 16);
        var spec = new NamedTypeSpec("Swift.String");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out int byteSize);

        Assert.True(resolved);
        Assert.Equal(16, byteSize);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_GenericValueTypeWithoutPersistedSize_IsIndeterminate()
    {
        // ClosedRange<Int> is a frozen reference-managed value type with no persisted InlineSize
        // and (cross-compile) no live metadata. Its inline size depends on the type arguments —
        // MemoryLayout<ClosedRange<Int>> = 16 but <ClosedRange<Float>> = 8 — and the bare
        // TypeDatabase record (generic args stripped) cannot derive it. Must fail closed.
        var record = CreateReferenceTypeRecord("Swift.ClosedRange", TypeRecordKind.Struct, inlineSize: null);
        var spec = new NamedTypeSpec("Swift.ClosedRange", new NamedTypeSpec("Swift.Int"));

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_GenericClassWithoutPersistedSize_IsOnePointer()
    {
        // A class reference is exactly one pointer regardless of its generic arguments, so it is
        // determinable even with no persisted size — never fail closed for a class.
        var record = CreateReferenceTypeRecord("TestModule.Box", TypeRecordKind.Class, inlineSize: null);
        var spec = new NamedTypeSpec("TestModule.Box", new NamedTypeSpec("Swift.Int"));

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out int byteSize);

        Assert.True(resolved);
        Assert.Equal(IntPtr.Size, byteSize);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_NonGenericValueTypeWithNoLayoutSource_KeepsPointerClamp()
    {
        // A NON-generic reference-managed value type with no persisted size, no live metadata, and no
        // parse-time declared layout at all (an XML-declared or Apple-supplement-synthesized record —
        // its stored fields were never seen, so no derivation was ever attempted) keeps the historical
        // single-pointer clamp. This is the third state of the layout lane: absent, not "attempted and
        // failed", so nothing new is known and behavior is unchanged.
        var record = CreateReferenceTypeRecord("TestModule.Handle", TypeRecordKind.Struct, inlineSize: null);
        var spec = new NamedTypeSpec("TestModule.Handle");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out int byteSize);

        Assert.True(resolved);
        Assert.Equal(IntPtr.Size, byteSize);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_DeclaredLayout_ResolvesTrueInlineSize()
    {
        // The defect this lane closes: a stored field whose type is itself a reference-bearing frozen
        // struct is NOT one pointer. A frozen struct holding a single Swift.String is 16 bytes inline
        // (MemoryLayout verified), so a Buffer mirror that reserved one 8-byte word for it blitted half
        // the value and wrote the rest past the allocation. The parse-time declared layout is the only
        // size source for such a module-local type cross-compile (no VWT metadata, no XML inlineSize).
        var record = CreateReferenceTypeRecord(
            "TestModule.RefLeaf", TypeRecordKind.Struct, inlineSize: null,
            declaredLayout: new DeclaredValueLayout(16, 8));
        var spec = new NamedTypeSpec("TestModule.RefLeaf");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out int byteSize);

        Assert.True(resolved);
        Assert.Equal(16, byteSize);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_PersistedInlineSize_WinsOverDeclaredLayout()
    {
        // Precedence: a measured/persisted InlineSize (live value-witness metadata, or a hand-stated
        // XML inlineSize) is ground truth and outranks the derived declared layout, which is only a
        // reconstruction from the declared stored fields.
        var record = CreateReferenceTypeRecord(
            "TestModule.RefLeaf", TypeRecordKind.Struct, inlineSize: 40,
            declaredLayout: new DeclaredValueLayout(16, 8));
        var spec = new NamedTypeSpec("TestModule.RefLeaf");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out int byteSize);

        Assert.True(resolved);
        Assert.Equal(40, byteSize);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_DeclaredLayoutIndeterminate_FailsClosed()
    {
        // Derivation ran over the type's own stored fields and could NOT produce a sound answer (a
        // field of an un-derivable type). A guessed width silently under-allocates the Buffer the blit
        // writes through, so the field must fail closed and take the containing struct's Buffer
        // projection out with it — never fall back to the pointer clamp.
        var record = CreateReferenceTypeRecord(
            "TestModule.RefLeaf", TypeRecordKind.Struct, inlineSize: null,
            declaredLayoutIndeterminate: true);
        var spec = new NamedTypeSpec("TestModule.RefLeaf");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_GenericInstantiation_FailsClosedDespiteDeclaredLayout()
    {
        // The generic fail-closed arm stays ahead of the declared-layout lane. A declared layout is
        // derived from the bare (argument-stripped) declaration, so it cannot describe a particular
        // instantiation — reading it here would re-introduce a per-instantiation guess.
        var record = CreateReferenceTypeRecord(
            "TestModule.Pair", TypeRecordKind.Struct, inlineSize: null,
            declaredLayout: new DeclaredValueLayout(16, 8));
        var spec = new NamedTypeSpec("TestModule.Pair", new NamedTypeSpec("Swift.Int"));

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_UnknownCustomAlignment_FailsClosedDespitePersistedInlineSize()
    {
        // `@_alignment(N)` with N absent from the ABI descriptor: the SIZE here is measured and
        // correct, and still unusable — the container has to round this field's offset up to an
        // alignment the emitted pointer-word fields cannot express, so it would sit at the wrong
        // offset and leave the Buffer short. The guard therefore has to outrank the size sources,
        // not sit behind them.
        var record = CreateReferenceTypeRecord(
            "TestModule.OverAligned", TypeRecordKind.Struct, inlineSize: 8,
            hasUnknownCustomAlignment: true);
        var spec = new NamedTypeSpec("TestModule.OverAligned");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_UnknownCustomAlignment_FailsClosedDespiteDeclaredLayout()
    {
        // Same guard on the derived lane: a size derived from the stored fields is just as unplaceable
        // once the type over-aligns.
        var record = CreateReferenceTypeRecord(
            "TestModule.OverAligned", TypeRecordKind.Struct, inlineSize: null,
            declaredLayout: new DeclaredValueLayout(16, 8),
            hasUnknownCustomAlignment: true);
        var spec = new NamedTypeSpec("TestModule.OverAligned");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_DeclaredLayoutWiderThanAPointer_FailsClosed()
    {
        // A Buffer mirrors the field as whole IntPtr words, so it can only start on an 8-byte
        // boundary. Accepting the size while discarding a 16-byte alignment would place the field at
        // the wrong offset and shorten the container — refuse it instead. The module-database reader
        // accepts such an alignment from XML, so this is not a hypothetical value.
        var record = CreateReferenceTypeRecord(
            "TestModule.WideAligned", TypeRecordKind.Struct, inlineSize: null,
            declaredLayout: new DeclaredValueLayout(32, 16));
        var spec = new NamedTypeSpec("TestModule.WideAligned");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_DeclaredLayoutAtPointerAlignment_IsHonored()
    {
        // Positive control: the ordinary derived shape — every reference-bearing Swift value type
        // aligns to at most a pointer — must keep resolving, or the guard above deletes the fix.
        var record = CreateReferenceTypeRecord(
            "TestModule.RefLeaf", TypeRecordKind.Struct, inlineSize: null,
            declaredLayout: new DeclaredValueLayout(32, 8));
        var spec = new NamedTypeSpec("TestModule.RefLeaf");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out int byteSize);

        Assert.True(resolved);
        Assert.Equal(32, byteSize);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_PersistedInlineSizeWithOverAlignedDeclaredLayout_FailsClosed()
    {
        // A record can carry BOTH a measured inlineSize and a declaredLayout — the module database
        // writes and reads them independently. The measured size is right and the offset is still
        // wrong, so the alignment refusal has to run ahead of every size source; checking it only
        // inside the declaredLayout arm would let the inlineSize arm return first and discard it.
        var record = CreateReferenceTypeRecord(
            "TestModule.WideAligned", TypeRecordKind.Struct, inlineSize: 32,
            declaredLayout: new DeclaredValueLayout(32, 16));
        var spec = new NamedTypeSpec("TestModule.WideAligned");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_NoCustomAlignment_StillHonorsPersistedInlineSize()
    {
        // Positive control for the two guards above: the alignment arm must not swallow the ordinary
        // measured-size path that every reference-bearing field takes.
        var record = CreateReferenceTypeRecord(
            "TestModule.RefLeaf", TypeRecordKind.Struct, inlineSize: 16);
        var spec = new NamedTypeSpec("TestModule.RefLeaf");

        var resolved = SwiftValueLayout.TryResolveReferenceFieldSize(record, spec, out int byteSize);

        Assert.True(resolved);
        Assert.Equal(16, byteSize);
    }

    #endregion

    #region Declared layout accumulation (AlignUp / DeclaredLayoutAccumulator)

    [Theory]
    [InlineData(0, 8, 0)]
    [InlineData(1, 8, 8)]
    [InlineData(8, 8, 8)]
    [InlineData(9, 8, 16)]
    [InlineData(25, 8, 32)]
    [InlineData(5, 4, 8)]
    [InlineData(7, 1, 7)]   // alignment 1 never moves the cursor
    [InlineData(7, 0, 7)]   // defensive: a zero alignment must not divide by zero
    public void AlignUp_RoundsOffsetToNextMultiple(int offset, int alignment, int expected)
    {
        Assert.Equal(expected, SwiftValueLayout.AlignUp(offset, alignment));
    }

    [Fact]
    public void DeclaredLayoutAccumulator_NoFields_IsEmptyAggregate()
    {
        // Swift gives an aggregate with no stored fields size 0 and alignment 1.
        var accumulator = new SwiftValueLayout.DeclaredLayoutAccumulator();

        Assert.Equal(new DeclaredValueLayout(0, 1), accumulator.Result);
    }

    [Fact]
    public void DeclaredLayoutAccumulator_MixedNestedShapes_MatchesSwiftMemoryLayout()
    {
        // The exact host shape the BindingTests fixture declares, checked against MemoryLayout:
        //   let leading:  RefLeaf      (16, align 8)  @0
        //   let optional: RefLeaf?     (16, align 8)  @16  — spare-bit payload, so no appended tag
        //   let trivial:  TrivialLeaf  ( 8, align 4)  @32
        //   let sentinel: Int32        ( 4, align 4)  @40
        // => size 44, alignment 8 (stride 48). A field mis-sized as one pointer shifts everything after it.
        var accumulator = new SwiftValueLayout.DeclaredLayoutAccumulator();
        accumulator.Add(new DeclaredValueLayout(16, 8));
        accumulator.Add(new DeclaredValueLayout(16, 8));
        accumulator.Add(new DeclaredValueLayout(8, 4));
        accumulator.Add(new DeclaredValueLayout(4, 4));

        Assert.Equal(new DeclaredValueLayout(44, 8), accumulator.Result);
    }

    [Fact]
    public void DeclaredLayoutAccumulator_AccumulatesSizeNotStride()
    {
        // A nested aggregate's trailing pad belongs to the NEXT field's alignment round-up, not to the
        // nested field itself. Accumulating stride (32) instead of size (25) would double-count it and
        // report 40+8 for a host that MemoryLayout measures at 40.
        var accumulator = new SwiftValueLayout.DeclaredLayoutAccumulator();
        accumulator.Add(new DeclaredValueLayout(25, 8)); // nested aggregate: size 25, stride 32
        accumulator.Add(new DeclaredValueLayout(8, 8));  // lands at 32, not 40

        Assert.Equal(new DeclaredValueLayout(40, 8), accumulator.Result);
    }

    [Fact]
    public void DeclaredLayoutAccumulator_AlignmentIsMaxFieldAlignment_AndPadsInteriorFields()
    {
        // Int8 @0, then an Int32 that must start at 4 (not 1). Size 8, alignment 4 — the aggregate's
        // alignment is the maximum of its fields', which is what a containing struct rounds up to.
        var accumulator = new SwiftValueLayout.DeclaredLayoutAccumulator();
        accumulator.Add(new DeclaredValueLayout(1, 1));
        accumulator.Add(new DeclaredValueLayout(4, 4));

        Assert.Equal(new DeclaredValueLayout(8, 4), accumulator.Result);
    }

    #endregion

    #region Fixed-width primitive sizing (TryGetFixedWidthPrimitiveSize)

    [Theory]
    [InlineData("Swift.Int8", 1)]
    [InlineData("Swift.UInt8", 1)]
    [InlineData("Swift.Bool", 1)]
    [InlineData("Swift.Int16", 2)]
    [InlineData("Swift.Int32", 4)]
    [InlineData("Swift.Float", 4)]
    [InlineData("Swift.Int64", 8)]
    [InlineData("Swift.Int", 8)]
    [InlineData("Swift.Double", 8)]
    [InlineData("Int32", 4)] // unqualified form is also recognized
    [InlineData("CoreFoundation.CGFloat", 8)] // CGFloat is recognized in the CoreFoundation spelling
    [InlineData("CGFloat", 8)]                 // ...and bare
    public void TryGetFixedWidthPrimitiveSize_KnownPrimitive_ReturnsLanguageConstantSize(string swiftName, int expected)
    {
        // The cross-compile XML persists no inlineSize for primitive records (Int32/Bool/...), and
        // there is no live metadata at generate time. Optional<primitive> Buffer fields must be
        // sized from the language-constant primitive table; resolving Int32 to a pointer width (the
        // reference-field fallback) mis-sizes Optional<Int32> as two words instead of one.
        var resolved = SwiftValueLayout.TryGetFixedWidthPrimitiveSize(new NamedTypeSpec(swiftName), out int byteSize);

        Assert.True(resolved);
        Assert.Equal(expected, byteSize);
    }

    [Theory]
    [InlineData("Swift.String")]      // reference-managed value type — sized via InlineSize, not this table
    [InlineData("Swift.AnyHashable")] // 40-byte existential box — sized via InlineSize
    [InlineData("Swift.ClosedRange")] // generic value type — fails closed elsewhere
    [InlineData("TestModule.Widget")] // arbitrary user type
    // The CoreGraphics spelling of CGFloat is deliberately NOT in the recognizer's domain (only the
    // CoreFoundation/bare spellings are); a CoreGraphics.CGFloat field falls through to the
    // InlineSize/metadata/reference path instead of this fast table. Pins that three-domain asymmetry.
    [InlineData("CoreGraphics.CGFloat")]
    public void TryGetFixedWidthPrimitiveSize_NonPrimitive_ReturnsFalse(string swiftName)
    {
        var resolved = SwiftValueLayout.TryGetFixedWidthPrimitiveSize(new NamedTypeSpec(swiftName), out int byteSize);

        Assert.False(resolved);
        Assert.Equal(0, byteSize);
    }

    #endregion

    #region Optional<primitive> inline sizing (TryGetOptionalPrimitiveInlineSize)

    [Theory]
    [InlineData("Swift.Bool", 1)]    // extra inhabitants — nil reuses a spare pattern, NO tag byte
    [InlineData("Swift.Int8", 2)]    // full byte range — tag byte: 1 + 1
    [InlineData("Swift.UInt8", 2)]   // full byte range — tag byte: 1 + 1
    [InlineData("Swift.Int16", 3)]   // 2 + 1
    [InlineData("Swift.Int32", 5)]   // 4 + 1
    [InlineData("Swift.Float", 5)]   // 4 + 1
    [InlineData("Swift.Int64", 9)]   // 8 + 1
    [InlineData("Swift.Int", 9)]     // 8 + 1
    [InlineData("Swift.Double", 9)]  // 8 + 1
    [InlineData("CoreFoundation.CGFloat", 9)] // 8-byte full-range float — gains a tag byte: 8 + 1
    public void TryGetOptionalPrimitiveInlineSize_KnownPrimitive_MatchesSwiftLayout(string swiftName, int expected)
    {
        // Optional<primitive> inline size is a language constant. Every full-range primitive needs a
        // separate tag byte (Optional<T>.size == T.size + 1), but Bool has only two valid bit patterns
        // so Optional<Bool> reuses a spare pattern for nil and stays one byte. Sizing Bool? at two
        // bytes (the naive "+1 for every primitive" rule) over-reserves its Buffer slot and shifts
        // every following field — heap corruption on the blit. Verified against MemoryLayout in Swift.
        var resolved = SwiftValueLayout.TryGetOptionalPrimitiveInlineSize(new NamedTypeSpec(swiftName), out int byteSize);

        Assert.True(resolved);
        Assert.Equal(expected, byteSize);
    }

    [Theory]
    // The frozen-struct field walk sees field type names in the bare spelling as well as the
    // module-qualified one. The spare-bit/Bool decision is single-sourced through
    // s_spareInhabitantPrimitives, which lists BOTH spellings, so bare "Bool" still keeps its
    // spare-inhabitant size and bare "Int32" still gains the tag byte — the regression guard for
    // removing the inline `== "Bool"` literal during the value-layout consolidation.
    [InlineData("Bool", 1)]
    [InlineData("Int32", 5)]
    [InlineData("Double", 9)]
    [InlineData("CGFloat", 9)] // bare CGFloat is recognized too: 8 + 1
    public void TryGetOptionalPrimitiveInlineSize_UnqualifiedSpelling_MatchesQualified(string bareName, int expected)
    {
        var resolved = SwiftValueLayout.TryGetOptionalPrimitiveInlineSize(new NamedTypeSpec(bareName), out int byteSize);

        Assert.True(resolved);
        Assert.Equal(expected, byteSize);
    }

    [Theory]
    [InlineData("Swift.String")]      // reference-managed — sized via InlineSize/metadata, not this table
    [InlineData("Swift.AnyHashable")] // existential box
    [InlineData("TestModule.Widget")] // arbitrary user type
    [InlineData("CoreGraphics.CGFloat")] // unrecognized CGFloat spelling — falls through, same asymmetry
    public void TryGetOptionalPrimitiveInlineSize_NonPrimitive_ReturnsFalse(string swiftName)
    {
        var resolved = SwiftValueLayout.TryGetOptionalPrimitiveInlineSize(new NamedTypeSpec(swiftName), out int byteSize);

        Assert.False(resolved);
        Assert.Equal(0, byteSize);
    }

    #endregion

    #region Spare-bit parity — the decline oracle and the frozen sizing path read one truth

    [Theory]
    [InlineData("Swift.Int")]
    [InlineData("Swift.Int8")]
    [InlineData("Swift.Int32")]
    [InlineData("Swift.UInt64")]
    [InlineData("Swift.Float")]
    [InlineData("Swift.Double")]
    public void SpareBitTruth_TagAddingScalar_DeclineOracleAndFrozenSizingAgree(string swiftName)
    {
        // The whole point of the consolidation: the decline-on-ambiguity oracle and the always-answer frozen
        // sizing path must read the SAME spare-bit truth so they can't drift. For a tag-adding scalar
        // the oracle says "tag appended" AND the frozen path independently produces inner + 1.
        Assert.True(SwiftValueLayout.HasAppendedOptionalTag(swiftName));

        Assert.True(SwiftValueLayout.TryGetFixedWidthPrimitiveSize(new NamedTypeSpec(swiftName), out int innerSize));
        Assert.True(SwiftValueLayout.TryGetOptionalPrimitiveInlineSize(new NamedTypeSpec(swiftName), out int optionalSize));
        Assert.Equal(innerSize + 1, optionalSize);
    }

    [Fact]
    public void SpareBitTruth_Bool_DeclineOracleAndFrozenSizingAgree_NoTag()
    {
        // Bool is the lone fixed-width primitive with a spare inhabitant: the decline oracle says
        // "no tag" and the frozen sizing path keeps the inner size. Same truth, single source — if
        // these two ever disagreed the register oracle would fabricate an over-wide Optional<Bool>.
        Assert.False(SwiftValueLayout.HasAppendedOptionalTag("Swift.Bool"));

        Assert.True(SwiftValueLayout.TryGetFixedWidthPrimitiveSize(new NamedTypeSpec("Swift.Bool"), out int innerSize));
        Assert.True(SwiftValueLayout.TryGetOptionalPrimitiveInlineSize(new NamedTypeSpec("Swift.Bool"), out int optionalSize));
        Assert.Equal(innerSize, optionalSize);
    }

    #endregion

    #region Optional inline sizing over the type database (TryComputeOptionalInlineSize)

    [Fact]
    public void TryComputeOptionalInlineSize_NotAnOptional_ReturnsFalseNotIndeterminate()
    {
        // A non-Optional field type is simply not this method's concern — it returns false WITHOUT
        // flagging indeterminate, so the caller continues its normal (non-optional) sizing path.
        var db = new TypeDatabase();

        var resolved = SwiftValueLayout.TryComputeOptionalInlineSize(
            new NamedTypeSpec("Swift.Int32"), db, out _, out bool indeterminate);

        Assert.False(resolved);
        Assert.False(indeterminate);
    }

    [Theory]
    [InlineData("Swift.Int32", 5)] // full-range scalar gains the tag byte
    [InlineData("Swift.Bool", 1)]  // spare-inhabitant primitive keeps its size — no DB lookup needed
    public void TryComputeOptionalInlineSize_PrimitiveInner_ResolvedFromLanguageConstants(string innerName, int expected)
    {
        // The primitive fast path wins before any TypeDatabase lookup: these sizes are language
        // constants the cross-compile database does not persist and for which no live metadata exists.
        var db = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(innerName));

        var resolved = SwiftValueLayout.TryComputeOptionalInlineSize(optionalSpec, db, out int optionalSize, out bool indeterminate);

        Assert.True(resolved);
        Assert.False(indeterminate);
        Assert.Equal(expected, optionalSize);
    }

    [Fact]
    public void TryComputeOptionalInlineSize_UnresolvableInner_FailsClosedIndeterminate()
    {
        // Optional<T> where T is neither a primitive nor registered in the database cannot be sized —
        // it must fail closed (indeterminate) rather than guess a word, which would mis-size the Buffer.
        var db = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Unregistered.Mystery"));

        var resolved = SwiftValueLayout.TryComputeOptionalInlineSize(optionalSpec, db, out _, out bool indeterminate);

        Assert.False(resolved);
        Assert.True(indeterminate);
    }

    #endregion

    #region Simple enum stored inline size (GetSimpleEnumStoredInlineSize)

    [Theory]
    [InlineData(1)] // ≤256 cases
    [InlineData(2)] // ≤65536 cases — the case the one-byte fallback would read too narrow
    [InlineData(4)]
    [InlineData(8)]
    public void GetSimpleEnumStoredInlineSize_PersistedInlineSize_HonoredVerbatim(int inlineSize)
    {
        // The stored discriminator width is the type's measured/persisted InlineSize — the read path
        // must reflect it exactly so a 2-byte (>256-case) enum is read as a short, not a byte.
        var record = CreateSimpleEnumRecord(inlineSize);

        Assert.Equal(inlineSize, SwiftValueLayout.GetSimpleEnumStoredInlineSize(record));
    }

    [Fact]
    public void GetSimpleEnumStoredInlineSize_NullInlineSize_FallsBackToOneByte()
    {
        // Cross-compile / XML-loaded simple enums persist simpleEnum+frozen but not inlineSize; the
        // single documented fallback is one byte (the minimal discriminator, correct for ≤256 cases).
        var record = CreateSimpleEnumRecord(inlineSize: null);

        Assert.Equal(1, SwiftValueLayout.DefaultSimpleEnumDiscriminatorBytes);
        Assert.Equal(SwiftValueLayout.DefaultSimpleEnumDiscriminatorBytes,
            SwiftValueLayout.GetSimpleEnumStoredInlineSize(record));
    }

    #endregion

    #region Determinism — algorithm path (MetadataPtr == 0) matches the live-VWT layout

    // The generator runs CROSS-COMPILE: it never loads the target dylib, so no live value-witness
    // metadata is available and `SwiftTypeInfo.MetadataPtr` is always zero at generate time. The
    // Optional inline-size algorithm therefore always takes the heuristic branch
    // (RequiresMemoryManagement / Kind == Class) rather than reading `HasExtraInhabitants` off a live
    // VWT. These pins assert that algorithm-only path reproduces the value a metadata-loaded host (or a
    // Swift `MemoryLayout` measurement) would compute for a known struct — so the SAME library generates
    // byte-identical bindings whether or not the host can load the dylib. A record built WITHOUT
    // `SwiftTypeInfo` forces `MetadataPtr == 0` (the cross-compile reality) explicitly.

    [Fact]
    public void TryComputeOptionalInlineSize_ReferenceBearingInner_MetadataPtrZero_MatchesLiveVwtSize()
    {
        // A String-like reference-managed struct (16-byte two-word storage) carries extra inhabitants in
        // its pointer's spare bit patterns, so nil folds in with NO appended tag:
        // MemoryLayout<String?>.size == MemoryLayout<String>.size == 16 — the value a live VWT reports.
        // With MetadataPtr == 0 the algorithm reaches that same 16 via the RequiresMemoryManagement
        // heuristic, so the binding is identical with or without a loadable dylib.
        var (db, optionalSpec) = OptionalOverRegisteredInner(
            "DetMod.StringLike", inlineSize: 16, referenceBearing: true);

        var resolved = SwiftValueLayout.TryComputeOptionalInlineSize(
            optionalSpec, db, out int optionalSize, out bool indeterminate);

        Assert.True(resolved);
        Assert.False(indeterminate);
        Assert.Equal(16, optionalSize); // == inner size, no tag (extra inhabitants) — matches live VWT
    }

    [Fact]
    public void TryComputeOptionalInlineSize_NoSpareBitStructInner_MetadataPtrZero_AppendsTagDeterministically()
    {
        // A plain (non-reference-managed) value struct that packs its full 16 bytes exposes no spare
        // inhabitant, so its Optional gains a tag byte: MemoryLayout<T?>.size == 17 — again the value a
        // live VWT reports. With MetadataPtr == 0 the heuristic (not memory-managed, not a class) returns
        // hasExtraInhabitants == false and the algorithm independently produces inner + 1 = 17.
        var (db, optionalSpec) = OptionalOverRegisteredInner(
            "DetMod.PackedPair", inlineSize: 16, referenceBearing: false);

        var resolved = SwiftValueLayout.TryComputeOptionalInlineSize(
            optionalSpec, db, out int optionalSize, out bool indeterminate);

        Assert.True(resolved);
        Assert.False(indeterminate);
        Assert.Equal(17, optionalSize); // inner + 1 tag byte (no spare inhabitant) — matches live VWT
    }

    [Fact]
    public void TryComputeOptionalInlineSize_PayloadEnumInner_MetadataPtrZero_FailsClosed()
    {
        // A payload-carrying enum is flagged reference-managed purely because it HAS associated
        // values, so that flag cannot stand in for spare inhabitants the way it does for a struct
        // holding a String: `enum E { case x(Int64) }` fills all eight bytes, making E? nine, while
        // an enum whose payload is a class folds nil into the pointer and stays eight. The
        // declaration does not say which, so the Optional must fail closed rather than pick one.
        var (db, optionalSpec) = OptionalOverRegisteredInner(
            "DetMod.PayloadEnum", inlineSize: 8, referenceBearing: true, kind: TypeRecordKind.Enum);

        var resolved = SwiftValueLayout.TryComputeOptionalInlineSize(
            optionalSpec, db, out _, out bool indeterminate);

        Assert.False(resolved);
        Assert.True(indeterminate);
    }

    [Fact]
    public void TryComputeOptionalInlineSize_UnderivableStructInner_FailsClosed()
    {
        // A struct that stores a payload enum inherits the reference-managed flag from it, so the
        // spare-bit heuristic would claim inhabitants the enum field may already have spent — the
        // same doubt as the payload enum itself, one level out. The record says its own layout could
        // not be derived, and that is the signal: the measured size stands, whether `.none` fits
        // inside it does not.
        var (db, optionalSpec) = OptionalOverRegisteredInner(
            "DetMod.EnumWrapper", inlineSize: 8, referenceBearing: true, layoutIndeterminate: true);

        var resolved = SwiftValueLayout.TryComputeOptionalInlineSize(
            optionalSpec, db, out _, out bool indeterminate);

        Assert.False(resolved);
        Assert.True(indeterminate);
    }

    [Fact]
    public void TryComputeOptionalInlineSize_NoPayloadEnumInner_StillResolves()
    {
        // Scoping control for the refusal above: it is keyed to the reference-managed payload flag,
        // not to being an enum. A no-payload enum carries no associated values, so nothing about its
        // spare bits was ever claimed and the deterministic tag-append arm still applies.
        var (db, optionalSpec) = OptionalOverRegisteredInner(
            "DetMod.PlainEnum", inlineSize: 1, referenceBearing: false, kind: TypeRecordKind.Enum);

        var resolved = SwiftValueLayout.TryComputeOptionalInlineSize(
            optionalSpec, db, out int optionalSize, out bool indeterminate);

        Assert.True(resolved);
        Assert.False(indeterminate);
        Assert.Equal(2, optionalSize);
    }

    /// <summary>
    /// Builds an <c>Optional&lt;Inner&gt;</c> spec over a struct registered in a fresh
    /// <see cref="TypeDatabase"/> with a persisted <paramref name="inlineSize"/> but NO
    /// <c>SwiftTypeInfo</c> — so <c>MetadataPtr == 0</c> and the Optional-sizing algorithm is forced down
    /// its cross-compile heuristic branch. <paramref name="referenceBearing"/> toggles
    /// <see cref="TypeRecordFlags.RequiresMemoryManagement"/>, which drives the extra-inhabitant heuristic.
    /// </summary>
    private static (TypeDatabase Db, NamedTypeSpec OptionalSpec) OptionalOverRegisteredInner(
        string moduleQualifiedInnerName, int inlineSize, bool referenceBearing,
        TypeRecordKind kind = TypeRecordKind.Struct, bool layoutIndeterminate = false)
    {
        var dotIndex = moduleQualifiedInnerName.IndexOf('.');
        var moduleName = moduleQualifiedInnerName[..dotIndex];
        var swiftName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedInnerName);
        var flags = TypeRecordFlags.Frozen;
        if (referenceBearing)
            flags |= TypeRecordFlags.RequiresMemoryManagement;

        var module = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        module.RegisterType(swiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, moduleQualifiedInnerName[(dotIndex + 1)..]),
            SwiftTypeName = swiftName,
            MetadataAccessor = string.Empty,
            Flags = flags,
            Kind = kind,
            InlineSize = inlineSize,
            DeclaredLayoutIndeterminate = layoutIndeterminate,
        });
        var db = new TypeDatabase();
        db.AddModuleDatabase(module);

        var optionalSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(moduleQualifiedInnerName));
        return (db, optionalSpec);
    }

    #endregion

    private static TypeRecord CreateSimpleEnumRecord(int? inlineSize) =>
        new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SomeEnum"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SomeEnum"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            Kind = TypeRecordKind.Enum,
            InlineSize = inlineSize,
        };

    private static TypeRecord CreateReferenceTypeRecord(
        string moduleQualifiedName,
        TypeRecordKind kind,
        int? inlineSize,
        DeclaredValueLayout? declaredLayout = null,
        bool declaredLayoutIndeterminate = false,
        bool hasUnknownCustomAlignment = false)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName);
        var flags = TypeRecordFlags.RequiresMemoryManagement;
        if (kind == TypeRecordKind.Struct)
            flags |= TypeRecordFlags.Frozen;
        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", moduleQualifiedName.Split('.').Last()),
            SwiftTypeName = swiftName,
            MetadataAccessor = "",
            Flags = flags,
            Kind = kind,
            InlineSize = inlineSize,
            DeclaredLayout = declaredLayout,
            DeclaredLayoutIndeterminate = declaredLayoutIndeterminate,
            HasUnknownCustomAlignment = hasUnknownCustomAlignment,
        };
    }
}
