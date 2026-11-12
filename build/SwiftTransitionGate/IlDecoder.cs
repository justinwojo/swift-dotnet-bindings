// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Link-compiled into projects that do not enable nullable reference types, so the annotations are
// opted into locally rather than inherited from a csproj.
#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;

namespace SwiftBindings.TransitionGate;

/// <summary>
/// One entry of the ECMA-335 opcode table: the opcode, its encoded length (1, or 2 for the
/// <c>0xFE</c>-prefixed page) and the class of its inline operand.
/// </summary>
public readonly record struct IlOpcodeInfo(ILOpCode OpCode, string Name, int EncodedSize, OperandType OperandType);

/// <summary>
/// The complete IL opcode table, derived rather than hand-maintained.
///
/// <para>The opcode values, names and operand classes come from the BCL's own
/// <see cref="OpCodes"/> table, which the runtime's IL emitter and verifier use. The set of opcodes
/// is cross-checked against <see cref="ILOpCode"/> from <c>System.Reflection.Metadata</c> when the
/// table is built: an opcode one library knows and the other does not makes construction throw, so
/// the two tables cannot drift silently. The one hand-written part is the operand-class → byte-size
/// mapping, which is fixed by ECMA-335 Partition III and pinned by unit tests.</para>
///
/// <para>The decoder exists because a scanner that gets one operand size wrong does not fail, it
/// desynchronizes: it reads an operand byte as the next opcode and can step over the instruction it
/// is looking for. The prototype IL-shape tool had exactly that defect (<c>conv.u2</c> read as
/// taking four operand bytes, <c>ldarga</c> as four instead of two).</para>
/// </summary>
public static class IlOpcodeTable
{
    private static readonly IlOpcodeInfo?[] OneByte = new IlOpcodeInfo?[256];
    private static readonly IlOpcodeInfo?[] TwoByte = new IlOpcodeInfo?[256];

    /// <summary>The <c>0xFE</c> prefix byte that selects the two-byte opcode page.</summary>
    public const byte TwoBytePrefix = 0xFE;

    private const ushort NoPrefix = 0xFE19;

    /// <summary>Every defined opcode, one-byte page first, in ascending value order.</summary>
    public static ImmutableArray<IlOpcodeInfo> All { get; }

    static IlOpcodeTable()
    {
        var builder = ImmutableArray.CreateBuilder<IlOpcodeInfo>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
                continue;
            var op = (OpCode)field.GetValue(null)!;
            // The Prefix1…Prefix7/Prefixref entries are the reserved prefix bytes themselves
            // (0xF8–0xFF), not instructions; Prefix1 is the 0xFE page selector.
            if (op.OpCodeType == OpCodeType.Nternal)
                continue;
            var value = unchecked((ushort)op.Value);
            var info = new IlOpcodeInfo((ILOpCode)value, op.Name ?? field.Name, op.Size, op.OperandType);
            if (op.Size == 1)
            {
                if (value > 0xFF)
                    throw new InvalidOperationException($"One-byte opcode {op.Name} has value 0x{value:X}.");
                Set(OneByte, value, info);
            }
            else if (op.Size == 2)
            {
                if ((value >> 8) != TwoBytePrefix)
                    throw new InvalidOperationException($"Two-byte opcode {op.Name} has value 0x{value:X4}.");
                Set(TwoByte, value & 0xFF, info);
            }
            else
            {
                throw new InvalidOperationException($"Opcode {op.Name} has encoded size {op.Size}.");
            }
            _ = OperandSize(op.OperandType); // throws on an operand class the decoder does not know
            builder.Add(info);
        }

        // ECMA-335 defines one instruction neither BCL table carries: the no. prefix (0xFE 0x19,
        // Partition III §2.2), which takes a one-byte flag. No compiler here emits it, but a
        // complete decoder must step over it rather than fail on it.
        var no = new IlOpcodeInfo((ILOpCode)NoPrefix, "no.", 2, OperandType.ShortInlineI);
        Set(TwoByte, NoPrefix & 0xFF, no);
        builder.Add(no);

        // Cross-check against System.Reflection.Metadata's opcode enumeration, in both directions.
        var known = new HashSet<ushort>();
        foreach (var info in builder)
            known.Add((ushort)info.OpCode);
        foreach (ILOpCode value in Enum.GetValues<ILOpCode>())
        {
            if (!known.Remove((ushort)value))
                throw new InvalidOperationException(
                    $"System.Reflection.Metadata defines opcode {value} (0x{(ushort)value:X}) that the BCL OpCodes table lacks.");
        }
        known.Remove(NoPrefix);
        if (known.Count > 0)
            throw new InvalidOperationException(
                $"The BCL OpCodes table defines {known.Count} opcode(s) System.Reflection.Metadata lacks.");

