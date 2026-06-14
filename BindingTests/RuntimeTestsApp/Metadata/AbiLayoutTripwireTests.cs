// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;

namespace RuntimeTestsApp.Metadata;

/// <summary>
/// ABI-layout tripwire (architecture review 2026-06, Findings 4 &amp; 59).
///
/// Swift.Runtime hand-mirrors ~40 Swift ABI layout facts as C# constants and struct layouts —
/// the value-witness-table field order, existential-container sizes for arity 0-8, the
/// metadata-kind discriminators (and the &gt; 0x7ff class heuristic), the tuple element-vector
/// stride, and frozen-struct sizes. Until now the only guards compared those C# mirrors to
/// other C# constants, so they catch our own edits but never an Apple ABI drift — and every
/// drift mode is silent memory corruption that looks like a Mono/NativeAOT bug.
///
/// These tests assert each mirror against the GROUND TRUTH exported by the Swift fixture
/// (Sources/SwiftBindingsTestLib/Metadata/AbiLayoutTripwire.swift), which computes the truth
/// from live <c>MemoryLayout</c> and the live type metadata of real Swift values. The truth is
/// observed, never a constant re-typed on the Swift side, so the two sides cannot drift
/// together. No skip attribute: these run on every platform leg (Mono-JIT simulator and
/// NativeAOT device) so a toolchain bump trips the wire wherever it lands.
/// </summary>
public class AbiLayoutTripwireTests : TestBase
{
    public AbiLayoutTripwireTests(TestResults results) : base(results) { }

    private const string TestLib = "SwiftBindingsTestLib";

    // Type ids — must mirror the switch in AbiLayoutTripwire.swift.
    private const int TypeInt = 0;
    private const int TypeBool = 1;
    private const int TypeDouble = 2;
    private const int TypeString = 3;
    private const int TypeProbeStruct = 4;
    private const int TypeProbeEnum = 5;
    private const int TypeOptionalInt = 6;
    private const int TypeProbeClass = 7;
    private const int TypeTuple = 8;

    [DllImport(TestLib, EntryPoint = "abi_layout_size")]
    private static extern nint AbiLayoutSize(int typeId);

    [DllImport(TestLib, EntryPoint = "abi_layout_stride")]
    private static extern nint AbiLayoutStride(int typeId);

    [DllImport(TestLib, EntryPoint = "abi_layout_alignment")]
    private static extern nint AbiLayoutAlignment(int typeId);

    [DllImport(TestLib, EntryPoint = "abi_type_metadata")]
    private static extern IntPtr AbiTypeMetadata(int typeId);

    [DllImport(TestLib, EntryPoint = "abi_metadata_kind_word")]
    private static extern nint AbiMetadataKindWord(int typeId);

    [DllImport(TestLib, EntryPoint = "abi_existential_size")]
    private static extern nint AbiExistentialSize(int arity);

    [DllImport(TestLib, EntryPoint = "abi_tuple_element_offsets")]
    private static extern unsafe void AbiTupleElementOffsets(nint* outOffsets);

    [DllImport(TestLib, EntryPoint = "abi_probe_struct_init")]
    private static extern unsafe void AbiProbeStructInit(void* storage);

    /// <summary>
    /// ExistentialContainer0-8 mirror the opaque existential container of arity N as
    /// (4 + N) machine words. Assert both the real C# struct size and the hardcoded
    /// <c>SizeOf</c> formula against the live <c>MemoryLayout</c> of an N-protocol existential,
    /// plus the 3-word inline payload buffer against the live <c>Any</c> layout.
    /// </summary>
    public void TestExistentialContainerSizesMatchLiveLayout()
    {
        AssertExistentialArity(0, Unsafe.SizeOf<ExistentialContainer0>(), default(ExistentialContainer0).SizeOf);
        AssertExistentialArity(1, Unsafe.SizeOf<ExistentialContainer1>(), default(ExistentialContainer1).SizeOf);
        AssertExistentialArity(2, Unsafe.SizeOf<ExistentialContainer2>(), default(ExistentialContainer2).SizeOf);
        AssertExistentialArity(3, Unsafe.SizeOf<ExistentialContainer3>(), default(ExistentialContainer3).SizeOf);
        AssertExistentialArity(4, Unsafe.SizeOf<ExistentialContainer4>(), default(ExistentialContainer4).SizeOf);
        AssertExistentialArity(5, Unsafe.SizeOf<ExistentialContainer5>(), default(ExistentialContainer5).SizeOf);
        AssertExistentialArity(6, Unsafe.SizeOf<ExistentialContainer6>(), default(ExistentialContainer6).SizeOf);
        AssertExistentialArity(7, Unsafe.SizeOf<ExistentialContainer7>(), default(ExistentialContainer7).SizeOf);
        AssertExistentialArity(8, Unsafe.SizeOf<ExistentialContainer8>(), default(ExistentialContainer8).SizeOf);

        // The opaque inline payload buffer is the existential size minus its metadata word and
        // witness-table words. For arity 0 (Any) that is size - 1 word = 3 words.
        int liveInlineBuffer = (int)AbiExistentialSize(0) - IntPtr.Size;
        AssertEqual(liveInlineBuffer, ExistentialContainerFactory.MaxInlinePayloadSize,
            "MaxInlinePayloadSize vs live (MemoryLayout<Any>.size - one metadata word)");

        TestLogger.Info("Existential container sizes (arity 0-8) + inline buffer match live MemoryLayout");
    }

