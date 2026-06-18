// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Microsoft.Win32.SafeHandles;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Abstract base for the C# wrappers over Swift's KeyPath reference class hierarchy
/// (AnyKeyPath, PartialKeyPath&lt;Root&gt;, KeyPath&lt;Root,Value&gt;,
/// WritableKeyPath&lt;Root,Value&gt;, ReferenceWritableKeyPath&lt;Root,Value&gt;).
///
/// <para>
/// Each wrapper IS a <see cref="SafeHandle"/> — the handle field directly stores the
/// retained Swift class object pointer. The C# inheritance chain mirrors the Swift
/// chain so up- and down-casts work naturally on the managed side.
/// </para>
///
/// <para>ARC contract:</para>
/// <list type="bullet">
///   <item>OUT (Swift returns): pointer is +1 retained; the wrapper adopts ownership in
///         its constructor — no extra retain.</item>
///   <item>IN (C# passes): pointer is borrowed (@guaranteed); the wrapper outlives the
///         call frame, so <see cref="SafeHandle.DangerousGetHandle"/> alone is enough —
///         no <c>DangerousAddRef</c> needed.</item>
///   <item>Finalizer: routes through <see cref="SwiftReleaseTrampoline.Release"/> to
///         avoid Mono's <c>jit-info.c:918 !ji->async</c> assertion. Explicit
///         <see cref="Dispose"/> uses <see cref="Arc.Release"/> (with
///         <c>swift_isDeallocating</c> defence).</item>
/// </list>
///
/// <para>Equality is value-based via <c>AnyKeyPath.==</c>, dispatched through the
/// <c>SBW_AnyKeyPath_Equals</c> shim. NEVER compare wrapper instances by reference
/// — cross-module compilation can produce two distinct Swift objects for the same
/// logical key path.</para>
///
/// <para>Thread safety mirrors Swift's: KeyPaths are <c>Sendable</c> when their Root
/// and Value are. C# has no compile-time Sendable; treat wrappers as safe to pass
/// between threads when the Swift APIs they originate from treat them that way.</para>
/// </summary>
[DebuggerDisplay("{DebugDisplay}")]
public abstract class SwiftKeyPathHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Tracks whether <see cref="Dispose"/> was called explicitly (vs the GC finalizer).
    /// Explicit dispose runs <see cref="Arc.Release"/> on the user thread — safe to take
    /// the defensive double-release check. Finalizer goes through
    /// <see cref="SwiftReleaseTrampoline.Release"/> to avoid the Mono JIT crash.
    /// </summary>
    private volatile bool _explicitDispose;

    /// <summary>
    /// Constructs a key-path handle from a retained Swift class pointer.
    /// Caller MUST transfer a +1 ARC retain — the wrapper adopts ownership.
    /// </summary>
    protected SwiftKeyPathHandle(IntPtr retainedKeyPathPointer)
        : base(ownsHandle: true)
    {
        SetHandle(retainedKeyPathPointer);
    }

    private string DebugDisplay => IsClosed || IsInvalid
        ? $"{GetType().Name} [DISPOSED]"
        : $"{GetType().Name} (0x{handle:X})";

    /// <summary>
    /// Bridges the wrapper to the generator's <c>.Payload</c> idiom that all generated
    /// class-typed parameter marshalling assumes (mirrors <c>SwiftClassHandle&lt;T&gt;.Payload</c>
    /// on classes that compose a SafeHandle). Because <see cref="SwiftKeyPathHandle"/> IS
    /// the SafeHandle (no composition layer), the property returns <c>this</c>. Lets
    /// emitted code like <c>new SafeHandlePin(kp.Payload)</c> and direct
    /// <c>SafeHandle</c> P/Invoke binding (<c>a.Payload</c> → <c>SafeHandle</c>) resolve
    /// without a KeyPath-specific generator branch.
    /// </summary>
    public SafeHandle Payload => this;

    /// <inheritdoc/>
    public new void Dispose()
    {
        _explicitDispose = true;
        GC.SuppressFinalize(this);
        base.Dispose();
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        if (handle == IntPtr.Zero)
            return true;

        if (SwiftExitGuard.IsProcessExiting && !_explicitDispose)
        {
            handle = IntPtr.Zero;
            return true;
        }

        try
        {
            if (_explicitDispose)
                Arc.Release(handle);
            else
                SwiftReleaseTrampoline.Release(handle);
        }
        catch
        {
            // ReleaseHandle must not throw per SafeHandle contract.
        }

        handle = IntPtr.Zero;
        return true;
    }

    /// <summary>
    /// Value-equality check against another key path (dispatches to Swift's
    /// <c>AnyKeyPath.==</c> via the <c>SBW_AnyKeyPath_Equals</c> shim). Returns
    /// true when both wrap the same null pointer, false when exactly one is null.
    /// </summary>
    public bool ValueEquals(SwiftKeyPathHandle? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        if (IsInvalid || other.IsInvalid) return IsInvalid && other.IsInvalid;
        return SwiftKeyPathRuntime.AnyKeyPath_Equals(handle, other.handle);
    }

    /// <summary>
    /// Swift's <c>AnyKeyPath.hashValue</c> via <c>SBW_AnyKeyPath_HashValue</c>.
    /// Returns 0 for an invalid handle (matches Swift's empty-path hash convention
    /// well enough for C#'s <see cref="object.GetHashCode"/> contract — equal paths
    /// produce equal hashes).
    /// </summary>
    public int ValueHash()
    {
        if (IsInvalid) return 0;
        return SwiftKeyPathRuntime.AnyKeyPath_HashValue(handle);
    }
}

