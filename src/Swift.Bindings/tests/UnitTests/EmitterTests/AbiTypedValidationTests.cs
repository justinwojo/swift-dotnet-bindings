// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using BindingsGeneration.Diagnostics;

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests the typed plan-vs-descriptor swap in <see cref="AbiContractChecker.ValidateModule"/>: typed
/// validation over recorded <see cref="AbiCallPlan"/>s is the primary oracle, the text scan is a
/// completeness cross-check plus a defense-in-depth backstop, and the two are reconciled by the
/// one-directional disagreement invariant. Also pins how a typed violation becomes a verify-recover loop
/// input (<see cref="InEmissionDriver.AttributeAbi"/> → <see cref="WrapperRecoveryController"/>).
/// </summary>
/// <remarks>
/// <para>
/// Per-axis coverage against the plan's contents (entry point, library, resolved calling convention,
/// lowered return/parameter carriers): CC-001/CC-002 carriers, CC-003 wrapper-symbol-library availability,
/// Tj-XM cross-module entry point, and CC-004's structural immunity on a plan. The layout-hash and
/// vtable-layout-identity axes named in the charter are NOT exercised here: the current
/// <see cref="AbiCallPlan"/> foundation carries only P/Invoke-call facts, so those axes have no typed
/// source to validate against yet — enumerated as not-yet-typed rather than fabricated.
/// </para>
/// <para>
/// Reconciliation polarity: typed-fail / text-pass is new recall by design (an attributable violation, no
/// invariant); text-fail / typed-pass on a plan-backed call is a generator invariant failure
/// (<see cref="AbiValidationInvariantException"/>, never auto-resolved); text-fail on a call no plan backs
/// is a backstop violation with a null owner (fails the module closed).
/// </para>
/// </remarks>
public class AbiTypedValidationTests
{
    private static readonly ILogger Log = NullLoggerFactory.Instance.CreateLogger("Test");

    // ── builders ────────────────────────────────────────────────────────────────────────────────

    private static AbiCallPlan Plan(
        string methodName,
        string entryPoint,
        string library,
        PInvokeCallingConvention cc,
        string returnCarrier = "void",
        string[]? parameters = null,
        ArtifactId? owner = null,
        bool isAsync = false) =>
        new()
        {
            MethodName = methodName,
            EntryPoint = entryPoint,
            Library = library,
            CallingConvention = cc,
            ReturnCarrier = returnCarrier,
            ParameterCarriers = (parameters ?? Array.Empty<string>()).ToImmutableArray(),
            IsAsync = isAsync,
            Owner = owner,
        };

    /// <summary>A droppable leaf owner (a method's public C# surface) — the shape a plan records.</summary>
    private static ArtifactId Owner(string name) =>
        ArtifactId.Create(DeclId.Create("Mod", "T", BindingItemKind.Method, name), ArtifactRole.CSharpPublic);

    /// <summary>One emitted P/Invoke declaration, in the exact shape <c>ExtractPInvokes</c> parses.</summary>
    private static string TextPInvoke(
        string callConv, string library, string entryPoint, string returnType, string methodName, string parameters) =>
        $@"
public sealed partial class TestClass
{{
    [UnmanagedCallConv(CallConvs = new Type[] {{ typeof({callConv}) }})]
    [LibraryImport(""{library}"", EntryPoint = ""{entryPoint}"")]
    private static partial {returnType} {methodName}({parameters});
}}";

    /// <summary>An emitted P/Invoke with NO calling-convention attribute — the text scan defaults it to
    /// Cdecl, the exact shape that historically false-positived CC-004 on a Swift-mangled symbol.</summary>
    private static string TextPInvokeNoCallConv(
        string library, string entryPoint, string returnType, string methodName, string parameters) =>
        $@"
public sealed partial class TestClass
{{
    [LibraryImport(""{library}"", EntryPoint = ""{entryPoint}"")]
    private static partial {returnType} {methodName}({parameters});
}}";

    // ── per-axis typed catch (typed-fail / text-pass: attributable, no invariant) ─────────────────

