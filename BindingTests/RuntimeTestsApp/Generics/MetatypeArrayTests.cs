// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Tests for the <c>[any Protocol.Type]</c> metatype array bridge
/// (<c>MetatypeArrayBridgeEmitter</c>). The generator produces a Swift
/// <c>@_cdecl</c> wrapper that accepts a C pointer + count and reconstructs
/// the Swift array of existential metatypes via <c>unsafeBitCast</c>.
/// The C# caller pins an array of metatype handles and passes the raw pointer.
/// </summary>
public class MetatypeArrayTests : TestBase
{
    public MetatypeArrayTests(TestResults results) : base(results) { }

    public unsafe void TestJoinSearchableKinds_ThreeConformers()
    {
        var handles = new[]
        {
            SwiftObjectHelper<SongItem>.GetTypeMetadata().Handle,
            SwiftObjectHelper<AlbumItem>.GetTypeMetadata().Handle,
            SwiftObjectHelper<ArtistItem>.GetTypeMetadata().Handle,
        };
        fixed (IntPtr* p = handles)
        {
            var joined = Functions.JoinSearchableKinds((IntPtr)p, (nint)handles.Length);
            AssertEqual("song,album,artist", joined, "JoinSearchableKinds(song,album,artist)");
        }
    }

    public unsafe void TestCountSearchableTypes_ThreeConformers()
    {
        var handles = new[]
        {
            SwiftObjectHelper<SongItem>.GetTypeMetadata().Handle,
            SwiftObjectHelper<AlbumItem>.GetTypeMetadata().Handle,
            SwiftObjectHelper<ArtistItem>.GetTypeMetadata().Handle,
        };
        fixed (IntPtr* p = handles)
        {
            var count = Functions.CountSearchableTypes((IntPtr)p, (nint)handles.Length);
            AssertEqual((nint)3, count, "CountSearchableTypes returns the number of metatypes passed");
        }
    }

    public unsafe void TestCountSearchableTypes_SingleConformer()
    {
        var handles = new[] { SwiftObjectHelper<AlbumItem>.GetTypeMetadata().Handle };
        fixed (IntPtr* p = handles)
        {
            var count = Functions.CountSearchableTypes((IntPtr)p, (nint)handles.Length);
            AssertEqual((nint)1, count, "CountSearchableTypes with one metatype");
        }
    }

    public void TestCountSearchableTypes_Empty()
    {
        var count = Functions.CountSearchableTypes(IntPtr.Zero, (nint)0);
        AssertEqual((nint)0, count, "CountSearchableTypes with zero count ignores pointer");
    }

    public unsafe void TestJoinSearchableKinds_ReorderedConformers()
    {
        var handles = new[]
        {
            SwiftObjectHelper<ArtistItem>.GetTypeMetadata().Handle,
            SwiftObjectHelper<SongItem>.GetTypeMetadata().Handle,
        };
        fixed (IntPtr* p = handles)
        {
            var joined = Functions.JoinSearchableKinds((IntPtr)p, (nint)handles.Length);
            AssertEqual("artist,song", joined, "JoinSearchableKinds preserves caller-supplied ordering");
        }
    }
}
