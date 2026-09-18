// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Exercises the buffer-mode type-metadata accessor ABI (indirect-buffer parameter
/// passing used when metadata + PWT args exceed three). A thin-mode mismatch here PAC-traps on
/// arm64e at first use, so these tests materialize the concrete specializations
/// and call <c>SwiftObjectHelper&lt;T&gt;.GetTypeMetadata()</c> to force the
/// accessor to fire end-to-end.
/// </summary>
public class BufferModeMetadataTests : TestBase
{
    public BufferModeMetadataTests(TestResults results) : base(results) { }

    public void TestBufferModeQuad_FourMetadataArgs_ResolvesViaBuffer()
    {
        // 4 unconstrained generic params → 4 metadata args in the Ma accessor.
        // Above the 3-arg threshold, so the accessor takes a buffer pointer.
        // Calling GetTypeMetadata() hits our emitted buffer-mode wrapper.
        //
        // Use four DISTINCT concrete types so a bug that writes the same metadata
        // pointer to all four buffer slots (e.g., missing index increment) would
        // produce a wrong total size instead of passing with aliased slots.
        var metadata = SwiftObjectHelper<BufferModeQuad<SimpleItem, ValuePoint, BitwiseValue, SimpleDescribable>>.GetTypeMetadata();
        AssertTrue(metadata.Handle != IntPtr.Zero,
            "BufferModeQuad<SimpleItem,ValuePoint,BitwiseValue,SimpleDescribable> metadata handle is non-zero");
        AssertTrue(metadata.Size > 0,
            "BufferModeQuad<SimpleItem,ValuePoint,BitwiseValue,SimpleDescribable> metadata size is non-zero");
    }

    public void TestBufferModeDescribablePair_MetadataPlusPwts_ResolvesViaBuffer()
    {
        // 2 Describable-constrained params → 2 metadata + 2 PWT args = 4 total.
        // Exercises PWT packing into the indirect buffer alongside metadata.
        var metadata = SwiftObjectHelper<BufferModeDescribablePair<SimpleItem, SimpleItem>>.GetTypeMetadata();
        AssertTrue(metadata.Handle != IntPtr.Zero,
            "BufferModeDescribablePair<SimpleItem,SimpleItem> metadata handle is non-zero");
        AssertTrue(metadata.Size > 0,
            "BufferModeDescribablePair<SimpleItem,SimpleItem> metadata size is non-zero");
    }

    public void TestBufferModeQuad_GenericProperties_RouteThroughPackedMetadataWrapper()
    {
#pragma warning disable SB0001 // Constructor remains on its separately qualified generic-parent route.
        using var quad = new BufferModeQuad<nint, int, uint, long>(11, 22, 33, 44);
#pragma warning restore SB0001

        AssertEqual((nint)11, quad.First, "BufferModeQuad.first through cdecl wrapper");
        AssertEqual(22, quad.Second, "BufferModeQuad.second through cdecl wrapper");
        AssertEqual((uint)33, quad.Third, "BufferModeQuad.third through cdecl wrapper");
        AssertEqual(44L, quad.Fourth, "BufferModeQuad.fourth through cdecl wrapper");
        AssertEqual(4, quad.Count, "BufferModeQuad.count through cdecl wrapper");
    }

    public void TestBufferModeDescribablePair_GenericProperties_RouteThroughPackedMetadataAndPwts()
    {
        using var first = new SimpleItem("first-id", "first-label");
        using var second = new SimpleItem("second-id", "second-label");
        using var pair = BufferModeDescribablePairSwiftBindingsTestLib_SimpleItemSwiftBindingsTestLib_SimpleItemCsmExtensions
            .FromSwiftBindingsTestLibSimpleItemSwiftBindingsTestLibSimpleItem(first, second);
        using var actualFirst = pair.First;
        using var actualSecond = pair.Second;

        AssertEqual("first-id", actualFirst.Id, "BufferModeDescribablePair.first through cdecl wrapper");
        AssertEqual("second-id", actualSecond.Id, "BufferModeDescribablePair.second through cdecl wrapper");
    }

    // The open-generic initializer is the only member of this type still on the direct
    // CallConvSwift arm: indirect result, two indirect generic arguments, then both metadata,
    // both Describable witness tables and the metatype. Its SB0001 marker says that shape is
    // unverified there, so these call it directly on distinct (K, V) pairs and read every field
    // back through both sides — C# getters and a Swift method that uses the witness tables.

    public void TestBufferModeDescribablePair_OpenGenericInit_StructPair_RoundTrips()
    {
        using var first = new SimpleItem("s1", "struct-first");
        using var second = new SimpleItem("s2", "struct-second");
#pragma warning disable SB0001 // The direct-arm initializer is what this test probes.
        using var pair = new BufferModeDescribablePair<SimpleItem, SimpleItem>(first, second);
#pragma warning restore SB0001
        using var actualFirst = pair.First;
        using var actualSecond = pair.Second;

        AssertEqual("s1", actualFirst.Id, "open-generic init: first.id");
        AssertEqual("struct-first", actualFirst.Label, "open-generic init: first.label");
        AssertEqual("s2", actualSecond.Id, "open-generic init: second.id");
        AssertEqual("struct-second", actualSecond.Label, "open-generic init: second.label");
        AssertEqual("[s1] struct-first | [s2] struct-second", pair.CombinedDescription(),
            "open-generic init: Swift reads both fields through the Describable witness tables");
    }

    public void TestBufferModeDescribablePair_OpenGenericInit_ClassPair_RetainsArgumentsPastTheirHandles()
    {
        BufferModeDescribablePair<SimpleDescribable, MultiProtocolEntity> pair;
        using (var first = new SimpleDescribable("class-first"))
        using (var second = new MultiProtocolEntity("c2", "class-second"))
        {
#pragma warning disable SB0001 // The direct-arm initializer is what this test probes.
            pair = new BufferModeDescribablePair<SimpleDescribable, MultiProtocolEntity>(first, second);
#pragma warning restore SB0001
        }

        // The caller's handles are released; the pair must hold its own references.
        using (pair)
        {
            using var actualFirst = pair.First;
            using var actualSecond = pair.Second;
            AssertEqual("class-first", actualFirst.Description, "open-generic init: first.description");
            AssertEqual("c2", actualSecond.Id, "open-generic init: second.id");
            AssertEqual("class-second", actualSecond.Name, "open-generic init: second.name");
            AssertEqual("class-first | [c2] class-second", pair.CombinedDescription(),
                "open-generic init: Swift reads both class fields through the Describable witness tables");
        }
    }

    public void TestBufferModeDescribablePair_OpenGenericInit_MixedPair_RoundTrips()
    {
        using var first = new MultiProtocolEntity("m1", "mixed-first");
        using var second = new SimpleItem("m2", "mixed-second");
#pragma warning disable SB0001 // The direct-arm initializer is what this test probes.
        using var pair = new BufferModeDescribablePair<MultiProtocolEntity, SimpleItem>(first, second);
#pragma warning restore SB0001
        using var actualFirst = pair.First;
        using var actualSecond = pair.Second;

        AssertEqual("m1", actualFirst.Id, "open-generic init: first.id");
        AssertEqual("mixed-second", actualSecond.Label, "open-generic init: second.label");
        AssertEqual("[m1] mixed-first | [m2] mixed-second", pair.CombinedDescription(),
            "open-generic init: Swift reads a class and a struct field through their witness tables");
    }
}
