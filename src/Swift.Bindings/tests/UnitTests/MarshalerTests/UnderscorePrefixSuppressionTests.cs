// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for underscore-prefixed type suppression logic (CollectUnderscoreSuppressedTypeNames).
/// Verifies that:
/// 1. _Foo types with no public references are collected for suppression
/// 2. _BaseClass referenced as superclass by non-_ type is NOT suppressed
/// 3. _Protocol conformed-to by non-_ type is NOT suppressed
/// 4. _Kept types in keepUnderscoreTypes override are NOT suppressed
/// 5. Module-qualified names are used (no collision for nested types)
/// </summary>
public class UnderscorePrefixSuppressionTests
{
    [Fact]
    public void UnreferencedUnderscoreType_IsSuppressed()
    {
        var module = CreateModule("TestModule",
            CreateStruct("_InternalHelper", "TestModule._InternalHelper"),
            CreateStruct("PublicWidget", "TestModule.PublicWidget"));

        var result = BindingsGenerator.CollectUnderscoreSuppressedTypeNames(module);

        Assert.Contains("TestModule._InternalHelper", result);
        Assert.DoesNotContain("TestModule.PublicWidget", result);
    }

    [Fact]
    public void UnderscoreBaseClass_IsNotSuppressed()
    {
        var baseClass = CreateClass("_AbstractBase", "TestModule._AbstractBase");
        var derivedClass = CreateClass("ConcreteWidget", "TestModule.ConcreteWidget",
            superclassName: "TestModule._AbstractBase");

        var module = CreateModule("TestModule", baseClass, derivedClass);

        var result = BindingsGenerator.CollectUnderscoreSuppressedTypeNames(module);

        Assert.DoesNotContain("TestModule._AbstractBase", result);
    }

    [Fact]
    public void UnderscoreProtocol_ConformedByNonUnderscore_IsNotSuppressed()
    {
        var proto = CreateProtocol("_InternalProtocol", "TestModule._InternalProtocol");
        var conformingStruct = CreateStructWithConformance("PublicType", "TestModule.PublicType",
            "TestModule._InternalProtocol");

        var module = CreateModule("TestModule", proto, conformingStruct);

        var result = BindingsGenerator.CollectUnderscoreSuppressedTypeNames(module);

        Assert.DoesNotContain("TestModule._InternalProtocol", result);
    }

    [Fact]
    public void UnderscoreProtocol_ConformedOnlyByUnderscore_IsSuppressed()
    {
        var proto = CreateProtocol("_InternalProtocol", "TestModule._InternalProtocol");
        var conformingStruct = CreateStructWithConformance("_InternalType", "TestModule._InternalType",
            "TestModule._InternalProtocol");

        var module = CreateModule("TestModule", proto, conformingStruct);

        var result = BindingsGenerator.CollectUnderscoreSuppressedTypeNames(module);

        // Both underscore types are suppressed — the protocol is only referenced by another underscore type
        Assert.Contains("TestModule._InternalProtocol", result);
        Assert.Contains("TestModule._InternalType", result);
    }

    [Fact]
    public void KeepUnderscoreTypes_Override_PreservesType()
    {
        var module = CreateModule("TestModule",
            CreateStruct("_KeptType", "TestModule._KeptType"),
            CreateStruct("_SuppressedType", "TestModule._SuppressedType"));

        var keepSet = new HashSet<string> { "TestModule._KeptType" };
        var result = BindingsGenerator.CollectUnderscoreSuppressedTypeNames(module, keepUnderscoreTypes: keepSet);

        Assert.DoesNotContain("TestModule._KeptType", result);
        Assert.Contains("TestModule._SuppressedType", result);
    }

    [Fact]
    public void NonUnderscoreTypes_NeverSuppressed()
    {
        var module = CreateModule("TestModule",
            CreateStruct("PublicType", "TestModule.PublicType"),
            CreateClass("AnotherPublic", "TestModule.AnotherPublic"));

        var result = BindingsGenerator.CollectUnderscoreSuppressedTypeNames(module);

        Assert.Empty(result);
    }

