// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Swift.Runtime;

/// <summary>
/// Internal marshalling type for Swift existential containers with 0 witness tables (represents 'Any' or 'any Any').
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. Use the protocol interface types (e.g., IMyProtocol) in your code instead.
/// Size: 4 machine words (32 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ExistentialContainer0 : IExistentialContainer
{
    private IntPtr _payload0;
    private IntPtr _payload1;
    private IntPtr _payload2;
    private TypeMetadata _metadata;

    public IntPtr Payload0 { get => _payload0; set => _payload0 = value; }
    public IntPtr Payload1 { get => _payload1; set => _payload1 = value; }
    public IntPtr Payload2 { get => _payload2; set => _payload2 = value; }
    public TypeMetadata ObjectMetadata { get => _metadata; set => _metadata = value; }

    public IntPtr this[int index]
    {
        get => throw new IndexOutOfRangeException("ExistentialContainer0 has no witness tables");
        set => throw new IndexOutOfRangeException("ExistentialContainer0 has no witness tables");
    }

    public int Count => 0;
    public int SizeOf => 4 * IntPtr.Size;

    public unsafe IntPtr CopyTo(IntPtr memory)
    {
        *(ExistentialContainer0*)memory = this;
        return memory;
    }

    public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
    {
        if (container.SizeOf != SizeOf)
            throw new ArgumentException($"Container size mismatch: expected {SizeOf}, got {container.SizeOf}");
        container.Payload0 = Payload0;
        container.Payload1 = Payload1;
        container.Payload2 = Payload2;
        container.ObjectMetadata = ObjectMetadata;
    }

    /// <summary>
    /// Boxes a C# object into an ExistentialContainer0 for passing as Swift 'Any'.
    /// Supports: bool, int, long (Swift.Int), double, string.
    /// </summary>
    /// <param name="value">The value to box. Must be a supported type.</param>
    /// <returns>An ExistentialContainer0 containing the value with correct Swift metadata.</returns>
    /// <exception cref="ArgumentNullException">Thrown if value is null.</exception>
    /// <exception cref="NotSupportedException">Thrown if the value type is not supported for boxing.</exception>
    public static unsafe ExistentialContainer0 Box(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var container = new ExistentialContainer0();

        switch (value)
        {
            case bool b:
            {
                if (!TypeMetadata.Cache.TryGet(typeof(bool), out var metadata))
                    throw new SwiftRuntimeException("Cannot get Swift.Bool metadata");
                container.ObjectMetadata = metadata.Value;
                container.Payload0 = b ? (IntPtr)1 : IntPtr.Zero;
                break;
            }
            case long l:
            {
                if (!TypeMetadata.Cache.TryGet(typeof(nint), out var metadata))
                    throw new SwiftRuntimeException("Cannot get Swift.Int metadata");
                container.ObjectMetadata = metadata.Value;
                container.Payload0 = (IntPtr)l;
                break;
            }
            case int i:
            {
                // A C# `int` is 32-bit — box it as Swift.Int32 (NOT Swift.Int, which is 64-bit).
                // Tagging it with the distinct Int32 metadata is what lets Unbox round-trip it
                // back to a C# `int` instead of silently widening to `long`. The
                // metadata cache maps typeof(int) -> `$ss5Int32VN` (Swift.Int32).
                if (!TypeMetadata.Cache.TryGet(typeof(int), out var metadata))
                    throw new SwiftRuntimeException("Cannot get Swift.Int32 metadata");
                container.ObjectMetadata = metadata.Value;
                container.Payload0 = (IntPtr)i;
                break;
            }
            case double d:
            {
                if (!TypeMetadata.Cache.TryGet(typeof(double), out var metadata))
                    throw new SwiftRuntimeException("Cannot get Swift.Double metadata");
                container.ObjectMetadata = metadata.Value;
                long bits = BitConverter.DoubleToInt64Bits(d);
                container.Payload0 = (IntPtr)bits;
                break;
            }
            case string s:
            {
                using var swiftStr = new SwiftString(s);
                if (!TypeMetadata.Cache.TryGet(typeof(SwiftString), out var metadata))
                    throw new SwiftRuntimeException("Cannot get Swift.String metadata");
                container.ObjectMetadata = metadata.Value;
                // SwiftString is 16 bytes (2 words) on 64-bit — store inline in payload.
                // MarshalToSwift calls initializeWithCopy, which retains the string data.
                // The container takes ownership of that retain count; swiftStr's dispose
                // releases the original reference — net result: container owns 1 retain.
                Span<byte> buffer = stackalloc byte[ExistentialContainerFactory.MaxInlinePayloadSize];
                buffer.Clear();
                var span = buffer;
                ((ISwiftObject)swiftStr).MarshalToSwift(ref span);
                fixed (byte* bufferPtr = buffer)
                {
                    var srcPtr = (IntPtr*)bufferPtr;
                    container.Payload0 = srcPtr[0];
                    container.Payload1 = srcPtr[1];
                }
                break;
            }
            default:
                // Non-primitive payloads (any generated Swift wrapper) box through the value
                // witness table using the value's own Swift type metadata, instead of throwing.
                // This is the runtime sibling of ExistentialContainerFactory.CreateAny<T> for the
                // bare-`Any` path, where the per-element static type is only `object`.
                if (value is ISwiftObject swiftObject)
                    return ExistentialContainerFactory.CreateAnyRuntime(swiftObject);

                throw new NotSupportedException(
                    $"Cannot box value of type '{value.GetType().Name}' into ExistentialContainer0. " +
                    $"Supported types: bool, int, long, double, string, or any Swift object (ISwiftObject).");
        }

        return container;
    }

    /// <summary>
    /// Unboxes an ExistentialContainer0 back to a C# object.
    /// Uses the metadata pointer to determine the contained Swift type.
    /// </summary>
    /// <param name="container">The existential container to unbox.</param>
    /// <returns>The contained value as a C# object.</returns>
    /// <exception cref="NotSupportedException">Thrown if the contained type is not recognized.</exception>
    public static unsafe object Unbox(ExistentialContainer0 container)
    {
        var metadata = container.ObjectMetadata;

        // Compare against known type metadata to determine the contained type
        if (TypeMetadata.Cache.TryGet(typeof(bool), out var boolMeta) && metadata.Equals(boolMeta.Value))
        {
            return container.Payload0 != IntPtr.Zero;
        }

        if (TypeMetadata.Cache.TryGet(typeof(nint), out var intMeta) && metadata.Equals(intMeta.Value))
        {
            return (long)container.Payload0;
        }

        // Swift.Int32 round-trips back to a C# `int`: a value boxed from a C# `int`
        // carries Int32 metadata, so recover it as `int` rather than widening to `long`. Checked
        // after the Swift.Int (nint) branch — the two metadata pointers are distinct.
        if (TypeMetadata.Cache.TryGet(typeof(int), out var int32Meta) && metadata.Equals(int32Meta.Value))
        {
            return (int)(long)container.Payload0;
        }

        if (TypeMetadata.Cache.TryGet(typeof(double), out var doubleMeta) && metadata.Equals(doubleMeta.Value))
        {
            return BitConverter.Int64BitsToDouble((long)container.Payload0);
        }

        if (TypeMetadata.Cache.TryGet(typeof(SwiftString), out var stringMeta) && metadata.Equals(stringMeta.Value))
        {
            // Reconstruct SwiftString from the container's payload words.
            // initializeWithCopy creates a retained copy (+1 ARC). The SwiftString
            // constructor bitwise-copies from the buffer, adopting the retain. When
            // SwiftString.Dispose calls VWT Destroy, it decrements (-1 ARC), netting
            // zero change to the container's original reference. Stack buffer memory
            // is reclaimed on return — no leak, no double-free.
            byte* buffer = stackalloc byte[ExistentialContainerFactory.MaxInlinePayloadSize];
            new Span<byte>(buffer, ExistentialContainerFactory.MaxInlinePayloadSize).Clear();
            metadata.ValueWitnessTable->InitializeWithCopy(buffer, &container, metadata);

            using var swiftStr = SwiftString.FromPayload((IntPtr)buffer);
            return swiftStr.ToString();
        }

        throw new NotSupportedException(
            $"Cannot unbox ExistentialContainer0 with metadata handle 0x{metadata.Handle:X}. " +
            $"Supported types: Bool, Int, Double, String.");
    }
}

/// <summary>
/// Internal marshalling type for Swift existential containers with 1 witness table.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. Use the protocol interface types (e.g., IMyProtocol) in your code instead.
/// Size: 5 machine words (40 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ExistentialContainer1 : IExistentialContainer
{
    private IntPtr _payload0;
    private IntPtr _payload1;
    private IntPtr _payload2;
    private TypeMetadata _metadata;
    private IntPtr _witnessTable0;

    public IntPtr Payload0 { get => _payload0; set => _payload0 = value; }
    public IntPtr Payload1 { get => _payload1; set => _payload1 = value; }
    public IntPtr Payload2 { get => _payload2; set => _payload2 = value; }
    public TypeMetadata ObjectMetadata { get => _metadata; set => _metadata = value; }

    public IntPtr this[int index]
    {
        get => index == 0 ? _witnessTable0 : throw new IndexOutOfRangeException();
        set { if (index == 0) _witnessTable0 = value; else throw new IndexOutOfRangeException(); }
    }

    public int Count => 1;
    public int SizeOf => 5 * IntPtr.Size;

    public unsafe IntPtr CopyTo(IntPtr memory)
    {
        *(ExistentialContainer1*)memory = this;
        return memory;
    }

    public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
    {
        if (container.SizeOf != SizeOf)
            throw new ArgumentException($"Container size mismatch: expected {SizeOf}, got {container.SizeOf}");
        container.Payload0 = Payload0;
        container.Payload1 = Payload1;
        container.Payload2 = Payload2;
        container.ObjectMetadata = ObjectMetadata;
        container[0] = _witnessTable0;
    }
}

