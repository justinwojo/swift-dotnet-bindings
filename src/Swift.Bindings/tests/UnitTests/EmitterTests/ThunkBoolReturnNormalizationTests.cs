// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// swiftcc returns a Swift <c>Bool</c> as an LLVM <c>i1</c>: only bit 0 of the return register is
/// defined, and Swift's own callers mask it (<c>and w0, w0, #0x1</c> on arm64) before use. The
/// generated P/Invoke reads the thunk's return as a C <c>bool</c> byte, where every nonzero value is
/// true, so a callee whose codegen leaves bits 1-7 set (an unoptimised <c>!(a &lt; b)</c> on x86_64
/// compiles to <c>setl %al; xorb $-1, %al</c>, giving 0xfe for false) reads back inverted. The
/// thunk is the cdecl boundary, so it owns the normalisation: every register that carries a lone
/// Bool is masked to bit 0 after the call, on both architectures.
///
/// <para>A Bool that shares an eightbyte with other fields is not affected: swiftcc coalesces that
/// eightbyte into one integer through memory, where a Bool is always a clean 0/1 byte.</para>
/// </summary>
public class ThunkBoolReturnNormalizationTests
{
    // ---- Lowering marks exactly the registers that carry an i1 ----

    [Fact]
    public void ScalarBool_LowersToABoolSlot()
    {
        var lowering = TypeLowering.LowerReturnType(new NamedTypeSpec("Swift.Bool"), CreateTypeDatabase());

        Assert.NotNull(lowering);
        var slot = Assert.Single(lowering!.Slots);
        Assert.True(slot.IsBool);
    }

    [Theory]
    [InlineData("Swift.UInt8")]
    [InlineData("Swift.Int8")]
    [InlineData("Swift.Int32")]
    [InlineData("Swift.Int")]
    public void IntegerScalars_AreNotBoolSlots(string type)
    {
        var lowering = TypeLowering.LowerReturnType(new NamedTypeSpec(type), CreateTypeDatabase());

        Assert.NotNull(lowering);
        Assert.DoesNotContain(lowering!.Slots, s => s.IsBool);
    }

    [Theory]
    [InlineData("i8,b1", 16, new[] { false, true })]   // {Int, Bool}: swiftcc returns { i64, i1 }
    [InlineData("b1,i8", 16, new[] { true, false })]   // {Bool, Int}: swiftcc returns { i1, i64 }
    [InlineData("b1", 1, new[] { true })]              // {Bool}: swiftcc returns i1
    [InlineData("i4,b1", 8, new[] { false })]          // {Int32, Bool}: one coalesced i64
    [InlineData("b1,b1", 2, new[] { false })]          // {Bool, Bool}: one coalesced i16
    public void FrozenStruct_MarksOnlyAnEightbyteHoldingALoneBool(string layout, int size, bool[] expected)
    {
        var db = CreateTypeDatabase(("TestModule.S", layout, size));

        var lowering = TypeLowering.LowerReturnType(new NamedTypeSpec("TestModule.S"), db);

        Assert.NotNull(lowering);
        Assert.Equal(expected, lowering!.Slots.Select(s => s.IsBool).ToArray());
    }

    // ---- x86_64 ----

    [Fact]
    public void X64_FreeFunctionBoolReturn_FramesTheCallAndMasksEax()
    {
        var asm = Emit(Free(BoolReturn()), ThunkTargetArch.X86_64);

        Assert.DoesNotContain("jmp", asm);
        AssertAfterCall(asm, "callq   _$sTarget", "andl    $1, %eax");
        Assert.Contains("retq", asm);
    }

    [Fact]
    public void X64_InstanceBoolReturn_MasksEaxAfterTheCall()
    {
        var asm = Emit(Instance(BoolReturn()), ThunkTargetArch.X86_64);

        AssertAfterCall(asm, "callq   _$sTarget", "andl    $1, %eax");
    }

    [Fact]
    public void X64_StructReturnWithALoneBoolEightbyte_MasksOnlyThatRegister()
    {
        var ret = new TypeLoweringResult(
            new[] { new RegisterSlot(RegisterFile.Integer, 0, 8), new RegisterSlot(RegisterFile.Integer, 1, 8, IsBool: true) },
            IsIndirect: false, TotalByteSize: 16);

        var asm = Emit(Free(ret), ThunkTargetArch.X86_64);

        AssertAfterCall(asm, "callq   _$sTarget", "andl    $1, %edx");
        Assert.DoesNotContain("andl    $1, %eax", asm);
    }

    [Fact]
    public void X64_BridgedStructReturn_MasksTheBoolBeforeStoringIt()
    {
        var ret = new TypeLoweringResult(
            new[]
            {
                new RegisterSlot(RegisterFile.Integer, 0, 8),
                new RegisterSlot(RegisterFile.Integer, 1, 8),
                new RegisterSlot(RegisterFile.Integer, 2, 8, IsBool: true),
            },
            IsIndirect: false, TotalByteSize: 24);

        var asm = Emit(Free(ret), ThunkTargetArch.X86_64);

        int mask = asm.IndexOf("andl    $1, %ecx", StringComparison.Ordinal);
        int store = asm.IndexOf("movq    %rcx, 16(%rbx)", StringComparison.Ordinal);
        Assert.True(mask > 0, asm);
        Assert.True(store > mask, asm);
    }

    [Fact]
    public void X64_IndirectReturn_HasNoRegisterToMask()
    {
        var ret = new TypeLoweringResult(
            new[] { new RegisterSlot(RegisterFile.Integer, 0, 8, IsBool: true) },
            IsIndirect: true, TotalByteSize: 40);

        var asm = Emit(Free(ret), ThunkTargetArch.X86_64);

        Assert.DoesNotContain("andl", asm);
    }

