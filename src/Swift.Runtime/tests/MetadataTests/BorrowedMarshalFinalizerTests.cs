// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Borrowed callback wrappers must own independent storage. Unresolved metadata may not
/// silently adopt/move Swift's buffer; Copy constructors remain self-owning.
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
    /// Move shape without a legacy ConsumePayloadBuffer override. Missing metadata must reject
    /// before its factory can move a borrowed pointer.
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
    /// Move shape with the legacy consume seam. Its presence must not bypass the independent-copy
    /// requirement when metadata is missing.
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
    public void MarshalCallbackArg_AdoptWithoutMetadata_RejectsBeforeConstructing()
    {
        bool constructed = false;
        NewFromPayloadDispatcher.Register(typeof(AdoptFake), _ => { constructed = true; return new AdoptFake(); });
        Assert.Throws<SwiftRuntimeException>(() => SwiftMarshal.MarshalCallbackArg<AdoptFake>(new IntPtr(0x5601)));
        Assert.False(constructed);
    }

    [Fact]
    public void MarshalCallbackArg_MoveWithoutMetadata_RejectsBeforeConstructing()
    {
        bool constructed = false;
        NewFromPayloadDispatcher.Register(typeof(MoveFake), _ => { constructed = true; return new MoveFake(); });
        Assert.Throws<SwiftRuntimeException>(() => SwiftMarshal.MarshalCallbackArg<MoveFake>(new IntPtr(0x5602)));
        Assert.False(constructed);
    }

    [Fact]
    public void MarshalCallbackArg_MoveWithConsumeOverride_StillRequiresIndependentCopy()
    {
        NewFromPayloadDispatcher.Register(typeof(MoveConsumeFake), _ => new MoveConsumeFake());
        Assert.Throws<SwiftRuntimeException>(() => SwiftMarshal.MarshalCallbackArg<MoveConsumeFake>(new IntPtr(0x5605)));
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
    public void MarshalCallbackArg_AdoptDefaultWithoutMetadata_RejectsUnsafeBorrow()
    {
        NewFromPayloadDispatcher.Register(typeof(AdoptDefaultFake), _ => new AdoptDefaultFake());
        Assert.Throws<SwiftRuntimeException>(() => SwiftMarshal.MarshalCallbackArg<AdoptDefaultFake>(new IntPtr(0x5604)));
    }

    [Fact]
    public void SuppressPayloadFinalizer_InterfaceDefault_IsNoOp()
    {
        // Calling the DIM directly on a type with no override must be a safe no-op.
        ISwiftObject obj = new AdoptDefaultFake();
        obj.SuppressPayloadFinalizer();
    }
}
