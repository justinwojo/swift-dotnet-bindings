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
/// finalizer. The two non-owning shapes (<c>Adopt</c>, <c>Move</c>) suppress (the wrapper does not
/// own the borrowed reference and must not free / over-release a buffer Swift still owns); the
/// owning <c>Copy</c> shape does <b>not</b> suppress — its <c>NewFromPayload</c> took an independent
/// <c>+1</c> via <c>InitializeWithCopy</c>, so the SafeHandle must run to <c>Destroy</c> that owned
/// copy. Suppressing it was the leak Finding 11 fixes.
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

    /// <summary>Non-owning Adopt shape: records the suppress DIM dispatch.</summary>
    private sealed class AdoptFake : ISwiftObject
    {
        private readonly object _payload = new object();
        public bool PayloadFinalizerSuppressed { get; private set; }

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
    }

    /// <summary>Non-owning Move shape: records the suppress DIM dispatch.</summary>
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

    /// <summary>Owning Copy shape: records the suppress DIM dispatch (which must NOT fire — leak fix).</summary>
    private sealed class CopyFake : ISwiftObject
    {
        private readonly object _payload = new object();
        public bool PayloadFinalizerSuppressed { get; private set; }

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
    }

    [Fact]
    public void MarshalCallbackArg_MoveSemantics_SuppressesPayloadFinalizer()
    {
        NewFromPayloadDispatcher.Register(typeof(MoveFake), _ => new MoveFake());

        var result = SwiftMarshal.MarshalCallbackArg<MoveFake>(new IntPtr(0x5602));

        Assert.NotNull(result);
        // Move bitwise-transferred a +0 reference Swift still owns — suppress to avoid over-release.
        Assert.True(result.PayloadFinalizerSuppressed);
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