    private void AssertExistentialArity(int arity, int structSize, int sizeOfProperty)
    {
        int live = (int)AbiExistentialSize(arity);
        AssertEqual(live, structSize,
            $"ExistentialContainer{arity} struct size vs live MemoryLayout<any …{arity}>.size");
        AssertEqual(live, sizeOfProperty,
            $"ExistentialContainer{arity}.SizeOf formula vs live MemoryLayout<any …{arity}>.size");
    }

    /// <summary>
    /// The value witness table stores size, stride, and flags after eight function-pointer
    /// slots. Reading them through the mirrored struct and matching live <c>MemoryLayout</c>
    /// validates those scalar fields sit at the right offsets (a missing/extra function-pointer
    /// slot would shift them) and that the AlignmentMask (low byte only) decodes correctly.
    /// </summary>
    public unsafe void TestValueWitnessSizeStrideAlignmentMatchLiveLayout()
    {
        AssertVwtScalars(TypeInt, "Int");
        AssertVwtScalars(TypeBool, "Bool");
        AssertVwtScalars(TypeDouble, "Double");
        AssertVwtScalars(TypeString, "String");
        AssertVwtScalars(TypeProbeStruct, "AbiTripwireProbeStruct");
        AssertVwtScalars(TypeProbeClass, "AbiTripwireProbeClass");
        TestLogger.Info("Value-witness size/stride/alignment match live MemoryLayout for all probe types");
    }

    private unsafe void AssertVwtScalars(int typeId, string name)
    {
        var metadata = TypeMetadata.FromHandle(AbiTypeMetadata(typeId));
        AssertTrue(metadata.IsValid, $"{name}: live metadata pointer is non-null");
        ValueWitnessTable* vwt = metadata.ValueWitnessTable;
        AssertEqual((int)AbiLayoutSize(typeId), (int)vwt->Size,
            $"{name}: VWT Size field vs live MemoryLayout.size");
        AssertEqual((int)AbiLayoutStride(typeId), (int)vwt->Stride,
            $"{name}: VWT Stride field vs live MemoryLayout.stride");
        AssertEqual((int)AbiLayoutAlignment(typeId), vwt->Alignment,
            $"{name}: VWT alignment (AlignmentMask decode) vs live MemoryLayout.alignment");
    }

    /// <summary>
    /// Behaviorally probes a value-witness function-pointer slot: copy a genuine Swift value
    /// through the mirrored <c>InitializeWithCopy</c> slot and verify the bytes round-trip. This
    /// is the same slot the runtime's collection types call in production; a copy that round-trips
    /// proves the slot holds a real, callable copy function at the mirrored offset.
    ///
    /// The source is initialized by the Swift fixture (<c>abi_probe_struct_init</c>) so the witness
    /// runs within its ABI preconditions on a fully-formed value, not arbitrary bytes. Note this
    /// asserts the slot is a real *copy-shaped* witness at the declared offset — for a trivial
    /// (POD) type the four copy/take/assign witnesses are bitwise-identical, so it does not
    /// distinguish <c>InitializeWithCopy</c> from its neighbors. The structural pin lives in
    /// <see cref="TestValueWitnessSizeStrideAlignmentMatchLiveLayout"/>: Size/Stride read correct
    /// values only if they sit immediately after the eight-slot function-pointer block, which
    /// fixes that block's boundary.
    /// </summary>
    public unsafe void TestValueWitnessInitializeWithCopyRoundTripsThroughMirroredSlot()
    {
        var metadata = TypeMetadata.FromHandle(AbiTypeMetadata(TypeProbeStruct));
        AssertTrue(metadata.IsValid, "ProbeStruct: live metadata pointer is non-null");
        ValueWitnessTable* vwt = metadata.ValueWitnessTable;

        int size = (int)vwt->Size;
        int stride = (int)vwt->Stride;
        AssertTrue(size > 0 && stride >= size, "ProbeStruct: sane size/stride from VWT");

        // Allocate stride bytes per buffer so the copy cannot run off the end regardless of how
        // many bytes the witness touches; compare only the value's `size` bytes. The source is a
        // genuine Swift value (written by the fixture); the destination stays zeroed so a real
        // copy is observable (a no-op/garbage slot would leave dst zero or differing).
        byte* src = stackalloc byte[stride];
        byte* dst = stackalloc byte[stride];
        for (int i = 0; i < stride; i++)
        {
            src[i] = 0;
            dst[i] = 0;
        }
        AbiProbeStructInit(src);

        vwt->InitializeWithCopy(dst, src, metadata);

        bool anyNonZero = false;
        for (int i = 0; i < size; i++)
        {
            if (src[i] != 0)
            {
                anyNonZero = true;
            }
            AssertEqual(src[i], dst[i],
                $"ProbeStruct byte {i} copied through mirrored InitializeWithCopy slot");
        }
        AssertTrue(anyNonZero,
            "ProbeStruct source value is non-zero, so the copy is observable against a zeroed dst");
        TestLogger.Info("InitializeWithCopy round-trips a real Swift value through the mirrored VWT slot");
    }

