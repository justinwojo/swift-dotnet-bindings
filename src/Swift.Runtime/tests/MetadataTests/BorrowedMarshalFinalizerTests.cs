// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 11: <see cref="SwiftMarshal.MarshalCallbackArg{T}"/> marshals a borrowed (+0) Swift
/// reference handed to a closure/callback by dispatching on the wrapper type's declared
/// <see cref="PayloadConstructionSemantics"/> — it no longer blanket-suppresses the payload
/// finalizer. <c>Adopt</c> suppresses (the wrapper adopted the borrowed pointer itself — Swift owns
/// that memory outright, so both the Destroy and the free must be foreclosed). <c>Move</c> consumes
/// via <see cref="ISwiftObject.ConsumePayloadBuffer"/>: the wrapper bitwise-transferred the borrowed
/// words into a container it allocated itself, so cleanup must free that container WITHOUT
/// value-witness-destroying the borrowed value (blanket suppression leaked the container per
/// callback); the interface default falls back to suppress for Move types with no separable
/// container. The owning <c>Copy</c> shape does <b>not</b> suppress — its <c>NewFromPayload</c> took
/// an independent <c>+1</c> via <c>InitializeWithCopy</c>, so the SafeHandle must run to
/// <c>Destroy</c> that owned copy. Suppressing it was the leak Finding 11 fixes.
///
/// These tests run on the desktop CoreCLR host. Each fake type returns an invalid
/// <see cref="TypeMetadata"/> (so the class fast path is skipped and the semantics branch runs),
/// declares its <see cref="PayloadConstructionSemantics"/>, and registers a NewFromPayload factory
/// so the cache-first marshal path (<c>MarshalFromSwiftCore</c>) returns the instance. The fakes are
/// unregistered in the by-Type dispatcher, so resolution exercises the reflection backstop
/// (<c>InvokePayloadConstructionSemantics</c>). A type that records the
/// <see cref="ISwiftObject.SuppressPayloadFinalizer"/> call proves whether the borrow path suppressed.
/// </summary>
public class BorrowedMarshalFinalizerTests
{
    // Distinct fake types per scenario so the process-wide dispatcher cache doesn't collide.

    /// <summary>Non-owning Adopt shape: records both borrow-arm DIM dispatches.</summary>
    private sealed class AdoptFake : ISwiftObject
    {
        private readonly object _payload = new object();
        public bool PayloadFinalizerSuppressed { get; private set; }
        public bool PayloadBufferConsumed { get; private set; }

        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new AdoptFake();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;

        void ISwiftObject.SuppressPayloadFinalizer()
        {
            GC.SuppressFinalize(_payload);
            PayloadFinalizerSuppressed = true;
        }

        void ISwiftObject.ConsumePayloadBuffer() => PayloadBufferConsumed = true;
    }

    /// <summary>
    /// Move shape WITHOUT a ConsumePayloadBuffer override: the interface default must fall back to
    /// the conservative suppress treatment (leak-not-crash for a container inseparable from the value).
    /// </summary>
    private sealed class MoveFake : ISwiftObject
    {
        private readonly object _payload = new object();
        public bool PayloadFinalizerSuppressed { get; private set; }

        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new MoveFake();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Move;

        void ISwiftObject.SuppressPayloadFinalizer()
        {
            GC.SuppressFinalize(_payload);
            PayloadFinalizerSuppressed = true;
        }
    }

    /// <summary>
    /// Move shape WITH a separable container (the SwiftString shape): overrides ConsumePayloadBuffer.
    /// The Move arm must consume — freeing the wrapper-owned container stays live — and must NOT
    /// blanket-suppress (that foreclosed the container free and leaked it per callback).
    /// </summary>
    private sealed class MoveConsumeFake : ISwiftObject
    {
        private readonly object _payload = new object();
        public bool PayloadFinalizerSuppressed { get; private set; }
        public bool PayloadBufferConsumed { get; private set; }

        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new MoveConsumeFake();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Move;

        void ISwiftObject.SuppressPayloadFinalizer()
        {
            GC.SuppressFinalize(_payload);
            PayloadFinalizerSuppressed = true;
        }

        void ISwiftObject.ConsumePayloadBuffer() => PayloadBufferConsumed = true;
    }

    /// <summary>Owning Copy shape: records both DIM dispatches (neither must fire — leak fix).</summary>
    private sealed class CopyFake : ISwiftObject
    {
        private readonly object _payload = new object();
        public bool PayloadFinalizerSuppressed { get; private set; }
        public bool PayloadBufferConsumed { get; private set; }

        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new CopyFake();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Copy;

