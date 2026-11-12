// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using static BindingsGeneration.Tests.SwiftCallPlanTests;

namespace BindingsGeneration.Tests;

/// <summary>
/// Emitted bridges, executed: a C driver calls swiftc-compiled callees through the bridges the
/// emitter writes for four plans (register-only, a stack prefix, an indirect result, a thrown error),
/// on arm64 macOS and on x86_64 under Rosetta, through a shim that fills every callee-saved register
/// with a sentinel and reports any the call did not preserve. The validator reads what the bridges
/// claim; this runs them.
/// </summary>
public class SwiftBridgeExecutionTests
{
    private const string CalleeSource = """
        public func registerOnly(_ a: Int, _ b: Double, _ c: Int32) -> Int { a + Int(b) * 1000 + Int(c) * 1_000_000 }

        public func stackPrefix(_ a0: Int, _ a1: Int, _ a2: Int, _ a3: Int, _ a4: Int, _ a5: Int, _ a6: Int, _ a7: Int, _ a8: Int, _ a9: Int, _ a10: Int) -> Int {
            [a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10].enumerated().reduce(0) { $0 + ($1.offset + 1) * $1.element }
        }

        public struct Wide {
            public var a, b, c, d, e: Int
        }

        public func indirect(_ seed: Int) -> Wide { Wide(a: seed, b: seed * 2, c: seed * 3, d: seed * 4, e: seed * 5) }

        public struct Failure: Error {
            public var code: Int
        }

        public func throwing(_ x: Int) throws -> Int {
            if x < 0 { throw Failure(code: x) }
            return x * 2
        }
        """;

    /// <summary>
    /// <c>sentinel_shim</c>: saves the callee-saved registers, copies sixteen stack words of its
    /// caller's arguments to its own outgoing area, fills x19-x28 and d8-d15 with sentinels, calls
    /// <c>sentinel_target</c> with every argument register untouched, and stores in
    /// <c>sentinel_clobbered</c> a bit per register not preserved (bits 0-9 x19-x28, 10-17 d8-d15,
    /// 18 sp).
    /// </summary>
    private static readonly string Arm64Shim = BuildArm64Shim();

    private static string BuildArm64Shim()
    {
        var s = new StringBuilder();
        s.Append("""
            .text
            .globl _sentinel_shim
            .p2align 2
            _sentinel_shim:
                stp x29, x30, [sp, #-16]!
                mov x29, sp
                sub sp, sp, #144
                stp x19, x20, [sp, #0]
                stp x21, x22, [sp, #16]
                stp x23, x24, [sp, #32]
                stp x25, x26, [sp, #48]
                stp x27, x28, [sp, #64]
                stp d8, d9, [sp, #80]
                stp d10, d11, [sp, #96]
                stp d12, d13, [sp, #112]
                stp d14, d15, [sp, #128]
                sub sp, sp, #128
                mov x9, #0
                add x10, x29, #16
            1:
                ldr x11, [x10, x9, lsl #3]
                str x11, [sp, x9, lsl #3]
                add x9, x9, #1
                cmp x9, #16
                b.lt 1b

            """);
        for (int i = 0; i < 10; i++)
            s.Append($"    mov x{19 + i}, #{275 + i}\n");
        for (int i = 0; i < 8; i++)
            s.Append($"    mov x9, #{520 + i}\n    fmov d{8 + i}, x9\n");
        s.Append("""
                adrp x16, _sentinel_target@PAGE
                ldr x16, [x16, _sentinel_target@PAGEOFF]
                blr x16
                mov x9, #0

            """);
        for (int i = 0; i < 10; i++)
            s.Append($"    cmp x{19 + i}, #{275 + i}\n    cset x10, ne\n    orr x9, x9, x10, lsl #{i}\n");
        for (int i = 0; i < 8; i++)
            s.Append($"    fmov x11, d{8 + i}\n    cmp x11, #{520 + i}\n    cset x10, ne\n    orr x9, x9, x10, lsl #{10 + i}\n");
        s.Append("""
                sub x11, x29, #272
                mov x12, sp
                cmp x12, x11
                cset x10, ne
                orr x9, x9, x10, lsl #18
                adrp x10, _sentinel_clobbered@PAGE
                str x9, [x10, _sentinel_clobbered@PAGEOFF]
                add sp, sp, #128
                ldp x19, x20, [sp, #0]
                ldp x21, x22, [sp, #16]
                ldp x23, x24, [sp, #32]
                ldp x25, x26, [sp, #48]
                ldp x27, x28, [sp, #64]
                ldp d8, d9, [sp, #80]
                ldp d10, d11, [sp, #96]
                ldp d12, d13, [sp, #112]
                ldp d14, d15, [sp, #128]
                add sp, sp, #144
                ldp x29, x30, [sp], #16
                ret

            """);
        return s.ToString();
    }

