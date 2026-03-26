// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class KnownMetadataTests : IClassFixture<KnownMetadataTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public KnownMetadataTests(TestFixture fixture)
    {
        _fixture = fixture;
    }

    public class TestFixture
    {
        static TestFixture()
        {
        }

        private static void InitializeResources()
        {
        }
    }

    [Fact]
    public static void HasPrimatives()
    {
        Assert.True(TypeMetadata.TryGetTypeMetadata<bool>(out var md0));
        Assert.True(TypeMetadata.TryGetTypeMetadata<sbyte>(out var md1));
        Assert.True(TypeMetadata.TryGetTypeMetadata<byte>(out var md2));
        Assert.True(TypeMetadata.TryGetTypeMetadata<short>(out var md3));
        Assert.True(TypeMetadata.TryGetTypeMetadata<ushort>(out var md4));
        Assert.True(TypeMetadata.TryGetTypeMetadata<int>(out var md5));
        Assert.True(TypeMetadata.TryGetTypeMetadata<uint>(out var md6));
        Assert.True(TypeMetadata.TryGetTypeMetadata<long>(out var md7));
        Assert.True(TypeMetadata.TryGetTypeMetadata<ulong>(out var md8));
        Assert.True(TypeMetadata.TryGetTypeMetadata<nint>(out var md9));
        Assert.True(TypeMetadata.TryGetTypeMetadata<nuint>(out var md10));
        Assert.True(TypeMetadata.TryGetTypeMetadata<float>(out var md11));
        Assert.True(TypeMetadata.TryGetTypeMetadata<double>(out var md12));
    }

    [Fact]
    public static void HasSwiftString()
    {
        Assert.True(TypeMetadata.Cache.TryGet(typeof(SwiftString), out var metadata));
        Assert.True(metadata!.Value.IsValid);
    }

    [Fact]
    public static void SwiftStringMetadataMatchesDirectLookup()
    {
        // Verify the cached metadata matches what SwiftString.GetTypeMetadata() would return
        Assert.True(TypeMetadata.Cache.TryGet(typeof(SwiftString), out var cachedMetadata));
        Assert.True(TypeMetadata.TryGetTypeMetadata<SwiftString>(out var lookupMetadata));
        Assert.Equal(cachedMetadata!.Value.Handle, lookupMetadata!.Value.Handle);
    }
}