/// <summary>
/// C# projection of Swift's <c>AnyKeyPath</c> — the type-erased base of every key path.
/// Use this when the Swift API surfaces a key path whose Root and Value aren't fixed
/// statically (e.g. <c>NSObject.value(forKeyPath:)</c>-style introspection).
/// </summary>
public class AnyKeyPath : SwiftKeyPathHandle, ISwiftObject
{
    /// <summary>Constructs an AnyKeyPath from a +1 retained Swift class pointer.</summary>
    public AnyKeyPath(IntPtr retainedPointer) : base(retainedPointer) { }

    static TypeMetadata ISwiftObject.GetTypeMetadata() =>
        TypeMetadata.Cache.GetOrAdd(typeof(AnyKeyPath),
            _ => SwiftKeyPathRuntime.AnyKeyPathMetadata(TypeMetadataRequest.Complete));

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload) =>
        new AnyKeyPath(payload);

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Adopt;

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() =>
        throw new SwiftRuntimeException(
            $"AnyKeyPath has no protocol conformance descriptor registered for {typeof(TProtocol).Name}.");

    IntPtr ISwiftObject.SwiftHandle => DangerousGetHandle();

    [EditorBrowsable(EditorBrowsableState.Never)]
    unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = SwiftObjectHelper<AnyKeyPath>.GetTypeMetadata();
        return MarshalKeyPathToSwift(this, metadata, ref swiftDestSpan);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is SwiftKeyPathHandle other && ValueEquals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => ValueHash();

    /// <summary>
    /// Shared MarshalToSwift implementation for all five wrapper variants. Mirrors
    /// the generated class-handle path (<c>VWT-&gt;InitializeWithCopy</c> over a
    /// local copy of the class pointer) so the ARC contract matches generated
    /// bindings exactly.
    /// </summary>
    internal static unsafe int MarshalKeyPathToSwift(
        SwiftKeyPathHandle handle, TypeMetadata metadata, ref Span<byte> swiftDestSpan)
    {
        if ((int)metadata.Size > swiftDestSpan.Length)
        {
            throw new ArgumentException(
                $"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
        }
        fixed (void* swiftDest = swiftDestSpan)
        {
            bool success = false;
            handle.DangerousAddRef(ref success);
            try
            {
                IntPtr selfPtr = handle.DangerousGetHandle();
                metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, &selfPtr, metadata);
                return (int)metadata.Size;
            }
            finally
            {
                if (success) handle.DangerousRelease();
            }
        }
    }
}