    [Fact]
    public void QualifiedNames_AvoidCollision()
    {
        // Two types with the same short name in different nesting contexts
        var outer = CreateStruct("_Helper", "TestModule._Helper");
        var innerParent = CreateClassWithNestedType("Container", "TestModule.Container",
            CreateStruct("_Helper", "TestModule.Container._Helper"));

        var module = CreateModule("TestModule", outer, innerParent);

        var result = BindingsGenerator.CollectUnderscoreSuppressedTypeNames(module);

        // Both are separate qualified names, both should be suppressed
        Assert.Contains("TestModule._Helper", result);
        Assert.Contains("TestModule.Container._Helper", result);
    }

    [Fact]
    public void EmptyModule_ReturnsEmptySet()
    {
        var module = CreateModule("TestModule");

        var result = BindingsGenerator.CollectUnderscoreSuppressedTypeNames(module);

        Assert.Empty(result);
    }

    [Fact]
    public void ModuleEmissionContext_IsUnderscoreSuppressed_ReturnsTrueForSuppressed()
    {
        var ctx = new ModuleEmissionContext();
        ctx.SetUnderscoreSuppressedNames(new HashSet<string> { "TestModule._Foo", "TestModule._Bar" });

        Assert.True(ctx.IsUnderscoreSuppressed("TestModule._Foo"));
        Assert.True(ctx.IsUnderscoreSuppressed("TestModule._Bar"));
        Assert.False(ctx.IsUnderscoreSuppressed("TestModule.PublicType"));
    }

    [Fact]
    public void ModuleEmissionContext_FreshlyConstructed_NothingSuppressed()
    {
        var ctx = new ModuleEmissionContext();

        Assert.False(ctx.IsUnderscoreSuppressed("TestModule._Anything"));
    }

    [Fact]
    public void SkipReason_UnderscorePrefixInternal_Exists()
    {
        // Verify the enum value is accessible
        var reason = SkipReason.UnderscorePrefixInternal;
        Assert.Equal(SkipReason.UnderscorePrefixInternal, reason);
    }

    [Fact]
    public void WorkaroundRecommendation_Exists()
    {
        var recommendation = WorkaroundRecommendations.GetRecommendation(SkipReason.UnderscorePrefixInternal);
        Assert.NotNull(recommendation);
        Assert.Contains("underscore", recommendation, StringComparison.OrdinalIgnoreCase);
    }

    #region Helper Methods

    private static ModuleDecl CreateModule(string name, params TypeDecl[] types)
    {
        var module = new ModuleDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new(),
            Methods = new(),
            Types = types.ToList(),
            Dependencies = new(),
            Protocols = types.OfType<ProtocolDecl>().ToList(),
        };
        module.ModuleDecl = module;
        return module;
    }

    private static StructDecl CreateStruct(string name, string qualifiedName) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName),
        MangledName = $"s{name}",
        Properties = new(),
        Methods = new(),
        Types = new(),
        Operators = new(),
        IsFrozen = true,
        Conformances = new(),
        MetadataAccessor = "",
    };

    private static StructDecl CreateStructWithConformance(string name, string qualifiedName, string protocolQualifiedName) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName),
        MangledName = $"s{name}",
        Properties = new(),
        Methods = new(),
        Types = new(),
        Operators = new(),
        IsFrozen = true,
        Conformances = new()
        {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName(qualifiedName),
                SwiftTypeName.FromModuleQualifiedName(protocolQualifiedName),
                "")
        },
        MetadataAccessor = "",
    };

    private static ClassDecl CreateClass(string name, string qualifiedName, string? superclassName = null) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName),
        MangledName = $"s{name}",
        Properties = new(),
        Methods = new(),
        Types = new(),
        Operators = new(),
        Conformances = new(),
        SuperclassNames = superclassName != null ? new() { superclassName } : new(),
    };

    private static ClassDecl CreateClassWithNestedType(string name, string qualifiedName, TypeDecl nested) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName),
        MangledName = $"s{name}",
        Properties = new(),
        Methods = new(),
        Types = new() { nested },
        Operators = new(),
        Conformances = new(),
        SuperclassNames = new(),
    };

    private static ProtocolDecl CreateProtocol(string name, string qualifiedName) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName),
        MangledName = $"s{name}",
        Properties = new(),
        Methods = new(),
        Types = new(),
        Operators = new(),
    };

    #endregion
}
