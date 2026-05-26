// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for <see cref="ConstructorAdmissibility"/>, the single predicate the
/// `_SBW_CI_`/GSF open paths, CSM closed forms, and the normal ctor wrapper all consult.
/// Two facets:
///   • cheap, receiver-independent filters (`_const` parameter, module-internal/unavailable), and
///   • the parent-generic constrained-extension `where`-clause check that decides whether an
///     init can be erased through an OPEN form against the unconstrained type.
/// </summary>
public class ConstructorAdmissibilityTests
{
    // ── Cheap filters ─────────────────────────────────────────────────────────────

    [Fact]
    public void HasConstLiteralParameter_True_WhenAnyNonSelfParamIsConstLiteral()
    {
        var method = Ctor(
            ReturnArg(),
            Param("identifier", "Swift.String", isConstLiteral: true));

        Assert.True(ConstructorAdmissibility.HasConstLiteralParameter(method));
    }

    [Fact]
    public void HasConstLiteralParameter_False_WhenNoConstLiteralParam()
    {
        var method = Ctor(
            ReturnArg(),
            Param("count", "Swift.Int"));

        Assert.False(ConstructorAdmissibility.HasConstLiteralParameter(method));
    }

    [Fact]
    public void PassesConstructorCheapFilters_RejectsConstLiteralParameter()
    {
        var method = Ctor(
            ReturnArg(),
            Param("identifier", "Swift.String", isConstLiteral: true));

        Assert.False(ConstructorAdmissibility.PassesConstructorCheapFilters(method, out var reason));
        Assert.Contains("_const", reason);
    }

    [Fact]
    public void PassesConstructorCheapFilters_RejectsModuleInternalInit()
    {
        var method = Ctor(ReturnArg(), Param("count", "Swift.Int"));
        method.IsModuleInternal = true;

        Assert.False(ConstructorAdmissibility.PassesConstructorCheapFilters(method, out var reason));
        Assert.Contains("internal", reason);
    }

    [Fact]
    public void PassesConstructorCheapFilters_AcceptsPlainPublicInit()
    {
        var method = Ctor(ReturnArg(), Param("count", "Swift.Int"));

        Assert.True(ConstructorAdmissibility.PassesConstructorCheapFilters(method, out var reason));
        Assert.Null(reason);
    }

    // ── Parent-generic constrained-extension where clause ──────────────────────────

    [Fact]
    public void HasUnsatisfiableConstraint_True_ForSameTypeExtensionConstraint()
    {
        // `extension Box where Value.Element == Int { init(intMarker:) }`
        var parent = GenericClass("Box", GenericParam("Value")); // unconstrained
        var method = Ctor(ReturnArg(), Param("intMarker", "Swift.Int"));
        method.GenericParameters.Add(GenericParam("Value",
            assoc: SameType(new[] { "Element" }, "Swift.Int")));

        Assert.True(ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    [Fact]
    public void HasUnsatisfiableConstraint_True_ForConformanceExtensionConstraint()
    {
        // `extension Box where Value.Element : Collectionish { init(ropeFlag:) }`
        var parent = GenericClass("Box", GenericParam("Value"));
        var method = Ctor(ReturnArg(), Param("ropeFlag", "Swift.Bool"));
        method.GenericParameters.Add(GenericParam("Value",
            assoc: Conformance(new[] { "Element" }, "TestModule.Collectionish")));

        Assert.True(ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    [Fact]
    public void HasUnsatisfiableConstraint_False_WhenParentDeclaresTheSameRecursiveConstraint()
    {
        // `class Box<Value> where Value.Element == Int` — the constraint is on the type itself,
        // so it appears on EVERY init and a plain in-body init must NOT be rejected.
        var parent = GenericClass("Box", GenericParam("Value",
            assoc: SameType(new[] { "Element" }, "Swift.Int")));
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int"));
        method.GenericParameters.Add(GenericParam("Value",
            assoc: SameType(new[] { "Element" }, "Swift.Int")));

        Assert.False(ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    [Fact]
    public void HasUnsatisfiableConstraint_False_ForMethodOwnGenericParam()
    {
        // A constraint on a METHOD-own generic param (τ_1) is the CSM/GSF method-generic
        // dimension, not a parent-type-erasure concern.
        var parent = GenericClass("Box", GenericParam("Value"));
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int"));
        method.GenericParameters.Add(GenericParam("U",
            generic: Conformance(System.Array.Empty<string>(), "TestModule.Marker")));

        Assert.False(ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    [Fact]
    public void HasUnsatisfiableConstraint_False_ForNonGenericParent()
    {
        var parent = NonGenericClass("Plain");
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int"));
        method.GenericParameters.Add(GenericParam("Value",
            assoc: SameType(new[] { "Element" }, "Swift.Int")));

        Assert.False(ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    [Fact]
    public void HasUnsatisfiableConstraint_False_WhenInitHasNoGenericParameters()
    {
        var parent = GenericClass("Box", GenericParam("Value"));
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int")); // no method generic params

        Assert.False(ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    // ── Minimal model builders ─────────────────────────────────────────────────────

    private static readonly ModuleDecl Module = new()
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

    private static ArgumentDecl ReturnArg() => new()
    {
        Name = "",
        PrivateName = "",
        SwiftTypeSpec = TupleTypeSpec.Empty,
        HasDefaultArg = false,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = Module
    };

    private static ArgumentDecl Param(string name, string swiftType, bool isConstLiteral = false) => new()
    {
        Name = name,
        PrivateName = name,
        SwiftTypeSpec = new NamedTypeSpec(swiftType),
        HasDefaultArg = false,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = Module,
        IsConstLiteral = isConstLiteral
    };

    private static MethodDecl Ctor(params ArgumentDecl[] signature) => new()
    {
        Name = "init",
        MangledName = "$s10TestModule3BoxCyACyxGcfC",
        MethodType = MethodType.Instance,
        IsConstructor = true,
        CSSignature = new List<ArgumentDecl>(signature),
        GenericParameters = new List<GenericArgumentDecl>(),
        ParentDecl = null,
        ModuleDecl = Module,
        Throws = false,
        IsAsync = false,
        Visibility = Visibility.Public
    };

    private static GenericParameterConformance SameType(string[] path, string target) =>
        new(path, SwiftTypeName.FromModuleQualifiedName(target), ConformanceKind.ConcreteType);

    private static GenericParameterConformance Conformance(string[] path, string target) =>
        new(path, SwiftTypeName.FromModuleQualifiedName(target), ConformanceKind.Protocol);

    private static GenericArgumentDecl GenericParam(
        string name,
        GenericParameterConformance? generic = null,
        GenericParameterConformance? assoc = null) =>
        new(name, name,
            generic is null ? new List<GenericParameterConformance>() : new List<GenericParameterConformance> { generic },
            assoc is null ? new List<GenericParameterConformance>() : new List<GenericParameterConformance> { assoc });

    private static ClassDecl GenericClass(string name, params GenericArgumentDecl[] genericParams)
    {
        var decl = NonGenericClass(name);
        decl.GenericParameters = new List<GenericArgumentDecl>(genericParams);
        return decl;
    }

    private static ClassDecl NonGenericClass(string name) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
        MangledName = $"$s10TestModule{name.Length}{name}CN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = Module,
        ModuleDecl = Module
    };
}
