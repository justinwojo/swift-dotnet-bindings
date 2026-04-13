// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit tests for ExistentialUnion — verifies construction, property access,
/// and container round-tripping. The As&lt;T&gt;/TryCast methods require live Swift
/// metadata and are covered by runtime tests (ExistentialUnionTests in BindingTests).
/// </summary>
public class ExistentialUnionTests
{
    [Fact]
    public void Constructor_StoresContainer()
    {
        var container = new ExistentialContainer1();
        var union = new ExistentialUnion(container);

        // Should not throw — basic construction works
        Assert.NotNull(union);
    }

    [Fact]
    public void GetExistentialContainer_RoundTrips()
    {
        var container = new ExistentialContainer1();
        var union = new ExistentialUnion(container);

        var retrieved = union.GetExistentialContainer();
        // Container is a value type — should be a copy with same field values
        Assert.Equal(container.Payload0, retrieved.Payload0);
        Assert.Equal(container.ObjectMetadata, retrieved.ObjectMetadata);
    }

    [Fact]
    public void ObjectMetadata_ReadsFromContainer()
    {
        var container = new ExistentialContainer1();
        var union = new ExistentialUnion(container);

        // Default container has zero metadata
        var metadata = union.ObjectMetadata;
        Assert.Equal(TypeMetadata.Zero, metadata);
    }

    [Fact]
    public void TryCast_WithZeroMetadata_ReturnsFalse()
    {
        // A container with zero metadata should fail to cast to any concrete type,
        // since no ISwiftObject type has zero metadata.
        var container = new ExistentialContainer1();
        var union = new ExistentialUnion(container);

        // TryCast delegates to As<T> which compares metadata —
        // zero metadata won't match any registered type's metadata.
        // Use SwiftString as a representative ISwiftObject type.
        bool success = union.TryCast<SwiftString>(out var result);
        Assert.False(success);
        Assert.Null(result);
    }
}