    [Fact]
    public void CC001_SwiftParamCarrier_IsCaughtTyped_AndAttributedToItsOwner()
    {
        // A CallConvSwift call whose parameter lowers to a C-string pointer where Swift passes a two-word
        // String value. Text is silent (empty output), so this is the typed-fail / text-pass polarity: a
        // new recall the swap adds, attributed to the plan's owner — no disagreement invariant.
        var plan = Plan("TakeString", "SBW_Mod_T_take_abc", "libTest.dylib",
            PInvokeCallingConvention.Swift, parameters: new[] { "global::System.String" }, owner: Owner("take"));

        var result = AbiContractChecker.ValidateModule(csOutput: "", new[] { plan }, "Mod", Log);

        Assert.False(result.IsClean);
        var v = Assert.Single(result.Violations);
        Assert.Equal("CC-001", v.RuleId);
        Assert.Equal("SWIFTBIND090", v.DiagnosticCode);
        Assert.NotNull(Assert.Single(result.Attributed).Owner);
    }

    [Fact]
    public void CC002_SwiftReturnCarrier_IsCaughtTyped()
    {
        var plan = Plan("MakeString", "SBW_Mod_T_make_abc", "libTest.dylib",
            PInvokeCallingConvention.Swift, returnCarrier: "string", owner: Owner("make"));

        var result = AbiContractChecker.ValidateModule(csOutput: "", new[] { plan }, "Mod", Log);

        var v = Assert.Single(result.Violations);
        Assert.Equal("CC-002", v.RuleId);
        Assert.Equal("SWIFTBIND091", v.DiagnosticCode);
    }

    [Fact]
    public void CC003_WrapperSymbolBoundAgainstOriginalLibrary_IsCaughtTyped()
    {
        // The symbol-availability axis: an SBW_ wrapper symbol bound against the original library (not the
        // configured wrapper) would throw EntryPointNotFoundException on first call. Typed validation reads
        // the plan's library and resolved Cdecl convention directly.
        var plan = Plan("Do", "SBW_Mod_T_do_abc", "libTest.dylib",
            PInvokeCallingConvention.Cdecl, returnCarrier: "global::System.IntPtr", owner: Owner("do"));

        var result = AbiContractChecker.ValidateModule(
            csOutput: "", new[] { plan }, "Mod", Log, wrapperLibraryName: "ModWrapper.dylib");

        var v = Assert.Single(result.Violations);
        Assert.Equal("CC-003", v.RuleId);
        Assert.Equal("SWIFTBIND093", v.DiagnosticCode);
    }

    [Fact]
    public void TjXM_CrossModuleDispatchThunk_IsCaughtTyped()
    {
        // A dispatch thunk whose mangled symbol names OtherModule, bound against libTest.dylib (module Mod):
        // the library does not export the symbol. Typed validation reads the plan's entry point + library.
        var plan = Plan("PInvoke_bar", "$s11OtherModule5MyFoo3barSiAA0C0CHFTj", "libTest.dylib",
            PInvokeCallingConvention.Swift, returnCarrier: "int",
            parameters: new[] { "global::System.IntPtr" }, owner: Owner("bar"));

        var result = AbiContractChecker.ValidateModule(csOutput: "", new[] { plan }, "Mod", Log);

        var v = Assert.Single(result.Violations);
        Assert.Equal("Tj-XM", v.RuleId);
        Assert.Equal("SWIFTBIND092", v.DiagnosticCode);
    }

    // ── CC-004 structural immunity comes from emission, not from ValidatePlans (the 91-FP class) ────

    [Fact]
    public void CC004_FiresInValidatePlans_OnAHandBuiltCdeclMangledPlan_SoImmunityIsNotInValidatePlans()
    {
        // ValidatePlans is NOT itself immune to CC-004: hand it the ($s + Cdecl) pairing directly and typed
        // validation fires CC-004, exactly as the text scan would (ToPInvokeInfo trusts the plan's recorded
        // convention). This pins WHERE the 91-false-positive immunity lives — not here, but one step
        // upstream in how a plan's convention is resolved. Without the emission-side coercion below, a
        // Cdecl-requested $s call would still reach this rule.
        var handBuilt = Plan("PInvoke_bar", "$s10TestModule5MyFoo3barSiAA0C0CHF", "TestModule",
            PInvokeCallingConvention.Cdecl, returnCarrier: "int",
            parameters: new[] { "global::System.IntPtr" }, owner: Owner("bar"));

        var typed = AbiContractChecker.ValidatePlans(new[] { handBuilt }, "TestModule", wrapperLibraryName: null);

        var v = Assert.Single(typed);
        Assert.Equal("CC-004", v.Violation.RuleId);
        Assert.Equal("SWIFTBIND094", v.Violation.DiagnosticCode);
    }