/// <summary>
/// C# projection of Swift's <c>PartialKeyPath&lt;Root&gt;</c> — Root is fixed, Value
/// is erased. Typed enough for compile-time root checks; Value resolution still
/// happens dynamically.
/// </summary>
public class PartialKeyPath<TRoot> : AnyKeyPath, ISwiftObject
{
    /// <summary>Constructs a PartialKeyPath from a +1 retained Swift class pointer.</summary>
    public PartialKeyPath(IntPtr retainedPointer) : base(retainedPointer) { }

    static TypeMetadata ISwiftObject.GetTypeMetadata() =>
        TypeMetadata.Cache.GetOrAdd(typeof(PartialKeyPath<TRoot>),
            _ => SwiftKeyPathRuntime.PartialKeyPathMetadata(
                TypeMetadataRequest.Complete,
                TypeMetadata.GetTypeMetadataOrThrow<TRoot>()));

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload) =>
        new PartialKeyPath<TRoot>(payload);

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Adopt;

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() =>
        throw new SwiftRuntimeException(
            $"PartialKeyPath<{typeof(TRoot).Name}> has no protocol conformance descriptor registered for {typeof(TProtocol).Name}.");

    [EditorBrowsable(EditorBrowsableState.Never)]
    unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = SwiftObjectHelper<PartialKeyPath<TRoot>>.GetTypeMetadata();
        return MarshalKeyPathToSwift(this, metadata, ref swiftDestSpan);
    }
}

/// <summary>
/// C# projection of Swift's <c>KeyPath&lt;Root, Value&gt;</c> — fully typed, read-only
/// access path from a Root to a Value. Use this for read-only access patterns
/// (<c>root[keyPath: kp]</c>).
/// </summary>
public class KeyPath<TRoot, TValue> : PartialKeyPath<TRoot>, ISwiftObject
{
    /// <summary>Constructs a KeyPath from a +1 retained Swift class pointer.</summary>
    public KeyPath(IntPtr retainedPointer) : base(retainedPointer) { }

    static TypeMetadata ISwiftObject.GetTypeMetadata() =>
        TypeMetadata.Cache.GetOrAdd(typeof(KeyPath<TRoot, TValue>),
            _ => SwiftKeyPathRuntime.KeyPathMetadata(
                TypeMetadataRequest.Complete,
                TypeMetadata.GetTypeMetadataOrThrow<TRoot>(),
                TypeMetadata.GetTypeMetadataOrThrow<TValue>()));

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload) =>
        new KeyPath<TRoot, TValue>(payload);

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Adopt;

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() =>
        throw new SwiftRuntimeException(
            $"KeyPath<{typeof(TRoot).Name}, {typeof(TValue).Name}> has no protocol conformance descriptor registered for {typeof(TProtocol).Name}.");

    [EditorBrowsable(EditorBrowsableState.Never)]
    unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = SwiftObjectHelper<KeyPath<TRoot, TValue>>.GetTypeMetadata();
        return MarshalKeyPathToSwift(this, metadata, ref swiftDestSpan);
    }
}

/// <summary>
/// C# projection of Swift's <c>WritableKeyPath&lt;Root, Value&gt;</c> — fully typed,
/// supports value-type (in-place) mutation: <c>root[keyPath: wkp] = newValue</c>.
/// </summary>
public class WritableKeyPath<TRoot, TValue> : KeyPath<TRoot, TValue>, ISwiftObject
{
    /// <summary>Constructs a WritableKeyPath from a +1 retained Swift class pointer.</summary>
    public WritableKeyPath(IntPtr retainedPointer) : base(retainedPointer) { }

    static TypeMetadata ISwiftObject.GetTypeMetadata() =>
        TypeMetadata.Cache.GetOrAdd(typeof(WritableKeyPath<TRoot, TValue>),
            _ => SwiftKeyPathRuntime.WritableKeyPathMetadata(
                TypeMetadataRequest.Complete,
                TypeMetadata.GetTypeMetadataOrThrow<TRoot>(),
                TypeMetadata.GetTypeMetadataOrThrow<TValue>()));

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload) =>
        new WritableKeyPath<TRoot, TValue>(payload);

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Adopt;

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() =>
        throw new SwiftRuntimeException(
            $"WritableKeyPath<{typeof(TRoot).Name}, {typeof(TValue).Name}> has no protocol conformance descriptor registered for {typeof(TProtocol).Name}.");

    [EditorBrowsable(EditorBrowsableState.Never)]
    unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = SwiftObjectHelper<WritableKeyPath<TRoot, TValue>>.GetTypeMetadata();
        return MarshalKeyPathToSwift(this, metadata, ref swiftDestSpan);
    }
}

