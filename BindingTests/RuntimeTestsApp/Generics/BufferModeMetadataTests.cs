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
}
