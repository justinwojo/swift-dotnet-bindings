// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 11: the unconstrained marshal seam resolves a wrapper's
/// <see cref="PayloadConstructionSemantics"/> through
/// <see cref="SwiftMarshal.GetPayloadSemanticsForType"/> — short-circuiting non-ISwiftObject and
/// value-type ISwiftObject to <see cref="PayloadConstructionSemantics.Inline"/>, then the by-Type
/// dispatcher cache, then a reflection backstop that reads the type's declared static member. These
/// tests pin that dispatch on the desktop CoreCLR host (where reflection always resolves the
/// declaration), covering the short-circuits, the reflection backstop, explicit registration, and
/// the representative runtime types whose declared semantics the leak fix depends on.
/// </summary>
public class PayloadSemanticsDispatchTests
{
    // ─── Fakes (distinct per scenario so the process-wide dispatcher cache doesn't collide) ───

    /// <summary>Value-type ISwiftObject declaring Adopt — the value-type short-circuit must still win.</summary>
    private struct ValueTypeFake : ISwiftObject
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

    /// <summary>Reference-type ISwiftObject declaring Copy — resolved only via the reflection backstop (unregistered).</summary>
    private sealed class CopyClassFake : ISwiftObject
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

    /// <summary>Reference-type ISwiftObject declaring Adopt, but explicitly registered as Move — the cache must win.</summary>
    private sealed class SeededClassFake : ISwiftObject
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

    // ─── Short-circuits ───

    [Fact]
    public void NonISwiftObjectPrimitive_ShortCircuitsToInline()
    {
        Assert.Equal(PayloadConstructionSemantics.Inline, SwiftMarshal.GetPayloadSemanticsForType(typeof(int)));
        Assert.Equal(PayloadConstructionSemantics.Inline, SwiftMarshal.GetPayloadSemantics<int>());
    }

    [Fact]
    public void SystemString_IsNotISwiftObject_ShortCircuitsToInline()
    {
        // System.String has no Swift metadata and is not an ISwiftObject — Inline, never a cache lookup.
        Assert.Equal(PayloadConstructionSemantics.Inline, SwiftMarshal.GetPayloadSemanticsForType(typeof(string)));
    }

    [Fact]
    public void ValueTypeISwiftObject_ShortCircuitsToInline_IgnoringDeclaration()
    {
        // A value-type ISwiftObject reads by value (SwiftHandle would throw), so the seam returns Inline
        // BEFORE the cache/backstop — even though the struct declares Adopt.
        Assert.Equal(PayloadConstructionSemantics.Inline, SwiftMarshal.GetPayloadSemanticsForType(typeof(ValueTypeFake)));
        Assert.Equal(PayloadConstructionSemantics.Inline, SwiftMarshal.GetPayloadSemantics<ValueTypeFake>());
    }

    // ─── Reflection backstop + cache ───

    [Fact]
    public void ReferenceTypeISwiftObject_ResolvesDeclaredSemantics_ViaReflectionBackstop()
    {
        // Unregistered reference type: the backstop reads the declared static member, then caches it.
        Assert.Equal(PayloadConstructionSemantics.Copy, SwiftMarshal.GetPayloadSemanticsForType(typeof(CopyClassFake)));
        // Second call hits the cache and must agree.
        Assert.Equal(PayloadConstructionSemantics.Copy, SwiftMarshal.GetPayloadSemanticsForType(typeof(CopyClassFake)));
        Assert.Equal(PayloadConstructionSemantics.Copy, SwiftMarshal.GetPayloadSemantics<CopyClassFake>());
    }

    [Fact]
    public void RegisterPayloadSemantics_SeedsCache_AheadOfReflectionBackstop()
    {
        // Explicit registration populates the by-Type cache, so TryGet returns before the backstop —
        // the registered Move wins over the type's declared Adopt.
        SwiftMarshal.RegisterPayloadSemantics(typeof(SeededClassFake), PayloadConstructionSemantics.Move);
        Assert.Equal(PayloadConstructionSemantics.Move, SwiftMarshal.GetPayloadSemanticsForType(typeof(SeededClassFake)));
    }

    // ─── Representative runtime types (the leak fix keys on these declared values) ───

    // Passed as typeof(...) literals (not a [Theory] Type parameter) so the statically-known type
    // satisfies GetPayloadSemanticsForType's [DynamicallyAccessedMembers] annotation without an IL2067.
    [Fact]
    public void SwiftString_ResolvesItsDeclaredMoveSemantics()
    {
        Assert.Equal(PayloadConstructionSemantics.Move, SwiftMarshal.GetPayloadSemanticsForType(typeof(SwiftString)));
    }

    [Fact]
    public void Hasher_ResolvesItsDeclaredAdoptSemantics()
    {
        Assert.Equal(PayloadConstructionSemantics.Adopt, SwiftMarshal.GetPayloadSemanticsForType(typeof(Hasher)));
    }

    [Fact]
    public void SwiftContainer_IsCopy_SoBorrowedMarshalKeepsItsOwnedPlus1()
    {
        // SwiftArray/Dictionary/Set/Result/Optional declare Copy: NewFromPayload InitializeWithCopy-s its
        // own +1, so the borrowed marshal must NOT suppress the finalizer (the leak Finding 11 fixes).
        Assert.Equal(PayloadConstructionSemantics.Copy, SwiftMarshal.GetPayloadSemanticsForType(typeof(SwiftArray<int>)));
    }
}