        builder.Sort((a, b) => ((ushort)a.OpCode).CompareTo((ushort)b.OpCode));
        All = builder.ToImmutable();

        static void Set(IlOpcodeInfo?[] page, int index, IlOpcodeInfo info)
        {
            if (page[index] is { } existing)
                throw new InvalidOperationException($"Opcodes {existing.Name} and {info.Name} share an encoding.");
            page[index] = info;
        }
    }

    /// <summary>Looks up a one-byte opcode; <c>0xFE</c> and undefined bytes return false.</summary>
    public static bool TryGetOneByte(byte value, out IlOpcodeInfo info) => TryGet(OneByte, value, out info);

    /// <summary>Looks up the opcode whose encoding is <c>0xFE</c> followed by <paramref name="second"/>.</summary>
    public static bool TryGetTwoByte(byte second, out IlOpcodeInfo info) => TryGet(TwoByte, second, out info);

    private static bool TryGet(IlOpcodeInfo?[] page, byte index, out IlOpcodeInfo info)
    {
        if (page[index] is { } found)
        {
            info = found;
            return true;
        }
        info = default;
        return false;
    }

    /// <summary>
    /// The fixed byte size of an inline operand of class <paramref name="operandType"/> (ECMA-335
    /// Partition III §1.2/§1.9). <see cref="OperandType.InlineSwitch"/> returns the size of its count
    /// field only; its target table follows (four bytes per target).
    /// </summary>
    public static int OperandSize(OperandType operandType) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
            or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR or OperandType.InlineSwitch => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => throw new InvalidOperationException($"Unknown IL operand class {operandType}."),
    };
}

/// <summary>
/// One decoded IL instruction. Exactly one operand field is meaningful, chosen by
/// <see cref="OperandType"/>: <see cref="Int64Operand"/> carries integer immediates, variable
/// indices and metadata tokens; <see cref="DoubleOperand"/> carries <c>ldc.r4</c>/<c>ldc.r8</c>;
/// branch instructions carry their absolute target in <see cref="BranchTargets"/>.
/// </summary>
public readonly record struct IlInstruction(
    int Offset,
    int Size,
    ILOpCode OpCode,
    OperandType OperandType,
    long Int64Operand,
    double DoubleOperand,
    ImmutableArray<int> BranchTargets)
{
    /// <summary>The metadata token of an <c>Inline{Field,Method,Sig,String,Tok,Type}</c> operand.</summary>
    public int Token => (int)Int64Operand;
}

/// <summary>A decoding failure: the IL offset at which it was detected and why.</summary>
public readonly record struct IlDecodeError(int Offset, string Message);

