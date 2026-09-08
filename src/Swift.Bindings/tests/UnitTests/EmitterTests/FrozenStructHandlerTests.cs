// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for FrozenStructHandler and FrozenStructHandlerFactory.
/// </summary>
public class FrozenStructHandlerTests
{
    #region Factory Tests

    [Fact]
    public void Factory_Handles_FrozenStructDecl_ReturnsTrue()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var frozenStruct = CreateFrozenStructDecl("Point");

        Assert.True(factory.Handles(frozenStruct));
    }

    [Fact]
    public void Factory_Handles_NonFrozenStructDecl_ReturnsFalse()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var nonFrozenStruct = CreateNonFrozenStructDecl("NonFrozenStruct");

        Assert.False(factory.Handles(nonFrozenStruct));
    }

    [Fact]
    public void Factory_Handles_ClassDecl_ReturnsFalse()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var classDecl = CreateClassDecl("MyClass");

        Assert.False(factory.Handles(classDecl));
    }

    [Fact]
    public void Factory_Handles_EnumDecl_ReturnsFalse()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var enumDecl = CreateEnumDecl("MyEnum");

        Assert.False(factory.Handles(enumDecl));
    }

    [Fact]
    public void Factory_Handles_ProtocolDecl_ReturnsFalse()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var protocolDecl = CreateProtocolDecl("MyProtocol");

        Assert.False(factory.Handles(protocolDecl));
    }

    [Fact]
    public void Factory_Construct_ReturnsHandler()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);

        var handler = factory.Construct();

        Assert.NotNull(handler);
        Assert.IsType<FrozenStructHandler>(handler);
    }

    #endregion

    #region StructDecl Configuration Tests

    [Fact]
    public void FrozenStructDecl_IsFrozen_ReturnsTrue()
    {
        var structDecl = CreateFrozenStructDecl("CGPoint");

        Assert.True(structDecl.IsFrozen);
    }

    [Fact]
    public void FrozenStructDecl_HasCorrectSwiftTypeName()
    {
        var structDecl = CreateFrozenStructDecl("CGPoint", moduleName: "CoreGraphics");

        Assert.Equal("CoreGraphics.CGPoint", structDecl.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void FrozenStructDecl_CanHaveProperties()
    {
        var structDecl = CreateFrozenStructDecl("CGPoint");
        structDecl.Properties.Add(CreatePropertyDecl("x", "Swift.Double"));
        structDecl.Properties.Add(CreatePropertyDecl("y", "Swift.Double"));

        Assert.Equal(2, structDecl.Properties.Count);
    }

    [Fact]
    public void FrozenStructDecl_CanHaveMethods()
    {
        var structDecl = CreateFrozenStructDecl("CGPoint");
        structDecl.Methods.Add(CreateMethodDecl("distance"));

        Assert.Single(structDecl.Methods);
    }

    [Fact]
    public void FrozenStructDecl_CanHaveOperators()
    {
        var structDecl = CreateFrozenStructDecl("Vector");
        structDecl.Operators.Add(CreateOperatorDecl("+", OperatorKind.Binary));
        structDecl.Operators.Add(CreateOperatorDecl("-", OperatorKind.Binary));

        Assert.Equal(2, structDecl.Operators.Count);
    }

    [Fact]
    public void FrozenStructDecl_CanHaveNestedTypes()
    {
        var structDecl = CreateFrozenStructDecl("Container");
        structDecl.Types.Add(CreateFrozenStructDecl("InnerStruct", moduleName: "TestModule.Container"));

        Assert.Single(structDecl.Types);
    }

    [Fact]
    public void FrozenStructDecl_CanHaveConformances()
    {
        var structDecl = CreateFrozenStructDecl("EquatablePoint");
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.EquatablePoint"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sConformance"));

        Assert.Single(structDecl.Conformances);
    }

    [Fact]
    public void FrozenStructDecl_ConformsToEquatable_CanBeDetected()
    {
        var structDecl = CreateFrozenStructDecl("Point");
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sEquatableConformance"));

        var conformsToEquatable = structDecl.Conformances
            .Any(c => c.Protocol.ModuleQualifiedName == "Swift.Equatable");

        Assert.True(conformsToEquatable);
    }

    #endregion

    #region Generic Parameters Tests

    [Fact]
    public void FrozenStructDecl_WithGenericParameter_HasGenericParameters()
    {
        var structDecl = CreateFrozenStructDecl("Container");
        structDecl.GenericParameters.Add(CreateGenericArgumentDecl("T"));

        Assert.Single(structDecl.GenericParameters);
        Assert.Equal("T", structDecl.GenericParameters[0].TypeName);
    }

    [Fact]
    public void FrozenStructDecl_WithMultipleGenericParameters_CollectsAll()
    {
        var structDecl = CreateFrozenStructDecl("Pair");
        structDecl.GenericParameters.Add(CreateGenericArgumentDecl("T"));
        structDecl.GenericParameters.Add(CreateGenericArgumentDecl("U"));

        Assert.Equal(2, structDecl.GenericParameters.Count);
    }

    [Fact]
    public void FrozenStructDecl_WithConstrainedGeneric_HasConformances()
    {
        var structDecl = CreateFrozenStructDecl("EquatableContainer");
        structDecl.GenericParameters.Add(CreateGenericArgumentDeclWithConformance("T", "Swift.Equatable"));

        Assert.Single(structDecl.GenericParameters[0].GenericConformances);
    }

    #endregion

    #region Operator Support Tests

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("/")]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData("<")]
    [InlineData(">")]
    public void FrozenStructDecl_CanHaveArithmeticAndComparisonOperators(string symbol)
    {
        var structDecl = CreateFrozenStructDecl("Number");
        structDecl.Operators.Add(CreateOperatorDecl(symbol, OperatorKind.Binary));

        Assert.Single(structDecl.Operators);
        Assert.Equal(symbol, structDecl.Operators[0].OperatorSymbol);
    }

    [Theory]
    [InlineData("!")]
    [InlineData("~")]
    public void FrozenStructDecl_CanHaveUnaryOperators(string symbol)
    {
        var structDecl = CreateFrozenStructDecl("BitField");
        structDecl.Operators.Add(CreateOperatorDecl(symbol, OperatorKind.Unary));

        Assert.Single(structDecl.Operators);
        Assert.Equal(OperatorKind.Unary, structDecl.Operators[0].Kind);
    }

    [Fact]
    public void FrozenStructDecl_HasEqualityOperator_CanBeDetected()
    {
        var structDecl = CreateFrozenStructDecl("Point");
        structDecl.Operators.Add(CreateOperatorDecl("==", OperatorKind.Binary));

        var hasEquality = structDecl.Operators.Any(o => o.OperatorSymbol == "==");

        Assert.True(hasEquality);
    }

    #endregion

    #region Property Storage Tests

    [Fact]
    public void FrozenStructDecl_StoredProperty_HasStorageTrue()
    {
        var structDecl = CreateFrozenStructDecl("Point");
        var property = CreatePropertyDecl("x", "Swift.Double", hasStorage: true);
        structDecl.Properties.Add(property);

        Assert.True(structDecl.Properties[0].HasStorage);
    }

    [Fact]
    public void FrozenStructDecl_ComputedProperty_HasStorageFalse()
    {
        var structDecl = CreateFrozenStructDecl("Rectangle");
        var property = CreatePropertyDecl("area", "Swift.Double", hasStorage: false);
        structDecl.Properties.Add(property);

        Assert.False(structDecl.Properties[0].HasStorage);
    }

    [Fact]
    public void FrozenStructDecl_MixedStorageProperties_BothDetected()
    {
        var structDecl = CreateFrozenStructDecl("Rectangle");
        structDecl.Properties.Add(CreatePropertyDecl("width", "Swift.Double", hasStorage: true));
        structDecl.Properties.Add(CreatePropertyDecl("height", "Swift.Double", hasStorage: true));
        structDecl.Properties.Add(CreatePropertyDecl("area", "Swift.Double", hasStorage: false));

        var storedCount = structDecl.Properties.Count(p => p.HasStorage);
        var computedCount = structDecl.Properties.Count(p => !p.HasStorage);

        Assert.Equal(2, storedCount);
        Assert.Equal(1, computedCount);
    }

    #endregion

    #region Metadata Accessor Tests

    [Fact]
    public void FrozenStructDecl_HasMetadataAccessor()
    {
        var structDecl = CreateFrozenStructDecl("Point");
        structDecl.MetadataAccessor = "$s12CoreGraphics7CGPointVMa";

        Assert.NotEmpty(structDecl.MetadataAccessor);
    }

    [Fact]
    public void FrozenStructDecl_MetadataAccessorFormat_ContainsMaSuffix()
    {
        var structDecl = CreateFrozenStructDecl("Point");
        structDecl.MetadataAccessor = "$s12CoreGraphics7CGPointVMa";

        Assert.EndsWith("Ma", structDecl.MetadataAccessor);
    }

    #endregion

    #region A8 — Property Dedup Tests

    [Fact]
    public void FrozenStructHandler_DuplicateProperty_SecondSkipped()
    {
        // When the same property name appears twice (e.g., from conditional extensions),
        // the second should be detected as a duplicate and skipped.
        var structDecl = CreateFrozenStructDecl("Settings");
        structDecl.Properties.Add(CreatePropertyDecl("maxRetries", "Swift.Int"));
        structDecl.Properties.Add(CreatePropertyDecl("maxRetries", "Swift.Int")); // duplicate from extension

        var names = new HashSet<string>();
        foreach (var prop in structDecl.Properties)
        {
            var csName = NameProvider.GetPropertyName(prop.Name, structDecl.Name);
            if (!names.Add(csName))
            {
                // Second add returns false — duplicate correctly detected
                Assert.True(true, "Duplicate property correctly detected for frozen struct");
                return;
            }
        }

        Assert.Fail("Should have detected duplicate property name");
    }

    #endregion

    #region Sub-word Optional by-value layout mismatch (HasSubWordOptionalLayoutMismatch)

    // A by-value frozen struct (NOT projected as a Buffer-backed class) emits each Optional<primitive>
    // field as a whole 8-byte IntPtr word, but Swift packs sub-word optionals tighter. When that pushes
    // a later field to a different byte offset than Swift's packed layout, a by-value cdecl pass reads
    // the field from the wrong slot and corrupts it — so we must skip. The predicate simulates BOTH
    // layouts field-by-field and fires ONLY on per-field START-OFFSET divergence (a count of sub-word
    // optionals is neither necessary nor sufficient — confirmed independently).

    [Fact]
    public void SubWordOptionalMismatch_BoolOptThenInt32Opt_OffsetDiverges_Skips()
    {
        // Swift: Bool? @0(size1,a1), Int32? @4(size5,a4). C#: @0(word8), @8(word8). Second field
        // offset 4≠8 → the classic divergence; a by-value pass would read Int32? from the wrong word.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("BoolThenInt32",
            ("flag", OptionalOf("Swift.Bool")),
            ("count", OptionalOf("Swift.Int32")));

        Assert.True(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_IntOptThenInt32Opt_SecondOffsetDiverges_Skips()
    {
        // Swift: Int? @0(size9,a8), Int32? @12(size5,a4). C#: @0(word16), @16(word8). Second field
        // offset 12≠16 → diverges even though Int? itself is whole-word (the sub-word Int32? is what packs).
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("IntThenInt32",
            ("big", OptionalOf("Swift.Int")),
            ("count", OptionalOf("Swift.Int32")));

        Assert.True(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_NonOptionalInt32ThenBoolOpt_OffsetDiverges_Skips()
    {
        // Swift: Int32 @0(size4,a4), Bool? @4(size1,a1). C#: Int32 @0(size4), Bool? @8(word8). The
        // trailing optional's C# 8-alignment pushes it to offset 8 vs Swift's 4 — a non-optional leading
        // field does not make a following sub-word optional safe (mixed-field witness).
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("Int32ThenBool",
            ("count", new NamedTypeSpec("Swift.Int32")),
            ("flag", OptionalOf("Swift.Bool")));

        Assert.True(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void OverPaddedOptionalMismatch_Int64OptThenInt8_WholeWordValueOptional_Skips()
    {
        // Int64? is a WHOLE-WORD value optional: Int64 uses every bit so Swift
        // appends a separate tag byte → size 9, align 8. C# emits two IntPtr words = 16B. The following
        // Int8 lands at Swift @9 but C# @16 → corrupting by-value divergence. The pre-fix gate
        // (swiftAlign < IntPtr.Size) missed this because Int64? aligns to 8; the over-pad gate
        // (csSize 16 != swiftSize 9) catches it.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("Int64OptThenInt8",
            ("a", OptionalOf("Swift.Int64")),
            ("b", new NamedTypeSpec("Swift.Int8")));

        Assert.True(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void OverPaddedOptionalMismatch_DoubleOptThenInt8_WholeWordValueOptional_Skips()
    {
        // Double? is likewise tag-extended to 9B align8 (no extra inhabitants for the nil case), so a
        // following Int8 diverges (Swift @9 vs C# @16) exactly like Int64?.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("DoubleOptThenInt8",
            ("a", OptionalOf("Swift.Double")),
            ("b", new NamedTypeSpec("Swift.Int8")));

        Assert.True(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void OverPaddedOptionalMismatch_Int64OptThenInt64_EightAlignedFieldRepairs_NoSkip()
    {
        // Guard rail against over-firing: Int64? @0(size9→word16), then Int64 @ AlignUp(9,8)=16 (Swift)
        // and AlignUp(16,8)=16 (C#) — the following field's own 8-alignment swallows the 9→16 over-pad,
        // so offsets coincide and the struct lays out identically. Must NOT skip.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("Int64OptThenInt64",
            ("a", OptionalOf("Swift.Int64")),
            ("b", new NamedTypeSpec("Swift.Int64")));

        Assert.False(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_SingleInt32Opt_TailPaddingAbsorbs_NoSkip()
    {
        // Swift: Int32? @0(size5,a4,stride8). C#: @0(word8). Offsets AND stride both 0/8 — the extra 3
        // C# bytes land in Swift's tail padding. A lone sub-word optional must NOT be skipped.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("SingleInt32",
            ("count", OptionalOf("Swift.Int32")));

        Assert.False(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_TwoInt32Opt_OffsetsCoincide_NoSkip()
    {
        // Swift: Int32? @0, Int32? @8 (AlignUp(5,4)=8). C#: @0, @8. Every offset coincides — Swift's
        // inter-field padding exactly equals the C# inflation, so a count-based "≥2 sub-word" rule would
        // wrongly skip this. Offset-divergence predicate correctly passes it.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("TwoInt32",
            ("a", OptionalOf("Swift.Int32")),
            ("b", OptionalOf("Swift.Int32")));

        Assert.False(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_BoolOptThenIntOpt_LargeAlignRepairsGap_NoSkip()
    {
        // Swift: Bool? @0(size1), Int? @8 (AlignUp(1,8)=8). C#: @0, @8. The leading sub-word optional's
        // slack is swallowed by the next field's 8-byte alignment gate on BOTH sides — offsets match.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("BoolThenInt",
            ("flag", OptionalOf("Swift.Bool")),
            ("big", OptionalOf("Swift.Int")));

        Assert.False(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_LoneBoolOpt_StrideOnlyDifference_NoSkip()
    {
        // Swift: Bool? @0(size1,stride1). C#: @0(word8,stride8). Offsets match (both 0); only the STRIDE
        // differs (1 vs 8). By design we fire ONLY on offset divergence — a stride-only difference is
        // absorbed by the ≤16-byte register classification + emitted Size= attribute, and skipping it
        // would over-suppress correctly-passing single-optional structs.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("LoneBool",
            ("flag", OptionalOf("Swift.Bool")));

        Assert.False(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_ProjectedAsClass_Excluded_NoSkip()
    {
        // The SAME diverging Bool?+Int32? field shape, but registered as a frozen-with-memory struct
        // (RequiresMemoryManagement) → projected as a Buffer-backed class, pointer-passed as an opaque
        // Buffer that Swift fills via accessors. It never lowers through a by-value ABI, so the by-value
        // guard must NOT fire (that is HasIndeterminateBufferLayout's domain).
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("ClassProjected",
            ("flag", OptionalOf("Swift.Bool")),
            ("count", OptionalOf("Swift.Int32")));
        db.AddOutOfModuleTypes(new[]
        {
            (s.SwiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ClassProjected"),
                SwiftTypeName = s.SwiftTypeName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            }),
        });

        Assert.False(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_NonFrozenStruct_Excluded_NoSkip()
    {
        // Regression guard: the SAME diverging sub-word optional field shape (two `Bool?` stored
        // properties), but on a NON-frozen struct. A non-frozen struct is projected as
        // ClassWithOpaquePayload (an opaque SafeHandle, pointer-passed and filled by Swift accessors) and
        // never lowers through a by-value ABI, so sub-word packing cannot corrupt it. The by-value gate
        // must NOT fire — otherwise the struct is added to the TypeSkipPrePass skip set that
        // ReferencesUnsupportedModule consults, silently dropping the struct's own constructor/factories
        // even though the type itself still emits. The decl-level IsFrozen flag is the discriminator.
        var db = new TypeDatabase();
        var s = CreateNonFrozenStructDecl("NonFrozenTwoBoolOpt");
        foreach (var (fieldName, spec) in new (string, TypeSpec)[]
                 {
                     ("animate", OptionalOf("Swift.Bool")),
                     ("silent", OptionalOf("Swift.Bool")),
                 })
        {
            var prop = CreatePropertyDecl(fieldName, "Swift.Int", hasStorage: true);
            prop.SwiftTypeSpec = spec;
            s.Properties.Add(prop);
        }

        Assert.False(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_IndeterminateField_BailsConservatively_NoSkip()
    {
        // A sub-word optional FOLLOWED by a field whose layout is not precisely derivable (an unregistered
        // nested value-struct typed field — neither optional nor a fixed-width primitive). The simulator
        // cannot place the second field, so it bails (preserve existing behavior) rather than guess —
        // even though a sub-word optional is present.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("WithOpaque",
            ("flag", OptionalOf("Swift.Bool")),
            ("inner", new NamedTypeSpec("TestModule.Inner")));

        Assert.False(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_AllWholeWordPrimitives_NoSubWordParticipant_NoSkip()
    {
        // Int? + Int? : both whole-word (size9→word16, align8). No sub-word optional participates, so the
        // gate rail (anySubWordOptional) keeps it from ever firing regardless of offsets.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("TwoInt",
            ("a", OptionalOf("Swift.Int")),
            ("b", OptionalOf("Swift.Int")));

        Assert.False(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void SubWordOptionalMismatch_StaticOptionalIgnored_NoFalseSkip()
    {
        // A `static let flag: Bool?` has storage but lives in type metadata, not the instance value
        // layout. Were the static counted, Bool?@0(size1) followed by the instance Int32?@4 would
        // diverge from the C# word layout (@0/@8) and the struct would be WRONGLY skipped. With static
        // fields excluded (matching the emission loop), only the lone instance Int32? remains — which
        // lays out safely — so the predicate must report no mismatch.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithMixedFields("StaticBoolThenInstInt32",
            ("flag", OptionalOf("Swift.Bool"), /*isStatic*/ true),
            ("count", OptionalOf("Swift.Int32"), /*isStatic*/ false));

        Assert.False(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void IndeterminateBufferLayout_StaticIndeterminateFieldIgnored_NoFalseSkip()
    {
        // A Buffer-backed (RequiresMemoryManagement) frozen struct whose ONLY indeterminate-size stored
        // field is `static` — a generic value type (ClosedRange<Int>) with no persisted/derivable inline
        // size. Statics live in type metadata, never in the instance Buffer, so the instance layout (a
        // lone Int32? sized from the primitive table) is fully determinable. The predicate must NOT skip.
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithMixedFields("StaticIndeterminate",
            ("shared", new NamedTypeSpec("Swift.ClosedRange", new NamedTypeSpec("Swift.Int")), /*isStatic*/ true),
            ("count", OptionalOf("Swift.Int32"), /*isStatic*/ false));
        db.AddOutOfModuleTypes(new[]
        {
            (s.SwiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "StaticIndeterminate"),
                SwiftTypeName = s.SwiftTypeName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            }),
            (SwiftTypeName.FromModuleQualifiedName("Swift.ClosedRange"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "ClosedRange"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.ClosedRange"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                InlineSize = null, // per-instantiation size unknown cross-compile → indeterminate if instance
            }),
        });

        Assert.False(FrozenStructHandler.HasIndeterminateBufferLayout(s, db));
    }

    #endregion

    #region Nested reference-bearing field sizing (ClassifyFrozenStructField)

    [Fact]
    public void ClassifyFrozenStructField_NestedReferenceBearingFrozenStruct_ReservesItsRealInlineSize()
    {
        // The defect: a stored field whose OWN type is a reference-bearing frozen struct was sized as a
        // single pointer, so the Buffer mirror reserved 8 bytes for a value Swift lays out as 16 (a
        // frozen struct holding one Swift.String — MemoryLayout verified). Every blit through that
        // Buffer then wrote 8 bytes past the allocation. The nested record's parse-time declared layout
        // is the size source; the field must claim all 16 bytes.
        var db = new TypeDatabase();
        var leaf = RegisterNestedLeaf(db, "RefLeaf", new DeclaredValueLayout(16, 8));

        var kind = FrozenStructHandler.ClassifyFrozenStructField(leaf, db, out int byteSize);

        Assert.Equal(FrozenStructHandler.FrozenFieldLayoutKind.IntPtrFields, kind);
        Assert.Equal(16, byteSize);
    }

    [Fact]
    public void ClassifyFrozenStructField_OptionalNestedReferenceBearingFrozenStruct_KeepsPayloadWidth()
    {
        // Optional over a reference-bearing payload folds nil into a spare inhabitant, so the field is
        // exactly as wide as the payload — no appended discriminator. Sizing it as one pointer (the old
        // clamp) under-reserves by half; appending a tag byte would over-reserve and shift the field
        // after it in a mirror that did not round to words.
        var db = new TypeDatabase();
        var leaf = RegisterNestedLeaf(db, "RefLeaf", new DeclaredValueLayout(16, 8));

        var kind = FrozenStructHandler.ClassifyFrozenStructField(
            new NamedTypeSpec("Swift.Optional", leaf), db, out int byteSize);

        Assert.Equal(FrozenStructHandler.FrozenFieldLayoutKind.IntPtrFields, kind);
        Assert.Equal(16, byteSize);
    }

    [Fact]
    public void ClassifyFrozenStructField_NestedIndeterminateRecord_FailsClosed()
    {
        // The nested type's own layout derivation ran and could not produce a sound answer. The field
        // must be reported indeterminate so the containing struct's Buffer projection is skipped,
        // rather than silently reserving a guessed width.
        var db = new TypeDatabase();
        var leaf = RegisterNestedLeaf(db, "OpaqueLeaf", declaredLayout: null, declaredLayoutIndeterminate: true);

        var kind = FrozenStructHandler.ClassifyFrozenStructField(leaf, db, out _);

        Assert.Equal(FrozenStructHandler.FrozenFieldLayoutKind.Indeterminate, kind);
    }

    [Fact]
    public void ClassifyFrozenStructField_TrivialNestedFrozenStruct_StaysATypedField()
    {
        // A nested frozen struct with only trivial fields carries no references, so it is emitted as a
        // typed C# struct field that already has the right size and alignment. It must not be dragged
        // onto the IntPtr-word path (which would round its 4-byte alignment up to 8 and shift the
        // fields after it).
        var db = new TypeDatabase();
        var leaf = SwiftTypeName.FromModuleQualifiedName("TestModule.TrivialLeaf");
        db.AddOutOfModuleTypes(new[]
        {
            (leaf, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "TrivialLeaf"),
                SwiftTypeName = leaf,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen, // no RequiresMemoryManagement — trivial contents
                Kind = TypeRecordKind.Struct,
                DeclaredLayout = new DeclaredValueLayout(8, 4),
            }),
        });

        var kind = FrozenStructHandler.ClassifyFrozenStructField(
            new NamedTypeSpec("TestModule.TrivialLeaf"), db, out _);

        Assert.Equal(FrozenStructHandler.FrozenFieldLayoutKind.TypedField, kind);
    }

    [Fact]
    public void ClassifyFrozenStructField_TrivialFieldWithUnknownCustomAlignment_IsIndeterminate()
    {
        // The trivial arm is the one that never consults the reference-managed size resolver, so the
        // over-alignment has to be caught here or an `@_alignment(16)` struct of two Int32s emits as
        // an ordinary typed field at a pointer-aligned offset — the interior pad Swift inserts before
        // it is simply missing, and every later field lands short.
        var db = new TypeDatabase();
        var leaf = SwiftTypeName.FromModuleQualifiedName("TestModule.AlignedTrivialLeaf");
        db.AddOutOfModuleTypes(new[]
        {
            (leaf, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "AlignedTrivialLeaf"),
                SwiftTypeName = leaf,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen, // trivial: no RequiresMemoryManagement
                Kind = TypeRecordKind.Struct,
                InlineSize = 8, // the size is known and still does not make the field placeable
                HasUnknownCustomAlignment = true,
            }),
        });

        var kind = FrozenStructHandler.ClassifyFrozenStructField(
            new NamedTypeSpec("TestModule.AlignedTrivialLeaf"), db, out _);

        Assert.Equal(FrozenStructHandler.FrozenFieldLayoutKind.Indeterminate, kind);
    }

    [Fact]
    public void ClassifyFrozenStructField_TrivialFieldWithRecordedOverAlignment_IsIndeterminate()
    {
        // Same refusal when the alignment is RECORDED rather than unknown. The trivial arm runs no
        // size resolver at all, so a 16-aligned field would otherwise emit as a typed C# field at a
        // pointer-aligned offset and shorten every field after it.
        var db = new TypeDatabase();
        var leaf = SwiftTypeName.FromModuleQualifiedName("TestModule.WideAlignedTrivialLeaf");
        db.AddOutOfModuleTypes(new[]
        {
            (leaf, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "WideAlignedTrivialLeaf"),
                SwiftTypeName = leaf,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen, // trivial: no RequiresMemoryManagement
                Kind = TypeRecordKind.Struct,
                InlineSize = 32,
                DeclaredLayout = new DeclaredValueLayout(32, 16),
            }),
        });

        var kind = FrozenStructHandler.ClassifyFrozenStructField(
            new NamedTypeSpec("TestModule.WideAlignedTrivialLeaf"), db, out _);

        Assert.Equal(FrozenStructHandler.FrozenFieldLayoutKind.Indeterminate, kind);
    }

    [Fact]
    public void ClassifyFrozenStructField_TrivialFieldAtPointerAlignment_StaysATypedField()
    {
        // Positive control: the ordinary trivial field — a plain struct that aligns to at most a
        // pointer — must keep its typed C# field, or the two guards above skip every Buffer host.
        var db = new TypeDatabase();
        var leaf = SwiftTypeName.FromModuleQualifiedName("TestModule.PlainTrivialLeaf");
        db.AddOutOfModuleTypes(new[]
        {
            (leaf, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PlainTrivialLeaf"),
                SwiftTypeName = leaf,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16,
                DeclaredLayout = new DeclaredValueLayout(16, 8),
            }),
        });

        var kind = FrozenStructHandler.ClassifyFrozenStructField(
            new NamedTypeSpec("TestModule.PlainTrivialLeaf"), db, out _);

        Assert.Equal(FrozenStructHandler.FrozenFieldLayoutKind.TypedField, kind);
    }

    [Fact]
    public void HasIndeterminateBufferLayout_TrivialFieldWithUnknownCustomAlignment_Skips()
    {
        // End of that chain: the host must actually be skipped, not merely classified.
        var db = new TypeDatabase();
        var leafName = SwiftTypeName.FromModuleQualifiedName("TestModule.AlignedTrivialLeaf");
        db.AddOutOfModuleTypes(new[]
        {
            (leafName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "AlignedTrivialLeaf"),
                SwiftTypeName = leafName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 8,
                HasUnknownCustomAlignment = true,
            }),
        });

        var host = CreateFrozenStructWithStoredFields(
            "AlignedHost", ("aligned", new NamedTypeSpec("TestModule.AlignedTrivialLeaf")));
        RegisterBufferProjectedHost(db, host);

        Assert.True(FrozenStructHandler.HasIndeterminateBufferLayout(host, db));
    }

    [Fact]
    public void HasIndeterminateBufferLayout_NestedIndeterminateInstanceField_Skips()
    {
        // End of the fail-closed chain: an un-derivable nested field makes the whole Buffer-projected
        // host indeterminate, so the host is skipped instead of emitting a mirror of guessed width.
        var db = new TypeDatabase();
        var leaf = RegisterNestedLeaf(db, "OpaqueLeaf", declaredLayout: null, declaredLayoutIndeterminate: true);
        var host = CreateFrozenStructWithStoredFields("Host", ("nested", leaf));
        RegisterBufferProjectedHost(db, host);

        Assert.True(FrozenStructHandler.HasIndeterminateBufferLayout(host, db));
    }

    [Fact]
    public void HasIndeterminateBufferLayout_NestedDerivedField_DoesNotSkip()
    {
        // The positive control for the test above: once the nested record's layout IS derivable, the
        // host lays out fine and must keep emitting. A fail-closed arm that fired here would delete a
        // working type from the binding.
        var db = new TypeDatabase();
        var leaf = RegisterNestedLeaf(db, "RefLeaf", new DeclaredValueLayout(16, 8));
        var host = CreateFrozenStructWithStoredFields("Host", ("nested", leaf));
        RegisterBufferProjectedHost(db, host);

        Assert.False(FrozenStructHandler.HasIndeterminateBufferLayout(host, db));
    }

    /// <summary>
    /// Registers a nested reference-managed frozen struct (no persisted InlineSize, no live metadata —
    /// the cross-compile shape) whose only size source is its parse-time declared layout.
    /// </summary>
    private static NamedTypeSpec RegisterNestedLeaf(
        TypeDatabase db, string name, DeclaredValueLayout? declaredLayout, bool declaredLayoutIndeterminate = false)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}");
        db.AddOutOfModuleTypes(new[]
        {
            (swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = swiftName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                DeclaredLayout = declaredLayout,
                DeclaredLayoutIndeterminate = declaredLayoutIndeterminate,
            }),
        });
        return new NamedTypeSpec($"TestModule.{name}");
    }

    private static void RegisterBufferProjectedHost(TypeDatabase db, StructDecl host)
    {
        db.AddOutOfModuleTypes(new[]
        {
            (host.SwiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", host.Name),
                SwiftTypeName = host.SwiftTypeName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            }),
        });
    }

    #endregion

    #region ~Copyable by-value projection (HasNonCopyableValueProjection)

    [Fact]
    public void HasNonCopyableValueProjection_PlainMoveOnlyStruct_IsRefused()
    {
        // A frozen ~Copyable struct with no reference-bearing field projects to a plain C# struct:
        // no payload, so C# assignment silently copies a move-only Swift value, Dispose() is a no-op
        // even when the Swift type has a deinit, and a consuming parameter has nothing to mark.
        // That projection COMPILES, which is what puts it on the refusal side of the freeze policy
        // rather than leaving it to the C# verify-recover loop.
        var db = new TypeDatabase();
        var moveOnly = CreateFrozenStructDecl("MoveOnlyResource");
        RegisterStructRecord(db, moveOnly, TypeRecordFlags.Frozen | TypeRecordFlags.NonCopyable);

        Assert.True(FrozenStructHandler.HasNonCopyableValueProjection(moveOnly, db));
    }

    [Fact]
    public void HasNonCopyableValueProjection_MoveOnlyStructCarryingAReference_IsAdmitted()
    {
        // The boundary the refusal must not cross: a frozen ~Copyable struct that holds a reference
        // is projected as a Buffer-backed C# class, which really does carry a payload and already
        // enforces consumed lifetimes on it. A refusal wide enough to swallow this shape would
        // withdraw working surface.
        var db = new TypeDatabase();
        var moveOnly = CreateFrozenStructDecl("MoveOnlyHolder");
        RegisterStructRecord(
            db,
            moveOnly,
            TypeRecordFlags.Frozen | TypeRecordFlags.NonCopyable | TypeRecordFlags.RequiresMemoryManagement);

        Assert.False(FrozenStructHandler.HasNonCopyableValueProjection(moveOnly, db));
    }

    [Fact]
    public void HasNonCopyableValueProjection_OrdinaryCopyableStruct_IsAdmitted()
    {
        // The by-value projection is only unsound for a move-only value; an ordinary copyable frozen
        // struct is exactly what that projection is for.
        var db = new TypeDatabase();
        var copyable = CreateFrozenStructDecl("Point");
        RegisterStructRecord(db, copyable, TypeRecordFlags.Frozen);

        Assert.False(FrozenStructHandler.HasNonCopyableValueProjection(copyable, db));
    }

    [Fact]
    public void HasNonCopyableValueProjection_NonFrozenMoveOnlyStruct_IsAdmitted()
    {
        // A non-frozen struct is never projected by value — it becomes an opaque-payload class, so
        // its consumed-ownership handling has a payload to work with and nothing here should fire.
        var db = new TypeDatabase();
        var moveOnly = CreateNonFrozenStructDecl("ResilientMoveOnly");
        RegisterStructRecord(db, moveOnly, TypeRecordFlags.NonCopyable);

        Assert.False(FrozenStructHandler.HasNonCopyableValueProjection(moveOnly, db));
    }

    [Fact]
    public void HasNonCopyableValueProjection_TypeWithNoRecord_IsAdmitted()
    {
        // Copyability is a database fact; with no record there is no evidence of a move-only type and
        // the predicate must not refuse on a guess.
        var db = new TypeDatabase();
        var unregistered = CreateFrozenStructDecl("Unregistered");

        Assert.False(FrozenStructHandler.HasNonCopyableValueProjection(unregistered, db));
    }

    [Fact]
    public void FirstMatch_PlainMoveOnlyStruct_ReportsTheNonCopyableProjectionCondition()
    {
        // The condition has to reach the shared authority, not just the predicate: TypeSkipPrePass and
        // the type handlers both read TypeSkipConditions.FirstMatch, and that is what closes the
        // dependency chain so members referencing the refused type are pruned too.
        var db = new TypeDatabase();
        var moveOnly = CreateFrozenStructDecl("MoveOnlyResource");
        RegisterStructRecord(db, moveOnly, TypeRecordFlags.Frozen | TypeRecordFlags.NonCopyable);

        var match = TypeSkipConditions.FirstMatch(moveOnly, db, out _);

        Assert.NotNull(match);
        Assert.Equal(TypeSkipConditionKind.NonCopyableValueProjection, match!.Kind);
    }

    [Fact]
    public void FirstMatch_MoveOnlyStructCarryingAReference_IsNotSkipped()
    {
        var db = new TypeDatabase();
        var moveOnly = CreateFrozenStructDecl("MoveOnlyHolder");
        RegisterStructRecord(
            db,
            moveOnly,
            TypeRecordFlags.Frozen | TypeRecordFlags.NonCopyable | TypeRecordFlags.RequiresMemoryManagement);

        Assert.Null(TypeSkipConditions.FirstMatch(moveOnly, db, out _));
    }

    /// <summary>
    /// The refusal above is only as good as the flag it reads, and every test above hands that flag
    /// to the database itself. This one walks the real ingestion instead: a <c>~Copyable</c> struct
    /// reaches the ABI as a type conforming to <c>Swift.Escapable</c> and NOT to <c>Swift.Copyable</c>
    /// (an ordinary Swift 6.2 struct lists both), and <c>ModuleProcessor</c> is what turns that into
    /// <see cref="TypeRecordFlags.NonCopyable"/>. Without this, ingestion could stop deriving the flag
    /// and the whole refusal would go quiet while every hand-seeded test above still passed.
    /// </summary>
    [Fact]
    public void Ingestion_OfAMoveOnlyStruct_DerivesTheFlagThatDrivesTheRefusal()
    {
        var moveOnly = CreateFrozenStructDecl("IngestedMoveOnly");
        moveOnly.Properties.Add(CreatePropertyDecl("value", "Swift.Int64", hasStorage: true));
        DeclareMoveOnly(moveOnly);

        var record = DeriveThroughIngestion(moveOnly);

        Assert.True((record.Flags & TypeRecordFlags.NonCopyable) != 0);
        Assert.True((record.Flags & TypeRecordFlags.Frozen) != 0);

        // Trivial fields only, so nothing puts it on the payload-backed class projection — which is
        // exactly the by-value shape the refusal exists for.
        Assert.True((record.Flags & TypeRecordFlags.RequiresMemoryManagement) == 0);
    }

    /// <summary>
    /// The seam itself: a declaration that only ever passed through the real parser derivation is
    /// refused by the shared condition list, with no hand-written record anywhere in the path.
    /// </summary>
    [Fact]
    public void Ingestion_OfAMoveOnlyStruct_ReachesTheRefusalWithoutAHandWrittenRecord()
    {
        var moveOnly = CreateFrozenStructDecl("IngestedMoveOnly");
        moveOnly.Properties.Add(CreatePropertyDecl("value", "Swift.Int64", hasStorage: true));
        DeclareMoveOnly(moveOnly);

        var db = DatabaseAfterIngestion(moveOnly);
        var match = TypeSkipConditions.FirstMatch(moveOnly, db, out _);

        Assert.NotNull(match);
        Assert.Equal(TypeSkipConditionKind.NonCopyableValueProjection, match!.Kind);
    }

    /// <summary>
    /// The control for the derivation rule, through the same path: a struct that lists BOTH
    /// <c>Swift.Copyable</c> and <c>Swift.Escapable</c> is an ordinary copyable Swift 6.2 struct and
    /// must keep projecting. Reading "conforms to Escapable" alone would refuse every struct in a
    /// 6.2-built module.
    /// </summary>
    [Fact]
    public void Ingestion_OfAnOrdinaryCopyableStruct_IsNotRefused()
    {
        var copyable = CreateFrozenStructDecl("IngestedCopyable");
        copyable.Properties.Add(CreatePropertyDecl("value", "Swift.Int64", hasStorage: true));
        DeclareConformance(copyable, "Swift.Copyable");
        DeclareConformance(copyable, "Swift.Escapable");

        var db = DatabaseAfterIngestion(copyable);

        Assert.True((DeriveThroughIngestion(copyable).Flags & TypeRecordFlags.NonCopyable) == 0);
        Assert.Null(TypeSkipConditions.FirstMatch(copyable, db, out _));
    }

    /// <summary>
    /// The boundary, through the same path: the identical <c>~Copyable</c> declaration carrying a
    /// CLASS-typed stored field picks up <see cref="TypeRecordFlags.RequiresMemoryManagement"/> from
    /// ingestion, projects as a payload-backed class, and must keep its whole member surface.
    /// </summary>
    [Fact]
    public void Ingestion_OfAMoveOnlyStructCarryingAReference_IsNotRefused()
    {
        var reference = CreateClassDecl("IngestedBox");
        var moveOnly = CreateFrozenStructDecl("IngestedMoveOnlyHolder");
        moveOnly.Properties.Add(CreatePropertyDecl("box", "TestModule.IngestedBox", hasStorage: true));
        DeclareMoveOnly(moveOnly);

        var db = DatabaseAfterIngestion(moveOnly, reference);

        Assert.True(db.TryGetTypeRecord(moveOnly.SwiftTypeName, out var record));
        Assert.True((record!.Flags & TypeRecordFlags.NonCopyable) != 0);
        Assert.True((record.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0);
        Assert.Null(TypeSkipConditions.FirstMatch(moveOnly, db, out _));
    }

    #endregion

    #region ~Copyable payload construction (move, not copy)

    [Fact]
    public void FrozenPayloadSemantics_MoveOnlyStructProjectedAsClass_IsMove()
    {
        // The bug this pins: a ~Copyable payload declared Copy makes the emitted NewFromPayload run
        // ValueWitnessTable->InitializeWithCopy, and a non-copyable type's copy witness is
        // __swift_cannot_copy_noncopyable_type — an unconditional EXC_BREAKPOINT, not an error.
        // Move is the arm that both constructs by take and suppresses the wire buffer's destroy.
        var record = StructRecord(
            "MoveOnlyHolder",
            TypeRecordFlags.Frozen | TypeRecordFlags.NonCopyable | TypeRecordFlags.RequiresMemoryManagement);

        Assert.Equal(
            Swift.Runtime.PayloadConstructionSemantics.Move,
            ISwiftObjectMethodWriter.SelectFrozenStructPayloadSemantics(record, isNonCopyable: true));
    }

    [Fact]
    public void FrozenPayloadSemantics_CopyableStructProjectedAsClass_StaysCopy()
    {
        // The regression guard: an ordinary reference-bearing frozen struct still takes a fresh +1
        // and still has its wire buffer destroyed. Nothing about the ~Copyable fix may move it.
        var record = StructRecord(
            "ReferenceBearingPoint",
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);

        Assert.Equal(
            Swift.Runtime.PayloadConstructionSemantics.Copy,
            ISwiftObjectMethodWriter.SelectFrozenStructPayloadSemantics(record, isNonCopyable: false));
    }

    [Fact]
    public void FrozenPayloadSemantics_ValueTypeProjection_StaysInline()
    {
        // A frozen struct with no reference-bearing field is read by value; it has no payload buffer
        // to move OR copy, so the copyability question never arises on this arm. (A ~Copyable one
        // cannot reach it at all — HasNonCopyableValueProjection refuses the type outright.)
        var record = StructRecord("PlainPoint", TypeRecordFlags.Frozen);

        Assert.Equal(
            Swift.Runtime.PayloadConstructionSemantics.Inline,
            ISwiftObjectMethodWriter.SelectFrozenStructPayloadSemantics(record, isNonCopyable: false));
        Assert.Equal(
            Swift.Runtime.PayloadConstructionSemantics.Inline,
            ISwiftObjectMethodWriter.SelectFrozenStructPayloadSemantics(record, isNonCopyable: true));
    }

    [Fact]
    public void NewFromPayload_MoveOnlyStructProjectedAsClass_TakesTheWireValue()
    {
        var emitted = ISwiftObjectMethodWriter.BuildNewFromPayloadProjectedAsClass(
            "MoveOnlyHolder", "MoveOnlyHolder", isNonCopyable: true);

        Assert.Contains("ValueWitnessTable->InitializeWithTake((void*)bufferPtr", emitted);
        Assert.DoesNotContain("InitializeWithCopy", emitted);
    }

    [Fact]
    public void NewFromPayload_CopyableStructProjectedAsClass_StillCopies()
    {
        var emitted = ISwiftObjectMethodWriter.BuildNewFromPayloadProjectedAsClass(
            "ReferenceBearingPoint", "ReferenceBearingPoint", isNonCopyable: false);

        Assert.Contains("ValueWitnessTable->InitializeWithCopy((void*)bufferPtr", emitted);
        Assert.DoesNotContain("InitializeWithTake", emitted);
    }

    [Fact]
    public void MarshalToSwift_MoveOnlyPayload_MovesOutAndMarksConsumed()
    {
        // The other direction, and the second instance of the same trap: handing a ~Copyable value
        // to Swift cannot be a copy either. It takes, then marks the handle consumed so the
        // SafeHandle frees the moved-from buffer without a second value-witness destroy.
        var emitted = ISwiftObjectMethodWriter.BuildMarshalToSwiftFromPayload(
            "MoveOnlyHolder", "MoveOnlyHolder", isNonCopyable: true);

        Assert.Contains("ValueWitnessTable->InitializeWithTake(swiftDest", emitted);
        Assert.DoesNotContain("InitializeWithCopy", emitted);
        Assert.Contains("_payload.MarkConsumed();", emitted);
        // And a value already moved out has nothing left to hand over.
        Assert.Contains("if (_payload.IsConsumed)", emitted);
        Assert.Contains("ObjectDisposedException", emitted);
    }

    [Fact]
    public void MarshalToSwift_CopyablePayload_StillCopiesAndKeepsOwnership()
    {
        var emitted = ISwiftObjectMethodWriter.BuildMarshalToSwiftFromPayload(
            "ReferenceBearingPoint", "ReferenceBearingPoint", isNonCopyable: false);

        Assert.Contains("ValueWitnessTable->InitializeWithCopy(swiftDest", emitted);
        Assert.DoesNotContain("InitializeWithTake", emitted);
        Assert.DoesNotContain("MarkConsumed", emitted);
    }

    /// <summary>
    /// The non-frozen lane keeps <c>Adopt</c> whether or not the struct is <c>~Copyable</c>: its
    /// <c>NewFromPayload</c> wraps the wire handle directly with no value-witness call at all, so
    /// there is no copy to trap on and nothing for the fix to change there. Only the frozen lane's
    /// alloc-and-initialize construction had to move. Stated as a test so a later "declare Move for
    /// every ~Copyable" edit has to confront it — declaring Move on an ADOPTING carrier would tell
    /// the seam to free a buffer the SafeHandle already owns.
    /// </summary>
    [Fact]
    public void NonFrozenLane_MoveOnlyStruct_IsNotAFrozenClassProjection()
    {
        var db = new TypeDatabase();
        var moveOnly = CreateNonFrozenStructDecl("ResilientMoveOnly");
        RegisterStructRecord(db, moveOnly, TypeRecordFlags.NonCopyable);

        Assert.True(db.TryGetTypeRecord(moveOnly.SwiftTypeName, out var record));
        // Not a frozen class projection, so SelectFrozenStructPayloadSemantics never runs for it and
        // its declaration site keeps the hardcoded Adopt.
        Assert.False(MarshallingHelpers.IsFrozenStructProjectedAsClass(record!));
    }

    /// <summary>
    /// The predicate the emitter branches on is the shared <c>~Copyable</c> oracle, reading the ABI
    /// shape (Escapable listed, Copyable absent) rather than a second hand-rolled rule.
    /// </summary>
    [Fact]
    public void NonCopyablePredicate_ReadsTheAbiConformanceShape()
    {
        var moveOnly = CreateFrozenStructDecl("MoveOnlyHolder");
        DeclareMoveOnly(moveOnly);
        Assert.True(WrapperValidation.IsNonCopyableStructParent(moveOnly));

        var copyable = CreateFrozenStructDecl("OrdinaryHolder");
        DeclareConformance(copyable, "Swift.Copyable");
        DeclareConformance(copyable, "Swift.Escapable");
        Assert.False(WrapperValidation.IsNonCopyableStructParent(copyable));
    }

    /// <summary>
    /// <c>Optional&lt;T&gt;</c> is itself move-only when <c>T</c> is: a predicate that stopped at the
    /// <c>Swift.Optional</c> spelling would report a copyable value whose copy witness is an
    /// unconditional runtime trap.
    /// </summary>
    [Fact]
    public void IsNonCopyableType_OptionalOfMoveOnlyStruct_ReturnsTrue()
    {
        var db = new TypeDatabase();
        var moveOnly = CreateFrozenStructDecl("MoveOnlyResource");
        DeclareMoveOnly(moveOnly);
        RegisterStructRecord(db, moveOnly, TypeRecordFlags.Frozen | TypeRecordFlags.NonCopyable);
        var module = ModuleContaining(moveOnly);

        Assert.True(WrapperValidation.IsNonCopyableType(
            OptionalOf(moveOnly.SwiftTypeName.ModuleQualifiedName), db, module));
    }

    /// <summary>
    /// Recursion must not paint every Optional as move-only: wrapping an ordinary copyable struct
    /// stays copyable, otherwise every <c>T?</c> would be refused as a ~Copyable payload.
    /// </summary>
    [Fact]
    public void IsNonCopyableType_OptionalOfCopyableStruct_ReturnsFalse()
    {
        var db = new TypeDatabase();
        var copyable = CreateFrozenStructDecl("Point");
        DeclareConformance(copyable, "Swift.Copyable");
        DeclareConformance(copyable, "Swift.Escapable");
        RegisterStructRecord(db, copyable, TypeRecordFlags.Frozen);
        var module = ModuleContaining(copyable);

        Assert.False(WrapperValidation.IsNonCopyableType(
            OptionalOf(copyable.SwiftTypeName.ModuleQualifiedName), db, module));
    }

    /// <summary>
    /// A directly-named ~Copyable struct is still non-copyable after Optional became transparent:
    /// the named-type arm must keep answering yes, not only the generic-argument walk.
    /// </summary>
    [Fact]
    public void IsNonCopyableType_DirectlyNamedMoveOnlyStruct_ReturnsTrue()
    {
        var db = new TypeDatabase();
        var moveOnly = CreateFrozenStructDecl("MoveOnlyResource");
        DeclareMoveOnly(moveOnly);
        RegisterStructRecord(db, moveOnly, TypeRecordFlags.Frozen | TypeRecordFlags.NonCopyable);
        var module = ModuleContaining(moveOnly);

        Assert.True(WrapperValidation.IsNonCopyableType(
            new NamedTypeSpec(moveOnly.SwiftTypeName.ModuleQualifiedName), db, module));
    }

    /// <summary>
    /// The parent oracle's name is historical: a ~Copyable enum (Escapable listed, Copyable absent)
    /// is move-only exactly as a ~Copyable struct is. Answering only for structs would let an
    /// enum's members take the copy-constructing emission path, whose value-witness copy traps at
    /// runtime. A copyable enum listing both Copyable and Escapable must stay on the copyable path.
    /// </summary>
    [Fact]
    public void IsNonCopyableStructParent_MoveOnlyEnum_ReturnsTrue_CopyableEnum_ReturnsFalse()
    {
        var moveOnly = CreateEnumDecl("MoveOnlyCase");
        DeclareMoveOnly(moveOnly);
        Assert.True(WrapperValidation.IsNonCopyableStructParent(moveOnly));

        var copyable = CreateEnumDecl("OrdinaryCase");
        DeclareConformance(copyable, "Swift.Copyable");
        DeclareConformance(copyable, "Swift.Escapable");
        Assert.False(WrapperValidation.IsNonCopyableStructParent(copyable));
    }

    private static TypeRecord StructRecord(string name, TypeRecordFlags flags)
        => new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MetadataAccessor = "",
            Flags = flags,
            Kind = TypeRecordKind.Struct,
        };

    #endregion

    #region ~Copyable helpers

    /// <summary>
    /// Marks a declaration the way the ABI describes a <c>~Copyable</c> type: Escapable listed,
    /// Copyable absent.
    /// </summary>
    private static ModuleDecl ModuleContaining(TypeDecl typeDecl)
        => new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { typeDecl },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static void DeclareMoveOnly(StructDecl structDecl)
        => DeclareConformance(structDecl, "Swift.Escapable");

    private static void DeclareMoveOnly(EnumDecl enumDecl)
        => DeclareConformance(enumDecl, "Swift.Escapable");

    private static void DeclareConformance(StructDecl structDecl, string protocolName)
        => structDecl.Conformances.Add(new TypeConformance(
            structDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName(protocolName),
            ProtocolConformanceDescriptor: string.Empty));

    private static void DeclareConformance(EnumDecl enumDecl, string protocolName)
        => enumDecl.Conformances.Add(new TypeConformance(
            enumDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName(protocolName),
            ProtocolConformanceDescriptor: string.Empty));

    private static TypeRecord DeriveThroughIngestion(TypeDecl queried, params TypeDecl[] alsoDeclared)
    {
        var db = DatabaseAfterIngestion(queried, alsoDeclared);
        Assert.True(db.TryGetTypeRecord(queried.SwiftTypeName, out var record));
        return record!;
    }

    /// <summary>
    /// Runs the real <see cref="ModuleProcessor"/> over the given declarations and returns the
    /// database it produced, so the flags under test are the ones ingestion actually derives.
    /// </summary>
    private static TypeDatabase DatabaseAfterIngestion(TypeDecl queried, params TypeDecl[] alsoDeclared)
    {
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>();
        foreach (var decl in alsoDeclared.Append(queried))
            typeDecls[new NamedTypeSpec(decl.SwiftTypeName.ModuleQualifiedName)] = decl;

        var typeDatabase = new TypeDatabase();
        var int64 = SwiftTypeName.FromModuleQualifiedName("Swift.Int64");
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (int64, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "Int64"),
                SwiftTypeName = int64,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            }),
        });

        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/TestModule.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        typeDatabase.AddModuleDatabase(processor.FinalizeTypeProcessingAndCreateModuleDatabase().ModuleDatabase);
        return typeDatabase;
    }

    private static void RegisterStructRecord(TypeDatabase db, StructDecl structDecl, TypeRecordFlags flags)
    {
        db.AddOutOfModuleTypes(new[]
        {
            (structDecl.SwiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", structDecl.Name),
                SwiftTypeName = structDecl.SwiftTypeName,
                MetadataAccessor = "",
                Flags = flags,
                Kind = TypeRecordKind.Struct,
            }),
        });
    }

    #endregion

    #region Field-shape helpers

    private static NamedTypeSpec OptionalOf(string innerSwiftName)
        => new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(innerSwiftName));

    private static StructDecl CreateFrozenStructWithStoredFields(
        string name, params (string fieldName, TypeSpec spec)[] fields)
    {
        var s = CreateFrozenStructDecl(name);
        foreach (var (fieldName, spec) in fields)
        {
            var prop = CreatePropertyDecl(fieldName, "Swift.Int", hasStorage: true);
            prop.SwiftTypeSpec = spec;
            s.Properties.Add(prop);
        }
        return s;
    }

    private static StructDecl CreateFrozenStructWithMixedFields(
        string name, params (string fieldName, TypeSpec spec, bool isStatic)[] fields)
    {
        var s = CreateFrozenStructDecl(name);
        foreach (var (fieldName, spec, isStatic) in fields)
        {
            var prop = CreatePropertyDecl(fieldName, "Swift.Int", hasStorage: true);
            prop.SwiftTypeSpec = spec;
            prop.IsStatic = isStatic;
            s.Properties.Add(prop);
        }
        return s;
    }

    #endregion

    #region Helper Methods

    private static StructDecl CreateFrozenStructDecl(string name, string moduleName = "TestModule")
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = ""
        };
    }

    private static StructDecl CreateNonFrozenStructDecl(string name, string moduleName = "TestModule")
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = false,
            MetadataAccessor = ""
        };
    }

    private static ClassDecl CreateClassDecl(string name, string moduleName = "TestModule")
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static EnumDecl CreateEnumDecl(string name, string moduleName = "TestModule")
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}ON",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Cases = new List<EnumCaseDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = false,
            MetadataAccessor = ""
        };
    }

    private static ProtocolDecl CreateProtocolDecl(string name, string moduleName = "TestModule")
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static PropertyDecl CreatePropertyDecl(string name, string typeName, bool hasStorage = false)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            IsStatic = false,
            HasStorage = hasStorage,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = $"$s{name}g",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>(),
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = null,
                        ModuleDecl = null,
                        Throws = false,
                        IsAsync = false,
                        IsSynthesizedAccessor = true
                    }
                }
            },
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static MethodDecl CreateMethodDecl(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static OperatorDecl CreateOperatorDecl(string symbol, OperatorKind kind, bool isPrefix = true)
    {
        var methodDecl = new MethodDecl
        {
            Name = symbol,
            MangledName = $"$s{symbol}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                    Name = "left",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        if (kind == OperatorKind.Binary)
        {
            methodDecl.CSSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                Name = "right",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            });
        }

        return new OperatorDecl
        {
            Name = symbol,
            OperatorSymbol = symbol,
            Kind = kind,
            IsPrefix = isPrefix,
            UnderlyingMethod = methodDecl,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static GenericArgumentDecl CreateGenericArgumentDecl(string name)
    {
        return new GenericArgumentDecl(
            TypeName: name,
            SugaredTypeName: name,
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>()
        );
    }

    private static GenericArgumentDecl CreateGenericArgumentDeclWithConformance(string name, string conformance)
    {
        return new GenericArgumentDecl(
            TypeName: name,
            SugaredTypeName: name,
            GenericConformances: new List<GenericParameterConformance>
            {
                new GenericParameterConformance(
                    Path: new[] { name },
                    ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(conformance),
                    Kind: ConformanceKind.Protocol
                )
            },
            AssosiatedTypeConformances: new List<GenericParameterConformance>()
        );
    }

    #endregion
}