/// <summary>
/// Internal marshalling carrier for a <em>class-bound</em> (superclass- or AnyObject-constrained)
/// single-protocol Swift existential — <c>any P</c> where <c>P</c> is class-bound. Unlike the
/// opaque 5-word <see cref="ExistentialContainer1"/>, a class-bound existential is a compact
/// 2-word value: <c>[classRef][witnessTable]</c> (16-byte stride). Using this carrier as the
/// <c>SwiftArray&lt;Element&gt;</c> element type makes the array read at the correct 16-byte
/// stride instead of over-reading each element at the opaque 40-byte stride (which SIGSEGVs).
///
/// The carrier converts implicitly to <see cref="ExistentialContainer1"/> (mapping
/// classRef → Payload0, witnessTable → Payload1) so the generated protocol proxy's existing
/// <c>(ExistentialContainer1 container, …)</c> constructor — whose class-bound layout path reads
/// the instance from Payload0 and the witness table from Payload1 — consumes it unchanged.
///
/// Metadata for this carrier is registered at module-init via
/// <see cref="TypeMetadata.RegisterClassBoundExistentialMetadata"/> from the real protocol
/// descriptor. The class-existential value-witness table (retain word0, copy the witness word
/// opaquely) and 16-byte stride are protocol-agnostic for a given arity, so any class-bound
/// arity-1 descriptor yields copy-correct metadata; the real witness table travels in the
/// element data and is preserved by the protocol-agnostic copy.
/// Size: 2 machine words (16 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ClassExistentialContainer1
{
    private IntPtr _classRef;
    private IntPtr _witnessTable0;

    public IntPtr ClassRef { get => _classRef; set => _classRef = value; }
    public IntPtr WitnessTable0 { get => _witnessTable0; set => _witnessTable0 = value; }

    /// <summary>Number of machine words in the class-bound existential layout.</summary>
    public const int WordCount = 2;

    /// <summary>
    /// Widens the compact 2-word class-bound existential into the 5-word
    /// <see cref="ExistentialContainer1"/> consumed by the generated proxy constructor. The
    /// class-bound proxy layout reads the instance from Payload0 and the witness table from
    /// Payload1; the remaining opaque-layout words (Payload2, metadata) stay zero.
    /// </summary>
    public static implicit operator ExistentialContainer1(ClassExistentialContainer1 c)
        => new ExistentialContainer1 { Payload0 = c._classRef, Payload1 = c._witnessTable0 };

    /// <summary>
    /// Reads a class-bound existential heap cell (the 2-word <c>[classRef][witnessTable]</c>,
    /// 16 bytes) and widens it to the 5-word <see cref="ExistentialContainer1"/> the proxy and
    /// marshalling layers store. Use at heap-cell READ sites where Swift allocated exactly
    /// <c>MemoryLayout&lt;any P&gt;.size</c> (16 bytes for class-bound) — reading the opaque
    /// <see cref="ExistentialContainer1"/> there (40 bytes) over-reads 24 bytes past the
    /// allocation. This is read-only: ownership (any retain on the class ref, the cell free)
    /// stays at the call site, exactly as it does for the opaque container read it replaces.
    /// </summary>
    /// <param name="cell">Pointer to the 2-word class-bound existential cell.</param>
    public static unsafe ExistentialContainer1 ReadHeapCell(IntPtr cell)
        => Unsafe.Read<ClassExistentialContainer1>((void*)cell);

    /// <summary>
    /// Narrows a class-bound <see cref="ExistentialContainer1"/> down to the compact 2-word carrier
    /// Swift expects for a class-bound <c>any P</c> array/collection element. The inverse of the
    /// widening <c>implicit operator</c> above: that maps <c>ClassRef → Payload0</c> and
    /// <c>WitnessTable0 → Payload1</c>, so this reads those same two words back.
    ///
    /// <para>
    /// Only valid for an EC1 holding a class-bound conformer (a single class instance), so callers
    /// MUST gate on the same class-bound check (<c>ExistentialProjection.IsClassBoundArity1</c>) the
    /// read-side carrier uses — narrowing an opaque or composition EC1 would copy inline payload
    /// bytes / metadata into the class-ref + witness words and hand Swift a garbage object.
    /// </para>
    /// <para>
    /// The class instance is always <see cref="ExistentialContainer1.Payload0"/>. The witness table,
    /// however, lands in a different word depending on which producer built the EC1 — and both
    /// producers are reachable through
    /// <see cref="ExistentialContainerFactory.GetOrCreate{TProtocol}(TProtocol, System.Func{TProtocol, ISwiftExistentialConvertible{ExistentialContainer1}})"/>:
    /// <list type="bullet">
    /// <item>the generated <b>proxy</b> path (a Swift-backed return wrapped in <c>{P}Proxy</c>, or a
    ///   C# implementation wrapped in an <c>EveryProtocol</c>-backed proxy) builds the 2-word
    ///   class-bound layout directly: witness in <see cref="ExistentialContainer1.Payload1"/>,
    ///   leaving the dedicated witness word zero;</item>
    /// <item>the <b>boxable</b> path (<c>IExistentialBoxable.BoxAsExistential1</c> →
    ///   <c>ExistentialContainerFactory.Create&lt;T,TProtocol&gt;</c>, taken for a concrete Swift class
    ///   conformer passed by value — e.g. <c>new {Conformer}(...)</c>) builds the opaque layout:
    ///   class ref in <see cref="ExistentialContainer1.Payload0"/> via <c>MarshalPayload</c> with
    ///   Payload1 left zero, and the witness in the dedicated witness word (<c>container[0]</c>).</item>
    /// </list>
    /// For a class-bound conformer the payload is a single class word, so exactly one of
    /// {Payload1, witness word} is the witness and the other is zero — pick the non-zero one. A valid
    /// witness-table pointer is never null, so this is unambiguous.
    /// </para>
    /// <para>
    /// Pure word-copy: this narrowing does NOT change the ownership of the class reference — the
    /// caller decides the +1. The two source layouts differ in who already owns it: the borrowed
    /// convertible/proxy layout keeps its +1 (released on Dispose/finalize via
    /// <c>ProxyLifetimeTracker</c>), whereas the boxable layout's <c>Create</c> has already minted a
    /// fresh +1 on the class ref via an inline <c>InitializeWithCopy</c>. Whoever builds a
    /// <c>SwiftArray&lt;ClassExistentialContainer1&gt;</c> element must therefore go through
    /// <see cref="ExistentialContainerFactory.CreateOwnedClassCarrier{TProtocol}(TProtocol, Func{TProtocol, ISwiftExistentialConvertible{ExistentialContainer1}})"/>, which hands the array
    /// exactly one owned +1 — minting for the borrowed layout, donating the boxable layout's existing
    /// +1 — because Swift's array element write is <c>__owned</c> (consuming) and the
    /// class-existential value-witness table releases word0 once on destroy. Using this bare
    /// narrowing for an array element (as the original carrier path did) over-releases the
    /// borrowed proxy and leaks the boxable +1.
    /// </para>
    /// </summary>
    /// <param name="c">A class-bound <see cref="ExistentialContainer1"/> (Payload0 = class instance).</param>
    public static ClassExistentialContainer1 FromExistentialContainer1(ExistentialContainer1 c)
        => new ClassExistentialContainer1
        {
            ClassRef = c.Payload0,
            WitnessTable0 = c.Payload1 != IntPtr.Zero ? c.Payload1 : c[0],
        };
}

