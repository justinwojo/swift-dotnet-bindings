// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Per-strategy unit tests for the resolver migration. Each strategy is
/// exercised in isolation — direct construction and a single
/// <see cref="IResolutionStrategy.TryResolve"/> call — so the test focuses on
/// the strategy's claim contract rather than dispatch ordering. Dispatch
/// composition lives in <see cref="TypeResolverTests"/>.
/// </summary>
public class StrategyTests
{
    private static TypeDatabase EmptyDatabase() => new();

    [Theory]
    [InlineData("any Swift.Encoder")]
    [InlineData("any")]
    public void ExistentialStrategy_MatchesExistentialNames(string name)
    {
        var strategy = new ExistentialStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec(name),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.Equal(TypeDatabaseExtensions.AnyType, result!.Record);
        Assert.NotNull(result.SyntheticFallback);
        Assert.Equal("Existential type fallback", result.SyntheticFallback!.Value.Reason);
    }

    [Fact]
    public void ExistentialStrategy_PlainExistentialOverPlainProtocol_SuppressesFallback()
    {
        // `any DotVectorAnimationCacheProvider` over a plain protocol (no associated types,
        // no Self requirement) projects cleanly to `IDotVectorAnimationCacheProvider` through
        // the standard existential proxy. Emitting `[UnsupportedSwiftType("Existential type
        // fallback", …)]` on a member whose body uses the working proxy is build-noise that
        // hides genuine obsoletes.
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("VectorAnimation", "/tmp/VectorAnimation.dylib");
        var protoName = SwiftTypeName.FromModuleQualifiedName("VectorAnimation.DotVectorAnimationCacheProvider");
        module.RegisterType(protoName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("VectorAnimation", "IDotVectorAnimationCacheProvider"),
            SwiftTypeName = protoName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol,
        });
        db.AddModuleDatabase(module);
        var strategy = new ExistentialStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("VectorAnimation.DotVectorAnimationCacheProvider") { IsAny = true },
            new ResolutionContext(db),
            out var result);

        Assert.True(resolved);
        Assert.Equal(TypeDatabaseExtensions.AnyType, result!.Record);
        Assert.Null(result.SyntheticFallback);
    }

    [Fact]
    public void ExistentialStrategy_PlainExistentialOverPATProtocol_KeepsFallback()
    {
        // `any P` where P has associated types degrades to `object` in the existential
        // projection (PAT can't be expressed without type arguments). The fallback
        // annotation stays — the surface IS opaque.
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("Sample", "/tmp/Sample.dylib");
        var protoName = SwiftTypeName.FromModuleQualifiedName("Sample.Container");
        module.RegisterType(protoName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Sample", "IContainer"),
            SwiftTypeName = protoName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.HasAssociatedTypes,
            Kind = TypeRecordKind.Protocol,
        });
        db.AddModuleDatabase(module);
        var strategy = new ExistentialStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Sample.Container") { IsAny = true },
            new ResolutionContext(db),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.SyntheticFallback);
        Assert.Equal("Existential type fallback", result.SyntheticFallback!.Value.Reason);
    }

    [Theory]
    [InlineData("Swift.Sendable")]
    [InlineData("Swift.Escapable")]
    [InlineData("Swift.Copyable")]
    [InlineData("Swift.SendableMetatype")]
    public void ExistentialStrategy_PlainExistentialOverMarkerProtocol_KeepsFallback(string markerName)
    {
        // Marker protocols (Sendable, Escapable, Copyable, SendableMetatype) are
        // stripped by ExistentialHandler.GetEffectiveProtocols, so a single-protocol
        // existential over a marker collapses to `object` rather than `IMarker`.
        // The fallback annotation must fire — the surface IS opaque.
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("Swift", "/tmp/Swift.dylib");
        var protoName = SwiftTypeName.FromModuleQualifiedName(markerName);
        module.RegisterType(protoName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "I" + protoName.Name),
            SwiftTypeName = protoName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol,
        });
        db.AddModuleDatabase(module);
        var strategy = new ExistentialStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec(markerName) { IsAny = true },
            new ResolutionContext(db),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.SyntheticFallback);
        Assert.Equal("Existential type fallback", result.SyntheticFallback!.Value.Reason);
    }

    [Fact]
    public void ExistentialStrategy_PlainExistentialOverObjCBridgedProtocol_KeepsFallback()
    {
        // ObjC-existential-bridged protocols (e.g. `any UIKit.UIScrollViewDelegate`)
        // are stripped by ExistentialHandler.GetEffectiveProtocols. The
        // single-protocol path then sees an empty effective list and returns
        // "object", not `IUIScrollViewDelegate`. The fallback annotation must fire.
        // (UIKit is registered in AppleFrameworkRegistry with the "UI" objcPrefix,
        // so the Apple registry classifies UIKit.UIScrollViewDelegate as ObjC-bridged.)
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("UIKit", "/tmp/UIKit.dylib");
        var protoName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIScrollViewDelegate");
        module.RegisterType(protoName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "IUIScrollViewDelegate"),
            SwiftTypeName = protoName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol,
        });
        db.AddModuleDatabase(module);
        var strategy = new ExistentialStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("UIKit.UIScrollViewDelegate") { IsAny = true },
            new ResolutionContext(db),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.SyntheticFallback);
        Assert.Equal("Existential type fallback", result.SyntheticFallback!.Value.Reason);
    }

    [Fact]
    public void ExistentialStrategy_PlainExistentialUnknownProtocol_KeepsFallback()
    {
        // No TypeRecord for the protocol — projection can't pick `IP`, falls back
        // to `object`. The fallback annotation must fire so the consumer sees the
        // degradation.
        var strategy = new ExistentialStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Unknown.Protocol") { IsAny = true },
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.SyntheticFallback);
    }

    [Fact]
    public void ExistentialStrategy_DoesNotMatchSwiftAny()
    {
        // Swift.Any / Swift.AnyObject are intentionally NOT existentials —
        // SwiftAnyAnyObjectStrategy owns them so they don't surface as fallbacks.
        var strategy = new ExistentialStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Swift.Any"),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Swift.Any")]
    [InlineData("Swift.AnyObject")]
    public void SwiftAnyAnyObjectStrategy_ResolvesToAnyType(string name)
    {
        var strategy = new SwiftAnyAnyObjectStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec(name),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.Equal(TypeDatabaseExtensions.AnyType, result!.Record);
        Assert.Null(result.SyntheticFallback);
    }

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void PointerStrategy_ResolvesPointerTypes(string name)
    {
        var strategy = new PointerStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec(name),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.Equal(TypeDatabaseExtensions.IntPtrType, result!.Record);
    }

    [Theory]
    [InlineData("Foundation.Decimal.Type")]
    [InlineData("Any.Type")]
    [InlineData("Type")]
    public void MetatypeStrategy_ResolvesMetatypes(string name)
    {
        var strategy = new MetatypeStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec(name),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.Equal(TypeDatabaseExtensions.AnyType, result!.Record);
    }

    [Fact]
    public void UnsupportedAppleModuleStrategy_FallsBackToAnyType()
    {
        var strategy = new UnsupportedAppleModuleStrategy();

        // SwiftUI is on the registry's unsupported-module list and there's no
        // registered TypeRecord for SwiftUI.View in the empty database.
        var resolved = strategy.TryResolve(
            new NamedTypeSpec("SwiftUI.View"),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.Equal(TypeDatabaseExtensions.AnyType, result!.Record);
    }

    [Theory]
    [InlineData("Swift.Dictionary")]
    [InlineData("Swift.Array")]
    [InlineData("Swift.Optional")]
    [InlineData("Swift.Result")]
    [InlineData("Swift.Set")]
    [InlineData("Swift.ClosedRange")]
    public void BareGenericGuardStrategy_ResolvesBareGenericsToAnyType(string name)
    {
        var strategy = new BareGenericGuardStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec(name),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.Equal(TypeDatabaseExtensions.AnyType, result!.Record);
    }

    [Fact]
    public void BareGenericGuardStrategy_DoesNotMatchBoundGeneric()
    {
        // Bound-generic TypeSpec carries generic args, so ContainsGenericParameters is true
        // and the guard must NOT fire — DatabaseLookup / SIMD alias / etc. handle these.
        var strategy = new BareGenericGuardStrategy();

        var bound = new NamedTypeSpec("Swift.Dictionary", new TypeSpec[]
        {
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int"),
        });

        var resolved = strategy.TryResolve(
            bound,
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    [Fact]
    public void DatabaseLookupStrategy_HitsRegisteredRecord()
    {
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("Swift", "/tmp/Swift.dylib");
        var doubleName = SwiftTypeName.FromModuleQualifiedName("Swift.Double");
        module.RegisterType(doubleName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
            SwiftTypeName = doubleName,
            MetadataAccessor = "$sSd",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        db.AddModuleDatabase(module);
        var strategy = new DatabaseLookupStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Swift.Double"),
            new ResolutionContext(db),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.Record);
        Assert.Equal("Swift.Double", result.Record!.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void DatabaseLookupStrategy_DeclinesOnMiss()
    {
        var strategy = new DatabaseLookupStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Made.Up.Type"),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    [Fact]
    public void ObjCBridgingStrategy_ProducesSyntheticRecord()
    {
        var strategy = new ObjCBridgingStrategy();

        // UIKit is an auto-bridge module in the registry; UIView is a class type
        // with no registered record, so the strategy synthesizes one.
        var resolved = strategy.TryResolve(
            new NamedTypeSpec("UIKit.UIView"),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.Record);
        Assert.True((result.Record!.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.Equal(TypeRecordKind.Class, result.Record.Kind);
    }

    [Fact]
    public void ObjCBridgingStrategy_SynthesizesMatterClassRecord()
    {
        // Pure-ObjC Matter framework: MatterSupport's MatterAddDeviceRequest.setupPayload
        // is typed as Matter.MTRSetupPayload?. Apple's Matter framework ships no
        // .swiftinterface, so AppleSupplementResolver misses; the ObjC bridging
        // strategy is the path that synthesizes the Class record pointing at
        // global::Matter.MTRSetupPayload. autoBridge:true in apple-frameworks.json
        // is what makes this work.
        var strategy = new ObjCBridgingStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Matter.MTRSetupPayload"),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.Record);
        Assert.True((result.Record!.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.Equal(TypeRecordKind.Class, result.Record.Kind);
        Assert.Equal("Matter", result.Record.CSharpTypeName.Namespace);
        Assert.Equal("MTRSetupPayload", result.Record.CSharpTypeName.Name);
        Assert.Equal("Matter.MTRSetupPayload", result.Record.CSharpTypeName.FullyQualifiedName);
    }

    [Fact]
    public void ObjCBridgingStrategy_DeclinesMatterWiFiValueTypes()
    {
        // The two Matter WiFi types are listed under valueTypes in
        // apple-frameworks.json, so ObjCBridgingStrategy must decline them — they
        // belong to MatterDatabase.xml's DatabaseLookupStrategy path. If the bridge
        // synthesized Class records here, we'd emit references to non-existent
        // global::Matter.MTRNetworkCommissioningWiFi{Band,Security} as classes.
        var strategy = new ObjCBridgingStrategy();

        foreach (var name in new[]
        {
            "Matter.MTRNetworkCommissioningWiFiBand",
            "Matter.MTRNetworkCommissioningWiFiSecurity",
        })
        {
            var resolved = strategy.TryResolve(
                new NamedTypeSpec(name),
                new ResolutionContext(EmptyDatabase()),
                out var result);
            Assert.False(resolved, $"ObjCBridgingStrategy must decline value-typed '{name}'");
            Assert.Null(result);
        }
    }

    [Fact]
    public void AppleSupplementStrategy_ResolvesSupplementOwnedIdentity()
    {
        // Foundation.Locale.Language is registered to TypeOwnerKind.AppleSupplement
        // and ships in the embedded apple-types-manifest, so the strategy must
        // claim it and synthesize a record pointing at the supplement projection.
        // The strategy also marks the identity in AppleSupplementReferences so
        // the project emitter knows to take the SwiftBindings.Apple package
        // dependency on the consuming binding.
        var strategy = new AppleSupplementStrategy();
        var spec = new NamedTypeSpec("Foundation.Locale.Language");

        var resolved = strategy.TryResolve(
            spec,
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.Record);
        Assert.Equal("Foundation.Locale.Language", result.Record!.SwiftTypeName.ModuleQualifiedName);
        Assert.NotNull(result.SupplementReference);
    }

    [Fact]
    public void AppleSupplementStrategy_DeclinesNonSupplementIdentity()
    {
        // Swift.Double is a primitive — never supplement-owned — so the strategy
        // must decline, letting the rest of the dispatch chain handle it.
        var strategy = new AppleSupplementStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Swift.Double"),
            new ResolutionContext(EmptyDatabase()),
            out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    [Fact]
    public void BoundGenericSimdAliasStrategy_ResolvesSimd3FloatToSimdAlias()
    {
        // Bound-generic SIMD3<Float> aliases to the simd_float3 typedef. The
        // strategy depends on the alias's underlying record being present in
        // the database; register it inline so the test isolates the strategy
        // from SimdDatabase.xml.
        var db = MakeDbWithSimdFloat3();
        var strategy = new BoundGenericSimdAliasStrategy();

        var bound = new NamedTypeSpec("Swift.SIMD3", new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Float"),
        });

        var resolved = strategy.TryResolve(
            bound,
            new ResolutionContext(db),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.Record);
        Assert.Equal("simd.simd_float3", result.Record!.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void BoundGenericSimdAliasStrategy_DeclinesUnboundSimd3()
    {
        // The bare-generic Swift.SIMD3 (no element type) is NOT a SIMD alias —
        // it must fall through to the BareGenericGuard / DB lookup path.
        var strategy = new BoundGenericSimdAliasStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Swift.SIMD3"),
            new ResolutionContext(MakeDbWithSimdFloat3()),
            out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    private static TypeDatabase MakeDbWithSimdFloat3()
    {
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("simd", "/tmp/simd.dylib");
        var name = SwiftTypeName.FromModuleQualifiedName("simd.simd_float3");
        module.RegisterType(name, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System.Numerics", "Vector3"),
            SwiftTypeName = name,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
            Kind = TypeRecordKind.Struct,
        });
        db.AddModuleDatabase(module);
        return db;
    }
}
