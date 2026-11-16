// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>One check a bridge failed: which function, which check and what was found.</summary>
public sealed record SwiftBridgeFinding(string Function, string Check, string Detail)
{
    public override string ToString() => $"{Function}: [{Check}] {Detail}";
}

/// <summary>The checks of one architecture's bridge assembly: the functions checked and every failure.</summary>
public sealed record SwiftBridgeValidation(SwiftArchitecture Architecture, ImmutableArray<string> Functions, ImmutableArray<SwiftBridgeFinding> Failures)
{
    public bool IsGreen => Failures.IsEmpty && !Functions.IsEmpty;

    public override string ToString() => IsGreen
        ? $"{Architecture}: {Functions.Length} bridge(s) green"
        : $"{Architecture}: {Functions.Length} bridge(s), {Failures.Length} failure(s)\n  " + string.Join("\n  ", Failures);
}

/// <summary>A saved register's location: its offset from the CFA.</summary>
public sealed record SwiftFrameState(int StackPointer, int? FramePointer, ImmutableSortedDictionary<string, int> Saved);

/// <summary>Where in a function the unwind rule is checked: its entry, a call, or its exit (a return or a tail branch).</summary>
public enum SwiftFramePointKind
{
    Entry,
    Call,
    Exit,
}

/// <summary>
/// The frame at one point of a function: sp and the frame pointer as CFA offsets (the frame pointer
/// null while the function has not set it, or after it reloads the caller's), the first save of each
/// callee-saved register, which register's caller value each stack slot holds, and which tracked
/// registers no longer hold the caller's value.
/// </summary>
public sealed record SwiftFramePoint(
    SwiftFramePointKind Kind, ulong Address, int StackPointer, int? FramePointer,
    ImmutableSortedDictionary<string, int> Saved, ImmutableDictionary<int, string> Stack, ImmutableHashSet<string> Changed);

/// <summary>What an image's unwind information claims for one function at its call site.</summary>
/// <param name="CfaRegister">The register the CFA is computed from (<c>x29</c>, <c>sp</c>, <c>rbp</c>, <c>rsp</c>).</param>
/// <param name="CfaOffset">What is added to it.</param>
/// <param name="Saved">Each saved register's offset from the CFA.</param>
/// <param name="Source">Where the claim comes from (the compact encoding, or a DWARF row).</param>
public sealed record SwiftUnwindClaim(string CfaRegister, int CfaOffset, ImmutableSortedDictionary<string, int> Saved, string Source);

/// <summary>A DWARF FDE: where it starts in <c>__eh_frame</c>, the code it covers and its evaluated rows.</summary>
public sealed record SwiftFde(ulong Offset, ulong Start, ulong End, ImmutableArray<SwiftCfaRow> Rows);

/// <summary>One evaluated CFA row: from <see cref="Pc"/>, the CFA rule and each saved register's offset from the CFA.</summary>
public sealed record SwiftCfaRow(ulong Pc, string CfaRegister, int CfaOffset, ImmutableSortedDictionary<string, int> Saved);

/// <summary>One disassembled instruction.</summary>
public sealed record SwiftInstruction(ulong Address, string Mnemonic, ImmutableArray<string> Operands)
{
    public override string ToString() => $"{Address:x}: {Mnemonic} {string.Join(", ", Operands)}";
}

/// <summary>
/// The unwind validator of the functions the generator writes in assembly: the native bridges and
/// the native thunks. For every function it checks, from the instructions alone, the frame the code
/// actually builds, and holds three things to it: the unwind information the linker wrote (the
/// compact encoding, or the DWARF row it defers to), the frame the plan says a bridge has, and the
/// platform's rules (<c>sp</c> 16-aligned and the frame pointer at CFA-16 at every call, every
/// callee-saved register the function writes saved first and restored on return).
/// </summary>
/// <remarks>
/// <para>
/// The assembler derives a compact encoding from the CFI directives without checking them against
/// the instructions: a frame whose CFA is the frame pointer plus 32 is encoded as the standard frame
/// (fp + 16), and saved registers named in the wrong order are encoded in that order. So the actual
/// frame is computed independently, by executing the prologue symbolically (the store, push and
/// stack-adjust forms a bridge uses; any other write of the stack or frame pointer fails closed),
/// and compared with what the encoding decodes to.
/// </para>
/// <para>
/// The unwind information is checked at every point an unwinder can stop in the function with the
/// function's own frame live: at entry, at each call (the return address the callee unwinds to), and
/// at exit (the return, or the tail branch of a thunk that builds no frame). At each point the rule
/// must compute the CFA the code has there and restore every callee-saved register the function has
/// changed from a slot that holds the caller's value. A rule on the frame pointer is checked only
/// where the function has set it, since compact unwind describes the body and not the prologue or
/// epilogue. Every instruction up to the exit must be covered by the function's unwind entry: the
/// unwind table must not end, or another function's entry begin, inside it.
/// </para>
/// <para>
/// It also checks that every symbol the object leaves undefined is a weak reference, so a function
/// whose target the running OS lacks never stops its image from loading (R6a).
/// </para>
/// </remarks>
public static class SwiftBridgeValidator
{
    /// <summary>Assembles <paramref name="assembly"/>, links a scratch image and validates every bridge against its plan.</summary>
    public static SwiftBridgeValidation Validate(SwiftBridgeAssembly assembly, IEnumerable<SwiftCallPlan> plans, SwiftBridgeToolchain toolchain, string scratch)
    {
        var frames = plans.Where(p => assembly.Bridges.Contains(p.Symbol))
            .ToDictionary(p => "_" + p.Symbol, p => p.Body.Architectures.Single(a => a.Architecture == assembly.Architecture).Frame, StringComparer.Ordinal);
        return ValidateSource(assembly.Source, assembly.Architecture, frames, toolchain, scratch, frames.Keys);
    }