    [Fact]
    public void CC004_CannotAriseFromEmission_BuildAbiCallPlanResolvesMangledToSwiftCC_SoValidatePlansIsClean()
    {
        // The immunity is emission-side: BuildAbiCallPlan routes the requested convention through
        // SelectCallingConvention, which coerces a Swift-mangled ($s) symbol to Swift CC. So even when
        // Cdecl is REQUESTED for a $s symbol, the recorded plan is Swift — and ValidatePlans over that plan
        // is clean. The ($s + Cdecl) pairing the hand-built plan above trips can never be produced by the
        // emission path, whatever the text scan reads. This is the root fix for the 91-FP episode.
        var info = new PInvokeEmissionInfo
        {
            LibraryPath = "TestModule",
            EntryPoint = "$s10TestModule5MyFoo3barSiAA0C0CHF",
            MethodName = "PInvoke_bar",
            ReturnType = "int",
            ParametersString = "global::System.IntPtr self_",
            CallingConvention = PInvokeCallingConvention.Cdecl, // Cdecl requested on a $s symbol
        };

        var plan = PInvokeEmitHelper.BuildAbiCallPlan(info);

        Assert.Equal(PInvokeCallingConvention.Swift, plan.CallingConvention);
        Assert.Empty(AbiContractChecker.ValidatePlans(new[] { plan }, "TestModule", wrapperLibraryName: null));
    }

    [Fact]
    public void NinetyOneFpShape_ThroughValidateModule_IsClean_BothOraclesAgree()
    {
        // The full 91-FP shape run end to end: the emitted text carries a Swift-mangled symbol correctly
        // under CallConvSwift, and a plan backs it as Swift. Both oracles pass and agree — no violation, no
        // disagreement invariant. (The disagreement invariant only fires when they DISAGREE; see below.)
        const string entry = "$s10TestModule5MyFoo3barSiAA0C0CHF";
        var text = TextPInvoke("CallConvSwift", "TestModule", entry, "int", "PInvoke_bar", "global::System.IntPtr self");
        var plan = Plan("PInvoke_bar", entry, "TestModule",
            PInvokeCallingConvention.Swift, returnCarrier: "int", parameters: new[] { "global::System.IntPtr" });

        var result = AbiContractChecker.ValidateModule(text, new[] { plan }, "TestModule", Log);

        Assert.True(result.IsClean);
    }

    // ── the one-directional disagreement invariant ────────────────────────────────────────────────

    [Fact]
    public void TextFail_TypedPass_OnAPlanBackedCall_ThrowsTheDisagreementInvariant()
    {
        // The emitted text presents ($s + Cdecl) → text fires CC-004; the plan backing the same
        // (method, entry point) resolves the convention to Swift → typed does NOT. On the plan-backed
        // subset the text scan is a cross-check that must agree, so this is a generator invariant failure:
        // one of plan population, the typed comparison, or the text scan is wrong. It fails closed loudly
        // and is never auto-resolved.
        const string entry = "$s10TestModule5MyFoo3barSiAA0C0CHF";
        var text = TextPInvoke("CallConvCdecl", "TestModule", entry, "int", "PInvoke_bar", "int value");
        var plan = Plan("PInvoke_bar", entry, "TestModule",
            PInvokeCallingConvention.Swift, returnCarrier: "int", parameters: new[] { "int" }, owner: Owner("bar"));

        var ex = Assert.Throws<AbiValidationInvariantException>(
            () => AbiContractChecker.ValidateModule(text, new[] { plan }, "TestModule", Log));

        Assert.Equal("TestModule", ex.ModuleName);
        var disagreement = Assert.Single(ex.Disagreements);
        Assert.Equal("CC-004", disagreement.RuleId);
    }

