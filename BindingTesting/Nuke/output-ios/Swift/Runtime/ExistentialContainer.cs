// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Swift.Runtime;

/// <summary>
/// Existential container with 0 witness tables (represents 'Any' or 'any Any').
/// Size: 4 machine words (32 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
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
}

/// <summary>
/// Existential container with 1 witness table.
/// Size: 5 machine words (40 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
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
/// Existential container with 2 witness tables.
/// Size: 6 machine words (48 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
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
/// Existential container with 3 witness tables.
/// Size: 7 machine words (56 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
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
/// Existential container with 4 witness tables.
/// Size: 8 machine words (64 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
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
/// Existential container with 5 witness tables.
/// Size: 9 machine words (72 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
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
/// Existential container with 6 witness tables.
/// Size: 10 machine words (80 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
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
/// Existential container with 7 witness tables.
/// Size: 11 machine words (88 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
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
/// Existential container with 8 witness tables.
/// Size: 12 machine words (96 bytes on 64-bit).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
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
/// Existential containers are how Swift passes values of protocol type (e.g., "any Protocol").
/// This factory enables passing C# objects that implement Swift protocols to Swift code.
/// </summary>
public static class ExistentialContainerFactory
{
    /// <summary>
    /// Maximum size in bytes that can be stored inline in an existential container's payload.
    /// On 64-bit systems, this is 24 bytes (3 machine words).
    /// Values larger than this are heap-allocated.
    /// </summary>
    public const int MaxInlinePayloadSize = 3 * 8; // 3 machine words on 64-bit

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
        container[0] = ProtocolWitnessTable.GetOrThrow<T, TProtocol>().Handle;
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
        container[0] = ProtocolWitnessTable.GetOrThrow<T, TProtocol1>().Handle;
        container[1] = ProtocolWitnessTable.GetOrThrow<T, TProtocol2>().Handle;
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
        container[0] = ProtocolWitnessTable.GetOrThrow<T, TProtocol1>().Handle;
        container[1] = ProtocolWitnessTable.GetOrThrow<T, TProtocol2>().Handle;
        container[2] = ProtocolWitnessTable.GetOrThrow<T, TProtocol3>().Handle;
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
        container[0] = ProtocolWitnessTable.GetOrThrow<T, TProtocol1>().Handle;
        container[1] = ProtocolWitnessTable.GetOrThrow<T, TProtocol2>().Handle;
        container[2] = ProtocolWitnessTable.GetOrThrow<T, TProtocol3>().Handle;
        container[3] = ProtocolWitnessTable.GetOrThrow<T, TProtocol4>().Handle;
        MarshalPayload(value, metadata, ref container);
        return container;
    }

    /// <summary>
    /// Marshals the value's payload into the existential container.
    /// Small values (≤ MaxInlinePayloadSize and not non-inline) are stored directly in the payload slots.
    /// Larger values are heap-allocated and a pointer is stored in Payload0.
    /// </summary>
    private static unsafe void MarshalPayload<T, TContainer>(T value, TypeMetadata metadata, ref TContainer container)
        where T : ISwiftObject
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
            // Allocate on heap using Swift's allocator semantics
            var alignment = (nuint)vwt->Alignment;
            var heapPtr = NativeMemory.AlignedAlloc((nuint)size, alignment);

            // Marshal the value into the heap buffer
            var heapSpan = new Span<byte>(heapPtr, size);
            value.MarshalToSwift(ref heapSpan);

            // Store pointer in first payload slot
            container.Payload0 = (IntPtr)heapPtr;
            container.Payload1 = IntPtr.Zero;
            container.Payload2 = IntPtr.Zero;
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
