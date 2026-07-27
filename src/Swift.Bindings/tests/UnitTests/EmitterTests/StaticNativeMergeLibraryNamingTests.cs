// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A vendor xcframework whose slice binary is a static <c>ar</c> archive is force-loaded into the
/// compiled Swift wrapper and then dropped from every consumer reference and pack site
/// (<see cref="NativePackagingPolicy.ShouldIncludeSourceXcframework"/> returning false). The wrapper
/// becomes the sole runtime carrier of the vendor's symbols, so the vendor's own library name no
/// longer resolves — an emitted import naming it throws <c>DllNotFoundException</c> on ordinary API
/// use even though the package is otherwise well-formed.
///
/// <para>
/// These tests pin the three halves of that fix and, just as importantly, its boundaries. A blanket
/// redirect would be actively wrong in three ways, each of which has a negative test here: it would
/// break the overwhelmingly common dynamic-source case, it would re-target a dynamic dependency's
/// dispatch thunk at a wrapper that does not export it, and it would collapse the metadata
/// accessor's wrapper-absent recovery arm into a duplicate of its own primary.
/// </para>
/// </summary>
public class StaticNativeMergeLibraryNamingTests
{
    private const string Vendor = "Mappedin";
    private const string VendorDylib = "/tmp/Mappedin.xcframework/ios-arm64/Mappedin.framework/Mappedin";
    private const string Wrapper = "MappedinSwiftBindings";

    private static IReadOnlySet<string> Merged(params string[] modules) =>
        ImmutableHashSet.Create(modules);

    private static IReadOnlySet<string> NothingMerged => ImmutableHashSet<string>.Empty;

    // ── The type-database choke point ───────────────────────────────────────────────────────────

    [Fact]
    public void GetLibraryPath_StaticMergedModule_NamesTheWrapper()
    {
        // The whole point: every emission site that bakes a library name for this module's symbols
        // goes through GetLibraryPath, so redirecting here is what stops the binding importing a
        // library the package does not ship.
        var db = new TypeDatabase { AsyncLibraryName = Wrapper, StaticallyMergedModules = Merged(Vendor) };
        db.AddModuleDatabase(new ModuleTypeDatabase(Vendor, VendorDylib));

        Assert.Equal(Wrapper, db.GetLibraryPath(Vendor));
    }

    [Fact]
    public void GetLibraryPath_DynamicSource_NamesTheVendorLibraryUnchanged()
    {
        // The dominant case by far — a dynamic vendor dylib ships and carries its own symbols, and
        // the wrapper defines only SBW_/SBSW_ entry points, not the vendor's mangled ones.
        // Redirecting here would convert every working direct import into an
        // EntryPointNotFoundException, so the redirect must be inert without a merge signal.
        var db = new TypeDatabase { AsyncLibraryName = Wrapper, StaticallyMergedModules = NothingMerged };
        db.AddModuleDatabase(new ModuleTypeDatabase(Vendor, VendorDylib));

        Assert.Equal(VendorDylib, db.GetLibraryPath(Vendor));
    }

    [Fact]
    public void GetLibraryPath_MergedPrimary_DoesNotRedirectAnUnmergedSibling()
    {
        // The signal is per-module, not a global mode. A dependency resolved alongside a merged
        // primary keeps its own native — only the module actually force-loaded moves.
        var db = new TypeDatabase { AsyncLibraryName = Wrapper, StaticallyMergedModules = Merged(Vendor) };
        db.AddModuleDatabase(new ModuleTypeDatabase(Vendor, VendorDylib));
        db.AddModuleDatabase(new ModuleTypeDatabase("SideKit", "@rpath/SideKit.framework/SideKit"));

        Assert.Equal("@rpath/SideKit.framework/SideKit", db.GetLibraryPath("SideKit"));
    }

