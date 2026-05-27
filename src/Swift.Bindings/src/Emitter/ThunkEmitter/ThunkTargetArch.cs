// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// The native architecture a thunk is emitted for. The thunk bridges cdecl → swiftcc;
/// the descriptor (slots, parameter counts, flags) is arch-neutral, and each
/// <see cref="ThunkTargetArch"/> renders it into that architecture's assembly.
/// </summary>
public enum ThunkArch
{
    /// <summary>ARM64 / AAPCS64 (Apple arm64, arm64e).</summary>
    Arm64,
    /// <summary>x86_64 / SysV AMD64 with Swift's swiftcc extensions (Intel macOS, simulators, Catalyst).</summary>
    X86_64,
}

/// <summary>
/// Renders an arch-neutral <see cref="ThunkDescriptor"/> into a specific architecture's
/// assembly. Register names, the indirect-return-pointer convention, frame layout, and
/// instruction mnemonics live behind this abstraction; the bridging decisions
/// (tail-call vs. full-frame, whether a return needs bridging) are arch-neutral and stay
/// in <see cref="ThunkAssemblyEmitter"/>.
///
/// The ARM64 and x86_64 full-frame templates are NOT identical: ARM64's indirect-return
/// pointer uses the dedicated x8 register (separate from the x0-x7 argument sequence),
/// while x86_64 SysV passes it as a hidden first integer argument (rdi), shifting every
/// other integer argument down one register. That structural difference is why each
/// architecture owns its full-frame body rather than sharing one template behind
/// register-name helpers.
/// </summary>
public abstract class ThunkTargetArch
{
    /// <summary>Shared ARM64 target instance.</summary>
    public static ThunkTargetArch Arm64 { get; } = new Arm64ThunkTarget();

    /// <summary>Shared x86_64 (SysV AMD64) target instance.</summary>
    public static ThunkTargetArch X86_64 { get; } = new SysVThunkTarget();

    /// <summary>Resolves the shared target instance for an architecture.</summary>
    public static ThunkTargetArch For(ThunkArch arch) =>
        arch == ThunkArch.X86_64 ? X86_64 : Arm64;

    /// <summary>
    /// Architecture tag used in the generated assembly file extension (<c>{ns}.{tag}.s</c>)
    /// and in the file-header comment. "arm64" or "x86_64".
    /// </summary>
    public abstract string ArchTag { get; }

    /// <summary>
    /// Whether this target can encode a thunk for the given descriptor. ARM64 always can
    /// (the indirect-return pointer lives outside the argument registers); x86_64 declines
    /// shapes whose arguments would spill past the integer/float register files, leaving the
    /// caller to fall back to an @_cdecl wrapper. Returning false here is not an error — the
    /// thunk is simply not emitted for this architecture.
    /// </summary>
    public virtual bool CanEmit(ThunkDescriptor descriptor) => true;

    /// <summary>Emits the assembly file header (section/alignment directives + comment).</summary>
    public abstract void EmitFileHeader(StringBuilder sb, string moduleName);

    /// <summary>Emits the assembly file footer (currently empty, reserved for future use).</summary>
    public virtual string EmitFileFooter() => string.Empty;

    /// <summary>Emits the symbol declaration (<c>.globl</c>, alignment, label) for a thunk.</summary>
    public abstract void EmitSymbolDecl(StringBuilder sb, string thunkSymbol);

    /// <summary>Emits a tail-call thunk — zero overhead, no frame, parameters/return forwarded as-is.</summary>
    public abstract void EmitTailCall(StringBuilder sb, ThunkDescriptor descriptor);

    /// <summary>Emits a full-frame thunk that bridges self, swifterror, and/or large struct returns.</summary>
    public abstract void EmitFullFrame(StringBuilder sb, ThunkDescriptor descriptor);
}
