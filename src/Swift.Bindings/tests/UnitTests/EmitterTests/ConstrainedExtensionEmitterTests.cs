// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ConstrainedExtensionEmitter"/> — the constrained-extension
/// specialization detection and grouping logic.
/// </summary>
public class ConstrainedExtensionEmitterTests
{
    [Fact]
    public void FindConstrainedSpecializations_NoGenericType_ReturnsEmpty()
    {
        var typeDecl = CreateStructDecl("MyStruct", isGeneric: false);
        var result = ConstrainedExtensionEmitter.FindConstrainedSpecializations(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void FindConstrainedSpecializations_SingleSpecPerName_GroupsByConcreteType()
    {
        // Bug 0.10.0 — single-specialization same-type-constraint properties are
        // bound to a closed-generic mangled symbol (e.g.,
        // Forecast<MinuteWeather>.Summary), so they must be routed to the
        // closed-generic extension-method emitter even though there is no
        // sibling-name conflict.
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        typeDecl.Properties.Add(CreatePropertyWithConstraint("propA", "TestModule.ConcreteA"));
        typeDecl.Properties.Add(CreatePropertyWithConstraint("propB", "TestModule.ConcreteB"));

        var result = ConstrainedExtensionEmitter.FindConstrainedSpecializations(typeDecl);
        Assert.Equal(2, result.Count);

        var concreteA = SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteA");
        var concreteB = SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteB");

        Assert.True(result.ContainsKey(concreteA));
        Assert.True(result.ContainsKey(concreteB));
        Assert.Single(result[concreteA]);
        Assert.Equal("propA", result[concreteA][0].Name);
        Assert.Single(result[concreteB]);
        Assert.Equal("propB", result[concreteB][0].Name);
    }

    [Fact]
    public void FindConstrainedSpecializations_NonGenericType_ReturnsEmpty()
    {
        // Non-generic types cannot have same-type-constrained extensions —
        // skip the whole walk regardless of property shape.
        var typeDecl = CreateStructDecl("Concrete", isGeneric: false);
        typeDecl.Properties.Add(CreateUnconstrainedProperty("prop"));

        var result = ConstrainedExtensionEmitter.FindConstrainedSpecializations(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void FindConstrainedSpecializations_TwoSpecializations_GroupsByConcreteType()
    {
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        typeDecl.Properties.Add(CreatePropertyWithConstraint("label", "TestModule.Alpha"));
        typeDecl.Properties.Add(CreatePropertyWithConstraint("label", "TestModule.Beta"));

        var result = ConstrainedExtensionEmitter.FindConstrainedSpecializations(typeDecl);
        Assert.Equal(2, result.Count);

        var alphaKey = SwiftTypeName.FromModuleQualifiedName("TestModule.Alpha");
        var betaKey = SwiftTypeName.FromModuleQualifiedName("TestModule.Beta");

        Assert.True(result.ContainsKey(alphaKey), "Should contain Alpha specialization");
        Assert.True(result.ContainsKey(betaKey), "Should contain Beta specialization");
        Assert.Single(result[alphaKey]);
        Assert.Single(result[betaKey]);
    }

    [Fact]
    public void FindConstrainedSpecializations_MixedConstrainedAndUnconstrained_ReturnsEmpty()
    {
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        typeDecl.Properties.Add(CreatePropertyWithConstraint("label", "TestModule.Alpha"));
        typeDecl.Properties.Add(CreateUnconstrainedProperty("label")); // no constraint

        var result = ConstrainedExtensionEmitter.FindConstrainedSpecializations(typeDecl);
        Assert.Empty(result); // not all siblings are constrained → skip all
    }

    [Fact]
    public void ExtractSameTypeConstraint_PropertyWithConcreteConstraint_ReturnsTarget()
    {
        var property = CreatePropertyWithConstraint("prop", "TestModule.ConcreteType");
        var result = ConstrainedExtensionEmitter.ExtractSameTypeConstraint(property);

        Assert.NotNull(result);
        Assert.Equal("TestModule", result!.Module);
        Assert.Equal("ConcreteType", result.Name);
    }

    [Fact]
    public void ExtractSameTypeConstraint_PropertyWithProtocolConstraint_ReturnsNull()
    {
        var property = CreatePropertyWithProtocolConstraint("prop", "TestModule.SomeProtocol");
        var result = ConstrainedExtensionEmitter.ExtractSameTypeConstraint(property);
        Assert.Null(result); // Protocol constraints are not same-type
    }

    [Fact]
    public void ExtractSameTypeConstraint_PropertyWithNoConstraint_ReturnsNull()
    {
        var property = CreateUnconstrainedProperty("prop");
        var result = ConstrainedExtensionEmitter.ExtractSameTypeConstraint(property);
        Assert.Null(result);
    }

    // ==================== Helpers ====================

    private static StructDecl CreateStructDecl(string name, bool isGeneric)
    {
        return new StructDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = isGeneric
                ? new List<GenericArgumentDecl> { new("τ_0_0", "T", new(), new()) }
                : new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };
    }

    private static StructDecl CreateGenericStructDecl(string name, string genericParamName)
    {
        return new StructDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", genericParamName, new(), new())
            },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };
    }

    private static PropertyDecl CreatePropertyWithConstraint(string name, string concreteType)
    {
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(concreteType),
            ConformanceKind.ConcreteType);

        var getterMethod = new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperV{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null
        };

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            AvailabilityAnnotations = null
        };
    }

    private static PropertyDecl CreatePropertyWithProtocolConstraint(string name, string protocolType)
    {
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(protocolType),
            ConformanceKind.Protocol);

        var getterMethod = new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperV{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null
        };

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            AvailabilityAnnotations = null
        };
    }

    private static PropertyDecl CreateUnconstrainedProperty(string name)
    {
        var getterMethod = new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperV{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new(), new()) // no constraints
            },
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null
        };

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            AvailabilityAnnotations = null
        };
    }
}
