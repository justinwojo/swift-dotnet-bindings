#nullable enable
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ProtocolExtensionDefaultsIndex — the index used by ProtocolConformanceValidator
/// to recognize protocol extension default implementations.
/// </summary>
public class ProtocolExtensionDefaultsIndexTests
{
    [Fact]
    public void HasMethodDefault_DirectProtocol_ReturnsTrue()
    {
        // Extension on Lottie.AnyInterpolatable provides _interpolate default
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["Lottie.AnyInterpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.True(index.HasMethodDefault("Lottie.AnyInterpolatable",
            "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)"));
    }

    [Fact]
    public void HasMethodDefault_NoMatch_ReturnsFalse()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["Lottie.AnyInterpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.False(index.HasMethodDefault("Lottie.AnyInterpolatable", "nonExistent()"));
    }

    [Fact]
    public void HasMethodDefault_ConstrainedExtension_ReturnsFalse()
    {
        // Constrained extensions (where T: SomeProtocol) are filtered out during construction
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["Lottie.AnyInterpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:)", whereConstraints: new() { "Self.Value : Comparable" })
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.False(index.HasMethodDefault("Lottie.AnyInterpolatable", "_interpolate(to:amount:)"));
    }

    [Fact]
    public void HasMethodDefault_SubProtocol_SatisfiesParent()
    {
        // Interpolatable inherits AnyInterpolatable. Extension on Interpolatable provides _interpolate.
        // This should satisfy AnyInterpolatable's _interpolate requirement.
        var protocols = new List<ProtocolDecl>
        {
            CreateProtocolDecl("AnyInterpolatable", "Lottie"),
            CreateProtocolDecl("Interpolatable", "Lottie", inheritedFrom: "Lottie.AnyInterpolatable")
        };

        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["Lottie.Interpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, protocols);

        // Direct query (no conformance filter)
        Assert.True(index.HasMethodDefault("Lottie.AnyInterpolatable",
            "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)"));
    }

    [Fact]
    public void HasMethodDefault_SubProtocol_WithConcreteConformances()
    {
        // Same setup: Interpolatable inherits AnyInterpolatable
        var protocols = new List<ProtocolDecl>
        {
            CreateProtocolDecl("AnyInterpolatable", "Lottie"),
            CreateProtocolDecl("Interpolatable", "Lottie", inheritedFrom: "Lottie.AnyInterpolatable")
        };

        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["Lottie.Interpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, protocols);

        // Type that conforms to both → should satisfy
        var conformances = new HashSet<string> { "Lottie.AnyInterpolatable", "Lottie.Interpolatable" };
        Assert.True(index.HasMethodDefault("Lottie.AnyInterpolatable",
            "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)", conformances));

        // Type that only conforms to AnyInterpolatable (not Interpolatable) → should NOT satisfy
        var onlyParent = new HashSet<string> { "Lottie.AnyInterpolatable" };
        Assert.False(index.HasMethodDefault("Lottie.AnyInterpolatable",
            "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)", onlyParent));
    }

    [Fact]
    public void HasMethodDefault_QualifiedNameMatching_NoCrossModuleCollision()
    {
        // Two different modules with same protocol name — should not cross-match
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["ModuleA.Parser"] = new()
            {
                CreateExtensionMethod("parse", "parse()")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.True(index.HasMethodDefault("ModuleA.Parser", "parse()"));
        Assert.False(index.HasMethodDefault("ModuleB.Parser", "parse()"));
    }

    [Fact]
    public void HasPropertyDefault_DirectProtocol_ReturnsTrue()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Configurable"] = new()
            {
                CreateExtensionProperty("defaultConfig")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        var conformances = new HashSet<string> { "TestModule.Configurable" };
        Assert.True(index.HasPropertyDefault("TestModule.Configurable", "defaultConfig", conformances));
        Assert.False(index.HasPropertyDefault("TestModule.Configurable", "nonExistent", conformances));
    }

    [Fact]
    public void HasDirectMethodDefault_DirectProtocol_ReturnsTrue()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Configurable"] = new()
            {
                CreateExtensionMethod("configure", "configure()")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.True(index.HasDirectMethodDefault("TestModule.Configurable", "configure()"));
        Assert.False(index.HasDirectMethodDefault("TestModule.Configurable", "nonExistent()"));
    }

    [Fact]
    public void HasDirectMethodDefault_SubProtocolDefault_ReturnsFalseForParent()
    {
        // Extension on Worker (sub-protocol) provides process() — parent AnyWorker should NOT match
        var protocols = new List<ProtocolDecl>
        {
            CreateProtocolDecl("AnyWorker", "TestModule"),
            CreateProtocolDecl("Worker", "TestModule", inheritedFrom: "TestModule.AnyWorker")
        };
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Worker"] = new()
            {
                CreateExtensionMethod("process", "process()")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, protocols);

        // Direct check on parent → false (default is on sub-protocol)
        Assert.False(index.HasDirectMethodDefault("TestModule.AnyWorker", "process()"));
        // Direct check on sub-protocol → true
        Assert.True(index.HasDirectMethodDefault("TestModule.Worker", "process()"));
        // Full traversal on parent → true (HasMethodDefault does sub-protocol lookup)
        Assert.True(index.HasMethodDefault("TestModule.AnyWorker", "process()"));
    }

    [Fact]
    public void HasDirectPropertyDefault_DirectProtocol_ReturnsTrue()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Themed"] = new()
            {
                CreateExtensionProperty("color")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.True(index.HasDirectPropertyDefault("TestModule.Themed", "color"));
        Assert.False(index.HasDirectPropertyDefault("TestModule.Themed", "nonExistent"));
    }

    [Fact]
    public void HasPropertyDefault_GetterOnlyDefault_SatisfiesGetterRequirement()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Readable"] = new()
            {
                CreateExtensionProperty("value") // getter-only default
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        var conformances = new HashSet<string> { "TestModule.Readable" };
        // Getter-only requirement → satisfied by getter-only default
        Assert.True(index.HasPropertyDefault("TestModule.Readable", "value", conformances, requiresSetter: false));
    }

    [Fact]
    public void HasPropertyDefault_GetterOnlyDefault_DoesNotSatisfySetterRequirement()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.ReadWrite"] = new()
            {
                CreateExtensionProperty("value") // getter-only default
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        var conformances = new HashSet<string> { "TestModule.ReadWrite" };
        // { get set } requirement → NOT satisfied by getter-only default
        Assert.False(index.HasPropertyDefault("TestModule.ReadWrite", "value", conformances, requiresSetter: true));
    }

    [Fact]
    public void HasPropertyDefault_GetSetDefault_SatisfiesSetterRequirement()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.ReadWrite"] = new()
            {
                CreateExtensionProperty("value", hasSetter: true) // { get set } default
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        var conformances = new HashSet<string> { "TestModule.ReadWrite" };
        // { get set } requirement → satisfied by { get set } default
        Assert.True(index.HasPropertyDefault("TestModule.ReadWrite", "value", conformances, requiresSetter: true));
        // { get } requirement → also satisfied
        Assert.True(index.HasPropertyDefault("TestModule.ReadWrite", "value", conformances, requiresSetter: false));
    }

    #region Helpers

    private static ProtocolExtensionMethodDecl CreateExtensionMethod(string methodName, string printedName,
        List<string>? whereConstraints = null)
    {
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = methodName,
            PrintedName = printedName,
            RawSignature = $"func {methodName}()",
            ReturnsSelf = false,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = false,
            HasSetter = false,
            IsDeprecated = false,
            IsMutating = false,
            WhereConstraints = whereConstraints ?? new List<string>()
        };
    }

    private static ProtocolExtensionMethodDecl CreateExtensionProperty(string propertyName, bool hasSetter = false)
    {
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = propertyName,
            PrintedName = propertyName,
            RawSignature = hasSetter ? $"var {propertyName}: Int {{ get set }}" : $"var {propertyName}: Int {{ get }}",
            ReturnsSelf = false,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = true,
            HasSetter = hasSetter,
            IsDeprecated = false,
            IsMutating = false,
            WhereConstraints = new List<string>()
        };
    }

    private static ProtocolDecl CreateProtocolDecl(string name, string moduleName, string? inheritedFrom = null)
    {
        var inherited = new List<NamedTypeSpec>();
        if (inheritedFrom != null)
            inherited.Add(new NamedTypeSpec(inheritedFrom));

        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName}{name}P",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = inherited,
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion
}