    // ---- arm64 ----

    [Fact]
    public void Arm64_FreeFunctionBoolReturn_FramesTheCallAndMasksW0()
    {
        var asm = Emit(Free(BoolReturn()), ThunkTargetArch.Arm64);

        Assert.DoesNotContain("b       _$sTarget", asm);
        AssertAfterCall(asm, "bl      _$sTarget", "and     w0, w0, #0x1");
        Assert.Contains("ret", asm);
    }

    [Fact]
    public void Arm64_InstanceBoolReturn_MasksW0AfterTheCall()
    {
        var asm = Emit(Instance(BoolReturn()), ThunkTargetArch.Arm64);

        AssertAfterCall(asm, "bl      _$sTarget", "and     w0, w0, #0x1");
    }

    [Fact]
    public void Arm64_StructReturnWithALoneBoolEightbyte_MasksOnlyThatRegister()
    {
        var ret = new TypeLoweringResult(
            new[] { new RegisterSlot(RegisterFile.Integer, 0, 8, IsBool: true), new RegisterSlot(RegisterFile.Integer, 1, 8) },
            IsIndirect: false, TotalByteSize: 16);

        var asm = Emit(Free(ret), ThunkTargetArch.Arm64);

        AssertAfterCall(asm, "bl      _$sTarget", "and     w0, w0, #0x1");
        Assert.DoesNotContain("w1, w1", asm);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonBoolReturn_KeepsTheTailCall(bool x64)
    {
        var ret = new TypeLoweringResult(new[] { new RegisterSlot(RegisterFile.Integer, 0, 1) }, IsIndirect: false, TotalByteSize: 1);

        var asm = Emit(Free(ret), x64 ? ThunkTargetArch.X86_64 : ThunkTargetArch.Arm64);

        Assert.Contains(x64 ? "jmp     _$sTarget" : "b       _$sTarget", asm);
        Assert.DoesNotContain("and", asm.Replace(".globl", string.Empty));
    }

    // ---- The generator wires a Bool-returning method into both slices ----

    [Fact]
    public void BoolReturningFreeFunction_EmitsAMaskingThunkOnBothArchitectures()
    {
        var db = CreateTypeDatabase();
        db.AsyncLibraryName = "TestModuleSwiftBindings";
        var module = CreateModuleDecl("TestModule");
        var method = new MethodDecl
        {
            Name = "atLeast",
            MangledName = "$s10TestModule7atLeastySbs5Int32V_ADtF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                Arg(string.Empty, "Swift.Bool", module),
                Arg("x", "Swift.Int32", module),
                Arg("y", "Swift.Int32", module),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = module,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        module.Methods.Add(method);
        var env = new MethodEnvironment(method, db);
        var arm64 = new StringBuilder();
        var x64 = new StringBuilder();

        Assert.True(NativeThunkEmitter.EmitThunk(env, "TestModule", arm64, x64AsmBuilder: x64));

        Assert.Contains("and     w0, w0, #0x1", arm64.ToString());
        Assert.Contains("andl    $1, %eax", x64.ToString());
    }

    // ---- Harness ----

    private static void AssertAfterCall(string asm, string call, string mask)
    {
        int callAt = asm.IndexOf(call, StringComparison.Ordinal);
        int maskAt = asm.IndexOf(mask, StringComparison.Ordinal);
        Assert.True(callAt >= 0, $"missing '{call}' in:\n{asm}");
        Assert.True(maskAt > callAt, $"'{mask}' must follow '{call}' in:\n{asm}");
    }

    private static string Emit(ThunkDescriptor descriptor, ThunkTargetArch target) =>
        ThunkAssemblyEmitter.EmitThunk(descriptor, target);

    private static TypeLoweringResult BoolReturn() => new(
        new[] { new RegisterSlot(RegisterFile.Integer, 0, 1, IsBool: true) },
        IsIndirect: false, TotalByteSize: 1);

    private static ThunkDescriptor Free(TypeLoweringResult ret) => new(
        ThunkSymbol: "thunk_t_bool",
        SwiftSymbol: "_$sTarget",
        ReturnLowering: ret,
        SelfLowering: null,
        ParameterCount: 2,
        FloatParameterCount: 0,
        IsInstanceMethod: false,
        IsStaticMethod: false,
        IsConstructor: false,
        Throws: false,
        MetadataAccessorSymbol: null);

    private static ThunkDescriptor Instance(TypeLoweringResult ret) => Free(ret) with
    {
        SelfLowering = new TypeLoweringResult(new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) }, IsIndirect: false, TotalByteSize: 8),
        IsInstanceMethod = true,
    };

    private static ArgumentDecl Arg(string name, string type, ModuleDecl module) => new()
    {
        Name = name,
        PrivateName = name,
        SwiftTypeSpec = new NamedTypeSpec(type),
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = module,
    };

    private static ModuleDecl CreateModuleDecl(string name) => new()
    {
        Name = name,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static TypeDatabase CreateTypeDatabase(params (string Name, string Layout, int Size)[] structs)
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        foreach (var (name, layout, size) in structs)
        {
            var simple = name.Substring(name.LastIndexOf('.') + 1);
            module.RegisterType(
                SwiftTypeName.FromModuleQualifiedName(name),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", simple),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(name),
                    MetadataAccessor = $"$s10TestModule{simple.Length}{simple}VMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                    AbiFieldLayout = layout,
                    InlineSize = size,
                });
        }
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
    }
}
