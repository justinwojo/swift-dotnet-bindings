// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the arm table of <see cref="SwiftMarshal.ReleaseIndirectResultValue"/> — the single seam
/// both the plan builder's <c>_cdeclBuf</c> cleanup and the concrete-specialization emitter's
/// result-buffer cleanup call to balance an owned indirect result.
///
/// The method answers ONE question per carrier: given what the carrier DECLARED its
/// <see cref="PayloadConstructionSemantics"/> to be, does the caller still own the buffer's
/// storage (return true → free it) and does the value inside it still need a value-witness
/// Destroy? Getting the first half wrong is a double-free or a use-after-free, so the return
/// value is asserted for every arm; getting the second half wrong is the leak the seam exists
/// to close, so the arms that must attempt a Destroy are observed through the release-path
/// counters.
///
/// The declared-semantics arms pass <c>null</c> metadata: that drives the Destroy attempt into its
/// "metadata unavailable" skip, which is exactly what makes the attempt observable
/// (<c>wireDestroy.skippedInvalid</c>) without any real Swift value to destroy. The class-slot arm
/// is the exception — it is the only arm that reads <see cref="TypeMetadata.Kind"/>, so those cases
/// pass a fabricated one-word metadata record and assert the opposite: that no Destroy is
/// attempted. The counters are process-global and delta-based here, and the collection serializes
/// against the only code that can LOWER them (<c>ReleasePathDiagnostics.Reset</c>).
/// </summary>
[Collection("ReleasePathDiagnostics")]
public class ReleaseIndirectResultValueTests
{
    // ─── Fakes (one per scenario so the process-wide semantics cache can't collide) ───