    /// <summary>The x86_64 shim: rbx and r12-r15 (bits 0-4), sp (bit 18).</summary>
    private static readonly string X86Shim = BuildX86Shim();

    private static string BuildX86Shim()
    {
        string[] saved = ["rbx", "r12", "r13", "r14", "r15"];
        var s = new StringBuilder();
        s.Append("""
            .text
            .globl _sentinel_shim
            .p2align 4, 0x90
            _sentinel_shim:
                pushq %rbp
                movq %rsp, %rbp
                pushq %rbx
                pushq %r12
                pushq %r13
                pushq %r14
                pushq %r15
                subq $136, %rsp
                xorl %r11d, %r11d
            1:
                movq 16(%rbp,%r11,8), %r10
                movq %r10, (%rsp,%r11,8)
                incq %r11
                cmpq $16, %r11
                jl 1b

            """);
        for (int i = 0; i < saved.Length; i++)
            s.Append($"    movq ${275 + i}, %{saved[i]}\n");
        s.Append("""
                movq _sentinel_target(%rip), %r11
                callq *%r11
                xorl %r10d, %r10d

            """);
        for (int i = 0; i < saved.Length; i++)
            s.Append($"    cmpq ${275 + i}, %{saved[i]}\n    setne %r11b\n    movzbq %r11b, %r11\n    shlq ${i}, %r11\n    orq %r11, %r10\n");
        s.Append("""
                leaq -176(%rbp), %r11
                cmpq %r11, %rsp
                setne %r11b
                movzbq %r11b, %r11
                shlq $18, %r11
                orq %r11, %r10
                movq %r10, _sentinel_clobbered(%rip)
                leaq -40(%rbp), %rsp
                popq %r15
                popq %r14
                popq %r13
                popq %r12
                popq %rbx
                popq %rbp
                retq

            """);
        return s.ToString();
    }

    private sealed record Bridges(SwiftCallPlan RegisterOnly, SwiftCallPlan StackPrefix, SwiftCallPlan Indirect, SwiftCallPlan Throwing);

    private static Bridges Plans()
    {
        var wide = With(("Callee.Wide", SwiftLoweringAnswer.IndirectBecause("five words")));
        var eleven = Enumerable.Repeat("Swift.Int", 11).ToArray();
        return new Bridges(
            Planned(Planner().PlanForward(Free("Swift.Int", "Swift.Int", "Swift.Double", "Swift.Int32"), Signature("long", "long", "double", "int"),
                "$s6Callee12registerOnlyyS2i_Sds5Int32VtF")),
            Planned(Planner().PlanForward(Free("Swift.Int", eleven), Signature("long", Enumerable.Repeat("long", 11).ToArray()),
                "$s6Callee11stackPrefixyS2i_S10itF")),
            Planned(Planner(lowering: wide).PlanForward(Free("Callee.Wide", "Swift.Int"), Signature("void", "SwiftIndirectResult", "long"),
                "$s6Callee8indirectyAA4WideVSiF")),
            Planned(Planner().PlanForward(Throwing(Free("Swift.Int", "Swift.Int")), Signature("long", "long", "SwiftError*"),
                "$s6Callee8throwingyS2iKF")));
    }

    private static string Driver(Bridges b) => $$"""
        #include <stdio.h>

        void *sentinel_target;
        long sentinel_clobbered;
        void sentinel_shim(void);

        long {{b.RegisterOnly.Symbol}}(long, double, int);
        long {{b.StackPrefix.Symbol}}(long, long, long, long, long, long, long, long, long, long, long);
        typedef struct { long a, b, c, d, e; } Wide;
        void {{b.Indirect.Symbol}}(long, Wide *);
        long {{b.Throwing.Symbol}}(long, void **);

        int main(void) {
            sentinel_target = (void *)&{{b.RegisterOnly.Symbol}};
            long r = ((long (*)(long, double, int))sentinel_shim)(7, 2.5, -3);
            printf("register-only %ld %ld\n", r, sentinel_clobbered);

            sentinel_target = (void *)&{{b.StackPrefix.Symbol}};
            r = ((long (*)(long, long, long, long, long, long, long, long, long, long, long))sentinel_shim)(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
            printf("stack-prefix %ld %ld\n", r, sentinel_clobbered);

            Wide w = { -1, -1, -1, -1, -1 };
            sentinel_target = (void *)&{{b.Indirect.Symbol}};
            ((void (*)(long, Wide *))sentinel_shim)(6, &w);
            printf("indirect %ld %ld %ld %ld %ld %ld\n", w.a, w.b, w.c, w.d, w.e, sentinel_clobbered);

            void *error = (void *)1;
            sentinel_target = (void *)&{{b.Throwing.Symbol}};
            r = ((long (*)(long, void **))sentinel_shim)(21, &error);
            printf("returned %ld %d %ld\n", r, error == 0, sentinel_clobbered);
            error = 0;
            ((long (*)(long, void **))sentinel_shim)(-5, &error);
            printf("thrown %d %ld\n", error != 0, sentinel_clobbered);
            return 0;
        }
        """;

