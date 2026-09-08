// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

public class WitnessCollectionLifetimeTests : TestBase
{
    public WitnessCollectionLifetimeTests(TestResults results) : base(results) { }

    public void TestCopyReadsAndEnumerationBalanceNativeDeinit()
    {
        Functions.ResetWitnessLifetimeCounts();
        var window = Functions.MakeWitnessCopyWindow();
        for (int i = 0; i < 20; i++)
        {
            using var item = window[0];
            AssertEqual(73, item.Identifier);
        }
        foreach (var item in window)
        {
            using (item) AssertEqual(73, item.Identifier);
        }
        using var survivor = window[0];
        window.Dispose();
        AssertEqual(0, Functions.GetWitnessLifetimeDeinits(), "returned Copy keeps the reference alive");
        AssertEqual(73, survivor.Identifier);
        survivor.Dispose();
        AssertOneNativeDeinit();
    }

    public void TestAdoptReadsAndEnumerationBalanceNativeDeinit()
    {
        Functions.ResetWitnessLifetimeCounts();
        var window = Functions.MakeWitnessAdoptWindow();
        for (int i = 0; i < 20; i++)
        {
            using var item = window[0];
            AssertEqual(73, item.Identifier);
        }
        foreach (var item in window)
        {
            using (item) AssertEqual(73, item.Identifier);
        }
        using var survivor = window[0];
        window.Dispose();
        AssertEqual(0, Functions.GetWitnessLifetimeDeinits(), "returned Adopt owns independent storage");
        AssertEqual(73, survivor.Identifier);
        survivor.Dispose();
        AssertOneNativeDeinit();
    }

    public void TestClassSlotReturnsObjectAndTransfersExactlyOneReference()
    {
        Functions.ResetWitnessLifetimeCounts();
        var window = Functions.MakeWitnessClassWindow();
        for (int i = 0; i < 20; i++)
        {
            using var item = window[0];
            AssertEqual(73, item.Identifier, "the wrapper contains an object pointer, not its slot address");
        }
        foreach (var item in window)
        {
            using (item) AssertEqual(73, item.Identifier);
        }
        using var survivor = window[0];
        window.Dispose();
        AssertEqual(0, Functions.GetWitnessLifetimeDeinits());
        AssertEqual(73, survivor.Identifier);
        survivor.Dispose();
        AssertOneNativeDeinit();
    }

    public void TestBoundsAndDisposedParentNeverDestroyUninitializedResults()
    {
        Functions.ResetWitnessLifetimeCounts();
        var window = Functions.MakeWitnessCopyWindow();
        AssertThrows<ArgumentOutOfRangeException>(() => { _ = window[-1]; });
        AssertThrows<ArgumentOutOfRangeException>(() => { _ = window[1]; });
        AssertEqual(0, Functions.GetWitnessLifetimeDeinits());
        using (var item = window[0]) AssertEqual(73, item.Identifier);
        window.Dispose();
        AssertOneNativeDeinit();
        AssertThrows<ObjectDisposedException>(() => { _ = window[0]; });
        AssertOneNativeDeinit();
    }

    public void TestInlineValueDoesNotRequireSwiftHandle()
    {
        using var window = Functions.MakeWitnessInlineWindow();
        var item = window[0];
        window.Dispose();
        AssertEqual(73, item.Identifier);
    }

    public void TestPodAdoptRetainsResultStorageAfterIndexerReturns()
    {
        using var window = Functions.MakeWitnessPodWindow(73);
        using var item = window[0];
        window.Dispose();
        AssertEqual(73, item.Identifier);
    }

    public void TestLongStringMoveRemainsValidAfterCollectionDisposal()
    {
        using var window = Functions.MakeWitnessStringWindow();
        using var item = window[0];
        window.Dispose();
        AssertEqual(string.Concat(System.Linq.Enumerable.Repeat("owned collection string ", 20)), item.ToString());
    }