    [Fact]
    public void GetLibraryPath_StaticSourceButNoWrapperConfigured_NamesTheVendorLibrary()
    {
        // Without a wrapper there is nothing to redirect TO. The static source is then its own
        // (sole) carrier, which is exactly what ShouldIncludeSourceXcframework decides for this
        // combination — so emission has to agree and keep the declared name.
        var db = new TypeDatabase { AsyncLibraryName = null, StaticallyMergedModules = Merged(Vendor) };
        db.AddModuleDatabase(new ModuleTypeDatabase(Vendor, VendorDylib));

        Assert.Equal(VendorDylib, db.GetLibraryPath(Vendor));
    }

    [Fact]
    public void GetDeclaredLibraryPath_StaticMergedModule_StillNamesTheVendorLibrary()
    {
        // The deliberate opt-out the metadata accessor's recovery arm uses. That arm runs only from
        // the catch around the wrapper-bound primary — i.e. precisely when the wrapper is missing —
        // so it must keep naming the source. Redirecting it would make both arms identical and turn
        // a recovery path into a no-op.
        var db = new TypeDatabase { AsyncLibraryName = Wrapper, StaticallyMergedModules = Merged(Vendor) };
        db.AddModuleDatabase(new ModuleTypeDatabase(Vendor, VendorDylib));

        Assert.Equal(VendorDylib, db.GetDeclaredLibraryPath(Vendor));
    }

    [Fact]
    public void StaticallyMergedModules_DefaultsToEmpty()
    {
        // Fail-safe default: a database nobody told about a static merge redirects nothing.
        Assert.Empty(new TypeDatabase().StaticallyMergedModules);
    }

    // ── The load-bearing safety property: an empty merged set changes NOTHING ────────────────────
    //
    // Every binding whose source native is dynamic — the overwhelming majority, and the entire
    // existing corpus — runs with an empty merged set. So "empty set ⇒ the decision is exactly the
    // pre-change decision" is the single property that makes this change safe to land, and it is
    // asserted here directly rather than argued in prose, at each of the three decision points.

