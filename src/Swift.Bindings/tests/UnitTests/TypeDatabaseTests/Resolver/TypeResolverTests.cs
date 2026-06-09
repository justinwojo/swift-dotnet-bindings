// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Resolver-core contract: dispatch order, skip-reason propagation,
/// argument validation, and the throw-shape of <see cref="TypeResolver.Resolve"/>.
/// </summary>
public class TypeResolverTests
{
    private sealed class FixedStrategy : IResolutionStrategy
    {
        private readonly Func<TypeSpec, ResolutionContext, TypeResolutionResult> _factory;
        private readonly Func<TypeSpec, bool> _claims;
        public int InvocationCount { get; private set; }

        public FixedStrategy(string name, Func<TypeSpec, bool> claims, Func<TypeSpec, ResolutionContext, TypeResolutionResult> factory)
        {
            Name = name;
            _claims = claims;
            _factory = factory;
        }

        public string Name { get; }

        public bool TryResolve(TypeSpec typeSpec, ResolutionContext context, [NotNullWhen(true)] out TypeResolutionResult? result)
        {
            InvocationCount++;
            if (_claims(typeSpec))
            {
                result = _factory(typeSpec, context);
                return true;
            }
            result = null;
            return false;
        }
    }

    private static TypeRecord MakeRecord(string fqName) => new()
    {
        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Tests", fqName),
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Tests.{fqName}"),
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.Frozen,
        Kind = TypeRecordKind.Struct,
    };

    [Fact]
    public void TryResolve_FirstMatchingStrategyWins()
    {
        var first = MakeRecord("First");
        var second = MakeRecord("Second");

        var firstStrategy = new FixedStrategy(
            "First",
            _ => true,
            (_, _) => new TypeResolutionResult(first));
        var secondStrategy = new FixedStrategy(
            "Second",
            _ => true,
            (_, _) => new TypeResolutionResult(second));

        var resolver = new TypeResolver(new IResolutionStrategy[] { firstStrategy, secondStrategy });
        var resolved = resolver.TryResolve(new NamedTypeSpec("X"), new ResolutionContext(new TypeDatabase()), out var result);

        Assert.True(resolved);
        Assert.NotNull(result);
        Assert.Same(first, result!.Record);
        Assert.Equal(1, firstStrategy.InvocationCount);
        Assert.Equal(0, secondStrategy.InvocationCount);
    }

    [Fact]
    public void TryResolve_FallsThroughWhenStrategyDeclines()
    {
        var match = MakeRecord("Match");

        var skipping = new FixedStrategy("Skip", _ => false, (_, _) => throw new InvalidOperationException());
        var matching = new FixedStrategy("Match", _ => true, (_, _) => new TypeResolutionResult(match));

        var resolver = new TypeResolver(new IResolutionStrategy[] { skipping, matching });
        var resolved = resolver.TryResolve(new NamedTypeSpec("X"), new ResolutionContext(new TypeDatabase()), out var result);

        Assert.True(resolved);
        Assert.Same(match, result!.Record);
        Assert.Equal(1, skipping.InvocationCount);
        Assert.Equal(1, matching.InvocationCount);
    }

    [Fact]
    public void TryResolve_NoMatchReturnsFalse()
    {
        var resolver = new TypeResolver(Array.Empty<IResolutionStrategy>());

        var resolved = resolver.TryResolve(new NamedTypeSpec("X"), new ResolutionContext(new TypeDatabase()), out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ThrowsWhenNoStrategyMatches()
    {
        var resolver = new TypeResolver(Array.Empty<IResolutionStrategy>());

        Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(new NamedTypeSpec("X"), new ResolutionContext(new TypeDatabase())));
    }

    [Fact]
    public void Resolve_ThrowsWhenStrategyClaimsButProducesNoRecord()
    {
        // Skip-style outcomes (Record == null) must not satisfy the
        // "resolve or throw" contract — callers that hard-require a record
        // expect an exception, not a skip envelope.
        var skipping = new FixedStrategy(
            "Skipping",
            _ => true,
            (_, _) => new TypeResolutionResult(Record: null, SkipReason: "deferred"));

        var resolver = new TypeResolver(new[] { skipping });

        Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(new NamedTypeSpec("X"), new ResolutionContext(new TypeDatabase())));
    }

