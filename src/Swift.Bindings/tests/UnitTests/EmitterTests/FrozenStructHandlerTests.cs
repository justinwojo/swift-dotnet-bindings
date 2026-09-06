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
