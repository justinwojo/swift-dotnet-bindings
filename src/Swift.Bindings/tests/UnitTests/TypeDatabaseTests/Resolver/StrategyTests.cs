// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Per-strategy unit tests for the M4 Session 2 migration. Each strategy is
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
