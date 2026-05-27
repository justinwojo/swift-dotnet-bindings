// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Describes a native thunk to be generated. Arch-neutral: the same descriptor drives both the
/// ARM64 and x86_64 targets, which map the slots and counts onto their own register files.
/// </summary>
/// <param name="ThunkSymbol">The C-level symbol name for the thunk (e.g., "thunk_Module_a1b2c3d4"). Assembly emitter adds platform prefix.</param>
/// <param name="SwiftSymbol">The target Swift symbol to call (with underscore prefix, e.g., "_$s6Module6methodyyF").</param>
/// <param name="ReturnLowering">How the return type maps to register slots. Null for void returns.</param>
/// <param name="SelfLowering">How the self parameter maps to registers. Null for free functions and constructors.</param>
/// <param name="ParameterCount">Number of cdecl integer register parameters (excluding self, error out pointer).</param>
/// <param name="FloatParameterCount">Number of cdecl float register parameters.</param>
/// <param name="IsInstanceMethod">True if this is an instance method (self in the swiftself register: ARM64 x20 / SysV r13).</param>
/// <param name="IsStaticMethod">True if this is a static method (metatype in the swiftself register).</param>
/// <param name="IsConstructor">True if this is a constructor (metatype in the swiftself register, allocating init).</param>
/// <param name="Throws">True if the function can throw (swifterror register: ARM64 x21 / SysV r12).</param>
/// <param name="MetadataAccessorSymbol">The metadata accessor symbol for constructors/static methods (with underscore prefix).</param>
public record ThunkDescriptor(
    string ThunkSymbol,
    string SwiftSymbol,
    TypeLoweringResult? ReturnLowering,
    TypeLoweringResult? SelfLowering,
    int ParameterCount,
    int FloatParameterCount,
    bool IsInstanceMethod,
    bool IsStaticMethod,
    bool IsConstructor,
    bool Throws,
    string? MetadataAccessorSymbol);

/// <summary>
/// Composes native assembly thunk functions that bridge cdecl → swiftcc calling conventions.
///
/// These thunks allow C# P/Invoke (which uses cdecl) to call Swift functions using their
/// native calling convention without @_cdecl wrappers or CallConvSwift runtime support.
///
/// This class owns the arch-neutral decisions — symbol naming, tail-call vs. full-frame
/// classification, and whether a return needs bridging — and delegates the actual register
/// names, frame layout, and instruction mnemonics to a <see cref="ThunkTargetArch"/>
/// (<see cref="Arm64ThunkTarget"/> or <see cref="SysVThunkTarget"/>).
/// </summary>
public static class ThunkAssemblyEmitter
{
    /// <summary>
    /// Emits the ARM64 assembly file header. Back-compat overload for callers that have not
    /// yet been parameterized by architecture; new multi-arch callers use
    /// <see cref="ThunkTargetArch.EmitFileHeader"/> on a specific target.
    /// </summary>
    /// <param name="moduleName">The Swift module name for the comment header.</param>
    /// <returns>Assembly header text.</returns>
    public static string EmitFileHeader(string moduleName)
    {
        var sb = new StringBuilder();
        ThunkTargetArch.Arm64.EmitFileHeader(sb, moduleName);
        return sb.ToString();
    }

    /// <summary>
    /// Emits the assembly file footer (currently empty, reserved for future use).
    /// </summary>
    /// <returns>Assembly footer text.</returns>
    public static string EmitFileFooter()
    {
        return ThunkTargetArch.Arm64.EmitFileFooter();
    }

    /// <summary>
    /// Emits a single ARM64 thunk function from a ThunkDescriptor. Back-compat overload that
    /// targets ARM64; use <see cref="EmitThunk(ThunkDescriptor, ThunkTargetArch)"/> for other
    /// architectures.
    /// </summary>
    /// <param name="descriptor">The thunk descriptor with all metadata needed for code generation.</param>
    /// <returns>The complete assembly text for this thunk function.</returns>
    public static string EmitThunk(ThunkDescriptor descriptor) =>
        EmitThunk(descriptor, ThunkTargetArch.Arm64);