    [Fact]
    public void TryResolve_PropagatesSkipReasonAndProvenance()
    {
        var skipping = new FixedStrategy(
            "Skipping",
            _ => true,
            (_, _) => new TypeResolutionResult(
                Record: null,
                SkipReason: "intentional-skip",
                Provenance: new ResolutionProvenance("strategy:Skipping")));

        var resolver = new TypeResolver(new[] { skipping });
        var resolved = resolver.TryResolve(new NamedTypeSpec("X"), new ResolutionContext(new TypeDatabase()), out var result);

        Assert.True(resolved);
        Assert.NotNull(result);
        Assert.Null(result!.Record);
        Assert.False(result.IsResolved);
        Assert.Equal("intentional-skip", result.SkipReason);
        Assert.Equal("strategy:Skipping", result.Provenance!.Source);
    }

    [Fact]
    public void Constructor_NullStrategies_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TypeResolver(null!));
    }

    [Fact]
    public void TryResolve_NullArgs_Throws()
    {
        var resolver = new TypeResolver(Array.Empty<IResolutionStrategy>());

        Assert.Throws<ArgumentNullException>(() =>
            resolver.TryResolve(null!, new ResolutionContext(new TypeDatabase()), out _));
        Assert.Throws<ArgumentNullException>(() =>
            resolver.TryResolve(new NamedTypeSpec("X"), null!, out _));
    }

    [Fact]
    public void Default_RegistersFullStrategyChainInDispatchOrder()
    {
        // The dispatch order is correctness-sensitive — Metatype must precede
        // Existential (otherwise nested-form metatypes get classified as
        // existential fallback), AppleSupplement must precede DatabaseLookup
        // (so Apple supplement projections take priority over any incidental
        // module DB entry of the same name), and BareGenericGuard must precede
        // BoundGenericSimdAlias (the bare guard fires only when no generic args
        // are present, so the ordering matters only in tandem with SIMD's
        // bound-generic claim contract). Pin the full sequence so a regression
        // surfaces here and not as a parity test failure two screens away.
        var names = TypeResolver.Default.Strategies.Select(s => s.Name).ToArray();

        Assert.Equal(new[]
        {
            "DynamicSelf",
            "GenericParameter",
            "PrimitiveAlias",
            "Metatype",
            "Existential",
            "SwiftAnyAnyObject",
            "Pointer",
            "UnsupportedAppleModule",
            "BareGenericGuard",
            "BoundGenericSimdAlias",
            "AppleSupplement",
            "DatabaseLookup",
            "ObjCBridging",
        }, names);
    }

    [Theory]
    [InlineData("Metatype", "Existential")]              // legacy GetTypeRecordOrThrow + TryGetAnyTypeFallbackInfo ordering
    [InlineData("AppleSupplement", "DatabaseLookup")]    // supplement projections win over incidental DB entries
    [InlineData("BareGenericGuard", "BoundGenericSimdAlias")] // bare guard short-circuits before SIMD bound-generic claim
    [InlineData("SwiftAnyAnyObject", "Pointer")]         // Swift.Any/AnyObject claimed before pointer fallback
    public void Default_RelativeOrdering_HoldsForCorrectnessCriticalPairs(string earlier, string later)
    {
        var names = TypeResolver.Default.Strategies.Select(s => s.Name).ToArray();

        var earlierIndex = Array.IndexOf(names, earlier);
        var laterIndex = Array.IndexOf(names, later);

        Assert.True(earlierIndex >= 0, $"Strategy '{earlier}' missing from Default. Got: [{string.Join(", ", names)}]");
        Assert.True(laterIndex >= 0, $"Strategy '{later}' missing from Default. Got: [{string.Join(", ", names)}]");
        Assert.True(earlierIndex < laterIndex,
            $"Expected '{earlier}' (index {earlierIndex}) to precede '{later}' (index {laterIndex}).");
    }

    [Fact]
    public void DynamicSelfStrategy_ResolvesSelfToAnyType()
    {
        var strategy = new DynamicSelfStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Self"),
            new ResolutionContext(new TypeDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.Equal(TypeDatabaseExtensions.AnyType, result!.Record);
        Assert.Equal("strategy:DynamicSelf", result.Provenance!.Source);
    }

    [Fact]
    public void DynamicSelfStrategy_DoesNotMatchUnrelatedType()
    {
        var strategy = new DynamicSelfStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Foundation.NSObject"),
            new ResolutionContext(new TypeDatabase()),
            out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("τ_0_0")]
    [InlineData("τ_1_0")]
    [InlineData("T")]
    [InlineData("U")]
    [InlineData("T0")]
    public void GenericParameterStrategy_ResolvesGenericNamesToAnyType(string name)
    {
        var strategy = new GenericParameterStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec(name),
            new ResolutionContext(new TypeDatabase()),
            out var result);

        Assert.True(resolved);
        Assert.Equal(TypeDatabaseExtensions.AnyType, result!.Record);
        Assert.Equal("strategy:GenericParameter", result.Provenance!.Source);
    }

    [Theory]
    [InlineData("Element")] // Long conventional name — not classified as a generic param by the helper.
    [InlineData("Foundation.Locale")]
    [InlineData("Self.Hello")]
    public void GenericParameterStrategy_DoesNotMatchLongerNames(string name)
    {
        var strategy = new GenericParameterStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec(name),
            new ResolutionContext(new TypeDatabase()),
            out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    [Fact]
    public void PrimitiveAliasStrategy_ResolvesTimeIntervalToDouble()
    {
        // Foundation.TimeInterval is the only registered alias today (Swift.Double).
        // Load the SwiftDatabase so the underlying primitive lookup succeeds.
        var typeDatabase = LoadDatabaseWithSwiftDouble();
        var strategy = new PrimitiveAliasStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Foundation.TimeInterval"),
            new ResolutionContext(typeDatabase),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.Record);
        Assert.Equal("Swift.Double", result.Record!.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal("strategy:PrimitiveAlias", result.Provenance!.Source);
    }

    [Fact]
    public void PrimitiveAliasStrategy_FallsThroughWhenUnderlyingMissing()
    {
        // Without Swift.Double loaded the strategy must DECLINE — semantics
        // identical to the legacy TryResolvePrimitiveTypeAlias helper.
        var strategy = new PrimitiveAliasStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Foundation.TimeInterval"),
            new ResolutionContext(new TypeDatabase()),
            out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    [Fact]
    public void PrimitiveAliasStrategy_DoesNotMatchUnknownAlias()
    {
        var strategy = new PrimitiveAliasStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Foundation.NotARealAlias"),
            new ResolutionContext(LoadDatabaseWithSwiftDouble()),
            out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    [Fact]
    public void PrimitiveAliasStrategy_ResolvesOSStatusToInt32()
    {
        // Darwin.OSStatus is a typealias for Int32 in Apple's Darwin overlay. The
        // swiftinterface scanner never materializes it as a type record, so without
        // this strategy the closure-parameter gate at ClosureHandler.cs rejects every
        // signature that names it — eg RealityFoundation's AudioGenerator PlayAudio
        // render handler.
        var typeDatabase = LoadDatabaseWithPrimitive("Swift.Int32", "Int32", "$ss5Int32V");
        var strategy = new PrimitiveAliasStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("Darwin.OSStatus"),
            new ResolutionContext(typeDatabase),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.Record);
        Assert.Equal("Swift.Int32", result.Record!.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal("strategy:PrimitiveAlias", result.Provenance!.Source);
    }

    [Fact]
    public void PrimitiveAliasStrategy_ResolvesAVAudioFrameCountToUInt32()
    {
        // AVFAudio.AVAudioFrameCount is a typealias for UInt32. Companion to OSStatus
        // — both gate the same PlayAudio/PrepareAudio render handler signature.
        var typeDatabase = LoadDatabaseWithPrimitive("Swift.UInt32", "UInt32", "$ss6UInt32V");
        var strategy = new PrimitiveAliasStrategy();

        var resolved = strategy.TryResolve(
            new NamedTypeSpec("AVFAudio.AVAudioFrameCount"),
            new ResolutionContext(typeDatabase),
            out var result);

        Assert.True(resolved);
        Assert.NotNull(result!.Record);
        Assert.Equal("Swift.UInt32", result.Record!.SwiftTypeName.ModuleQualifiedName);
    }

    private static TypeDatabase LoadDatabaseWithSwiftDouble()
        => LoadDatabaseWithPrimitive("Swift.Double", "Double", "$sSd");

    private static TypeDatabase LoadDatabaseWithPrimitive(string swiftQualified, string netName, string metadataAccessor)
    {
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("Swift", "/tmp/Swift.dylib");
        var name = SwiftTypeName.FromModuleQualifiedName(swiftQualified);
        module.RegisterType(name, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", netName),
            SwiftTypeName = name,
            MetadataAccessor = metadataAccessor,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        db.AddModuleDatabase(module);
        return db;
    }
}
