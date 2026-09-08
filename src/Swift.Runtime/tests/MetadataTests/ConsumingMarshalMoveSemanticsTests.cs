// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the teardown decision inside
/// <see cref="SwiftMarshal.MarshalFromSwiftObjectConsuming{T}(void*)"/> — the direct (by-value
/// register) return arm's wire cleanup.
///
/// <para>The helper hands its buffer to the carrier's <c>NewFromPayload</c> and then releases the
/// caller-owned temporary. Whether that release is correct depends entirely on what
/// <c>NewFromPayload</c> did with the buffer, which is exactly what the carrier's declared
/// <see cref="PayloadConstructionSemantics"/> reports: a <see cref="PayloadConstructionSemantics.Copy"/>
/// carrier took its own <c>+1</c> and the temporary still owns Swift's, so it must be
/// value-witness-destroyed; a <see cref="PayloadConstructionSemantics.Move"/> carrier took the value
/// OUT (<c>InitializeWithTake</c>, or <see cref="Swift.SwiftString"/>'s documented bitwise move), so
/// the temporary is moved-from and destroying it runs the value's <c>deinit</c> a second time on
/// storage Swift already relinquished. For a <c>~Copyable</c> payload that second destroy is a
/// double-deinit; for <see cref="Swift.SwiftString"/> it is an over-release of the bridge object.</para>
///
/// <para>The observable is per-probe-type and needs no Swift runtime: the destroy arm resolves the
/// carrier's Swift metadata (<c>TypeMetadata.GetTypeMetadataOrThrow&lt;T&gt;</c> → the type's static
/// <c>GetTypeMetadata</c>), while the construction arm on this host reaches only
/// <c>NewFromPayload</c>. So a probe that counts its own metadata resolutions records whether the
/// teardown ran, with no process-global counter and no dependence on parallel test activity.</para>
/// </summary>
public class ConsumingMarshalMoveSemanticsTests
{
    /// <summary>Carrier declaring Copy: its NewFromPayload duplicates, so the temporary must be destroyed.</summary>
    private sealed class CopyCarrierProbe : ISwiftObject
    {
        internal static int MetadataResolutions;
        internal static int Constructions;

        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();

        public static TypeMetadata GetTypeMetadata()
        {
            MetadataResolutions++;
            return TypeMetadata.Zero;
        }

        public static ISwiftObject NewFromPayload(IntPtr payload)
        {
            Constructions++;
            return new CopyCarrierProbe();
        }

        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();

        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Copy;
    }

    /// <summary>Carrier declaring Move: its NewFromPayload takes the value, so the temporary must NOT be destroyed.</summary>
    private sealed class MoveCarrierProbe : ISwiftObject
    {
        internal static int MetadataResolutions;
        internal static int Constructions;

        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();

        public static TypeMetadata GetTypeMetadata()
        {
            MetadataResolutions++;
            return TypeMetadata.Zero;
        }

        public static ISwiftObject NewFromPayload(IntPtr payload)
        {
            Constructions++;
            return new MoveCarrierProbe();
        }

        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();

        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Move;
    }

    [Fact]
    public unsafe void CopyCarrier_ConsumingMarshal_TearsDownTheOwnedTemporary()
    {
        long buffer = 0;

        var value = SwiftMarshal.MarshalFromSwiftObjectConsuming<CopyCarrierProbe>(&buffer);

        Assert.NotNull(value);
        Assert.Equal(1, CopyCarrierProbe.Constructions);
        // The teardown arm ran: it reached for the carrier's Swift metadata to call the value witness.
        Assert.True(CopyCarrierProbe.MetadataResolutions >= 1,
            "a Copy carrier's owned temporary must still be value-witness-destroyed after construction");
    }

    [Fact]
    public unsafe void MoveCarrier_ConsumingMarshal_LeavesTheMovedFromTemporaryAlone()
    {
        long buffer = 0;

        var value = SwiftMarshal.MarshalFromSwiftObjectConsuming<MoveCarrierProbe>(&buffer);

        // Construction still happens — only the teardown is suppressed.
        Assert.NotNull(value);
        Assert.Equal(1, MoveCarrierProbe.Constructions);
        // No metadata resolution means no destroy was attempted. Reverting the guard makes this
        // assertion fail, because the unconditional teardown resolves metadata for every carrier.
        Assert.Equal(0, MoveCarrierProbe.MetadataResolutions);
    }
}