    public void TestPodAdoptDictionaryLookupRemoveAndEntrySlotsOwnIndependentStorage()
    {
        using var window = Functions.MakeWitnessPodWindow(73);
        using var input = window[0];
        using var secondWindow = Functions.MakeWitnessPodWindow(91);
        using var secondInput = secondWindow[0];
        using var dictionary = new SwiftDictionary<int, WitnessLifetimePod>();
        dictionary[1] = input;
        dictionary[2] = secondInput;
        AssertTrue(dictionary.TryGetValue(1, out var found));
        using (found) AssertEqual(73, found.Identifier);
        foreach (var entry in dictionary)
        {
            using (entry.Value) AssertEqual(entry.Key == 1 ? 73 : 91, entry.Value.Identifier,
                "each tuple-interior value owns separate allocation after iterator storage reuse");
        }
        using var removed = dictionary.RemoveValue(1);
        dictionary.Dispose();
        AssertEqual(73, removed.Identifier);
    }

    public void TestPodAdoptSetSnapshotOwnsIndependentStorage()
    {
        using var window = Functions.MakeWitnessPodWindow(73);
        using var input = window[0];
        using var secondWindow = Functions.MakeWitnessPodWindow(91);
        using var secondInput = secondWindow[0];
        using var set = new SwiftSet<WitnessLifetimePod>(new[] { input, secondInput });
        var identifiers = new System.Collections.Generic.HashSet<int>();
        foreach (var item in set)
        {
            using (item) identifiers.Add(item.Identifier);
        }
        AssertTrue(identifiers.SetEquals(new[] { 73, 91 }), "snapshot elements survive iterator storage reuse independently");
    }

    public void TestPodAdoptGenericClosureResultSurvivesAlignedBufferRelease()
    {
        using var window = Functions.MakeWitnessPodWindow(73);
        using var input = window[0];
        using var fixture = new GenericArgClosureFixture();
        using var result = fixture.Apply<WitnessLifetimePod>(item => item, input);
        AssertEqual(73, result.Identifier, "generic closure releases its aligned source result buffer");
    }

    private void AssertOneNativeDeinit()
    {
        AssertEqual(1, Functions.GetWitnessLifetimeAllocations(), "one native reference created");
        AssertEqual(1, Functions.GetWitnessLifetimeDeinits(), "native reference destroyed exactly once");
    }

    public void TestMoveCarrierTransfersNativeReference()
    {
        RegisterCarriers();
        Functions.ResetWitnessLifetimeCounts();
        using var original = Functions.MakeWitnessAdoptWindow();
        using var window = ReinterpretWindow<MoveCarrier>(original);
        original.Dispose();
        using var item = window[0];
        window.Dispose();
        AssertEqual(0, Functions.GetWitnessLifetimeDeinits());
        AssertEqual(73, item.Identifier);
        item.Dispose();
        AssertOneNativeDeinit();
    }

    public void TestInlineCarrierTransfersNativeReference()
    {
        RegisterCarriers();
        Functions.ResetWitnessLifetimeCounts();
        using var original = Functions.MakeWitnessAdoptWindow();
        using var window = ReinterpretWindow<InlineCarrier>(original);
        original.Dispose();
        var item = window[0];
        window.Dispose();
        AssertEqual(0, Functions.GetWitnessLifetimeDeinits());
        AssertEqual(73, item.Identifier);
        item.Dispose();
        AssertOneNativeDeinit();
    }

    public void TestCopyConversionFailureDestroysInitializedSlot() => AssertConversionFailure<ThrowingCopyCarrier>();
    public void TestAdoptConversionFailureDestroysInitializedSlot() => AssertConversionFailure<ThrowingAdoptCarrier>();
    public void TestMoveConversionFailureDestroysInitializedSlot() => AssertConversionFailure<ThrowingMoveCarrier>();
    public void TestInlineConversionFailureDestroysInitializedSlot() => AssertConversionFailure<ThrowingInlineCarrier>();