        void ISwiftObject.SuppressPayloadFinalizer()
        {
            GC.SuppressFinalize(_payload);
            PayloadFinalizerSuppressed = true;
        }

        void ISwiftObject.ConsumePayloadBuffer() => PayloadBufferConsumed = true;
    }

    /// <summary>Non-owning Adopt shape with no SuppressPayloadFinalizer override: relies on the default no-op DIM.</summary>
    private sealed class AdoptDefaultFake : ISwiftObject
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.Zero;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new AdoptDefaultFake();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
    }

    [Fact]
    public void MarshalCallbackArg_AdoptSemantics_SuppressesPayloadFinalizer()
    {
        NewFromPayloadDispatcher.Register(typeof(AdoptFake), _ => new AdoptFake());

        var result = SwiftMarshal.MarshalCallbackArg<AdoptFake>(new IntPtr(0x5601));

        Assert.NotNull(result);
        // Adopt does not own the borrowed reference — the borrow path suppresses the payload finalizer.
        Assert.True(result.PayloadFinalizerSuppressed);
        // Adopt has no wrapper-owned container to reclaim: the adopted pointer IS Swift's memory,
        // so freeing it would free Swift-owned memory. The consume seam must not fire.
        Assert.False(result.PayloadBufferConsumed);
    }

    [Fact]
    public void MarshalCallbackArg_MoveSemantics_NoOverride_FallsBackToSuppress()
    {
        NewFromPayloadDispatcher.Register(typeof(MoveFake), _ => new MoveFake());

        var result = SwiftMarshal.MarshalCallbackArg<MoveFake>(new IntPtr(0x5602));

        Assert.NotNull(result);
        // A Move type with NO ConsumePayloadBuffer override reaches the interface default, which
        // must fall back to the conservative suppress (leak-not-crash): a finalizer Destroy would
        // over-release a value Swift still owns.
        Assert.True(result.PayloadFinalizerSuppressed);
    }

    [Fact]
    public void MarshalCallbackArg_MoveSemantics_SeparableContainer_ConsumesInsteadOfSuppressing()
    {
        NewFromPayloadDispatcher.Register(typeof(MoveConsumeFake), _ => new MoveConsumeFake());

        var result = SwiftMarshal.MarshalCallbackArg<MoveConsumeFake>(new IntPtr(0x5605));

        Assert.NotNull(result);
        // The Move-arm leak fix: a Move wrapper with a separable container (SwiftString's shape)
        // must have its container consumed — cleanup frees the wrapper-owned buffer without
        // destroying the borrowed value...
        Assert.True(result.PayloadBufferConsumed);
        // ...and must NOT be blanket-suppressed, which foreclosed the container free and leaked
        // the wrapper's own allocation on every callback invocation.
        Assert.False(result.PayloadFinalizerSuppressed);
    }

    [Fact]
    public void MarshalCallbackArg_CopySemantics_DoesNotSuppressPayloadFinalizer()
    {
        NewFromPayloadDispatcher.Register(typeof(CopyFake), _ => new CopyFake());

        var result = SwiftMarshal.MarshalCallbackArg<CopyFake>(new IntPtr(0x5603));

        Assert.NotNull(result);
        // The leak fix: a Copy wrapper owns its own +1, so the borrow path must NOT suppress its
        // payload finalizer — the SafeHandle has to Destroy the owned copy. Suppressing it leaked.
        Assert.False(result.PayloadFinalizerSuppressed);
        // Copy is fully owning — the borrowed-consume seam must not fire either.
        Assert.False(result.PayloadBufferConsumed);
    }

    [Fact]
    public void MarshalCallbackArg_DefaultNoOp_DoesNotThrow_WhenTypeHasNoPayloadOverride()
    {
        NewFromPayloadDispatcher.Register(typeof(AdoptDefaultFake), _ => new AdoptDefaultFake());

        // A non-owning type with no separately-finalizable payload falls through to the default
        // no-op DIM (the value-type / self-payload / existential-proxy shape) and must not throw.
        var result = SwiftMarshal.MarshalCallbackArg<AdoptDefaultFake>(new IntPtr(0x5604));

        Assert.NotNull(result);
    }

    [Fact]
    public void SuppressPayloadFinalizer_InterfaceDefault_IsNoOp()
    {
        // Calling the DIM directly on a type with no override must be a safe no-op.
        ISwiftObject obj = new AdoptDefaultFake();
        obj.SuppressPayloadFinalizer();
    }
}
