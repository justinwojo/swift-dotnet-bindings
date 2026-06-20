// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Parity between the new <see cref="TypeResolver"/> and the legacy
/// <see cref="TypeDatabaseExtensions"/> entry points for the three
/// resolver strategies (dynamic self, generic parameter, primitive alias).
/// Each test routes the same <see cref="TypeSpec"/> through both paths and
/// asserts the records (and supporting flags) match.
/// </summary>
public class ResolverParityTests
{
    private static TypeDatabase MakeDbWithSwiftDouble()
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
        return db;
    }

    [Theory]
    [InlineData("Self")]                      // dynamic self
    [InlineData("τ_0_0")]                     // canonical generic param
    [InlineData("T")]                         // short generic-name convention
    [InlineData("Foundation.TimeInterval")]   // primitive alias
    public void TryGetTypeRecord_AgreesWithResolverDirect(string typeName)
    {
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec(typeName);

        var legacy = db.TryGetTypeRecord(spec, out var legacyRecord);
        var resolved = TypeResolver.Default.TryResolve(spec, new ResolutionContext(db), out var resolverResult);

        Assert.True(legacy);
        Assert.True(resolved);
        Assert.NotNull(legacyRecord);
        Assert.NotNull(resolverResult);
        Assert.Equal(legacyRecord, resolverResult!.Record);
    }

    [Theory]
    [InlineData("Self")]
    [InlineData("τ_0_0")]
    [InlineData("T")]
    [InlineData("Foundation.TimeInterval")]
    public void GetTypeRecordOrAnyType_AgreesWithResolverDirect(string typeName)
    {
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec(typeName);

        var legacy = db.GetTypeRecordOrAnyType(spec);
        TypeResolver.Default.TryResolve(spec, new ResolutionContext(db), out var resolverResult);

        Assert.NotNull(resolverResult);
        Assert.Equal(legacy, resolverResult!.Record);
    }

    [Theory]
    [InlineData("Self")]
    [InlineData("τ_0_0")]
    [InlineData("T")]
    [InlineData("Foundation.TimeInterval")]
    public void GetTypeRecordOrThrow_AgreesWithResolverDirect(string typeName)
    {
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec(typeName);

        var legacy = db.GetTypeRecordOrThrow(spec);
        TypeResolver.Default.TryResolve(spec, new ResolutionContext(db), out var resolverResult);

        Assert.NotNull(resolverResult);
        Assert.Equal(legacy, resolverResult!.Record);
    }

    [Theory]
    [InlineData("Self")]
    [InlineData("τ_0_0")]
    [InlineData("T")]
    [InlineData("Foundation.TimeInterval")]
    public void IsTypeProcessed_AgreesWithResolverDirect(string typeName)
    {
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec(typeName);

        var legacy = db.IsTypeProcessed(spec);
        var resolved = TypeResolver.Default.TryResolve(spec, new ResolutionContext(db), out _);

        Assert.True(legacy);
        Assert.True(resolved);
    }

    [Theory]
    [InlineData("Self")]
    [InlineData("τ_0_0")]
    [InlineData("T")]
    [InlineData("Foundation.TimeInterval")]
    public void TryGetAnyTypeFallbackInfo_IsNotFallbackForMigratedStrategies(string typeName)
    {
        // The migrated strategies all map intentionally — the resolver must
        // suppress the legacy "Type is missing from the type database" path.
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec(typeName);

        var isFallback = db.TryGetAnyTypeFallbackInfo(spec, out var info);

        Assert.False(isFallback);
        Assert.Null(info);
    }

    [Fact]
    public void PrimitiveAlias_FallsThroughLegacyWhenUnderlyingPrimitiveMissing()
    {
        // Mirrors the pre-migration behaviour of TryResolvePrimitiveTypeAlias:
        // when Swift.Double is not registered, the alias must NOT resolve
        // through the resolver — callers fall through to the next legacy
        // stage (ObjC bridge / SwiftTypeName lookup) for backwards compat.
        var emptyDb = new TypeDatabase();
        var spec = new NamedTypeSpec("Foundation.TimeInterval");

        var resolved = TypeResolver.Default.TryResolve(spec, new ResolutionContext(emptyDb), out var resolverResult);

        Assert.False(resolved);
        Assert.Null(resolverResult);
    }

    // -------------------------------------------------------------------------
    // Parity for the strategies migrated to the resolver.
    // The legacy entry points are now thin shims over the resolver, so each
    // pair of asserts proves that the entry-point projection matches a direct
    // resolver call. Together they prove the four overloads carry no
    // independent logic — the single-path policy.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("any Swift.Encoder")]   // existential
    [InlineData("Swift.Any")]            // Swift.Any short-circuit
    [InlineData("Swift.AnyObject")]      // Swift.AnyObject short-circuit
    [InlineData("Swift.OpaquePointer")]  // pointer
    [InlineData("Foundation.Decimal.Type")] // metatype
    [InlineData("SwiftUI.View")]         // unsupported Apple module
    [InlineData("Swift.Dictionary")]     // bare-generic guard
    [InlineData("UIKit.UIView")]         // ObjC bridging
    [InlineData("Foundation.Locale.Language")] // Apple supplement-owned identity
    public void EntryPoints_AgreeWithResolverDirect(string typeName)
    {
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec(typeName);
        var ctx = new ResolutionContext(db);

        TypeResolver.Default.TryResolve(spec, ctx, out var direct);

        var orAnyType = db.GetTypeRecordOrAnyType(spec);
        var hasRecord = db.TryGetTypeRecord(spec, out var record);
        var processed = db.IsTypeProcessed(spec);

        Assert.NotNull(direct);
        Assert.NotNull(direct!.Record);
        Assert.Equal(direct.Record, orAnyType);
        Assert.True(hasRecord);
        Assert.Equal(direct.Record, record);
        Assert.True(processed);
    }

    [Fact]
    public void IsTypeProcessed_NowAgreesWithTryGetTypeRecordOnSupplementOwnedIdentity()
    {
        // Pin the single-path widening called out in TypeResolver.Default's
        // docstring: legacy IsTypeProcessed(NamedTypeSpec) only consulted the
        // module DB / module-alias / Apple-umbrella paths, so supplement-owned
        // identities like Foundation.Locale.Language disagreed with
        // TryGetTypeRecord (which DID call AppleSupplementResolver). The
        // resolver-based shim aligns the two — anything the resolver claims
        // is, by definition, processed.
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec("Foundation.Locale.Language");

        Assert.True(db.IsTypeProcessed(spec));
        Assert.True(db.TryGetTypeRecord(spec, out var record));
        Assert.NotNull(record);
        Assert.Equal("Foundation.Locale.Language", record!.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void EntryPoints_AgreeWithResolverDirect_BoundSimd3()
    {
        // Bound-generic SIMD3<Float> goes through BoundGenericSimdAliasStrategy,
        // which depends on simd.simd_float3 being present in the database. The
        // [InlineData] form can't carry generic parameters, so this case is a
        // standalone Fact; the assertions match EntryPoints_AgreeWithResolverDirect.
        var db = MakeDbWithSwiftDouble();
        var simdModule = new ModuleTypeDatabase("simd", "/tmp/simd.dylib");
        var aliasName = SwiftTypeName.FromModuleQualifiedName("simd.simd_float3");
        simdModule.RegisterType(aliasName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System.Numerics", "Vector3"),
            SwiftTypeName = aliasName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
            Kind = TypeRecordKind.Struct,
        });
        db.AddModuleDatabase(simdModule);

        var spec = new NamedTypeSpec("Swift.SIMD3", new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Float"),
        });
        var ctx = new ResolutionContext(db);

        TypeResolver.Default.TryResolve(spec, ctx, out var direct);

        var orAnyType = db.GetTypeRecordOrAnyType(spec);
        var hasRecord = db.TryGetTypeRecord(spec, out var record);
        var processed = db.IsTypeProcessed(spec);

        Assert.NotNull(direct);
        Assert.NotNull(direct!.Record);
        Assert.Equal("simd.simd_float3", direct.Record!.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal(direct.Record, orAnyType);
        Assert.True(hasRecord);
        Assert.Equal(direct.Record, record);
        Assert.True(processed);
    }

    [Fact]
    public void Existential_FallbackInfoFlowsThroughResolver()
    {
        // The existential strategy is the only strategy that sets
        // SyntheticFallback. The shim entry point reads it directly, so the
        // existential fallback message must round-trip without any legacy
        // re-classification logic.
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec("any Swift.Encoder");

        var fallback = db.TryGetAnyTypeFallbackInfo(spec, out var info);

        Assert.True(fallback);
        Assert.NotNull(info);
        Assert.Equal("Existential type fallback", info!.Value.Reason);
    }

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Foundation.Decimal.Type")]
    [InlineData("SwiftUI.View")]
    [InlineData("Swift.Dictionary")]
    [InlineData("UIKit.UIView")]
    [InlineData("Swift.Any")]
    [InlineData("Swift.AnyObject")]
    public void IntentionalResolutions_AreNotFallback(string typeName)
    {
        // Every resolution that produces a real record without setting
        // SyntheticFallback must NOT show up as a fallback diagnostic. This
        // is the contract that closes the legacy "Type is missing from the
        // type database" drift for Swift.Any and friends.
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec(typeName);

        var fallback = db.TryGetAnyTypeFallbackInfo(spec, out var info);

        Assert.False(fallback);
        Assert.Null(info);
    }

    [Fact]
    public void SinglePathPolicy_LegacyEntryPointsBodiesAreResolverCalls()
    {
        // Single-path policy: the four legacy NamedTypeSpec overloads must
        // collapse into resolver calls. A type that the resolver cannot claim
        // must reach the canonical "missing from database" / throw / false
        // failure modes — there must be no leftover inline branch making a
        // separate decision. The "Made.Up.Type" identity below misses every
        // strategy in the chain (no DB record, not ObjC, not Apple-unsupported,
        // not a metatype, …) and exercises the failure surface of each
        // overload. "MissingType" name is deliberate — anything ending in
        // ".Type" would route through MetatypeStrategy.
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec("MadeUp.MissingIdentity");
        var ctx = new ResolutionContext(db);

        Assert.False(TypeResolver.Default.TryResolve(spec, ctx, out _));

        Assert.False(db.IsTypeProcessed(spec));
        Assert.False(db.TryGetTypeRecord(spec, out var maybeRecord));
        Assert.Null(maybeRecord);
        Assert.Equal(TypeDatabaseExtensions.AnyType, db.GetTypeRecordOrAnyType(spec));
        Assert.Throws<Exception>(() => db.GetTypeRecordOrThrow(spec));
        Assert.True(db.TryGetAnyTypeFallbackInfo(spec, out var fallback));
        Assert.NotNull(fallback);
        Assert.Equal("Type is missing from the type database", fallback!.Value.Reason);
    }

    // -------------------------------------------------------------------------
    // F10 Stage 17 — direct-strategy parity for the remaining raw-name cascade
    // arms (4 out-of-module, 5 cross-module alias, 6 Swift.Error). Each new
    // strategy is invoked DIRECTLY — not through Default, where it is shadowed by
    // DatabaseLookupStrategy until the Stage 18 split — and asserted to reproduce
    // its inline arm in TryGetTypeRecordWithoutSupplement, record for record.
    // -------------------------------------------------------------------------

    [Fact]
    public void OutOfModuleLookupStrategy_MirrorsArm4()
    {
        var db = MakeDbWithSwiftDouble();
        // Register an out-of-module type: no loaded module DB for "Ghost", so
        // UpdateTypeRecord falls through to the _outOfModuleTypes cache (arm 4).
        var name = SwiftTypeName.FromModuleQualifiedName("Ghost.Spectre");
        db.UpdateTypeRecord(name, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Ghost", "Spectre"),
            SwiftTypeName = name,
            MetadataAccessor = "$sGhost",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });

        var spec = new NamedTypeSpec("Ghost.Spectre");
        var strategy = new OutOfModuleLookupStrategy();

        var claimed = strategy.TryResolve(spec, new ResolutionContext(db), out var result);
        var armHit = db.TryGetTypeRecordWithoutSupplement(name, out var armRecord);

        Assert.True(armHit);
        Assert.True(claimed);
        Assert.NotNull(result);
        Assert.Equal(armRecord, result!.Record);
        Assert.Equal("strategy:OutOfModuleLookup", result.Provenance!.Source);
    }

    [Fact]
    public void CrossModuleAliasStrategy_MirrorsArm5()
    {
        var db = MakeDbWithSwiftDouble();
        // Register the canonical base ManagedSettings.Token so the built-in
        // FamilyControls.ApplicationToken alias (→ ManagedSettings.Token<…>)
        // resolves once the strategy strips the generic argument.
        var module = new ModuleTypeDatabase("ManagedSettings", "/tmp/ManagedSettings.dylib");
        var tokenName = SwiftTypeName.FromModuleQualifiedName("ManagedSettings.Token");
        module.RegisterType(tokenName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ManagedSettings", "Token"),
            SwiftTypeName = tokenName,
            MetadataAccessor = "$sTok",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        db.AddModuleDatabase(module);

        var aliasName = SwiftTypeName.FromModuleQualifiedName("FamilyControls.ApplicationToken");
        var spec = new NamedTypeSpec("FamilyControls.ApplicationToken");
        var strategy = new CrossModuleAliasStrategy();

        var claimed = strategy.TryResolve(spec, new ResolutionContext(db), out var result);
        var armHit = db.TryGetTypeRecordWithoutSupplement(aliasName, out var armRecord);

        Assert.True(armHit);
        Assert.True(claimed);
        Assert.NotNull(result);
        Assert.Equal(armRecord, result!.Record);
        Assert.Equal("ManagedSettings.Token", result.Record!.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal("strategy:CrossModuleAlias", result.Provenance!.Source);
    }

    [Fact]
    public void SwiftErrorStrategy_MirrorsArm6()
    {
        var db = MakeDbWithSwiftDouble();
        var name = SwiftTypeName.FromModuleQualifiedName("Swift.Error");
        var spec = new NamedTypeSpec("Swift.Error");
        var strategy = new SwiftErrorStrategy();

        var claimed = strategy.TryResolve(spec, new ResolutionContext(db), out var result);
        var armHit = db.TryGetTypeRecordWithoutSupplement(name, out var armRecord);

        Assert.True(armHit);
        Assert.True(claimed);
        Assert.NotNull(result);
        Assert.Equal(armRecord, result!.Record);
        Assert.Equal(TypeDatabaseExtensions.SwiftErrorType, result.Record);
        Assert.Equal("strategy:SwiftError", result.Provenance!.Source);
    }

    [Fact]
    public void Stage17Strategies_AreShadowedInDefault_IdentityUnchanged()
    {
        // The three new strategies are registered AFTER DatabaseLookupStrategy,
        // which still black-boxes arms 2–6 at Stage 17. So in Default every name
        // they could claim is claimed upstream by DatabaseLookup — proven by the
        // winning provenance. This is the Stage 17 identity-parity guarantee at
        // the unit layer: adding the strategies changes neither what Default
        // resolves nor which strategy wins. Swift.Error is the sharpest probe —
        // arm 6's record only exists via the cascade, yet DatabaseLookup (not the
        // new SwiftErrorStrategy) must still be the claimant.
        var db = MakeDbWithSwiftDouble();
        var spec = new NamedTypeSpec("Swift.Error");

        var resolved = TypeResolver.Default.TryResolve(spec, new ResolutionContext(db), out var result);

        Assert.True(resolved);
        Assert.NotNull(result);
        Assert.Equal(TypeDatabaseExtensions.SwiftErrorType, result!.Record);
        Assert.Equal("strategy:DatabaseLookup", result.Provenance!.Source);
    }
}
