// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="IlDecoder"/>, the IL decoder under the Swift transition gate's calli rules.
/// A decoder that mis-sizes one operand desynchronizes every later instruction and can read a
/// <c>calli</c> as something else (or invent one), so these pin the opcode table against both BCL
/// tables and ECMA-335, decode one instruction of every operand class to its exact operand, and
/// check that every way a body can fail to decode is reported rather than skipped.
/// </summary>
public class IlDecoderTests
{
    [Fact]
    public void Table_IsEveryOpcodeSystemReflectionMetadataDefines_PlusTheEcmaOnlyNoPrefix()
    {
        var table = IlOpcodeTable.All.Select(i => (ushort)i.OpCode).ToHashSet();
        var expected = Enum.GetValues<ILOpCode>().Select(o => (ushort)o).Append((ushort)0xFE19).ToHashSet();
        Assert.Equal(expected.Count, IlOpcodeTable.All.Length);
        Assert.True(expected.SetEquals(table));
    }

    [Theory]
    // value, encoded size, operand class, operand bytes (ECMA-335 Partition III)
    [InlineData(0x00, 1, OperandType.InlineNone, 0)]           // nop
    [InlineData(0xD1, 1, OperandType.InlineNone, 0)]           // conv.u2
    [InlineData(0x0F, 1, OperandType.ShortInlineVar, 1)]       // ldarga.s
    [InlineData(0xFE0A, 2, OperandType.InlineVar, 2)]          // ldarga
    [InlineData(0xFE09, 2, OperandType.InlineVar, 2)]          // ldarg
    [InlineData(0x1F, 1, OperandType.ShortInlineI, 1)]         // ldc.i4.s
    [InlineData(0x20, 1, OperandType.InlineI, 4)]              // ldc.i4
    [InlineData(0x21, 1, OperandType.InlineI8, 8)]             // ldc.i8
    [InlineData(0x22, 1, OperandType.ShortInlineR, 4)]         // ldc.r4
    [InlineData(0x23, 1, OperandType.InlineR, 8)]              // ldc.r8
    [InlineData(0x29, 1, OperandType.InlineSig, 4)]            // calli
    [InlineData(0x28, 1, OperandType.InlineMethod, 4)]         // call
    [InlineData(0x7B, 1, OperandType.InlineField, 4)]          // ldfld
    [InlineData(0x72, 1, OperandType.InlineString, 4)]         // ldstr
    [InlineData(0xD0, 1, OperandType.InlineTok, 4)]            // ldtoken
    [InlineData(0x8C, 1, OperandType.InlineType, 4)]           // box
    [InlineData(0x2B, 1, OperandType.ShortInlineBrTarget, 1)]  // br.s
    [InlineData(0x38, 1, OperandType.InlineBrTarget, 4)]       // br
    [InlineData(0x45, 1, OperandType.InlineSwitch, 4)]         // switch (count; targets follow)
    [InlineData(0xFE12, 2, OperandType.ShortInlineI, 1)]       // unaligned.
    [InlineData(0xFE19, 2, OperandType.ShortInlineI, 1)]       // no.
    [InlineData(0xFE06, 2, OperandType.InlineMethod, 4)]       // ldftn
    public void Table_MatchesEcmaEncodingAndOperandSize(int value, int encodedSize, OperandType operandType, int operandBytes)
    {
        var info = IlOpcodeTable.All.Single(i => (ushort)i.OpCode == value);
        Assert.Equal(encodedSize, info.EncodedSize);
        Assert.Equal(operandType, info.OperandType);
        Assert.Equal(operandBytes, IlOpcodeTable.OperandSize(info.OperandType));
        var found = encodedSize == 1
            ? IlOpcodeTable.TryGetOneByte((byte)value, out var looked)
            : IlOpcodeTable.TryGetTwoByte((byte)value, out looked);
        Assert.True(found);
        Assert.Equal(info, looked);
    }

    [Fact]
    public void Table_PrefixBytesAreNotInstructions()
    {
        Assert.False(IlOpcodeTable.TryGetOneByte(IlOpcodeTable.TwoBytePrefix, out _));
        for (var b = 0xF8; b <= 0xFF; b++)
            Assert.False(IlOpcodeTable.TryGetOneByte((byte)b, out _));
    }

