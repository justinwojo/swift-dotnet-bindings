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
    public void TryBuildPlan_ParameterizedProtocolConstraint_Declines()
    {
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : Swift.Collection<Swift.String>>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
    }

    [Fact]
    public void TryBuildPlan_ClassBoundConstraint_Declines()
    {
        // A superclass bound resolves to a class record, and a class is not a protocol — the
        // existential metatype cannot express it.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.BaseWidget>",
            genericParamNames: new[] { "τ_0_0" });

        Assert.False(MethodLevelGenericOpening.TryBuildPlan(env, out _));
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
    public void TryBuildPlan_AssociatedTypeClause_Declines()
    {
        // `τ_0_0.Element : P` cannot be said by an existential metatype — it needs a carrier whose
        // conformance is conditional on the clause. Declining keeps the member's working route.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable, τ_0_0.Element : TestModule.Identifiable>",
            genericParamNames: new[] { "τ_0_0" });

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
    public void TryBuildPlan_ReturnMentionsOwnGeneric_Declines()
    {
        // A generic return needs the indirect-result buffer sized from the opened layout.
        var env = CreateGenericMethodEnv(
            rawGenericSig: "<τ_0_0 where τ_0_0 : TestModule.Describable>",
            genericParamNames: new[] { "τ_0_0" },
            returnType: new NamedTypeSpec("τ_0_0"));

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
                m.Throws = throws;
            });

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
        Action<MethodDecl>? configure = null)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

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

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment()
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
                Kind = TypeRecordKind.Class
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
