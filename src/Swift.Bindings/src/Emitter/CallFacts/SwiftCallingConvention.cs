// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>An architecture a plan is made for.</summary>
public enum SwiftArchitecture
{
    Arm64,
    X86_64,

    /// <summary>Refused until pointer-authenticated emission has tests.</summary>
    Arm64e,
}

/// <summary>Which register file a piece travels in.</summary>
public enum SwiftRegisterClass
{
    General,
    Float,
}

/// <summary>Where an operand is: a register, a stack slot, a field of the closure context, or a local of the bridge's frame.</summary>
public enum SwiftOperandKind
{
    Register,

    /// <summary>An argument slot on the stack, at <see cref="SwiftOperand.Offset"/> from the stack-argument base (the caller's <c>sp</c> at the call).</summary>
    Stack,

    /// <summary>A word of the closure context the self register points at, at <see cref="SwiftOperand.Offset"/>.</summary>
    ContextField,

    /// <summary>A local of the bridge's frame, at <see cref="SwiftOperand.Offset"/> from its outgoing stack area's base.</summary>
    Local,
}

/// <summary>One operand: an argument location, a source or a destination of a move.</summary>
public sealed record SwiftOperand(SwiftOperandKind Kind, string? Register, int Offset, int Size)
{
    public static SwiftOperand InRegister(string register, int size) => new(SwiftOperandKind.Register, register, 0, size);

    public static SwiftOperand OnStack(int offset, int size) => new(SwiftOperandKind.Stack, null, offset, size);

    public override string ToString() => Kind switch
    {
        SwiftOperandKind.Register => $"{Register}:{Size}",
        SwiftOperandKind.Stack => $"[stack+{Offset}]:{Size}",
        SwiftOperandKind.ContextField => $"[context+{Offset}]:{Size}",
        _ => $"[local+{Offset}]:{Size}",
    };
}

/// <summary>One scalar argument as an assignment sees it: its register class and its size.</summary>
public readonly record struct SwiftArgumentSlot(SwiftRegisterClass Class, int Size);

/// <summary>
/// The argument and result assignment of one architecture, for the two conventions a bridge stands
/// between: the platform C convention (the model M the twin's Cdecl call uses) and <c>swiftcc</c>. For
/// ordinary scalar arguments the two are the same on both architectures, which is what lets a bridge
/// leave the prefix in place; the special registers and the direct-result registers are
/// <c>swiftcc</c>'s own.
/// </summary>
public sealed class SwiftCallingConvention
{
    public SwiftArchitecture Architecture { get; }

    public ImmutableArray<string> GeneralArguments { get; }

    public ImmutableArray<string> FloatArguments { get; }

    /// <summary><c>swiftcc</c>'s direct-result registers, up to four pieces of each class.</summary>
    public ImmutableArray<string> GeneralResults { get; }

    public ImmutableArray<string> FloatResults { get; }

    /// <summary>The indirect-result register.</summary>
    public string IndirectResult { get; }

    /// <summary>The self (context) register.</summary>
    public string Self { get; }

    /// <summary>The error register.</summary>
    public string Error { get; }

    /// <summary>The scratch register a thick call's or a reverse stub's target is called through.</summary>
    public string Function { get; }

    /// <summary>
    /// True when a stack argument takes its natural size and alignment (Apple arm64); false when
    /// every stack argument takes an eight-byte slot (System V x86_64).
    /// </summary>
    public bool PacksStackArguments { get; }

    private SwiftCallingConvention(
        SwiftArchitecture architecture, string[] general, string[] floats, string[] generalResults, string[] floatResults,
        string indirect, string self, string error, string function, bool packs)
    {
        Architecture = architecture;
        GeneralArguments = general.ToImmutableArray();
        FloatArguments = floats.ToImmutableArray();
        GeneralResults = generalResults.ToImmutableArray();
        FloatResults = floatResults.ToImmutableArray();
        IndirectResult = indirect;
        Self = self;
        Error = error;
        Function = function;
        PacksStackArguments = packs;
    }

    /// <summary>Apple arm64: x0–x7 and v0–v7 counted apart, stack packed at natural size; x8 indirect result, x20 self, x21 error.</summary>
    public static readonly SwiftCallingConvention Arm64 = new(
        SwiftArchitecture.Arm64,
        ["x0", "x1", "x2", "x3", "x4", "x5", "x6", "x7"],
        ["v0", "v1", "v2", "v3", "v4", "v5", "v6", "v7"],
        ["x0", "x1", "x2", "x3"],
        ["v0", "v1", "v2", "v3"],
        indirect: "x8", self: "x20", error: "x21", function: "x16", packs: true);

