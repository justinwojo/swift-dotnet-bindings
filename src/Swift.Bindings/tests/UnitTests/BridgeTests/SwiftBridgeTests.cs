// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;
using static BindingsGeneration.Tests.SwiftCallPlanTests;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="SwiftBridgeEmitter"/> and <see cref="SwiftBridgeValidator"/>: every bridge shape the
/// emitter has a template for assembles on arm64 and x86_64, leaves only weak references undefined,
/// and builds exactly the frame its plan and its unwind information describe; and the validator goes
/// red on the frames whose unwind information lies (a CFA the code does not have, saved registers in
/// the wrong order, no unwind information, a DWARF entry without its FDE, a strong reference).
/// </summary>
public sealed class SwiftBridgeTests : IDisposable
{
    private static readonly SwiftArchitecture[] Architectures = [SwiftArchitecture.Arm64, SwiftArchitecture.X86_64];

    private static readonly Lazy<SwiftBridgeToolchain?> Toolchain = new(SwiftBridgeToolchain.Locate);

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "swift-bridge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Scratch(string name)
    {
        var path = Path.Combine(_scratch, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>One plan of each shape the emitter has a template for.</summary>
    public static IEnumerable<(string Name, SwiftCallPlan Plan)> Shapes()
    {
        var wide = With(("M.Wide", SwiftLoweringAnswer.IndirectBecause("five pieces")));
        yield return ("register-only", Planned(Planner().PlanForward(Free("Swift.Int", "Swift.Int", "Swift.Double"), Signature("long", "long", "double"), "$s1M1fyS2i_SdtF")));
        yield return ("float-result", Planned(Planner().PlanForward(Free("Swift.Double", "Swift.Double"), Signature("double", "double"), "$s1M1gyS2dF")));
        yield return ("stack-prefix", Planned(Planner().PlanForward(
            Free("Swift.Int", Enumerable.Repeat("Swift.Int", 11).ToArray()), Signature("long", Enumerable.Repeat("long", 11).ToArray()), "$s1M1hyS2iF")));
        yield return ("self", Planned(Planner().PlanForward(ClassMethod("Swift.Int", "Swift.Int"), Signature("long", "long", "SwiftSelf"), "$s1M1CC1fyS2iF")));
        yield return ("direct-result", Planned(Planner().PlanForward(Free("Swift.String"), Signature("Swift.SwiftString.Buffer"), "$s1M1fSSyF")));
        yield return ("indirect-result", Planned(Planner(lowering: wide).PlanForward(Free("M.Wide", "Swift.Int"), Signature("void", "SwiftIndirectResult", "long"), "$s1M4wideAA4WideVSiF")));
        yield return ("throwing", Planned(Planner().PlanForward(Throwing(Free("Swift.Int", "Swift.Int")), Signature("long", "long", "SwiftError*"), "$s1M1tyS2iKF")));
        yield return ("throwing-self-direct", Planned(Planner().PlanForward(
            Throwing(ClassMethod("Swift.String", "Swift.Int")), Signature("Swift.SwiftString.Buffer", "long", "SwiftSelf", "SwiftError*"), "$s1M1CC1tySSSiKF")));
        yield return ("throwing-stack-prefix", Planned(Planner().PlanForward(
            Throwing(Free(null, Enumerable.Repeat("Swift.Int", 9).ToArray())), Signature("void", Enumerable.Repeat("long", 9).Append("SwiftError*").ToArray()), "$s1M1uyySiKF")));
        yield return ("sign-extended", Planned(Planner().PlanForward(Free("Swift.Int8"), Signature("sbyte"), "$s1M1bs4Int8VyF")));
        yield return ("zero-extended", Planned(Planner().PlanForward(Free("Swift.UInt16"), Signature("ushort"), "$s1M1cs6UInt16VyF")));
        yield return ("thick", Planned(Planner().PlanForward(Closure("Swift.Int", "Swift.Int"), Signature(SwiftManagedMarshalling.None, "long", "long", "SwiftSelf"), null)));
        yield return ("thick-throwing", Planned(Planner().PlanForward(Throwing(Closure("Swift.Int", "Swift.Int")),
            Signature(SwiftManagedMarshalling.None, "long", "long", "SwiftSelf", "SwiftError*"), null)));
    }

    public static IEnumerable<object[]> ShapeNames() => Shapes().Select(s => new object[] { s.Name });

    private static SwiftCallPlan Shape(string name) => Shapes().Single(s => s.Name == name).Plan;

    // ---- the emitter ----

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void EveryShape_HasATemplate_OnBothArchitectures(string name)
    {
        var plan = Shape(name);
        foreach (var architecture in Architectures)
        {
            var assembly = SwiftBridgeEmitter.Emit([plan], architecture);
            Assert.Empty(assembly.Refused);
            Assert.Equal(plan.Symbol, Assert.Single(assembly.Bridges));
        }
    }

    [Fact]
    public void Emit_IsDeterministic_SortedAndDeduplicated()
    {
        var plans = Shapes().Select(s => s.Plan).ToList();
        foreach (var architecture in Architectures)
        {
            var forward = SwiftBridgeEmitter.Emit(plans, architecture);
            var backward = SwiftBridgeEmitter.Emit(Enumerable.Reverse(plans).Concat(plans), architecture);
            Assert.Equal(forward.Source, backward.Source);
            Assert.Equal(forward.Bridges.Order(StringComparer.Ordinal), forward.Bridges);
            Assert.Equal(plans.Select(p => p.Symbol).Distinct().Count(), forward.Bridges.Length);
        }
    }

    [Fact]
    public void EveryTarget_IsAWeakReference_AndAThickBridgeNamesNone()
    {
        var plans = Shapes().Select(s => s.Plan).ToList();
        var source = SwiftBridgeEmitter.Emit(plans, SwiftArchitecture.Arm64).Source;
        foreach (var target in plans.Select(p => p.Body.Target).OfType<string>())
            Assert.Contains($".weak_reference _{target}\n", source);
        Assert.Equal(plans.Select(p => p.Body.Target).OfType<string>().Distinct().Count(),
            source.Split('\n').Count(l => l.StartsWith(".weak_reference ", StringComparison.Ordinal)));
        Assert.Contains("blr x16", source);
        Assert.Contains("callq *%r11", SwiftBridgeEmitter.Emit(plans, SwiftArchitecture.X86_64).Source);
    }

    [Fact]
    public void Reverse_HasNoTemplate()
    {
        var plan = Planned(Planner().PlanReverse(Closure("Swift.Int", "Swift.Int"), Signature(SwiftManagedMarshalling.None, "long", "long", "SwiftSelf")));

        var refusal = Assert.Single(SwiftBridgeEmitter.Emit([plan], SwiftArchitecture.Arm64).Refused);
        Assert.Contains("reverse", refusal.Reason);
    }

    [Fact]
    public void AFrameTheTemplateDoesNotHave_IsRefused()
    {
        var plan = Shape("self");
        foreach (var (mutate, expected) in new (Func<SwiftFrame, SwiftFrame>, string)[]
        {
            (f => f with { SavedRegisters = f.SavedRegisters.Add("x9").Add("x10") }, "not callee-saved"),
            (f => f with { SavedRegisters = f.SavedRegisters.Add("x23") }, "pairs"),
            (f => f with { SavedRegisters = f.SavedRegisters.RemoveAt(0) }, "frame record"),
            (f => f with { LocalBytes = 8 }, "local bytes"),
        })
        {
            var native = On(plan, SwiftArchitecture.Arm64);
            var changed = plan with
            {
                Body = plan.Body with { Architectures = plan.Body.Architectures.Replace(native, native with { Frame = mutate(native.Frame) }) },
            };
            Assert.Contains(expected, SwiftBridgeEmitter.Refusal(changed, SwiftArchitecture.Arm64));
        }
    }

    // ---- the validator on emitted bridges ----

    [SkippableTheory]
    [MemberData(nameof(ShapeNames))]
    public void EveryShape_Assembles_AndItsFrameMatchesItsPlanAndItsUnwindInformation(string name)
    {
        var toolchain = Toolchain.Value;
        Skip.If(toolchain is null, "xcrun with the iOS SDKs is required");
        var plan = Shape(name);
        foreach (var architecture in Architectures)
        {
            var assembly = SwiftBridgeEmitter.Emit([plan], architecture);
            var validation = SwiftBridgeValidator.Validate(assembly, [plan], toolchain!, Scratch(name));
            Assert.True(validation.IsGreen, validation.ToString() + "\n" + assembly.Source);
        }
    }

    [SkippableFact]
    public void AllShapesInOneAssembly_AreGreen()
    {
        var toolchain = Toolchain.Value;
        Skip.If(toolchain is null, "xcrun with the iOS SDKs is required");
        var plans = Shapes().Select(s => s.Plan).ToList();
        foreach (var architecture in Architectures)
        {
            var assembly = SwiftBridgeEmitter.Emit(plans, architecture);
            var validation = SwiftBridgeValidator.Validate(assembly, plans, toolchain!, Scratch("all"));
            Assert.True(validation.IsGreen, validation.ToString());
            Assert.Equal(plans.Count, validation.Functions.Length);
        }
    }

    [SkippableFact]
    public void APlanWhoseFrameDiffersFromTheCode_IsRed()
    {
        var toolchain = Toolchain.Value;
        Skip.If(toolchain is null, "xcrun with the iOS SDKs is required");
        var plan = Shape("throwing");
        var assembly = SwiftBridgeEmitter.Emit([plan], SwiftArchitecture.Arm64);
        var native = On(plan, SwiftArchitecture.Arm64);
        var claimed = native.Frame with { SavedRegisters = native.Frame.SavedRegisters.Add("x24").Add("x23") };

        var validation = SwiftBridgeValidator.ValidateSource(assembly.Source, SwiftArchitecture.Arm64,
            new Dictionary<string, SwiftFrame> { ["_" + plan.Symbol] = claimed }, toolchain!, Scratch("plan"), ["_" + plan.Symbol]);

        Assert.False(validation.IsGreen);
        Assert.Contains(validation.Failures, f => f.Check == "plan");
    }

    // ---- the validator on seeded frames ----

    private SwiftBridgeValidation Seed(SwiftArchitecture architecture, string body, string name)
    {
        var toolchain = Toolchain.Value!;
        var source = ".section __TEXT,__text,regular,pure_instructions\n.weak_reference _target\n" + body + ".subsections_via_symbols\n";
        return SwiftBridgeValidator.ValidateSource(source, architecture, new Dictionary<string, SwiftFrame>(), toolchain, Scratch(name), ["_seed"]);
    }

    private const string Arm64Good = """
        .globl _seed
        .p2align 2
        _seed:
          .cfi_startproc
          stp x29, x30, [sp, #-16]!
          mov x29, sp
          .cfi_def_cfa w29, 16
          .cfi_offset w30, -8
          .cfi_offset w29, -16
          stp x20, x19, [sp, #-16]!
          .cfi_offset w19, -24
          .cfi_offset w20, -32
          mov x20, x0
          bl _target
          ldp x20, x19, [sp], #16
          ldp x29, x30, [sp], #16
          ret
          .cfi_endproc

        """;

    [SkippableFact]
    public void TheStandardFrame_IsGreen()
    {
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var validation = Seed(SwiftArchitecture.Arm64, Arm64Good, "good");
        Assert.True(validation.IsGreen, validation.ToString());
    }

    [SkippableFact]
    public void AFrameWhoseCfaIsTheFramePointerPlus32_IsRed()
    {
        // The frame record sits 32 bytes below the CFA. With CFI that says so, the assembler defers to
        // DWARF and the row agrees with the code, but the frame breaks the bridges' frame-record rule;
        // with CFI that claims the standard frame, the compact encoding restores from the wrong slots.
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var honest = Seed(SwiftArchitecture.Arm64, """
            .globl _seed
            .p2align 2
            _seed:
              .cfi_startproc
              stp x20, x19, [sp, #-16]!
              stp x29, x30, [sp, #-16]!
              mov x29, sp
              .cfi_def_cfa w29, 32
              .cfi_offset w30, -24
              .cfi_offset w29, -32
              .cfi_offset w19, -8
              .cfi_offset w20, -16
              mov x20, x0
              bl _target
              ldp x29, x30, [sp], #16
              ldp x20, x19, [sp], #16
              ret
              .cfi_endproc

            """, "cfa32");
        Assert.False(honest.IsGreen);
        Assert.Contains(honest.Failures, f => f.Check == "frame" && f.Detail.Contains("CFA-32"));

        var lying = Seed(SwiftArchitecture.Arm64, """
            .globl _seed
            .p2align 2
            _seed:
              .cfi_startproc
              stp x20, x19, [sp, #-16]!
              stp x29, x30, [sp, #-16]!
              mov x29, sp
              .cfi_def_cfa w29, 16
              .cfi_offset w30, -8
              .cfi_offset w29, -16
              .cfi_offset w19, -24
              .cfi_offset w20, -32
              mov x20, x0
              bl _target
              ldp x29, x30, [sp], #16
              ldp x20, x19, [sp], #16
              ret
              .cfi_endproc

            """, "cfa32lie");
        Assert.False(lying.IsGreen);
        Assert.Contains(lying.Failures, f => f.Check == "unwind" && f.Detail.Contains("compact encoding"));
    }

    [SkippableFact]
    public void AFunctionWithoutUnwindInformation_IsRed()
    {
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var validation = Seed(SwiftArchitecture.Arm64, """
            .globl _seed
            .p2align 2
            _seed:
              stp x29, x30, [sp, #-16]!
              mov x29, sp
              bl _target
              ldp x29, x30, [sp], #16
              ret

            """, "nocfi");

        Assert.False(validation.IsGreen);
        Assert.Contains(validation.Failures, f => f.Check == "unwind");
    }

    [SkippableFact]
    public void SavedRegistersListedInTheWrongOrder_AreRed()
    {
        // x86_64 compact unwind reads the saved registers upward from the lowest; CFI that names them
        // from the highest encodes the registers swapped.
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        const string Head = """
            .globl _seed
            .p2align 4, 0x90
            _seed:
              .cfi_startproc
              pushq %rbp
              .cfi_def_cfa_offset 16
              .cfi_offset %rbp, -16
              movq %rsp, %rbp
              .cfi_def_cfa_register %rbp
              pushq %rbx
              pushq %r12
              pushq %r13

            """;
        const string Tail = """
              subq $8, %rsp
              xorl %r12d, %r12d
              callq _target
              leaq -24(%rbp), %rsp
              popq %r13
              popq %r12
              popq %rbx
              popq %rbp
              retq
              .cfi_endproc

            """;
        var right = Seed(SwiftArchitecture.X86_64, Head + "  .cfi_offset %r13, -40\n  .cfi_offset %r12, -32\n  .cfi_offset %rbx, -24\n" + Tail, "x86right");
        Assert.True(right.IsGreen, right.ToString());

        var wrong = Seed(SwiftArchitecture.X86_64, Head + "  .cfi_offset %rbx, -24\n  .cfi_offset %r12, -32\n  .cfi_offset %r13, -40\n" + Tail, "x86wrong");
        Assert.False(wrong.IsGreen);
        Assert.Contains(wrong.Failures, f => f.Check == "unwind");
    }

    [SkippableFact]
    public void AFrameTheCompactFormatCannotEncode_IsCheckedAgainstItsDwarfRow_AndRedWithoutItsFde()
    {
        // A single saved register is not a pair: the assembler defers to DWARF.
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var toolchain = Toolchain.Value!;
        const string Body = """
            .globl _seed
            .p2align 2
            _seed:
              .cfi_startproc
              stp x29, x30, [sp, #-16]!
              mov x29, sp
              .cfi_def_cfa w29, 16
              .cfi_offset w30, -8
              .cfi_offset w29, -16
              str x19, [sp, #-16]!
              .cfi_offset w19, -32
              mov x19, x0
              bl _target
              ldr x19, [sp], #16
              ldp x29, x30, [sp], #16
              ret
              .cfi_endproc

            """;
        var validation = Seed(SwiftArchitecture.Arm64, Body, "dwarf");
        Assert.True(validation.IsGreen, validation.ToString());

        // The same function and encoding, with the FDE gone from __eh_frame.
        var instructions = new List<SwiftInstruction>
        {
            new(0x4000, "stp", ["x29", "x30", "[sp, #-0x10]!"]),
            new(0x4004, "mov", ["x29", "sp"]),
            new(0x4008, "str", ["x19", "[sp, #-0x10]!"]),
            new(0x400c, "mov", ["x19", "x0"]),
            new(0x4010, "bl", ["0x5000"]),
            new(0x4014, "ldr", ["x19", "[sp]", "#0x10"]),
            new(0x4018, "ldp", ["x29", "x30", "[sp]", "#0x10"]),
            new(0x401c, "ret", []),
        };
        var row = new SwiftCfaRow(0x400c, "x29", 16,
            ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, int> { ["x29"] = -16, ["x30"] = -8, ["x19"] = -32 }));
        var fde = new SwiftFde(0x18, 0x4000, 0x4020, [row]);
        Assert.Empty(SwiftBridgeValidator.Check("_seed", SwiftArchitecture.Arm64, 0x4000, instructions, 0x03000018, [fde], null));
        var missing = SwiftBridgeValidator.Check("_seed", SwiftArchitecture.Arm64, 0x4000, instructions, 0x03000018, [], null);
        Assert.Contains(missing, f => f.Check == "unwind" && f.Detail.Contains("does not have"));
        var elsewhere = SwiftBridgeValidator.Check("_seed", SwiftArchitecture.Arm64, 0x4000, instructions, 0x03000018, [fde with { Start = 0x5000, End = 0x5020 }], null);
        Assert.Contains(elsewhere, f => f.Check == "unwind" && f.Detail.Contains("covers"));
    }

    [SkippableFact]
    public void AStrongUndefinedReference_IsRed()
    {
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var validation = SwiftBridgeValidator.ValidateSource(
            ".section __TEXT,__text,regular,pure_instructions\n" + Arm64Good + ".subsections_via_symbols\n",
            SwiftArchitecture.Arm64, new Dictionary<string, SwiftFrame>(), Toolchain.Value!, Scratch("strong"), ["_seed"]);

        Assert.Contains(validation.Failures, f => f.Check == "R6a" && f.Detail.Contains("_target"));
    }

    [SkippableFact]
    public void ACalleeSavedRegisterWrittenWithoutASave_IsRed()
    {
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var validation = Seed(SwiftArchitecture.Arm64, Arm64Good.Replace("  mov x20, x0\n", "  mov x20, x0\n  mov x23, x0\n"), "clobber");

        Assert.Contains(validation.Failures, f => f.Check == "callee-saved" && f.Detail.Contains("x23"));
    }

    /// <summary>A bridge's arm64 frame: x19 and x20 saved in a pair below the frame record, x20 written, one call.</summary>
    private static List<SwiftInstruction> Arm64Frame() =>
    [
        new(0x4000, "stp", ["x29", "x30", "[sp, #-0x20]!"]),
        new(0x4004, "mov", ["x29", "sp"]),
        new(0x4008, "stp", ["x20", "x19", "[sp, #0x10]"]),
        new(0x400c, "mov", ["x20", "x0"]),
        new(0x4010, "bl", ["0x5000"]),
        new(0x4014, "ldp", ["x20", "x19", "[sp, #0x10]"]),
        new(0x4018, "ldp", ["x29", "x30", "[sp]", "#0x20"]),
        new(0x401c, "ret", []),
    ];

    [Theory]
    [InlineData("before its save", 0x4008, "mov", "x20, #1", "x20 is written")]
    [InlineData("after its restore", 0x4018, "mov", "x19, #1", "x19 does not hold")]
    [InlineData("by a bitfield insert", 0x4010, "bfi", "x23, x0, #0, #8", "x23 is written")]
    [InlineData("by a bit clear", 0x4010, "bic", "x24, x24, x0", "x24 is written")]
    [InlineData("of the return address after its restore", 0x401c, "mov", "x30, x0", "x30 does not hold")]
    public void ACallersValueLost_IsRed_WhereverTheWriteFalls(string _, ulong before, string mnemonic, string operands, string expected)
    {
        Assert.Empty(SwiftBridgeValidator.Execute(SwiftArchitecture.Arm64, Arm64Frame()).Clobbers);

        var instructions = Arm64Frame();
        instructions.Insert(instructions.FindIndex(i => i.Address == before),
            new SwiftInstruction(before - 2, mnemonic, [.. operands.Split(", ")]));
        var execution = SwiftBridgeValidator.Execute(SwiftArchitecture.Arm64, instructions);

        Assert.Null(execution.Error);
        Assert.Contains(execution.Clobbers, c => c.Contains(expected));
    }

    [Fact]
    public void AnArm64StackPointerMisalignedBetweenAlignedPoints_IsRed()
    {
        // Two eight-byte steps realign sp before the call, but sp is misaligned in between.
        var instructions = Arm64Frame();
        int call = instructions.FindIndex(i => i.Mnemonic == "bl");
        instructions.InsertRange(call, [new(0x400e, "sub", ["sp", "sp", "#0x8"]), new(0x400f, "sub", ["sp", "sp", "#0x8"])]);
        instructions.Insert(call + 3, new SwiftInstruction(0x4012, "add", ["sp", "sp", "#0x10"]));

        Assert.Contains("not 16-aligned", SwiftBridgeValidator.Execute(SwiftArchitecture.Arm64, instructions).Error);
    }

    [Fact]
    public void AnX86CallersValueLost_IsRed_WhereverTheWriteFalls()
    {
        List<SwiftInstruction> Frame() =>
        [
            new(0x4000, "pushq", ["%rbp"]),
            new(0x4001, "movq", ["%rsp", "%rbp"]),
            new(0x4004, "pushq", ["%r13"]),
            new(0x4006, "pushq", ["%rax"]),
            new(0x4007, "movq", ["%rdi", "%r13"]),
            new(0x400a, "callq", ["0x5000"]),
            new(0x400f, "popq", ["%rcx"]),
            new(0x4010, "popq", ["%r13"]),
            new(0x4012, "popq", ["%rbp"]),
            new(0x4013, "retq", []),
        ];
        Assert.Empty(SwiftBridgeValidator.Execute(SwiftArchitecture.X86_64, Frame()).Clobbers);

        var early = Frame();
        early.Insert(2, new SwiftInstruction(0x4002, "movq", ["$1", "%r13"]));
        Assert.Contains(SwiftBridgeValidator.Execute(SwiftArchitecture.X86_64, early).Clobbers, c => c.Contains("r13 is written"));

        var late = Frame();
        late.Insert(9, new SwiftInstruction(0x4012, "movq", ["$1", "%r13"]));
        Assert.Contains(SwiftBridgeValidator.Execute(SwiftArchitecture.X86_64, late).Clobbers, c => c.Contains("r13 does not hold"));
    }

    // ---- the claim decoder ----

    [Theory]
    [InlineData(0x04000000u, "x29=-16 x30=-8")]
    [InlineData(0x04000001u, "x19=-24 x20=-32 x29=-16 x30=-8")]
    [InlineData(0x04000003u, "x19=-24 x20=-32 x21=-40 x22=-48 x29=-16 x30=-8")]
    [InlineData(0x04000100u, "d8=-24 d9=-32 x29=-16 x30=-8")]
    public void Arm64FrameEncodings_DecodeAsLibunwindReadsThem(uint encoding, string expected)
    {
        var claim = SwiftBridgeValidator.Claim(SwiftArchitecture.Arm64, 0, encoding, [], 0, out var error);
        Assert.Null(error);
        Assert.Equal("x29", claim!.CfaRegister);
        Assert.Equal(16, claim.CfaOffset);
        Assert.Equal(expected, string.Join(" ", claim.Saved.Select(s => $"{s.Key}={s.Value}")));
    }

    [Theory]
    [InlineData(0x01000000u, "rbp=-16")]
    [InlineData(0x01030054u, "r12=-32 r14=-40 rbp=-16 rbx=-24")]
    [InlineData(0x01010001u, "rbp=-16 rbx=-24")]
    public void X86FrameEncodings_DecodeAsLibunwindReadsThem(uint encoding, string expected)
    {
        var claim = SwiftBridgeValidator.Claim(SwiftArchitecture.X86_64, 0, encoding, [], 0, out var error);
        Assert.Null(error);
        Assert.Equal(expected, string.Join(" ", claim!.Saved.Select(s => $"{s.Key}={s.Value}")));
    }

    [Fact]
    public void NoEntry_AnEmptyEncoding_AndAFramelessMode_AreNoClaim()
    {
        Assert.Null(SwiftBridgeValidator.Claim(SwiftArchitecture.Arm64, 0, null, [], 0, out var none));
        Assert.Contains("no unwind entry", none);
        Assert.Null(SwiftBridgeValidator.Claim(SwiftArchitecture.Arm64, 0, 0, [], 0, out var empty));
        Assert.Contains("empty", empty);
        Assert.Null(SwiftBridgeValidator.Claim(SwiftArchitecture.Arm64, 0, 0x02000000, [], 0, out var frameless));
        Assert.Contains("not a frame-based", frameless);
        Assert.Null(SwiftBridgeValidator.Claim(SwiftArchitecture.Arm64, 0, 0x04000040, [], 0, out var undefined));
        Assert.Contains("does not define", undefined);
    }

    // ---- reading the tools' output ----

    [Fact]
    public void ToolOutput_IsRead()
    {
        Assert.Equal(new[] { ("_$s1M1fyyF", true), ("_strong", false) }, SwiftBridgeValidator.UndefinedSymbols("""
                             (undefined) weak external _$s1M1fyyF
                             (undefined) external _strong
            0000000000000000 (__TEXT,__text) external _SBSR_one
            """).ToArray());
        Assert.Equal(0x510ul, SwiftBridgeValidator.DefinedSymbols("0000000000000510 T _SBSR_one\n")["_SBSR_one"]);
        var entries = SwiftBridgeValidator.UnwindEntries(
            "      [0]: function offset=0x00004000, encoding[0]=0x04000002\n      [1]: function offset=0x00004040, encoding[1]=0x04000000\n");
        Assert.Equal(0x04000002u, entries[0x4000]);
        // A function the linker merged into the entry before it takes that entry's encoding.
        Assert.Equal(0x04000002u, SwiftBridgeValidator.EntryFor(entries, 0x4020));
        Assert.Equal(0x04000000u, SwiftBridgeValidator.EntryFor(entries, 0x4040));
        Assert.Null(SwiftBridgeValidator.EntryFor(entries, 0x3ff0));

        var fde = Assert.Single(SwiftBridgeValidator.Fdes("""
            00000000 00000010 ffffffff CIE
              Format:                DWARF32

            00000014 00000020 00000018 FDE cie=00000000 pc=00004000...00004020
              Format:       DWARF32
              0x4000: CFA=WSP
              0x4008: CFA=W29+32: W29=[CFA-32], W30=[CFA-24]

            """, SwiftArchitecture.Arm64));
        Assert.Equal((0x14ul, 0x4000ul, 0x4020ul), (fde.Offset, fde.Start, fde.End));
        Assert.Equal(("sp", 0), (fde.Rows[0].CfaRegister, fde.Rows[0].CfaOffset));
        Assert.Equal(("x29", 32, -32, -24), (fde.Rows[1].CfaRegister, fde.Rows[1].CfaOffset, fde.Rows[1].Saved["x29"], fde.Rows[1].Saved["x30"]));

        var code = SwiftBridgeValidator.Disassembly("""
            Disassembly of section __TEXT,__text:

            0000000000004000 <_SBSR_one>:
                4000:      	stp	x29, x30, [sp, #-0x10]!
                4004:      	bl	0x5000 <_target>
                4008:      	ldp	x29, x30, [sp], #0x10
            """);
        Assert.Equal(new[] { "x29", "x30", "[sp, #-0x10]!" }, code["_SBSR_one"][0].Operands);
        Assert.Equal(new[] { "0x5000" }, code["_SBSR_one"][1].Operands);
        Assert.Equal(new[] { "x29", "x30", "[sp]", "#0x10" }, code["_SBSR_one"][2].Operands);
    }
}