    [Fact]
    public void TypedFail_TextPass_IsNotAnInvariant_ButAnAttributableViolation()
    {
        // The opposite polarity of the invariant: text passes the call (a benign IntPtr carrier under
        // Swift CC) while the plan for the SAME (method, entry point) carries a C-string param → typed
        // fails CC-001. This is new recall working as designed, not a disagreement failure: the typed
        // violation stands, attributed to its owner, and nothing throws.
        const string entry = "SBW_Mod_T_take_abc";
        var text = TextPInvoke("CallConvSwift", "libTest.dylib", entry, "void", "TakeString", "global::System.IntPtr self");
        var plan = Plan("TakeString", entry, "libTest.dylib",
            PInvokeCallingConvention.Swift, parameters: new[] { "global::System.String" }, owner: Owner("take"));

        var result = AbiContractChecker.ValidateModule(text, new[] { plan }, "Mod", Log);

        Assert.False(result.IsClean);
        var v = Assert.Single(result.Violations);
        Assert.Equal("CC-001", v.RuleId);
        Assert.NotNull(Assert.Single(result.Attributed).Owner);
    }

    // ── the text scan as a defense-in-depth backstop (no plan backs the call) ──────────────────────

    [Fact]
    public void TextFail_OnACallNoPlanBacks_IsABackstopViolation_WithNullOwner()
    {
        // A genuine ($s + Cdecl) call that NO plan backs: the text scan is the only oracle, so its CC-004
        // stands as a backstop violation with no owner. A null owner resolves to nothing, so it fails the
        // module closed — the sound default, and proof the text scan stays a live safety net for calls the
        // plan population does not yet cover.
        const string entry = "$s10TestModule5MyFoo3barSiAA0C0CHF";
        var text = TextPInvoke("CallConvCdecl", "TestModule", entry, "int", "PInvoke_orphan", "int value");

        var result = AbiContractChecker.ValidateModule(text, Array.Empty<AbiCallPlan>(), "TestModule", Log);

        Assert.False(result.IsClean);
        var v = Assert.Single(result.Violations);
        Assert.Equal("CC-004", v.RuleId);
        Assert.Null(Assert.Single(result.Attributed).Owner);
    }

    [Fact]
    public void TwoBackstopViolations_SameMethodDifferentEntryPoint_BothSurvive_NotCollapsedByCoarseDedup()
    {
        // The same C# method name may legally recur across containing types under different entry points.
        // Two such backstop calls (no plan backs either) both fire CC-004. The text scan and the module
        // reconciliation must dedup at (RuleId, MethodName, EntryPoint) — NOT the coarser (RuleId,
        // MethodName) — or the second call's violation is silently dropped before the backstop check can
        // see it, starving the safety net of a call it must fail closed on.
        const string entryA = "$s10TestModule5MyFoo3barSiAA0C0CHF";
        const string entryB = "$s10TestModule5MyBar3barSiAA0C0CHF";
        var text =
            TextPInvoke("CallConvCdecl", "TestModule", entryA, "int", "PInvoke_bar", "int value") +
            TextPInvoke("CallConvCdecl", "TestModule", entryB, "int", "PInvoke_bar", "int value");

        var result = AbiContractChecker.ValidateModule(text, Array.Empty<AbiCallPlan>(), "TestModule", Log);

        Assert.Equal(2, result.Violations.Length);
        Assert.All(result.Violations, v => Assert.Equal("CC-004", v.RuleId));
        Assert.Equal(new[] { entryA, entryB }.OrderBy(e => e), result.Violations.Select(v => v.EntryPoint).OrderBy(e => e));
        // Neither is plan-backed, so both are null-owner backstop violations (module fails closed).
        Assert.All(result.Attributed, a => Assert.Null(a.Owner));
    }

