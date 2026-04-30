// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MemberEmissionValidator — CanEmitSubscript, Codable pruning,
/// HasUnsupportedPropertyType, non-simple enum detection.
/// </summary>
public class MemberEmissionValidatorTests
{
    #region CanEmitSubscript Tests

    [Fact]
    public void CanEmitSubscript_AlwaysReturnsUnsupportedType()
    {
        // Subscripts on concrete types are not yet supported — always returns SkipReason
        var typeDatabase = CreateTypeDatabase();
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$sTest_subscript",
            IsStatic = false,
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                CreateArgument("index", new NamedTypeSpec("Swift.Int"))
            },
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var result = MemberEmissionValidator.CanEmitSubscript(subscript, typeDatabase, out var skipDetails, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedType, result);
        Assert.Contains("not yet supported", skipDetails);
    }

    [Fact]
    public void CanEmitSubscript_WithAnyTypeIndex_StillReturnsUnsupportedType()
    {
        // Even with AnyType index, the early return catches it first
        var typeDatabase = CreateTypeDatabase();
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$sTest_subscript2",
            IsStatic = false,
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                CreateArgument("key", new NamedTypeSpec("UnknownModule.Foo"))
            },
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var result = MemberEmissionValidator.CanEmitSubscript(subscript, typeDatabase, out _, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedType, result);
    }

    #endregion

    #region Codable Pruning Tests (via ShouldSkipMethodEmission)

    [Fact]
    public void ShouldSkipMethodEmission_CodableEncodeMember_ReturnsSynthesizedCodable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "encode",
            MangledName = "$s10TestModule6encodeyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("to", new NamedTypeSpec("Swift.Encoder"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.Equal(SkipReason.SynthesizedCodable, result);
        Assert.Contains("Codable", skipDetails);
    }

    [Fact]
    public void ShouldSkipMethodEmission_CodableInitFromDecoder_ReturnsSynthesizedCodable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4inityyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.MyType")),
                CreateArgument("from", new NamedTypeSpec("Swift.Decoder"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.Equal(SkipReason.SynthesizedCodable, result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_NormalMethod_ReturnsNull()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule7processyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("value", new NamedTypeSpec("Swift.Int"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_InOutUnsafeMutableRawBufferParam_ReturnsSwiftBind104()
    {
        // C# emits raw-buffer parameters as a split (IntPtr, nint) pair, but the Swift
        // wrapper for an inout raw buffer would receive a single UnsafeMutableRawPointer
        // and reconstruct it as a buffer-pointer struct via assumingMemoryBound.pointee.
        // The two halves disagree on shape, so an inout raw-buffer param is an ABI mismatch
        // and a memory-corruption hazard. Fail closed with SWIFTBIND104.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "mutateBuffer",
            MangledName = "$s10TestModule12mutateBufferyySwzF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                new ArgumentDecl
                {
                    Name = "buf",
                    PrivateName = "buf",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.UnsafeMutableRawBufferPointer"),
                    IsInOut = true,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.Equal(SkipReason.UnsupportedSignature, result);
        Assert.Contains("SWIFTBIND104", skipDetails);
        Assert.Contains("inout", skipDetails);
        Assert.Contains("UnsafeMutableRawBufferPointer", skipDetails);
    }

    [Fact]
    public void ShouldSkipMethodEmission_InOutUnsafeRawBufferParam_ReturnsSwiftBind104()
    {
        // Read-only raw buffer in inout position is rejected for the same ABI-shape reason.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "swapBuffer",
            MangledName = "$s10TestModule10swapBufferyySWzF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                new ArgumentDecl
                {
                    Name = "buf",
                    PrivateName = "buf",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.UnsafeRawBufferPointer"),
                    IsInOut = true,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.Equal(SkipReason.UnsupportedSignature, result);
        Assert.Contains("SWIFTBIND104", skipDetails);
        Assert.Contains("inout", skipDetails);
        Assert.Contains("UnsafeRawBufferPointer", skipDetails);
    }

    [Fact]
    public void ShouldSkipMethodEmission_ByValueUnsafeMutableRawBufferParam_ReturnsNull()
    {
        // Sanity check: a synchronous, non-inout raw-buffer parameter is supported (v1)
        // and must NOT be rejected by the SWIFTBIND104 gate.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "consumeBuffer",
            MangledName = "$s10TestModule13consumeBufferyySwF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("buf", new NamedTypeSpec("Swift.UnsafeMutableRawBufferPointer"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_SwiftUIParam_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "render",
            MangledName = "$s10TestModule6renderyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("view", new NamedTypeSpec("SwiftUI.View"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.Equal(SkipReason.SwiftUIConstraint, result);
        Assert.Contains("SwiftUI", skipDetails);
    }

    #endregion

    #region HasUnsupportedPropertyType Tests

    [Fact]
    public void HasUnsupportedPropertyType_NormalProperty_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var result = MemberEmissionValidator.HasUnsupportedPropertyType(property, typeDatabase);

        Assert.False(result);
    }

    [Fact]
    public void HasUnsupportedPropertyType_SwiftUIType_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var property = new PropertyDecl
        {
            Name = "body",
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.View"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var result = MemberEmissionValidator.HasUnsupportedPropertyType(property, typeDatabase);

        Assert.True(result);
    }

    [Fact]
    public void HasUnsupportedPropertyType_UnresolvableType_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var property = new PropertyDecl
        {
            Name = "data",
            SwiftTypeSpec = new NamedTypeSpec("UnknownModule.SomeType"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var result = MemberEmissionValidator.HasUnsupportedPropertyType(property, typeDatabase);

        Assert.True(result);
    }

    #endregion

    #region Constructor Unsupported Module Gate (Issue 5)

    [Fact]
    public void ShouldSkipMethodEmission_Constructor_WithSwiftUIParam_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4inityyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.MyType")),
                CreateArgument("view", new NamedTypeSpec("SwiftUI.Color"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.Equal(SkipReason.SwiftUIConstraint, result);
        Assert.Contains("SwiftUI", skipDetails);
    }

    [Fact]
    public void CanEmitMethod_Constructor_WithSwiftUIParam_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4inityyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.MyType")),
                CreateArgument("view", new NamedTypeSpec("SwiftUI.Color"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var skipDetails, out _);

        Assert.Equal(SkipReason.SwiftUIConstraint, result);
        Assert.Contains("SwiftUI", skipDetails);
    }

    [Fact]
    public void ShouldSkipMethodEmission_Constructor_WithNormalParam_ReturnsNull()
    {
        // Constructors with normal params should still be allowed through
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4inityyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.MyType")),
                CreateArgument("value", new NamedTypeSpec("Swift.Int"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    #endregion

    #region CanEmitProperty Tests

    [Fact]
    public void CanEmitProperty_InternalProperty_ReturnsModuleInternal()
    {
        // S4: Internal properties (not public) should be suppressed from bindings.
        // Without this gate, @_cdecl wrappers reference internal members, causing
        // "'member' is inaccessible due to 'internal' protection level" errors.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var property = new PropertyDecl
        {
            Name = "internalConfig",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = true,
            IsModuleInternal = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out var skipDetails, out _);

        Assert.Equal(SkipReason.ModuleInternal, result);
        Assert.Contains("Internal", skipDetails!);
    }

    [Fact]
    public void CanEmitProperty_PublicProperty_NotGated()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = true,
            IsModuleInternal = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out _, out _);

        // Not gated by internal check (may still be null or gated by other checks)
        Assert.NotEqual(SkipReason.ModuleInternal, result ?? SkipReason.UnsupportedType);
    }

    [Fact]
    public void CanEmitProperty_ConstrainedExtensionConflict_AllSpecializationsSkipped()
    {
        // Bug #2 regression: multiple `extension Wrapper where T == Concrete` blocks each
        // declare a property with the same Swift name. Each ABI Var node carries its own
        // specialization-specific accessor symbol. C# generics have only one specialization,
        // so emitting any of them silently dispatches the wrong symbol for the other closed
        // generic instantiations. The validator must skip ALL conflicting copies.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var parent = BuildGenericConflictParent(
            "VerificationResult", moduleDecl, "jwsRepresentation", specializationCount: 3);

        foreach (var prop in parent.Properties)
        {
            var skipReason = MemberEmissionValidator.CanEmitProperty(
                prop, typeDatabase, out var skipDetails, out _);

            Assert.Equal(SkipReason.UnsupportedType, skipReason);
            Assert.Contains("constrained-extension", skipDetails!);
            Assert.Contains("jwsRepresentation", skipDetails!);
        }
    }

    [Fact]
    public void CanEmitProperty_ConstrainedExtensionConflict_PropertyOrderingDoesNotMatter()
    {
        // P2 regression: previous fix used "first wins" dedup at parser time. If the first
        // copy was later filtered (unsupported type, internal visibility, etc.), the only
        // viable specialization was already removed. With "skip all" semantics, ordering
        // doesn't matter — every copy hits the gate independently.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var parent = BuildGenericConflictParent(
            "Wrapper", moduleDecl, "data", specializationCount: 2);

        var first = parent.Properties[0];
        var second = parent.Properties[1];

        // Verify both copies hit the gate, regardless of evaluation order. There is no
        // "winner" — both are dropped, so a downstream pipeline change that filters one
        // copy first cannot leak the other.
        var firstReason = MemberEmissionValidator.CanEmitProperty(first, typeDatabase, out _, out _);
        var secondReason = MemberEmissionValidator.CanEmitProperty(second, typeDatabase, out _, out _);

        Assert.Equal(SkipReason.UnsupportedType, firstReason);
        Assert.Equal(SkipReason.UnsupportedType, secondReason);
    }

    [Fact]
    public void CanEmitProperty_GenericParentSinglePropertyName_NotBlockedByConflictGate()
    {
        // Single-occurrence properties on a generic type pass the constrained-extension gate.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var parent = BuildGenericConflictParent(
            "Wrapper", moduleDecl, "uniqueProp", specializationCount: 1);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            parent.Properties[0], typeDatabase, out var skipDetails, out _);

        // The conflict-specific message must NOT be present (other gates may still apply).
        if (skipDetails != null)
            Assert.DoesNotContain("constrained-extension", skipDetails);
    }

    [Fact]
    public void CanEmitProperty_NonGenericParent_NoConflictGate()
    {
        // Constrained extensions can only exist on generic parents — non-generic parents
        // must not be silently absorbed by this gate even if (hypothetically) they had
        // duplicate PropertyDecls.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var parent = BuildBareStruct("Plain", moduleDecl, isGeneric: false);
        var prop = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl
        };
        parent.Properties.Add(prop);

        var skipReason = MemberEmissionValidator.CanEmitProperty(prop, typeDatabase, out var skipDetails, out _);

        if (skipDetails != null)
            Assert.DoesNotContain("constrained-extension", skipDetails);
    }

    [Fact]
    public void CanEmitProperty_StaticAndInstanceWithSameName_NoConflict()
    {
        // Instance and static properties with the same Swift name project to distinct C#
        // members and must NOT collide on the constrained-extension gate.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var parent = BuildBareStruct("Holder", moduleDecl, isGeneric: true);
        var instanceProp = new PropertyDecl
        {
            Name = "value",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl
        };
        var staticProp = new PropertyDecl
        {
            Name = "value",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = true,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl
        };
        parent.Properties.Add(instanceProp);
        parent.Properties.Add(staticProp);

        var instanceReason = MemberEmissionValidator.CanEmitProperty(instanceProp, typeDatabase, out var instDetails, out _);
        var staticReason = MemberEmissionValidator.CanEmitProperty(staticProp, typeDatabase, out var staticDetails, out _);

        if (instDetails != null)
            Assert.DoesNotContain("constrained-extension", instDetails);
        if (staticDetails != null)
            Assert.DoesNotContain("constrained-extension", staticDetails);
    }

    private static StructDecl BuildBareStruct(string typeName, ModuleDecl moduleDecl, bool isGeneric)
    {
        var decl = new StructDecl
        {
            Name = typeName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            MangledName = $"$sTestModule{typeName.Length}{typeName}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = string.Empty
        };
        if (isGeneric)
        {
            decl.GenericParameters.Add(new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>()));
        }
        return decl;
    }

    private static StructDecl BuildGenericConflictParent(
        string typeName,
        ModuleDecl moduleDecl,
        string propertyName,
        int specializationCount)
    {
        var parent = BuildBareStruct(typeName, moduleDecl, isGeneric: true);
        for (int i = 0; i < specializationCount; i++)
        {
            parent.Properties.Add(new PropertyDecl
            {
                Name = propertyName,
                SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                IsStatic = false,
                HasStorage = false,
                Accessors = new List<AccessorDecl>(),
                ParentDecl = parent,
                ModuleDecl = moduleDecl
            });
        }
        return parent;
    }

    #endregion

    #region Helper Methods

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
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
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion

    #region NetStaticClassType Tests

    [Fact]
    public void ReferencesUnsupportedModule_UITextContentType_ReturnsTrue()
    {
        // UITextContentType is a static class in .NET iOS — can't be used as a variable or type arg
        var typeSpec = new NamedTypeSpec("UIKit.UITextContentType");
        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(typeSpec));
    }

    [Fact]
    public void ReferencesUnsupportedModule_OptionalUITextContentType_ReturnsTrue()
    {
        // SwiftOptional<UITextContentType> should also be caught (generic parameter recursion)
        var inner = new NamedTypeSpec("UIKit.UITextContentType");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(optional));
    }

    [Fact]
    public void ReferencesUnsupportedModule_NormalUIKitType_ReturnsFalse()
    {
        // Normal UIKit types (UIView etc.) are NOT static classes
        var typeSpec = new NamedTypeSpec("UIKit.UIView");
        Assert.False(MemberEmissionValidator.ReferencesUnsupportedModule(typeSpec));
    }

    [Fact]
    public void IsNetStaticClassType_UITextContentType_ReturnsTrue()
    {
        Assert.True(MemberEmissionValidator.IsNetStaticClassType("UIKit.UITextContentType"));
    }

    [Fact]
    public void IsNetStaticClassType_UIKeyboardType_ReturnsFalse()
    {
        Assert.False(MemberEmissionValidator.IsNetStaticClassType("UIKit.UIKeyboardType"));
    }

    [Fact]
    public void ReferencesUnsupportedModule_FoundationPredicate_ReturnsTrue()
    {
        // Foundation.Predicate is auto-bridged in Swift but not present in .NET Foundation —
        // referencing it would produce CS0234. Now configured via apple-frameworks.json's
        // netUnavailableTypes entry and surfaced through AppleFrameworkRegistry.
        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(
            new NamedTypeSpec("Foundation.Predicate")));
    }

    [Fact]
    public void ReferencesUnsupportedModule_FoundationLocalizedStringResource_ReturnsTrue()
    {
        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(
            new NamedTypeSpec("Foundation.LocalizedStringResource")));
    }

    [Fact]
    public void ReferencesUnsupportedModule_OptionalFoundationPredicate_ReturnsTrue()
    {
        // SwiftOptional<Foundation.Predicate> — recursion into generic parameters.
        var inner = new NamedTypeSpec("Foundation.Predicate");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(optional));
    }

    [Fact]
    public void ReferencesUnsupportedModule_NonUnavailableFoundationType_ReturnsFalse()
    {
        // Foundation.Date is supported and must not be flagged.
        Assert.False(MemberEmissionValidator.ReferencesUnsupportedModule(
            new NamedTypeSpec("Foundation.Date")));
    }

    [Fact]
    public void ReferencesUnsupportedModule_TypeWithoutModulePrefix_DoesNotThrow()
    {
        // Regression for ArgumentException crash in ValidationRuleSet when a NamedTypeSpec
        // lacked a module qualifier (e.g., unqualified generic parameter "Attributes").
        // The Unemittable-flag lookup previously called SwiftTypeName.FromModuleQualifiedName
        // unconditionally and threw. Now guarded by namedType.HasModule().
        var typeSpec = new NamedTypeSpec("Attributes");
        var exception = Record.Exception(() =>
            MemberEmissionValidator.ReferencesUnsupportedModule(typeSpec, CreateTypeDatabase()));
        Assert.Null(exception);
    }

    [Fact]
    public void ReferencesUnsupportedModule_UnemittableTypeRecord_ReturnsTrue()
    {
        // Types flagged Unemittable (e.g., single-case no-payload enums marked so by the
        // parser) must be treated as unsupported — no emitted C# type exists to reference.
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Marker"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Marker"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Marker"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.Unemittable,
                Kind = TypeRecordKind.Enum,
            });
        db.AddModuleDatabase(module);

        var typeSpec = new NamedTypeSpec("TestModule.Marker");
        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(typeSpec, db));
    }

    #endregion

    #region ContainsAssociatedTypeReference Tests

    [Fact]
    public void ContainsAssociatedTypeReference_NullTypeSpec_ReturnsFalse()
    {
        Assert.False(MemberEmissionValidator.ContainsAssociatedTypeReference(null));
    }

    [Fact]
    public void ContainsAssociatedTypeReference_SimpleNamedType_ReturnsFalse()
    {
        Assert.False(MemberEmissionValidator.ContainsAssociatedTypeReference(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void ContainsAssociatedTypeReference_DirectAssociatedType_ReturnsTrue()
    {
        Assert.True(MemberEmissionValidator.ContainsAssociatedTypeReference(
            new AssociatedTypeReferenceSpec("Self.Element")));
    }

    [Fact]
    public void ContainsAssociatedTypeReference_NestedInGenericParam_ReturnsTrue()
    {
        // Swift.Array<Self.Element> — the generic param contains an associated type ref
        var arrayType = new NamedTypeSpec("Swift.Array",
            new TypeSpec[] { new AssociatedTypeReferenceSpec("Self.Element") });
        Assert.True(MemberEmissionValidator.ContainsAssociatedTypeReference(arrayType));
    }

    [Fact]
    public void ContainsAssociatedTypeReference_InClosureReturn_ReturnsTrue()
    {
        // (Swift.Int) -> Self.Element
        var closure = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new AssociatedTypeReferenceSpec("Self.Element"));
        Assert.True(MemberEmissionValidator.ContainsAssociatedTypeReference(closure));
    }

    [Fact]
    public void ContainsAssociatedTypeReference_InClosureArgs_ReturnsTrue()
    {
        // (Self.Element) -> Swift.Int
        var closure = new ClosureTypeSpec(
            new AssociatedTypeReferenceSpec("Self.Element"),
            new NamedTypeSpec("Swift.Int"));
        Assert.True(MemberEmissionValidator.ContainsAssociatedTypeReference(closure));
    }

    [Fact]
    public void ContainsAssociatedTypeReference_InTupleElement_ReturnsTrue()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new AssociatedTypeReferenceSpec("Self.Element")
        });
        Assert.True(MemberEmissionValidator.ContainsAssociatedTypeReference(tuple));
    }

    [Fact]
    public void ContainsAssociatedTypeReference_CleanClosure_ReturnsFalse()
    {
        var closure = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        Assert.False(MemberEmissionValidator.ContainsAssociatedTypeReference(closure));
    }

    #endregion

    #region ReferencesUnsupportedModule SwiftUI Tests

    [Fact]
    public void ReferencesUnsupportedModule_SwiftUIType_AlwaysReturnsTrue()
    {
        // SwiftUI types are always unsupported in standalone library bindings,
        // even if registered in the type database.
        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(new NamedTypeSpec("SwiftUI.Color")));
    }

    [Fact]
    public void ReferencesUnsupportedModule_SwiftUICoreType_AlwaysReturnsTrue()
    {
        // SwiftUICore is the internal split-out of SwiftUI in newer SDKs and must be
        // suppressed identically to SwiftUI. Without parity here, member signatures
        // referencing e.g. SwiftUICore.View leak past the gate and fail to compile.
        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(new NamedTypeSpec("SwiftUICore.View")));
    }

    [Fact]
    public void ReferencesUnsupportedModule_CombineType_AlwaysReturnsTrue()
    {
        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(new NamedTypeSpec("Combine.Publisher")));
    }

    [Fact]
    public void ReferencesUnsupportedModule_SwiftType_ReturnsFalse()
    {
        Assert.False(MemberEmissionValidator.ReferencesUnsupportedModule(new NamedTypeSpec("Swift.Int")));
    }

    #endregion
}
