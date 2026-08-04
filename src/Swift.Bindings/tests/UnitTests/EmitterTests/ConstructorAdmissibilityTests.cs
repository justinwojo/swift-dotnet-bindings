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
    public void HasUnsatisfiableConstraint_True_ForDroppedConcreteSameTypePin()
    {
        // `final class TableAlias<RowDecoder> { init(name:) where RowDecoder == () }` — the `== ()`
        // pin is dropped by GenericSignatureParser (unrepresentable target) but flagged on the
        // parameter. The init is confined to TableAlias<Void>; an open `_SBW_CI_`/GSF wrapper
        // against the unconstrained type would not compile, so the gate must refuse it.
        var parent = GenericClass("TableAlias", GenericParam("RowDecoder")); // unconstrained
        var method = Ctor(ReturnArg(), Param("name", "Swift.Optional<Swift.String>"));
        method.GenericParameters.Add(GenericParam("RowDecoder", concretePin: true));

        Assert.True(ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    [Fact]
    public void HasUnsatisfiableConstraint_False_ForConcretePinOnMethodOwnParam()
    {
        // A concrete pin on a METHOD-own generic param (τ_1, not the parent's) is the method-generic
        // dimension, not a parent-type-erasure concern — it must not trip the gate.
        var parent = GenericClass("Box", GenericParam("Value"));
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int"));
        method.GenericParameters.Add(GenericParam("U", concretePin: true));

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

    // ── Unerasable parent marker (BitwiseCopyable) ─────────────────────────────────
    // BitwiseCopyable is dropped from GenericConformances, so it is invisible to the
    // GenericParameter-list walk above; these cases drive the predicate off the lossless
    // RawGenericSig instead. The parent param's raw token (τ_0_0) must match the requirement
    // root, mirroring production (TypeName = τ_0_0, SugaredTypeName = "Value").

    [Theory]
    [InlineData("Swift.BitwiseCopyable")]
    [InlineData("BitwiseCopyable")]
    public void HasUnsatisfiableConstraint_True_ForBitwiseCopyableParentParam(string marker)
    {
        // `extension Box where Value: BitwiseCopyable { init(bitwiseCount:) }` — the unconditional
        // open GSF body `Self(bitwiseCount:)` requires Value: BitwiseCopyable and fails swiftc, and
        // the marker cannot be a conditional-conformance requirement. No legal open form exists, so
        // the gate must refuse it.
        var parent = GenericClass("Box", GenericParam("τ_0_0"));
        var method = Ctor(ReturnArg(), Param("bitwiseCount", "Swift.Int"));
        method.RawGenericSig = $"<τ_0_0 where τ_0_0 : {marker}>";

        Assert.True(ConstructorAdmissibility.HasUnerasableParentMarkerConstraint(method, parent));
        Assert.True(ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    [Theory]
    [InlineData("Swift.Sendable")]
    [InlineData("Swift.Copyable")]
    [InlineData("Swift.Escapable")]
    [InlineData("Swift.SendableMetatype")]
    public void HasUnerasableMarker_False_ForErasureSafeMarkers(string marker)
    {
        // The other stdlib markers ARE erasure-safe: the unconditional GSF body type-checks against
        // them (implicit defaults / advisory). They are handled by the where-clause drop, not by a
        // ctor refusal — so this predicate must leave them admissible.
        var parent = GenericClass("Box", GenericParam("τ_0_0"));
        var method = Ctor(ReturnArg(), Param("markerCount", "Swift.Int"));
        method.RawGenericSig = $"<τ_0_0 where τ_0_0 : {marker}>";

        Assert.False(ConstructorAdmissibility.HasUnerasableParentMarkerConstraint(method, parent));
        Assert.False(ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    [Fact]
    public void HasUnerasableMarker_False_ForBitwiseCopyableOnMethodOwnParam()
    {
        // A BitwiseCopyable constraint on a METHOD-own param (τ_1_0) is the method-generic
        // dimension, satisfied by closing over the conformer — not a parent-type-erasure concern.
        var parent = GenericClass("Box", GenericParam("τ_0_0"));
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int"));
        method.RawGenericSig = "<τ_0_0, τ_1_0 where τ_1_0 : Swift.BitwiseCopyable>";

        Assert.False(ConstructorAdmissibility.HasUnerasableParentMarkerConstraint(method, parent));
    }

    [Theory]
    [InlineData("Swift.BitwiseCopyable")]
    [InlineData("BitwiseCopyable")]
    public void HasUnsatisfiableConstraint_True_ForBitwiseCopyableParentMemberClause(string marker)
    {
        // `extension Box where Value.Item: BitwiseCopyable { init(bitwiseItemCount:) }` — the
        // associated-type MEMBER clause (τ_0_0.Item) is rooted at the parent param. The unconditional
        // open GSF body errors "requires that 'Value.Item' conform to 'BitwiseCopyable'" exactly like
        // the direct form, so the gate must refuse it too — even though it is NOT a direct constraint.
        var parent = GenericClass("Box", GenericParam("τ_0_0"));
        var method = Ctor(ReturnArg(), Param("bitwiseItemCount", "Swift.Int"));
        method.RawGenericSig = $"<τ_0_0 where τ_0_0 : SomeModule.HasItem, τ_0_0.Item : {marker}>";

        Assert.True(ConstructorAdmissibility.HasUnerasableParentMarkerConstraint(method, parent));
        Assert.True(ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    [Fact]
    public void HasUnerasableMarker_False_ForBitwiseCopyableOnMethodOwnMemberClause()
    {
        // A member-clause BitwiseCopyable rooted at a METHOD-own param (τ_1_0.Item) is the
        // method-generic dimension, not a parent-type-erasure concern — must stay admissible.
        var parent = GenericClass("Box", GenericParam("τ_0_0"));
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int"));
        method.RawGenericSig = "<τ_0_0, τ_1_0 where τ_1_0 : SomeModule.HasItem, τ_1_0.Item : Swift.BitwiseCopyable>";

        Assert.False(ConstructorAdmissibility.HasUnerasableParentMarkerConstraint(method, parent));
    }

    [Fact]
    public void HasUnerasableMarker_False_ForNonGenericParent()
    {
        var parent = NonGenericClass("Plain");
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int"));
        method.RawGenericSig = "<τ_0_0 where τ_0_0 : Swift.BitwiseCopyable>";

        Assert.False(ConstructorAdmissibility.HasUnerasableParentMarkerConstraint(method, parent));
    }

    [Fact]
    public void HasUnerasableMarker_False_WhenNoRawGenericSig()
    {
        var parent = GenericClass("Box", GenericParam("τ_0_0"));
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int")); // RawGenericSig stays null

        Assert.False(ConstructorAdmissibility.HasUnerasableParentMarkerConstraint(method, parent));
    }

    // ── Parser → gate seam ─────────────────────────────────────────────────────────
    // The tests above hand-build GenericParameters, so they pass even when the PARSER drops the
    // confinement on the floor and the gate is never given anything to refuse. These two drive the
    // real signature text through GenericSignatureParser first, which is where the constructed-generic
    // pin was previously lost without a trace.

    [Theory]
    // Pinned to ONE concrete type: an open erasure against the unconstrained parent cannot compile.
    [InlineData("Unit.Measure<Unit.Duration>", true)]
    // Related to a sibling PARAMETER: a family, and the open form compiles — must stay admissible.
    [InlineData("Unit.Measure<τ_0_1>", false)]
    public void HasUnsatisfiableConstraint_ForParsedConstructedGenericMemberPin(string target, bool expected)
    {
        // `extension Holder where Value.ValueType == <target> { init(key:) }`, with the parent
        // declaring only `Value : Other.Valued` — so the `where` clause is extension-origin.
        var sig = $"<τ_0_0, τ_0_1 where τ_0_0 : Other.Valued, τ_0_0.ValueType == {target}>";

        var parent = GenericClass("Holder",
            GenericParam("τ_0_0", generic: Conformance(new[] { "τ_0_0" }, "Other.Valued")),
            GenericParam("τ_0_1"));
        var method = Ctor(ReturnArg(), Param("key", "Swift.String"));
        method.GenericParameters = GenericSignatureParser.ParseGenericSignature(sig, sig);

        Assert.Equal(
            expected,
            ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
    }

    // ── Dropped-pin helpers: strict (CSM) vs extension-added (open erasure) ────────

    [Fact]
    public void HasUnrepresentableConcreteParentPin_True_ForParentLevelPinnedParam()
    {
        // `final class TableAlias<RowDecoder> { init(name:) where RowDecoder == () }` — the dropped
        // `== ()` pin is recorded on the parent-level param. This is CSM's gate: a CSM closed form
        // closing over a different parameter would leave RowDecoder generic, and `()` is never a
        // conformer CSM enumerates.
        var parent = GenericClass("TableAlias", GenericParam("RowDecoder"));
        var method = Ctor(ReturnArg(), Param("name", "Swift.String"));
        method.GenericParameters.Add(GenericParam("RowDecoder", concretePin: true));

        Assert.True(ConstructorAdmissibility.HasUnrepresentableConcreteParentPin(method, parent));
    }

    [Fact]
    public void HasUnrepresentableConcreteParentPin_False_ForMethodOwnPinnedParam()
    {
        // A pin on a METHOD-own param (not a parent generic param) is the method-generic dimension,
        // which CSM/GSF satisfy by closing over it — not an unsatisfiable parent confinement.
        var parent = GenericClass("Box", GenericParam("Value"));
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int"));
        method.GenericParameters.Add(GenericParam("U", concretePin: true));

        Assert.False(ConstructorAdmissibility.HasUnrepresentableConcreteParentPin(method, parent));
    }

    [Fact]
    public void HasUnrepresentableConcreteParentPin_False_WhenNoPin()
    {
        var parent = GenericClass("Box", GenericParam("Value"));
        var method = Ctor(ReturnArg(), Param("x", "Swift.Int"));
        method.GenericParameters.Add(GenericParam("Value")); // no pin

        Assert.False(ConstructorAdmissibility.HasUnrepresentableConcreteParentPin(method, parent));
    }

    [Fact]
    public void ParentDeclaredPin_IsSubtractedByTheOpenGateButStillRefusedByCsm()
    {
        // `final class Holder<τ_0_0: Other.Valued> where τ_0_0.ValueType == Unit.Measure<Unit.Duration>`
        // with a plain in-body `init(key:)`. The pin is the PARENT's own, so it lands on every init,
        // including ones no extension constrained. That shape is legal Swift and its OPEN erased form
        // compiles — the extension carries the type's own requirements and the type is never usable
        // unpinned — so the open gate must not read the inherited pin as an extension-added
        // confinement and suppress the constructor.
        //
        // CSM must still refuse it: CSM does not subtract parent-declared constraints, it evaluates
        // them per candidate conformer, and a dropped pin is invisible to that evaluation. The two
        // gates therefore disagree on this input BY DESIGN — asserted together so a future
        // "simplification" that re-merges them fails here.
        //
        // Both signatures go through the real parser: the confinement is carried on a side-channel,
        // and a test that hand-built the flags could not observe whether parent and init agree.
        const string sig = "<τ_0_0 where τ_0_0 : Other.Valued, τ_0_0.ValueType == Unit.Measure<Unit.Duration>>";
        var parent = GenericClass("Holder");
        parent.GenericParameters = GenericSignatureParser.ParseGenericSignature(sig, sig);
        var method = Ctor(ReturnArg(), Param("key", "Swift.String"));
        method.GenericParameters = GenericSignatureParser.ParseGenericSignature(sig, sig);

        Assert.False(
            ConstructorAdmissibility.HasExtensionAddedUnrepresentableConcretePin(method, parent));
        Assert.False(
            ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint(method, parent));
        Assert.True(ConstructorAdmissibility.HasUnrepresentableConcreteParentPin(method, parent));
    }

    [Fact]
    public void HasExtensionAddedUnrepresentableConcretePin_True_WhenAnExtensionAddsASecondPinOnAPinnedParam()
    {
        // Same parent as above, but the init comes from `extension Holder where τ_0_0.KeyType ==
        // Unit.Measure<Unit.Count>` — a SECOND dropped pin rooted at the SAME parameter the parent
        // already pins. Subtracting per parameter would cancel it and admit an init whose open erased
        // form cannot compile; subtracting per clause keeps it refused. CSM refuses it either way.
        const string parentSig = "<τ_0_0 where τ_0_0 : Other.Valued, τ_0_0.ValueType == Unit.Measure<Unit.Duration>>";
        const string initSig = "<τ_0_0 where τ_0_0 : Other.Valued, τ_0_0.ValueType == Unit.Measure<Unit.Duration>, τ_0_0.KeyType == Unit.Measure<Unit.Count>>";
        var parent = GenericClass("Holder");
        parent.GenericParameters = GenericSignatureParser.ParseGenericSignature(parentSig, parentSig);
        var method = Ctor(ReturnArg(), Param("key", "Swift.String"));
        method.GenericParameters = GenericSignatureParser.ParseGenericSignature(initSig, initSig);

        Assert.True(
            ConstructorAdmissibility.HasExtensionAddedUnrepresentableConcretePin(method, parent));
        Assert.True(ConstructorAdmissibility.HasUnrepresentableConcreteParentPin(method, parent));
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
        IsSynthesizedAccessor = false
    };

    private static GenericParameterConformance SameType(string[] path, string target) =>
        new(path, SwiftTypeName.FromModuleQualifiedName(target), ConformanceKind.ConcreteType);

    private static GenericParameterConformance Conformance(string[] path, string target) =>
        new(path, SwiftTypeName.FromModuleQualifiedName(target), ConformanceKind.Protocol);

    private static GenericArgumentDecl GenericParam(
        string name,
        GenericParameterConformance? generic = null,
        GenericParameterConformance? assoc = null,
        bool concretePin = false,
        string[]? concretePins = null) =>
        new(name, name,
            generic is null ? new List<GenericParameterConformance>() : new List<GenericParameterConformance> { generic },
            assoc is null ? new List<GenericParameterConformance>() : new List<GenericParameterConformance> { assoc },
            concretePins ?? (concretePin ? new[] { $"{name}==<dropped>" } : null));

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