    /// <summary>
    /// Assembles <paramref name="source"/> and validates each of <paramref name="functions"/> (its
    /// global symbols, underscore included): against its planned frame when <paramref name="frames"/>
    /// has one, against the unwind information and the platform rules always.
    /// </summary>
    public static SwiftBridgeValidation ValidateSource(
        string source, SwiftArchitecture architecture, IReadOnlyDictionary<string, SwiftFrame> frames,
        SwiftBridgeToolchain toolchain, string scratch, IEnumerable<string> functions)
    {
        Directory.CreateDirectory(scratch);
        var name = $"bridges-{architecture.ToString().ToLowerInvariant()}";
        var sourcePath = Path.Combine(scratch, name + ".s");
        var objectPath = Path.Combine(scratch, name + ".o");
        var imagePath = Path.Combine(scratch, name + ".dylib");
        File.WriteAllText(sourcePath, source);
        var wanted = functions.Order(StringComparer.Ordinal).ToImmutableArray();
        var failures = new List<SwiftBridgeFinding>();

        var (assembled, assemblerOutput) = toolchain.Assemble(sourcePath, objectPath, architecture);
        if (!assembled)
            return new(architecture, wanted, [new SwiftBridgeFinding(name, "assemble", assemblerOutput.Trim())]);

        // R6a: every undefined symbol is weak.
        var (nmCode, nmOutput, nmError) = toolchain.Run("nm", "-m", objectPath);
        if (nmCode != 0)
            return new(architecture, wanted, [new SwiftBridgeFinding(name, "nm", nmError.Trim())]);
        var undefined = new List<string>();
        foreach (var (symbol, weak) in UndefinedSymbols(nmOutput))
        {
            undefined.Add(symbol);
            if (!weak)
                failures.Add(new SwiftBridgeFinding(name, "R6a", $"{symbol} is a strong undefined reference"));
        }

        var (linked, linkOutput) = toolchain.LinkScratch([objectPath], imagePath, architecture, undefined);
        if (!linked)
            return new(architecture, wanted, failures.Append(new SwiftBridgeFinding(name, "link", linkOutput.Trim())).ToImmutableArray());

        var image = ValidateImage(imagePath, architecture, frames, toolchain, wanted);
        return new(architecture, wanted, failures.Concat(image.Failures).ToImmutableArray());
    }

    /// <summary>
    /// Validates each of <paramref name="functions"/> (underscore included) as an already linked image
    /// holds it: <paramref name="imagePath"/> is a thin Mach-O of <paramref name="architecture"/>, such
    /// as a shipped framework's binary thinned to one slice. The bridge is held to the unwind
    /// information that image's linker wrote, which is the information the unwinder reads at run
    /// time, and to its planned frame when <paramref name="frames"/> has one.
    /// </summary>
    public static SwiftBridgeValidation ValidateImage(
        string imagePath, SwiftArchitecture architecture, IReadOnlyDictionary<string, SwiftFrame> frames,
        SwiftBridgeToolchain toolchain, IEnumerable<string> functions)
    {
        var wanted = functions.Order(StringComparer.Ordinal).ToImmutableArray();
        var failures = new List<SwiftBridgeFinding>();
        var triple = toolchain.TargetOf(architecture).Triple;
        var symbols = DefinedSymbols(toolchain.Run("nm", "--defined-only", imagePath).Output);
        var unwindOutput = toolchain.Run("llvm-objdump", "--unwind-info", imagePath).Output;
        var unwind = UnwindEntries(unwindOutput);
        var tableEnd = UnwindTableEnd(unwindOutput);
        var fdes = Fdes(toolchain.Run("llvm-dwarfdump", "--eh-frame", imagePath).Output, architecture);
        var code = Disassembly(toolchain.Run("llvm-objdump", "-d", "--no-show-raw-insn", "--triple=" + triple, imagePath).Output);

        foreach (var function in wanted)
        {
            if (!symbols.TryGetValue(function, out var address))
            {
                failures.Add(new SwiftBridgeFinding(function, "symbol", "the image defines no such function"));
                continue;
            }
            if (!code.TryGetValue(function, out var instructions))
            {
                failures.Add(new SwiftBridgeFinding(function, "disassembly", "no instructions"));
                continue;
            }
            failures.AddRange(Check(function, architecture, address, instructions,
                pc => tableEnd is { } end && pc >= end ? null : EntryFor(unwind, pc), fdes,
                frames.TryGetValue(function, out var frame) ? frame : null));
        }
        return new(architecture, wanted, failures.ToImmutableArray());
    }

    /// <summary>
    /// The checks of one function: <paramref name="encoding"/> is its compact unwind entry (null when
    /// the image has none), the same at every instruction; <paramref name="fdes"/> the image's FDEs,
    /// <paramref name="planned"/> the frame its plan describes (null when there is no plan to hold it to).
    /// </summary>
    public static ImmutableArray<SwiftBridgeFinding> Check(
        string function, SwiftArchitecture architecture, ulong address, IReadOnlyList<SwiftInstruction> instructions,
        uint? encoding, IReadOnlyList<SwiftFde> fdes, SwiftFrame? planned) =>
        Check(function, architecture, address, instructions, _ => encoding, fdes, planned);