    /// <summary>
    /// Emits a single thunk function for a specific architecture. The classification
    /// (tail-call vs. full-frame) is arch-neutral; the chosen <paramref name="target"/>
    /// renders the symbol declaration and body.
    /// </summary>
    /// <param name="descriptor">The thunk descriptor with all metadata needed for code generation.</param>
    /// <param name="target">The architecture target that renders the assembly.</param>
    /// <returns>The complete assembly text for this thunk function.</returns>
    public static string EmitThunk(ThunkDescriptor descriptor, ThunkTargetArch target)
    {
        var sb = new StringBuilder();

        // Classify the thunk to select the optimal template (arch-neutral decision).
        var classification = ClassifyThunk(descriptor);

        target.EmitSymbolDecl(sb, descriptor.ThunkSymbol);

        switch (classification)
        {
            case ThunkKind.TailCall:
                target.EmitTailCall(sb, descriptor);
                break;
            case ThunkKind.FullFrame:
                target.EmitFullFrame(sb, descriptor);
                break;
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Classifies a thunk to determine which template to use.
    /// </summary>
    private enum ThunkKind
    {
        /// <summary>Tail call — zero overhead, no frame needed.</summary>
        TailCall,
        /// <summary>Full frame with callee-saved registers.</summary>
        FullFrame,
    }

    /// <summary>
    /// Determines which template to use based on the descriptor. Arch-neutral: the same
    /// bridging conditions (self, metatype, swifterror, large struct return) drive the choice
    /// on every architecture.
    /// </summary>
    private static ThunkKind ClassifyThunk(ThunkDescriptor descriptor)
    {
        bool needsReturnBridge = NeedsReturnBridge(descriptor);
        bool needsSelfBridge = descriptor.IsInstanceMethod;
        bool needsMetatype = descriptor.IsConstructor || descriptor.IsStaticMethod;
        bool needsErrorBridge = descriptor.Throws;

        // Tail call: no bridging needed at all — parameters and return are identical
        // in both cdecl and swiftcc, so just branch directly
        if (!needsReturnBridge && !needsSelfBridge && !needsMetatype && !needsErrorBridge)
            return ThunkKind.TailCall;

        // Full frame: needs to bridge a return buffer / error-out pointer and/or self/metatype
        return ThunkKind.FullFrame;
    }

    /// <summary>
    /// Determines whether the return value needs bridging: Swift returns a 17-32 byte struct
    /// directly in registers, while cdecl returns it indirectly via a pointer (ARM64 x8 /
    /// SysV %rdi sret). Arch-neutral — both architectures share the size thresholds.
    /// </summary>
    internal static bool NeedsReturnBridge(ThunkDescriptor descriptor)
    {
        if (descriptor.ReturnLowering == null)
            return false;

        // If indirect, both conventions use the indirect pointer — no bridge needed
        if (descriptor.ReturnLowering.IsIndirect)
            return false;

        // If total byte size > 16, cdecl uses the indirect pointer but Swift uses registers — bridge needed
        if (descriptor.ReturnLowering.TotalByteSize > 16)
            return true;

        // ≤ 16 bytes: swiftcc and cdecl agree on the return registers for every shape the thunk is
        // allowed to reach here — single-slot returns, all-integer packs, and homogeneous floating-point
        // aggregates whose fields each own a full eightbyte ({Double, Double}).
        // NativeThunkEmitter.SmallStructReturnDivergesFromCAbi declines every shape that diverges on
        // either target ABI (any int/float mix, two packed floats, or a non-HFA aggregate containing a
        // float — which arm64 AAPCS64 returns in GPRs) to the @_cdecl wrapper, so no repacking bridge is
        // needed for what reaches here.
        return false;
    }

    /// <summary>
    /// Walks a return type's register slots in field order, pairing each with the byte offset it
    /// occupies in the cdecl return buffer. Offsets follow natural C/Swift struct alignment (each
    /// field aligned to its own size), which matches the <c>[StructLayout(Sequential)]</c> layout the
    /// generated C# struct uses — so the thunk's field-wise stores land exactly where the managed
    /// side reads them. Arch-neutral; each backend chooses the width-correct store instruction.
    /// </summary>
    internal static IEnumerable<(RegisterSlot Slot, int Offset)> ReturnBufferSlots(TypeLoweringResult returnLowering)
    {
        int offset = 0;
        foreach (var slot in returnLowering.Slots)
        {
            int size = slot.ByteSize > 0 ? slot.ByteSize : 8;
            offset = AlignUp(offset, size);
            yield return (slot, offset);
            offset += size;
        }
    }

    /// <summary>Rounds <paramref name="offset"/> up to a multiple of the power-of-two <paramref name="alignment"/>.</summary>
    private static int AlignUp(int offset, int alignment) => (offset + (alignment - 1)) & ~(alignment - 1);

    /// <summary>
    /// Generates a unique thunk symbol name from a module name and method mangled name.
    /// Uses a hash of the mangled name for uniqueness while keeping the symbol readable.
    /// </summary>
    /// <param name="moduleName">The Swift module name.</param>
    /// <param name="mangledName">The Swift mangled name of the method.</param>
    /// <returns>The thunk C symbol name (e.g., "thunk_Module_a1b2c3d4"). No platform prefix — assembly emitter adds it.</returns>
    public static string GenerateThunkSymbol(string moduleName, string mangledName)
    {
        // Use a stable hash of the full mangled name for uniqueness
        var hash = ComputeStableHash(mangledName);
        var sanitizedModule = SanitizeForSymbol(moduleName);
        return $"thunk_{sanitizedModule}_{hash:x8}";
    }

    /// <summary>
    /// Computes a stable 32-bit hash for symbol generation.
    /// Uses FNV-1a for good distribution and determinism.
    /// </summary>
    private static uint ComputeStableHash(string input)
    {
        const uint fnvPrime = 0x01000193;
        const uint fnvOffset = 0x811c9dc5;

        uint hash = fnvOffset;
        foreach (char c in input)
        {
            hash ^= c;
            hash *= fnvPrime;
        }
        return hash;
    }

    /// <summary>
    /// Sanitizes a module name for use in assembly symbols (alphanumeric + underscore only).
    /// </summary>
    private static string SanitizeForSymbol(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.ToString();
    }
}