/// <summary>The decoded instruction stream of one method body, or the first error that stopped it.</summary>
public sealed record IlDecodeResult(ImmutableArray<IlInstruction> Instructions, IlDecodeError? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// A complete, table-driven IL decoder over <see cref="IlOpcodeTable"/>.
///
/// <para>Decoding is checked, not best-effort. A body fails if it contains an undefined opcode, if
/// an operand runs past the end of the body (so the instructions must consume exactly the body's
/// code size), or if a branch target, switch target or exception-region boundary does not land on
/// an instruction start. The last check is what turns a silent desynchronization into an error: a
/// mis-sized operand shifts every later instruction boundary, and the branch targets the compiler
/// wrote stop agreeing with them.</para>
/// </summary>
public static class IlDecoder
{
    /// <summary>Decodes <paramref name="il"/> with no exception regions to check.</summary>
    public static IlDecodeResult Decode(ReadOnlySpan<byte> il) => Decode(il, ReadOnlySpan<(int Start, int Length)>.Empty);

    /// <summary>
    /// Decodes <paramref name="il"/> and checks that every branch target and every exception-region
    /// boundary in <paramref name="regions"/> (try and handler ranges, filter starts) lands on an
    /// instruction start or on the end of the body.
    /// </summary>
    public static IlDecodeResult Decode(ReadOnlySpan<byte> il, ReadOnlySpan<(int Start, int Length)> regions)
    {
        var instructions = ImmutableArray.CreateBuilder<IlInstruction>();
        var offset = 0;
        while (offset < il.Length)
        {
            var start = offset;
            IlOpcodeInfo info;
            var first = il[offset++];
            if (first == IlOpcodeTable.TwoBytePrefix)
            {
                if (offset >= il.Length)
                    return Fail(start, "truncated two-byte opcode");
                var second = il[offset++];
                if (!IlOpcodeTable.TryGetTwoByte(second, out info))
                    return Fail(start, $"undefined opcode 0xFE 0x{second:X2}");
            }
            else if (!IlOpcodeTable.TryGetOneByte(first, out info))
            {
                return Fail(start, $"undefined opcode 0x{first:X2}");
            }

            var operandSize = IlOpcodeTable.OperandSize(info.OperandType);
            if (il.Length - offset < operandSize)
                return Fail(start, $"{info.Name}: operand needs {operandSize} byte(s), {il.Length - offset} remain");

            var operand = il.Slice(offset, operandSize);
            offset += operandSize;
            long int64 = 0;
            double real = 0;
            var targets = ImmutableArray<int>.Empty;
            switch (info.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                    targets = ImmutableArray.Create(offset + (sbyte)operand[0]);
                    break;
                case OperandType.InlineBrTarget:
                    targets = ImmutableArray.Create(offset + ReadInt32(operand));
                    break;
                case OperandType.ShortInlineI:
                    // ldc.i4.s is signed; unaligned. and no. take an unsigned byte.
                    int64 = info.OpCode == ILOpCode.Ldc_i4_s ? (sbyte)operand[0] : operand[0];
                    break;
                case OperandType.ShortInlineVar:
                    int64 = operand[0];
                    break;
                case OperandType.InlineVar:
                    int64 = (ushort)(operand[0] | (operand[1] << 8));
                    break;
                case OperandType.InlineI:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    int64 = ReadInt32(operand);
                    break;
                case OperandType.InlineI8:
                    int64 = (long)ReadUInt32(operand) | ((long)ReadUInt32(operand.Slice(4)) << 32);
                    break;
                case OperandType.ShortInlineR:
                    real = BitConverter.Int32BitsToSingle(ReadInt32(operand));
                    break;
                case OperandType.InlineR:
                    real = BitConverter.Int64BitsToDouble((long)ReadUInt32(operand) | ((long)ReadUInt32(operand.Slice(4)) << 32));
                    break;
                case OperandType.InlineSwitch:
                {
                    var count = ReadUInt32(operand);
                    if (count > (uint)((il.Length - offset) / 4))
                        return Fail(start, $"switch: {count} target(s) do not fit in the {il.Length - offset} remaining byte(s)");
                    var table = il.Slice(offset, (int)count * 4);
                    offset += (int)count * 4;
                    var builder = ImmutableArray.CreateBuilder<int>((int)count);
                    for (var i = 0; i < (int)count; i++)
                        builder.Add(offset + ReadInt32(table.Slice(i * 4)));
                    targets = builder.MoveToImmutable();
                    int64 = count;
                    break;
                }
                default:
                    return Fail(start, $"{info.Name}: unhandled operand class {info.OperandType}");
            }

            instructions.Add(new IlInstruction(start, offset - start, info.OpCode, info.OperandType, int64, real, targets));
        }

        var decoded = instructions.ToImmutable();
        var boundaries = new HashSet<int>(decoded.Length + 1) { il.Length };
        foreach (var instruction in decoded)
            boundaries.Add(instruction.Offset);
        foreach (var instruction in decoded)
        {
            foreach (var target in instruction.BranchTargets)
            {
                if (target < 0 || target >= il.Length || !boundaries.Contains(target))
                    return new IlDecodeResult(decoded,
                        new IlDecodeError(instruction.Offset, $"{Name(instruction.OpCode)}: target IL_{target:X4} is not an instruction start"));
            }
        }
        foreach (var (regionStart, regionLength) in regions)
        {
            if (!boundaries.Contains(regionStart) || !boundaries.Contains(regionStart + regionLength))
                return new IlDecodeResult(decoded,
                    new IlDecodeError(regionStart, $"exception region [IL_{regionStart:X4}, +{regionLength}) does not align with instruction boundaries"));
        }
        return new IlDecodeResult(decoded, null);

        IlDecodeResult Fail(int at, string message) => new(instructions.ToImmutable(), new IlDecodeError(at, message));
    }

    /// <summary>
    /// Decodes a method body read from metadata, checking every exception region (try range,
    /// handler range and filter start) against the decoded instruction boundaries.
    /// </summary>
    public static IlDecodeResult Decode(MethodBodyBlock body)
    {
        var regions = new List<(int, int)>();
        foreach (var region in body.ExceptionRegions)
        {
            regions.Add((region.TryOffset, region.TryLength));
            regions.Add((region.HandlerOffset, region.HandlerLength));
            if (region.Kind == ExceptionRegionKind.Filter)
                regions.Add((region.FilterOffset, 0));
        }
        var reader = body.GetILReader();
        var bytes = reader.ReadBytes(reader.Length);
        return Decode(bytes, regions.ToArray());
    }

    private static string Name(ILOpCode opCode)
    {
        var value = (ushort)opCode;
        var found = value > 0xFF
            ? IlOpcodeTable.TryGetTwoByte((byte)value, out var info)
            : IlOpcodeTable.TryGetOneByte((byte)value, out info);
        return found ? info.Name : opCode.ToString();
    }

    private static int ReadInt32(ReadOnlySpan<byte> b) => b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);

    private static uint ReadUInt32(ReadOnlySpan<byte> b) => (uint)ReadInt32(b);
}
