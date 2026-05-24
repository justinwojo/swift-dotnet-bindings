// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration;
using Xunit;

#nullable enable

namespace BindingsGeneration.Tests;

public class GenericSignatureParserTests
{
    [Fact]
    public void ParseGenericSignature_ReturnsEmpty_WhenGenericSignatureIsNullOrEmpty()
    {
        string? genericSig = null;
        string? sugaredSig = null;

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseGenericSignature_UsesFallback_WhenSugaredSignatureIsNullOrEmpty()
    {
        // When sugared signature is missing, use the generic signature itself as fallback
        var genericSig = "<τ_0_0>";
        string? sugaredSig = null;

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Single(result);
        var decl = result[0];
        // Both TypeName and SugaredTypeName should be the same (the generic name)
        Assert.Equal("τ_0_0", decl.TypeName);
        Assert.Equal("τ_0_0", decl.SugaredTypeName);
        Assert.Empty(decl.GenericConformances);
    }

    [Fact]
    public void ParseGenericSignature_UsesFallback_WithConstraints()
    {
        // When sugared signature is missing but there are constraints
        var genericSig = "<τ_0_0 where τ_0_0 : Swift.Equatable>";
        string? sugaredSig = null;

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Single(result);
        var decl = result[0];
        Assert.Equal("τ_0_0", decl.TypeName);
        Assert.Equal("τ_0_0", decl.SugaredTypeName);
        Assert.Single(decl.GenericConformances);
        var conformance = Assert.IsType<GenericParameterConformance>(decl.GenericConformances[0]);
        Assert.Equal("Swift.Equatable", conformance.ConformanceTarget.ModuleQualifiedName);
    }

    [Fact]
    public void ParseGenericSignature_ParsesSingleParamNoConstraints()
    {
        var genericSig = "<τ_0_0>";
        var sugaredSig = "<T>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Single(result);
        var decl = result[0];
        Assert.Equal("τ_0_0", decl.TypeName);
        Assert.Equal("T", decl.SugaredTypeName);
        Assert.Empty(decl.GenericConformances);
    }

    [Fact]
    public void ParseGenericSignature_ParsesMultipleParamsNoConstraints()
    {
        var genericSig = "<τ_0_0, τ_0_1>";
        var sugaredSig = "<T, U>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Equal(2, result.Count);

        var first = result[0];
        Assert.Equal("τ_0_0", first.TypeName);
        Assert.Equal("T", first.SugaredTypeName);
        Assert.Empty(first.GenericConformances);

        var second = result[1];
        Assert.Equal("τ_0_1", second.TypeName);
        Assert.Equal("U", second.SugaredTypeName);
        Assert.Empty(second.GenericConformances);
    }

    [Fact]
    public void ParseGenericSignature_ParsesSingleParamWithConstraints()
    {
        var genericSig = "<τ_0_0 where τ_0_0 : Swift.Equatable>";
        var sugaredSig = "<T where T : Swift.Equatable>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Single(result);
        var decl = result[0];
        Assert.Equal("τ_0_0", decl.TypeName);
        Assert.Equal("T", decl.SugaredTypeName);
        Assert.Single(decl.GenericConformances);
        var conformance = Assert.IsType<GenericParameterConformance>(decl.GenericConformances[0]);
        Assert.Equal("τ_0_0", conformance.Path[0]);
        Assert.Equal("Swift.Equatable", conformance.ConformanceTarget.ModuleQualifiedName);
    }

    [Fact]
    public void ParseGenericSignature_ParsesMultipleParamsWithConstraints()
    {
        var genericSig = "<τ_0_0, τ_0_1 where τ_0_0 : Swift.Equatable, τ_0_1 : Swift.Hashable>";
        var sugaredSig = "<T, U where T : Swift.Equatable, U : Swift.Hashable>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Equal(2, result.Count);

        var first = result[0];
        Assert.Equal("τ_0_0", first.TypeName);
        Assert.Equal("T", first.SugaredTypeName);
        Assert.Single(first.GenericConformances);
        var firstConformance = Assert.IsType<GenericParameterConformance>(first.GenericConformances[0]);
        Assert.Equal("τ_0_0", firstConformance.Path[0]);
        Assert.Equal("Swift.Equatable", firstConformance.ConformanceTarget.ModuleQualifiedName);

        var second = result[1];
        Assert.Equal("τ_0_1", second.TypeName);
        Assert.Equal("U", second.SugaredTypeName);
        Assert.Single(second.GenericConformances);
        var secondConformance = Assert.IsType<GenericParameterConformance>(second.GenericConformances[0]);
        Assert.Equal("τ_0_1", secondConformance.Path[0]);
        Assert.Equal("Swift.Hashable", secondConformance.ConformanceTarget.ModuleQualifiedName);
    }

    [Fact]
    public void ParseGenericSignature_SkipsConstructedGenericConstraint_WithoutThrowing()
    {
        // A constraint whose target is a constructed generic (e.g. `: KeyPath<Intent, Parameter>`)
        // is not representable as a nominal SwiftTypeName. It must be skipped, not thrown on —
        // throwing drops the whole enclosing decl silently (HandleNode swallows the exception).
        var genericSig = "<τ_0_0, τ_0_1 where τ_0_0 : Swift.Equatable, τ_0_1 : Swift.KeyPath<τ_0_0, τ_0_0>>";
        var sugaredSig = "<T, KP where T : Swift.Equatable, KP : Swift.KeyPath<T, T>>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Equal(2, result.Count);

        // The nominal constraint on τ_0_0 is preserved.
        var first = result[0];
        Assert.Equal("τ_0_0", first.TypeName);
        Assert.Single(first.GenericConformances);
        Assert.Equal("Swift.Equatable", first.GenericConformances[0].ConformanceTarget.ModuleQualifiedName);

        // The constructed-generic constraint on τ_0_1 is dropped (unrepresentable), not recorded.
        var second = result[1];
        Assert.Equal("τ_0_1", second.TypeName);
        Assert.Empty(second.GenericConformances);
    }

    [Fact]
    public void ParseGenericSignature_HandlesConstructedGenericTargetWithInnerComma()
    {
        // Mirrors AppShortcutParameterPresentation's signature: a four-param pack whose
        // last param is constrained to a constructed generic carrying an inner comma
        // (`KeyPath<Intent, Parameter>`), and whose Parameter is a `==` same-type bound
        // to another constructed generic. The inner comma must not split the constraint
        // clause, and neither constructed target may throw.
        var genericSig = "<τ_0_0, τ_0_1, τ_0_2, τ_0_3 where τ_0_0 : AppIntents.AppIntent, τ_0_1 : AppIntents._IntentValue, τ_0_2 == AppIntents.IntentParameter<τ_0_1>, τ_0_3 : Swift.KeyPath<τ_0_0, τ_0_2>>";
        var sugaredSig = "<Intent, Value, Parameter, ParameterKeyPath where Intent : AppIntents.AppIntent, Value : AppIntents._IntentValue, Parameter == AppIntents.IntentParameter<Value>, ParameterKeyPath : Swift.KeyPath<Intent, Parameter>>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Equal(4, result.Count);

        // Nominal constraints survive.
        Assert.Equal("AppIntents.AppIntent", result[0].GenericConformances.Single().ConformanceTarget.ModuleQualifiedName);
        Assert.Equal("AppIntents._IntentValue", result[1].GenericConformances.Single().ConformanceTarget.ModuleQualifiedName);

        // Both constructed-generic targets (the `==` same-type and the `:` subtype) are dropped.
        Assert.Empty(result[2].GenericConformances);
        Assert.Empty(result[3].GenericConformances);
    }

    [Fact]
    public void ParseGenericSignature_ParsesAssociatedTypeConstraints()
    {
        var genericSig = "<τ_0_0 where τ_0_0 : SomeModule.SomeProtocol, τ_0_0.ID == System.Guid, τ_0_0.ID : SomeModule.SomeProtocol>";
        var sugaredSig = "<T where T : SomeModule.SomeProtocol, T.ID == System.Guid, T.ID : SomeModule.SomeProtocol>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Single(result);
        var decl = result[0];
        Assert.Equal("τ_0_0", decl.TypeName);
        Assert.Equal("T", decl.SugaredTypeName);
        Assert.Single(decl.GenericConformances);
        Assert.Equal(2, decl.AssosiatedTypeConformances.Count);

        var proto = Assert.IsType<GenericParameterConformance>(decl.GenericConformances[0]);
        Assert.Equal("τ_0_0", proto.Path[0]);
        Assert.Equal("SomeModule.SomeProtocol", proto.ConformanceTarget.ModuleQualifiedName);
        Assert.Equal(ConformanceKind.Protocol, proto.Kind);

        proto = Assert.IsType<GenericParameterConformance>(decl.AssosiatedTypeConformances[0]);
        Assert.Equal("τ_0_0", proto.Path[0]);
        Assert.Equal("ID", proto.Path[1]);
        Assert.Equal("System.Guid", proto.ConformanceTarget.ModuleQualifiedName);
        Assert.Equal(ConformanceKind.ConcreteType, proto.Kind);

        proto = Assert.IsType<GenericParameterConformance>(decl.AssosiatedTypeConformances[1]);
        Assert.Equal("τ_0_0", proto.Path[0]);
        Assert.Equal("ID", proto.Path[1]);
        Assert.Equal("SomeModule.SomeProtocol", proto.ConformanceTarget.ModuleQualifiedName);
        Assert.Equal(ConformanceKind.Protocol, proto.Kind);
    }
}