    [Fact]
    public void Decode_EveryOperandClass_ReadsExactOperandsAndOffsets()
    {
        var il = new List<byte>();
        void Emit(params byte[] bytes) => il.AddRange(bytes);
        Emit(0x00);                                              // 0  nop
        Emit(0x1F, 0xFB);                                        // 1  ldc.i4.s -5
        Emit(0x20, 0x78, 0x56, 0x34, 0x12);                      // 3  ldc.i4 0x12345678
        Emit(0x21, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x81); // 8  ldc.i8 0x8102030405060708
        Emit(0x22);                                              // 17 ldc.r4 1.5
        Emit(BitConverter.GetBytes(1.5f));
        Emit(0x23);                                              // 22 ldc.r8 -2.25
        Emit(BitConverter.GetBytes(-2.25));
        Emit(0x0E, 0xF0);                                        // 31 ldarg.s 240 (unsigned)
        Emit(0xFE, 0x09, 0x34, 0x12);                            // 33 ldarg 0x1234
        Emit(0x28, 0x01, 0x00, 0x00, 0x0A);                      // 37 call 0x0A000001
        Emit(0x29, 0x02, 0x00, 0x00, 0x11);                      // 42 calli 0x11000002
        Emit(0xFE, 0x12, 0x04);                                  // 47 unaligned. 4
        Emit(0x2B, 0x00);                                        // 50 br.s +0 -> 52
        Emit(0x45, 0x02, 0x00, 0x00, 0x00,                       // 52 switch (2) -> 65 + 0, 65 + 5
             0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00);
        Emit(0x38, 0x00, 0x00, 0x00, 0x00);                      // 65 br +0 -> 70
        Emit(0x2A);                                              // 70 ret

        var result = IlDecoder.Decode(il.ToArray());
        Assert.True(result.Succeeded, result.Error?.Message);
        var x = result.Instructions;
        Assert.Equal(new[] { 0, 1, 3, 8, 17, 22, 31, 33, 37, 42, 47, 50, 52, 65, 70 }, x.Select(i => i.Offset));
        Assert.Equal(-5, x[1].Int64Operand);
        Assert.Equal(0x12345678, x[2].Int64Operand);
        Assert.Equal(unchecked((long)0x8102030405060708UL), x[3].Int64Operand);
        Assert.Equal(1.5, x[4].DoubleOperand);
        Assert.Equal(-2.25, x[5].DoubleOperand);
        Assert.Equal(240, x[6].Int64Operand);
        Assert.Equal(0x1234, x[7].Int64Operand);
        Assert.Equal(ILOpCode.Ldarg, x[7].OpCode);
        Assert.Equal(0x0A000001, x[8].Token);
        Assert.Equal(ILOpCode.Calli, x[9].OpCode);
        Assert.Equal(0x11000002, x[9].Token);
        Assert.Equal(ILOpCode.Unaligned, x[10].OpCode);
        Assert.Equal(4, x[10].Int64Operand);
        Assert.Equal(new[] { 52 }, x[11].BranchTargets);
        Assert.Equal(13, x[12].Size);
        Assert.Equal(new[] { 65, 70 }, x[12].BranchTargets);
        Assert.Equal(new[] { 70 }, x[13].BranchTargets);
        Assert.Equal(il.Count, x.Sum(i => i.Size));
    }

    [Fact]
    public void Decode_ConvU2AndLdargaThenCalli_StaysInStep()
    {
        // The shapes a hand-sized decoder got wrong: conv.u2 has no operand and ldarga takes two
        // bytes. Any mis-size here lands the calli on the wrong byte and loses its token.
        byte[] il =
        {
            0x0F, 0x01,                     // 0  ldarga.s 1
            0xFE, 0x0A, 0x02, 0x00,         // 2  ldarga 2
            0xD1,                           // 6  conv.u2
            0x29, 0x07, 0x00, 0x00, 0x11,   // 7  calli 0x11000007
            0x2A,                           // 12 ret
        };
        var result = IlDecoder.Decode(il);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(new[] { ILOpCode.Ldarga_s, ILOpCode.Ldarga, ILOpCode.Conv_u2, ILOpCode.Calli, ILOpCode.Ret },
            result.Instructions.Select(i => i.OpCode));
        Assert.Equal(1, result.Instructions[0].Int64Operand);
        Assert.Equal(2, result.Instructions[1].Int64Operand);
        Assert.Equal(7, result.Instructions[3].Offset);
        Assert.Equal(0x11000007, result.Instructions[3].Token);
    }

