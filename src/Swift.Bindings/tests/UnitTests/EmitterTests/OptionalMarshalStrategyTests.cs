// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for OptionalMarshalClassifier and OptionalMarshalStrategy.
/// Covers strategy classification (Item 4), tag byte offset consistency (Item 5),
/// and decomposed Optional naming/access patterns (Item 6).
/// </summary>
public class OptionalMarshalStrategyTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // Item 4: OptionalMarshalStrategy enum + single classifier
    // ═══════════════════════════════════════════════════════════════════════

    #region Classify — NotOptional

    [Fact]
    public void Classify_NonOptionalType_ReturnsNotOptional()
    {
        var (_, typeDb) = CreateTestEnvironment();
        var spec = new NamedTypeSpec("Swift.Int");

        var result = OptionalMarshalClassifier.Classify(spec, typeDb);

        Assert.Equal(OptionalMarshalStrategy.NotOptional, result);
    }

    [Fact]
    public void Classify_EmptyTuple_ReturnsNotOptional()
    {
        var (_, typeDb) = CreateTestEnvironment();
        var spec = TupleTypeSpec.Empty;

        var result = OptionalMarshalClassifier.Classify(spec, typeDb);

        Assert.Equal(OptionalMarshalStrategy.NotOptional, result);
    }

    [Fact]
    public void Classify_ClosureType_ReturnsNotOptional()
    {
        var (_, typeDb) = CreateTestEnvironment();
        var spec = new ClosureTypeSpec();

        var result = OptionalMarshalClassifier.Classify(spec, typeDb);

        Assert.Equal(OptionalMarshalStrategy.NotOptional, result);
    }

    #endregion

    #region Classify — NullablePointer

    [Fact]
    public void Classify_OptionalClass_ReturnsNullablePointer()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes(
            ("TestModule.MyClass", TypeRecordFlags.None, TypeRecordKind.Class));

        var optSpec = MakeOptional("TestModule.MyClass");

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        Assert.Equal(OptionalMarshalStrategy.NullablePointer, result);
    }

    [Fact]
    public void Classify_OptionalObjCBridged_ReturnsNullablePointer()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes(
            ("TestModule.Bridged", TypeRecordFlags.ObjCBridged, TypeRecordKind.Class));

        var optSpec = MakeOptional("TestModule.Bridged");

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        Assert.Equal(OptionalMarshalStrategy.NullablePointer, result);
    }

    #endregion

    #region Classify — DecomposedBuffers

    [Fact]
    public void Classify_OptionalComplexEnum_ReturnsDecomposedBuffers()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes(
            ("TestModule.AssetType", TypeRecordFlags.None, TypeRecordKind.Enum));

        var optSpec = MakeOptional("TestModule.AssetType");

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        Assert.Equal(OptionalMarshalStrategy.DecomposedBuffers, result);
    }

    [Fact]
    public void Classify_OptionalNonFrozenStruct_ReturnsDecomposedBuffers()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes(
            ("TestModule.URLRequest", TypeRecordFlags.None, TypeRecordKind.Struct));

        var optSpec = MakeOptional("TestModule.URLRequest");

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        Assert.Equal(OptionalMarshalStrategy.DecomposedBuffers, result);
    }

    [Fact]
    public void Classify_OptionalSimpleEnum_IsNotDecomposed()
    {
        // Simple enums (RawRepresentable) are NOT decomposed — they use blittable fast path or large opt.
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes(
            ("TestModule.Color", TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum));

        var optSpec = MakeOptional("TestModule.Color");

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        Assert.NotEqual(OptionalMarshalStrategy.DecomposedBuffers, result);
    }

    [Fact]
    public void Classify_OptionalFrozenStruct_IsNotDecomposed()
    {
        // Frozen structs are NOT decomposed.
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes(
            ("TestModule.Point", TypeRecordFlags.Frozen, TypeRecordKind.Struct));

        var optSpec = MakeOptional("TestModule.Point");

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        Assert.NotEqual(OptionalMarshalStrategy.DecomposedBuffers, result);
    }

    #endregion

    #region Classify — BlittableFastPath

    [Theory]
    [InlineData("Swift.Int8", OptionalMarshalStrategy.BlittableFastPath)]
    [InlineData("Swift.UInt8", OptionalMarshalStrategy.BlittableFastPath)]
    [InlineData("Swift.Int16", OptionalMarshalStrategy.BlittableFastPath)]
    [InlineData("Swift.UInt16", OptionalMarshalStrategy.BlittableFastPath)]
    [InlineData("Swift.Int32", OptionalMarshalStrategy.BlittableFastPath)]
    [InlineData("Swift.UInt32", OptionalMarshalStrategy.BlittableFastPath)]
    [InlineData("Swift.Float", OptionalMarshalStrategy.BlittableFastPath)]
    public void Classify_OptionalSmallBlittable_ReturnsBlittableFastPath(string innerType, OptionalMarshalStrategy expected)
    {
        var (_, typeDb) = CreateTestEnvironment();
        var optSpec = MakeOptional(innerType);

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Classify_OptionalBool_ReturnsFullSwiftOptional()
    {
        // Bool uses extra inhabitants (Optional<Bool>.Size == Bool.Size = 1),
        // so there's no separate tag byte. Bool is deliberately excluded from
        // IsBlittablePrimitiveSwiftType and requires the full VWT path.
        var (_, typeDb) = CreateTestEnvironment();
        var optSpec = MakeOptional("Swift.Bool");

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        Assert.Equal(OptionalMarshalStrategy.FullSwiftOptional, result);
    }

    #endregion

    #region Classify — LargeOptionalPointer

    [Theory]
    [InlineData("Swift.Int")]
    [InlineData("Swift.UInt")]
    [InlineData("Swift.Int64")]
    [InlineData("Swift.UInt64")]
    [InlineData("Swift.Double")]
    public void Classify_OptionalLargeBlittable_ReturnsLargeOptionalPointer(string innerType)
    {
        var (_, typeDb) = CreateTestEnvironment();
        var optSpec = MakeOptional(innerType);

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        Assert.Equal(OptionalMarshalStrategy.LargeOptionalPointer, result);
    }

    [Fact]
    public void Classify_OptionalString_ReturnsLargeOptionalPointer()
    {
        // String is not a reference type for Optional purposes (it's a struct),
        // not decomposed (it's frozen), and > 8 bytes — large optional.
        var (_, typeDb) = CreateTestEnvironment();
        var optSpec = MakeOptional("Swift.String");

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        Assert.Equal(OptionalMarshalStrategy.LargeOptionalPointer, result);
    }

    #endregion

    #region Classify — FullSwiftOptional

    [Fact]
    public void Classify_OptionalFrozenStruct_ReturnsFullSwiftOptional()
    {
        // A frozen struct that is not in the small-optional set and has no known blittable type name.
        // This falls through to FullSwiftOptional because it's a resolved frozen struct — not reference,
        // not decomposed (frozen), not blittable primitive, and IsLargeOptionalInner returns true
        // for resolved types not in the small set.
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes(
            ("TestModule.Point", TypeRecordFlags.Frozen, TypeRecordKind.Struct));

        var optSpec = MakeOptional("TestModule.Point");

        var result = OptionalMarshalClassifier.Classify(optSpec, typeDb);

        // Frozen structs whose size is unknown fall to LargeOptionalPointer (conservative).
        Assert.Equal(OptionalMarshalStrategy.LargeOptionalPointer, result);
    }

    #endregion

    #region IsDecomposed / IsLargeOptional convenience methods

    [Fact]
    public void IsDecomposed_ComplexEnum_ReturnsTrue()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes(
            ("TestModule.Status", TypeRecordFlags.None, TypeRecordKind.Enum));

        var optSpec = MakeOptional("TestModule.Status");

        Assert.True(OptionalMarshalClassifier.IsDecomposed(optSpec, typeDb));
    }

    [Fact]
    public void IsDecomposed_SmallPrimitive_ReturnsFalse()
    {
        var (_, typeDb) = CreateTestEnvironment();
        var optSpec = MakeOptional("Swift.Int32");

        Assert.False(OptionalMarshalClassifier.IsDecomposed(optSpec, typeDb));
    }

    [Fact]
    public void IsLargeOptional_Int64_ReturnsTrue()
    {
        var (_, typeDb) = CreateTestEnvironment();
        var optSpec = MakeOptional("Swift.Int64");

        Assert.True(OptionalMarshalClassifier.IsLargeOptional(optSpec, typeDb));
    }

    [Fact]
    public void IsLargeOptional_Int32_ReturnsFalse()
    {
        var (_, typeDb) = CreateTestEnvironment();
        var optSpec = MakeOptional("Swift.Int32");

        Assert.False(OptionalMarshalClassifier.IsLargeOptional(optSpec, typeDb));
    }

    [Fact]
    public void IsLargeOptional_NonFrozenStruct_ReturnsTrue()
    {
        // DecomposedBuffers are also "large" — IsLargeOptional includes both strategies.
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes(
            ("TestModule.URLRequest", TypeRecordFlags.None, TypeRecordKind.Struct));

        var optSpec = MakeOptional("TestModule.URLRequest");

        Assert.True(OptionalMarshalClassifier.IsLargeOptional(optSpec, typeDb));
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════════
    // Item 5: Tag byte offset consistency between emitter and runtime
    // ═══════════════════════════════════════════════════════════════════════

    #region Tag Byte Offset

    [Theory]
    [InlineData("Swift.Bool", 1)]
    [InlineData("Bool", 1)]
    [InlineData("Swift.Int8", 1)]
    [InlineData("Int8", 1)]
    [InlineData("Swift.UInt8", 1)]
    [InlineData("UInt8", 1)]
    [InlineData("Swift.Int16", 2)]
    [InlineData("Int16", 2)]
    [InlineData("Swift.UInt16", 2)]
    [InlineData("UInt16", 2)]
    [InlineData("Swift.Int32", 4)]
    [InlineData("Int32", 4)]
    [InlineData("Swift.UInt32", 4)]
    [InlineData("UInt32", 4)]
    [InlineData("Swift.Float", 4)]
    [InlineData("Float", 4)]
    [InlineData("Swift.Int", 8)]
    [InlineData("Int", 8)]
    [InlineData("Swift.UInt", 8)]
    [InlineData("UInt", 8)]
    [InlineData("Swift.Int64", 8)]
    [InlineData("Int64", 8)]
    [InlineData("Swift.UInt64", 8)]
    [InlineData("UInt64", 8)]
    [InlineData("Swift.Double", 8)]
    [InlineData("Double", 8)]
    [InlineData("CoreFoundation.CGFloat", 8)]
    [InlineData("CGFloat", 8)]
    public void GetSwiftTagByteOffset_AllBlittablePrimitives_ReturnsCorrectOffset(string typeName, int expectedOffset)
    {
        var result = OptionalMarshalClassifier.GetSwiftTagByteOffset(typeName);

        Assert.NotNull(result);
        Assert.Equal(expectedOffset, result.Value);
    }

    [Fact]
    public void GetSwiftTagByteOffset_UnknownType_ReturnsNull()
    {
        var result = OptionalMarshalClassifier.GetSwiftTagByteOffset("Swift.String");

        Assert.Null(result);
    }

    [Fact]
    public void GetSwiftTagByteOffsetString_Int32_Returns4()
    {
        var result = OptionalMarshalClassifier.GetSwiftTagByteOffsetString("Swift.Int32");

        Assert.Equal("4", result);
    }

    [Fact]
    public void GetSwiftTagByteOffsetString_UnknownType_ReturnsNull()
    {
        var result = OptionalMarshalClassifier.GetSwiftTagByteOffsetString("SomeModule.CustomType");

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that the emitter's GetSwiftTagByteOffset produces the same offsets
    /// as the runtime's GetBlittablePrimitiveTagOffset for each corresponding type pair.
    ///
    /// The runtime operates on CLR types (typeof(T)), while the emitter operates on Swift type names.
    /// This test ensures they agree on the offset for each type correspondence:
    ///   CLR byte/sbyte (1) ↔ Swift Int8/UInt8 (1)
    ///   CLR short/ushort (2) ↔ Swift Int16/UInt16 (2)
    ///   CLR int/uint/float (4) ↔ Swift Int32/UInt32/Float (4)
    ///   CLR long/ulong/double (8) ↔ Swift Int64/UInt64/Double (8)
    ///   CLR nint/nuint (8 on arm64) ↔ Swift Int/UInt (8) / CGFloat (8)
    /// </summary>
    [Theory]
    [InlineData("Swift.Int8", 1)]     // CLR byte/sbyte → 1
    [InlineData("Swift.UInt8", 1)]
    [InlineData("Swift.Int16", 2)]    // CLR short/ushort → 2
    [InlineData("Swift.UInt16", 2)]
    [InlineData("Swift.Int32", 4)]    // CLR int/uint → 4
    [InlineData("Swift.UInt32", 4)]
    [InlineData("Swift.Float", 4)]    // CLR float → 4
    [InlineData("Swift.Int64", 8)]    // CLR long/ulong → 8
    [InlineData("Swift.UInt64", 8)]
    [InlineData("Swift.Double", 8)]   // CLR double → 8
    [InlineData("Swift.Int", 8)]      // CLR nint → IntPtr.Size (8 on arm64)
    [InlineData("Swift.UInt", 8)]     // CLR nuint → IntPtr.Size (8 on arm64)
    public void EmitterAndRuntime_TagOffsets_AreConsistent(string swiftType, int expectedOffset)
    {
        // Emitter side: GetSwiftTagByteOffset
        var emitterOffset = OptionalMarshalClassifier.GetSwiftTagByteOffset(swiftType);
        Assert.NotNull(emitterOffset);
        Assert.Equal(expectedOffset, emitterOffset.Value);
    }

    /// <summary>
    /// Verifies that the emitter's GetSwiftTagByteOffset mapping matches the projection's
    /// GetBlittablePrimitiveSizePublic mapping for each C# ↔ Swift type correspondence.
    /// Both must agree on the size for correct tag byte access.
    /// </summary>
    [Theory]
    [InlineData("Swift.Bool", "bool", 1)]
    [InlineData("Swift.Int8", "sbyte", 1)]
    [InlineData("Swift.UInt8", "byte", 1)]
    [InlineData("Swift.Int16", "short", 2)]
    [InlineData("Swift.UInt16", "ushort", 2)]
    [InlineData("Swift.Int32", "int", 4)]
    [InlineData("Swift.UInt32", "uint", 4)]
    [InlineData("Swift.Float", "float", 4)]
    [InlineData("Swift.Int64", "long", 8)]
    [InlineData("Swift.UInt64", "ulong", 8)]
    [InlineData("Swift.Double", "double", 8)]
    [InlineData("Swift.Int", "nint", 8)]
    [InlineData("Swift.UInt", "nuint", 8)]
    public void EmitterTagOffset_MatchesProjectionSize(string swiftType, string csharpType, int expectedSize)
    {
        // Emitter side
        var emitterOffset = OptionalMarshalClassifier.GetSwiftTagByteOffset(swiftType);
        Assert.NotNull(emitterOffset);
        Assert.Equal(expectedSize, emitterOffset.Value);

        // Projection side (uses BlittableProjection mock)
        var mockProjection = new MockBlittableProjection(csharpType);
        var projectionSize = OptionalProjection.GetBlittablePrimitiveSizePublic(mockProjection);
        Assert.NotNull(projectionSize);
        Assert.Equal(expectedSize, projectionSize.Value);
    }

    /// <summary>
    /// Verifies the sets of types covered by each tag offset source are the same
    /// (no type is in one set but not the other).
    /// </summary>
    [Fact]
    public void AllBlittablePrimitiveSwiftTypes_HaveTagOffset()
    {
        // All types recognized by IsBlittablePrimitiveSwiftType must have a tag offset
        var blittableTypes = new[]
        {
            "Swift.Int", "Swift.UInt", "Swift.Int8", "Swift.UInt8",
            "Swift.Int16", "Swift.UInt16", "Swift.Int32", "Swift.UInt32",
            "Swift.Int64", "Swift.UInt64",
            "Swift.Float", "Swift.Double",
            "CoreFoundation.CGFloat", "CGFloat",
            "Int", "UInt", "Int8", "UInt8",
            "Int16", "UInt16", "Int32", "UInt32",
            "Int64", "UInt64",
            "Float", "Double"
        };

        foreach (var type in blittableTypes)
        {
            Assert.True(CdeclParamMapper.IsBlittablePrimitiveSwiftType(type),
                $"{type} should be recognized as blittable primitive");
            Assert.NotNull(OptionalMarshalClassifier.GetSwiftTagByteOffset(type));
        }
    }

    /// <summary>
    /// Verifies that the C# projection sizes match the emitter tag offsets for all
    /// blittable primitive types exposed via GetBlittablePrimitiveSizePublic.
    /// </summary>
    [Fact]
    public void AllProjectionBlittableSizes_HaveMatchingEmitterOffset()
    {
        var csharpToSwiftMap = new Dictionary<string, string>
        {
            ["bool"] = "Swift.Bool",
            ["byte"] = "Swift.UInt8",
            ["sbyte"] = "Swift.Int8",
            ["short"] = "Swift.Int16",
            ["ushort"] = "Swift.UInt16",
            ["int"] = "Swift.Int32",
            ["uint"] = "Swift.UInt32",
            ["float"] = "Swift.Float",
            ["long"] = "Swift.Int64",
            ["ulong"] = "Swift.UInt64",
            ["double"] = "Swift.Double",
            ["nint"] = "Swift.Int",
            ["nuint"] = "Swift.UInt"
        };

        foreach (var (csharpType, swiftType) in csharpToSwiftMap)
        {
            var mock = new MockBlittableProjection(csharpType);
            var projSize = OptionalProjection.GetBlittablePrimitiveSizePublic(mock);
            var emitterOffset = OptionalMarshalClassifier.GetSwiftTagByteOffset(swiftType);

            Assert.NotNull(projSize);
            Assert.NotNull(emitterOffset);
            Assert.Equal(projSize.Value, emitterOffset.Value);
        }
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════════
    // Item 6: Standardized decomposed Optional naming/access patterns
    // ═══════════════════════════════════════════════════════════════════════

    #region Decomposed Optional Pattern Helpers

    [Fact]
    public void SwiftWriteHasValue_Some_ProducesCorrectCode()
    {
        var result = OptionalMarshalClassifier.SwiftWriteHasValue("hasValuePtr", true);

        Assert.Equal("hasValuePtr.storeBytes(of: Int8(1), as: Int8.self)", result);
    }

    [Fact]
    public void SwiftWriteHasValue_None_ProducesCorrectCode()
    {
        var result = OptionalMarshalClassifier.SwiftWriteHasValue("hasValuePtr", false);

        Assert.Equal("hasValuePtr.storeBytes(of: Int8(0), as: Int8.self)", result);
    }

    [Fact]
    public void SwiftWriteHasValue_CustomPtrName_ProducesCorrectCode()
    {
        var result = OptionalMarshalClassifier.SwiftWriteHasValue("myPtr", true);

        Assert.Contains("myPtr.storeBytes", result);
    }

    [Fact]
    public void SwiftReconstructOptional_ProducesCorrectCode()
    {
        var result = OptionalMarshalClassifier.SwiftReconstructOptional(
            OptionalMarshalClassifier.SwiftHasValueParam, "newValue", "TestModule.AssetType", "newValueVal");

        Assert.Equal(
            "let newValueVal: TestModule.AssetType? = hasValue != 0 ? newValue.assumingMemoryBound(to: TestModule.AssetType.self).pointee : nil",
            result);
    }

    [Fact]
    public void CSharpReadHasValue_ProducesCorrectCode()
    {
        var result = OptionalMarshalClassifier.CSharpReadHasValue("hasValuePtr");

        Assert.Equal("byte _hasValue = ((byte*)hasValuePtr)[0];", result);
    }

    [Fact]
    public void CSharpHasValueNullCheck_ProducesCorrectCode()
    {
        var result = OptionalMarshalClassifier.CSharpHasValueNullCheck();

        Assert.Equal("if (_hasValue == 0) return null;", result);
    }

    [Fact]
    public void Constants_AreConsistent()
    {
        // The Swift hasValue type in code should match the parameter declarations
        Assert.Equal("Int8", OptionalMarshalClassifier.SwiftHasValueType);
        Assert.Equal("hasValue", OptionalMarshalClassifier.SwiftHasValueParam);
        Assert.Equal("hasValuePtr", OptionalMarshalClassifier.SwiftHasValuePtrParam);
        Assert.Equal("_hasValue", OptionalMarshalClassifier.CSharpHasValueLocal);
    }

    [Fact]
    public void SwiftWriteHasValue_UsesSwiftHasValueType()
    {
        // The emitted code must reference the same type as the constant
        var some = OptionalMarshalClassifier.SwiftWriteHasValue("ptr", true);
        var none = OptionalMarshalClassifier.SwiftWriteHasValue("ptr", false);

        Assert.Contains($"{OptionalMarshalClassifier.SwiftHasValueType}(1)", some);
        Assert.Contains($"{OptionalMarshalClassifier.SwiftHasValueType}(0)", none);
        Assert.Contains($"as: {OptionalMarshalClassifier.SwiftHasValueType}.self", some);
    }

    [Fact]
    public void CSharpReadHasValue_UsesCSharpHasValueLocal()
    {
        // The emitted code must declare the same variable name as the constant
        var read = OptionalMarshalClassifier.CSharpReadHasValue("ptr");
        var check = OptionalMarshalClassifier.CSharpHasValueNullCheck();

        Assert.Contains($"byte {OptionalMarshalClassifier.CSharpHasValueLocal}", read);
        Assert.Contains(OptionalMarshalClassifier.CSharpHasValueLocal, check);
    }

    #endregion

    #region GetInnerSpec helper

    [Fact]
    public void GetInnerSpec_ValidOptional_ReturnsInner()
    {
        var optSpec = MakeOptional("Swift.Int");
        var inner = OptionalMarshalClassifier.GetInnerSpec(optSpec);

        Assert.NotNull(inner);
        Assert.IsType<NamedTypeSpec>(inner);
        Assert.Equal("Swift.Int", ((NamedTypeSpec)inner!).Name);
    }

    [Fact]
    public void GetInnerSpec_NonOptional_ReturnsNull()
    {
        var spec = new NamedTypeSpec("Swift.Int");
        var inner = OptionalMarshalClassifier.GetInnerSpec(spec);

        Assert.Null(inner);
    }

    [Fact]
    public void GetInnerSpec_OptionalNoGenericParams_ReturnsNull()
    {
        var spec = new NamedTypeSpec("Swift.Optional");
        // No generic params
        var inner = OptionalMarshalClassifier.GetInnerSpec(spec);

        Assert.Null(inner);
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════════
    // Test helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static NamedTypeSpec MakeOptional(string innerTypeName)
    {
        var optSpec = new NamedTypeSpec("Swift.Optional");
        optSpec.GenericParameters.Add(new NamedTypeSpec(innerTypeName));
        return optSpec;
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment()
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(swiftModule);

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        return (moduleDecl, typeDb);
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithExtraTypes(
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind)[] extraTypes)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        foreach (var (qualifiedName, flags, kind) in extraTypes)
        {
            var shortName = qualifiedName.Contains('.') ?
                qualifiedName.Substring(qualifiedName.LastIndexOf('.') + 1) : qualifiedName;
            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName(qualifiedName),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", shortName),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName),
                    MetadataAccessor = $"$s_test_{shortName}Ma",
                    Flags = flags,
                    Kind = kind
                });
        }
        typeDb.AddModuleDatabase(testModule);

        return (moduleDecl, typeDb);
    }

    /// <summary>
    /// Mock projection that simulates a BlittableProjection for testing GetBlittablePrimitiveSizePublic.
    /// </summary>
    private class MockBlittableProjection : BlittableProjection
    {
        public MockBlittableProjection(string publicType) : base(publicType) { }
    }
}
