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

    // --- Layout/marker keyword constraints (AnyObject, Sendable, ...) ---
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
    // `_SBW_CI_`/GSF constructor wrapper and emit non-compiling Swift (the `init(name:) where
    // RowDecoder == ()` regression). The flag preserves that confinement.

    [Fact]
    public void ParseGenericSignature_DropsConcreteSameTypePin_FlagsParameter()
    {
        // Mirrors init(name:) where RowDecoder == (): the constraint is dropped
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
    // The PARAMETERIZED constructed-generic `== Foo.Bar<τ_0_1>` case below stays unflagged because
    // those real constructors reach a working method-generic path; the bare param==param form does not.
    [InlineData("τ_0_0 == τ_0_1", true)]
    // A constructed-generic target that mentions NO generic parameter names ONE concrete type, so it
    // confines the member exactly like `== ()` does — the angle brackets make it unrepresentable as a
    // nominal conformance, but they do not make it a family.
    [InlineData("τ_0_0 == Foo.Bar<Foo.Baz>", true)]
    [InlineData("τ_0_0.Element == Foo.Bar<Foo.Baz>", true)]   // pin via an associated-type member clause
    // Representable / non-pin constraints → never flagged.
    [InlineData("τ_0_0 : Swift.Equatable", false)]      // protocol constraint, not a pin
    [InlineData("τ_0_0 == Swift.Int", false)]           // module-qualified concrete survives as a real constraint
    [InlineData("τ_0_0 : Foo.Bar<Foo.Baz>", false)]     // conformance to a constructed generic is not a same-type pin
    [InlineData("τ_0_0 == Foo.Bar<τ_0_1>", false)]      // constructed-generic family relationship, not a single-specialization pin
    public void ParseGenericSignature_ConcreteSameTypePinFlag_OnlySetForDroppedConcretePin(string constraint, bool expectedFlag)
    {
        // τ_0_1 is declared so the constructed-generic case has a referent; the simpler cases ignore it.
        var sig = $"<τ_0_0, τ_0_1 where {constraint}>";

        var result = GenericSignatureParser.ParseGenericSignature(sig, sig);

        Assert.Equal(expectedFlag, result[0].HasUnrepresentableConcreteSameTypePin);
    }

    [Fact]
    public void ParseGenericSignature_SugaredConstructedGenericFamily_IsNotFlagged()
    {
        // Signatures reach the parser in BOTH spellings — desugared (`τ_0_0`) and sugared
        // (`Self`, `Value`) — so "does the target mention a generic parameter?" cannot be answered
        // by looking for `τ_`. Here `Self == Mod.Container<Value, Swift.Never>` relates two
        // DECLARED parameters: it is a family whose open form compiles, and flagging it would
        // withdraw working constructors.
        var sig = "<Self, Value where Self == Mod.Container<Value, Swift.Never>, Value : Mod.Marker>";

        var result = GenericSignatureParser.ParseGenericSignature(sig, sig);

        Assert.False(result[0].HasUnrepresentableConcreteSameTypePin);   // Self — family, not a pin
        Assert.False(result[1].HasUnrepresentableConcreteSameTypePin);   // Value
    }

    [Fact]
    public void ParseGenericSignature_ConcretePinWhoseTargetSharesAParameterPrefix_IsStillFlagged()
    {
        // The declared parameter `Unit` is a PREFIX of the target's `UnitDuration`. Matching the
        // target's identifiers by substring would read this concrete pin as a family and let the
        // confined member reach the open-erasure path — the exact failure the flag exists to stop.
        var sig = "<Unit, Value where Value.ValueType == Foundation.Measurement<Foundation.UnitDuration>>";

        var result = GenericSignatureParser.ParseGenericSignature(sig, sig);

        var value = Assert.Single(result, p => p.TypeName == "Value");
        Assert.True(value.HasUnrepresentableConcreteSameTypePin);
    }

    [Fact]
    public void ParseGenericSignature_ConcretePinWhoseTargetQualifierMatchesAParameterName_IsStillFlagged()
    {
        // Harder than the prefix case: the declared parameter `Measurement` matches a segment of the
        // target EXACTLY — but that segment is dot-qualified (`Foundation.Measurement`), so it names
        // a nominal type, not the parameter. A generic parameter is always referenced unqualified,
        // so a qualified segment can never be one; reading this target as a family would drop the
        // confinement and let the member reach the open-erasure path.
        var sig = "<Measurement, Value where Value.ValueType == Foundation.Measurement<Foundation.UnitDuration>>";

        var result = GenericSignatureParser.ParseGenericSignature(sig, sig);

        var value = Assert.Single(result, p => p.TypeName == "Value");
        Assert.True(value.HasUnrepresentableConcreteSameTypePin);
    }

    [Fact]
    public void ParseGenericSignature_UnqualifiedParameterInsideConstructedGenericTarget_IsStillAFamily()
    {
        // The companion to the test above: skipping DOT-QUALIFIED segments must not stop an
        // unqualified parameter reference from being seen. `Container<Value, Swift.Never>` relates a
        // declared parameter, so it stays a family and its open form keeps compiling.
        var sig = "<Self, Value where Self == Mod.Container<Value, Swift.Never>>";

        var result = GenericSignatureParser.ParseGenericSignature(sig, sig);

        Assert.All(result, p => Assert.False(p.HasUnrepresentableConcreteSameTypePin));
    }

    [Fact]
    public void ParseGenericSignature_RecordsEachDroppedPinSeparately()
    {
        // The confinement is carried as one entry PER CLAUSE, not as a per-parameter flag, so a
        // consumer can subtract the pins a parent type declares from the ones an initializer carries.
        // Two pins rooted at the same parameter must therefore stay distinguishable.
        var sig = "<τ_0_0 where τ_0_0.ValueType == Unit.Measure<Unit.Duration>, τ_0_0.KeyType == Unit.Measure<Unit.Count>>";

        var result = GenericSignatureParser.ParseGenericSignature(sig, sig);

        var decl = Assert.Single(result);
        Assert.Equal(
            new[] { "τ_0_0.ValueType==Unit.Measure<Unit.Duration>", "τ_0_0.KeyType==Unit.Measure<Unit.Count>" },
            decl.UnrepresentableConcreteSameTypePins);
    }

    [Fact]
    public void ParseGenericSignature_DropsConcreteConstructedGenericPin_FlagsRootParameter()
    {
        // `extension Holder where Value.ValueType == Unit.Measure<Unit.Duration> { init(key:) }` —
        // the target is unrepresentable (angle brackets) so the clause is dropped, but it names a
        // single concrete type, so the confinement must still reach the open-erasure gate. Dropping
        // it silently lets an unconditional `extension Holder: _SBW_CI_… {}` be emitted against the
        // unconstrained type, which swiftc rejects ("does not conform to protocol"), and the whole
        // wrapper compile fails. Shape observed on a system framework whose generic property type
        // carries ~66 such per-unit constrained extensions.
        var genericSig = "<τ_0_0 where τ_0_0 : Other.Valued, τ_0_0.ValueType == Unit.Measure<Unit.Duration>>";
        var sugaredSig = "<Value where Value : Other.Valued, Value.ValueType == Unit.Measure<Unit.Duration>>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        var decl = Assert.Single(result);
        Assert.Equal("τ_0_0", decl.TypeName);
        // The representable sibling constraint survives; only the constructed-generic pin is dropped.
        var conformance = Assert.Single(decl.GenericConformances);
        Assert.Equal("Other.Valued", conformance.ConformanceTarget.ModuleQualifiedName);
        Assert.Empty(decl.AssosiatedTypeConformances);
        Assert.True(decl.HasUnrepresentableConcreteSameTypePin);  // confinement preserved
    }

    // --- Dropped module-qualified marker conformances (`where U : Swift.Sendable`) are flagged ---
    // Dropping the unrepresentable marker (B3) also erases the only conformance the parameter carried,
    // so the enum-demotion gate (ModuleProcessor.HasProtocolConstraintAtPosition, which keys off "param
    // has any conformance") stops firing and a simple enum used at that position is no longer demoted to
    // a class. A raw-value enum that conforms to a protocol but is used at such a position then regresses
    // from a class to a bare C# enum, which cannot implement that protocol's interface. The flag
    // preserves the "position is constrained" signal so demotion still fires.

    [Fact]
    public void ParseGenericSignature_DropsModuleQualifiedMarker_FlagsParameter()
    {
        // A two-parameter generic `Outer<T, U> where U : Swift.Sendable`: the marker is dropped (no
        // representable nominal conformance) but the parameter is flagged as marker-constrained.
        var genericSig = "<τ_0_0, τ_0_1 where τ_0_1 : Swift.Sendable>";
        var sugaredSig = "<T, U where U : Swift.Sendable>";

        var result = GenericSignatureParser.ParseGenericSignature(genericSig, sugaredSig);

        Assert.Equal(2, result.Count);
        Assert.Empty(result[1].GenericConformances);                 // unrepresentable marker dropped
        Assert.True(result[1].HasDroppedNominalMarkerConstraint);    // demotion signal preserved
        Assert.False(result[0].HasDroppedNominalMarkerConstraint);   // unconstrained param unaffected
    }

    [Theory]
    // Module-qualified protocol-kind marker dropped as a `:` conformance → flagged so the enum-demotion
    // gate still treats the position as constrained.
    [InlineData("τ_0_0 : Swift.Sendable", true)]
    [InlineData("τ_0_0 : Swift.Copyable", true)]
    [InlineData("τ_0_0 : Swift.SendableMetatype", true)]
    [InlineData("τ_0_0 : Swift.BitwiseCopyable", true)]
    // Bare (non-module-qualified) marker keyword → not flagged: no nominal conformance was ever
    // representable, and an unqualified position is genuinely constraint-free for demotion purposes.
    [InlineData("τ_0_0 : Sendable", false)]
    [InlineData("τ_0_0 : AnyObject", false)]
    // Same-type pin to a marker keyword → the concrete-pin path, not the nominal-marker path.
    [InlineData("τ_0_0 == Any", false)]
    // A real representable conformance survives as a constraint and is not a "dropped marker".
    [InlineData("τ_0_0 : Swift.Equatable", false)]
    // A user protocol from a NON-stdlib module that merely shares a marker name is a real nominal
    // conformance, NOT a dropped marker: it survives below, so the "dropped marker" flag stays false.
    [InlineData("τ_0_0 : SomeModule.Sendable", false)]
    [InlineData("τ_0_0 : SomeModule.Copyable", false)]
    public void ParseGenericSignature_DroppedNominalMarkerFlag_OnlySetForModuleQualifiedMarkerConformance(string constraint, bool expectedFlag)
    {
        // τ_0_1 is declared so the bare param==param case has a referent; the simpler cases ignore it.
        var sig = $"<τ_0_0, τ_0_1 where {constraint}>";

        var result = GenericSignatureParser.ParseGenericSignature(sig, sig);

        Assert.Equal(expectedFlag, result[0].HasDroppedNominalMarkerConstraint);
    }

    [Theory]
    // A protocol from a non-stdlib module that happens to be named after a Swift marker keyword is a
    // real protocol carrying a witness table. The marker-drop is module-qualified (mirroring
    // IsStdlibMarkerProtocol everywhere else), so the conformance is KEPT as a normal nominal
    // constraint rather than silently dropped — and the parameter is NOT flagged as a dropped marker
    // because nothing was dropped (the surviving GenericConformances already feed the demotion gate).
    [InlineData("SomeModule.Sendable")]
    [InlineData("SomeModule.Copyable")]
    [InlineData("App.Escapable")]
    public void ParseGenericSignature_UserProtocolNamedAfterMarker_KeptAsRealConformance(string conformanceTarget)
    {
        var sig = $"<τ_0_0 where τ_0_0 : {conformanceTarget}>";

        var result = GenericSignatureParser.ParseGenericSignature(sig, sig);

        var conformance = Assert.IsType<GenericParameterConformance>(Assert.Single(result[0].GenericConformances));
        Assert.Equal(ConformanceKind.Protocol, conformance.Kind);
        Assert.Equal(conformanceTarget, conformance.ConformanceTarget.ModuleQualifiedName);
        // Nothing was dropped, so the "dropped marker" compensation flag must stay false.
        Assert.False(result[0].HasDroppedNominalMarkerConstraint);
    }

    [Fact]
    public void ConformanceTargetsRootedAt_IncludesMemberClauses_UnlikeDirectConformanceTargets()
    {
        // A direct conformance AND an associated-type member conformance, both rooted at τ_0_0.
        var sig = "<τ_0_0 where τ_0_0 : SomeModule.HasItem, τ_0_0.Item : Swift.BitwiseCopyable>";
        var model = GenericSignatureParser.ParseSignature(sig);

        // DirectConformanceTargets is direct-only: the member clause (τ_0_0.Item) is excluded.
        Assert.Equal(
            new[] { "SomeModule.HasItem" },
            model.DirectConformanceTargets("τ_0_0").ToArray());

        // ConformanceTargetsRootedAt is member-inclusive: BOTH the direct and the member clause appear.
        Assert.Equal(
            new[] { "SomeModule.HasItem", "Swift.BitwiseCopyable" },
            model.ConformanceTargetsRootedAt("τ_0_0").ToArray());
    }

    [Fact]
    public void ConformanceTargetsRootedAt_MatchesByRoot_ExcludesOtherParams()
    {
        // A member-clause marker rooted at a DIFFERENT param (τ_1_0) must not be yielded for τ_0_0.
        var sig = "<τ_0_0, τ_1_0 where τ_1_0 : SomeModule.HasItem, τ_1_0.Item : Swift.BitwiseCopyable>";
        var model = GenericSignatureParser.ParseSignature(sig);

        Assert.Empty(model.ConformanceTargetsRootedAt("τ_0_0"));
        Assert.Equal(
            new[] { "SomeModule.HasItem", "Swift.BitwiseCopyable" },
            model.ConformanceTargetsRootedAt("τ_1_0").ToArray());
    }
}