    private static (int Code, string Output) Run(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }

    private static bool HasRosetta() => File.Exists("/usr/bin/arch") && Run("/usr/bin/arch", "-x86_64", "/usr/bin/true").Code == 0;

    /// <summary>
    /// With <paramref name="clobber"/>, the register-only bridge is edited to write a callee-saved
    /// register it never saves (x23, or r15): the shim must see it, or a green run proves nothing.
    /// </summary>
    [SkippableTheory]
    [InlineData(SwiftArchitecture.Arm64, false)]
    [InlineData(SwiftArchitecture.X86_64, false)]
    [InlineData(SwiftArchitecture.Arm64, true)]
    [InlineData(SwiftArchitecture.X86_64, true)]
    public void EmittedBridges_CallSwiftcCompiledCallees_AndPreserveEveryCalleeSavedRegister(SwiftArchitecture architecture, bool clobber)
    {
        Skip.IfNot(OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64 && File.Exists("/usr/bin/xcrun"),
            "an arm64 Mac with Xcode is required");
        Skip.If(architecture == SwiftArchitecture.X86_64 && !HasRosetta(), "Rosetta is required for the x86_64 bridges");

        var triple = architecture == SwiftArchitecture.Arm64 ? "arm64-apple-macos11" : "x86_64-apple-macos11";
        var scratch = Path.Combine(Path.GetTempPath(), "swift-bridge-execution", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var b = Plans();
            var assembly = SwiftBridgeEmitter.Emit([b.RegisterOnly, b.StackPrefix, b.Indirect, b.Throwing], architecture);
            Assert.Empty(assembly.Refused);
            File.WriteAllText(Path.Combine(scratch, "Callee.swift"), CalleeSource);
            var source = assembly.Source;
            if (clobber)
            {
                var call = architecture == SwiftArchitecture.Arm64 ? $"  bl _{b.RegisterOnly.Body.Target}\n" : $"  callq _{b.RegisterOnly.Body.Target}\n";
                Assert.Contains(call, source);
                source = source.Replace(call, call + (architecture == SwiftArchitecture.Arm64 ? "  mov x23, #1\n" : "  movq $1, %r15\n"));
            }
            File.WriteAllText(Path.Combine(scratch, "bridges.s"), source);
            File.WriteAllText(Path.Combine(scratch, "shim.s"), architecture == SwiftArchitecture.Arm64 ? Arm64Shim : X86Shim);
            File.WriteAllText(Path.Combine(scratch, "driver.c"), Driver(b));

            var callee = Run("/usr/bin/xcrun", "swiftc", "-parse-as-library", "-emit-library", "-target", triple, "-module-name", "Callee",
                "-Xlinker", "-install_name", "-Xlinker", "@rpath/libCallee.dylib",
                "-o", Path.Combine(scratch, "libCallee.dylib"), Path.Combine(scratch, "Callee.swift"));
            Assert.True(callee.Code == 0, callee.Output);
            var symbols = Run("/usr/bin/xcrun", "nm", "-g", Path.Combine(scratch, "libCallee.dylib")).Output;
            foreach (var target in new[] { b.RegisterOnly, b.StackPrefix, b.Indirect, b.Throwing }.Select(p => p.Body.Target!))
                Assert.Contains(" T _" + target, symbols);

            var driver = Path.Combine(scratch, "driver");
            var link = Run("/usr/bin/xcrun", "clang", "-target", triple, "-o", driver,
                Path.Combine(scratch, "driver.c"), Path.Combine(scratch, "shim.s"), Path.Combine(scratch, "bridges.s"),
                "-L", scratch, "-lCallee", "-Wl,-rpath," + scratch);
            Assert.True(link.Code == 0, link.Output);

            var run = architecture == SwiftArchitecture.Arm64 ? Run(driver) : Run("/usr/bin/arch", "-x86_64", driver);
            Assert.True(run.Code == 0, run.Output);
            var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(new[]
            {
                // 7 + 2 * 1000 - 3 * 1000000; every register preserved (0), or x23/r15 (bit 4) clobbered.
                clobber ? "register-only -2997993 16" : "register-only -2997993 0",
                // The sum of (i + 1) * (i + 1) for i in 0..<11.
                "stack-prefix 506 0",
                "indirect 6 12 18 24 30 0",
                // The bridge stores the zeroed error register into the slot: null on return.
                "returned 42 1 0",
                "thrown 1 0",
            }, lines);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }
}
