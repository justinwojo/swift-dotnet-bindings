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
                if (!TypeMetadata.Cache.TryGet(typeof(nint), out var metadata))
                    throw new SwiftRuntimeException("Cannot get Swift.Int metadata");
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
                throw new NotSupportedException(
                    $"Cannot box value of type '{value.GetType().Name}' into ExistentialContainer0. " +
                    $"Supported types: bool, int, long, double, string.");
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
    {
        if (value is ISwiftExistentialConvertible<ExistentialContainer1> convertible)
            return convertible.GetExistentialContainer();

        if (value is IExistentialBoxable boxable)
            return boxable.BoxAsExistential1<TProtocol>();

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
    /// The inner value is wrapped in <see cref="Lazy{T}"/> with
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> so that under concurrent first
    /// access for the same <c>(impl, protocol)</c> pair the wrap fallback executes exactly once.
    /// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/>'s value
    /// factory may run multiple times when threads race, and each losing factory invocation
    /// would otherwise allocate a fresh <c>EveryProtocol</c> and register a hidden proxy with
    /// <see cref="SwiftObjectRegistry.RegisterStrong{TProxy}(IntPtr, TProxy)"/> — leaking
    /// proxies that the cache never returns. <see cref="Lazy{T}.Value"/> serializes construction
    /// even when several losing <see cref="Lazy{T}"/> wrappers are produced by GetOrAdd.
    /// </para>
    /// </remarks>
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<Type, Lazy<ISwiftExistentialConvertible<ExistentialContainer1>>>>
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
    /// <b>Lifetime caveat (auto-wrap path):</b> The generated proxy registers itself with
    /// <c>SwiftObjectRegistry.RegisterStrong</c> in its constructor, which holds a strong
    /// managed reference for the lifetime of the process. There is currently no callback from
    /// Swift's <c>EveryProtocol</c> deinit to release this strong reference, so each distinct
    /// <c>(impl, protocol)</c> pair leaks one proxy until process exit. The cache bounds the
    /// leak to the number of <c>(impl, protocol)</c> pairs ever passed (not the number of
    /// assignments). For typical "set delegate once" usage this is negligible.
    /// </para>
    /// <para>
    /// <b>SwiftDisposeScope is intentionally bypassed for cached proxies:</b> the auto-wrap
    /// factory immediately detaches each new proxy from the active scope (if any) so that
    /// scope disposal cannot mark a still-cached proxy as disposed and trip
    /// <see cref="ObjectDisposedException"/> on the next reuse. To control auto-wrap proxy
    /// lifetime explicitly, construct the hidden <c>{Protocol}Proxy</c> manually and dispose
    /// it yourself — the manual path goes through branch (1) and never enters the cache. The
    /// proper fix — adding a Swift-side deinit callback so proxies can be released when Swift
    /// releases the existential — is tracked in the roadmap.
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
    {
        if (value is ISwiftExistentialConvertible<ExistentialContainer1> convertible)
            return convertible.GetExistentialContainer();

        if (value is IExistentialBoxable boxable)
            return boxable.BoxAsExistential1<TProtocol>();

        if (wrapFallback == null)
            throw new ArgumentNullException(nameof(wrapFallback));

        // Reuse a previously-created proxy for the same (impl, protocol) pair. Reference
        // identity on the impl is the right outer key — distinct impl instances always need
        // distinct proxies (otherwise Swift dispatch would land on the wrong _csharpImpl).
        // The inner dictionary keys per protocol type so that one C# instance implementing
        // multiple generated interfaces gets a distinct proxy (and matching witness table)
        // for each protocol it can be passed as. The Lazy<T> wrapper ensures the wrap
        // fallback runs exactly once per (impl, protocol) pair even under concurrent first
        // access — see the cache field's remarks for the leak this prevents.
        var perImplMap = s_autoWrapCache.GetValue(
            value,
            static _ => new ConcurrentDictionary<Type, Lazy<ISwiftExistentialConvertible<ExistentialContainer1>>>());
        var lazy = perImplMap.GetOrAdd(
            typeof(TProtocol),
            _ => new Lazy<ISwiftExistentialConvertible<ExistentialContainer1>>(
                () =>
                {
                    var proxy = wrapFallback(value);
                    // Cached proxies live for the cache lifetime, not the active dispose scope.
                    // The proxy constructor unconditionally calls SwiftDisposeScope.TryRegister,
                    // which would let scope disposal mark the still-cached proxy as disposed and
                    // trip ObjectDisposedException on the next GetOrCreate. Detach immediately
                    // so the cache owns the proxy lifetime exclusively.
                    if (proxy is IDisposable disposable)
                        SwiftDisposeScope.Detach(disposable);
                    return proxy;
                },
                LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.GetExistentialContainer();
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