    [Fact]
    public void EmptyMergedSet_GetLibraryPath_IsIdentityOverEveryRegisteredModule()
    {
        // At the choke point: with nothing merged, the redirect is the identity function, so every
        // emission site that resolves a library through it sees exactly the declared name it saw
        // before the redirect existed — even with a wrapper configured.
        var db = new TypeDatabase { AsyncLibraryName = Wrapper, StaticallyMergedModules = NothingMerged };
        db.AddModuleDatabase(new ModuleTypeDatabase(Vendor, VendorDylib));
        db.AddModuleDatabase(new ModuleTypeDatabase("SideKit", "@rpath/SideKit.framework/SideKit"));
        db.AddModuleDatabase(new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib"));

        foreach (var module in new[] { Vendor, "SideKit", "Swift" })
            Assert.Equal(db.GetDeclaredLibraryPath(module), db.GetLibraryPath(module));
    }

    [Theory]
    // A plain mangled API symbol, a generic metadata accessor, a same-module dispatch thunk, a
    // cross-module dispatch thunk, a path-form library, the in-process sentinel, and Swift's own
    // stdlib — the full shape matrix the resolver discriminates on.
    [InlineData("Mappedin", "Mappedin", "$s8Mappedin6MapViewC10getMapDataSSyF")]
    [InlineData("Mappedin", "Mappedin", "$s8Mappedin10TypedEventCMa")]
    [InlineData("Mappedin", "Mappedin", "$s8Mappedin6MapViewC4nameSSvgTj")]
    [InlineData("Mappedin", "Mappedin", "$s7SideKit6WidgetC4drawyyFTj")]
    [InlineData("/tmp/build/Mappedin.dylib", "Mappedin", "$s7SideKit6WidgetC4drawyyFTj")]
    [InlineData("__Internal", "Mappedin", "$s8Mappedin6MapViewC10getMapDataSSyF")]
    [InlineData("/usr/lib/swift/libswiftCore.dylib", "Swift", "$sSiMa")]
    public void EmptyMergedSet_ResolveModuleLibraryPathCore_MatchesThePreRedirectDecision(
        string moduleLibPath, string moduleName, string entryPoint)
    {
        // The three-argument call IS the pre-change code path — the merge parameters were added as
        // optional precisely so the old signature keeps compiling and keeps meaning what it meant.
        // Asserting the informed-but-empty call against it makes "byte-identical to today" a
        // mechanical check rather than a claim, across every shape the resolver branches on.
        var preRedirect = PInvokeEmitter.ResolveModuleLibraryPathCore(
            moduleLibPath, moduleName, entryPoint);

        var withEmptyMergeSet = PInvokeEmitter.ResolveModuleLibraryPathCore(
            moduleLibPath, moduleName, entryPoint,
            staticallyMergedModules: NothingMerged, wrapperLibraryName: Wrapper);

        Assert.Equal(preRedirect, withEmptyMergeSet);
    }

    [Theory]
    [InlineData("Mappedin", "$s8Mappedin6MapViewC4drawyyFTj")]
    [InlineData("Mappedin", "$s7SideKit6WidgetC4drawyyFTj")]
    [InlineData("MappedinSwiftBindings", "$s8Mappedin6MapViewC4drawyyFTj")]
    [InlineData("/usr/lib/swift/libswiftCore.dylib", "$ss6ObjectC4drawyyFTj")]
    public void EmptyMergedSet_TjXm_ReportsExactlyWhatTheUninformedRuleReports(
        string library, string entryPoint)
    {
        // Same property on the checker side. Passing no merge information at all is what every
        // caller did before this change; passing an empty set must be indistinguishable from it,
        // including on the newly-judged wrapper arm — otherwise the widened rule would change what
        // a dynamic-source build reports.
        var cs = TjPInvoke(library, entryPoint);

        var uninformed = AbiContractChecker.Validate(cs, Vendor, NullLogger.Instance, Wrapper);
        var emptySet = AbiContractChecker.Validate(
            cs, Vendor, NullLogger.Instance, Wrapper, NothingMerged);

        Assert.Equal(
            uninformed.Violations.Select(v => v.Describe()),
            emptySet.Violations.Select(v => v.Describe()));
    }

    // ── The per-P/Invoke library decision ───────────────────────────────────────────────────────

    [Fact]
    public void ResolveModuleLibraryPathCore_MergedModule_MetadataAccessor_BindsWrapper()
    {
        // A generic type's $s…Ma accessor is the sharp case: it has no @_cdecl wrapper primary to
        // fall back from, so this raw import is the ONLY path to the symbol. Naming a dropped
        // library here is an unconditional DllNotFoundException on first use of the type.
        Assert.Equal(
            Wrapper,
            PInvokeEmitter.ResolveModuleLibraryPathCore(
                moduleLibPath: Vendor, moduleName: Vendor,
                entryPoint: ManglingProbes.ModulePrefix(Vendor) + "10TypedEventCMa",
                staticallyMergedModules: Merged(Vendor), wrapperLibraryName: Wrapper));
    }

    [Fact]
    public void ResolveModuleLibraryPathCore_MergedModule_OrdinaryApiSymbol_BindsWrapper()
    {
        // The bulk of the live exposure: ordinary methods, statics and property getters bound
        // directly to the vendor name.
        Assert.Equal(
            Wrapper,
            PInvokeEmitter.ResolveModuleLibraryPathCore(
                moduleLibPath: Vendor, moduleName: Vendor,
                entryPoint: ManglingProbes.ModulePrefix(Vendor) + "6MapViewC10getMapDataSSyF",
                staticallyMergedModules: Merged(Vendor), wrapperLibraryName: Wrapper));
    }

    [Fact]
    public void ResolveModuleLibraryPathCore_MergedModule_SameModuleDispatchThunk_BindsWrapper()
    {
        // A non-final class's Tj thunk lives in the same merged objects, so it moves with them.
        Assert.Equal(
            Wrapper,
            PInvokeEmitter.ResolveModuleLibraryPathCore(
                moduleLibPath: Vendor, moduleName: Vendor,
                entryPoint: ManglingProbes.ModulePrefix(Vendor) + "6MapViewC4nameSSvgTj",
                staticallyMergedModules: Merged(Vendor), wrapperLibraryName: Wrapper));
    }

    [Fact]
    public void ResolveModuleLibraryPathCore_DynamicSource_BindsVendorModuleUnchanged()
    {
        // Regression guard for the common case: with nothing merged the decision is byte-identical
        // to what it was before the redirect existed.
        Assert.Equal(
            Vendor,
            PInvokeEmitter.ResolveModuleLibraryPathCore(
                moduleLibPath: Vendor, moduleName: Vendor,
                entryPoint: ManglingProbes.ModulePrefix(Vendor) + "10TypedEventCMa",
                staticallyMergedModules: NothingMerged, wrapperLibraryName: Wrapper));
    }

    [Fact]
    public void ResolveModuleLibraryPathCore_CrossModuleThunk_OwningModuleNotMerged_BindsOwningModule()
    {
        // The redirect keys on the module that OWNS the symbol, not on the module being emitted.
        // A dispatch thunk declared by a separate, dynamically-linked dependency is still carried by
        // that dependency's own native; pointing it at the wrapper — which never linked those
        // objects — would trade one EntryPointNotFoundException for another.
        Assert.Equal(
            "SideKit",
            PInvokeEmitter.ResolveModuleLibraryPathCore(
                moduleLibPath: Vendor, moduleName: Vendor,
                entryPoint: ManglingProbes.ModulePrefix("SideKit") + "6WidgetC4drawyyFTj",
                staticallyMergedModules: Merged(Vendor), wrapperLibraryName: Wrapper));
    }

    [Fact]
    public void ResolveModuleLibraryPathCore_CrossModuleThunk_OwningModuleMerged_BindsWrapper()
    {
        // The other polarity of the same rule: when the dependency IS one of the archives force-
        // loaded into the wrapper, the wrapper is where its thunk lives.
        Assert.Equal(
            Wrapper,
            PInvokeEmitter.ResolveModuleLibraryPathCore(
                moduleLibPath: Vendor, moduleName: Vendor,
                entryPoint: ManglingProbes.ModulePrefix("SideKit") + "6WidgetC4drawyyFTj",
                staticallyMergedModules: Merged(Vendor, "SideKit"), wrapperLibraryName: Wrapper));
    }

    [Theory]
    [InlineData("__Internal")]
    [InlineData("/usr/lib/swift/libswiftCore.dylib")]
    [InlineData("CryptoKit")]
    public void ResolveModuleLibraryPathCore_NonVendorLibraryNames_AreNeverRedirected(string library)
    {
        // Guardrail for the names that must survive untouched: the in-process sentinel, Swift's
        // OS-resident stdlib, and the bare Apple system-framework names ResolveRuntimeLibraryName
        // produces for the NativeAOT device resolver. None of these modules is ever a merged static
        // archive, so keying the redirect on module membership leaves all of them alone.
        Assert.Equal(
            library,
            PInvokeEmitter.ResolveModuleLibraryPathCore(
                moduleLibPath: library, moduleName: "CryptoKit",
                entryPoint: "$s9CryptoKit6SHA256VMa",
                staticallyMergedModules: Merged(Vendor), wrapperLibraryName: Wrapper));
    }

    // ── SWIFTBIND092 / Tj-XM: narrowed for the static-merge case, not weakened ───────────────────

    private static string TjPInvoke(string library, string entryPoint) => $@"
public sealed partial class TestClass
{{
    [UnmanagedCallConv(CallConvs = new Type[] {{ typeof(CallConvSwift) }})]
    [LibraryImport(""{library}"", EntryPoint = ""{entryPoint}"")]
    private static partial int PInvoke_bar_ABC123(SwiftSelf<IntPtr> self);
}}";

    [Fact]
    public void TjXm_WrapperBoundThunk_OwningModuleStaticallyMerged_NoViolation()
    {
        // The one case a wrapper-bound thunk is legal: the wrapper force-loaded the archive that
        // declares it, so the wrapper really does export the symbol.
        var cs = TjPInvoke(Wrapper, ManglingProbes.ModulePrefix(Vendor) + "6MapViewC4drawyyFTj");

        var result = AbiContractChecker.Validate(
            cs, Vendor, NullLogger.Instance, Wrapper, Merged(Vendor));

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "Tj-XM");
    }

