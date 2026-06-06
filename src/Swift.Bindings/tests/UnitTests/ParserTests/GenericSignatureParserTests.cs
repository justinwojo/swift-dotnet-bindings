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

    // --- P1-27 B3: layout/marker keyword constraints (AnyObject, Sendable, ...) ---
    // These have no module-qualified nominal type. FromModuleQualifiedName throws on them, and
    // that throw used to propagate to SwiftABIParser.HandleNode and discard the ENTIRE enclosing
    // decl. ParseConstraint must instead drop just the unrepresentable constraint.

    [Theory]
    [InlineData("AnyObject")]
    [InlineData("Sendable")]
    [InlineData("Escapable")]
    [InlineData("Copyable")]
    [InlineData("SendableMetatype")]
    [InlineData("Any")]
    [InlineData("Swift.Sendable")]   // module-qualified marker keyword is still dropped
    public void ParseGenericSignature_DropsLayoutKeywordConstraint_KeepsRealConstraint(string keyword)
    {
        // The keyword constraint must be dropped while the real nominal conformance survives,
        // and crucially the decl itself must not be discarded.
        var genericSig = $"<τ_0_0 where τ_0_0 : {keyword}, τ_0_0 : Swift.Equatable>";
        var sugaredSig = $"<T where T : {keyword}, T : Swift.Equatable>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Single(result);
        var conformance = Assert.IsType<GenericParameterConformance>(Assert.Single(result[0].GenericConformances));
        Assert.Equal("Swift.Equatable", conformance.ConformanceTarget.ModuleQualifiedName);
    }

    [Fact]
    public void ParseGenericSignature_KeywordOnlyConstraint_DeclSurvivesWithNoConformance()
    {
        // A param whose only constraint is a marker keyword must still yield the decl
        // (previously the throw discarded it), just with no representable conformance.
        var genericSig = "<τ_0_0 where τ_0_0 : AnyObject>";
        var sugaredSig = "<T where T : AnyObject>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Single(result);
        Assert.Equal("τ_0_0", result[0].TypeName);
        Assert.Empty(result[0].GenericConformances);
    }

    [Fact]
    public void ParseGenericSignature_DotlessConstraintTarget_IsDropped()
    {
        // A non-module-qualified (dot-less) target would make FromModuleQualifiedName throw and
        // discard the decl; it is dropped instead, leaving the real constraint intact.
        var genericSig = "<τ_0_0 where τ_0_0 : BareName, τ_0_0 : Swift.Hashable>";
        var sugaredSig = "<T where T : BareName, T : Swift.Hashable>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Single(result);
        var conformance = Assert.IsType<GenericParameterConformance>(Assert.Single(result[0].GenericConformances));
        Assert.Equal("Swift.Hashable", conformance.ConformanceTarget.ModuleQualifiedName);
    }

    // --- Same-type concrete pins (`where T == ()`) are dropped but flagged ---
    // B3 drops the unrepresentable `== ()` target; the dropped constraint would otherwise erase
    // the single-specialization confinement, letting a constrained init flow to the open
    // `_SBW_CI_`/GSF constructor wrapper and emit non-compiling Swift (the GRDB.TableAlias
    // `init(name:) where RowDecoder == ()` regression). The flag preserves that confinement.

    [Fact]
    public void ParseGenericSignature_DropsConcreteSameTypePin_FlagsParameter()
    {
        // Mirrors GRDB.TableAlias.init(name:) where RowDecoder == (): the constraint is dropped
        // (target `()` is not a nominal type) but the parameter is flagged as concretely pinned.
        var genericSig = "<τ_0_0 where τ_0_0 == ()>";
        var sugaredSig = "<RowDecoder where RowDecoder == ()>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        var decl = Assert.Single(result);
        Assert.Equal("τ_0_0", decl.TypeName);
        Assert.Empty(decl.GenericConformances);            // unrepresentable target dropped
        Assert.Empty(decl.AssosiatedTypeConformances);
        Assert.True(decl.HasUnrepresentableConcreteSameTypePin);  // confinement preserved
    }

    [Theory]
    // Same-type pin to an unrepresentable concrete target → dropped AND flagged.
    [InlineData("τ_0_0 == ()", true)]
    [InlineData("τ_0_0 == Any", true)]                  // keyword same-type pin
    // Bare same-type to ANOTHER generic parameter (`τ_0_0 == τ_0_1`) → dropped AND flagged.
    // It is NOT a working family relationship: the open `_SBW_CI_` erasure conforms the
    // UNCONSTRAINED type, which swiftc rejects identically to `== ()` ("candidate would match if
    // 'T' was the same type as 'U'"). Flagging is therefore protective, not a false positive — it
    // gates only the open/CSM erasure paths (an unconstrained parent has no other surface anyway).
    // The constructed-generic `== Foo.Bar<τ_0_1>` case below stays unflagged because those real
    // constructors reach a working method-generic path; the bare param==param form does not.
    [InlineData("τ_0_0 == τ_0_1", true)]
    // Representable / non-pin constraints → never flagged.
    [InlineData("τ_0_0 : Swift.Equatable", false)]      // protocol constraint, not a pin
    [InlineData("τ_0_0 == Swift.Int", false)]           // module-qualified concrete survives as a real constraint
    [InlineData("τ_0_0 == Foo.Bar<τ_0_1>", false)]      // constructed-generic family relationship, not a single-specialization pin
    public void ParseGenericSignature_ConcreteSameTypePinFlag_OnlySetForDroppedConcretePin(string constraint, bool expectedFlag)
    {
        // τ_0_1 is declared so the constructed-generic case has a referent; the simpler cases ignore it.
        var sig = $"<τ_0_0, τ_0_1 where {constraint}>";

        var result = GenericSignatureParser.ParseGenericSignature(sig, sig);

        Assert.Equal(expectedFlag, result[0].HasUnrepresentableConcreteSameTypePin);
    }
}