    /// <summary>A Swift CLASS carrier: a reference-type ISwiftObject that is NOT an ISwiftStruct.
    /// Its indirect result is a one-word slot holding the object pointer, so the declaration
    /// (Adopt — of the reference) does not describe what happened to the buffer.</summary>
    private sealed class ClassSlotCarrier : ISwiftObject
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
    }

    /// <summary>The carrier shape the C# type test alone cannot tell apart from a class: a sealed
    /// bare-ISwiftObject wrapper with no ISwiftStruct, wrapping a Swift STRUCT whose buffer it
    /// ADOPTS. The runtime's SwiftUI wrappers — Color, Font, Image, Text, Animation, AnyView,
    /// EdgeInsets — are all exactly this, so mistaking one for a class slot frees a buffer the
    /// wrapper still owns.</summary>
    private sealed class BareObjectValueCarrier : ISwiftObject
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
    }

    // The value-semantics fakes below all implement ISwiftStruct, as every real value carrier
    // does: a C# class wrapping a Swift value, not a Swift class. Without it they would be
    // indistinguishable from ClassSlotCarrier and would never reach the arms they exist to pin.

    private sealed class AdoptCarrier : ISwiftObject, ISwiftStruct
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
    }

    private sealed class CopyCarrier : ISwiftObject, ISwiftStruct
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Copy;
    }

    private sealed class MoveCarrier : ISwiftObject, ISwiftStruct
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Move;
    }

    /// <summary>Value-type ISwiftObject: the seam resolves these as Inline regardless of what
    /// they declare, which is the shape <c>Foundation.Data</c> arrives in.</summary>
    private struct InlineCarrier : ISwiftObject, ISwiftStruct
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Inline;
    }

    // A non-null buffer address that is never dereferenced: every arm here either returns before
    // touching it, or routes into the metadata-unavailable skip.
    private static readonly IntPtr FakeBuffer = (IntPtr)0x1000;

    // Fabricated metadata records for the class-slot arm, which is the only arm that reads
    // TypeMetadata.Kind. Kind is one pointer-sized word at the handle: above the ABI's max
    // discriminator (0x7ff) it means class, otherwise it is the discriminator itself. The arm
    // returns before any value-witness access, so a one-word record is the whole requirement.
    private static readonly TypeMetadata ClassKindMetadata = AllocKindMetadata(0x1000);
    private static readonly TypeMetadata StructKindMetadata = AllocKindMetadata((long)TypeMetadataKind.Struct);

    private static TypeMetadata AllocKindMetadata(long kindWord)
    {
        IntPtr record = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(record, (IntPtr)kindWord);
        return TypeMetadata.FromHandle(record);
    }

    private static long SkippedInvalid()
    {
        string snapshot = ReleasePathDiagnostics.Snapshot();
        const string group = "wireDestroy(";
        int gi = snapshot.IndexOf(group, StringComparison.Ordinal);
        Assert.True(gi >= 0, $"wireDestroy group missing from snapshot: {snapshot}");
        int inner = gi + group.Length;
        string body = snapshot.Substring(inner, snapshot.IndexOf(')', inner) - inner);

        const string field = "skippedInvalid=";
        int fi = body.IndexOf(field, StringComparison.Ordinal);
        Assert.True(fi >= 0, $"skippedInvalid missing from wireDestroy group: {body}");
        int vs = fi + field.Length;
        int ve = vs;
        while (ve < body.Length && char.IsDigit(body[ve]))
            ve++;
        return long.Parse(body.Substring(vs, ve - vs));
    }

    // ─── Guards ───

    [Fact]
    public void NullBuffer_ReportsCallerMustNotFree()
    {
        Assert.False(SwiftMarshal.ReleaseIndirectResultValue(IntPtr.Zero, typeof(CopyCarrier), null, valueEscapesSeam: false));
    }

    [Fact]
    public void NullCarrierType_ReportsCallerMustNotFree()
    {
        // No declaration to follow, so the seam degrades to leaving the buffer alone: leaking is
        // recoverable, freeing a buffer an adopting carrier owns is not.
        Assert.False(SwiftMarshal.ReleaseIndirectResultValue(FakeBuffer, null!, null, valueEscapesSeam: false));
    }

    // ─── Arms ───

    [Fact]
    public void AdoptCarrier_LeavesBufferAndValueToTheCarrier()
    {
        // The managed wrapper took the buffer pointer over. Freeing it here is a use-after-free,
        // and destroying the value inside it double-releases what the wrapper will release.
        long before = SkippedInvalid();

        Assert.False(SwiftMarshal.ReleaseIndirectResultValue(FakeBuffer, typeof(AdoptCarrier), null, valueEscapesSeam: true));

        Assert.Equal(before, SkippedInvalid());
    }

    [Fact]
    public void CopyCarrier_DestroysTheValueAndHandsBackTheStorage()
    {
        // NewFromPayload made its own copy, so the value written into the buffer is still the
        // caller's to destroy AND the storage is still the caller's to free. This is the arm the
        // plan builder used to skip entirely for every non-frozen struct.
        long before = SkippedInvalid();

        Assert.True(SwiftMarshal.ReleaseIndirectResultValue(FakeBuffer, typeof(CopyCarrier), null, valueEscapesSeam: true));

        Assert.True(SkippedInvalid() > before, "the Copy arm must attempt a value-witness Destroy before the storage is freed");
    }

    [Fact]
    public void MoveCarrier_FreesTheStorageWithoutDestroyingTheValue()
    {
        // The value was moved out bitwise: destroying it here would release what the managed
        // representation now owns, but the storage itself is still the caller's.
        long before = SkippedInvalid();

        Assert.True(SwiftMarshal.ReleaseIndirectResultValue(FakeBuffer, typeof(MoveCarrier), null, valueEscapesSeam: true));

        Assert.Equal(before, SkippedInvalid());
    }

    [Fact]
    public void InlineCarrier_ThatEscapesTheSeam_IsNotDestroyed()
    {
        // The raw carrier value outlives this call (it is handed to another emitter's body), so
        // the seam may release the storage but must not touch the value.
        long before = SkippedInvalid();

        Assert.True(SwiftMarshal.ReleaseIndirectResultValue(FakeBuffer, typeof(InlineCarrier), null, valueEscapesSeam: true));

        Assert.Equal(before, SkippedInvalid());
    }

    [Fact]
    public void InlineCarrier_ConsumedAtTheSeam_IsDestroyed()
    {
        // The seam already converted the value to an owned managed representation, so nothing
        // aliases it any more — this is where an out-of-line Data payload gets released.
        long before = SkippedInvalid();

        Assert.True(SwiftMarshal.ReleaseIndirectResultValue(FakeBuffer, typeof(InlineCarrier), null, valueEscapesSeam: false));

        Assert.True(SkippedInvalid() > before, "a consumed Inline carrier must attempt a value-witness Destroy");
    }

    [Fact]
    public void NonSwiftCarrier_IsTreatedAsAConsumedInlineValue()
    {
        // A carrier with no Swift declaration resolves to Inline, so the seam still frees the
        // storage rather than stranding it — the pre-existing behaviour for those returns.
        Assert.True(SwiftMarshal.ReleaseIndirectResultValue(FakeBuffer, typeof(int), null, valueEscapesSeam: true));
    }

    [Fact]
    public void ClassSlotCarrier_FreesTheSlotWithoutConsultingItsDeclaration()
    {
        // A Swift class's indirect result is one word holding the object pointer, which the
        // marshal seam reads out before this runs. The wrapper adopted the REFERENCE, so the
        // storage is dead (free it) and holds nothing to destroy. Following the carrier's
        // declaration instead — Adopt, describing the reference — strands the allocation.
        long before = SkippedInvalid();

        Assert.True(SwiftMarshal.ReleaseIndirectResultValue(
            FakeBuffer, typeof(ClassSlotCarrier), ClassKindMetadata, valueEscapesSeam: true));

        Assert.Equal(before, SkippedInvalid());
    }

    [Fact]
    public void BareObjectValueCarrier_KeepsItsAdoptDeclaration_SoTheBufferIsNotFreedUnderIt()
    {
        // Same C# shape as a class carrier (reference type, ISwiftObject, no ISwiftStruct) but a
        // Swift STRUCT underneath, which the wrapper adopts — the SwiftUI wrappers' shape. Freeing
        // here would pull the buffer out from under a live wrapper, so the metadata kind, not the
        // C# shape, has to decide. This is the use-after-free guard on the class-slot arm.
        long before = SkippedInvalid();

        Assert.False(SwiftMarshal.ReleaseIndirectResultValue(
            FakeBuffer, typeof(BareObjectValueCarrier), StructKindMetadata, valueEscapesSeam: true));

        Assert.Equal(before, SkippedInvalid());
    }

    [Fact]
    public void ClassSlotCarrier_WithUnresolvableMetadata_DegradesToLeavingTheBufferAlone()
    {
        // No metadata means no witness to the carrier's kind, and the two candidates differ by a
        // free: a genuine class slot leaks one allocation, an adopting value wrapper is corrupted.
        // The seam takes the leak.
        Assert.False(SwiftMarshal.ReleaseIndirectResultValue(
            FakeBuffer, typeof(ClassSlotCarrier), null, valueEscapesSeam: true));
    }

    [Fact]
    public void ClassSlotDetection_ExcludesEveryValueSemanticsCarrierTheRuntimeShips()
    {
        // The class-slot arm runs BEFORE the declared semantics, so anything it catches never
        // reaches Copy/Move/Adopt. Every value carrier the runtime ships is a C# class wrapping a
        // Swift value and says so with ISwiftStruct; a new one that forgets the interface would be
        // silently rerouted into the class-slot arm and have its payload leaked instead of
        // destroyed. Class metadata is passed deliberately, so the exclusion is proven by the
        // ISwiftStruct test rather than by the kind check happening to disagree.
        Type[] valueCarriers =
        {
            typeof(Swift.SwiftString),
            typeof(Swift.SwiftArray<int>),
            typeof(Swift.SwiftDictionary<int, int>),
            typeof(Swift.SwiftSet<int>),
            typeof(Swift.SwiftOptional<int>),
            typeof(Swift.SwiftResult<int, int>),
            typeof(Swift.SwiftClosedRange<int>),
        };

        foreach (Type carrier in valueCarriers)
        {
            Assert.False(
                SwiftMarshal.IsIndirectClassSlot(carrier, ClassKindMetadata),
                $"{carrier.Name} carries a Swift VALUE, so it must reach the declared-semantics arms, not the class-slot arm");
        }

        // Positive control: the detection is not vacuously false for everything.
        Assert.True(SwiftMarshal.IsIndirectClassSlot(typeof(ClassSlotCarrier), ClassKindMetadata));
    }

    [Fact]
    public void InvalidMetadata_NeverThrows_SoACleanupCannotReplaceAnInFlightException()
    {
        // The seam runs from a finally. A throw out of it would replace whatever exception the
        // Swift call was already propagating, so every arm must return normally.
        var exception = Record.Exception(() =>
        {
            SwiftMarshal.ReleaseIndirectResultValue(FakeBuffer, typeof(CopyCarrier), null, valueEscapesSeam: false);
            SwiftMarshal.ReleaseIndirectResultValue(FakeBuffer, typeof(AdoptCarrier), TypeMetadata.Zero, valueEscapesSeam: false);
            SwiftMarshal.ReleaseIndirectResultValue(FakeBuffer, typeof(InlineCarrier), TypeMetadata.Zero, valueEscapesSeam: false);
        });

        Assert.Null(exception);
    }
}