    [Fact]
    public void TjXm_WrapperBoundThunk_OwningModuleNotMerged_DetectsViolation()
    {
        // The half that proves the carve-out above is a narrowing rather than a hole. Before the
        // static-merge redirect existed, a wrapper-bound thunk left this rule through an early
        // return and was never judged at all; it is now judged, and rejected unless the merge fact
        // justifies it. This makes the checker an independent oracle for the emitter's redirect —
        // a redirect the emitter should not have made fails the build here instead of shipping.
        var cs = TjPInvoke(Wrapper, ManglingProbes.ModulePrefix("SideKit") + "6WidgetC4drawyyFTj");

        var result = AbiContractChecker.Validate(
            cs, Vendor, NullLogger.Instance, Wrapper, Merged(Vendor));

        var tj = result.Violations.Where(v => v.RuleId == "Tj-XM").ToList();
        Assert.Single(tj);
        Assert.Equal("SWIFTBIND092", tj[0].DiagnosticCode);
    }

    [Fact]
    public void TjXm_WrapperBoundThunk_NoMergeInformationSupplied_DetectsViolation()
    {
        // Absent any merge signal the rule stays at its strictest. Every dynamic-source run passes
        // through here, so this is also what keeps the new arm from silently trusting a wrapper.
        var cs = TjPInvoke(Wrapper, ManglingProbes.ModulePrefix(Vendor) + "6MapViewC4drawyyFTj");

        var result = AbiContractChecker.Validate(cs, Vendor, NullLogger.Instance, Wrapper);

        Assert.Contains(result.Violations, v => v.RuleId == "Tj-XM");
    }