    public unsafe void TestClassConversionFailureReleasesSlotReference()
    {
        SwiftMarshal.RegisterSwiftObjectFactory<ThrowingClassCarrier>();
        SwiftMarshal.RegisterPayloadSemantics(typeof(ThrowingClassCarrier), PayloadConstructionSemantics.Adopt);
        Functions.ResetWitnessLifetimeCounts();
        using var original = Functions.MakeWitnessClassWindow();
        var metadata = SwiftObjectHelper<WitnessLifetimeWindow<WitnessLifetimeReference>>.GetTypeMetadata();
        void* copy = NativeMemory.Alloc(metadata.Size);
        SwiftMarshal.CopyWireBufferRetains((IntPtr)copy, original.Payload.DangerousGetHandle(), metadata);
        using var window = SwiftMarshal.MarshalFromSwift<WitnessLifetimeWindow<ThrowingClassCarrier>>((IntPtr)copy);
        original.Dispose();
        for (int i = 0; i < 10; i++)
            AssertThrows<InvalidOperationException>(() => { _ = window[0]; });
        AssertEqual(0, Functions.GetWitnessLifetimeDeinits());
        window.Dispose();
        AssertOneNativeDeinit();
    }

    private void AssertConversionFailure<T>()
    {
        RegisterCarriers();
        Functions.ResetWitnessLifetimeCounts();
        using var original = Functions.MakeWitnessAdoptWindow();
        using var window = ReinterpretWindow<T>(original);
        original.Dispose();
        for (int i = 0; i < 10; i++)
            AssertThrows<InvalidOperationException>(() => { _ = window[0]; });
        AssertEqual(0, Functions.GetWitnessLifetimeDeinits(), "failed result conversion preserves the collection");
        window.Dispose();
        AssertOneNativeDeinit();
    }

    // Test-only aliases have precisely WitnessLifetimeValue's native metadata, layout and
    // ownership. VWT copy preserves the collection's actual generic instantiation; only its
    // managed element factory changes, so every operation reaches the generated indexer.
    private static unsafe WitnessLifetimeWindow<T> ReinterpretWindow<T>(WitnessLifetimeWindow<WitnessLifetimeValue> source)
    {
        var metadata = SwiftObjectHelper<WitnessLifetimeWindow<WitnessLifetimeValue>>.GetTypeMetadata();
        void* copy = NativeMemory.Alloc(metadata.Size);
        SwiftMarshal.CopyWireBufferRetains((IntPtr)copy, source.Payload.DangerousGetHandle(), metadata);
        return SwiftMarshal.MarshalFromSwift<WitnessLifetimeWindow<T>>((IntPtr)copy);
    }

    private static TypeMetadata ValueMetadata => SwiftObjectHelper<WitnessLifetimeValue>.GetTypeMetadata();

    private static void RegisterCarriers()
    {
        SwiftMarshal.RegisterSwiftObjectFactory<MoveCarrier>();
        SwiftMarshal.RegisterSwiftObjectFactory<InlineCarrier>();
        SwiftMarshal.RegisterSwiftObjectFactory<ThrowingCopyCarrier>();
        SwiftMarshal.RegisterSwiftObjectFactory<ThrowingAdoptCarrier>();
        SwiftMarshal.RegisterSwiftObjectFactory<ThrowingMoveCarrier>();
        SwiftMarshal.RegisterSwiftObjectFactory<ThrowingInlineCarrier>();
        SwiftMarshal.RegisterPayloadSemantics(typeof(MoveCarrier), PayloadConstructionSemantics.Move);
        SwiftMarshal.RegisterPayloadSemantics(typeof(ThrowingCopyCarrier), PayloadConstructionSemantics.Copy);
        SwiftMarshal.RegisterPayloadSemantics(typeof(ThrowingAdoptCarrier), PayloadConstructionSemantics.Adopt);
        SwiftMarshal.RegisterPayloadSemantics(typeof(ThrowingMoveCarrier), PayloadConstructionSemantics.Move);
    }