    [Fact]
    public void Decode_NoPrefix_TakesItsFlagByte()
    {
        byte[] il = { 0xFE, 0x19, 0x01, 0x14, 0x7A }; // no. typecheck; ldnull; throw
        var result = IlDecoder.Decode(il);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(new[] { 0, 3, 4 }, result.Instructions.Select(i => i.Offset));
        Assert.Equal(1, result.Instructions[0].Int64Operand);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0xA6 }, 1, "undefined opcode 0xA6")]
    [InlineData(new byte[] { 0xFE, 0x23 }, 0, "undefined opcode 0xFE 0x23")]
    [InlineData(new byte[] { 0x00, 0xFE }, 1, "truncated two-byte opcode")]
    [InlineData(new byte[] { 0x20, 0x01, 0x02 }, 0, "operand needs 4 byte(s), 2 remain")]
    [InlineData(new byte[] { 0xFE, 0x0A, 0x01 }, 0, "operand needs 2 byte(s), 1 remain")]
    [InlineData(new byte[] { 0x45, 0xE8, 0x03, 0x00, 0x00, 0x2A }, 0, "do not fit")]
    // br.s +1 lands inside ldc.i4.s's operand.
    [InlineData(new byte[] { 0x2B, 0x01, 0x1F, 0x05, 0x2A }, 0, "is not an instruction start")]
    // br.s past the end of the body.
    [InlineData(new byte[] { 0x2B, 0x10, 0x2A }, 0, "is not an instruction start")]
    // switch target inside the following ldc.i4.
    [InlineData(new byte[] { 0x45, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x20, 0, 0, 0, 0, 0x2A }, 0, "is not an instruction start")]
    public void Decode_MalformedBody_IsAnErrorAtTheOffendingInstruction(byte[] il, int offset, string message)
    {
        var result = IlDecoder.Decode(il);
        Assert.False(result.Succeeded);
        Assert.Equal(offset, result.Error!.Value.Offset);
        Assert.Contains(message, result.Error.Value.Message);
    }

    [Fact]
    public void Decode_ExceptionRegionOffInstructionBoundary_IsAnError()
    {
        byte[] il = { 0x20, 0, 0, 0, 0, 0x26, 0x2A }; // ldc.i4 0; pop; ret
        Assert.True(IlDecoder.Decode(il, new[] { (0, 5), (5, 2) }).Succeeded);
        var result = IlDecoder.Decode(il, new[] { (0, 3) });
        Assert.False(result.Succeeded);
        Assert.Contains("exception region", result.Error!.Value.Message);
    }

    /// <summary>
    /// Every method body of every platform assembly, the Swift runtime and the generator decodes
    /// cleanly: a real corpus with every opcode the compilers emit, exception regions included.
    /// </summary>
    [Fact]
    public void Decode_EveryBodyOfThePlatformAndRuntimeAssemblies_HasNoErrors()
    {
        var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
        var ours = new[] { "Swift.Runtime.dll", "Swift.Bindings.dll" }
            .Select(n => Path.Combine(AppContext.BaseDirectory, n));
        foreach (var path in ours)
            Assert.True(File.Exists(path), path);

        var failures = new List<string>();
        long bodies = 0, instructions = 0;
        var opcodes = new HashSet<ILOpCode>();
        var sawCoreLib = false;
        foreach (var path in tpa.Concat(ours).Distinct(StringComparer.Ordinal))
        {
            using var pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata)
                continue;
            var md = pe.GetMetadataReader();
            sawCoreLib |= Path.GetFileName(path) == "System.Private.CoreLib.dll";
            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0)
                    continue;
                var result = IlDecoder.Decode(pe.GetMethodBody(method.RelativeVirtualAddress));
                bodies++;
                if (!result.Succeeded)
                {
                    failures.Add($"{Path.GetFileName(path)} {md.GetString(method.Name)}: {result.Error}");
                    continue;
                }
                instructions += result.Instructions.Length;
                foreach (var instruction in result.Instructions)
                    opcodes.Add(instruction.OpCode);
            }
        }

        Assert.Empty(failures);
        Assert.True(sawCoreLib, "System.Private.CoreLib was not among the trusted platform assemblies");
        Assert.True(bodies > 100_000, $"only {bodies} bodies decoded");
        Assert.True(instructions > 1_000_000, $"only {instructions} instructions decoded");
        // Non-vacuous: the corpus exercises the opcodes the gate depends on and the ones that bit.
        // (Long-form ldarga needs an argument index above 255, which no compiler here emits; the
        // synthetic bodies above cover it.)
        var missing = new[] { ILOpCode.Calli, ILOpCode.Switch, ILOpCode.Ldarga_s,
                ILOpCode.Conv_u2, ILOpCode.Ldc_i8, ILOpCode.Ldc_r8, ILOpCode.Unaligned }
            .Where(o => !opcodes.Contains(o)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void OperandSize_UnknownClass_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => IlOpcodeTable.OperandSize((OperandType)99));
    }
}