    /// <summary>
    /// The checks of one function: <paramref name="encodingAt"/> gives the compact unwind entry the
    /// unwinder finds for a pc (null where the table has none), <paramref name="fdes"/> the image's
    /// FDEs, <paramref name="planned"/> the frame its plan describes (null when there is no plan to
    /// hold it to).
    /// </summary>
    public static ImmutableArray<SwiftBridgeFinding> Check(
        string function, SwiftArchitecture architecture, ulong address, IReadOnlyList<SwiftInstruction> instructions,
        Func<ulong, uint?> encodingAt, IReadOnlyList<SwiftFde> fdes, SwiftFrame? planned)
    {
        var failures = new List<SwiftBridgeFinding>();
        var reported = new HashSet<(string, string)>();
        void Fail(string check, string detail)
        {
            if (reported.Add((check, detail)))
                failures.Add(new SwiftBridgeFinding(function, check, detail));
        }

        var execution = Execute(architecture, instructions);
        if (execution.Error is { } error)
        {
            Fail("frame", error);
            return failures.ToImmutableArray();
        }
        var calls = execution.Points.Where(p => p.Kind == SwiftFramePointKind.Call).ToList();
        var exit = execution.Points.Single(p => p.Kind == SwiftFramePointKind.Exit);
        if (planned is not null && calls.Count == 0)
        {
            Fail("frame", "the bridge returns without calling anything");
            return failures.ToImmutableArray();
        }

        // The platform's rules. A function that calls builds the canonical frame: the frame record
        // at the top, so the frame pointer is CFA-16 at every call.
        foreach (var call in calls)
        {
            if (call.StackPointer % 16 != 0)
                Fail("alignment", $"sp is CFA{call.StackPointer:+0;-0} at the call at {call.Address:x}, not 16-aligned");
            if (call.FramePointer != -16)
                Fail("frame", $"the frame pointer is {(call.FramePointer is { } fp ? $"CFA{fp:+0;-0}" : "not set")} at the call at {call.Address:x}; the canonical frame record puts it at CFA-16");
        }
        foreach (var clobber in execution.Clobbers)
            Fail("callee-saved", clobber);
        var savedSet = (calls.Count > 0 ? calls[0] : exit).Saved;
        foreach (var (register, offset) in savedSet.Where(s => CalleeSaved(architecture).Contains(s.Key) || s.Key is "x29" or "x30" or "rbp"))
        {
            if (!execution.Restored.TryGetValue(register, out var from))
                Fail("callee-saved", $"{register} is saved at CFA{offset:+0;-0} but not restored before return");
            else if (from != offset)
                Fail("callee-saved", $"{register} is saved at CFA{offset:+0;-0} but restored from CFA{from:+0;-0}");
        }
        if (execution.StackPointerAtReturn != EntryStackPointer(architecture))
            Fail("callee-saved", $"sp is CFA{execution.StackPointerAtReturn:+0;-0} at return, not CFA{EntryStackPointer(architecture):+0;-0}");

        // Coverage: every instruction up to the exit is under the function's own unwind entry, and
        // that entry yields a rule there.
        var entryEncoding = encodingAt(address);
        foreach (var instruction in instructions.TakeWhile(i => i.Address <= exit.Address))
        {
            var here = encodingAt(instruction.Address);
            if (here != entryEncoding)
            {
                Fail("unwind", here is null
                    ? $"the unwind table ends before {instruction.Address:x}: the code from there has no unwind entry"
                    : $"the code at {instruction.Address:x} falls under another unwind entry ({here:x8}, not {entryEncoding:x8})");
                break;
            }
            if (Claim(architecture, address, here, fdes, instruction.Address, out var uncovered) is null)
            {
                Fail("unwind", instruction.Address == address ? uncovered! : $"at {instruction.Address:x}: {uncovered}");
                break;
            }
        }

        // The unwind information, at entry, at every call and at exit.
        int compared = 0;
        SwiftUnwindClaim? anyClaim = null;
        foreach (var point in execution.Points)
        {
            var claim = Claim(architecture, address, encodingAt(point.Address), fdes, point.Address, out _);
            if (claim is null)
                continue;
            anyClaim ??= claim;
            int? cfaAt = claim.CfaRegister switch
            {
                "x29" or "rbp" => point.FramePointer is { } fp ? -fp : null,
                "sp" or "rsp" => -point.StackPointer,
                _ => null,
            };
            // A rule on the frame pointer says nothing where the function has not set it.
            if (cfaAt is null && claim.CfaRegister is "x29" or "rbp")
                continue;
            compared++;
            var where = point.Kind switch
            {
                SwiftFramePointKind.Entry => $"at entry ({point.Address:x})",
                SwiftFramePointKind.Call => $"at the call at {point.Address:x}",
                _ => $"at exit ({point.Address:x})",
            };
            if (cfaAt != claim.CfaOffset)
            {
                Fail("unwind", $"{claim.Source} computes the CFA as {claim.CfaRegister}{claim.CfaOffset:+0;-0} {where}; the code puts the CFA at {claim.CfaRegister}{(cfaAt is { } a ? a.ToString("+0;-0", CultureInfo.InvariantCulture) : "?")}");
                continue;
            }
            foreach (var (register, offset) in claim.Saved)
            {
                if (point.Stack.TryGetValue(offset, out var held) && held == register)
                    continue;
                if (point.Saved.TryGetValue(register, out var actual) && actual != offset)
                    Fail("unwind", $"{claim.Source} restores {register} from CFA{offset:+0;-0} {where}; the code saves it at CFA{actual:+0;-0}");
                else
                    Fail("unwind", $"{claim.Source} restores {register} from CFA{offset:+0;-0} {where}, where the code holds nothing of it");
            }
            foreach (var register in point.Changed.Order(StringComparer.Ordinal))
            {
                if (!claim.Saved.ContainsKey(register))
                    Fail("unwind", $"the code has changed {register} {where}; {claim.Source} does not restore it");
            }
            if (point.Kind == SwiftFramePointKind.Call)
            {
                foreach (var (register, offset) in point.Saved)
                {
                    if (!claim.Saved.ContainsKey(register) && register != ReturnAddress(architecture))
                        Fail("unwind", $"the code saves {register} at CFA{offset:+0;-0}; {claim.Source} does not restore it");
                }
            }
        }
        if (anyClaim is not null && compared == 0)
            Fail("unwind", $"{anyClaim.Source} computes the CFA from {anyClaim.CfaRegister}, which the code never sets");

        // The plan.
        if (planned is not null)
        {
            var atCall = execution.AtCall!;
            var actualSaved = atCall.Saved.Keys.Where(r => r != ReturnAddress(architecture)).ToHashSet(StringComparer.Ordinal);
            var plannedSaved = planned.SavedRegisters.Where(r => r != ReturnAddress(architecture)).ToHashSet(StringComparer.Ordinal);
            if (architecture == SwiftArchitecture.X86_64)
                actualSaved.Remove("rip");
            if (!actualSaved.SetEquals(plannedSaved))
                Fail("plan", $"the code saves {{{string.Join(", ", actualSaved.Order(StringComparer.Ordinal))}}}; the plan saves {{{string.Join(", ", plannedSaved.Order(StringComparer.Ordinal))}}}");
            int expected = architecture == SwiftArchitecture.Arm64
                ? -(8 * planned.SavedRegisters.Length + planned.LocalBytes)
                : -(8 + 8 * planned.SavedRegisters.Length + planned.LocalBytes);
            if (atCall.StackPointer != expected)
                Fail("plan", $"sp is CFA{atCall.StackPointer:+0;-0} at the call; the plan's frame puts it at CFA{expected:+0;-0}");
        }
        return failures.ToImmutableArray();
    }