    /// <summary>
    /// The metadata-kind discriminators (Struct = 0x200, Enum = 0x201, Optional = 0x202,
    /// Tuple = 0x301) and the &gt; 0x7ff class heuristic are hardcoded in TypeMetadata. Assert
    /// the mirrored enum constants equal the live first metadata word, and that the Kind getter
    /// classifies live metadata correctly — including mapping a class's large first word to Class.
    /// </summary>
    public void TestMetadataKindDiscriminatorsMatchLive()
    {
        AssertEqual((long)TypeMetadataKind.Struct, (long)AbiMetadataKindWord(TypeProbeStruct),
            "Struct kind discriminator vs live metadata word");
        AssertEqual((long)TypeMetadataKind.Enum, (long)AbiMetadataKindWord(TypeProbeEnum),
            "Enum kind discriminator vs live metadata word");
        AssertEqual((long)TypeMetadataKind.Optional, (long)AbiMetadataKindWord(TypeOptionalInt),
            "Optional kind discriminator vs live metadata word");
        AssertEqual((long)TypeMetadataKind.Tuple, (long)AbiMetadataKindWord(TypeTuple),
            "Tuple kind discriminator vs live metadata word");

        AssertEqual(TypeMetadataKind.Struct, TypeMetadata.FromHandle(AbiTypeMetadata(TypeProbeStruct)).Kind,
            "Kind getter classifies a struct");
        AssertEqual(TypeMetadataKind.Enum, TypeMetadata.FromHandle(AbiTypeMetadata(TypeProbeEnum)).Kind,
            "Kind getter classifies an enum");
        AssertEqual(TypeMetadataKind.Optional, TypeMetadata.FromHandle(AbiTypeMetadata(TypeOptionalInt)).Kind,
            "Kind getter classifies an optional");
        AssertEqual(TypeMetadataKind.Tuple, TypeMetadata.FromHandle(AbiTypeMetadata(TypeTuple)).Kind,
            "Kind getter classifies a tuple");
        AssertEqual(TypeMetadataKind.Class, TypeMetadata.FromHandle(AbiTypeMetadata(TypeProbeClass)).Kind,
            "Kind getter applies the > 0x7ff class heuristic");
        TestLogger.Info("Metadata-kind discriminators and the class heuristic match live metadata");
    }

    /// <summary>
    /// Tuple metadata stores an element vector of (type, byte-offset) pairs after a 3-word
    /// header. Reading element offsets through the mirrored <c>TupleTypeMetadata</c> and matching
    /// the offsets observed directly from a live tuple value validates the header field offsets
    /// and the 2-word element-vector stride.
    /// </summary>
    public unsafe void TestTupleElementVectorOffsetsMatchLiveLayout()
    {
        var metadata = TypeMetadata.FromHandle(AbiTypeMetadata(TypeTuple));
        AssertEqual(TypeMetadataKind.Tuple, metadata.Kind, "Tuple metadata kind");
        TupleTypeMetadata* tuple = metadata.AsTupleMetadata();
        AssertEqual((nuint)3, tuple->NumElements, "Tuple element count");

        nint* live = stackalloc nint[3];
        AbiTupleElementOffsets(live);

        for (int i = 0; i < 3; i++)
        {
            AssertEqual((long)live[i], (long)tuple->GetElementOffset(i),
                $"Tuple element {i} offset (mirrored element vector) vs live tuple layout");
        }
        TestLogger.Info($"Tuple element offsets via metadata [{live[0]}, {live[1]}, {live[2]}] match live layout");
    }

    /// <summary>
    /// <c>SwiftString.Buffer</c> is the runtime-facing mirror of Swift's @frozen String storage —
    /// the same two-word size recorded as <c>inlineSize="16"</c> in SwiftDatabase.xml. Assert its
    /// size against the live <c>MemoryLayout&lt;String&gt;.size</c> so an Apple change to the
    /// frozen String layout trips the wire.
    /// </summary>
    public void TestFrozenStringBufferSizeMatchesLiveLayout()
    {
        AssertEqual((int)AbiLayoutSize(TypeString), Unsafe.SizeOf<SwiftString.Buffer>(),
            "SwiftString.Buffer size vs live MemoryLayout<String>.size");
        TestLogger.Info($"SwiftString.Buffer size matches live String size ({AbiLayoutSize(TypeString)} bytes)");
    }
}
