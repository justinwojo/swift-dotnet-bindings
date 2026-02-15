// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for tuple type detection and handling.
/// These tests focus on the TupleTypeSpec parsing and translation to C# types.
/// </summary>
public class TupleHandlerTests
{
    private readonly MockTypeDatabase _typeDatabase;
    private readonly TupleHandler _tupleHandler;

    public TupleHandlerTests()
    {
        _typeDatabase = new MockTypeDatabase();
        _tupleHandler = new TupleHandler(_typeDatabase);
    }

    #region IsTuple Detection Tests

    [Fact]
    public void IsTuple_WithNonEmptyTuple_ReturnsTrue()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        Assert.True(_tupleHandler.IsTuple(tuple));
    }

    [Fact]
    public void IsTuple_WithEmptyTuple_ReturnsFalse()
    {
        var tuple = TupleTypeSpec.Empty;

        Assert.False(_tupleHandler.IsTuple(tuple));
    }

    [Fact]
    public void IsTuple_WithNamedTypeSpec_ReturnsFalse()
    {
        var namedType = new NamedTypeSpec("Swift.Int");

        Assert.False(_tupleHandler.IsTuple(namedType));
    }

    [Fact]
    public void IsTuple_WithClosureTypeSpec_ReturnsFalse()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        Assert.False(_tupleHandler.IsTuple(closure));
    }

    [Fact]
    public void IsTuple_WithArgumentDecl_DetectsTuple()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var argument = CreateArgumentDecl("point", tuple);

        Assert.True(_tupleHandler.IsTuple(argument));
    }

    [Fact]
    public void IsTuple_WithArgumentDeclNonTuple_ReturnsFalse()
    {
        var namedType = new NamedTypeSpec("Swift.Int");
        var argument = CreateArgumentDecl("value", namedType);

        Assert.False(_tupleHandler.IsTuple(argument));
    }

    #endregion

    #region GetTupleTypeSpec Tests

    [Fact]
    public void GetTupleTypeSpec_WithTupleArgument_ReturnsTupleTypeSpec()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });
        var argument = CreateArgumentDecl("data", tuple);

        var result = _tupleHandler.GetTupleTypeSpec(argument);

        Assert.NotNull(result);
        Assert.Equal(2, result.Elements.Count);
    }

    [Fact]
    public void GetTupleTypeSpec_WithNonTupleArgument_ReturnsNull()
    {
        var namedType = new NamedTypeSpec("Swift.Int");
        var argument = CreateArgumentDecl("value", namedType);

        var result = _tupleHandler.GetTupleTypeSpec(argument);

        Assert.Null(result);
    }

    [Fact]
    public void GetTupleTypeSpec_WithEmptyTupleArgument_ReturnsNull()
    {
        var argument = CreateArgumentDecl("void", TupleTypeSpec.Empty);

        var result = _tupleHandler.GetTupleTypeSpec(argument);

        Assert.Null(result);
    }

    #endregion

    #region IsSupportedTuple Tests

    [Fact]
    public void IsSupportedTuple_WithFrozenPrimitives_ReturnsTrue()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.True(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_WithEmptyTuple_ReturnsFalse()
    {
        Assert.False(_tupleHandler.IsSupportedTuple(TupleTypeSpec.Empty));
    }

    [Fact]
    public void IsSupportedTuple_WithNestedTuple_ReturnsFalse()
    {
        var innerTuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });
        var outerTuple = new TupleTypeSpec(new List<TypeSpec>
        {
            innerTuple,
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.False(_tupleHandler.IsSupportedTuple(outerTuple));
    }

    [Fact]
    public void IsSupportedTuple_WithClosure_ReturnsFalse()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            closure
        });

        Assert.False(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_With8Elements_ReturnsFalse()
    {
        var elements = new List<TypeSpec>();
        for (int i = 0; i < 8; i++)
        {
            elements.Add(new NamedTypeSpec("Swift.Int"));
        }
        var tuple = new TupleTypeSpec(elements);

        Assert.False(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_With7Elements_ReturnsTrue()
    {
        var elements = new List<TypeSpec>();
        for (int i = 0; i < 7; i++)
        {
            elements.Add(new NamedTypeSpec("Swift.Int"));
        }
        var tuple = new TupleTypeSpec(elements);

        Assert.True(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_WithNonFrozenType_ReturnsFalse()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Test.NonFrozenType") // Not registered as frozen
        });

        Assert.False(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_WithGenericParameters_ReturnsFalse()
    {
        var genericType = new NamedTypeSpec("Swift.Array");
        genericType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            genericType
        });

        Assert.False(_tupleHandler.IsSupportedTuple(tuple));
    }

    #endregion

    #region GetCSharpTupleType Tests

    [Fact]
    public void GetCSharpTupleType_IntString_ReturnsCorrectType()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        var result = _tupleHandler.GetCSharpTupleType(tuple);

        Assert.Equal("(long, Swift.SwiftString)", result);
    }

    [Fact]
    public void GetCSharpTupleType_WithLabels_PreservesLabels()
    {
        var intType = new NamedTypeSpec("Swift.Int") { TypeLabel = "x" };
        var boolType = new NamedTypeSpec("Swift.Bool") { TypeLabel = "y" };
        var tuple = new TupleTypeSpec(new List<TypeSpec> { intType, boolType });

        var result = _tupleHandler.GetCSharpTupleType(tuple);

        Assert.Equal("(long x, bool y)", result);
    }

    [Fact]
    public void GetCSharpTupleType_SingleElement_ReturnsCorrectType()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Double")
        });

        var result = _tupleHandler.GetCSharpTupleType(tuple);

        Assert.Equal("(double)", result);
    }

    [Fact]
    public void GetCSharpTupleType_ThreeElements_ReturnsCorrectType()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Double")
        });

        var result = _tupleHandler.GetCSharpTupleType(tuple);

        Assert.Equal("(long, bool, double)", result);
    }

    #endregion

    #region GetPInvokeTupleType Tests

    [Fact]
    public void GetPInvokeTupleType_IntString_ReturnsValueTuple()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<long, Swift.SwiftString>", result);
    }

    [Fact]
    public void GetPInvokeTupleType_ThreeElements_ReturnsValueTuple()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Double")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<long, bool, double>", result);
    }

    [Fact]
    public void GetPInvokeTupleType_BoundGenericElement_UsesIntPtrNotVoidStar()
    {
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            arrayType,
            new NamedTypeSpec("Swift.Bool")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, bool>", result);
        Assert.DoesNotContain("void*", result);
    }

    [Fact]
    public void GetPInvokeTupleType_OptionalNonObjCElement_UsesIntPtr()
    {
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            optionalInt,
            new NamedTypeSpec("Swift.Bool")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, bool>", result);
        Assert.DoesNotContain("void*", result);
    }

    [Fact]
    public void GetPInvokeTupleType_MultipleBoundGenerics_UsesIntPtrForAll()
    {
        var arrayType1 = new NamedTypeSpec("Swift.Array");
        arrayType1.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var arrayType2 = new NamedTypeSpec("Swift.Array");
        arrayType2.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            arrayType1,
            arrayType2
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, IntPtr>", result);
    }

    [Fact]
    public void GetPInvokeTupleType_NonFrozenStructElement_ReturnsIntPtr()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Nuke.ImageResponse"),
            new NamedTypeSpec("Swift.Int")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, long>", result);
    }

    [Fact]
    public void GetPInvokeTupleType_ClassElement_ReturnsIntPtr()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Nuke.ImageTask"),
            new NamedTypeSpec("Swift.Bool")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, bool>", result);
    }

    [Fact]
    public void GetPInvokeTupleType_AnyTypeElement_ReturnsIntPtr()
    {
        // An unknown type that resolves to AnyType should use IntPtr fallback
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("SomeModule.UnknownType"),
            new NamedTypeSpec("Swift.Int")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, long>", result);
    }

    [Fact]
    public void HasClosureUnsafeTupleElements_WithOptionalNonFrozen_ReturnsTrue()
    {
        // Optional<NonFrozenStruct> → P/Invoke IntPtr vs C# SwiftOptional<T> → mismatch
        var optionalResponse = new NamedTypeSpec("Swift.Optional");
        optionalResponse.GenericParameters.Add(new NamedTypeSpec("Nuke.ImageResponse"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            optionalResponse,
            new NamedTypeSpec("Swift.Int")
        });

        Assert.True(_tupleHandler.HasClosureUnsafeTupleElements(tuple));
    }

    [Fact]
    public void HasClosureUnsafeTupleElements_WithPointerType_ReturnsFalse()
    {
        // UnsafeMutablePointer<T> → IntPtr in BOTH contexts → no mismatch
        var pointerType = new NamedTypeSpec("Swift.UnsafeMutablePointer");
        pointerType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            pointerType,
            new NamedTypeSpec("Swift.Int")
        });

        Assert.False(_tupleHandler.HasClosureUnsafeTupleElements(tuple));
    }

    [Fact]
    public void HasClosureUnsafeTupleElements_WithOnlyPrimitives_ReturnsFalse()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });

        Assert.False(_tupleHandler.HasClosureUnsafeTupleElements(tuple));
    }

    #endregion

    #region TupleTypeSpec Kind Tests

    [Fact]
    public void TupleTypeSpec_HasCorrectKind()
    {
        var tuple = new TupleTypeSpec();

        Assert.Equal(TypeSpecKind.Tuple, tuple.Kind);
    }

    #endregion

    #region Helper Methods

    private static ArgumentDecl CreateArgumentDecl(string name, TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion

    #region Mock Type Database

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabase()
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Bool"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Double"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.String"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Optional"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                },
                // Non-frozen struct (ClassWithOpaquePayload)
                ["Nuke.ImageResponse"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Nuke", "ImageResponse"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Nuke.ImageResponse"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.None, // NOT frozen
                    Kind = TypeRecordKind.Struct
                },
                // Swift class
                ["Nuke.ImageTask"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Nuke", "ImageTask"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Nuke.ImageTask"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                },
                // Pointer type — must return the exact TypeDatabaseExtensions.IntPtrType instance
                // so TranslateBoundGenericToCSharp recognizes it as a pointer (reference equality check)
                ["Swift.UnsafeMutablePointer"] = TypeDatabaseExtensions.IntPtrType
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
