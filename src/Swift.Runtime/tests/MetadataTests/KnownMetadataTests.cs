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

    /// <summary>
    /// <c>System.String</c> resolves to Swift.String's metadata, exactly as <c>SwiftString</c> does.
    /// A public generic surface may carry the IDIOMATIC projection as its type argument rather than
    /// the wire type — an emitted key path is <c>KeyPath&lt;Root, string&gt;</c> — and building that
    /// generic's Swift metadata resolves each argument's metadata in turn. Without this mapping the
    /// numeric arguments resolved while a String-valued one threw, and the throw surfaced inside a
    /// reverse-dispatch receiver, which cannot let an exception escape.
    /// </summary>
    [Fact]
    public static void SystemStringResolvesToSwiftStringMetadata()
    {
        Assert.True(TypeMetadata.TryGetTypeMetadata<string>(out var stringMetadata));
        Assert.True(stringMetadata!.Value.IsValid);

        Assert.True(TypeMetadata.TryGetTypeMetadata<SwiftString>(out var swiftStringMetadata));
        Assert.Equal(swiftStringMetadata!.Value.Handle, stringMetadata.Value.Handle);
    }

    /// <summary>
    /// <c>TryGetTypeMetadata</c> honours its Try contract even when a type's own metadata resolution
    /// THROWS — a generic wrapper resolves its type arguments first, and an unresolvable argument
    /// throws out of the caching factory. Callers that ask for a boolean must get one: the reverse-
    /// dispatch receivers sit in <c>UnmanagedCallersOnly</c> frames where an escaping exception
    /// fail-fasts the process instead of reporting which type could not be resolved.
    /// <c>GetTypeMetadataOrThrow</c> still surfaces the cause, as the inner exception.
    /// </summary>
    [Fact]
    public static void UnresolvableTypeArgumentIsReportedNotThrownFromTry()
    {
        // KeyPath<TRoot, TValue> resolves both arguments; a C#-only class is not a Swift type, so
        // the Root resolution throws from inside the metadata factory.
        Assert.False(TypeMetadata.TryGetTypeMetadata<KeyPath<UnboundToSwift, string>>(out var metadata));
        Assert.Null(metadata);

        var thrown = Assert.Throws<SwiftRuntimeException>(
            () => TypeMetadata.GetTypeMetadataOrThrow<KeyPath<UnboundToSwift, string>>());
        Assert.NotNull(thrown.InnerException);
    }

    /// <summary>A type with no Swift counterpart, used only as an unresolvable type argument.</summary>
    private sealed class UnboundToSwift
    {
    }
}
