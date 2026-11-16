// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The native thunks' frames and unwind information. Every thunk shape the emitter writes, on arm64
/// and x86_64, assembles and links into an image whose unwind table covers it and describes the
/// frame its code builds at entry, at each call and at exit, as <see cref="SwiftBridgeValidator"/>
/// checks it; and the validator goes red on the thunk frames the emitter used to write (no CFI, the
/// frame record 32 bytes below the CFA) and on a frame claim the code never builds.
/// </summary>
public sealed class ThunkFrameTests : IDisposable
{
    private static readonly Lazy<SwiftBridgeToolchain?> Toolchain = new(() => SwiftBridgeToolchain.Locate());

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "thunk-frame-tests", Guid.NewGuid().ToString("N"));

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

    private static TypeLoweringResult Ints(params int[] sizes) => new(
        sizes.Select((size, i) => new RegisterSlot(RegisterFile.Integer, i, size)).ToArray(), IsIndirect: false, TotalByteSize: sizes.Sum());

    private static ThunkDescriptor Thunk(string name, TypeLoweringResult? result = null, int parameters = 0, int floats = 0,
        bool instance = false, bool isStatic = false, bool constructor = false, bool throws = false, int zeroExtend = 0)
    {
        var mangled = $"$s4Seed{name.Length}{name}yyF";
        return new ThunkDescriptor(
            ThunkAssemblyEmitter.GenerateThunkSymbol("Seed", mangled), "_" + mangled,
            ReturnLowering: result, SelfLowering: null, ParameterCount: parameters, FloatParameterCount: floats,
            IsInstanceMethod: instance, IsStaticMethod: isStatic, IsConstructor: constructor, Throws: throws,
            MetadataAccessorSymbol: isStatic || constructor ? "_$s4Seed4TypeVMa" : null, ReturnZeroExtendFromBytes: zeroExtend);
    }

    /// <summary>One descriptor of each shape the emitter writes: every tail-call form and every full-frame bridge.</summary>
    public static IEnumerable<(string Shape, ThunkDescriptor Descriptor)> Shapes()
    {
        yield return ("tail-call", Thunk("tail", Ints(8), parameters: 2));
        yield return ("bool-tail-call", Thunk("flag", new TypeLoweringResult([new RegisterSlot(RegisterFile.Integer, 0, 1, IsBool: true)], false, 1), parameters: 1));
        yield return ("zero-extended-tail-call", Thunk("tag", Ints(1), parameters: 1, zeroExtend: 1));
        yield return ("indirect-tail-call", Thunk("wide", new TypeLoweringResult([], IsIndirect: true, TotalByteSize: 48), parameters: 2));
        yield return ("instance", Thunk("instance", Ints(8), parameters: 1, instance: true));
        yield return ("throwing-instance", Thunk("throwing", Ints(8), parameters: 1, instance: true, throws: true));
        yield return ("return-bridge", Thunk("triple", Ints(8, 8, 8), parameters: 1));
        yield return ("throwing-return-bridge", Thunk("quad", Ints(8, 8, 8, 8), parameters: 1, instance: true, throws: true));
        yield return ("static", Thunk("make", Ints(8), parameters: 1, floats: 1, isStatic: true));
        yield return ("static-indirect", Thunk("makeWide", new TypeLoweringResult([], IsIndirect: true, TotalByteSize: 48), parameters: 1, isStatic: true));
        yield return ("throwing-constructor", Thunk("init", Ints(8), parameters: 2, constructor: true, throws: true));
    }

    private static string Emit(ThunkTargetArch target, IEnumerable<ThunkDescriptor> descriptors)
    {
        var sb = new StringBuilder();
        target.EmitFileHeader(sb, "Seed");
        foreach (var descriptor in descriptors)
            sb.Append(ThunkAssemblyEmitter.EmitThunk(descriptor, target));
        sb.Append(target.EmitFileFooter());
        return sb.ToString();
    }

    [SkippableTheory]
    [InlineData(SwiftArchitecture.Arm64)]
    [InlineData(SwiftArchitecture.X86_64)]
    public void EveryThunkShape_LinksWithUnwindInformationThatDescribesItsFrame(SwiftArchitecture architecture)
    {
        var toolchain = Toolchain.Value;
        Skip.If(toolchain is null, "xcrun with the iOS SDKs is required");
        var target = architecture == SwiftArchitecture.Arm64 ? ThunkTargetArch.Arm64 : ThunkTargetArch.X86_64;
        var shapes = Shapes().Where(s => target.CanEmit(s.Descriptor)).ToList();
        var source = Emit(target, shapes.Select(s => s.Descriptor));
        var thunks = SwiftBridgeGate.ThunkSymbols(source);
        Assert.Equal(shapes.Count, thunks.Count);

        var validation = SwiftBridgeValidator.ValidateSource(source, architecture, new Dictionary<string, SwiftFrame>(), toolchain!, Scratch("shapes"), thunks);

        var names = shapes.ToDictionary(s => "_" + s.Descriptor.ThunkSymbol, s => s.Shape);
        Assert.True(validation.IsGreen, string.Join("\n", validation.Failures.Select(f => $"{names.GetValueOrDefault(f.Function, f.Function)}: {f}")));
        Assert.Equal(shapes.Count, validation.Functions.Length);
    }

    /// <summary>
    /// Every framed thunk builds the canonical frame: on arm64 the frame record pushed first and the
    /// frame pointer set to it, on x86_64 rbp pushed over the return address and made the frame pointer.
    /// </summary>
    [Theory]
    [InlineData(SwiftArchitecture.Arm64)]
    [InlineData(SwiftArchitecture.X86_64)]
    public void EveryFramedThunk_OpensWithTheCanonicalFrameRecord(SwiftArchitecture architecture)
    {
        var target = architecture == SwiftArchitecture.Arm64 ? ThunkTargetArch.Arm64 : ThunkTargetArch.X86_64;
        foreach (var (shape, descriptor) in Shapes().Where(s => target.CanEmit(s.Descriptor)))
        {
            var lines = ThunkAssemblyEmitter.EmitThunk(descriptor, target).Split('\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith(".globl", StringComparison.Ordinal) && !l.StartsWith(".p2align", StringComparison.Ordinal)
                            && !l.StartsWith(".weak_reference", StringComparison.Ordinal) && !l.EndsWith(':'))
                .ToList();
            Assert.True(lines[0] == ".cfi_startproc", $"{shape}: {lines[0]}");
            Assert.True(lines[^1] == ".cfi_endproc", $"{shape}: {lines[^1]}");
            var body = lines.Where(l => !l.StartsWith(".cfi", StringComparison.Ordinal)).ToList();
            bool framed = body.Any(l => l.StartsWith(architecture == SwiftArchitecture.Arm64 ? "bl " : "callq", StringComparison.Ordinal));
            if (!framed)
                continue;
            if (architecture == SwiftArchitecture.Arm64)
            {
                Assert.True(Normalize(body[0]) == "stp x29, x30, [sp, #-16]!" && Normalize(body[1]) == "mov x29, sp", $"{shape}: {body[0]} / {body[1]}");
                Assert.Contains(".cfi_def_cfa w29, 16", lines.Select(Normalize));
            }
            else
            {
                Assert.True(Normalize(body[0]) == "pushq %rbp" && Normalize(body[1]) == "movq %rsp, %rbp", $"{shape}: {body[0]} / {body[1]}");
                Assert.Contains(".cfi_def_cfa_register %rbp", lines.Select(Normalize));
            }
        }

        static string Normalize(string line) => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // ---- seeded reds: the thunk frames the emitter used to write ----

    private SwiftBridgeValidation Seed(SwiftArchitecture architecture, string body, string name)
    {
        var source = ".text\n.weak_reference _target\n" + body;
        return SwiftBridgeValidator.ValidateSource(source, architecture, new Dictionary<string, SwiftFrame>(), Toolchain.Value!, Scratch(name), ["_thunk_seed"]);
    }

    /// <summary>The arm64 throwing instance-method thunk as the emitter used to write it: the frame record 32 bytes below the CFA.</summary>
    private const string OldArm64ThrowingBody = """
            stp     x20, x19, [sp, #-48]!
            stp     x29, x30, [sp, #16]
            add     x29, sp, #16
            str     x21, [sp, #32]
            mov     x19, x2
            mov     x20, x1
            mov     x21, xzr
            bl      _target
            str     x21, [x19]
            ldr     x21, [sp, #32]
            ldp     x29, x30, [sp, #16]
            ldp     x20, x19, [sp], #48
            ret

        """;

    private const string Arm64Head = ".globl _thunk_seed\n.p2align 2\n_thunk_seed:\n";

    [SkippableFact]
    public void TheOldArm64ThrowingThunk_WithoutCfi_IsRed()
    {
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var validation = Seed(SwiftArchitecture.Arm64, Arm64Head + OldArm64ThrowingBody, "old-arm64");
        Assert.False(validation.IsGreen);
        Assert.Contains(validation.Failures, f => f.Check == "unwind");
        // The frame record sits at CFA-32: not the canonical frame either.
        Assert.Contains(validation.Failures, f => f.Check == "frame" && f.Detail.Contains("CFA-32"));
    }

    [SkippableFact]
    public void TheOldArm64ThrowingThunk_WithCfiThatSaysSo_IsRedForItsFrame()
    {
        // Honest CFI (CFA = fp + 32): the assembler defers to DWARF, whose row agrees with the code,
        // but the frame is not the canonical one the compact format and the template require.
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var body = OldArm64ThrowingBody.Replace("    add     x29, sp, #16\n", """
                add     x29, sp, #16
                .cfi_def_cfa w29, 32
                .cfi_offset w30, -24
                .cfi_offset w29, -32
                .cfi_offset w21, -16
                .cfi_offset w19, -40
                .cfi_offset w20, -48

            """);
        var validation = Seed(SwiftArchitecture.Arm64, Arm64Head + "    .cfi_startproc\n" + body + "    .cfi_endproc\n", "old-arm64-honest");
        Assert.False(validation.IsGreen);
        Assert.Contains(validation.Failures, f => f.Check == "frame" && f.Detail.Contains("CFA-32"));
        Assert.DoesNotContain(validation.Failures, f => f.Check == "unwind");
    }

    [SkippableFact]
    public void TheOldArm64ThrowingThunk_WithCfiClaimingTheStandardFrame_IsRedForItsUnwindInformation()
    {
        // CFI that claims the standard frame: the assembler writes a compact frame encoding that
        // unwinds the caller's sp 16 bytes short and restores from the wrong slots.
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var body = OldArm64ThrowingBody.Replace("    add     x29, sp, #16\n", """
                add     x29, sp, #16
                .cfi_def_cfa w29, 16
                .cfi_offset w30, -8
                .cfi_offset w29, -16
                .cfi_offset w19, -24
                .cfi_offset w20, -32

            """);
        var validation = Seed(SwiftArchitecture.Arm64, Arm64Head + "    .cfi_startproc\n" + body + "    .cfi_endproc\n", "old-arm64-lie");
        Assert.False(validation.IsGreen);
        Assert.Contains(validation.Failures, f => f.Check == "unwind" && f.Detail.Contains("compact encoding"));
    }

    [SkippableTheory]
    [InlineData(SwiftArchitecture.Arm64)]
    [InlineData(SwiftArchitecture.X86_64)]
    public void AnOldTailCallThunk_WithoutCfi_IsRed(SwiftArchitecture architecture)
    {
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var body = architecture == SwiftArchitecture.Arm64
            ? Arm64Head + "    b       _target\n"
            : ".globl _thunk_seed\n.p2align 4, 0x90\n_thunk_seed:\n    jmp     _target\n";
        var validation = Seed(architecture, body, "old-tail");
        Assert.False(validation.IsGreen);
        Assert.Contains(validation.Failures, f => f.Check == "unwind");
    }

    [SkippableFact]
    public void TheOldX86FullFrameThunk_WithoutCfi_IsRed()
    {
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var validation = Seed(SwiftArchitecture.X86_64, """
            .globl _thunk_seed
            .p2align 4, 0x90
            _thunk_seed:
                pushq   %rbp
                movq    %rsp, %rbp
                pushq   %rbx
                pushq   %r12
                pushq   %r13
                subq    $8, %rsp
                movq    %rsi, -32(%rbp)
                movq    %rdi, %r13
                xorl    %r12d, %r12d
                callq   _target
                movq    -32(%rbp), %r10
                movq    %r12, (%r10)
                addq    $8, %rsp
                popq    %r13
                popq    %r12
                popq    %rbx
                popq    %rbp
                retq

            """, "old-x86");
        Assert.False(validation.IsGreen);
        Assert.Contains(validation.Failures, f => f.Check == "unwind");
        Assert.DoesNotContain(validation.Failures, f => f.Check is "frame" or "callee-saved");
    }

    [SkippableTheory]
    [InlineData(SwiftArchitecture.Arm64)]
    [InlineData(SwiftArchitecture.X86_64)]
    public void AFrameClaimOnCodeThatBuildsNoFrame_IsRed(SwiftArchitecture architecture)
    {
        // CFI describing the standard frame around a tail branch that builds none: the compact
        // encoding's rule is on a frame pointer the code never sets.
        Skip.If(Toolchain.Value is null, "xcrun with the iOS SDKs is required");
        var body = architecture == SwiftArchitecture.Arm64
            ? Arm64Head + """
                .cfi_startproc
                .cfi_def_cfa w29, 16
                .cfi_offset w30, -8
                .cfi_offset w29, -16
                b       _target
                .cfi_endproc

            """
            : """
            .globl _thunk_seed
            .p2align 4, 0x90
            _thunk_seed:
                .cfi_startproc
                .cfi_def_cfa_offset 16
                .cfi_offset %rbp, -16
                .cfi_def_cfa_register %rbp
                jmp     _target
                .cfi_endproc

            """;
        var validation = Seed(architecture, body, "frame-claim");
        Assert.False(validation.IsGreen);
        Assert.Contains(validation.Failures, f => f.Check == "unwind" && f.Detail.Contains("never sets"));
    }

    // ---- coverage ----

    /// <summary>A canonical arm64 frame saving x19/x20, with one call.</summary>
    private static List<SwiftInstruction> CanonicalArm64() =>
    [
        new(0x4000, "stp", ["x29", "x30", "[sp, #-0x10]!"]),
        new(0x4004, "mov", ["x29", "sp"]),
        new(0x4008, "stp", ["x20", "x19", "[sp, #-0x10]!"]),
        new(0x400c, "mov", ["x20", "x0"]),
        new(0x4010, "bl", ["0x5000"]),
        new(0x4014, "ldp", ["x20", "x19", "[sp]", "#0x10"]),
        new(0x4018, "ldp", ["x29", "x30", "[sp]", "#0x10"]),
        new(0x401c, "ret", []),
    ];

    [Fact]
    public void AFunctionTheUnwindTableStopsCovering_IsRed()
    {
        var code = CanonicalArm64();
        Assert.Empty(SwiftBridgeValidator.Check("_f", SwiftArchitecture.Arm64, 0x4000, code, _ => 0x04000001u, [], null));

        // The table's end sentinel falls inside the function.
        var ended = SwiftBridgeValidator.Check("_f", SwiftArchitecture.Arm64, 0x4000, code, pc => pc < 0x4014 ? 0x04000001u : null, [], null);
        Assert.Contains(ended, f => f.Check == "unwind" && f.Detail.Contains("ends before 4014"));

        // Another function's entry begins inside it.
        var overlapped = SwiftBridgeValidator.Check("_f", SwiftArchitecture.Arm64, 0x4000, code, pc => pc < 0x4010 ? 0x04000001u : 0x02000000u, [], null);
        Assert.Contains(overlapped, f => f.Check == "unwind" && f.Detail.Contains("another unwind entry"));
    }

    [Fact]
    public void AFrameEncodingThatOmitsASavedPair_IsRedAtTheCall()
    {
        // 0x04000000 restores only the frame record; the code also writes x19 and x20.
        var findings = SwiftBridgeValidator.Check("_f", SwiftArchitecture.Arm64, 0x4000, CanonicalArm64(), _ => 0x04000000u, [], null);
        Assert.Contains(findings, f => f.Check == "unwind" && f.Detail.Contains("x20") && f.Detail.Contains("does not restore"));
    }

    [Fact]
    public void AFramelessTailCall_IsCheckedAtEntryAndExit()
    {
        List<SwiftInstruction> tail = [new(0x4000, "b", ["0x5000"])];
        Assert.Empty(SwiftBridgeValidator.Check("_f", SwiftArchitecture.Arm64, 0x4000, tail, _ => 0x02000000u, [], null));

        // A frameless encoding with a stack size the code does not allocate.
        var sized = SwiftBridgeValidator.Check("_f", SwiftArchitecture.Arm64, 0x4000, tail, _ => 0x02001000u, [], null);
        Assert.Contains(sized, f => f.Check == "unwind" && f.Detail.Contains("sp+16"));

        // A tail branch that leaves a callee-saved register changed.
        List<SwiftInstruction> clobbering = [new(0x4000, "mov", ["x19", "x0"]), new(0x4004, "b", ["0x5000"])];
        Assert.Contains(SwiftBridgeValidator.Check("_f", SwiftArchitecture.Arm64, 0x4000, clobbering, _ => 0x02000000u, [], null),
            f => f.Check == "callee-saved" && f.Detail.Contains("x19"));

        List<SwiftInstruction> x86 = [new(0x4000, "movq", ["%rdi", "%rax"]), new(0x4003, "jmp", ["0x5000"])];
        Assert.Empty(SwiftBridgeValidator.Check("_f", SwiftArchitecture.X86_64, 0x4000, x86, _ => 0x02010000u, [], null));
        Assert.Contains(SwiftBridgeValidator.Check("_f", SwiftArchitecture.X86_64, 0x4000, x86, _ => 0x02000000u, [], null),
            f => f.Check == "unwind" && f.Detail.Contains("rsp+0"));
    }

    [Fact]
    public void AConditionalBranch_FailsClosed()
    {
        var code = CanonicalArm64();
        code.Insert(4, new SwiftInstruction(0x400e, "cbz", ["x0", "0x4018"]));
        Assert.Contains(SwiftBridgeValidator.Check("_f", SwiftArchitecture.Arm64, 0x4000, code, _ => 0x04000001u, [], null),
            f => f.Check == "frame" && f.Detail.Contains("conditional branch"));
    }

    [Fact]
    public void ThunkAssemblyFiles_AreRecognizedByName_AndTheirThunksListed()
    {
        Assert.Equal(SwiftArchitecture.Arm64, SwiftBridgeGate.ThunkAssemblyArchitecture("/x/Mod.arm64.s"));
        Assert.Equal(SwiftArchitecture.X86_64, SwiftBridgeGate.ThunkAssemblyArchitecture("/x/Mod.x86_64.s"));
        Assert.Null(SwiftBridgeGate.ThunkAssemblyArchitecture("/x/Mod.s"));
        var source = Emit(ThunkTargetArch.Arm64, Shapes().Select(s => s.Descriptor));
        Assert.Equal(Shapes().Select(s => "_" + s.Descriptor.ThunkSymbol).Order(StringComparer.Ordinal), SwiftBridgeGate.ThunkSymbols(source));
    }
}
