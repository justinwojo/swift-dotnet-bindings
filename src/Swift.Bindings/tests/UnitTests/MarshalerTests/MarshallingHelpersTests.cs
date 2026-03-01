// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MarshallingHelpers utility methods.
/// </summary>
public class MarshallingHelpersTests
{
    #region MethodIsSetter Tests

    [Fact]
    public void MethodIsSetter_ReturnsTrueForSetterMethod()
    {
        var method = CreateMethodDecl("myProperty_Set");
        Assert.True(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsTrueForSetterWithUnderscoreInName()
    {
        var method = CreateMethodDecl("my_Property_Set");
        Assert.True(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForGetterMethod()
    {
        var method = CreateMethodDecl("myProperty_Get");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForRegularMethod()
    {
        var method = CreateMethodDecl("doSomething");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForMethodEndingInSet()
    {
        // "Set" without underscore is not a setter
        var method = CreateMethodDecl("resetSet");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForMethodContainingSetInMiddle()
    {
        var method = CreateMethodDecl("set_something");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_IsCaseSensitive()
    {
        // "_set" (lowercase) should not match
        var method = CreateMethodDecl("myProperty_set");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    #endregion

    #region IsObjCBridged Tests

    [Fact]
    public void IsObjCBridged_ReturnsTrueWhenFlagIsSet()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.ObjCBridged);
        Assert.True(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsFalseWhenFlagIsNotSet()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.None);
        Assert.False(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsTrueWhenObjCBridgedCombinedWithOtherFlags()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement);
        Assert.True(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsFalseForFrozenType()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.Frozen);
        Assert.False(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsFalseForRequiresMemoryManagement()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.RequiresMemoryManagement);
        Assert.False(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    #endregion

    #region MethodRequiresIndirectResult Tests

    [Fact]
    public void MethodRequiresIndirectResult_AsyncMethod_ReturnsFalse()
    {
        // Async methods never need indirect result
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"), isAsync: true);
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_FailableConstructor_ReturnsTrue()
    {
        // Failable constructors (init?) always need indirect result for Optional<Self> checking
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("Swift.Int"),
            isConstructor: true,
            isFailable: true,
            parentDecl: CreateFrozenStructParent());
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_NonFrozenConstructor_ReturnsTrue()
    {
        // Non-frozen struct constructors need indirect result
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("Swift.Int"),
            isConstructor: true,
            parentDecl: CreateNonFrozenStructParent());
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_FrozenStructConstructor_ReturnsFalse()
    {
        // Frozen struct constructors return in-register
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("Swift.Int"),
            isConstructor: true,
            parentDecl: CreateFrozenStructParent());
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_ClosureReturn_ReturnsFalse()
    {
        // Closure return types are passed as function pointers, not indirectly
        var closureReturn = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var env = CreateMethodEnv(returnType: closureReturn);
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_ExistentialReturn_ReturnsFalse()
    {
        // Existential types (any Protocol) are passed via existential containers (IntPtr)
        var existentialReturn = new ProtocolListTypeSpec();
        var env = CreateMethodEnv(returnType: existentialReturn);
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_NonGenericTupleReturn_ReturnsFalse()
    {
        // Non-generic tuples are handled by TupleHandler, not via indirect result
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var env = CreateMethodEnv(returnType: tupleReturn);
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_TupleWithGenericElements_ReturnsTrue()
    {
        // Tuples with generic type parameter elements require indirect result
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("τ_0_0"),
            new NamedTypeSpec("Swift.Int")
        });
        var env = CreateMethodEnv(returnType: tupleReturn);
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_BoundGenericWithMarshalling_ReturnsFalse()
    {
        // Bound generics that require marshalling (SwiftArray, SwiftOptional) return IntPtr directly
        var arrayReturn = new NamedTypeSpec("Swift.Array");
        arrayReturn.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var env = CreateMethodEnv(returnType: arrayReturn);
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_GenericReturn_ReturnsTrue()
    {
        // Generic return types need indirect result because sizes are unknown
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"), isGenericReturn: true);
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_ClassReturn_ReturnsFalse()
    {
        // Swift classes return pointers directly in registers
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("TestModule.MyClass"));
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_FrozenStructReturn_ReturnsFalse()
    {
        // Frozen structs fit in registers
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"));
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_NonFrozenStructReturn_ReturnsTrue()
    {
        // Non-frozen types need indirect result
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("TestModule.NonFrozen"));
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_DynamicSelfReturn_ReturnsTrue()
    {
        // A1 DynamicSelf hardening: "Self" return type always requires indirect result.
        // The explicit IsDynamicSelf guard fires early (before GetTypeRecordOrThrow).
        // Companion test TryGetAnyTypeFallbackInfo_DynamicSelf_IsNotFallback verifies
        // DynamicSelf is NOT classified as an existential — that test would fail if
        // the explicit guard were removed.
        var selfReturn = new NamedTypeSpec("Self");
        Assert.True(selfReturn.IsDynamicSelf);
        var env = CreateMethodEnv(returnType: selfReturn);
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    #endregion

    #region Helper Methods

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
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
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
            Visibility = Visibility.Private
        };
    }

    private static TypeRecord CreateTypeRecord(TypeRecordFlags flags)
    {
        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "TestType"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.TestType"),
            MetadataAccessor = "testAccessor",
            Flags = flags,
            Kind = TypeRecordKind.Class
        };
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
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
    }

    private static StructDecl CreateFrozenStructParent()
    {
        var moduleDecl = CreateModuleDecl();
        return new StructDecl
        {
            Name = "TestStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestStruct"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
    }

    private static StructDecl CreateNonFrozenStructParent()
    {
        var moduleDecl = CreateModuleDecl();
        return new StructDecl
        {
            Name = "NonFrozenStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.NonFrozenStruct"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = CreateModuleDecl(),
            ModuleDecl = CreateModuleDecl(),
            IsFrozen = false,
            MetadataAccessor = "$sMa"
        };
    }

    private static MethodEnvironment CreateMethodEnv(
        TypeSpec returnType,
        bool isAsync = false,
        bool isConstructor = false,
        bool isFailable = false,
        bool isGenericReturn = false,
        BaseDecl? parentDecl = null)
    {
        var moduleDecl = CreateModuleDecl();
        parentDecl ??= moduleDecl;

        var method = new MethodDecl
        {
            Name = isConstructor ? "init" : "testMethod",
            MangledName = "$sTest",
            MethodType = MethodType.Instance,
            IsConstructor = isConstructor,
            IsFailable = isFailable,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnType,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = isGenericReturn,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };

        return new MethodEnvironment(method, new MockTypeDatabase());
    }

    #endregion

    #region MockTypeDatabase

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string? AsyncLibraryName => null;

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
                ["Swift.String"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Array"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Optional"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["TestModule.MyClass"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyClass"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                },
                ["TestModule.NonFrozen"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "NonFrozen"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.NonFrozen"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