/// <summary>
/// Internal marshalling type for Swift existential containers with 2 witness tables.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. Use the protocol interface types (e.g., IMyProtocol) in your code instead.
/// Size: 6 machine words (48 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ExistentialContainer2 : IExistentialContainer
{
    private IntPtr _payload0;
    private IntPtr _payload1;
    private IntPtr _payload2;
    private TypeMetadata _metadata;
    private IntPtr _witnessTable0;
    private IntPtr _witnessTable1;

    public IntPtr Payload0 { get => _payload0; set => _payload0 = value; }
    public IntPtr Payload1 { get => _payload1; set => _payload1 = value; }
    public IntPtr Payload2 { get => _payload2; set => _payload2 = value; }
    public TypeMetadata ObjectMetadata { get => _metadata; set => _metadata = value; }

    public IntPtr this[int index]
    {
        get => index switch
        {
            0 => _witnessTable0,
            1 => _witnessTable1,
            _ => throw new IndexOutOfRangeException()
        };
        set
        {
            switch (index)
            {
                case 0: _witnessTable0 = value; break;
                case 1: _witnessTable1 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    public int Count => 2;
    public int SizeOf => 6 * IntPtr.Size;

    public unsafe IntPtr CopyTo(IntPtr memory)
    {
        *(ExistentialContainer2*)memory = this;
        return memory;
    }

    public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
    {
        if (container.SizeOf != SizeOf)
            throw new ArgumentException($"Container size mismatch: expected {SizeOf}, got {container.SizeOf}");
        container.Payload0 = Payload0;
        container.Payload1 = Payload1;
        container.Payload2 = Payload2;
        container.ObjectMetadata = ObjectMetadata;
        container[0] = _witnessTable0;
        container[1] = _witnessTable1;
    }
}

/// <summary>
/// Internal marshalling type for Swift existential containers with 3 witness tables.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. Use the protocol interface types (e.g., IMyProtocol) in your code instead.
/// Size: 7 machine words (56 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ExistentialContainer3 : IExistentialContainer
{
    private IntPtr _payload0;
    private IntPtr _payload1;
    private IntPtr _payload2;
    private TypeMetadata _metadata;
    private IntPtr _witnessTable0;
    private IntPtr _witnessTable1;
    private IntPtr _witnessTable2;

    public IntPtr Payload0 { get => _payload0; set => _payload0 = value; }
    public IntPtr Payload1 { get => _payload1; set => _payload1 = value; }
    public IntPtr Payload2 { get => _payload2; set => _payload2 = value; }
    public TypeMetadata ObjectMetadata { get => _metadata; set => _metadata = value; }

    public IntPtr this[int index]
    {
        get => index switch
        {
            0 => _witnessTable0,
            1 => _witnessTable1,
            2 => _witnessTable2,
            _ => throw new IndexOutOfRangeException()
        };
        set
        {
            switch (index)
            {
                case 0: _witnessTable0 = value; break;
                case 1: _witnessTable1 = value; break;
                case 2: _witnessTable2 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    public int Count => 3;
    public int SizeOf => 7 * IntPtr.Size;

    public unsafe IntPtr CopyTo(IntPtr memory)
    {
        *(ExistentialContainer3*)memory = this;
        return memory;
    }

    public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
    {
        if (container.SizeOf != SizeOf)
            throw new ArgumentException($"Container size mismatch: expected {SizeOf}, got {container.SizeOf}");
        container.Payload0 = Payload0;
        container.Payload1 = Payload1;
        container.Payload2 = Payload2;
        container.ObjectMetadata = ObjectMetadata;
        for (int i = 0; i < Count; i++)
            container[i] = this[i];
    }
}

/// <summary>
/// Internal marshalling type for Swift existential containers with 4 witness tables.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. Use the protocol interface types (e.g., IMyProtocol) in your code instead.
/// Size: 8 machine words (64 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ExistentialContainer4 : IExistentialContainer
{
    private IntPtr _payload0;
    private IntPtr _payload1;
    private IntPtr _payload2;
    private TypeMetadata _metadata;
    private IntPtr _witnessTable0;
    private IntPtr _witnessTable1;
    private IntPtr _witnessTable2;
    private IntPtr _witnessTable3;

    public IntPtr Payload0 { get => _payload0; set => _payload0 = value; }
    public IntPtr Payload1 { get => _payload1; set => _payload1 = value; }
    public IntPtr Payload2 { get => _payload2; set => _payload2 = value; }
    public TypeMetadata ObjectMetadata { get => _metadata; set => _metadata = value; }

    public IntPtr this[int index]
    {
        get => index switch
        {
            0 => _witnessTable0,
            1 => _witnessTable1,
            2 => _witnessTable2,
            3 => _witnessTable3,
            _ => throw new IndexOutOfRangeException()
        };
        set
        {
            switch (index)
            {
                case 0: _witnessTable0 = value; break;
                case 1: _witnessTable1 = value; break;
                case 2: _witnessTable2 = value; break;
                case 3: _witnessTable3 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    public int Count => 4;
    public int SizeOf => 8 * IntPtr.Size;

    public unsafe IntPtr CopyTo(IntPtr memory)
    {
        *(ExistentialContainer4*)memory = this;
        return memory;
    }

    public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
    {
        if (container.SizeOf != SizeOf)
            throw new ArgumentException($"Container size mismatch: expected {SizeOf}, got {container.SizeOf}");
        container.Payload0 = Payload0;
        container.Payload1 = Payload1;
        container.Payload2 = Payload2;
        container.ObjectMetadata = ObjectMetadata;
        for (int i = 0; i < Count; i++)
            container[i] = this[i];
    }
}

/// <summary>
/// Internal marshalling type for Swift existential containers with 5 witness tables.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. Use the protocol interface types (e.g., IMyProtocol) in your code instead.
/// Size: 9 machine words (72 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ExistentialContainer5 : IExistentialContainer
{
    private IntPtr _payload0;
    private IntPtr _payload1;
    private IntPtr _payload2;
    private TypeMetadata _metadata;
    private IntPtr _witnessTable0;
    private IntPtr _witnessTable1;
    private IntPtr _witnessTable2;
    private IntPtr _witnessTable3;
    private IntPtr _witnessTable4;

    public IntPtr Payload0 { get => _payload0; set => _payload0 = value; }
    public IntPtr Payload1 { get => _payload1; set => _payload1 = value; }
    public IntPtr Payload2 { get => _payload2; set => _payload2 = value; }
    public TypeMetadata ObjectMetadata { get => _metadata; set => _metadata = value; }

    public IntPtr this[int index]
    {
        get => index switch
        {
            0 => _witnessTable0,
            1 => _witnessTable1,
            2 => _witnessTable2,
            3 => _witnessTable3,
            4 => _witnessTable4,
            _ => throw new IndexOutOfRangeException()
        };
        set
        {
            switch (index)
            {
                case 0: _witnessTable0 = value; break;
                case 1: _witnessTable1 = value; break;
                case 2: _witnessTable2 = value; break;
                case 3: _witnessTable3 = value; break;
                case 4: _witnessTable4 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    public int Count => 5;
    public int SizeOf => 9 * IntPtr.Size;

    public unsafe IntPtr CopyTo(IntPtr memory)
    {
        *(ExistentialContainer5*)memory = this;
        return memory;
    }

    public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
    {
        if (container.SizeOf != SizeOf)
            throw new ArgumentException($"Container size mismatch: expected {SizeOf}, got {container.SizeOf}");
        container.Payload0 = Payload0;
        container.Payload1 = Payload1;
        container.Payload2 = Payload2;
        container.ObjectMetadata = ObjectMetadata;
        for (int i = 0; i < Count; i++)
            container[i] = this[i];
    }
}

/// <summary>
/// Internal marshalling type for Swift existential containers with 6 witness tables.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. Use the protocol interface types (e.g., IMyProtocol) in your code instead.
/// Size: 10 machine words (80 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ExistentialContainer6 : IExistentialContainer
{
    private IntPtr _payload0;
    private IntPtr _payload1;
    private IntPtr _payload2;
    private TypeMetadata _metadata;
    private IntPtr _witnessTable0;
    private IntPtr _witnessTable1;
    private IntPtr _witnessTable2;
    private IntPtr _witnessTable3;
    private IntPtr _witnessTable4;
    private IntPtr _witnessTable5;

    public IntPtr Payload0 { get => _payload0; set => _payload0 = value; }
    public IntPtr Payload1 { get => _payload1; set => _payload1 = value; }
    public IntPtr Payload2 { get => _payload2; set => _payload2 = value; }
    public TypeMetadata ObjectMetadata { get => _metadata; set => _metadata = value; }

    public IntPtr this[int index]
    {
        get => index switch
        {
            0 => _witnessTable0,
            1 => _witnessTable1,
            2 => _witnessTable2,
            3 => _witnessTable3,
            4 => _witnessTable4,
            5 => _witnessTable5,
            _ => throw new IndexOutOfRangeException()
        };
        set
        {
            switch (index)
            {
                case 0: _witnessTable0 = value; break;
                case 1: _witnessTable1 = value; break;
                case 2: _witnessTable2 = value; break;
                case 3: _witnessTable3 = value; break;
                case 4: _witnessTable4 = value; break;
                case 5: _witnessTable5 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    public int Count => 6;
    public int SizeOf => 10 * IntPtr.Size;

    public unsafe IntPtr CopyTo(IntPtr memory)
    {
        *(ExistentialContainer6*)memory = this;
        return memory;
    }

    public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
    {
        if (container.SizeOf != SizeOf)
            throw new ArgumentException($"Container size mismatch: expected {SizeOf}, got {container.SizeOf}");
        container.Payload0 = Payload0;
        container.Payload1 = Payload1;
        container.Payload2 = Payload2;
        container.ObjectMetadata = ObjectMetadata;
        for (int i = 0; i < Count; i++)
            container[i] = this[i];
    }
}

/// <summary>
/// Internal marshalling type for Swift existential containers with 7 witness tables.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. Use the protocol interface types (e.g., IMyProtocol) in your code instead.
/// Size: 11 machine words (88 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ExistentialContainer7 : IExistentialContainer
{
    private IntPtr _payload0;
    private IntPtr _payload1;
    private IntPtr _payload2;
    private TypeMetadata _metadata;
    private IntPtr _witnessTable0;
    private IntPtr _witnessTable1;
    private IntPtr _witnessTable2;
    private IntPtr _witnessTable3;
    private IntPtr _witnessTable4;
    private IntPtr _witnessTable5;
    private IntPtr _witnessTable6;

    public IntPtr Payload0 { get => _payload0; set => _payload0 = value; }
    public IntPtr Payload1 { get => _payload1; set => _payload1 = value; }
    public IntPtr Payload2 { get => _payload2; set => _payload2 = value; }
    public TypeMetadata ObjectMetadata { get => _metadata; set => _metadata = value; }

    public IntPtr this[int index]
    {
        get => index switch
        {
            0 => _witnessTable0,
            1 => _witnessTable1,
            2 => _witnessTable2,
            3 => _witnessTable3,
            4 => _witnessTable4,
            5 => _witnessTable5,
            6 => _witnessTable6,
            _ => throw new IndexOutOfRangeException()
        };
        set
        {
            switch (index)
            {
                case 0: _witnessTable0 = value; break;
                case 1: _witnessTable1 = value; break;
                case 2: _witnessTable2 = value; break;
                case 3: _witnessTable3 = value; break;
                case 4: _witnessTable4 = value; break;
                case 5: _witnessTable5 = value; break;
                case 6: _witnessTable6 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    public int Count => 7;
    public int SizeOf => 11 * IntPtr.Size;

    public unsafe IntPtr CopyTo(IntPtr memory)
    {
        *(ExistentialContainer7*)memory = this;
        return memory;
    }

    public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
    {
        if (container.SizeOf != SizeOf)
            throw new ArgumentException($"Container size mismatch: expected {SizeOf}, got {container.SizeOf}");
        container.Payload0 = Payload0;
        container.Payload1 = Payload1;
        container.Payload2 = Payload2;
        container.ObjectMetadata = ObjectMetadata;
        for (int i = 0; i < Count; i++)
            container[i] = this[i];
    }
}

/// <summary>
/// Internal marshalling type for Swift existential containers with 8 witness tables.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. Use the protocol interface types (e.g., IMyProtocol) in your code instead.
/// Size: 12 machine words (96 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ExistentialContainer8 : IExistentialContainer
{
    private IntPtr _payload0;
    private IntPtr _payload1;
    private IntPtr _payload2;
    private TypeMetadata _metadata;
    private IntPtr _witnessTable0;
    private IntPtr _witnessTable1;
    private IntPtr _witnessTable2;
    private IntPtr _witnessTable3;
    private IntPtr _witnessTable4;
    private IntPtr _witnessTable5;
    private IntPtr _witnessTable6;
    private IntPtr _witnessTable7;

    public IntPtr Payload0 { get => _payload0; set => _payload0 = value; }
    public IntPtr Payload1 { get => _payload1; set => _payload1 = value; }
    public IntPtr Payload2 { get => _payload2; set => _payload2 = value; }
    public TypeMetadata ObjectMetadata { get => _metadata; set => _metadata = value; }

    public IntPtr this[int index]
    {
        get => index switch
        {
            0 => _witnessTable0,
            1 => _witnessTable1,
            2 => _witnessTable2,
            3 => _witnessTable3,
            4 => _witnessTable4,
            5 => _witnessTable5,
            6 => _witnessTable6,
            7 => _witnessTable7,
            _ => throw new IndexOutOfRangeException()
        };
        set
        {
            switch (index)
            {
                case 0: _witnessTable0 = value; break;
                case 1: _witnessTable1 = value; break;
                case 2: _witnessTable2 = value; break;
                case 3: _witnessTable3 = value; break;
                case 4: _witnessTable4 = value; break;
                case 5: _witnessTable5 = value; break;
                case 6: _witnessTable6 = value; break;
                case 7: _witnessTable7 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    public int Count => 8;
    public int SizeOf => 12 * IntPtr.Size;

    public unsafe IntPtr CopyTo(IntPtr memory)
    {
        *(ExistentialContainer8*)memory = this;
        return memory;
    }

    public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
    {
        if (container.SizeOf != SizeOf)
            throw new ArgumentException($"Container size mismatch: expected {SizeOf}, got {container.SizeOf}");
        container.Payload0 = Payload0;
        container.Payload1 = Payload1;
        container.Payload2 = Payload2;
        container.ObjectMetadata = ObjectMetadata;
        for (int i = 0; i < Count; i++)
            container[i] = this[i];
    }
}

/// <summary>
/// Factory for creating existential containers from C# objects.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. The generated binding code handles existential container creation automatically.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ExistentialContainerFactory
{
    /// <summary>
    /// Maximum size in bytes that can be stored inline in an existential container's payload.
    /// On 64-bit systems, this is 24 bytes (3 machine words).
    /// Values larger than this are heap-allocated.
    /// </summary>
    public const int MaxInlinePayloadSize = 3 * 8; // 3 machine words on 64-bit

    /// <summary>
    /// Return type for swift_allocBox: a heap object pointer and a buffer pointer.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BoxPair
    {
        public IntPtr HeapObject;
        public IntPtr Buffer;
    }

    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "swift_allocBox")]
    private static extern BoxPair swift_allocBox(TypeMetadata type);

    /// <summary>
    /// Creates an existential container for 'any' type (no protocol constraints).
    /// Use this when a Swift method expects 'Any' or 'any Any'.
    /// </summary>
    /// <typeparam name="T">The Swift object type to box.</typeparam>
    /// <param name="value">The value to place in the existential container.</param>
    /// <returns>An ExistentialContainer0 containing the value.</returns>
    public static ExistentialContainer0 CreateAny<T>(T value)
        where T : ISwiftObject
    {
        var container = new ExistentialContainer0();
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
        container.ObjectMetadata = metadata;
        MarshalPayload(value, metadata, ref container);
        return container;
    }

    /// <summary>
    /// The runtime (non-generic) counterpart of <see cref="CreateAny{T}"/>, used when only the
    /// erased <see cref="ISwiftObject"/> instance is available — e.g. boxing the per-element values
    /// of a bare-<c>Any</c> dictionary/collection where the static element type is <c>object</c>.
    /// Resolves the value's Swift type metadata from its concrete runtime type and marshals the
    /// payload through the same inline/heap-box logic as <see cref="MarshalPayload"/>.
    /// </summary>
    /// <param name="value">The Swift object to box into an existential container.</param>
    /// <returns>An <see cref="ExistentialContainer0"/> holding <paramref name="value"/>.</returns>
    /// <exception cref="SwiftRuntimeException">Thrown if the value's Swift metadata cannot be resolved.</exception>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "value.GetType() is an ISwiftObject implementation whose GetTypeMetadata/NewFromPayload members are preserved for consumers by the shipped ILLink.Descriptors.xml — the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics, or Swift.Runtime's own embedded+rooted descriptor for Runtime-owned ISwiftObject types (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    public static ExistentialContainer0 CreateAnyRuntime(ISwiftObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var container = new ExistentialContainer0();
        var metadata = SwiftObjectReflectionHelper.InvokeGetTypeMetadata(value.GetType());
        if (!metadata.IsValid)
            throw new SwiftRuntimeException(
                $"Cannot resolve Swift type metadata for '{value.GetType().Name}' when boxing into Any.");
        container.ObjectMetadata = metadata;
        MarshalPayload(value, metadata, ref container);
        return container;
    }

    /// <summary>
    /// Creates an existential container with 1 protocol witness table.
    /// Use this when a Swift method expects 'any Protocol'.
    /// </summary>
    /// <typeparam name="T">The Swift object type that conforms to the protocol.</typeparam>
    /// <typeparam name="TProtocol">The protocol interface type.</typeparam>
    /// <param name="value">The value to place in the existential container.</param>
    /// <returns>An ExistentialContainer1 containing the value and its witness table.</returns>
    /// <exception cref="SwiftRuntimeException">Thrown if the type does not conform to the protocol.</exception>
    public static ExistentialContainer1 Create<T, TProtocol>(T value)
        where T : ISwiftObject
        where TProtocol : class
    {
        var container = new ExistentialContainer1();
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
        container.ObjectMetadata = metadata;
        var witnessTable = ProtocolWitnessTable.GetOrThrowAuto<T, TProtocol>();
        container[0] = witnessTable.Handle;
        MarshalPayload(value, metadata, ref container);
        return container;
    }

    /// <summary>
    /// Creates an existential container with 2 protocol witness tables.
    /// Use this when a Swift method expects 'any Protocol1 &amp; Protocol2'.
    /// </summary>
    /// <typeparam name="T">The Swift object type that conforms to both protocols.</typeparam>
    /// <typeparam name="TProtocol1">The first protocol interface type.</typeparam>
    /// <typeparam name="TProtocol2">The second protocol interface type.</typeparam>
    /// <param name="value">The value to place in the existential container.</param>
    /// <returns>An ExistentialContainer2 containing the value and its witness tables.</returns>
    /// <exception cref="SwiftRuntimeException">Thrown if the type does not conform to all protocols.</exception>
    public static ExistentialContainer2 Create<T, TProtocol1, TProtocol2>(T value)
        where T : ISwiftObject
        where TProtocol1 : class
        where TProtocol2 : class
    {
        var container = new ExistentialContainer2();
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
        container.ObjectMetadata = metadata;
        container[0] = ProtocolWitnessTable.GetOrThrowAuto<T, TProtocol1>().Handle;
        container[1] = ProtocolWitnessTable.GetOrThrowAuto<T, TProtocol2>().Handle;
        MarshalPayload(value, metadata, ref container);
        return container;
    }

    /// <summary>
    /// Creates an existential container with 3 protocol witness tables.
    /// </summary>
    public static ExistentialContainer3 Create<T, TProtocol1, TProtocol2, TProtocol3>(T value)
        where T : ISwiftObject
        where TProtocol1 : class
        where TProtocol2 : class
        where TProtocol3 : class
    {
        var container = new ExistentialContainer3();
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
        container.ObjectMetadata = metadata;
        container[0] = ProtocolWitnessTable.GetOrThrowAuto<T, TProtocol1>().Handle;
        container[1] = ProtocolWitnessTable.GetOrThrowAuto<T, TProtocol2>().Handle;
        container[2] = ProtocolWitnessTable.GetOrThrowAuto<T, TProtocol3>().Handle;
        MarshalPayload(value, metadata, ref container);
        return container;
    }

    /// <summary>
    /// Creates an existential container with 4 protocol witness tables.
    /// </summary>
    public static ExistentialContainer4 Create<T, TProtocol1, TProtocol2, TProtocol3, TProtocol4>(T value)
        where T : ISwiftObject
        where TProtocol1 : class
        where TProtocol2 : class
        where TProtocol3 : class
        where TProtocol4 : class
    {
        var container = new ExistentialContainer4();
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
        container.ObjectMetadata = metadata;
        container[0] = ProtocolWitnessTable.GetOrThrowAuto<T, TProtocol1>().Handle;
        container[1] = ProtocolWitnessTable.GetOrThrowAuto<T, TProtocol2>().Handle;
        container[2] = ProtocolWitnessTable.GetOrThrowAuto<T, TProtocol3>().Handle;
        container[3] = ProtocolWitnessTable.GetOrThrowAuto<T, TProtocol4>().Handle;
        MarshalPayload(value, metadata, ref container);
        return container;
    }

    /// <summary>
    /// Marshals the value's payload into the existential container.
    /// Small values (≤ MaxInlinePayloadSize and not non-inline) are stored directly in the payload slots.
    /// Larger values are heap-allocated and a pointer is stored in Payload0.
    /// </summary>
    private static unsafe void MarshalPayload<TContainer>(ISwiftObject value, TypeMetadata metadata, ref TContainer container)
        where TContainer : struct, IExistentialContainer
    {
        var vwt = metadata.ValueWitnessTable;
        var size = (int)vwt->Size;
        var isNonInline = (vwt->Flags & ValueWitnessFlags.IsNonInline) != 0;

        if (size <= MaxInlinePayloadSize && !isNonInline)
        {
            // Store inline in the payload slots
            // Use a temporary buffer to marshal the Swift value
            Span<byte> buffer = stackalloc byte[MaxInlinePayloadSize];
            buffer.Clear();
            var span = buffer;
            value.MarshalToSwift(ref span);

            // Copy from buffer to payload slots
            fixed (byte* bufferPtr = buffer)
            {
                var srcPtr = (IntPtr*)bufferPtr;
                container.Payload0 = srcPtr[0];
                if (size > IntPtr.Size)
                    container.Payload1 = srcPtr[1];
                if (size > 2 * IntPtr.Size)
                    container.Payload2 = srcPtr[2];
            }
        }
        else
        {
            // Non-inline values must be stored in a Swift box (heap object with refcount header).
            // Swift's existential initializeWithCopy calls swift_retain on payload[0], so a raw
            // NativeMemory pointer would SIGSEGV. Use swift_allocBox which returns a properly
            // formatted heap object that Swift's ARC can manage.
            var boxPair = swift_allocBox(metadata);

            // Marshal the value into the box's buffer area
            var heapSpan = new Span<byte>((void*)boxPair.Buffer, size);
            value.MarshalToSwift(ref heapSpan);

            // Store the heap object pointer (with refcount header) in first payload slot
            container.Payload0 = boxPair.HeapObject;
            container.Payload1 = IntPtr.Zero;
            container.Payload2 = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Gets or creates an ExistentialContainer1 for a protocol-typed value.
    /// Handles both proxy types (which already hold a container via ISwiftExistentialConvertible)
    /// and concrete types (which construct a container via IExistentialBoxable).
    /// </summary>
    /// <typeparam name="TProtocol">The protocol interface type (e.g., IBlockMode).</typeparam>
    /// <param name="value">The protocol-typed value to box.</param>
    /// <returns>An ExistentialContainer1 for the value.</returns>
    public static ExistentialContainer1 GetOrCreate<TProtocol>(TProtocol value)
        where TProtocol : class
        => GetOrCreate(value, out _);

    /// <summary>
    /// Ownership-aware overload of <see cref="GetOrCreate{TProtocol}(TProtocol)"/>.
    /// </summary>
    /// <remarks>
    /// The boxed-vs-borrowed distinction is a RUNTIME property of <paramref name="value"/>, not
    /// something the call site can decide statically (the emitted parameter type is the erased
    /// interface, and both a boxable value conformer and a proxy are assignable to it — audit
    /// This overload carries the decision out of the single branch point so cleanup uses
    /// the exact signal that made it, rather than re-testing <c>is</c> at each call site (which
    /// would drift if branch precedence ever changed).
    ///
    /// <para>
    /// <paramref name="ownsContainer"/> is <c>true</c> ONLY for the boxable branch, where
    /// <see cref="IExistentialBoxable.BoxAsExistential1{TProtocol}"/> → <see cref="MarshalPayload"/>
    /// creates a fresh +1 (an inline <c>InitializeWithCopy</c> or a <c>swift_allocBox</c>) that
    /// nothing else releases. The caller MUST run the existential value-witness destroy of the
    /// whole container after the (guaranteed) native call returns. For the convertible/proxy
    /// branch it is <c>false</c>: the proxy owns its +1 (released on Dispose/finalize via
    /// <see cref="ProxyLifetimeTracker"/>), so destroying the borrowed container would over-release.
    /// </para>
    /// </remarks>
    public static ExistentialContainer1 GetOrCreate<TProtocol>(TProtocol value, out bool ownsContainer)
        where TProtocol : class
        => GetOrCreate(value, out ownsContainer, out _);

    /// <summary>
    /// <c>keepAlive</c>-returning overload of <see cref="GetOrCreate{TProtocol}(TProtocol, out bool)"/>
    /// (design change 4 — see <c>src/docs/Design/reverse-dispatch-lifetime.md</c>).
    /// <paramref name="keepAlive"/> is the object whose liveness must span the native call: the proxy
    /// that owns the EveryProtocol construction +1 (R0). Under Design B2 the proxy is registered only
    /// weakly, so a borrowed proxy container (raw pointers, no managed ref) would let a GC during the
    /// Swift call finalize the proxy → release R0 → premature deinit / UAF mid-call. The marshalling
    /// site captures this and emits <c>GC.KeepAlive(keepAlive)</c> after the call returns. It is the
    /// proxy in both existential branches and <c>null</c> in the boxable branch (owned container, no
    /// proxy to root). KeepAlive is harmless in the already-a-proxy branch (the proxy is a live arg).
    /// </summary>
    public static ExistentialContainer1 GetOrCreate<TProtocol>(TProtocol value, out bool ownsContainer, out object? keepAlive)
        where TProtocol : class
    {
        // Round-trip: value is already a marshalled existential container — e.g. read back from a
        // degraded `object` PAT-existential getter, which hands out the raw ExistentialContainer1 (it is
        // neither a proxy nor a boxable conformer). Feed it straight back. The boxed container still owns
        // the payload's +1, so this is a borrowed container (ownsContainer = false) with the boxed value
        // as the keep-alive root across the native call — the same contract as the
        // ISwiftExistentialConvertible branch below. Matched as the exact ExistentialContainer1 (not the
        // IExistentialContainer interface) because this overload can only correctly return that arity.
        if (value is ExistentialContainer1 roundTripContainer)
        {
            ownsContainer = false;
            keepAlive = value;
            return roundTripContainer;
        }

        if (value is ISwiftExistentialConvertible<ExistentialContainer1> convertible)
        {
            ownsContainer = false;
            keepAlive = convertible;
            return convertible.GetExistentialContainer();
        }

        if (value is IExistentialBoxable boxable)
        {
            ownsContainer = true;
            keepAlive = null;
            return boxable.BoxAsExistential1<TProtocol>();
        }

        throw new InvalidCastException(
            $"Cannot create ExistentialContainer1 for {value?.GetType().Name ?? "null"}: " +
            $"type must implement ISwiftExistentialConvertible<ExistentialContainer1> or IExistentialBoxable.");
    }

    /// <summary>
    /// Cache of auto-wrapped proxies keyed by the user's implementation instance, with a
    /// per-impl map keyed by the protocol interface type. Prevents repeated assignments of
    /// the same impl from accumulating proxy instances in
    /// <c>SwiftObjectRegistry._strongRegistry</c>, while still constructing distinct proxies
    /// when the same C# instance implements multiple generated protocol interfaces (each
    /// proxy carries a different protocol witness table, so they cannot be coalesced).
    /// The outer table is keyed weakly so the entry becomes eligible for collection once
    /// the user releases the impl AND no Swift-side reference keeps the proxies alive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> requires <c>TKey</c> to be a reference
    /// type and uses reference identity, which is exactly the semantics we want for matching
    /// repeated assignments of the same managed instance. The inner
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> handles the case where one C# class
    /// implements multiple <c>I&lt;Protocol&gt;</c> interfaces and gets passed to Swift APIs
    /// expecting different existential types — each call site needs its own
    /// <c>{Protocol}Proxy</c> with the matching witness table.
    /// </para>
    /// <para>
    /// The inner value is a <see cref="WeakReference{T}"/> to the proxy. The
    /// authoritative "is this proxy alive?" root is the <see cref="SwiftObjectRegistry"/>
    /// strong registry, which is dropped automatically when
    /// <see cref="ProxyLifetimeTracker"/> releases the Swift +1 (impl GC) or when
    /// <c>EveryProtocol.deinit</c> fires from the Swift side. The cache is a pure
    /// memoization optimisation — a cache-hit with a live weak reference returns
    /// the previously built proxy; a stale hit (or miss) rebuilds via the
    /// <c>wrapFallback</c> supplied to <c>GetOrCreate</c>.
    /// </para>
    /// <para>
    /// Benign race: two threads concurrently reach "stale or miss" for the same
    /// <c>(impl, protocol)</c> pair and both build proxies. The losing proxy
    /// becomes a weak-cache orphan, but it is still tracked by
    /// <see cref="ProxyLifetimeTracker"/>, so its +1 is released when the impl is
    /// GC'd and its strong-registry root drops via the deinit callback. Wasted
    /// allocation, no correctness issue.
    /// </para>
    /// </remarks>
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<Type, WeakReference<ISwiftExistentialConvertible<ExistentialContainer1>>>>
        s_autoWrapCache = new();

    /// <summary>
    /// Gets or creates an ExistentialContainer1 for a protocol-typed value, auto-wrapping
    /// plain C# implementations of the protocol interface in a generator-emitted proxy class.
    /// </summary>
    /// <remarks>
    /// This overload is emitted by the bindings generator at call sites that pass user-supplied
    /// interface values into Swift (property setters, method parameters, constructor args).
    /// It lets users implement a generated C# interface directly and pass the instance without
    /// manually constructing the hidden <c>{Protocol}Proxy</c> class.
    ///
    /// Resolution order:
    /// 1. Already a proxy (<see cref="ISwiftExistentialConvertible{T}"/>) — use as-is.
    /// 2. A boxable value type (<see cref="IExistentialBoxable"/>) — box via witness.
    /// 3. Fall back to the generator-supplied <paramref name="wrapFallback"/>, which constructs
    ///    a proxy wrapping the user's implementation. Auto-wrapped proxies are cached per
    ///    <c>(impl, TProtocol)</c> pair so repeated assignments of the same managed instance
    ///    reuse a single proxy per protocol instead of allocating a new one each time.
    ///
    /// <para>
    /// <b>Lifetime:</b> Auto-wrapped proxies are anchored to the user's <c>impl</c>
    /// lifetime via <see cref="ProxyLifetimeTracker"/>. When the impl is
    /// garbage-collected, the tracker releases the Swift +1 on <c>EveryProtocol</c>,
    /// Swift's <c>deinit</c> fires, and the proxy's strong-registry root drops via
    /// the deinit callback. There is no process-lifetime leak.
    /// </para>
    /// <para>
    /// <b>SwiftDisposeScope is intentionally bypassed for cached proxies:</b> the auto-wrap
    /// factory immediately detaches each new proxy from the active scope (if any) so that
    /// scope disposal cannot mark a still-cached proxy as disposed and trip
    /// <see cref="ObjectDisposedException"/> on the next reuse. To control auto-wrap proxy
    /// lifetime explicitly, construct the hidden <c>{Protocol}Proxy</c> manually and dispose
    /// it yourself — the manual path goes through branch (1) and never enters the cache.
    /// </para>
    /// </remarks>
    /// <typeparam name="TProtocol">The protocol interface type (e.g., IPerformanceMonitorDelegate).</typeparam>
    /// <param name="value">The protocol-typed value to box.</param>
    /// <param name="wrapFallback">
    /// Factory that wraps a plain C# implementation in the protocol's proxy class. Invoked only
    /// when <paramref name="value"/> is neither already-convertible nor boxable, and not already
    /// cached for this <c>(impl, TProtocol)</c> pair. Typically a <c>static</c> lambda like
    /// <c>static v =&gt; new FooProxy(v)</c>.
    /// </param>
    /// <returns>An ExistentialContainer1 for the value.</returns>
    public static ExistentialContainer1 GetOrCreate<TProtocol>(
        TProtocol value,
        Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback)
        where TProtocol : class
        => GetOrCreate(value, wrapFallback, out _);

    /// <summary>
    /// Ownership-aware overload of
    /// <see cref="GetOrCreate{TProtocol}(TProtocol, Func{TProtocol, ISwiftExistentialConvertible{ExistentialContainer1}})"/>.
    /// See <see cref="GetOrCreate{TProtocol}(TProtocol, out bool)"/> for the ownership contract.
    /// The auto-wrap fallback path is a proxy (borrowed), so <paramref name="ownsContainer"/> is
    /// <c>false</c> there — only the boxable branch transfers a fresh +1 the caller must destroy.
    /// </summary>
    public static ExistentialContainer1 GetOrCreate<TProtocol>(
        TProtocol value,
        Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback,
        out bool ownsContainer)
        where TProtocol : class
        => GetOrCreate(value, wrapFallback, out ownsContainer, out _);

    /// <summary>
    /// <c>keepAlive</c>-returning overload of
    /// <see cref="GetOrCreate{TProtocol}(TProtocol, Func{TProtocol, ISwiftExistentialConvertible{ExistentialContainer1}}, out bool)"/>
    /// (design change 4). <paramref name="keepAlive"/> is the proxy whose liveness must span the
    /// native call so that R0 is not released mid-call by a GC of an otherwise-unrooted auto-wrapped
    /// proxy — see <see cref="GetOrCreate{TProtocol}(TProtocol, out bool, out object)"/> for the full
    /// rationale. It is the (built or reused) proxy in the convertible and auto-wrap branches and
    /// <c>null</c> in the boxable branch.
    /// </summary>
    public static ExistentialContainer1 GetOrCreate<TProtocol>(
        TProtocol value,
        Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback,
        out bool ownsContainer,
        out object? keepAlive)
        where TProtocol : class
    {
        // Round-trip an already-marshalled existential container (see the wrapFallback-free overload
        // above for the full rationale) before attempting proxy/boxable/auto-wrap dispatch.
        if (value is ExistentialContainer1 roundTripContainer)
        {
            ownsContainer = false;
            keepAlive = value;
            return roundTripContainer;
        }

        if (value is ISwiftExistentialConvertible<ExistentialContainer1> convertible)
        {
            ownsContainer = false;
            keepAlive = convertible;
            return convertible.GetExistentialContainer();
        }

        if (value is IExistentialBoxable boxable)
        {
            ownsContainer = true;
            keepAlive = null;
            return boxable.BoxAsExistential1<TProtocol>();
        }

        ownsContainer = false;

        if (wrapFallback == null)
            throw new ArgumentNullException(nameof(wrapFallback));

        // Reuse a previously-created proxy for the same (impl, protocol) pair. Reference
        // identity on the impl is the right outer key — distinct impl instances always need
        // distinct proxies (otherwise Swift dispatch would land on the wrong _csharpImpl).
        // The inner dictionary keys per protocol type so that one C# instance implementing
        // multiple generated interfaces gets a distinct proxy (and matching witness table)
        // for each protocol it can be passed as. Weak references mean the
        // SwiftObjectRegistry weak root is NOT a liveness anchor — the caller MUST keep the
        // returned proxy (keepAlive) alive across the native call, else a GC could finalize it
        // mid-call and release R0 (the EveryProtocol construction +1) prematurely.
        var perImplMap = s_autoWrapCache.GetValue(
            value,
            static _ => new ConcurrentDictionary<Type, WeakReference<ISwiftExistentialConvertible<ExistentialContainer1>>>());

        if (perImplMap.TryGetValue(typeof(TProtocol), out var weakRef) &&
            weakRef.TryGetTarget(out var cached))
        {
            keepAlive = cached;
            return cached.GetExistentialContainer();
        }

        // Miss or stale weak reference — build a new proxy and publish it. The
        // proxy constructor unconditionally calls SwiftDisposeScope.TryRegister,
        // which would let scope disposal mark the still-cached proxy as disposed
        // and trip ObjectDisposedException on the next GetOrCreate. Detach
        // immediately so the cache's weak ref is the only cache-side reference.
        var proxy = wrapFallback(value);
        if (proxy is IDisposable disposable)
            SwiftDisposeScope.Detach(disposable);

        // A benign race here lets two concurrent misses both build proxies; the
        // losing proxy becomes a weak-cache orphan but is still tracked by
        // ProxyLifetimeTracker, so its +1 releases when the proxy is collected.
        perImplMap[typeof(TProtocol)] = new WeakReference<ISwiftExistentialConvertible<ExistentialContainer1>>(proxy);
        keepAlive = proxy;
        return proxy.GetExistentialContainer();
    }

    /// <summary>
    /// Builds a class-bound <see cref="ClassExistentialContainer1"/> array-element carrier whose
    /// word0 (the class reference) holds exactly ONE owned +1, ready for
    /// <c>SwiftArray&lt;ClassExistentialContainer1&gt;</c> to adopt and release once through the
    /// class-existential value-witness table on destroy.
    /// </summary>
    /// <remarks>
    /// Swift's array element write path is <c>__owned</c> (append/insert/subscript-set consume the
    /// element at +1) and the array's class-existential value-witness table releases word0 on
    /// destroy, so the array needs to OWN exactly one +1 per element. The two ways a protocol value
    /// reaches this carrier differ in who already owns the class +1, and the authoritative signal is
    /// the <c>ownsContainer</c> out-parameter of
    /// <see cref="GetOrCreate{TProtocol}(TProtocol, Func{TProtocol, ISwiftExistentialConvertible{ExistentialContainer1}}, out bool)"/>:
    /// <list type="bullet">
    /// <item><description>
    /// <b>Borrowed (convertible/proxy or auto-wrapped fallback — <c>ownsContainer == false</c>):</b>
    /// the container ALIASES a +1 owned by the proxy / <see cref="ProxyLifetimeTracker"/>. We MINT a
    /// fresh +1 via <see cref="Arc.UnknownObjectRetain"/> so the array owns its own reference and the
    /// source proxy keeps its — mirroring the proxy's <c>Arc.UnknownObjectReleaseFinalizerSafe</c> on
    /// Dispose/finalize, correct whether word0 is an Objective-C or native Swift class.
    /// </description></item>
    /// <item><description>
    /// <b>Owned (boxable conformer — <c>ownsContainer == true</c>):</b>
    /// <see cref="IExistentialBoxable.BoxAsExistential1{TProtocol}"/> → <c>Create</c> →
    /// <c>MarshalPayload</c> already minted a fresh +1 on the class ref (an inline
    /// <c>InitializeWithCopy</c>). We DONATE that +1 to the array — no second retain. This also closes
    /// the boxable orphan: the array-carrier path runs no scalar
    /// <see cref="DestroyAndFreeExistential"/>, so without donation that +1 would have no owner and
    /// leak.
    /// </description></item>
    /// </list>
    /// Because the carrier returned here always owns its +1, the array write path
    /// (<c>SwiftMarshal.MarshalToSwift&lt;ClassExistentialContainer1&gt;</c>) is a pure byte-copy that
    /// transfers ownership of those words into array storage with no further retain.
    /// </remarks>
    public static ClassExistentialContainer1 CreateOwnedClassCarrier<TProtocol>(
        TProtocol value,
        Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback)
        where TProtocol : class
    {
        // B2 change 4: an auto-wrapped proxy is the SOLE owner of R0 (the EveryProtocol
        // construction +1) and is now registered only weakly, so nothing strong roots it
        // between GetOrCreate building it and the synchronous mint below. A GC + finalizer in
        // that window would release R0 → free the Swift object → MintOrDonate's Arc retain /
        // value-witness copy reads freed memory. Keep the proxy alive until the array owns its
        // own +1. No-op (and harmless) for the convertible/boxable shapes where keepAlive is the
        // live arg or null.
        var container = GetOrCreate(value, wrapFallback, out bool ownsContainer, out object? keepAlive);
        var carrier = MintOrDonateClassCarrier(container, ownsContainer);
        GC.KeepAlive(keepAlive);
        return carrier;
    }

    /// <summary>
    /// No-fallback overload, emitted at call sites whose proxy class was suppressed (a
    /// closed-constrained PAT existential projects to a typed generic interface with no usable
    /// <c>{Protocol}Proxy</c> constructor, so <c>CSharpWrapperCoGater</c> strips the wrap-fallback
    /// argument). With no fallback the value MUST already be a Swift-vended
    /// <see cref="ISwiftExistentialConvertible{T}"/> or an <see cref="IExistentialBoxable"/> conformer
    /// — <see cref="GetOrCreate{TProtocol}(TProtocol, out bool)"/> throws otherwise, preserving the
    /// throw-on-incompatible-input contract the co-gater documents. The ownership mint/donate is
    /// identical to the two-arg overload, so a suppressed-proxy class-bound collection element is
    /// balanced exactly as a proxy-backed one. Mirrors <see cref="GetOrCreate{TProtocol}(TProtocol)"/>.
    /// </summary>
    public static ClassExistentialContainer1 CreateOwnedClassCarrier<TProtocol>(TProtocol value)
        where TProtocol : class
    {
        // B2 change 4: keep the (possibly auto-wrapped) proxy alive across the synchronous mint —
        // see the two-arg overload for the full rationale.
        var container = GetOrCreate(value, out bool ownsContainer, out object? keepAlive);
        var carrier = MintOrDonateClassCarrier(container, ownsContainer);
        GC.KeepAlive(keepAlive);
        return carrier;
    }

    private static ClassExistentialContainer1 MintOrDonateClassCarrier(ExistentialContainer1 container, bool ownsContainer)
    {
        var carrier = ClassExistentialContainer1.FromExistentialContainer1(container);
        if (!ownsContainer && carrier.ClassRef != IntPtr.Zero)
        {
            // Borrowed source: mint the array's own +1. (Owned/boxable source already carries a
            // donatable +1 on word0 from Create's InitializeWithCopy — adopt it as-is.)
            Arc.UnknownObjectRetain(carrier.ClassRef);
        }
        return carrier;
    }

    /// <summary>
    /// Opaque/40-byte sibling of <see cref="CreateOwnedClassCarrier{TProtocol}(TProtocol, Func{TProtocol, ISwiftExistentialConvertible{ExistentialContainer1}})"/>: builds an
    /// <see cref="ExistentialContainer1"/> collection-element carrier that owns exactly ONE +1 on its
    /// payload, for a non-class-bound single-protocol <c>any P</c> whose array/dictionary element
    /// strides over the full opaque container (not the compact 16-byte
    /// <see cref="ClassExistentialContainer1"/>).
    /// </summary>
    /// <remarks>
    /// Like the class-bound carrier, the Swift collection write is <c>__owned</c> (append/insert/
    /// subscript-set consume the element at +1) and the container's existential value-witness table
    /// destroys the element on teardown, so the carrier must OWN exactly one +1. The two source shapes
    /// — reported by
    /// <see cref="GetOrCreate{TProtocol}(TProtocol, Func{TProtocol, ISwiftExistentialConvertible{ExistentialContainer1}}, out bool)"/>'s
    /// <c>ownsContainer</c> signal — are handled the same way as the class-bound path, but the MINT
    /// differs: an opaque payload may be an inline class ref, an inline value type, or an out-of-line
    /// <c>swift_allocBox</c>, so we run the existential value-witness <c>InitializeWithCopy</c>
    /// (<see cref="Swift.Runtime.InteropServices.SwiftMarshal.CopyWireBufferRetains"/>) rather than the
    /// class-only <see cref="Arc.UnknownObjectRetain"/> shortcut.
    /// <list type="bullet">
    /// <item><description>
    /// <b>Borrowed (proxy/auto-wrap — <c>ownsContainer == false</c>):</b> the container ALIASES a +1
    /// owned by the proxy / <see cref="ProxyLifetimeTracker"/>. MINT a fresh owned copy so the array
    /// owns its own reference and the source proxy keeps its. Without this the <c>__owned</c> consume
    /// plus the carrier's value-witness destroy over-released the proxy's only +1 (opaque-existential
    /// sibling).
    /// </description></item>
    /// <item><description>
    /// <b>Owned (boxable conformer — <c>ownsContainer == true</c>):</b>
    /// <see cref="IExistentialBoxable.BoxAsExistential1{TProtocol}"/> → <c>Create</c> →
    /// <c>MarshalPayload</c> already minted a fresh +1 (inline <c>InitializeWithCopy</c> or
    /// <c>swift_allocBox</c>); DONATE it to the array as-is. This also closes the boxable orphan: the
    /// array-carrier path runs no scalar <see cref="DestroyAndFreeExistential"/>, so without donation
    /// that +1 would have no owner and leak.
    /// </description></item>
    /// </list>
    /// Either way the returned container owns its +1, so the array write
    /// (<c>SwiftMarshal.MarshalToSwift</c> → <c>IExistentialContainer.CopyTo</c>) is a pure byte-copy
    /// that transfers ownership of those words into array storage with no further retain.
    /// </remarks>
    public static unsafe ExistentialContainer1 CreateOwnedExistential1<TProtocol>(
        TProtocol value,
        Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback)
        where TProtocol : class
    {
        // B2 change 4: keep the (possibly auto-wrapped) proxy alive across the synchronous mint —
        // here the mint is the existential value-witness InitializeWithCopy, which reads the
        // payload; a premature R0 release would have it read freed memory. See
        // CreateOwnedClassCarrier for the full rationale.
        var container = GetOrCreate(value, wrapFallback, out bool ownsContainer, out object? keepAlive);
        var carrier = MintOrDonateExistential1(container, ownsContainer);
        GC.KeepAlive(keepAlive);
        return carrier;
    }

    /// <summary>
    /// No-fallback overload, emitted at call sites whose proxy class was suppressed (a
    /// closed-constrained PAT existential — e.g. <c>any LabelledContainer&lt;String&gt;</c> — projects
    /// to a typed generic interface with no usable <c>{Protocol}Proxy</c> constructor, so
    /// <c>CSharpWrapperCoGater</c> strips the wrap-fallback argument). With no fallback the value MUST
    /// already be a Swift-vended <see cref="ISwiftExistentialConvertible{T}"/> or an
    /// <see cref="IExistentialBoxable"/> conformer — <see cref="GetOrCreate{TProtocol}(TProtocol, out bool)"/>
    /// throws otherwise, preserving the throw-on-incompatible-input contract the co-gater documents.
    /// The ownership mint/donate is identical to the two-arg overload, so a suppressed-proxy opaque
    /// collection element is balanced exactly as a proxy-backed one (the over-release fix still
    /// applies). Mirrors <see cref="GetOrCreate{TProtocol}(TProtocol)"/>.
    /// </summary>
    public static unsafe ExistentialContainer1 CreateOwnedExistential1<TProtocol>(TProtocol value)
        where TProtocol : class
    {
        // B2 change 4: keep the (possibly auto-wrapped) proxy alive across the synchronous mint —
        // see CreateOwnedExistential1(value, wrapFallback) for the full rationale.
        var container = GetOrCreate(value, out bool ownsContainer, out object? keepAlive);
        var carrier = MintOrDonateExistential1(container, ownsContainer);
        GC.KeepAlive(keepAlive);
        return carrier;
    }

    private static unsafe ExistentialContainer1 MintOrDonateExistential1(ExistentialContainer1 container, bool ownsContainer)
    {
        if (ownsContainer)
        {
            // Boxable conformer: a fresh +1 already lives in the container — donate it to the array's
            // __owned consume.
            return container;
        }

        // Borrowed source: mint the array's own +1 via the existential value-witness InitializeWithCopy,
        // which correctly retains an inline class ref, copies an inline value type, or retains a
        // swift_allocBox payload — the general operation the class-only Arc.UnknownObjectRetain cannot do.
        ExistentialContainer1 owned = default;
        try
        {
            var metadata = TypeMetadata.GetExistentialTypeMetadata(container.Count);
            // owned/container are stack locals (already-fixed), so take their addresses directly —
            // a `fixed` statement on them is a CS0213 error.
            Swift.Runtime.InteropServices.SwiftMarshal.CopyWireBufferRetains((IntPtr)(&owned), (IntPtr)(&container), metadata);
            return owned;
        }
        catch (SwiftRuntimeException)
        {
            // GetExistentialTypeMetadata throws SwiftRuntimeException ONLY when SwiftBindingsRuntime
            // is unavailable (no-Swift-runtime unit contexts); that path never actually consumes the
            // container, so the aliased (+0) fallback is safe there. A genuine value-witness fault
            // from CopyWireBufferRetains (which runs only AFTER metadata resolves) throws a DIFFERENT
            // type and is intentionally NOT caught — masking it would hand a borrowed (+0) carrier to
            // an __owned consume and re-introduce the over-release.
            return container;
        }
    }

    /// <summary>
    /// Owned (+1) C#→Swift mint for an EC2+ COMPOSITION existential (<c>any P &amp; Q…</c>), the
    /// multi-protocol sibling of <see cref="CreateOwnedExistential1{TProtocol}(TProtocol)"/>. Emitted
    /// by <c>ExistentialProjection.GetOwnedParameterElementConversion</c> at every C#→Swift owned
    /// hand-off of a composition existential (reverse-dispatch getter/method returns, closure returns).
    /// <para>
    /// The only C# type that implements a composition interface is the Swift-vended
    /// <c>{Composition}Proxy</c>: value conformers box solely as EC1 via
    /// <see cref="IExistentialBoxable.BoxAsExistential1{TProtocol}"/> (there is no
    /// <c>BoxAsExistential2</c>), so <see cref="ISwiftExistentialConvertible{T}.GetExistentialContainer"/>
    /// here ALWAYS returns the proxy's stored bytes BORROWED — no fresh retain. Handing those bytes back
    /// to Swift unchanged at +1 would alias the proxy's sole construction +1 (R0): Swift's owned release
    /// and the proxy's eventual release would both target that one retain — a double-release. So this
    /// unconditionally mints an independent +1 via the existential value-witness InitializeWithCopy
    /// (arity-generic in <see cref="IExistentialContainer.Count"/>), exactly as
    /// <see cref="MintOrDonateExistential1"/>'s borrowed arm does for EC1. Unlike EC1 there is no donate
    /// arm — no boxable composition conformer exists, so the source is unconditionally borrowed and the
    /// owns-bit branching collapses away.
    /// </para>
    /// </summary>
    /// <typeparam name="TProtocol">The composition interface (e.g. <c>IAgeableAndNameable</c>).</typeparam>
    /// <typeparam name="TContainer">The EC2..EC8 carrier the proxy implements (e.g. <c>ExistentialContainer2</c>).</typeparam>
    public static unsafe TContainer CreateOwnedCompositionExistential<TProtocol, TContainer>(TProtocol value)
        where TProtocol : class
        where TContainer : unmanaged, IExistentialContainer
    {
        // Only a Swift-vended proxy implements the composition interface, so this cast and the
        // borrowed GetExistentialContainer() always succeed; an incompatible input throws
        // InvalidCastException, preserving the throw-on-incompatible contract (mirrors the
        // no-fallback CreateOwnedExistential1 overload).
        var container = ((ISwiftExistentialConvertible<TContainer>)value).GetExistentialContainer();
        TContainer owned = default;
        try
        {
            var metadata = TypeMetadata.GetExistentialTypeMetadata(container.Count);
            // owned/container are unmanaged stack locals; Unsafe.AsPointer takes their addresses
            // without a `fixed` (a CS0213 error on already-fixed locals, and unavailable for an
            // open unmanaged generic).
            Swift.Runtime.InteropServices.SwiftMarshal.CopyWireBufferRetains(
                (IntPtr)Unsafe.AsPointer(ref owned), (IntPtr)Unsafe.AsPointer(ref container), metadata);
            // B2 change 4: the proxy is the SOLE owner of R0 (the EveryProtocol construction +1) and
            // is registered only weakly, so nothing strong roots it across this mint. `container` is an
            // unmanaged stack copy of the proxy's bytes — keeping IT alive is a no-op; the InitializeWithCopy
            // above retains the payload the proxy owns, so a GC + finalizer between GetExistentialContainer()
            // and this retain would release R0 → free the Swift object → the copy reads/retains freed memory.
            // Root the proxy itself across the mint, mirroring the EC1 CreateOwnedExistential1 keepAlive.
            GC.KeepAlive(value);
            return owned;
        }
        catch (SwiftRuntimeException)
        {
            // SwiftBindingsRuntime unavailable (no-Swift-runtime unit contexts): nothing there
            // consumes the container, so the aliased (+0) fallback is safe. A genuine value-witness
            // fault throws a DIFFERENT type and is intentionally NOT caught — masking it would
            // re-introduce the over-release. Mirrors MintOrDonateExistential1.
            return container;
        }
    }

    /// <summary>
    /// Releases a heap-allocated existential container that the marshalling layer passed by
    /// pointer to a borrowing Swift <c>@_cdecl</c> wrapper, then frees the buffer.
    /// </summary>
    /// <remarks>
    /// Swift receives <c>any P</c> arguments <c>@in_guaranteed</c> (borrowed): the wrapper reads
    /// the container with a copying <c>load</c>/<c>.pointee</c> and never releases the caller's
    /// buffer. So when the C# side boxed the conformer at +1 — a value-type conformer routed
    /// through <see cref="IExistentialBoxable.BoxAsExistential1{TProtocol}"/> / <c>swift_allocBox</c>,
    /// reported by the <c>GetOrCreate(..., out owns)</c> overloads as <paramref name="owns"/> ==
    /// <see langword="true"/> — the caller must run the existential value-witness <c>destroy</c> to
    /// balance that +1 once the call returns. A borrowed/+0 container (class or proxy conformer, or
    /// a well-known container such as AnyError) reports <paramref name="owns"/> ==
    /// <see langword="false"/> and is only freed; destroying it would over-release.
    ///
    /// Single release path shared by every existential-parameter marshalling site — method/
    /// function/constructor params (WrapperEmitter), enum-case factories (EnumHandler), and
    /// optional-existential setters (PropertyHandler).
    /// </remarks>
    /// <param name="heap">Buffer returned by <c>NativeMemory.Alloc</c> holding the container, or null.</param>
    /// <param name="witnessTableCount">Protocol-witness-table count of the container (EC1 = 1).</param>
    /// <param name="owns">Whether the C# side boxed the payload at +1 (from <c>GetOrCreate(..., out owns)</c>).</param>
    public static unsafe void DestroyAndFreeExistential(void* heap, int witnessTableCount, bool owns)
    {
        if (heap == null)
            return;

        try
        {
            if (owns)
            {
                // The existential value-witness destroy handles inline and boxed payloads
                // uniformly (releases the swift_allocBox or the inline InitializeWithCopy retains).
                var metadata = TypeMetadata.GetExistentialTypeMetadata(witnessTableCount);
                Swift.Runtime.InteropServices.SwiftMarshal.DestroyWireBufferRetains((IntPtr)heap, metadata);
            }
        }
        catch (SwiftRuntimeException)
        {
            // GetExistentialTypeMetadata throws SwiftRuntimeException ONLY when SwiftBindingsRuntime
            // is unavailable (no-Swift-runtime unit contexts); the unbalanced +1 leak is benign there.
            // A genuine destroy fault throws a different type and is intentionally NOT swallowed —
            // masking it would hide a real over-release. The buffer is freed either way (finally).
        }
        finally
        {
            NativeMemory.Free(heap);
        }
    }

    /// <summary>
    /// Creates an existential container from a value using a pre-computed witness table array.
    /// This is useful when the protocol types are not known at compile time.
    /// </summary>
    /// <typeparam name="T">The Swift object type.</typeparam>
    /// <param name="value">The value to box.</param>
    /// <param name="witnessTables">Array of protocol witness table handles.</param>
    /// <returns>An IExistentialContainer with the appropriate number of witness tables.</returns>
    /// <exception cref="ArgumentException">Thrown if more than 8 witness tables are provided.</exception>
    public static IExistentialContainer CreateWithWitnessTables<T>(T value, params ProtocolWitnessTable[] witnessTables)
        where T : ISwiftObject
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();

        return witnessTables.Length switch
        {
            0 => CreateContainerWithWitnessTables<T, ExistentialContainer0>(value, metadata, witnessTables),
            1 => CreateContainerWithWitnessTables<T, ExistentialContainer1>(value, metadata, witnessTables),
            2 => CreateContainerWithWitnessTables<T, ExistentialContainer2>(value, metadata, witnessTables),
            3 => CreateContainerWithWitnessTables<T, ExistentialContainer3>(value, metadata, witnessTables),
            4 => CreateContainerWithWitnessTables<T, ExistentialContainer4>(value, metadata, witnessTables),
            5 => CreateContainerWithWitnessTables<T, ExistentialContainer5>(value, metadata, witnessTables),
            6 => CreateContainerWithWitnessTables<T, ExistentialContainer6>(value, metadata, witnessTables),
            7 => CreateContainerWithWitnessTables<T, ExistentialContainer7>(value, metadata, witnessTables),
            8 => CreateContainerWithWitnessTables<T, ExistentialContainer8>(value, metadata, witnessTables),
            _ => throw new ArgumentException($"Too many witness tables ({witnessTables.Length}). Maximum supported is 8.")
        };
    }

    private static TContainer CreateContainerWithWitnessTables<T, TContainer>(
        T value,
        TypeMetadata metadata,
        ProtocolWitnessTable[] witnessTables)
        where T : ISwiftObject
        where TContainer : struct, IExistentialContainer
    {
        var container = new TContainer();
        container.ObjectMetadata = metadata;

        for (int i = 0; i < witnessTables.Length; i++)
        {
            container[i] = witnessTables[i].Handle;
        }

        MarshalPayload(value, metadata, ref container);
        return container;
    }
}