    // ── IsClean, dedup ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CleanPlanAndCleanText_IsClean()
    {
        // A benign Cdecl wrapper call with pointer carriers and no wrapper library configured — no rule
        // fires — and no emitted text. The module validates clean.
        var plan = Plan("Fetch", "SBW_Mod_T_fetch_abc", "libTest.dylib",
            PInvokeCallingConvention.Cdecl, returnCarrier: "global::System.IntPtr",
            parameters: new[] { "global::System.IntPtr" }, owner: Owner("fetch"));

        var result = AbiContractChecker.ValidateModule(csOutput: "", new[] { plan }, "Mod", Log);

        Assert.True(result.IsClean);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void TwoPlansSameViolationKey_AreDeduped_PreferringTheOwnerCarryingCopy()
    {
        // Two plans that share (method, entry point) and both fire CC-001 — the same symbol surfaced under
        // two owners, one of which the emission site did not capture (null owner). Dedup collapses them to
        // one violation and keeps the owner-carrying copy, so the loop retains an attributable culprit.
        const string entry = "SBW_dup";
        var withOwner = Plan("Dup", entry, "libTest.dylib",
            PInvokeCallingConvention.Swift, parameters: new[] { "string" }, owner: Owner("dup"));
        var withoutOwner = Plan("Dup", entry, "libTest.dylib",
            PInvokeCallingConvention.Swift, parameters: new[] { "string" }, owner: null);

        var result = AbiContractChecker.ValidateModule(
            csOutput: "", new[] { withOwner, withoutOwner }, "Mod", Log);

        var v = Assert.Single(result.Violations);
        Assert.Equal("CC-001", v.RuleId);
        Assert.NotNull(Assert.Single(result.Attributed).Owner);
    }

    // ── double-emit determinism ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateModule_IsDeterministic_AcrossRepeatedCalls()
    {
        // Same plans + same text, twice: the reconciled, deduplicated violation set is byte-identical in
        // content and order. A backstop text CC-004 on an un-backed call rides alongside typed violations
        // to make the ordering non-trivial.
        var plans = new[]
        {
            Plan("TakeString", "SBW_take", "libTest.dylib", PInvokeCallingConvention.Swift,
                parameters: new[] { "string" }, owner: Owner("take")),
            Plan("MakeString", "SBW_make", "libTest.dylib", PInvokeCallingConvention.Swift,
                returnCarrier: "string", owner: Owner("make")),
        };
        var text = TextPInvoke("CallConvCdecl", "TestModule", "$s10TestModule5MyFoo3barSiAA0C0CHF",
            "int", "PInvoke_orphan", "int value");

        var first = AbiContractChecker.ValidateModule(text, plans, "TestModule", Log);
        var second = AbiContractChecker.ValidateModule(text, plans, "TestModule", Log);

        Assert.Equal(
            first.Violations.Select(v => v.Describe()),
            second.Violations.Select(v => v.Describe()));
        Assert.Equal(3, first.Violations.Length);
    }

    // ── loop integration: a typed violation becomes a verify-recover loop input ────────────────────

    [Fact]
    public void DroppableTypedViolation_AttributesToALeafCulprit_AndTheLoopConvergesDegradedGreen()
    {
        // The full ABI loop input path: a CC-001 plan with a droppable owner → typed violation → the ABI
        // exception the module throws → InEmissionDriver.AttributeAbi resolves the owner to a leaf culprit
        // → the controller withdraws it and the next render converges. Degraded-green: the module ships
        // minus the one rejected call, exactly as a Swift/C# compile withdrawal would.
        var plan = Plan("TakeString", "SBW_take", "libTest.dylib",
            PInvokeCallingConvention.Swift, parameters: new[] { "string" }, owner: Owner("brokenMember"));
        var attributed = AbiContractChecker.ValidatePlans(new[] { plan }, "Mod", wrapperLibraryName: null);
        var abi = new AbiContractViolationException("Mod", attributed);

        var attribution = InEmissionDriver.AttributeAbi(abi);
        var culprit = Assert.Single(attribution.Culprits);
        Assert.True(WrapperRecoveryController.IsLeafRecoverable(culprit.Scope));

        var driver = new AbiPolicyDriver(denylist => denylist.Contains(culprit) ? null : attribution);
        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(new[] { culprit }, result.Denylist);
        Assert.Equal(2, result.Rounds);
    }

    [Fact]
    public void NullOwnerTypedViolation_IsUnattributed_AndTheLoopFailsClosed()
    {
        // A typed violation on a plan whose owner the emission site did not capture (the generic-helper /
        // raw-LibraryImport sinks): AttributeAbi cannot resolve a culprit, so it is an unattributed error
        // and the controller fails the module closed — the sound default ABI violations have always taken.
        var plan = Plan("TakeString", "SBW_take", "libTest.dylib",
            PInvokeCallingConvention.Swift, parameters: new[] { "string" }, owner: null);
        var attributed = AbiContractChecker.ValidatePlans(new[] { plan }, "Mod", wrapperLibraryName: null);
        var abi = new AbiContractViolationException("Mod", attributed);

        var attribution = InEmissionDriver.AttributeAbi(abi);
        Assert.Empty(attribution.Culprits);
        Assert.All(attribution.Diagnostics, d => Assert.Equal(AttributionKind.Unattributed, d.Kind));

        var driver = new AbiPolicyDriver(_ => attribution);
        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.Unattributable, result.Cause);
        Assert.Empty(result.Denylist);
    }

