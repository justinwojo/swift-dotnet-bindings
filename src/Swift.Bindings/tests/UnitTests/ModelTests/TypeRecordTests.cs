// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Exercises the three-aspect split introduced to <see cref="TypeRecord"/> for the Apple
/// supplement work (see <c>src/docs/apple-swift-types-architecture.md</c> §"Implementation
/// specifics" item 5). Swift identity / managed projection / ABI carrier must all be
/// addressable as first-class properties, with the projection and carrier defaulting to
/// <c>CSharpTypeName</c> when not explicitly overridden so existing call sites are unaffected.
/// </summary>
public class TypeRecordTests
{
    private static TypeRecord NewRecord(CSharpTypeName csharp, CSharpTypeName? projection = null, CSharpTypeName? carrier = null)
    {
        return new TypeRecord
        {
            CSharpTypeName = csharp,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Locale"),
            MetadataAccessor = "$s10Foundation6LocaleVMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct,
            ManagedProjectionTypeName = projection,
            AbiCarrierTypeName = carrier,
        };
    }

    [Fact]
    public void SwiftIdentity_AliasesSwiftTypeName()
    {
        var record = NewRecord(CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "Locale"));

        Assert.Same(record.SwiftTypeName, record.SwiftIdentity);
        Assert.Equal("Foundation.Locale", record.SwiftIdentity.ModuleQualifiedName);
    }

    [Fact]
    public void EffectiveManagedProjection_DefaultsToCSharpTypeName()
    {
        var csharp = CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "Locale");
        var record = NewRecord(csharp);

        Assert.Null(record.ManagedProjectionTypeName);
        Assert.Same(csharp, record.EffectiveManagedProjection);
    }

    [Fact]
    public void EffectiveAbiCarrier_DefaultsToCSharpTypeName()
    {
        var csharp = CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "Locale");
        var record = NewRecord(csharp);

        Assert.Null(record.AbiCarrierTypeName);
        Assert.Same(csharp, record.EffectiveAbiCarrier);
    }

    [Fact]
    public void EffectiveManagedProjection_UsesOverrideWhenPresent()
    {
        var csharp = CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "Locale");
        var projection = CSharpTypeName.FromNamespaceAndName("Foundation", "NSLocale");
        var record = NewRecord(csharp, projection: projection);

        Assert.Same(projection, record.EffectiveManagedProjection);
        Assert.Same(csharp, record.EffectiveAbiCarrier);
    }

    [Fact]
    public void EffectiveAbiCarrier_UsesOverrideWhenPresent()
    {
        var csharp = CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "Locale");
        var carrier = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftHandle");
        var record = NewRecord(csharp, carrier: carrier);

        Assert.Same(carrier, record.EffectiveAbiCarrier);
        Assert.Same(csharp, record.EffectiveManagedProjection);
    }

    [Fact]
    public void AllThreeAspects_AddressableIndependently()
    {
        var csharp = CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "Locale");
        var projection = CSharpTypeName.FromNamespaceAndName("Foundation", "NSLocale");
        var carrier = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftHandle");
        var record = NewRecord(csharp, projection: projection, carrier: carrier);

        // Swift identity
        Assert.Equal("Foundation.Locale", record.SwiftIdentity.ModuleQualifiedName);
        // Managed projection — what the consumer sees
        Assert.Equal("Foundation.NSLocale", record.EffectiveManagedProjection.FullyQualifiedName);
        // ABI carrier — what crosses the Swift→C boundary
        Assert.Equal("Swift.Runtime.SwiftHandle", record.EffectiveAbiCarrier.FullyQualifiedName);
    }

    [Fact]
    public void Record_WithExpression_PreservesExistingCallersUnchanged()
    {
        // Existing consumers that ignore the new fields still observe the old behavior:
        // EffectiveManagedProjection and EffectiveAbiCarrier both track CSharpTypeName.
        var csharp = CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "Locale");
        var record = NewRecord(csharp);
        var updatedCsharp = CSharpTypeName.FromNamespaceAndName("Swift.Foundation.V2", "Locale");

        var clone = record with { CSharpTypeName = updatedCsharp };

        Assert.Same(updatedCsharp, clone.CSharpTypeName);
        Assert.Same(updatedCsharp, clone.EffectiveManagedProjection);
        Assert.Same(updatedCsharp, clone.EffectiveAbiCarrier);
    }
}