    private static string ReturnAddress(SwiftArchitecture architecture) => architecture == SwiftArchitecture.Arm64 ? "x30" : "rip";

    private static int EntryStackPointer(SwiftArchitecture architecture) => architecture == SwiftArchitecture.Arm64 ? 0 : -8;

    private static IReadOnlySet<string> CalleeSaved(SwiftArchitecture architecture) => architecture == SwiftArchitecture.Arm64
        ? Arm64CalleeSaved
        : X86CalleeSaved;

    private static readonly HashSet<string> Arm64CalleeSaved = new(StringComparer.Ordinal)
    {
        "x19", "x20", "x21", "x22", "x23", "x24", "x25", "x26", "x27", "x28", "x29",
        "d8", "d9", "d10", "d11", "d12", "d13", "d14", "d15",
    };

    private static readonly HashSet<string> X86CalleeSaved = new(StringComparer.Ordinal) { "rbx", "rbp", "r12", "r13", "r14", "r15" };

    // ---- the claim ----

    /// <summary>What the unwind information claims at <paramref name="pc"/>: the compact encoding decoded, or the DWARF row it defers to.</summary>
    public static SwiftUnwindClaim? Claim(SwiftArchitecture architecture, ulong address, uint? encoding, IReadOnlyList<SwiftFde> fdes, ulong pc, out string? error)
    {
        error = null;
        if (encoding is not { } value)
        {
            error = "the image has no unwind entry for the function";
            return null;
        }
        if (value == 0)
        {
            error = "the function's unwind entry is empty: it has no unwind information";
            return null;
        }
        uint mode = value & 0x0F000000;
        bool dwarf = architecture == SwiftArchitecture.Arm64 ? mode == 0x03000000 : mode == 0x04000000;
        if (dwarf)
        {
            ulong offset = value & 0x00FFFFFF;
            var fde = fdes.FirstOrDefault(f => f.Offset == offset);
            if (fde is null)
            {
                error = $"the encoding {value:x8} defers to the FDE at {offset:x}, which __eh_frame does not have";
                return null;
            }
            if (address < fde.Start || address >= fde.End || pc < fde.Start || pc >= fde.End)
            {
                error = $"the FDE at {offset:x} covers {fde.Start:x}..{fde.End:x}, not the function at {address:x}";
                return null;
            }
            var row = fde.Rows.LastOrDefault(r => r.Pc <= pc);
            if (row is null)
            {
                error = $"the FDE at {offset:x} has no row in effect at {pc:x}";
                return null;
            }
            return new SwiftUnwindClaim(row.CfaRegister, row.CfaOffset, row.Saved, $"the DWARF row at {row.Pc:x}");
        }

        // Frameless: the CFA is sp plus a fixed size. A frameless encoding that also saves registers
        // is not one the generator writes, and fails closed.
        if (mode == 0x02000000)
        {
            bool savesRegisters = architecture == SwiftArchitecture.Arm64
                ? (value & 0x00000FFF) != 0
                : (value & 0x0000FFFF) != 0;
            if (savesRegisters)
            {
                error = $"the encoding {value:x8} is a frameless one that saves registers, which the validator does not model";
                return null;
            }
            return architecture == SwiftArchitecture.Arm64
                ? new SwiftUnwindClaim("sp", (int)((value >> 12) & 0xFFF) * 16, ImmutableSortedDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal), $"the compact encoding {value:x8}")
                : new SwiftUnwindClaim("rsp", (int)((value >> 16) & 0xFF) * 8, ImmutableSortedDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal), $"the compact encoding {value:x8}");
        }

        if (architecture == SwiftArchitecture.Arm64)
        {
            if (mode != 0x04000000)
            {
                error = $"the encoding {value:x8} is not a frame-based or frameless one";
                return null;
            }
            var saved = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["x29"] = -16, ["x30"] = -8 };
            int location = -24;
            (uint Bit, string First, string Second)[] pairs =
            [
                (0x001, "x19", "x20"), (0x002, "x21", "x22"), (0x004, "x23", "x24"), (0x008, "x25", "x26"), (0x010, "x27", "x28"),
                (0x100, "d8", "d9"), (0x200, "d10", "d11"), (0x400, "d12", "d13"), (0x800, "d14", "d15"),
            ];
            foreach (var (bit, first, second) in pairs)
            {
                if ((value & bit) == 0)
                    continue;
                saved[first] = location;
                saved[second] = location - 8;
                location -= 16;
            }
            if ((value & 0x00FFF0E0) != 0)
            {
                error = $"the encoding {value:x8} sets bits the frame mode does not define";
                return null;
            }
            return new SwiftUnwindClaim("x29", 16, saved.ToImmutableSortedDictionary(StringComparer.Ordinal), $"the compact encoding {value:x8}");
        }
        else
        {
            if (mode != 0x01000000)
            {
                error = $"the encoding {value:x8} is not an rbp-frame or frameless one";
                return null;
            }
            int frameOffset = (int)((value >> 16) & 0xFF);
            uint registers = value & 0x7FFF;
            var saved = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["rbp"] = -16 };
            int location = -16 - 8 * frameOffset;
            for (int i = 0; i < 5; i++)
            {
                var register = ((registers >> (3 * i)) & 7) switch
                {
                    0 => null,
                    1 => "rbx",
                    2 => "r12",
                    3 => "r13",
                    4 => "r14",
                    5 => "r15",
                    6 => "rbp",
                    _ => "?",
                };
                if (register == "?")
                {
                    error = $"the encoding {value:x8} names an unknown register";
                    return null;
                }
                if (register is not null)
                    saved[register] = location;
                location += 8;
            }
            return new SwiftUnwindClaim("rbp", 16, saved.ToImmutableSortedDictionary(StringComparer.Ordinal), $"the compact encoding {value:x8}");
        }
    }

    // ---- the actual frame ----

    /// <summary>
    /// The frame a function builds, from its instructions: the state at its first call (null when it
    /// calls nothing), what it writes and restores, sp at return, <paramref name="Clobbers"/>, each way
    /// the caller's value of a callee-saved register (or the return address) is lost: written before a
    /// copy of it was saved, or not holding it again at return, and <paramref name="Points"/>, the frame
    /// at entry, at each call and at exit.
    /// </summary>
    public sealed record Execution(
        SwiftFrameState? AtCall, ulong CallAddress, ImmutableHashSet<string> Written,
        ImmutableDictionary<string, int> Restored, int StackPointerAtReturn, string? Error,
        ImmutableArray<string> Clobbers, ImmutableArray<SwiftFramePoint> Points);

    /// <summary>
    /// Executes the function symbolically from its entry, tracking sp and the frame pointer as CFA
    /// offsets and every save of a callee-saved register, through its calls to its exit: a return, or
    /// an unconditional tail branch. The code is straight-line; a conditional branch fails closed, as
    /// does any write of sp or the frame pointer outside the forms the generator writes. Each
    /// callee-saved register is followed in order: whether it still holds the caller's value, and
    /// which stack slots hold a copy of that value.
    /// </summary>
    public static Execution Execute(SwiftArchitecture architecture, IReadOnlyList<SwiftInstruction> instructions)
    {
        bool arm = architecture == SwiftArchitecture.Arm64;
        int sp = EntryStackPointer(architecture);
        int? fp = null;
        var saved = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var stack = new Dictionary<int, string>();
        if (!arm)
        {
            saved["rip"] = -8;
            stack[-8] = "rip";
        }
        var written = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var restored = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        var clobbers = new List<string>();
        var points = ImmutableArray.CreateBuilder<SwiftFramePoint>();
        SwiftFrameState? atCall = null;
        ulong callAddress = 0;
        Execution Fail(SwiftInstruction at, string why) =>
            new(null, 0, written.ToImmutable(), restored.ToImmutable(), sp, $"{why} at {at}", clobbers.ToImmutableArray(), points.ToImmutable());

        // The registers whose caller's value must survive: the callee-saved ones and, on arm64, the
        // return address. A stack slot names the register whose caller's value it holds; a copy of a
        // value the function made itself is marked as such, and restores nothing.
        var preserved = CalleeSaved(architecture);
        var tracked = arm ? preserved.Append(ReturnAddress(architecture)).ToHashSet(StringComparer.Ordinal) : preserved.ToHashSet(StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        const string NotTheCallers = "~";
        void Save(string register, int offset)
        {
            bool callers = !changed.Contains(register);
            if ((preserved.Contains(register) || register == ReturnAddress(architecture)) && callers && !saved.ContainsKey(register) && atCall is null)
                saved[register] = offset;
            stack[offset] = callers ? register : NotTheCallers + register;
        }
        void Write(string register, SwiftInstruction at)
        {
            written.Add(register);
            if (!tracked.Contains(register))
                return;
            if (!changed.Contains(register) && !stack.ContainsValue(register))
                clobbers.Add($"{register} is written at {at.Address:x} before the caller's value is saved");
            changed.Add(register);
        }
        void Load(string register, int slot, SwiftInstruction at)
        {
            if (stack.TryGetValue(slot, out var held) && held == register)
            {
                restored[register] = slot;
                changed.Remove(register);
            }
            else
                Write(register, at);
        }
        void Point(SwiftFramePointKind kind, ulong at) => points.Add(new SwiftFramePoint(
            kind, at, sp, fp, saved.ToImmutableSortedDictionary(StringComparer.Ordinal),
            stack.ToImmutableDictionary(), changed.ToImmutableHashSet(StringComparer.Ordinal)));
        Execution Returned(SwiftInstruction at)
        {
            Point(SwiftFramePointKind.Exit, at.Address);
            foreach (var register in changed.Order(StringComparer.Ordinal))
                clobbers.Add($"{register} does not hold the caller's value at return");
            return new(atCall, callAddress, written.ToImmutable(), restored.ToImmutable(), sp, null, clobbers.ToImmutableArray(), points.ToImmutable());
        }

        if (instructions.Count > 0)
            Point(SwiftFramePointKind.Entry, instructions[0].Address);
        SwiftInstruction? previous = null;
        foreach (var instruction in instructions)
        {
            // arm64 faults on any sp-based access while sp is misaligned, so sp stays 16-aligned
            // throughout, not only at the call.
            if (arm && sp % 16 != 0)
                return Fail(previous!, $"sp is CFA{sp:+0;-0}, not 16-aligned,");
            previous = instruction;
            var m = instruction.Mnemonic;
            var ops = instruction.Operands;
            bool isCall = arm ? m is "bl" or "blr" : m is "callq" or "call";
            bool isReturn = arm ? m == "ret" : m is "retq" or "ret";
            if (isCall)
            {
                Point(SwiftFramePointKind.Call, instruction.Address);
                if (atCall is null)
                {
                    atCall = new SwiftFrameState(sp, fp, saved.ToImmutableSortedDictionary(StringComparer.Ordinal));
                    callAddress = instruction.Address;
                }
                // A call leaves the callee-saved registers as it found them; on arm64 it replaces the return address.
                if (arm)
                    Write(ReturnAddress(architecture), instruction);
                continue;
            }
            // An unconditional branch away is a tail call: the function's exit, which must leave the
            // frame exactly as it found it. A conditional one is not modelled.
            bool isTailBranch = arm ? m is "b" or "br" or "braa" or "braaz" or "brab" or "brabz" : m is "jmp" or "jmpq";
            bool isConditional = arm
                ? m.StartsWith("b.", StringComparison.Ordinal) || m.StartsWith("bc.", StringComparison.Ordinal)
                    || m is "cbz" or "cbnz" or "tbz" or "tbnz"
                : m.StartsWith('j') && !isTailBranch;
            if (isReturn || isTailBranch)
                return Returned(instruction);
            if (isConditional)
                return Fail(instruction, "a conditional branch, which the straight-line model does not follow,");

            if (arm)
            {
                var regs = ops.Select(Arm64Register).ToList();
                switch (m)
                {
                    case "stp" or "str" or "stur" when ops.Length >= 2 && ParseMemory(ops, out var memory) && memory.Base == "x29":
                    {
                        // A store into the frame, addressed from the frame pointer.
                        if (memory.PreIndex || memory.PostIndex || fp is not { } record)
                            return Fail(instruction, "an unmodelled store through the frame pointer");
                        Save(regs[0]!, record + memory.Offset);
                        if (m == "stp")
                            Save(regs[1]!, record + memory.Offset + 8);
                        continue;
                    }
                    case "stp" or "str" or "stur" when ops.Length >= 2 && ParseMemory(ops, out var memory):
                    {
                        if (memory.Base != "sp")
                            break;
                        int address = sp + memory.Offset;
                        if (memory.PreIndex)
                            sp += memory.Offset;
                        int at = memory.PreIndex ? sp : address;
                        if (m == "stp")
                        {
                            Save(regs[0]!, at);
                            Save(regs[1]!, at + 8);
                        }
                        else
                            Save(regs[0]!, at);
                        if (memory.PostIndex)
                            sp += memory.PostOffset;
                        continue;
                    }
                    case "ldp" or "ldr" when ops.Length >= 2 && ParseMemory(ops, out var memory) && memory.Base == "sp":
                    {
                        int at = sp + memory.Offset;
                        if (memory.PreIndex)
                            sp += memory.Offset;
                        var destinations = m == "ldp" ? new[] { regs[0]!, regs[1]! } : new[] { regs[0]! };
                        for (int k = 0; k < destinations.Length; k++)
                            Load(destinations[k], at + 8 * k, instruction);
                        // Reloading the frame pointer ends this function's frame record.
                        if (destinations.Contains("x29"))
                            fp = null;
                        if (memory.PostIndex)
                            sp += memory.PostOffset;
                        continue;
                    }
                    case "sub" or "add" when regs.Count == 3 && regs[0] == "sp" && regs[1] == "sp" && Immediate(ops[2]) is { } n:
                        sp += m == "sub" ? -n : n;
                        continue;
                    case "mov" when regs.Count == 2 && regs[0] == "x29" && regs[1] == "sp":
                        Write("x29", instruction);
                        fp = sp;
                        continue;
                    case "add" when regs.Count == 3 && regs[0] == "x29" && regs[1] == "sp" && Immediate(ops[2]) is { } n:
                        Write("x29", instruction);
                        fp = sp + n;
                        continue;
                    case "mov" when regs.Count == 2 && regs[0] == "sp" && regs[1] == "x29" && fp is { } frame:
                        sp = frame;
                        continue;
                    case "sub" when regs.Count == 3 && regs[0] == "sp" && regs[1] == "x29" && Immediate(ops[2]) is { } n && fp is { } frame:
                        sp = frame - n;
                        continue;
                }
                // Anything else: what it writes.
                if (Arm64Destinations(m, regs) is { } destinationsWritten)
                {
                    foreach (var destination in destinationsWritten)
                    {
                        if (destination is "sp" or "x29")
                            return Fail(instruction, $"an unmodelled write of {destination}");
                        Write(destination, instruction);
                    }
                }
            }
            else
            {
                var regs = ops.Select(X86Register).ToList();
                switch (m)
                {
                    case "pushq" when regs.Count == 1 && regs[0] is { } pushed:
                        sp -= 8;
                        Save(pushed, sp);
                        continue;
                    case "popq" when regs.Count == 1 && regs[0] is { } popped:
                        if (popped == "rsp")
                            return Fail(instruction, "an unmodelled write of rsp");
                        Load(popped, sp, instruction);
                        if (popped == "rbp")
                            fp = null;
                        sp += 8;
                        continue;
                    case "movq" when regs.Count == 2 && regs[0] == "rsp" && regs[1] == "rbp":
                        Write("rbp", instruction);
                        fp = sp;
                        continue;
                    case "movq" when regs.Count == 2 && regs[0] == "rbp" && regs[1] == "rsp" && fp is { } frame:
                        sp = frame;
                        continue;
                    case "subq" or "addq" when regs.Count == 2 && regs[1] == "rsp" && ops[0].StartsWith('$') && Immediate(ops[0][1..]) is { } n:
                        sp += m == "subq" ? -n : n;
                        continue;
                    case "leaq" when regs.Count == 2 && regs[1] == "rsp" && X86Memory(ops[0]) is { Base: "rbp" } memory && fp is { } frame:
                        sp = frame + memory.Offset;
                        continue;
                    case "movq" when regs.Count == 2 && regs[0] is { } stored && X86Memory(ops[1]) is { Base: "rsp" } memory:
                        Save(stored, sp + memory.Offset);
                        continue;
                    case "movq" when regs.Count == 2 && regs[0] is { } stored && X86Memory(ops[1]) is { Base: "rbp" } memory:
                        // A store into the frame, addressed from the frame pointer.
                        if (fp is not { } rbpFrame)
                            return Fail(instruction, "an unmodelled store through rbp");
                        Save(stored, rbpFrame + memory.Offset);
                        continue;
                }
                if (X86Destination(m, ops, regs) is { } destination)
                {
                    if (destination is "rsp" or "rbp")
                        return Fail(instruction, $"an unmodelled write of {destination}");
                    Write(destination, instruction);
                }
            }
        }
        return new(atCall, callAddress, written.ToImmutable(), restored.ToImmutable(), sp, "the function does not return", clobbers.ToImmutableArray(), points.ToImmutable());
    }

    private readonly record struct Memory(string Base, int Offset, bool PreIndex, bool PostIndex, int PostOffset);

    /// <summary>An arm64 memory operand <c>[base, #imm]</c>, <c>[base, #imm]!</c> or <c>[base], #imm</c>.</summary>
    private static bool ParseMemory(ImmutableArray<string> all, out Memory memory)
    {
        memory = default;
        // A post-index puts the immediate after the bracket as its own operand.
        int bracket = -1;
        for (int i = 0; i < all.Length; i++)
            if (all[i].StartsWith('['))
            {
                bracket = i;
                break;
            }
        if (bracket < 0)
            return false;
        var text = all[bracket];
        bool pre = text.EndsWith('!');
        var inner = text.TrimEnd('!').TrimStart('[').TrimEnd(']');
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        var baseRegister = Arm64Register(parts[0]);
        if (baseRegister is null)
            return false;
        int offset = parts.Length > 1 && Immediate(parts[1]) is { } n ? n : 0;
        bool post = bracket + 1 < all.Length && Immediate(all[bracket + 1]) is not null;
        memory = new Memory(baseRegister, offset, pre, post, post ? Immediate(all[bracket + 1])!.Value : 0);
        return true;
    }

    private static int? Immediate(string text)
    {
        var t = text.Trim().TrimStart('#', '$');
        bool negative = t.StartsWith('-');
        if (negative)
            t = t[1..];
        int value;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return null;
        }
        else if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return null;
        return negative ? -value : value;
    }

    /// <summary>The canonical name of an arm64 register operand (<c>w19</c> is <c>x19</c>, <c>s8</c> is <c>d8</c>), or null.</summary>
    private static string? Arm64Register(string operand)
    {
        var t = operand.Trim().ToLowerInvariant();
        if (t is "sp" or "wsp")
            return "sp";
        if (t == "fp")
            return "x29";
        if (t == "lr")
            return "x30";
        if (t is "xzr" or "wzr")
            return "zr";
        if (t.Length >= 2 && t[0] is 'x' or 'w' && int.TryParse(t[1..], out var g))
            return "x" + g.ToString(CultureInfo.InvariantCulture);
        if (t.Length >= 2 && t[0] is 'd' or 's' or 'h' or 'b' or 'q' or 'v' && int.TryParse(t[1..].Split('.')[0], out var f))
            return "d" + f.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>The registers an arm64 instruction writes, or null when it writes none (a store, a compare, a branch).</summary>
    private static IEnumerable<string>? Arm64Destinations(string mnemonic, List<string?> registers)
    {
        if (mnemonic.StartsWith("st", StringComparison.Ordinal) || mnemonic is "cmp" or "cmn" or "tst" or "nop" or "ret"
            || IsArm64BranchOrHint(mnemonic) || mnemonic.StartsWith("cb", StringComparison.Ordinal) || mnemonic.StartsWith("tb", StringComparison.Ordinal))
            return null;
        if (registers.Count == 0 || registers[0] is null)
            return null;
        var list = new List<string> { registers[0]! };
        if (mnemonic == "ldp" && registers.Count > 1 && registers[1] is { } second)
            list.Add(second);
        return list.Where(r => r != "zr");
    }

    /// <summary>
    /// An arm64 branch (<c>b</c>, <c>b.cond</c>, <c>bc.cond</c>, <c>br</c>, <c>bl</c>, <c>blr</c> and their
    /// authenticated forms) or a branch-target or breakpoint hint, none of which writes a register it
    /// names; the bitfield and logical instructions that also start with "b" (<c>bfi</c>, <c>bic</c>,
    /// <c>bsl</c>...) write their first operand.
    /// </summary>
    private static bool IsArm64BranchOrHint(string mnemonic) =>
        mnemonic is "b" or "bl" or "bti" or "brk"
        || mnemonic.StartsWith("b.", StringComparison.Ordinal) || mnemonic.StartsWith("bc.", StringComparison.Ordinal)
        || mnemonic is "br" or "braa" or "braaz" or "brab" or "brabz"
        || mnemonic is "blr" or "blraa" or "blraaz" or "blrab" or "blrabz";

    /// <summary>The canonical 64-bit name of an x86_64 register operand, or null.</summary>
    private static string? X86Register(string operand)
    {
        var t = operand.Trim();
        if (!t.StartsWith('%'))
            return null;
        t = t[1..].ToLowerInvariant();
        return t switch
        {
            "rax" or "eax" or "ax" or "al" or "ah" => "rax",
            "rbx" or "ebx" or "bx" or "bl" or "bh" => "rbx",
            "rcx" or "ecx" or "cx" or "cl" or "ch" => "rcx",
            "rdx" or "edx" or "dx" or "dl" or "dh" => "rdx",
            "rsi" or "esi" or "si" or "sil" => "rsi",
            "rdi" or "edi" or "di" or "dil" => "rdi",
            "rbp" or "ebp" or "bp" or "bpl" => "rbp",
            "rsp" or "esp" or "sp" or "spl" => "rsp",
            _ when t.StartsWith('r') && t.Length >= 2 && char.IsDigit(t[1]) => "r" + new string(t[1..].TakeWhile(char.IsDigit).ToArray()),
            _ when t.StartsWith("xmm", StringComparison.Ordinal) => t,
            _ => null,
        };
    }

    private static Memory? X86Memory(string operand)
    {
        var match = Regex.Match(operand.Trim(), @"^(?<offset>-?(?:0x[0-9a-fA-F]+|\d+))?\(%(?<base>\w+)\)$");
        if (!match.Success)
            return null;
        var baseRegister = X86Register("%" + match.Groups["base"].Value);
        if (baseRegister is null)
            return null;
        int offset = match.Groups["offset"].Success ? Immediate(match.Groups["offset"].Value) ?? 0 : 0;
        return new Memory(baseRegister, offset, false, false, 0);
    }

    /// <summary>The register an x86_64 (AT&amp;T) instruction writes, or null: its last operand, when that is a register and the instruction writes it.</summary>
    private static string? X86Destination(string mnemonic, ImmutableArray<string> operands, List<string?> registers)
    {
        if (operands.Length == 0 || mnemonic.StartsWith("cmp", StringComparison.Ordinal) || mnemonic.StartsWith("test", StringComparison.Ordinal)
            || mnemonic.StartsWith('j') || mnemonic.StartsWith("push", StringComparison.Ordinal) || mnemonic.StartsWith("nop", StringComparison.Ordinal))
            return null;
        return registers[^1];
    }

    // ---- reading the tools' output ----

    /// <summary>The undefined symbols <c>nm -m</c> lists, and whether each is weak.</summary>
    public static IEnumerable<(string Symbol, bool Weak)> UndefinedSymbols(string nmOutput)
    {
        foreach (var raw in nmOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("(undefined)", StringComparison.Ordinal))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var symbol = parts.FirstOrDefault(p => p.StartsWith('_')) ?? parts[^1];
            yield return (symbol, line.Contains(" weak ", StringComparison.Ordinal));
        }
    }

    /// <summary>Each defined text symbol of <c>nm --defined-only</c> and its address.</summary>
    public static Dictionary<string, ulong> DefinedSymbols(string nmOutput)
    {
        var symbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(nmOutput, @"^([0-9a-f]+) [Tt] (\S+)$", RegexOptions.Multiline))
            symbols[match.Groups[2].Value] = ulong.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return symbols;
    }

    /// <summary>
    /// The encoding in effect at <paramref name="address"/>: the entry with the greatest function
    /// offset at or below it, as the unwinder looks it up. The linker merges a run of functions with
    /// the same encoding into the entry of the first, so a function can have no entry of its own.
    /// </summary>
    public static uint? EntryFor(IReadOnlyDictionary<ulong, uint> entries, ulong address)
    {
        ulong? best = null;
        foreach (var offset in entries.Keys)
            if (offset <= address && (best is null || offset > best))
                best = offset;
        return best is { } found ? entries[found] : null;
    }

    /// <summary>Each second-level entry of <c>llvm-objdump --unwind-info</c>: function offset to encoding.</summary>
    public static Dictionary<ulong, uint> UnwindEntries(string objdumpOutput)
    {
        var entries = new Dictionary<ulong, uint>();
        foreach (Match match in Regex.Matches(objdumpOutput, @"function offset=0x([0-9a-f]+), encoding(?:\[\d+\])?=0x([0-9a-f]+)"))
            entries[ulong.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] =
                uint.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return entries;
    }

    /// <summary>
    /// The end of the code <c>llvm-objdump --unwind-info</c> describes: the last top-level index, a
    /// sentinel the linker writes past the last function, at and beyond which the unwinder finds no
    /// entry. Null when the output lists no index.
    /// </summary>
    public static ulong? UnwindTableEnd(string objdumpOutput)
    {
        ulong? end = null;
        foreach (Match match in Regex.Matches(objdumpOutput, @"\[\d+\]: function offset=0x([0-9a-f]+), 2nd level page offset="))
            end = ulong.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return end;
    }

    /// <summary>The FDEs of <c>llvm-dwarfdump --eh-frame</c>, with their evaluated rows.</summary>
    public static ImmutableArray<SwiftFde> Fdes(string dwarfdumpOutput, SwiftArchitecture architecture)
    {
        var fdes = ImmutableArray.CreateBuilder<SwiftFde>();
        ulong offset = 0, start = 0, end = 0;
        List<SwiftCfaRow>? rows = null;
        void Flush()
        {
            if (rows is not null)
                fdes.Add(new SwiftFde(offset, start, end, rows.ToImmutableArray()));
            rows = null;
        }
        foreach (var raw in dwarfdumpOutput.Split('\n'))
        {
            var header = Regex.Match(raw, @"^([0-9a-f]{8}) [0-9a-f]+ [0-9a-f]+ (FDE|CIE)(?:.*pc=([0-9a-f]+)\.\.\.([0-9a-f]+))?");
            if (header.Success)
            {
                Flush();
                if (header.Groups[2].Value == "FDE")
                {
                    offset = ulong.Parse(header.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    start = ulong.Parse(header.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    end = ulong.Parse(header.Groups[4].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    rows = new List<SwiftCfaRow>();
                }
                continue;
            }
            var row = Regex.Match(raw, @"^\s+0x([0-9a-f]+): CFA=([A-Za-z0-9]+)([+-]\d+)?(?:: (.*))?$");
            if (row.Success && rows is not null)
            {
                var saved = new SortedDictionary<string, int>(StringComparer.Ordinal);
                if (row.Groups[4].Success)
                {
                    foreach (Match rule in Regex.Matches(row.Groups[4].Value, @"([A-Za-z0-9]+)=\[CFA([+-]\d+)\]"))
                        saved[DwarfRegister(rule.Groups[1].Value, architecture)] = int.Parse(rule.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                rows.Add(new SwiftCfaRow(
                    ulong.Parse(row.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    DwarfRegister(row.Groups[2].Value, architecture),
                    row.Groups[3].Success ? int.Parse(row.Groups[3].Value, CultureInfo.InvariantCulture) : 0,
                    saved.ToImmutableSortedDictionary(StringComparer.Ordinal)));
            }
        }
        Flush();
        return fdes.ToImmutable();
    }

    private static string DwarfRegister(string name, SwiftArchitecture architecture)
    {
        var lower = name.ToLowerInvariant();
        if (architecture == SwiftArchitecture.Arm64)
            return Arm64Register(lower) ?? lower;
        return X86Register("%" + lower) ?? lower;
    }

    /// <summary>The instructions of each function of <c>llvm-objdump -d --no-show-raw-insn</c>.</summary>
    public static Dictionary<string, List<SwiftInstruction>> Disassembly(string objdumpOutput)
    {
        var functions = new Dictionary<string, List<SwiftInstruction>>(StringComparer.Ordinal);
        List<SwiftInstruction>? current = null;
        foreach (var raw in objdumpOutput.Split('\n'))
        {
            var header = Regex.Match(raw, @"^[0-9a-f]+ <([^>]+)>:");
            if (header.Success)
            {
                current = new List<SwiftInstruction>();
                functions[header.Groups[1].Value] = current;
                continue;
            }
            if (raw.StartsWith("Disassembly of section", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }
            var line = Regex.Match(raw, @"^\s+([0-9a-f]+):\s+(\S+)\s*(.*?)\s*$");
            if (!line.Success || current is null)
                continue;
            var operandText = line.Groups[3].Value;
            int comment = operandText.IndexOfAny(['<', ';']);
            if (comment >= 0 && !operandText[..comment].Contains('['))
                operandText = operandText[..comment];
            int hash = operandText.IndexOf("##", StringComparison.Ordinal);
            if (hash >= 0)
                operandText = operandText[..hash];
            current.Add(new SwiftInstruction(
                ulong.Parse(line.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                line.Groups[2].Value,
                SplitOperands(operandText.Trim())));
        }
        return functions;
    }

    private static ImmutableArray<string> SplitOperands(string text)
    {
        var parts = ImmutableArray.CreateBuilder<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '[' or '(' or '{')
                depth++;
            else if (text[i] is ']' or ')' or '}')
                depth--;
            else if (text[i] == ',' && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        if (text.Length > start && text[start..].Trim() is { Length: > 0 } last)
            parts.Add(last);
        return parts.ToImmutable();
    }
}
