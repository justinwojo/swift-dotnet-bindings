// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for <see cref="KeyPathBagWalker.CollectValueTypeAvailability"/>, the shared
/// helper both KeyPath trampoline emitters (singleton + EntityProperty factory) use to lift a
/// trampoline's <c>@available</c> floor to include the KeyPath <c>Value</c> type's own floor.
///
/// <para>The end-to-end behaviour — a Value gated above the conformer/dependency floor keeps its
/// <c>@_cdecl</c> trampoline from being stripped at wrapper build — is pinned by the gated
/// <c>MockReleaseInfo</c> BindingTests fixture. These tests pin the helper's structural contract
/// that BindingTests can't cheaply express per case: it resolves a Value's record availability,
/// recurses into generic arguments / tuple elements / closure arg+return, and contributes nothing
/// for un-annotated or unresolved types.</para>
/// </summary>
public class KeyPathBagWalkerAvailabilityTests
{
    private static readonly AvailabilityAnnotation iOS18 = new(
        Platform: "iOS",
        IntroducedVersion: "18.0",
        DeprecatedVersion: null,
        ObsoletedVersion: null,
        IsUnconditionallyDeprecated: false,
        IsUnconditionallyUnavailable: false,
        Message: null,
        Renamed: null);

    private static readonly AvailabilityAnnotation macOS15 = new(
        Platform: "macOS",
        IntroducedVersion: "15.0",
        DeprecatedVersion: null,
        ObsoletedVersion: null,
        IsUnconditionallyDeprecated: false,
        IsUnconditionallyUnavailable: false,
        Message: null,
        Renamed: null);

    [Fact]
    public void GatedNamedType_SurfacesItsAvailability()
    {
        var db = new FakeTypeDatabase();
        db.Register("TestModule.GatedValue", new[] { iOS18, macOS15 });

        var result = KeyPathBagWalker.CollectValueTypeAvailability(
            new NamedTypeSpec("TestModule.GatedValue"), db);

        Assert.NotNull(result);
        Assert.Contains(iOS18, result!);
        Assert.Contains(macOS15, result!);
    }

    [Fact]
    public void UnannotatedType_ContributesNothing()
    {
        // A stdlib/primitive Value (String, Int) resolves to a record with no availability.
        var db = new FakeTypeDatabase();
        db.Register("Swift.String", availability: null);

        var result = KeyPathBagWalker.CollectValueTypeAvailability(
            new NamedTypeSpec("Swift.String"), db);

        Assert.Null(result);
    }

    [Fact]
    public void UnresolvedType_ReturnsNull()
    {
        // Nothing registered → no record → no contribution (and no throw).
        var db = new FakeTypeDatabase();

        var result = KeyPathBagWalker.CollectValueTypeAvailability(
            new NamedTypeSpec("TestModule.Unknown"), db);

        Assert.Null(result);
    }

    [Fact]
    public void GenericArgument_SurfacesInnerFloor()
    {
        // Optional<GatedValue>: the outer Optional carries no availability, but the gated
        // generic argument must still lift the floor.
        var db = new FakeTypeDatabase();
        db.Register("TestModule.GatedValue", new[] { iOS18 });

        var optionalOfGated = new NamedTypeSpec(
            "Swift.Optional", new NamedTypeSpec("TestModule.GatedValue"));

        var result = KeyPathBagWalker.CollectValueTypeAvailability(optionalOfGated, db);

        Assert.NotNull(result);
        Assert.Contains(iOS18, result!);
    }

    [Fact]
    public void TupleElement_SurfacesInnerFloor()
    {
        // (GatedValue, Int): a tuple Value must walk its elements.
        var db = new FakeTypeDatabase();
        db.Register("TestModule.GatedValue", new[] { iOS18 });

        var tuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("TestModule.GatedValue"),
            new NamedTypeSpec("Swift.Int"),
        });

        var result = KeyPathBagWalker.CollectValueTypeAvailability(tuple, db);

        Assert.NotNull(result);
        Assert.Contains(iOS18, result!);
    }

    [Fact]
    public void ExistentialProtocolList_SurfacesMemberFloor()
    {
        // any P & GatedProtocol: the trampoline names the composition, so a gated member
        // protocol must lift the floor. The Value is a ProtocolListTypeSpec whose members
        // are NamedTypeSpec.
        var db = new FakeTypeDatabase();
        db.Register("TestModule.GatedProtocol", new[] { iOS18 });
        db.Register("TestModule.PlainProtocol", availability: null);

        var existential = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.PlainProtocol"),
            new NamedTypeSpec("TestModule.GatedProtocol"),
        });

        var result = KeyPathBagWalker.CollectValueTypeAvailability(existential, db);

        Assert.NotNull(result);
        Assert.Contains(iOS18, result!);
    }

    [Fact]
    public void ClosureArgumentAndReturn_SurfaceInnerFloor()
    {
        // (GatedArg) -> GatedReturn: both the argument and return specs must be walked.
        var db = new FakeTypeDatabase();
        db.Register("TestModule.GatedArg", new[] { iOS18 });
        db.Register("TestModule.GatedReturn", new[] { macOS15 });

        var closure = new ClosureTypeSpec(
            arguments: new NamedTypeSpec("TestModule.GatedArg"),
            returnType: new NamedTypeSpec("TestModule.GatedReturn"));

        var result = KeyPathBagWalker.CollectValueTypeAvailability(closure, db);

        Assert.NotNull(result);
        Assert.Contains(iOS18, result!);
        Assert.Contains(macOS15, result!);
    }

    /// <summary>
    /// Minimal <see cref="ITypeDatabase"/> double. <see cref="KeyPathBagWalker.CollectValueTypeAvailability"/>
    /// resolves named specs through <c>TypeResolver.Default</c>'s <c>DatabaseLookupStrategy</c>, which keys
    /// on <see cref="SwiftTypeName"/>; registering by module-qualified name is enough for it to find records.
    /// </summary>
    private sealed class FakeTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _records = new();

        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _records.ContainsKey(swiftTypeName.ToString());
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(true)] out TypeRecord? record)
            => _records.TryGetValue(swiftTypeName.ToString(), out record);
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }

        public void Register(string moduleQualifiedName, IReadOnlyList<AvailabilityAnnotation>? availability)
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName);
            _records[swiftTypeName.ToString()] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestNs", swiftTypeName.Name),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
                AvailabilityAnnotations = availability,
            };
        }
    }
}
