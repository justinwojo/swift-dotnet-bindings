// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Parity between the new <see cref="TypeResolver"/> and the legacy
/// <see cref="TypeDatabaseExtensions"/> entry points for the three M4
/// Session 1 strategies (dynamic self, generic parameter, primitive alias).
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
}
