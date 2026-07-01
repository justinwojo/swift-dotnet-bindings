// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the decomposed indirect-result sub-methods in MarshallingHelpers:
/// IsCdeclNonSetterWrapper, IsCdeclIndirectResultRequired,
/// IsConstructorIndirectResultRequired, and IsTypeInherentlyIndirect.
/// </summary>
public class IndirectResultDecompositionTests
{
    #region IsCdeclNonSetterWrapper Tests

    [Fact]
    public void IsCdeclNonSetterWrapper_CdeclProperty_ReturnsTrue()
    {
        // UsesCdeclPropertyWrapper=true, method name doesn't end with "_Set"
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"));
        env.MethodDecl.UsesCdeclPropertyWrapper = true;
        env.MethodDecl.Name = "myProperty_Get";
        Assert.True(MarshallingHelpers.IsCdeclNonSetterWrapper(env));
    }

    [Fact]
    public void IsCdeclNonSetterWrapper_CdeclMethod_ReturnsTrue()
    {
        // UsesCdeclMethodWrapper=true
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"));
        env.MethodDecl.UsesCdeclMethodWrapper = true;
        Assert.True(MarshallingHelpers.IsCdeclNonSetterWrapper(env));
    }

    [Fact]
    public void IsCdeclNonSetterWrapper_Setter_ReturnsFalse()
    {
        // UsesCdeclPropertyWrapper=true, but method name ends with "_Set" (setter)
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"));
        env.MethodDecl.UsesCdeclPropertyWrapper = true;
        env.MethodDecl.Name = "myProperty_Set";
        Assert.False(MarshallingHelpers.IsCdeclNonSetterWrapper(env));
    }

    [Fact]
    public void IsCdeclNonSetterWrapper_NoCdecl_ReturnsFalse()
    {
        // Neither UsesCdeclPropertyWrapper nor UsesCdeclMethodWrapper is true
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"));
        Assert.False(env.MethodDecl.UsesCdeclPropertyWrapper);
        Assert.False(env.MethodDecl.UsesCdeclMethodWrapper);
        Assert.False(MarshallingHelpers.IsCdeclNonSetterWrapper(env));
    }

    #endregion

    #region IsCdeclIndirectResultRequired Tests

    [Fact]
    public void IsCdeclIndirectResult_VoidReturn_ReturnsFalse()
    {
        // CSSignature first element has IsEmptyTuple (void return)
        var env = CreateMethodEnv(returnType: TupleTypeSpec.Empty);
        var result = MarshallingHelpers.IsCdeclIndirectResultRequired(env);
        Assert.NotNull(result);
        Assert.False(result!.Value);
    }

    [Fact]
    public void IsCdeclIndirectResult_StringReturn_ReturnsTrue()
    {
        // CSSignature first element has SwiftTypeSpec = NamedTypeSpec("Swift.String")
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.String"));
        var result = MarshallingHelpers.IsCdeclIndirectResultRequired(env);
        Assert.NotNull(result);
        Assert.True(result!.Value);
    }

    [Fact]
    public void IsCdeclIndirectResult_ClosureReturn_ReturnsTrue()
    {
        // First element has ClosureTypeSpec
        var closureReturn = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var env = CreateMethodEnv(returnType: closureReturn);
        var result = MarshallingHelpers.IsCdeclIndirectResultRequired(env);
        Assert.NotNull(result);
        Assert.True(result!.Value);
    }

    [Fact]
    public void IsCdeclIndirectResult_DynamicSelf_ReturnsFalse()
    {
        // First element has IsDynamicSelf=true (NamedTypeSpec("Self"))
        var selfReturn = new NamedTypeSpec("Self");
        Assert.True(selfReturn.IsDynamicSelf); // sanity check
        var env = CreateMethodEnv(returnType: selfReturn);
        var result = MarshallingHelpers.IsCdeclIndirectResultRequired(env);
        Assert.NotNull(result);
        Assert.False(result!.Value);
    }

    [Fact]
    public void IsCdeclIndirectResult_PrimitiveInt_ReturnsNull()
    {
        // NamedTypeSpec("Swift.Int"), no special case -- returns null (fall through)
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"));
        var result = MarshallingHelpers.IsCdeclIndirectResultRequired(env);
        Assert.Null(result);
    }

    [Fact]
    public void IsCdeclIndirectResult_BoundGenericArray_ReturnsTrue()
    {
        // Bound generic collection returns (Swift.Array<T>) must return true.
        // @_cdecl can't return generics directly — Swift wrapper writes to resultPtr
        // via initializeMemory(as:).
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var env = CreateMethodEnv(returnType: arrayType);
        env.MethodDecl.UsesCdeclMethodWrapper = true;
        var result = MarshallingHelpers.IsCdeclIndirectResultRequired(env);
        Assert.NotNull(result);
        Assert.True(result!.Value);
    }

