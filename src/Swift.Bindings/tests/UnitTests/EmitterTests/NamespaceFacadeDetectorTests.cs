// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="NamespaceFacadeDetector.IsNamespaceFacade"/>.
/// The predicate is conservative — only matches the strict
/// "uninhabited type as namespace" idiom (no member surface, at least
/// one nested type, zero conformances). Anything with runtime semantics
/// (stored property, init, instance/static method, operator, subscript,
/// conformance, generic parameter, enum case) falls through to the
/// existing class-emission path.
///
/// See <c>bug-0.10.0-namespace-facade-as-static-class.md</c> (Bundle 04 #3).
/// </summary>
public class NamespaceFacadeDetectorTests
{
    #region Positive cases — facade matches

    [Fact]
    public void IsNamespaceFacade_True_StructWithOnlyNestedTypes()
    {
        var nested = CreateNestedStruct("Nested");
        var facade = CreateStruct("BlinkIDSDK", nestedTypes: new[] { nested });

        Assert.True(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    [Fact]
    public void IsNamespaceFacade_True_CaselessEnumWithOnlyNestedTypes()
    {
        var nested = CreateNestedStruct("Holder");
        var facade = CreateEnum("Constants", nestedTypes: new[] { nested });

        Assert.True(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    #endregion

    #region Negative cases — non-facade shapes

    [Fact]
    public void IsNamespaceFacade_False_StructWithStoredProperty()
    {
        var nested = CreateNestedStruct("Nested");
        var facade = CreateStruct("HasField", nestedTypes: new[] { nested },
            properties: new[] { CreateProperty("count", isStatic: false, hasStorage: true) });

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    [Fact]
    public void IsNamespaceFacade_False_StructWithStaticProperty()
    {
        var nested = CreateNestedStruct("Nested");
        var facade = CreateStruct("HasStatic", nestedTypes: new[] { nested },
            properties: new[] { CreateProperty("shared", isStatic: true, hasStorage: true) });

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    [Fact]
    public void IsNamespaceFacade_False_StructWithMethod()
    {
        var nested = CreateNestedStruct("Nested");
        var facade = CreateStruct("HasMethod", nestedTypes: new[] { nested },
            methods: new[] { CreateMethod("doThing") });

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    [Fact]
    public void IsNamespaceFacade_False_StructWithConformance()
    {
        // Real (non-marker) protocol conformance — Foundation.NSCopying carries a
        // witness table, so the type cannot be a bare namespace facade. The
        // predicate must reject this even when every other count is zero.
        var nested = CreateNestedStruct("Nested");
        var facade = CreateStruct("HasConformance", nestedTypes: new[] { nested },
            conformances: new[] { "Foundation.NSCopying" });

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    [Fact]
    public void IsNamespaceFacade_True_StructWithImplicitMarkerConformancesOnly()
    {
        // Every Swift struct/enum gets implicit Swift.Copyable + Swift.Escapable
        // (and Swift.Sendable for Sendable-eligible shapes) auto-attached by the
        // parser. These markers carry no runtime witness table, so the predicate
        // filters them out and still recognizes the type as a namespace facade
        // — without this filter, BlinkID's BlinkIDSDK / our LocalFacade fixture
        // never triggers the namespace lift.
        var nested = CreateNestedStruct("Nested");
        var facade = CreateStruct("ImplicitMarkers", nestedTypes: new[] { nested },
            conformances: new[] { "Swift.Copyable", "Swift.Escapable", "Swift.Sendable" });

        Assert.True(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    [Fact]
    public void IsNamespaceFacade_False_GenericStruct()
    {
        var nested = CreateNestedStruct("Nested");
        var facade = CreateStruct("Generic", nestedTypes: new[] { nested });
        facade.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0",
            "T",
            new List<GenericParameterConformance>(),
            new List<GenericParameterConformance>()));

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    [Fact]
    public void IsNamespaceFacade_False_StructWithoutNestedTypes()
    {
        // Empty struct with no member surface and no nested types — not
        // a facade (nothing to scope), falls through to current emission.
        var facade = CreateStruct("Empty");

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    [Fact]
    public void IsNamespaceFacade_False_EnumWithCases()
    {
        var nested = CreateNestedStruct("Nested");
        var facade = CreateEnum("HasCases", nestedTypes: new[] { nested });
        facade.Cases.Add(new EnumCaseDecl
        {
            Name = "first",
            MangledName = "$sTestModule8HasCasesO5firstyA2CmF",
            ParentDecl = null,
            ModuleDecl = null,
        });

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    [Fact]
    public void IsNamespaceFacade_False_ClassDecl()
    {
        // Class hierarchies have runtime identity (vtables, deinit) that
        // a namespace cannot host. Predicate must reject ClassDecl even
        // when all the member-surface counts are zero.
        var classDecl = new ClassDecl
        {
            Name = "EmptyClass",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.EmptyClass"),
            MangledName = "$s10TestModule10EmptyClassCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { CreateNestedStruct("Inner") },
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = TestModule,
            ModuleDecl = TestModule,
        };

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(classDecl));
    }

    [Fact]
    public void IsNamespaceFacade_False_NestedFacadeShape()
    {
        // A facade-shaped struct nested inside a real type body must not
        // qualify. Emitting `namespace { … }` inside a class/struct/enum
        // body is invalid C# (CS0116). The lift-to-namespace transformation
        // only makes sense at module scope, where the new namespace can sit
        // alongside the module's other top-level types.
        var nestedFacade = CreateStruct("InnerFacade",
            nestedTypes: new[] { CreateNestedStruct("Leaf") });
        var outerClass = new ClassDecl
        {
            Name = "Outer",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer"),
            MangledName = "$s10TestModule5OuterCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedFacade },
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = TestModule,
            ModuleDecl = TestModule,
        };
        // Re-parent the inner facade to the outer class — this is the
        // shape the parser produces when a struct is declared inside a
        // class/struct/enum body.
        nestedFacade.ParentDecl = outerClass;
        nestedFacade.ModuleDecl = TestModule;

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(nestedFacade));
    }

    #endregion

    #region Helpers

    /// <summary>Shared module decl used as the <c>ParentDecl</c> for top-level
    /// fixtures. The detector rejects types whose parent is not a
    /// <see cref="ModuleDecl"/> so all positive-case fixtures and any
    /// negative case that would otherwise pass must wire this up.</summary>
    private static readonly ModuleDecl TestModule = new ModuleDecl
    {
        Name = "TestModule",
        ParentDecl = null,
        ModuleDecl = null,
        Methods = new List<MethodDecl>(),
        Properties = new List<PropertyDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
    };

    private static StructDecl CreateStruct(string name,
        TypeDecl[]? nestedTypes = null,
        PropertyDecl[]? properties = null,
        MethodDecl[]? methods = null,
        string[]? conformances = null)
    {
        var conformanceList = (conformances ?? Array.Empty<string>()).Select(p => new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            SwiftTypeName.FromModuleQualifiedName(p),
            $"${name}_{p.Replace(".", "_")}_conformance"
        )).ToList();

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = (properties ?? Array.Empty<PropertyDecl>()).ToList(),
            Methods = (methods ?? Array.Empty<MethodDecl>()).ToList(),
            Types = (nestedTypes ?? Array.Empty<TypeDecl>()).ToList(),
            Operators = new List<OperatorDecl>(),
            Conformances = conformanceList,
            ParentDecl = TestModule,
            ModuleDecl = TestModule,
            IsFrozen = false,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
        };
    }

    private static EnumDecl CreateEnum(string name,
        TypeDecl[]? nestedTypes = null,
        PropertyDecl[]? properties = null,
        MethodDecl[]? methods = null,
        string[]? conformances = null)
    {
        var conformanceList = (conformances ?? Array.Empty<string>()).Select(p => new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            SwiftTypeName.FromModuleQualifiedName(p),
            $"${name}_{p.Replace(".", "_")}_conformance"
        )).ToList();

        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}O",
            Properties = (properties ?? Array.Empty<PropertyDecl>()).ToList(),
            Methods = (methods ?? Array.Empty<MethodDecl>()).ToList(),
            Types = (nestedTypes ?? Array.Empty<TypeDecl>()).ToList(),
            Operators = new List<OperatorDecl>(),
            Conformances = conformanceList,
            ParentDecl = TestModule,
            ModuleDecl = TestModule,
            IsFrozen = false,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}OMa",
        };
    }

    private static StructDecl CreateNestedStruct(string name)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
        };
    }

    private static PropertyDecl CreateProperty(string name, bool isStatic, bool hasStorage)
    {
        return new PropertyDecl
        {
            Name = name,
            HasStorage = hasStorage,
            IsStatic = isStatic,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            Accessors = Array.Empty<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static MethodDecl CreateMethod(string name)
    {
        return new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$sTestModule{name}",
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsConstructor = false,
            MethodType = MethodType.Instance,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
        };
    }

    #endregion
}