    [Fact]
    public void TjXm_OriginalLibraryBoundThunk_WrongModule_StillDetectsViolation_WithMergeSetSupplied()
    {
        // The merge carve-out must not leak into the arm the rule was written for: a thunk bound to
        // a source library that does not declare it is still unresolvable, merge set or not.
        var cs = TjPInvoke(Vendor, ManglingProbes.ModulePrefix("SideKit") + "6WidgetC4drawyyFTj");

        var result = AbiContractChecker.Validate(
            cs, Vendor, NullLogger.Instance, Wrapper, Merged(Vendor));

        Assert.Contains(result.Violations, v => v.RuleId == "Tj-XM");
    }

    [Fact]
    public void TjXm_OriginalLibraryBoundThunk_MatchingModule_NoViolation_WithMergeSetSupplied()
    {
        // Discrimination guard for the test above — the correct pairing still passes.
        var cs = TjPInvoke(Vendor, ManglingProbes.ModulePrefix(Vendor) + "6MapViewC4drawyyFTj");

        var result = AbiContractChecker.Validate(
            cs, Vendor, NullLogger.Instance, Wrapper, Merged(Vendor));

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "Tj-XM");
    }

    [Fact]
    public void TjXm_SwiftCoreBoundThunk_NoViolation_WithMergeSetSupplied()
    {
        // Swift's own OS-resident stdlib keeps its pre-existing exemption; widening the rule past
        // the OriginalLibrary arm must not start reporting it.
        var cs = TjPInvoke("/usr/lib/swift/libswiftCore.dylib", "$ss6ObjectC4drawyyFTj");

        var result = AbiContractChecker.Validate(
            cs, Vendor, NullLogger.Instance, Wrapper, Merged(Vendor));

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "Tj-XM");
    }
}
