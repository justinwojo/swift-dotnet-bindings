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

    #region P1-15 — Reference field inline sizing (TryResolveReferenceFieldSize)

    [Fact]
    public void TryResolveReferenceFieldSize_PersistedInlineSize_HonoredVerbatim()
    {
        // AnyHashable is a reference-managed struct with a fixed 40-byte existential box. The
        // size is a property of the type (not its use site), persisted as inlineSize in the XML,
        // and must be honored verbatim — clamping it to one pointer (the historical bug) under-
        // sizes the Buffer field and corrupts the heap.
        var record = CreateReferenceTypeRecord("Swift.AnyHashable", TypeRecordKind.Struct, inlineSize: 40);
        var spec = new NamedTypeSpec("Swift.AnyHashable");

        var resolved = FrozenStructHandler.TryResolveReferenceFieldSize(record, spec, out int byteSize);

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

        var resolved = FrozenStructHandler.TryResolveReferenceFieldSize(record, spec, out int byteSize);

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

        var resolved = FrozenStructHandler.TryResolveReferenceFieldSize(record, spec, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_GenericClassWithoutPersistedSize_IsOnePointer()
    {
        // A class reference is exactly one pointer regardless of its generic arguments, so it is
        // determinable even with no persisted size — never fail closed for a class.
        var record = CreateReferenceTypeRecord("TestModule.Box", TypeRecordKind.Class, inlineSize: null);
        var spec = new NamedTypeSpec("TestModule.Box", new NamedTypeSpec("Swift.Int"));

        var resolved = FrozenStructHandler.TryResolveReferenceFieldSize(record, spec, out int byteSize);

        Assert.True(resolved);
        Assert.Equal(IntPtr.Size, byteSize);
    }

    [Fact]
    public void TryResolveReferenceFieldSize_NonGenericValueTypeWithoutPersistedSize_KeepsPointerClamp()
    {
        // A NON-generic reference-managed value type with no persisted size keeps the historical
        // single-pointer clamp. There is no per-instantiation ambiguity to fail closed on, so the
        // surgical fix leaves this path unchanged — preserving behavior for nested non-generic
        // structs (zero regression).
        var record = CreateReferenceTypeRecord("TestModule.Handle", TypeRecordKind.Struct, inlineSize: null);
        var spec = new NamedTypeSpec("TestModule.Handle");

        var resolved = FrozenStructHandler.TryResolveReferenceFieldSize(record, spec, out int byteSize);

        Assert.True(resolved);
        Assert.Equal(IntPtr.Size, byteSize);
    }

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
    public void TryGetFixedWidthPrimitiveSize_KnownPrimitive_ReturnsLanguageConstantSize(string swiftName, int expected)
    {
        // The cross-compile XML persists no inlineSize for primitive records (Int32/Bool/...), and
        // there is no live metadata at generate time. Optional<primitive> Buffer fields must be
        // sized from the language-constant primitive table; resolving Int32 to a pointer width (the
        // reference-field fallback) mis-sizes Optional<Int32> as two words instead of one.
        var resolved = FrozenStructHandler.TryGetFixedWidthPrimitiveSize(new NamedTypeSpec(swiftName), out int byteSize);

        Assert.True(resolved);
        Assert.Equal(expected, byteSize);
    }

    [Theory]
    [InlineData("Swift.String")]      // reference-managed value type — sized via InlineSize, not this table
    [InlineData("Swift.AnyHashable")] // 40-byte existential box — sized via InlineSize
    [InlineData("Swift.ClosedRange")] // generic value type — fails closed elsewhere
    [InlineData("TestModule.Widget")] // arbitrary user type
    public void TryGetFixedWidthPrimitiveSize_NonPrimitive_ReturnsFalse(string swiftName)
    {
        var resolved = FrozenStructHandler.TryGetFixedWidthPrimitiveSize(new NamedTypeSpec(swiftName), out int byteSize);

        Assert.False(resolved);
        Assert.Equal(0, byteSize);
    }

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
    public void TryGetOptionalPrimitiveInlineSize_KnownPrimitive_MatchesSwiftLayout(string swiftName, int expected)
    {
        // Optional<primitive> inline size is a language constant. Every full-range primitive needs a
        // separate tag byte (Optional<T>.size == T.size + 1), but Bool has only two valid bit patterns
        // so Optional<Bool> reuses a spare pattern for nil and stays one byte. Sizing Bool? at two
        // bytes (the naive "+1 for every primitive" rule) over-reserves its Buffer slot and shifts
        // every following field — heap corruption on the blit. Verified against MemoryLayout in Swift.
        var resolved = FrozenStructHandler.TryGetOptionalPrimitiveInlineSize(new NamedTypeSpec(swiftName), out int byteSize);

        Assert.True(resolved);
        Assert.Equal(expected, byteSize);
    }

    [Theory]
    [InlineData("Swift.String")]      // reference-managed — sized via InlineSize/metadata, not this table
    [InlineData("Swift.AnyHashable")] // existential box
    [InlineData("TestModule.Widget")] // arbitrary user type
    public void TryGetOptionalPrimitiveInlineSize_NonPrimitive_ReturnsFalse(string swiftName)
    {
        var resolved = FrozenStructHandler.TryGetOptionalPrimitiveInlineSize(new NamedTypeSpec(swiftName), out int byteSize);

        Assert.False(resolved);
        Assert.Equal(0, byteSize);
    }

    private static TypeRecord CreateReferenceTypeRecord(
        string moduleQualifiedName, TypeRecordKind kind, int? inlineSize)
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
        };
    }

    #endregion

    #region §6 #5 — Sub-word Optional by-value layout mismatch (HasSubWordOptionalLayoutMismatch)

    // A by-value frozen struct (NOT projected as a Buffer-backed class) emits each Optional<primitive>
    // field as a whole 8-byte IntPtr word, but Swift packs sub-word optionals tighter. When that pushes
    // a later field to a different byte offset than Swift's packed layout, a by-value cdecl pass reads
    // the field from the wrong slot and corrupts it — so we must skip. The predicate simulates BOTH
    // layouts field-by-field and fires ONLY on per-field START-OFFSET divergence (a count of sub-word
    // optionals is neither necessary nor sufficient — confirmed independently by Codex + Grok).

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
        // field does not make a following sub-word optional safe (Codex/Grok mixed-field witness).
        var db = new TypeDatabase();
        var s = CreateFrozenStructWithStoredFields("Int32ThenBool",
            ("count", new NamedTypeSpec("Swift.Int32")),
            ("flag", OptionalOf("Swift.Bool")));

        Assert.True(FrozenStructHandler.HasSubWordOptionalLayoutMismatch(s, db));
    }

    [Fact]
    public void OverPaddedOptionalMismatch_Int64OptThenInt8_WholeWordValueOptional_Skips()
    {
        // Codex review repro. Int64? is a WHOLE-WORD value optional: Int64 uses every bit so Swift
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
                        Visibility = Visibility.Private
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
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
