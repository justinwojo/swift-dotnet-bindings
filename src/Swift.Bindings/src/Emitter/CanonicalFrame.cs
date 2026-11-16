// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// The one frame shape of every function the generator writes in assembly: the Swift bridges
/// (<see cref="SwiftBridgeEmitter"/>) and the native thunks (<see cref="Arm64ThunkTarget"/>,
/// <see cref="SysVThunkTarget"/>). A frame record at the top (<c>x29</c>/<c>x30</c>, or <c>rbp</c>
/// over the return address) whose CFA is the frame pointer plus 16, the saved callee-saved
/// registers immediately below it, then any locals. The CFI describes exactly that frame, in the
/// order the compact unwind format reads it, so the assembler's compact encoding agrees with the
/// code; <see cref="SwiftBridgeValidator"/> checks both against the instructions.
/// </summary>
/// <remarks>
/// <para>
/// This is a conservative restriction, not a claim that other frames are invalid: the toolchain's
/// compact-unwind synthesis encoded a frame whose CFA is the frame pointer plus 32 as the standard
/// frame, which unwinds the caller's stack pointer 16 bytes short. Every emitter uses this template,
/// so no emitter can reintroduce that shape.
/// </para>
/// <para>
/// The epilogue carries no CFI: compact unwind describes the body only, and an epilogue directive
/// would force the function out of the compact format into DWARF. A function that builds no frame
/// (a tail branch) still opens and closes its CFI, which gives it a frameless encoding rather than
/// none (on x86_64 through <see cref="X86FramelessBegin"/>).
/// </para>
/// </remarks>
public static class CanonicalFrame
{
    /// <summary>How a caller lays out its lines: the indent, and the width a mnemonic is padded to (0: one space).</summary>
    public readonly record struct Style(string Indent, int MnemonicWidth)
    {
        /// <summary>The bridges' layout: two spaces, one space after the mnemonic.</summary>
        public static Style Bridge { get; } = new("  ", 0);

        /// <summary>The thunks' layout: four spaces, mnemonics padded to seven columns.</summary>
        public static Style Thunk { get; } = new("    ", 7);

        internal string Line(string mnemonic, string operands = "") => operands.Length == 0
            ? Indent + mnemonic
            : Indent + (MnemonicWidth > 0 ? mnemonic.PadRight(MnemonicWidth) : mnemonic) + " " + operands;
    }

    /// <summary>Opens a function's CFI.</summary>
    public static void Begin(StringBuilder s, Style style) => s.Append(style.Line(".cfi_startproc")).Append('\n');

    /// <summary>Closes a function's CFI.</summary>
    public static void End(StringBuilder s, Style style) => s.Append(style.Line(".cfi_endproc")).Append('\n');

    /// <summary>
    /// Opens the CFI of an x86_64 function that builds no frame (a tail branch), restating the entry
    /// rule (CFA = rsp + 8). The x86_64 compact encoder writes no encoding at all for a function with
    /// no CFI instruction, which the unwinder reads as no unwind information; the restated rule gives
    /// the function the frameless encoding instead. (arm64's encoder already writes frameless for an
    /// empty body: <see cref="Begin"/> is enough there.)
    /// </summary>
    public static void X86FramelessBegin(StringBuilder s, Style style)
    {
        Begin(s, style);
        s.Append(style.Line(".cfi_def_cfa_offset", "8")).Append('\n');
    }

    // ---- arm64 ----