    /// <summary>System V x86_64: six general and eight vector registers, eight-byte stack slots; rax indirect result, r13 self, r12 error.</summary>
    public static readonly SwiftCallingConvention X86_64 = new(
        SwiftArchitecture.X86_64,
        ["rdi", "rsi", "rdx", "rcx", "r8", "r9"],
        ["xmm0", "xmm1", "xmm2", "xmm3", "xmm4", "xmm5", "xmm6", "xmm7"],
        ["rax", "rdx", "rcx", "r8"],
        ["xmm0", "xmm1", "xmm2", "xmm3"],
        indirect: "rax", self: "r13", error: "r12", function: "r11", packs: false);

    public static SwiftCallingConvention For(SwiftArchitecture architecture) => architecture switch
    {
        SwiftArchitecture.Arm64 => Arm64,
        SwiftArchitecture.X86_64 => X86_64,
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "no calling convention is modelled for it"),
    };

    /// <summary>The C convention's one-register scalar result.</summary>
    public string ScalarResult(SwiftRegisterClass @class) => @class == SwiftRegisterClass.Float ? FloatResults[0] : GeneralResults[0];

    /// <summary>
    /// Assigns scalar arguments in order. Each class takes its next register while any are left,
    /// then the stack; returns each location and the bytes of stack used.
    /// </summary>
    public (ImmutableArray<SwiftOperand> Locations, int StackBytes) Assign(IEnumerable<SwiftArgumentSlot> slots)
    {
        int general = 0, floats = 0, stack = 0;
        var locations = ImmutableArray.CreateBuilder<SwiftOperand>();
        foreach (var slot in slots)
        {
            if (slot.Class == SwiftRegisterClass.Float && floats < FloatArguments.Length)
            {
                locations.Add(SwiftOperand.InRegister(FloatArguments[floats++], slot.Size));
                continue;
            }
            if (slot.Class == SwiftRegisterClass.General && general < GeneralArguments.Length)
            {
                locations.Add(SwiftOperand.InRegister(GeneralArguments[general++], slot.Size));
                continue;
            }
            if (PacksStackArguments)
            {
                stack = SwiftLeafLayout.AlignUp(stack, slot.Size);
                locations.Add(SwiftOperand.OnStack(stack, slot.Size));
                stack += slot.Size;
            }
            else
            {
                locations.Add(SwiftOperand.OnStack(stack, slot.Size));
                stack += 8;
            }
        }
        return (locations.ToImmutable(), stack);
    }

    /// <summary>
    /// <c>swiftcc</c>'s direct-result registers for up to four pieces, each class counted apart;
    /// null when a class needs more registers than it has.
    /// </summary>
    public ImmutableArray<SwiftOperand>? AssignResult(IEnumerable<SwiftArgumentSlot> pieces)
    {
        int general = 0, floats = 0;
        var locations = ImmutableArray.CreateBuilder<SwiftOperand>();
        foreach (var piece in pieces)
        {
            if (piece.Class == SwiftRegisterClass.Float)
            {
                if (floats >= FloatResults.Length)
                    return null;
                locations.Add(SwiftOperand.InRegister(FloatResults[floats++], piece.Size));
            }
            else
            {
                if (general >= GeneralResults.Length)
                    return null;
                locations.Add(SwiftOperand.InRegister(GeneralResults[general++], piece.Size));
            }
        }
        return locations.ToImmutable();
    }

    /// <summary>The register class and size of a scalar IR type, or null for anything that is not one scalar (a vector).</summary>
    public static SwiftArgumentSlot? SlotOfIrType(string irType) => irType switch
    {
        "ptr" or "i64" => new SwiftArgumentSlot(SwiftRegisterClass.General, 8),
        "i32" => new SwiftArgumentSlot(SwiftRegisterClass.General, 4),
        "i16" => new SwiftArgumentSlot(SwiftRegisterClass.General, 2),
        "i8" or "i1" => new SwiftArgumentSlot(SwiftRegisterClass.General, 1),
        "float" => new SwiftArgumentSlot(SwiftRegisterClass.Float, 4),
        "double" => new SwiftArgumentSlot(SwiftRegisterClass.Float, 8),
        _ => null,
    };

    /// <summary>The register class and size of a lowered piece.</summary>
    public static SwiftArgumentSlot SlotOf(SwiftLoweredPiece piece) => new(
        piece.IsFloatingPoint ? SwiftRegisterClass.Float : SwiftRegisterClass.General, piece.Size);
}
