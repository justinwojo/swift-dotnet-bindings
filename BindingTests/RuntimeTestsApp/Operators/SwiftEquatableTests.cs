// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Calls the runtime equality fallback directly, including its Swift Bool boundary.
/// Generated equality operators use different native wrappers and cannot cover it.
/// </summary>
public class SwiftEquatableTests : TestBase
{
    public SwiftEquatableTests(TestResults results) : base(results) { }

    public void TestSwiftIntEquality()
    {
        AssertEqual((nuint)8, TypeMetadata.GetTypeMetadataOrThrow<SwiftIntValue>().Size,
            "Swift.Int payload size");
        TestLogger.Info("Comparing equal and unequal Swift.Int payloads through SwiftEquatable");
        AssertTrue(SwiftEquatable.Equals(new SwiftIntValue(42), new SwiftIntValue(42)),
            "Equal Swift.Int values");
        AssertFalse(SwiftEquatable.Equals(new SwiftIntValue(42), new SwiftIntValue(100)),
            "Unequal Swift.Int values");
    }

    public void TestSwiftIntHighWordEquality()
    {
        long highValue = 0x1_0000_002AL;
        long unsignedLowWordValue = 0xFFFF_FFFFL;
        var high = checked((nint)highValue);
        var unsignedLowWord = checked((nint)unsignedLowWordValue);
        TestLogger.Info($"Comparing full-width Swift.Int payloads: {high}, {unsignedLowWord}");
        AssertTrue(SwiftEquatable.Equals(new SwiftIntValue(high), new SwiftIntValue(high)),
            "Equal Swift.Int values with a nonzero high word");
        AssertFalse(SwiftEquatable.Equals(new SwiftIntValue(42), new SwiftIntValue(high)),
            "Values sharing their low 32 bits remain unequal");
        AssertFalse(SwiftEquatable.Equals(new SwiftIntValue(-1), new SwiftIntValue(unsignedLowWord)),
            "Negative and positive values sharing their low 32 bits remain unequal");
    }

    public void TestLargeEquatableValuePreservesOwnership()
    {
        LifetimeTracker.Reset();
        var text = new string('x', 256) + " — equality ownership";
        using (var left = new RuntimeEquatableValue(text, 7, 11, 23))
        using (var equal = new RuntimeEquatableValue(text, 7, 11, 23))
        using (var unequal = new RuntimeEquatableValue(text, 7, 11, 24))
        {
            AssertTrue(TypeMetadata.GetTypeMetadataOrThrow<TrackedValue>().Size > 3 * (nuint)IntPtr.Size,
                "Nontrivial operand exceeds the existential inline buffer");
            TestLogger.Info("Comparing large String/ARC-backed operands through SwiftEquatable");
            for (int i = 0; i < 200; i++)
            {
                AssertTrue(SwiftEquatable.Equals(new TrackedValue(left), new TrackedValue(equal)),
                    "Equal nontrivial values");
                AssertFalse(SwiftEquatable.Equals(new TrackedValue(left), new TrackedValue(unequal)),
                    "Different last field remains unequal");
            }
            var originalText = left.Text;
            AssertEqual(text, originalText, "Original String survives comparisons");
            AssertEqual(23L, left.Last, "Original last field survives comparisons");
            var live = LifetimeTracker.GetStats();
            AssertEqual(3, live.allocations, "Exactly three native sentinels created");
            AssertEqual(0, live.deallocations, "No original consumed by comparison");
            AssertEqual(3, live.live, "All original sentinels remain alive");
        }
        var released = LifetimeTracker.GetStats();
        AssertEqual(3, released.deallocations, "All sentinels released exactly once");
        AssertEqual(0, released.live, "No comparison retains leaked");
    }

    // Generated == operators have their own wrapper and do not expose this
    // descriptor route. The carrier borrows the generated owner, delegates its
    // owned-copy marshalling, and supplies the concrete Swift Equatable descriptor.
    private readonly struct TrackedValue(RuntimeEquatableValue value) : ISwiftObject
    {
        static TypeMetadata ISwiftObject.GetTypeMetadata() =>
            TypeMetadata.GetTypeMetadataOrThrow<RuntimeEquatableValue>();
        static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics => PayloadConstructionSemantics.Inline;
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        {
            if (typeof(TProtocol) != typeof(IEquatable<TrackedValue>))
                throw new NotSupportedException();
            return ProtocolConformanceDescriptor.LoadFromSymbol(
                "SwiftBindingsTestLib", "$s20SwiftBindingsTestLib21RuntimeEquatableValueVSQAAMc");
        }
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        int ISwiftObject.MarshalToSwift(ref Span<byte> destination) =>
            ((ISwiftObject)value).MarshalToSwift(ref destination);
        public void Dispose() { } // The test owns the borrowed generated wrapper.
    }

    // This app-defined carrier deliberately uses the real Swift.Int metadata and
    // conformance. Its storage, marshal, and materialization paths must all be nint-sized.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SwiftIntValue(nint value) : ISwiftObject
    {
        private readonly nint _value = value;

        static TypeMetadata ISwiftObject.GetTypeMetadata() => TypeMetadata.GetTypeMetadataOrThrow<nint>();
        static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics => PayloadConstructionSemantics.Inline;

        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        {
            if (typeof(TProtocol) != typeof(IEquatable<SwiftIntValue>))
                throw new NotSupportedException();
            return ProtocolConformanceDescriptor.LoadFromSymbol(
                "/usr/lib/swift/libswiftCore.dylib", "$sSiSQsMc");
        }

        static unsafe ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload) =>
            new SwiftIntValue(*(nint*)payload);

        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> destination)
        {
            fixed (byte* address = destination)
                *(nint*)address = _value;
            return sizeof(nint);
        }

        public void Dispose() { }
    }
}