    [Fact]
    public void IsCdeclIndirectResult_BoundGenericDictionary_ReturnsTrue()
    {
        // Swift.Dictionary<K,V> is also a collection type needing indirect result.
        var dictType = new NamedTypeSpec("Swift.Dictionary");
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var env = CreateMethodEnv(returnType: dictType);
        env.MethodDecl.UsesCdeclMethodWrapper = true;
        var result = MarshallingHelpers.IsCdeclIndirectResultRequired(env);
        Assert.NotNull(result);
        Assert.True(result!.Value);
    }

    [Fact]
    public void IsCdeclIndirectResult_BoundGenericSet_ReturnsTrue()
    {
        // Swift.Set<T> is also a collection type needing indirect result.
        var setType = new NamedTypeSpec("Swift.Set");
        setType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var env = CreateMethodEnv(returnType: setType);
        env.MethodDecl.UsesCdeclMethodWrapper = true;
        var result = MarshallingHelpers.IsCdeclIndirectResultRequired(env);
        Assert.NotNull(result);
        Assert.True(result!.Value);
    }

    #endregion

    #region IsConstructorIndirectResultRequired Tests

    [Fact]
    public void IsConstructorIndirectResult_FailableFrozenStructInit_ReturnsTrue()
    {
        // A failable VALUE-type (struct) init returns Optional<value>, which is address-only — the
        // tag + payload live in a result buffer — so it genuinely needs an indirect result. This is
        // NOT a universal "failable => indirect" rule: a failable CLASS init returns Optional<Self>
        // as a single nullable pointer in-register, so it does NOT need an indirect result when
        // wrapped (see IsConstructorIndirectResult_FailableClassWithCdeclWrapper_ReturnsFalse).
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("Swift.Int"),
            isConstructor: true,
            isFailable: true,
            parentDecl: CreateFrozenStructParent());
        var result = MarshallingHelpers.IsConstructorIndirectResultRequired(env);
        Assert.NotNull(result);
        Assert.True(result!.Value);
    }

    [Fact]
    public void IsConstructorIndirectResult_FailableClassWithCdeclWrapper_ReturnsFalse()
    {
        // A failable CLASS init routed through a @_cdecl wrapper returns the nullable retained class
        // pointer DIRECTLY (UnsafeMutableRawPointer?, nil == failure) — exactly like a non-failable
        // class init — so it must NOT request an indirect result. Adding a leading resultPtr would
        // shift every real argument one slot to the right and corrupt the call.
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("TestModule.MyClass"),
            isConstructor: true,
            isFailable: true,
            parentDecl: CreateClassParent());
        env.MethodDecl.UsesCdeclConstructorWrapper = true;
        var result = MarshallingHelpers.IsConstructorIndirectResultRequired(env);
        Assert.NotNull(result);
        Assert.False(result!.Value);
    }

    [Fact]
    public void IsConstructorIndirectResult_NonFrozenStruct_ReturnsTrue()
    {
        // IsConstructor=true, parent is StructDecl with IsFrozen=false
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("Swift.Int"),
            isConstructor: true,
            parentDecl: CreateNonFrozenStructParent());
        var result = MarshallingHelpers.IsConstructorIndirectResultRequired(env);
        Assert.NotNull(result);
        Assert.True(result!.Value);
    }

    [Fact]
    public void IsConstructorIndirectResult_ClassConstructor_ReturnsNull()
    {
        // IsConstructor=true, parent is ClassDecl -- returns null (fall through)
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("TestModule.MyClass"),
            isConstructor: true,
            parentDecl: CreateClassParent());
        var result = MarshallingHelpers.IsConstructorIndirectResultRequired(env);
        Assert.Null(result);
    }

    [Fact]
    public void IsConstructorIndirectResult_NotConstructor_ReturnsNull()
    {
        // IsConstructor=false -- returns null immediately
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("Swift.Int"),
            isConstructor: false);
        var result = MarshallingHelpers.IsConstructorIndirectResultRequired(env);
        Assert.Null(result);
    }

    #endregion

    #region Helper Methods

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
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = "$sMa"
        };
    }

    private static ClassDecl CreateClassParent()
    {
        var moduleDecl = CreateModuleDecl();
        return new ClassDecl
        {
            Name = "TestClass",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestClass"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodEnvironment CreateMethodEnv(
        TypeSpec returnType,
        bool isConstructor = false,
        bool isFailable = false,
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
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
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
                    CSharpTypeName = CSharpTypeName.NIntType,
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
                ["TestModule.MyClass"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
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