    [Fact]
    public void TheDisagreementInvariant_EscapesTheRecoveryLoop_AndIsNeverAutoResolved()
    {
        // The invariant is a NonRecoverableFault: the loop must never catch it and turn it into a
        // withdrawal or a convergence. Two facts pin that boundary. First, it is NOT an
        // AbiContractViolationException, so the emission driver's exact `catch (AbiContractViolationException)`
        // — the only ABI catch on the render path — cannot swallow it. Second, when a render throws it, the
        // controller propagates the very same instance untouched: it does not converge, attribute, or retry.
        // (This guards the specific regression where widening that catch to include the invariant would
        // leave every other test green.)
        Assert.False(typeof(AbiContractViolationException)
            .IsAssignableFrom(typeof(AbiValidationInvariantException)));

        var invariant = new AbiValidationInvariantException("Mod", ImmutableArray.Create(new AbiCheckViolation
        {
            DiagnosticCode = "SWIFTBIND094",
            RuleId = "CC-004",
            MethodName = "PInvoke_bar",
            EntryPoint = "$s10TestModule5MyFoo3barSiAA0C0CHF",
            Explanation = "text-fail / typed-pass on a plan-backed call",
        }));
        var driver = new AbiPolicyDriver(_ => throw invariant);

        Assert.Same(invariant, Assert.Throws<AbiValidationInvariantException>(
            () => WrapperRecoveryController.Run(driver)));
    }

    // ── the plan owner an accessor records resolves to the withdrawable unit, not the accessor leaf ──

    [Fact]
    public void ResolvePlanOwner_ForAnAccessor_ResolvesToTheEnclosingPropertyFragment_NotTheAccessorLeaf()
    {
        // The M1 attribution fix. The type handler opens ONE fragment for a property — its AccessorGroup
        // unit — and emits both accessors' P/Invokes inside it (MethodHandler.Emit opens no fragment of its
        // own), so the interval map stamps the accessor bytes to that enclosing fragment. Gate 0 withdraws
        // the AccessorGroup, so the recorded plan owner must be the enclosing fragment too — reading the
        // accessor method's own leaf would re-blame a unit the loop cannot withdraw and fail the module
        // closed. Reading the innermost open fragment keeps the plan owner and the interval map from drifting.
        var property = TestDecls.Property("value", hasSetter: true);
        var accessor = TestDecls.Method("value_get");
        accessor.IsAccessor = true;

        var csWriter = new CSharpWriter(new StringWriter());
        var enclosing = FragmentOwners.ForDecl(property);
        csWriter.Fragments.Open(enclosing, 0);

        var resolved = PInvokeEmitter.ResolvePlanOwner(csWriter, accessor);

        Assert.Equal(enclosing.Artifact, resolved);
        Assert.NotEqual(FragmentOwners.ForDecl(accessor).Artifact, resolved);
    }

    [Fact]
    public void ResolvePlanOwner_ForANonAccessor_ResolvesToItsOwnLeaf_IgnoringTheEnclosingFragment()
    {
        // The accessor-only gate. A normal method IS dispatched under its own ForDecl fragment, so plan owner
        // and interval map already coincide; ResolvePlanOwner must NOT coarsen it to whatever fragment happens
        // to be open. Routing every method through the fragment stack would wrongly re-attribute a
        // synthesized/operator method (emitted with no per-member fragment) to the enclosing scope, so the
        // fragment route is taken for accessors only.
        var method = TestDecls.Method("doThing"); // IsAccessor is false by default

        var csWriter = new CSharpWriter(new StringWriter());
        var unrelated = FragmentOwners.ForDecl(TestDecls.Property("other"));
        csWriter.Fragments.Open(unrelated, 0);

        var resolved = PInvokeEmitter.ResolvePlanOwner(csWriter, method);

        Assert.Equal(FragmentOwners.ForDecl(method).Artifact, resolved);
        Assert.NotEqual(unrelated.Artifact, resolved);
    }