    private static unsafe int ReadIdentifier(IntPtr payload)
    {
        using var copy = SwiftMarshal.ExtractCopiedValue<WitnessLifetimeValue>((void*)payload, ValueMetadata.Size);
        return copy.Identifier;
    }

    public sealed unsafe class MoveCarrier : ISwiftStruct
    {
        private IntPtr payload;
        private MoveCarrier(IntPtr source)
        {
            payload = (IntPtr)NativeMemory.Alloc(ValueMetadata.Size);
            new ReadOnlySpan<byte>((void*)source, checked((int)ValueMetadata.Size))
                .CopyTo(new Span<byte>((void*)payload, checked((int)ValueMetadata.Size)));
        }
        public int Identifier => ReadIdentifier(payload);
        public IntPtr SwiftHandle => payload;
        public void Dispose()
        {
            if (payload == IntPtr.Zero) return;
            SwiftMarshal.DestroyWireBufferRetains(payload, ValueMetadata);
            NativeMemory.Free((void*)payload);
            payload = IntPtr.Zero;
        }
        public static TypeMetadata GetTypeMetadata() => ValueMetadata;
        public static PayloadConstructionSemantics PayloadConstructionSemantics => PayloadConstructionSemantics.Move;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new MoveCarrier(payload);
        public int MarshalToSwift(ref Span<byte> destination) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class => throw new NotSupportedException();
    }

    public unsafe struct InlineCarrier : ISwiftObject
    {
        private IntPtr reference;
        public int Identifier { get { var slot = reference; return ReadIdentifier((IntPtr)(&slot)); } }
        public void Dispose()
        {
            if (reference == IntPtr.Zero) return;
            var slot = reference;
            SwiftMarshal.DestroyWireBufferRetains((IntPtr)(&slot), ValueMetadata);
            reference = IntPtr.Zero;
        }
        public static TypeMetadata GetTypeMetadata() => ValueMetadata;
        public static PayloadConstructionSemantics PayloadConstructionSemantics => PayloadConstructionSemantics.Inline;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new InlineCarrier { reference = *(IntPtr*)payload };
        public int MarshalToSwift(ref Span<byte> destination) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class => throw new NotSupportedException();
    }

    public abstract class ThrowingCarrier
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> destination) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class => throw new NotSupportedException();
    }
    public sealed class ThrowingCopyCarrier : ThrowingCarrier, ISwiftStruct
    {
        public static TypeMetadata GetTypeMetadata() => ValueMetadata;
        public static PayloadConstructionSemantics PayloadConstructionSemantics => PayloadConstructionSemantics.Copy;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new InvalidOperationException("Injected Copy conversion failure");
    }
    public sealed class ThrowingAdoptCarrier : ThrowingCarrier, ISwiftStruct
    {
        public static TypeMetadata GetTypeMetadata() => ValueMetadata;
        public static PayloadConstructionSemantics PayloadConstructionSemantics => PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new InvalidOperationException("Injected Adopt conversion failure");
    }
    public sealed class ThrowingMoveCarrier : ThrowingCarrier, ISwiftStruct
    {
        public static TypeMetadata GetTypeMetadata() => ValueMetadata;
        public static PayloadConstructionSemantics PayloadConstructionSemantics => PayloadConstructionSemantics.Move;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new InvalidOperationException("Injected Move conversion failure");
    }
    public sealed class ThrowingClassCarrier : ThrowingCarrier, ISwiftObject
    {
        public static TypeMetadata GetTypeMetadata() => SwiftObjectHelper<WitnessLifetimeReference>.GetTypeMetadata();
        public static PayloadConstructionSemantics PayloadConstructionSemantics => PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new InvalidOperationException("Injected class conversion failure");
    }
    public struct ThrowingInlineCarrier : ISwiftObject
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> destination) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => ValueMetadata;
        public static PayloadConstructionSemantics PayloadConstructionSemantics => PayloadConstructionSemantics.Inline;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new InvalidOperationException("Injected Inline conversion failure");
    }
}