/// <summary>
/// C# projection of Swift's <c>ReferenceWritableKeyPath&lt;Root, Value&gt;</c> — fully
/// typed key path that mutates a reference-type Root's property in place
/// (<c>refRoot[keyPath: rwkp] = newValue</c> without inout).
/// </summary>
public class ReferenceWritableKeyPath<TRoot, TValue> : WritableKeyPath<TRoot, TValue>, ISwiftObject
{
    /// <summary>Constructs a ReferenceWritableKeyPath from a +1 retained Swift class pointer.</summary>
    public ReferenceWritableKeyPath(IntPtr retainedPointer) : base(retainedPointer) { }

    static TypeMetadata ISwiftObject.GetTypeMetadata() =>
        TypeMetadata.Cache.GetOrAdd(typeof(ReferenceWritableKeyPath<TRoot, TValue>),
            _ => SwiftKeyPathRuntime.ReferenceWritableKeyPathMetadata(
                TypeMetadataRequest.Complete,
                TypeMetadata.GetTypeMetadataOrThrow<TRoot>(),
                TypeMetadata.GetTypeMetadataOrThrow<TValue>()));

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload) =>
        new ReferenceWritableKeyPath<TRoot, TValue>(payload);

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Adopt;

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() =>
        throw new SwiftRuntimeException(
            $"ReferenceWritableKeyPath<{typeof(TRoot).Name}, {typeof(TValue).Name}> has no protocol conformance descriptor registered for {typeof(TProtocol).Name}.");

    [EditorBrowsable(EditorBrowsableState.Never)]
    unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = SwiftObjectHelper<ReferenceWritableKeyPath<TRoot, TValue>>.GetTypeMetadata();
        return MarshalKeyPathToSwift(this, metadata, ref swiftDestSpan);
    }
}

/// <summary>
/// P/Invoke surface for Swift KeyPath family metadata accessors plus the
/// AnyKeyPath equality/hash shims exported from <c>SwiftBindingsRuntime</c>.
/// Equality and hash are routed through <c>@_cdecl</c> shims rather than direct
/// CallConvSwift calls into <c>libswiftCore</c>: the shim path is empirically
/// stable on Mono iOS Simulator, where direct CallConvSwift dispatch into
/// stdlib statics can contaminate JIT state.
/// </summary>
internal static class SwiftKeyPathRuntime
{
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$ss10AnyKeyPathCMa")]
    internal static extern TypeMetadata AnyKeyPathMetadata(TypeMetadataRequest request);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$ss14PartialKeyPathCMa")]
    internal static extern TypeMetadata PartialKeyPathMetadata(TypeMetadataRequest request, TypeMetadata rootMetadata);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$ss7KeyPathCMa")]
    internal static extern TypeMetadata KeyPathMetadata(TypeMetadataRequest request, TypeMetadata rootMetadata, TypeMetadata valueMetadata);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$ss15WritableKeyPathCMa")]
    internal static extern TypeMetadata WritableKeyPathMetadata(TypeMetadataRequest request, TypeMetadata rootMetadata, TypeMetadata valueMetadata);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$ss24ReferenceWritableKeyPathCMa")]
    internal static extern TypeMetadata ReferenceWritableKeyPathMetadata(TypeMetadataRequest request, TypeMetadata rootMetadata, TypeMetadata valueMetadata);

    [DllImport("SwiftBindingsRuntime", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_AnyKeyPath_Equals")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool AnyKeyPath_Equals(IntPtr a, IntPtr b);

    [DllImport("SwiftBindingsRuntime", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_AnyKeyPath_HashValue")]
    internal static extern int AnyKeyPath_HashValue(IntPtr keyPath);
}