    [Fact]
    public void ResolvePlanOwner_ForAnAccessorWithNoOpenFragment_FallsBackToItsOwnLeaf()
    {
        // Defensive fallback: if an accessor is ever dispatched with no fragment open (InnermostOwner null),
        // ResolvePlanOwner must not throw — it degrades to the accessor's own leaf. The pattern-match guard
        // (`InnermostOwner is { } enclosing`) is what makes this safe.
        var accessor = TestDecls.Method("value_get");
        accessor.IsAccessor = true;

        var csWriter = new CSharpWriter(new StringWriter());

        var resolved = PInvokeEmitter.ResolvePlanOwner(csWriter, accessor);

        Assert.Equal(FragmentOwners.ForDecl(accessor).Artifact, resolved);
    }

    // ── fingerprint: the ABI plane hashes through the same FNV-1a as Swift/C# ──────────────────────

    [Fact]
    public void AttributeAbi_Fingerprint_UsesTheSharedFnvHashUnderTheAbiPlaneDiscriminator()
    {
        // The ABI plane must fingerprint through the SAME DiagnosticFingerprint.Compute (FNV-1a over the
        // normalized, sorted error messages) the Swift and C# planes use — under an "abi:" discriminator
        // so an ABI failure can never share a fingerprint with a Swift/C# failure of the same normalized
        // text. Previously it built a bespoke unhashed "abi:" + join(Describe()) string, out of step with
        // the other two planes.
        var plan = Plan("TakeString", "SBW_take", "libTest.dylib",
            PInvokeCallingConvention.Swift, parameters: new[] { "string" }, owner: Owner("brokenMember"));
        var attributed = AbiContractChecker.ValidatePlans(new[] { plan }, "Mod", wrapperLibraryName: null);
        var abi = new AbiContractViolationException("Mod", attributed);

        var attribution = InEmissionDriver.AttributeAbi(abi);

        var groups = abi.Attributed
            .Select(a => new DiagnosticGroup
            {
                Primary = CompilerDiagnostic.Global(DiagnosticSeverity.Error, a.Violation.Describe()),
            })
            .ToList();
        Assert.StartsWith("abi:", attribution.Fingerprint);
        Assert.Equal("abi:" + DiagnosticFingerprint.Compute(groups), attribution.Fingerprint);
    }

    [Fact]
    public void AttributeAbi_Fingerprint_DiffersAcrossDistinctViolationSets()
    {
        // Distinct ABI failures must fingerprint differently, or the no-progress detector could
        // false-positive a stall between two genuinely different failures.
        AttributionResult AttributeFor(string member, string entry)
        {
            var plan = Plan(member, entry, "libTest.dylib",
                PInvokeCallingConvention.Swift, parameters: new[] { "string" }, owner: Owner(member));
            var attributed = AbiContractChecker.ValidatePlans(new[] { plan }, "Mod", wrapperLibraryName: null);
            return InEmissionDriver.AttributeAbi(new AbiContractViolationException("Mod", attributed));
        }

        Assert.NotEqual(
            AttributeFor("TakeString", "SBW_take").Fingerprint,
            AttributeFor("TakeOther", "SBW_other").Fingerprint);
    }

    /// <summary>Decides each round from the denylist it is handed — the same fake-driver shape the wave-1
    /// controller tests use, so the real controller drives the real ABI attribution.</summary>
    private sealed class AbiPolicyDriver : IWrapperRecoveryDriver
    {
        private readonly Func<IReadOnlySet<RecoveryUnitId>, AttributionResult?> _policy;

        public AbiPolicyDriver(Func<IReadOnlySet<RecoveryUnitId>, AttributionResult?> policy) => _policy = policy;

        public AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist) => _policy(denylist);
    }
}
