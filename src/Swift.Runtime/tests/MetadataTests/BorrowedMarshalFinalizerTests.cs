// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 56a: <see cref="SwiftMarshal.MarshalBorrowedFromSwift{T}"/> must suppress a borrowed
/// (+0) wrapper's payload finalizer without per-call reflection. The former implementation did
/// <c>GetType().GetProperty("Payload")</c> + boxed <c>GetValue</c> + <c>GC.SuppressFinalize</c>
/// on every borrowed object parameter; it now dispatches through the
/// <see cref="ISwiftObject.SuppressPayloadFinalizer"/> default-interface method.
///
/// These tests run on the desktop CoreCLR host. Each fake type registers a NewFromPayload factory
/// so the cache-first marshal path (<c>MarshalFromSwiftCore</c>) returns the instance, then the
/// borrowed path invokes the DIM. A type that overrides the member records the call (behavior
/// proof that the non-reflective virtual was dispatched); a type that relies on the default no-op
/// must round-trip cleanly without throwing (the value-type / self-payload / no-payload case).
/// </summary>
public class BorrowedMarshalFinalizerTests
{
    // Distinct fake types per scenario so the process-wide dispatcher cache doesn't collide.

    /// <summary>Heap-backed shape: overrides the DIM to suppress its (fake) payload.</summary>
    private sealed class BorrowedOverrideFake : ISwiftObject
    {
        // Stands in for the SafeHandle a real wrapper would suppress.
        private readonly object _payload = new object();
        public bool PayloadFinalizerSuppressed { get; private set; }

        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload) => new BorrowedOverrideFake();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();

        void ISwiftObject.SuppressPayloadFinalizer()
        {
            GC.SuppressFinalize(_payload);
            PayloadFinalizerSuppressed = true;
        }
    }

    /// <summary>No-payload shape: relies on the default no-op DIM (must not throw).</summary>
    private sealed class BorrowedDefaultFake : ISwiftObject
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload) => new BorrowedDefaultFake();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    [Fact]
    public void MarshalBorrowedFromSwift_InvokesSuppressPayloadFinalizer_OnOverridingType()
    {
        NewFromPayloadDispatcher.Register(typeof(BorrowedOverrideFake), _ => new BorrowedOverrideFake());

        var result = SwiftMarshal.MarshalBorrowedFromSwift<BorrowedOverrideFake>(new IntPtr(0x5601));

        Assert.NotNull(result);
        // The non-reflective DIM was dispatched to the type's override — no GetProperty("Payload").
        Assert.True(result.PayloadFinalizerSuppressed);
    }

    [Fact]
    public void MarshalBorrowedFromSwift_DefaultNoOp_DoesNotThrow_WhenTypeHasNoPayloadOverride()
    {
        NewFromPayloadDispatcher.Register(typeof(BorrowedDefaultFake), _ => new BorrowedDefaultFake());

        // The default no-op covers value-type / self-payload / existential-proxy shapes that
        // previously matched no "Payload" property under the reflection scan.
        var result = SwiftMarshal.MarshalBorrowedFromSwift<BorrowedDefaultFake>(new IntPtr(0x5602));

        Assert.NotNull(result);
    }

    [Fact]
    public void SuppressPayloadFinalizer_InterfaceDefault_IsNoOp()
    {
        // Calling the DIM directly on a type with no override must be a safe no-op.
        ISwiftObject obj = new BorrowedDefaultFake();
        obj.SuppressPayloadFinalizer();
    }
}