    /// <summary>
    /// Opens the function's CFI and builds the arm64 frame: the frame record, then each pair of
    /// <paramref name="pairs"/> pushed below it (<c>stp First, Second, [sp, #-16]!</c>), described by CFI.
    /// </summary>
    /// <param name="pairs">Callee-saved register pairs, from the frame record down. The compact
    /// format names x19/x20, x21/x22, … pairs; each pair is written <c>(x20, x19)</c>, the higher
    /// register at the lower address.</param>
    public static void Arm64Prologue(StringBuilder s, Style style, IReadOnlyList<(string First, string Second)> pairs)
    {
        Begin(s, style);
        s.Append(style.Line("stp", "x29, x30, [sp, #-16]!")).Append('\n');
        s.Append(style.Line("mov", "x29, sp")).Append('\n');
        s.Append(style.Line(".cfi_def_cfa", "w29, 16")).Append('\n');
        s.Append(style.Line(".cfi_offset", "w30, -8")).Append('\n');
        s.Append(style.Line(".cfi_offset", "w29, -16")).Append('\n');
        foreach (var (first, second) in pairs)
            s.Append(style.Line("stp", $"{first}, {second}, [sp, #-16]!")).Append('\n');
        // Pair j (from 1) sits at CFA - 16(j+1): its first register at the lower address. The
        // compact format reads the pairs from the frame record down, the second register first.
        for (int j = 0; j < pairs.Count; j++)
        {
            int baseOffset = 16 * (j + 2);
            s.Append(style.Line(".cfi_offset", $"w{pairs[j].Second[1..]}, -{Number(baseOffset - 8)}")).Append('\n');
            s.Append(style.Line(".cfi_offset", $"w{pairs[j].First[1..]}, -{Number(baseOffset)}")).Append('\n');
        }
    }

    /// <summary>
    /// Restores what <see cref="Arm64Prologue"/> saved and returns; sp must be back where the
    /// prologue left it. Closes the function's CFI.
    /// </summary>
    public static void Arm64Epilogue(StringBuilder s, Style style, IReadOnlyList<(string First, string Second)> pairs)
    {
        for (int j = pairs.Count - 1; j >= 0; j--)
            s.Append(style.Line("ldp", $"{pairs[j].First}, {pairs[j].Second}, [sp], #16")).Append('\n');
        s.Append(style.Line("ldp", "x29, x30, [sp], #16")).Append('\n');
        s.Append(style.Line("ret")).Append('\n');
        End(s, style);
    }

    // ---- x86_64 ----

    /// <summary>
    /// Opens the function's CFI and builds the x86_64 frame: <c>rbp</c> pushed over the return
    /// address and made the frame pointer, then each of <paramref name="saved"/> pushed in order,
    /// described by CFI.
    /// </summary>
    public static void X86Prologue(StringBuilder s, Style style, IReadOnlyList<string> saved)
    {
        Begin(s, style);
        s.Append(style.Line("pushq", "%rbp")).Append('\n');
        s.Append(style.Line(".cfi_def_cfa_offset", "16")).Append('\n');
        s.Append(style.Line(".cfi_offset", "%rbp, -16")).Append('\n');
        s.Append(style.Line("movq", "%rsp, %rbp")).Append('\n');
        s.Append(style.Line(".cfi_def_cfa_register", "%rbp")).Append('\n');
        foreach (var register in saved)
            s.Append(style.Line("pushq", "%" + register)).Append('\n');
        // The k-th push (from 1) is at CFA - 16 - 8k. The compact format reads the saved registers
        // from the lowest address up, in the order the CFI names them: the last push first.
        for (int k = saved.Count; k >= 1; k--)
            s.Append(style.Line(".cfi_offset", $"%{saved[k - 1]}, -{Number(16 + 8 * k)}")).Append('\n');
    }

    /// <summary>
    /// Drops any locals by resetting <c>rsp</c> from the frame pointer, pops what
    /// <see cref="X86Prologue"/> pushed and returns. Closes the function's CFI.
    /// </summary>
    public static void X86Epilogue(StringBuilder s, Style style, IReadOnlyList<string> saved)
    {
        if (saved.Count > 0)
            s.Append(style.Line("leaq", $"-{Number(8 * saved.Count)}(%rbp), %rsp")).Append('\n');
        else
            s.Append(style.Line("movq", "%rbp, %rsp")).Append('\n');
        for (int k = saved.Count - 1; k >= 0; k--)
            s.Append(style.Line("popq", "%" + saved[k])).Append('\n');
        s.Append(style.Line("popq", "%rbp")).Append('\n');
        s.Append(style.Line("retq")).Append('\n');
        End(s, style);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
