// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the method-level-generic opening route: which member shapes
/// <see cref="MethodLevelGenericOpening.TryBuildPlan"/> admits onto the free <c>@_cdecl</c>
/// wrapper, what existential the admitted plan casts the type-argument metadata to, and the
/// agreement between the wrapper's Swift parameter list and the C# P/Invoke's.
///
/// <para>
/// The admit rules are a whitelist: every decline leaves the member on the direct
/// <c>CallConvSwift</c> route it already has, so a wrong decline costs coverage while a wrong
/// admit costs soundness. The declines below are therefore asserted individually per constraint
/// form rather than as one "unsupported" bucket.
/// </para>
/// </summary>
public class MethodLevelGenericOpeningTests
{
    #region Admitted constraint forms

    [Fact]
    public void TryBuildPlan_SingleProtocolConstraint_OpensToConstrainedExistential()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var only = Assert.Single(opened);
        Assert.Equal("τ_0_0", only.SwiftName);
        Assert.Equal(0, only.Ordinal);
        Assert.Equal(new[] { "TestModule.Describable" }, only.ConstraintTargets);
        Assert.Equal("any TestModule.Describable.Type", only.ExistentialMetatype);
        Assert.Equal(": TestModule.Describable", only.ConstraintClause);
    }

    [Fact]
    public void TryBuildPlan_Unconstrained_OpensWithNoConstraintClause()
    {
        // No requirement at all — the wrapper reaches the generic context through
        // _openExistential rather than a cast, so there is nothing to refuse.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var only = Assert.Single(opened);
        Assert.Empty(only.ConstraintTargets);
        Assert.Equal("", only.ConstraintClause);
    }

    [Fact]
    public void TryBuildPlan_ProtocolComposition_OpensToParenthesizedExistential()
    {
        // Two conformances on one parameter compose. The targets sort ordinally so the emitted
        // existential is stable across runs regardless of the order the parser reports them in.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Identifiable, τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var only = Assert.Single(opened);
        Assert.Equal(new[] { "TestModule.Describable", "TestModule.Identifiable" }, only.ConstraintTargets);
        Assert.Equal("any (TestModule.Describable & TestModule.Identifiable).Type", only.ExistentialMetatype);
        Assert.Equal(": TestModule.Describable & TestModule.Identifiable", only.ConstraintClause);
    }

    [Fact]
    public void TryBuildPlan_StandardLibraryProtocolTarget_Opens()
    {
        // Swift standard-library protocols carry no module TypeRecord but are always in scope
        // for the wrapper, so they must not be mistaken for an unnameable target.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Swift.Equatable>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        Assert.Equal(new[] { "Swift.Equatable" }, Assert.Single(opened).ConstraintTargets);
    }

    [Fact]
    public void TryBuildPlan_ThreeOwnGenerics_OpensAllThree()
    {
        // One metadata pointer and one nesting level per opened parameter; three is the widest
        // shape the corpus has, and each carries its own ordinal for the refusal encoding.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0, τ_0_1, τ_0_2 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0", "τ_0_1", "τ_0_2" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        Assert.Equal(3, opened.Count);
        Assert.Equal(new[] { 0, 1, 2 }, opened.Select(o => o.Ordinal));
        Assert.Equal(new[] { "TestModule.Describable" }, opened[0].ConstraintTargets);
        Assert.Empty(opened[1].ConstraintTargets);
    }

    #endregion

    #region Declined constraint forms

    [Theory]
    [InlineData("Swift.Sendable")]
    [InlineData("Swift.SendableMetatype")]
    [InlineData("Swift.Copyable")]
    [InlineData("Swift.Escapable")]
    [InlineData("Swift.BitwiseCopyable")]
    [InlineData("Swift.AnyObject")]
    public void TryBuildPlan_MarkerConstraint_Declines(string constraint)
    {
        // A marker protocol leaves no runtime conformance record, so `any Sendable.Type` is
        // rejected by swiftc outright. Declining is the only safe answer: a wrapper swiftc rejects
        // is stripped from the dylib and leaves the emitted P/Invoke pointing at nothing, so a
        // marker missed here surfaces as a run-time missing entry point, not a build failure.
        var env = CreateGenericMethodEnv(
            rawGenericSig: $"<τ_0_0 where τ_0_0 : {constraint}>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_ParameterizedCollectionString_UsesExactExistential()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Swift.Collection<Swift.String>>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var only = Assert.Single(opened);
        Assert.Equal(MlgOpeningStrategy.ParameterizedExistential, only.Strategy);
        Assert.Equal(new[] { "Swift.Collection<Swift.String>" }, only.ConstraintTargets);
        Assert.Equal("any Swift.Collection<Swift.String>.Type", only.ExistentialMetatype);
    }

    [Fact]
    public void TryBuildPlan_ImportedHintedProtocol_UsesRegistryIdentityProof()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Foundation.DataProtocol>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var plan));
        var only = Assert.Single(plan);
        Assert.Equal(new[] { "Foundation.DataProtocol" }, only.ConstraintTargets);
        Assert.Equal("any Foundation.DataProtocol.Type", only.ExistentialMetatype);
    }

    [Fact]
    public void TryBuildPlan_CollectionElementSameType_NormalizesToParameterizedExistential()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Swift.Collection, τ_0_0.Element == Swift.String>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var only = Assert.Single(opened);
        Assert.Equal(MlgOpeningStrategy.ParameterizedExistential, only.Strategy);
        Assert.Equal(new[] { "Swift.Collection<Swift.String>" }, only.ConstraintTargets);
        Assert.Equal(
            new[]
            {
                "τ_0_0 : Swift.Collection",
                "τ_0_0.Element == Swift.String",
            },
            only.DiagnosticRequirements);
    }

    [Fact]
    public void TryBuildPlan_RedundantParameterizedCollectionSpelling_ConsumesBothAndPreservesComposition()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Swift.Collection<Swift.String>, τ_0_0.Element == Swift.String, τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var only = Assert.Single(opened);
        Assert.Equal(MlgOpeningStrategy.ParameterizedExistential, only.Strategy);
        Assert.Equal(
            new[] { "Swift.Collection<Swift.String>", "TestModule.Describable" },
            only.ConstraintTargets);
        Assert.Equal(3, only.DiagnosticRequirements.Count);
    }

    [Fact]
    public void TryBuildPlan_ClassBoundConstraint_UsesSuperclassCarrier()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.BaseWidget>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var only = Assert.Single(opened);
        Assert.Equal(MlgOpeningStrategy.SuperclassCarrier, only.Strategy);
        Assert.Equal("TestModule.BaseWidget", only.SuperclassTarget);
    }

    [Fact]
    public void TryBuildPlan_SameTypeRequirement_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 == Swift.Int>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_ElementProtocolClause_UsesConditionalCarrier()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable, τ_0_0.Element : TestModule.Identifiable>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var only = Assert.Single(opened);
        Assert.Equal(MlgOpeningStrategy.AssociatedTypeCarrier, only.Strategy);
        Assert.Equal("Element", only.ConditionalRequirement?.MemberPath);
        Assert.Equal("TestModule.Identifiable", only.ConditionalRequirement?.Target);
    }

    [Fact]
    public void TryBuildPlan_ElementSuperclassClause_UsesConditionalCarrier()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Swift.Sequence, τ_0_0.Element : TestModule.BaseWidget>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        Assert.Equal(MlgOpeningStrategy.AssociatedTypeCarrier, Assert.Single(opened).Strategy);
    }

    [Theory]
    [InlineData("<τ_0_0 where τ_0_0 : Swift.Collection<τ_0_1>>")]
    [InlineData("<τ_0_0 where τ_0_0 : Swift.Collection, τ_0_0.Element == Swift.Int>")]
    [InlineData("<τ_0_0 where τ_0_0 : Swift.Sequence, τ_0_0.Element.Value : TestModule.Identifiable>")]
    [InlineData("<τ_0_0 where τ_0_0 : TestModule.BaseWidget, τ_0_0 : TestModule.Describable>")]
    public void TryBuildPlan_UnprovedExpandedConstraint_Declines(string rawGenericSig)
    {
        var env = CreateGenericMethodEnv(rawGenericSig, new[] { "τ_0_0" });

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_UnknownNonSwiftTarget_Declines()
    {
        // A target with no TypeRecord and no `Swift` module can't be named in the wrapper.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : OtherModule.Mystery>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_FourOwnGenerics_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0, τ_0_1, τ_0_2, τ_0_3>",
            genericParamNames: new[] { "τ_0_0", "τ_0_1", "τ_0_2", "τ_0_3" });

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_BareOwnGenericReturn_OpensForIndirectResult()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            returnType: new NamedTypeSpec("τ_0_0"));

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        Assert.Equal(MlgOpeningStrategy.Existential, Assert.Single(opened).Strategy);
    }

    [Fact]
    public void TryBuildPlan_BareOwnGenericReturn_SuperclassCarrierDeclines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.BaseWidget>",
            genericParamNames: new[] { "τ_0_0" },
            returnType: new NamedTypeSpec("τ_0_0"));

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_BareOwnGenericReturn_AssociatedTypeCarrierDeclines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable, τ_0_0.Element : TestModule.Identifiable>",
            genericParamNames: new[] { "τ_0_0" },
            returnType: new NamedTypeSpec("τ_0_0"));

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_CompositeOwnGenericReturn_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            returnType: new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("τ_0_0")));

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryBuildPlan_DynamicSelfReturn_OpensAndNamesTheParentInstead(bool optional)
    {
        // The opened bodies are LOCAL functions inside a free @_cdecl, which has no enclosing type
        // for `Self` to resolve against, so the route admits the member only because the renderer
        // can write the parent's name in its place. Both admitted shapes — bare `Self` and
        // `Optional<Self>` — resolve to that name.
        TypeSpec selfSpec = new NamedTypeSpec("Self");
        if (optional)
            selfSpec = new NamedTypeSpec("Swift.Optional", selfSpec);

        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            returnType: selfSpec);

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var expected = optional ? "TestModule.MyType?" : "TestModule.MyType";
        Assert.Equal(expected, MethodLevelGenericWrapperEmitter.TryRenderDynamicSelfReturn(env.ParentDecl, selfSpec));

        // The renderer agreeing in isolation is not the defect this covers: the bug was the
        // emitted local function still saying `-> Self`. Assert on the Swift text so reverting
        // the renderer's use at the emission site fails here too.
        var sw = new StringWriter();
        MethodLevelGenericWrapperEmitter.Emit(
            new SwiftWriter(sw), env, new ModuleEmissionContext(), "SBW_TestModule_MyType_describe_TEST", opened);
        var swift = sw.ToString();

        Assert.Contains($"-> {expected}", swift);
        Assert.DoesNotContain("-> Self", swift);
    }

    [Fact]
    public void TryBuildPlan_DynamicSelfReturnTheRendererCannotSpell_Declines()
    {
        // A `Self` buried in a shape the renderer cannot rewrite to a name is declined here rather
        // than emitted and then withdrawn by the Swift compile — a decline leaves the member on the
        // direct route it already has, which works.
        TypeSpec nestedSelf = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Self"));

        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            returnType: nestedSelf);

        Assert.Null(MethodLevelGenericWrapperEmitter.TryRenderDynamicSelfReturn(env.ParentDecl, nestedSelf));
        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_InOutGenericParameter_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            paramIsInOut: true);

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_NonGenericInOutSibling_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m => m.CSSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = "counter",
                PrivateName = "counter",
                IsInOut = true,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = m.ModuleDecl,
            }));

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_CompositeGenericParameter_Declines()
    {
        // `Box<τ_0_0>` cannot bind a typed pointer from the opened type without a second layout
        // derivation, so only a whole by-value payload is admitted.
        var boxed = new NamedTypeSpec("TestModule.Box");
        boxed.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));

        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            paramType: boxed);

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_NonGenericMethod_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: null,
            genericParamNames: Array.Empty<string>(),
            paramType: new NamedTypeSpec("Swift.Int"));

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_AsyncMethod_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m => m.IsAsync = true);

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_Constructor_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m => m.IsConstructor = true);

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_Accessor_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m => m.IsAccessor = true);

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_VariadicParameter_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m => m.HasVariadicParameter = true);

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    #endregion

    #region Signature contract

    [Fact]
    public void DetermineParameterOrder_OpeningRoute_AppendsRefusalLast()
    {
        // OpenRefusal is purely additive over the shape every other regular method already has:
        // nothing before it shifts position, so a member that gains the route keeps every
        // existing argument at the same index.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m => { m.Throws = true; m.UsesMethodLevelGenericOpening = true; });

        var order = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: false);

        Assert.Equal(
            new[]
            {
                CdeclPhase.Arguments, CdeclPhase.Metadata, CdeclPhase.Self,
                CdeclPhase.ErrorOut, CdeclPhase.OpenRefusal,
            },
            order.Phases);
    }

    [Fact]
    public void OpeningWrapper_BareGenericReturn_WritesResultInsideOpenedBody()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            returnType: new NamedTypeSpec("τ_0_0"),
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
                m.UsesWrapperLibrary = true;
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_GENERIC_RETURN");

        var swift = EmitOpeningSwift(env);
        var pinvoke = EmitPInvokeText(env);
        var swiftParameters = ParseCdeclParameterList(swift);
        var pInvokeParameters = new SignatureHandler(env).GetPInvokeSignature().Parameters;

        Assert.Equal(
            new[]
            {
                "_ resultPtr: UnsafeMutableRawPointer",
                "_ itemPayload: UnsafeRawPointer",
                "_ _metadata0: UnsafeRawPointer",
                "_ self_: UnsafeMutableRawPointer",
                "_ _openRefused: UnsafeMutablePointer<UInt8>",
            },
            swiftParameters);
        Assert.Equal(
            new[]
            {
                ("IntPtr", "resultPtr", ""),
                ("IntPtr", "itemPayload", ""),
                ("IntPtr", "T0Metadata", ""),
                ("IntPtr", "_selfClass", ""),
                ("byte", "_openRefused", "out"),
            },
            pInvokeParameters.Select(p => (p.TypeString(), p.Name, p.modifier)));
        Assert.Equal(
            "func _mlgBody0<_MLG0: TestModule.Describable>(_: _MLG0.Type) {",
            swift.Split('\n').Single(line => line.Contains("func _mlgBody0<", StringComparison.Ordinal)).Trim());
        Assert.Contains("let result: _MLG0 = obj.describe(item: item)", swift);
        Assert.Contains("resultPtr.initializeMemory(as: _MLG0.self, repeating: result, count: 1)", swift);
        Assert.Contains("CallConvCdecl", pinvoke);
        Assert.DoesNotContain("SwiftSelf", pinvoke);
        Assert.DoesNotContain("τ_0_0", swift.Split('\n').Single(
            line => line.Contains("public func ", StringComparison.Ordinal)));
    }

    [Fact]
    public void OpeningWrapper_ReturnOnlyGenericInference_AnnotatesResultInsideOpenedBody()
    {
        // ObjectMapper.Map.value<T>(...) has no T-typed argument, so Swift can infer T only from
        // the assignment context. Keeping the type solely on initializeMemory's `as:` argument is
        // too late: the preceding generic invocation is independently type-checked and rejected.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0>",
            genericParamNames: new[] { "τ_0_0" },
            returnType: new NamedTypeSpec("τ_0_0"),
            paramType: new NamedTypeSpec("Swift.Int"),
            configure: m =>
            {
                m.CSSignature[1].IsGeneric = false;
                m.Throws = true;
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
                m.UsesWrapperLibrary = true;
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_RETURN_ONLY_GENERIC");

        var swift = EmitOpeningSwift(env);

        Assert.Contains("let result: _MLG0 = try obj.describe(item: item)", swift);
        Assert.DoesNotContain("let result = try obj.describe(item: item)", swift);
        Assert.Contains("resultPtr.initializeMemory(as: _MLG0.self, repeating: result, count: 1)", swift);
    }

    [Fact]
    public void DetermineParameterOrder_FlagWithoutPlan_OmitsRefusal()
    {
        // Specializing emitters clone a member with `method with { GenericParameters = [], … }`,
        // and a record clone inherits the promotion flag from its generic original. The clone has
        // nothing to open, so it must take the ordinary shape — giving it a refusal parameter its
        // wrapper never emits would shift every argument the P/Invoke passes by one slot.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m => m.UsesMethodLevelGenericOpening = true);

        var specialized = env.MethodDecl with
        {
            GenericParameters = new List<GenericArgumentDecl>(),
            RawGenericSig = null,
        };
        var specializedEnv = new MethodEnvironment(specialized, env.TypeDatabase);

        Assert.True(specialized.UsesMethodLevelGenericOpening);
        Assert.DoesNotContain(
            CdeclPhase.OpenRefusal,
            CdeclSignatureContract.DetermineParameterOrder(specializedEnv, overrideNeedsResultPtr: false).Phases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningWrapper_ParameterPhases_AgreeSlotForSlotWithThePInvoke(bool throws)
    {
        // The failure mode this pins is a shifted register, not a compile error: the Swift wrapper
        // and the C# P/Invoke walk the same CdeclSignatureContract phases through two different
        // emitters, so a phase one side honours and the other drops is silent until it corrupts an
        // argument at run time. Equal arity alone does not catch it — two phases swapped keep the
        // count — so the slots are compared by what each one CARRIES. The two sides spell the same
        // slot differently (`_metadata0` vs `T0Metadata`, `self_` vs `_selfClass`), which is why
        // the comparison is over categories rather than names.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
                m.UsesWrapperLibrary = true;
                m.Throws = throws;
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_TEST");

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));

        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        MethodLevelGenericWrapperEmitter.Emit(
            swiftWriter, env, new ModuleEmissionContext(), "SBW_TestModule_MyType_describe_TEST", opened);

        var swiftParams = ParseCdeclParameterList(sw.ToString());
        var pInvokeParams = new SignatureHandler(env).GetPInvokeSignature().Parameters;

        Assert.Equal(
            pInvokeParams.Select(p => ClassifySlot(p.Name)).ToList(),
            swiftParams.Select(p => ClassifySlot(ParameterBindingName(p))).ToList());
        Assert.Contains("openRefusal", swiftParams.Select(p => ClassifySlot(ParameterBindingName(p))));
        Assert.Equal(throws, pInvokeParams.Any(p => ClassifySlot(p.Name) == "errorOut"));

        // F3: phase agreement is necessary but not sufficient. The production route sets BOTH
        // flags, and its receiver must be an ordinary C pointer under the resolved C convention;
        // an untyped SwiftSelf in the same semantic slot would still pass the normalized comparison
        // above while targeting x20 instead of the C argument register.
        var receiver = Assert.Single(pInvokeParams, p => ClassifySlot(p.Name) == "self");
        Assert.Equal("IntPtr", receiver.TypeString());
        Assert.Equal(
            PInvokeCallingConvention.Cdecl,
            PInvokeEmitHelper.SelectCallingConvention(
                env.EmissionSymbol,
                WrapperValidation.GetCallingConvention(env.MethodDecl)));

        var pInvokeText = EmitPInvokeText(env);
        Assert.True(pInvokeText.Contains("CallConvCdecl", StringComparison.Ordinal), pInvokeText);
        Assert.Contains($"EntryPoint = \"{env.EmissionSymbol}\"", pInvokeText);
        Assert.Contains($"IntPtr {receiver.Name}", pInvokeText);
        Assert.DoesNotContain("SwiftSelf", pInvokeText);

        var actualCarriers = pInvokeParams
            .Select(p => (Phase: ClassifySlot(p.Name), Carrier: p.TypeString()))
            .ToList();
        var mismatchedCarriers = actualCarriers
            .Select(slot => slot.Phase == "self" ? (slot.Phase, Carrier: "SwiftSelf") : slot)
            .ToList();

        // Negative control: a phase-only oracle accepts this deliberately bad receiver, while the
        // carrier-aware oracle rejects it. This keeps the assertions above load-bearing.
        Assert.Equal(actualCarriers.Select(s => s.Phase), mismatchedCarriers.Select(s => s.Phase));
        Assert.NotEqual(actualCarriers, mismatchedCarriers);
    }

    [Fact]
    public void OpeningWrapper_MutatingStructReceiver_EmitsMutablePointerAndCdeclIntPtr()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            structParent: true,
            configure: m =>
            {
                m.IsMutating = true;
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
                m.UsesWrapperLibrary = true;
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_MUTATING_STRUCT");

        var swift = EmitOpeningSwift(env);
        var pInvoke = EmitPInvokeText(env);
        var receiver = Assert.Single(
            new SignatureHandler(env).GetPInvokeSignature().Parameters,
            p => ClassifySlot(p.Name) == "self");

        Assert.Contains("_ self_: UnsafeMutableRawPointer", swift);
        Assert.Equal("IntPtr", receiver.TypeString());
        Assert.True(pInvoke.Contains("CallConvCdecl", StringComparison.Ordinal), pInvoke);
        Assert.Contains($"IntPtr {receiver.Name}", pInvoke);
        Assert.DoesNotContain("SwiftSelf", pInvoke);
    }

    [Fact]
    public void OpeningWrapper_VoidParameter_ContributesNoSlotAndIsForwardedAtTheCall()
    {
        // A Void parameter carries no bytes and both sides skip the ABI slot. A slot only the
        // wrapper declared would shift every argument after it, and dropping the argument from the
        // inner call instead would not compile — Swift still requires it.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.CSSignature.Add(new ArgumentDecl
                {
                    SwiftTypeSpec = new TupleTypeSpec(),
                    Name = "extra",
                    PrivateName = "extra",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = m.ModuleDecl
                });
            });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));

        var sw = new StringWriter();
        MethodLevelGenericWrapperEmitter.Emit(
            new SwiftWriter(sw), env, new ModuleEmissionContext(), "SBW_TestModule_MyType_describe_TEST", opened);
        var swift = sw.ToString();

        Assert.Equal(
            new SignatureHandler(env).GetPInvokeSignature().Parameters.Count,
            ParseCdeclParameterList(swift).Count);
        Assert.Contains("extra: ()", swift);
    }

    [Fact]
    public void OpeningWrapper_DeclaresNoWitnessTableParameter()
    {
        // The `as? any P.Type` cast IS the conformance check and it runs before the payload is
        // dereferenced, so no witness table crosses the seam. Both sides suppress it; a PWT on
        // one side only is an argument-slot mismatch.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m => m.UsesMethodLevelGenericOpening = true);

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));

        var sw = new StringWriter();
        MethodLevelGenericWrapperEmitter.Emit(
            new SwiftWriter(sw), env, new ModuleEmissionContext(), "SBW_TestModule_MyType_describe_TEST", opened);

        Assert.DoesNotContain("_pwt", sw.ToString());
        Assert.DoesNotContain(
            new SignatureHandler(env).GetPInvokeSignature().Parameters,
            p => p.Name.Contains("Pwt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OpeningWrapper_ParameterizedCollectionString_CastsTheExactExistential()
    {
        // BindingTests compile wrappers at an iOS 15 deployment floor. Swiftc rejects an
        // unguarded parameterized-protocol cast there with: "runtime support for parameterized
        // protocol types is only available in iOS 16.0.0 or newer". The availability branch is
        // therefore required even though the generic declaration itself is legal at the floor.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Swift.Collection<Swift.String>>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_COLLECTION");

        var swift = EmitOpeningSwift(env);

        Assert.Contains("if #available(iOS 16.0, macOS 13.0, tvOS 16.0", swift);
        Assert.Contains("as? any Swift.Collection<Swift.String>.Type", swift);
        Assert.Contains("func _mlgBody0<_MLG0: Swift.Collection<Swift.String>>", swift);
        Assert.DoesNotContain("_pwt", swift);

        var wrapper = swift[swift.IndexOf("public func ", StringComparison.Ordinal)..];
        var availability = wrapper.IndexOf("if #available(iOS 16.0", StringComparison.Ordinal);
        var metadataRead = wrapper.IndexOf("unsafeBitCast(_metadata0", StringComparison.Ordinal);
        var existentialCast = wrapper.IndexOf("as? any Swift.Collection<Swift.String>.Type", StringComparison.Ordinal);
        var payloadRead = wrapper.IndexOf("assumingMemoryBound(to: _MLG0.self)", StringComparison.Ordinal);
        var oldRuntimeRefusal = wrapper.IndexOf("} else {", StringComparison.Ordinal);
        Assert.True(availability >= 0 && metadataRead > availability);
        Assert.True(existentialCast > metadataRead && payloadRead > existentialCast && oldRuntimeRefusal > payloadRead);

        var oldRuntimeBranch = wrapper[oldRuntimeRefusal..];
        Assert.Contains("_openRefused.pointee = 1", oldRuntimeBranch);
        Assert.DoesNotContain("unsafeBitCast", oldRuntimeBranch);
        Assert.DoesNotContain("assumingMemoryBound", oldRuntimeBranch);
        Assert.DoesNotContain("resultPtr.storeBytes", oldRuntimeBranch);
    }

    [Fact]
    public void OpeningWrapper_AssociatedTypeCarrier_ProvesBothConstraintsBeforeReadingPayloadOrSelf()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Swift.Sequence, τ_0_0.Element : TestModule.Identifiable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_ASSOCIATED");

        var swift = EmitOpeningSwift(env);

        Assert.Contains("private protocol _SBW_MLG_", swift);
        Assert.Contains("private enum _SBW_MLG_", swift);
        Assert.Contains("where _MLG0.Element: TestModule.Identifiable", swift);
        Assert.Contains("as? any Swift.Sequence.Type", swift);
        Assert.Contains("as? any _SBW_MLG_", swift);
        Assert.DoesNotContain("_pwt", swift);

        var wrapper = swift[swift.IndexOf("public func ", StringComparison.Ordinal)..];
        var rootProof = wrapper.IndexOf("as? any Swift.Sequence.Type", StringComparison.Ordinal);
        var conditionalProof = wrapper.IndexOf("as? any _SBW_MLG_", StringComparison.Ordinal);
        var carrierCall = wrapper.IndexOf("carrier._sbw_mlg_call_", StringComparison.Ordinal);
        Assert.True(rootProof >= 0 && conditionalProof > rootProof);
        Assert.True(carrierCall > conditionalProof);
        Assert.DoesNotContain("assumingMemoryBound", wrapper);
        Assert.Contains("assumingMemoryBound(to: _MLG0.self)", swift[..swift.IndexOf("public func ", StringComparison.Ordinal)]);
    }

    [Fact]
    public void OpeningWrapper_AssociatedTypeCarrier_PrivateDispatchCarriesAvailabilityAndMainActor()
    {
        // The public @_cdecl already carries these annotations, but the carrier extension is a
        // separate declaration that mentions and calls the real API. RealityFoundation exposed
        // this distinction: its iOS-18 @MainActor EntityCollection methods were withdrawn because
        // swiftc checked the unannotated private dispatch body at the iOS-15 deployment target.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Swift.Sequence, τ_0_0.Element : TestModule.BaseWidget>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
                m.IsMainActorIsolated = true;
                m.AvailabilityAnnotations = new List<AvailabilityAnnotation>
                {
                    new("iOS", "18.0", null, null, false, false, null, null),
                };
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_ASSOCIATED_AVAILABLE");

        var swift = EmitOpeningSwift(env);
        var extension = swift[swift.IndexOf("extension _SBW_MLG_", StringComparison.Ordinal)..];
        var hash = EmitterUtility.DeterministicHash8(env.EmissionSymbol);
        var availability = "@available(iOS 18.0, *)\n";
        var anchor = OriginAnchorEmitter.LineForWrapper(env.MethodDecl);

        Assert.Contains("@MainActor static func _sbw_mlg_call_", swift);
        Assert.Contains($"{availability}private enum _SBW_MLG_{hash}Outcome", swift);
        Assert.Contains($"{availability}private protocol _SBW_MLG_{hash}Carrier", swift);
        Assert.Contains($"{availability}private enum _SBW_MLG_{hash}Open", swift);
        Assert.Contains($"{availability}extension _SBW_MLG_{hash}Open", swift);
        Assert.Contains("@MainActor static func _sbw_mlg_call_", extension);
        Assert.Contains("@available(iOS 18.0, *)\n@MainActor\n@_cdecl", swift);
        Assert.Equal(4, swift.Split(anchor, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void OpeningWrapper_AssociatedTypeCarrier_ThrowingResultSeparatesRefusalFromSwiftError()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Swift.Sequence, τ_0_0.Element : TestModule.Identifiable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
                m.Throws = true;
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_THROWING_ASSOCIATED");

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var swift = EmitOpeningSwift(env, opened);
        var refusal = string.Join("\n", MethodLevelGenericOpening.BuildRefusalCheckLines(
            env, opened, MethodLevelGenericWrapperEmitter.RefusalParameterName));

        Assert.Contains("do {", swift);
        Assert.Contains("switch try _mlgBody0", swift);
        Assert.Contains("case .refused:", swift);
        Assert.Contains("errorOut.pointee = Unmanaged.passRetained", swift);
        Assert.Contains("τ_0_0 : Swift.Sequence", refusal);
        Assert.Contains("τ_0_0.Element : TestModule.Identifiable", refusal);

        var refusedStart = swift.IndexOf("case .refused:", StringComparison.Ordinal);
        var successStart = swift.IndexOf("case .success", refusedStart, StringComparison.Ordinal);
        var refusedArm = swift[refusedStart..successStart];
        Assert.Contains("_openRefused.pointee = 1", refusedArm);
        Assert.DoesNotContain("errorOut", refusedArm);

        var catchStart = swift.IndexOf("} catch {", StringComparison.Ordinal);
        var clearStart = swift.IndexOf("errorOut.pointee = nil", StringComparison.Ordinal);
        var doStart = swift.IndexOf("do {", StringComparison.Ordinal);
        Assert.True(catchStart > successStart);
        Assert.True(clearStart >= 0 && clearStart < doStart,
            $"The throwing wrapper must clear errorOut before entering the Swift do block.\n{swift}");
        Assert.Contains("errorOut.pointee = Unmanaged.passRetained", swift[catchStart..]);
        Assert.Equal(2, CountOccurrences(swift, "errorOut.pointee"));
    }

    [Fact]
    public void OpeningWrapper_SuperclassCarrier_PrivateScaffoldingCarriesAvailabilityActorAndOrigin()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.BaseWidget>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
                m.IsMainActorIsolated = true;
                m.AvailabilityAnnotations = new List<AvailabilityAnnotation>
                {
                    new("iOS", "18.0", null, null, false, false, null, null),
                };
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_SUPERCLASS_AVAILABLE");

        var swift = EmitOpeningSwift(env);
        var hash = EmitterUtility.DeterministicHash8(env.EmissionSymbol);
        var availability = "@available(iOS 18.0, *)\n";
        var anchor = OriginAnchorEmitter.LineForWrapper(env.MethodDecl);

        Assert.Contains($"{availability}private enum _SBW_MLG_{hash}Outcome", swift);
        Assert.Contains($"{availability}private protocol _SBW_MLG_{hash}Carrier", swift);
        Assert.Contains($"{availability}extension TestModule.BaseWidget: _SBW_MLG_{hash}Carrier", swift);
        Assert.Contains("@MainActor static func _sbw_mlg_call_", swift);
        Assert.Contains("@available(iOS 18.0, *)\n@MainActor\n@_cdecl", swift);
        Assert.Equal(3, swift.Split(anchor, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void OpeningWrapper_LocalizedStringResourceBodyReturnsConvertedSwiftString()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            returnType: new NamedTypeSpec("Foundation.LocalizedStringResource"),
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_LSR");

        var swift = EmitOpeningSwift(env);

        Assert.Contains("func _mlgBody0<_MLG0: TestModule.Describable>(_: _MLG0.Type) -> Swift.String", swift);
        Assert.Contains("return String(localized: obj.describe(item: item))", swift);
        Assert.DoesNotContain("_MLG0.Type) -> Foundation.LocalizedStringResource", swift);
    }

    [Fact]
    public void OpeningWrapper_SuperclassCarrier_ReconstructsPayloadAsSelfAfterTheCarrierCast()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.BaseWidget>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.UsesCdeclMethodWrapper = true;
            });
        env.PromoteSymbol("SBW_TestModule_MyType_describe_SUPERCLASS");

        var swift = EmitOpeningSwift(env);

        Assert.Contains("extension TestModule.BaseWidget: _SBW_MLG_", swift);
        Assert.Contains("assumingMemoryBound(to: Self.self)", swift);
        Assert.Contains("as? any _SBW_MLG_", swift);
        Assert.DoesNotContain("_pwt", swift);

        var wrapper = swift[swift.IndexOf("public func ", StringComparison.Ordinal)..];
        var carrierProof = wrapper.IndexOf("as? any _SBW_MLG_", StringComparison.Ordinal);
        var carrierCall = wrapper.IndexOf("carrier._sbw_mlg_call_", StringComparison.Ordinal);
        Assert.True(carrierProof >= 0 && carrierCall > carrierProof);
        Assert.DoesNotContain("assumingMemoryBound", wrapper);
    }

    [Theory]
    [InlineData("itemPayload")]
    [InlineData("_openRefused")]
    [InlineData("_metadata0")]
    public void OpeningWrapper_UserParameterSpellingASynthetic_StillEmitsUniqueSwiftNames(string userLabel)
    {
        // Swift requires every binding in one function to be unique, and a wrapper it rejects is
        // dropped from the compiled dylib rather than failing the build — the member then goes
        // missing at run time. A user parameter is free to be spelled like any name this emitter
        // synthesises, so the synthetic is what has to move.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<\u03C4_0_0 where \u03C4_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "\u03C4_0_0" },
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.CSSignature.Add(new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = userLabel,
                    PrivateName = userLabel,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = m.ModuleDecl
                });
            });

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));

        var sw = new StringWriter();
        MethodLevelGenericWrapperEmitter.Emit(
            new SwiftWriter(sw), env, new ModuleEmissionContext(), "SBW_TestModule_MyType_describe_TEST", opened);

        var names = ParseCdeclParameterList(sw.ToString())
            .Select(ParameterBindingName)
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        // The user's own parameter keeps its spelling: it is the one name a caller can read off
        // the Swift declaration, and the wrapper's own bindings are the substitutable ones.
        Assert.Contains(userLabel, names);
    }

    #endregion

    #region Managed refusal

    [Fact]
    public void RefusalCheck_UserParameterNamedLikeTheRefusalSlot_ReadsTheRenamedSlot()
    {
        // The refusal slot is added under a fixed name, but a user parameter may legally carry the
        // same spelling and, sitting earlier in the signature, is the one the deduplicator lets
        // keep it. Reading the constant would then test the caller's own argument: a supplied
        // non-zero value would throw a bogus refusal, and a real refusal would go unnoticed and
        // mark an uninitialized indirect-result buffer live.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m =>
            {
                m.UsesMethodLevelGenericOpening = true;
                m.CSSignature.Add(new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = MethodLevelGenericWrapperEmitter.RefusalParameterName,
                    PrivateName = MethodLevelGenericWrapperEmitter.RefusalParameterName,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = m.ModuleDecl
                });
            });

        var pInvokeParams = new SignatureHandler(env).GetPInvokeSignature().Parameters;
        var resolved = MethodLevelGenericOpening.ResolveRefusalParameterName(pInvokeParams);

        // The contract puts OpenRefusal last, and that slot — not the user's argument — is what the
        // managed check has to read. The user's parameter is the earlier occurrence, so it keeps the
        // bare spelling and the refusal slot is the one that moved.
        Assert.Equal(pInvokeParams[^1].Name, resolved);
        Assert.NotEqual(MethodLevelGenericWrapperEmitter.RefusalParameterName, resolved);
        Assert.Contains(pInvokeParams, p => p.Name == MethodLevelGenericWrapperEmitter.RefusalParameterName);
        Assert.Equal(
            resolved,
            pInvokeParams.Single(p => p.modifier == "out" && p.Name.StartsWith(
                MethodLevelGenericWrapperEmitter.RefusalParameterName, StringComparison.Ordinal)).Name);

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var text = string.Join("\n", MethodLevelGenericOpening.BuildRefusalCheckLines(env, opened, resolved));
        Assert.Contains($"if ({resolved} != 0)", text);
    }

    [Fact]
    public void BuildRefusalCheckLines_NamesTypeArgumentAndConstraint()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m => m.UsesMethodLevelGenericOpening = true);

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var text = string.Join("\n", MethodLevelGenericOpening.BuildRefusalCheckLines(
            env, opened, MethodLevelGenericWrapperEmitter.RefusalParameterName));

        // A refused type argument surfaces as a typed managed exception, never a fault, and the
        // message has to name both halves of the mismatch for the caller to act on it.
        Assert.Contains("SwiftRuntimeException", text);
        Assert.Contains("TestModule.Describable", text);
        Assert.Contains($"typeof({opened[0].CSharpName})", text);
        // The 1-based ordinal is the wire encoding; 0 means the call succeeded.
        Assert.Contains("1 =>", text);
    }

    [Fact]
    public void BuildRefusalCheckLines_UnconstrainedGeneric_FallsBackToGenericWording()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0>",
            genericParamNames: new[] { "τ_0_0" },
            configure: m => m.UsesMethodLevelGenericOpening = true);

        Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out var opened));
        var text = string.Join("\n", MethodLevelGenericOpening.BuildRefusalCheckLines(
            env, opened, MethodLevelGenericWrapperEmitter.RefusalParameterName));

        Assert.Contains("the method's Swift constraints", text);
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Extracts the parameter list of the emitted <c>@_cdecl</c> function, split on top-level
    /// commas. Nothing in a wrapper parameter type carries a comma today, but splitting at
    /// angle-bracket depth 0 keeps the count honest if one ever does.
    /// </summary>
    private static IReadOnlyList<string> ParseCdeclParameterList(string swiftSource)
    {
        var funcLine = swiftSource
            .Split('\n')
            .First(l => l.Contains("public func ", StringComparison.Ordinal));

        int open = funcLine.IndexOf('(');
        int close = funcLine.LastIndexOf(')');
        Assert.True(open >= 0 && close > open, $"could not find a parameter list in: {funcLine}");

        var inner = funcLine[(open + 1)..close].Trim();
        if (inner.Length == 0)
            return Array.Empty<string>();

        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '<': depth++; break;
                case '>': if (depth > 0) depth--; break;
                case ',' when depth == 0:
                    parts.Add(inner[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        parts.Add(inner[start..].Trim());
        return parts;
    }

    /// <summary>
    /// Buckets one wrapper slot by what it carries, so the Swift and C# parameter lists can be
    /// compared position by position even though the two emitters name their slots differently.
    /// </summary>
    private static string ClassifySlot(string name) => name switch
    {
        _ when name.Contains("openRefused", StringComparison.OrdinalIgnoreCase) => "openRefusal",
        _ when name.Contains("resultPtr", StringComparison.OrdinalIgnoreCase) => "resultPtr",
        _ when name.Contains("error", StringComparison.OrdinalIgnoreCase) => "errorOut",
        _ when name.Contains("metadata", StringComparison.OrdinalIgnoreCase) => "metadata",
        _ when name.Contains("self", StringComparison.OrdinalIgnoreCase) => "self",
        _ => "argument",
    };

    /// <summary>
    /// The binding name of one <c>@_cdecl</c> parameter — the identifier before the type
    /// annotation, ignoring any external label.
    /// </summary>
    private static string ParameterBindingName(string parameter)
    {
        var beforeColon = parameter.Split(':')[0].Trim();
        var words = beforeColon.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words[^1];
    }

    private static string EmitOpeningSwift(
        MethodEnvironment env,
        IReadOnlyList<MlgOpenedGeneric>? opened = null)
    {
        if (opened == null)
            Assert.True(MethodLevelGenericOpening.TryBuildPlan(env, out opened));

        var sw = new StringWriter();
        MethodLevelGenericWrapperEmitter.Emit(
            new SwiftWriter(sw), env, new ModuleEmissionContext(), env.EmissionSymbol, opened);
        return sw.ToString();
    }

    private static string EmitPInvokeText(MethodEnvironment env)
    {
        var sw = new StringWriter();
        var writer = new CSharpWriter(sw);
        PInvokeEmitter.EmitPInvoke(writer, env, new SignatureHandler(env));
        writer.Flush();
        return sw.ToString();
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    /// <summary>
    /// Builds a class-parented instance method <c>describe(item:)</c> returning
    /// <c>Swift.Int</c>, with one parameter typed as the first declared generic parameter.
    /// </summary>
    private static MethodEnvironment CreateGenericMethodEnv(
        string? rawGenericSig,
        IReadOnlyList<string> genericParamNames,
        TypeSpec? returnType = null,
        TypeSpec? paramType = null,
        bool paramIsInOut = false,
        bool structParent = false,
        Action<MethodDecl>? configure = null)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment(structParent);
        TypeDecl parentDecl = structParent
            ? CreateStructDecl("MyType", moduleDecl)
            : CreateClassDecl("MyType", moduleDecl);

        var method = new MethodDecl
        {
            Name = "describe",
            MangledName = "$s10TestModule_describe",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnType ?? new NamedTypeSpec("Swift.Int"),
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = paramType
                        ?? new NamedTypeSpec(genericParamNames.Count > 0 ? genericParamNames[0] : "Swift.Int"),
                    Name = "item",
                    PrivateName = "item",
                    IsInOut = paramIsInOut,
                    IsGeneric = genericParamNames.Count > 0,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = genericParamNames
                .Select(n => new GenericArgumentDecl(
                    n, n, new List<GenericParameterConformance>(), new List<GenericParameterConformance>()))
                .ToList(),
            RawGenericSig = rawGenericSig,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        configure?.Invoke(method);
        return new MethodEnvironment(method, typeDb);
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new ClassDecl
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
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(bool structParent = false)
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyType"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyType"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyType"),
                MetadataAccessor = "$s10TestModule6MyTypeCMa",
                Flags = TypeRecordFlags.None,
                Kind = structParent ? TypeRecordKind.Struct : TypeRecordKind.Class
            });
        foreach (var protocolName in new[] { "Describable", "Identifiable" })
        {
            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName($"TestModule.{protocolName}"),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", $"I{protocolName}"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{protocolName}"),
                    MetadataAccessor = $"$s10TestModule{protocolName.Length}{protocolName}Mp",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Protocol
                });
        }
        // A class the constraint tests use as a superclass bound — resolvable, but not a protocol.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.BaseWidget"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BaseWidget"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BaseWidget"),
                MetadataAccessor = "$s10TestModule10BaseWidgetCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        typeDb.AddModuleDatabase(testModule);

        var moduleDecl = new ModuleDecl
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

        return (moduleDecl, typeDb);
    }

    #endregion
}
