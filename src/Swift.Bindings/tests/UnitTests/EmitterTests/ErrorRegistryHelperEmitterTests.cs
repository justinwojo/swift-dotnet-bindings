// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Locks the namespace-pattern remap contract on the per-module error
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

    // ── Synchronous plain-throws classification ─────────────────────────────────────────

    [Fact]
    public void SyncClassifier_SwiftAndCSharpSidesAgreeOnTheSameEntryPoint()
    {
        // The sync path only works if the C# side calls the exact symbol the Swift side
        // exports. Emitting both halves and requiring each to carry the module's classifier
        // symbol is the contract; a rename on one side alone breaks the binding at load time
        // with a missing-entry-point failure, which no string-free assertion would catch.
        var ctx = new ModuleEmissionContext();
        ctx.ResolvedNamespace = "TestModule";
        ctx.RegisterErrorTypeId("TestModule.WeatherError");

        var symbol = ErrorRegistryHelperEmitter.GetSwiftClassifierSymbolName("TestModule");

        var swiftSide = EmitSwiftCascade(ctx, moduleName: "TestModule");
        Assert.Contains($"@_cdecl(\"{symbol}\")", swiftSide);

        var csharpSide = EmitCSharpRegistry(ctx, moduleName: "TestModule", wrapperLib: "TestWrapper");
        Assert.Contains($"EntryPoint = \"{symbol}\"", csharpSide);
    }

    [Fact]
    public void SyncClassifier_TypedArmHandsTheLiveErrorBoxToTheException()
    {
        // A sync throw must end up with exactly one owner of the Swift error box, typed or
        // not. Both arms of the sync entry point therefore construct through the runtime
        // factories that take the box plus its release delegate, rather than the plain
        // message-only constructor the async path uses (async has already released by then).
        var ctx = new ModuleEmissionContext();
        ctx.ResolvedNamespace = "TestModule";
        ctx.RegisterErrorTypeId("TestModule.WeatherError");

        var output = EmitCSharpRegistry(ctx, moduleName: "TestModule", wrapperLib: "TestWrapper");
        var syncEntryPoint = ExtractMember(output, "CreateSyncException");

        Assert.Contains("CreateSwiftError<global::TestModule.WeatherError>", syncEntryPoint);
        Assert.Contains("errorBox", syncEntryPoint);
        Assert.Contains("releaseError", syncEntryPoint);
        // The message-only constructor would drop the box on the floor — that shape belongs
        // to the async entry point, never to this one.
        Assert.DoesNotContain("new global::Swift.Runtime.SwiftException(errorMessage)", syncEntryPoint);
    }

    [Fact]
    public void SyncClassifier_AsyncEntryPointIsUnchangedByTheSyncArm()
    {
        // Both arms share one cascade builder, so the async entry point is re-verified here:
        // it still constructs the typed exception from the payload alone and never touches a
        // box it does not own.
        var ctx = new ModuleEmissionContext();
        ctx.ResolvedNamespace = "TestModule";
        ctx.RegisterErrorTypeId("TestModule.WeatherError");

        var output = EmitCSharpRegistry(ctx, moduleName: "TestModule", wrapperLib: "TestWrapper");
        var asyncEntryPoint = ExtractMember(output, "CreateException");

        Assert.Contains("new global::Swift.Runtime.SwiftException<global::TestModule.WeatherError>", asyncEntryPoint);
        Assert.Contains("new global::Swift.Runtime.SwiftException(errorMessage)", asyncEntryPoint);
        Assert.DoesNotContain("releaseError", asyncEntryPoint);
    }

    [Fact]
    public void GetSyncDispatchHelperReference_ModuleWithRegisteredErrors_ReturnsHelperReference()
    {
        var ctx = new ModuleEmissionContext();
        ctx.ResolvedNamespace = "StoreKit2";
        ctx.ErrorRegistryModuleName = "StoreKit";
        ctx.RegisterErrorTypeId("StoreKit.SKError");

        Assert.Equal(
            "global::StoreKit2._SbwModuleErrorRegistry_StoreKit",
            ErrorRegistryHelperEmitter.GetSyncDispatchHelperReference("StoreKit", ctx));
    }

    [Fact]
    public void GetSyncDispatchHelperReference_ModuleWithNoRegisteredErrors_ReturnsNull()
    {
        // Nothing to dispatch to: no helper class is emitted for a module that registered no
        // Error-conforming types, so the member must keep the untyped path rather than
        // reference a class that does not exist.
        var ctx = new ModuleEmissionContext();
        ctx.ResolvedNamespace = "TestModule";
        ctx.ErrorRegistryModuleName = "TestModule";

        Assert.Null(ErrorRegistryHelperEmitter.GetSyncDispatchHelperReference("TestModule", ctx));
    }

    [Fact]
    public void GetSyncDispatchHelperReference_MemberFromAnotherModule_ReturnsNull()
    {
        // The registry is computed for exactly one module per emission. A member reached from
        // a different module has no helper class in this file, so it keeps the untyped path.
        var ctx = new ModuleEmissionContext();
        ctx.ResolvedNamespace = "TestModule";
        ctx.ErrorRegistryModuleName = "TestModule";
        ctx.RegisterErrorTypeId("TestModule.WeatherError");

        Assert.Null(ErrorRegistryHelperEmitter.GetSyncDispatchHelperReference("OtherModule", ctx));
    }

    [Fact]
    public void GetSyncDispatchHelperReference_NoContextOrModule_ReturnsNull()
    {
        Assert.Null(ErrorRegistryHelperEmitter.GetSyncDispatchHelperReference("TestModule", ctx: null));
        Assert.Null(ErrorRegistryHelperEmitter.GetSyncDispatchHelperReference(null, new ModuleEmissionContext()));
    }

    private static string EmitCSharpRegistry(ModuleEmissionContext ctx, string moduleName, string wrapperLib)
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        ErrorRegistryHelperEmitter.EmitCSharpRegistryIfNeeded(csWriter, moduleName, wrapperLib, ctx, typeDatabase: null);
        return output.ToString();
    }

    private static string EmitSwiftCascade(ModuleEmissionContext ctx, string moduleName)
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);
        ErrorRegistryHelperEmitter.EmitSwiftCascadeIfNeeded(swiftWriter, moduleName, ctx, typeDatabase: null);
        return output.ToString();
    }

    // Returns the emitted text from the declaration of <paramref name="memberName"/> up to the
    // start of the next member, so an assertion about one entry point cannot be satisfied by
    // text belonging to its sibling.
    private static string ExtractMember(string emitted, string memberName)
    {
        const string declPrefix = "internal static System.Exception ";
        var start = emitted.IndexOf(declPrefix + memberName + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected the emitted registry to declare {memberName}");
        var next = emitted.IndexOf(declPrefix, start + declPrefix.Length, StringComparison.Ordinal);
        return next < 0 ? emitted.Substring(start) : emitted.Substring(start, next - start);
    }
}
