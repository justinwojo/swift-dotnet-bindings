// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Locks the namespace-pattern remap contract on Phase 4's per-module error
/// registry helper. When a binding project sets <c>&lt;NamespacePattern&gt;</c>
/// to a value different from the Swift module name (e.g. StoreKit2 maps Swift
/// module <c>StoreKit</c> to C# namespace <c>StoreKit2</c>), every
/// <c>global::</c> cross-reference emitted into the same wrapper file must
/// target the resolved C# namespace, not the raw module name — otherwise the
/// helper class and its referenced error types resolve to a non-existent
/// <c>global::StoreKit.*</c> path and the generated wrapper fails C#
/// compilation with CS0234.
/// </summary>
public class ErrorRegistryHelperEmitterTests
{
    [Fact]
    public void EmitCSharpRegistry_IdentityNamespace_EmitsHelperWithModuleNameClassAndGlobalPrefix()
    {
        var ctx = new ModuleEmissionContext();
        ctx.ResolvedNamespace = "TestModule";
        ctx.RegisterErrorTypeId("TestModule.WeatherError");

        var output = EmitCSharpRegistry(ctx, moduleName: "TestModule", wrapperLib: "TestWrapper");

        Assert.Contains("_SbwModuleErrorRegistry_TestModule", output);
        Assert.Contains("global::TestModule.WeatherError", output);
        // No collateral references to the resolved namespace under a different name.
        Assert.DoesNotContain("global::TestModule.TestModule", output);
    }

    [Fact]
    public void EmitCSharpRegistry_RemappedNamespace_HelperLivesInResolvedNamespaceAndTypesRebased()
    {
        // Swift module: StoreKit. Resolved C# namespace: StoreKit2.
        // The helper class name keeps the Swift module suffix
        // (_SbwModuleErrorRegistry_StoreKit) so the symbol stays distinct per Swift
        // module; cross-references inside the same wrapper file must reach it via
        // the resolved namespace (global::StoreKit2._SbwModuleErrorRegistry_StoreKit),
        // and registered error types from the Swift module must rebase their
        // module-prefix to the resolved namespace
        // (StoreKit.SKError → global::StoreKit2.SKError).
        var ctx = new ModuleEmissionContext();
        ctx.ResolvedNamespace = "StoreKit2";
        ctx.RegisterErrorTypeId("StoreKit.SKError");
        ctx.RegisterErrorTypeId("StoreKit.StoreKitError");

        var output = EmitCSharpRegistry(ctx, moduleName: "StoreKit", wrapperLib: "StoreKit2Wrapper");

        // Helper class name still derives from the Swift module name.
        Assert.Contains("_SbwModuleErrorRegistry_StoreKit", output);

        // Registered error types are rebased to the resolved namespace.
        Assert.Contains("global::StoreKit2.SKError", output);
        Assert.Contains("global::StoreKit2.StoreKitError", output);

        // The raw Swift module path is never emitted into the dispatch body, since
        // C# has no namespace named "StoreKit" in this binding project — references
        // to "global::StoreKit.*" would fail with CS0234 in the consumer csproj.
        Assert.DoesNotContain("global::StoreKit.", output);
    }

    [Fact]
    public void ToCSharpFullyQualifiedName_Identity_PreservesPath()
    {
        var qualified = ErrorRegistryHelperEmitter.ToCSharpFullyQualifiedName(
            "WeatherKit.WeatherError",
            moduleName: "WeatherKit",
            resolvedNamespace: "WeatherKit");
        Assert.Equal("global::WeatherKit.WeatherError", qualified);
    }

    [Fact]
    public void ToCSharpFullyQualifiedName_Remap_RebasesModulePrefixOnly()
    {
        // Nested-type path under a module prefix must be preserved verbatim once the
        // prefix is replaced — only the leading module segment is rewritten.
        var qualified = ErrorRegistryHelperEmitter.ToCSharpFullyQualifiedName(
            "StoreKit.Product.PurchaseError",
            moduleName: "StoreKit",
            resolvedNamespace: "StoreKit2");
        Assert.Equal("global::StoreKit2.Product.PurchaseError", qualified);
    }

    [Fact]
    public void ToCSharpFullyQualifiedName_ForeignModule_KeepsOriginalPath()
    {
        // Defensive: types from foreign modules (cross-module registration) keep
        // their original module prefix — only the current module's prefix is
        // rewritten. Today the registry is scoped to the current module per
        // ErrorEnumRegistryEmitter.Precompute, so this branch is reserved for a
        // future cross-module registration follow-up.
        var qualified = ErrorRegistryHelperEmitter.ToCSharpFullyQualifiedName(
            "Foundation.LocalizedError",
            moduleName: "StoreKit",
            resolvedNamespace: "StoreKit2");
        Assert.Equal("global::Foundation.LocalizedError", qualified);
    }

    [Fact]
    public void GetFullyQualifiedHelperReference_Identity_UsesModuleName()
    {
        // Under the default {Module} pattern the resolved namespace equals the Swift
        // module name; the helper reference is identical to the simple global:: form.
        var helperRef = ErrorRegistryHelperEmitter.GetFullyQualifiedHelperReference(
            moduleName: "TestModule", resolvedNamespace: "TestModule");
        Assert.Equal("global::TestModule._SbwModuleErrorRegistry_TestModule", helperRef);
    }

    [Fact]
    public void GetFullyQualifiedHelperReference_Remap_UsesResolvedNamespaceWithSwiftModuleSymbol()
    {
        // The helper symbol stays anchored to the Swift module name (so it remains
        // distinct per Swift module across NamespacePattern remaps), but the namespace
        // segment of the cross-reference is the resolved C# namespace. Async cascade
        // error callbacks emit this reference into the same wrapper file as the helper
        // class, so a mismatch here is what produced the original StoreKit2 CS0234 wave.
        var helperRef = ErrorRegistryHelperEmitter.GetFullyQualifiedHelperReference(
            moduleName: "StoreKit", resolvedNamespace: "StoreKit2");
        Assert.Equal("global::StoreKit2._SbwModuleErrorRegistry_StoreKit", helperRef);
    }

    [Fact]
    public void GetFullyQualifiedHelperReference_NullResolvedNamespace_FallsBackToModuleName()
    {
        // The async-callback emit sites read the resolved namespace off
        // ModuleEmissionContext, which is nullable. A null value (test fixtures
        // that never reach ModuleHandler, or an early-stage emission with no
        // resolver wired) must fall back to the identity path rather than emit
        // a malformed global::. path with a dangling segment.
        var helperRef = ErrorRegistryHelperEmitter.GetFullyQualifiedHelperReference(
            moduleName: "TestModule", resolvedNamespace: null);
        Assert.Equal("global::TestModule._SbwModuleErrorRegistry_TestModule", helperRef);
    }

    private static string EmitCSharpRegistry(ModuleEmissionContext ctx, string moduleName, string wrapperLib)
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        ErrorRegistryHelperEmitter.EmitCSharpRegistryIfNeeded(csWriter, moduleName, wrapperLib, ctx, typeDatabase: null);
        return output.ToString();
    }
}
