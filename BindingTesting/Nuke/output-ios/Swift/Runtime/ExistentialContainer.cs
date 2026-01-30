// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

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
