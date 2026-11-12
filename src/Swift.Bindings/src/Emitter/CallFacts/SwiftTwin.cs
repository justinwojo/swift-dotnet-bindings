// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;

namespace BindingsGeneration;

/// <summary>
/// A twin's canonical C-convention signature: each parameter's and the return's type as the layout
/// fixes it (register class, size, signedness), never as the managed declaration spells it. Two
/// transitions sharing a plan share this signature; a managed type with the same layout is the
/// same twin parameter.
/// </summary>
/// <param name="Parameters">One per twin argument, in order: the ordinary prefix, then the trailing pointers.</param>
/// <param name="Return"><c>void</c>, or the one scalar a scalar result comes back as.</param>
public sealed record SwiftTwinSignature(ImmutableArray<string> Parameters, string Return)
{
    public bool Equals(SwiftTwinSignature? other) =>
        other is not null && Return == other.Return && Parameters.AsSpan().SequenceEqual(other.Parameters.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Return, Parameters.Length);

    public override string ToString() => $"{Return} ({string.Join(", ", Parameters)})";

    public static SwiftTwinSignature Of(SwiftManagedView view) => new(
        view.Arguments.Select(a => TypeOf(a.Class, a.Size, a.Signed)).ToImmutableArray(),
        view.Result.Kind == SwiftResultKind.Scalar ? TypeOf(view.Result.Class!.Value, view.Result.Size, view.Result.Signed) : "void");

    /// <summary>
    /// The C# type of a scalar of this class and size. Signedness is kept below eight bytes, where it
    /// decides how the caller extends the value; a pointer-sized integer is <c>nint</c> either way.
    /// </summary>
    public static string TypeOf(SwiftRegisterClass @class, int size, bool signed) => (@class, size) switch
    {
        (SwiftRegisterClass.Float, 4) => "float",
        (SwiftRegisterClass.Float, 8) => "double",
        (SwiftRegisterClass.General, 1) => signed ? "sbyte" : "byte",
        (SwiftRegisterClass.General, 2) => signed ? "short" : "ushort",
        (SwiftRegisterClass.General, 4) => signed ? "int" : "uint",
        (SwiftRegisterClass.General, 8) => "nint",
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, $"no twin type for a {@class} scalar of {size} bytes"),
    };
}

/// <summary>What the managed side of a twin call must do that the twin's types do not say.</summary>
public enum SwiftMarshallingObligationKind
{
    /// <summary>A <c>SafeHandle</c>: take a reference before the call, pass the raw handle, release it after the call returns or throws.</summary>
    SafeHandleAddRefRelease,

    /// <summary>A by-reference parameter: pin the referent for the call; the twin passes its address.</summary>
    PinByReference,

    /// <summary>A <c>bool</c>: pass (or read back) exactly 1 or 0 at the width its <c>MarshalAs</c> names.</summary>
    NormalizeBool,

    /// <summary>An aggregate passed in pieces: the twin takes the aggregate's bytes at each piece's offset.</summary>
    AggregatePieces,

    /// <summary>An aggregate passed by address: the twin takes the address of a copy that stays alive and unmoved for the call.</summary>
    AggregateAddress,

    /// <summary><c>SwiftIndirectResult</c>: pass the address of uninitialized storage of the result's size.</summary>
    IndirectResultAddress,

    /// <summary>A direct result of several pieces: supply the result buffer and read the managed result from it after the call.</summary>
    ResultBuffer,

    /// <summary><c>SwiftSelf</c>: pass its value.</summary>
    SelfValue,

    /// <summary><c>SwiftError*</c>: pass the address of a slot the shim zeroes; a non-null slot after the call is the thrown error, owned by the caller.</summary>
    ErrorSlot,

    /// <summary>The callee consumes the argument (+1): the shim transfers its reference and must not release what it passed.</summary>
    OwnedTransfer,

    /// <summary>An <c>inout</c> argument: the referent is accessed exclusively for the call and may be written.</summary>
    InoutExclusive,
}

/// <summary>One marshalling obligation: what, for which managed parameter (or Swift formal), and why.</summary>
/// <param name="Parameter">The managed parameter it applies to; null for the result or for self.</param>
public sealed record SwiftMarshallingObligation(SwiftMarshallingObligationKind Kind, int? Parameter, string Detail)
{
    /// <summary>For an obligation the Swift side states (ownership, <c>inout</c>): the formal it applies to, or -1 for self.</summary>
    public int? Formal { get; init; }
}

/// <summary>
/// The managed marshalling a twin call must preserve, derived from the managed declaration (its
/// carriers, ref kinds and <c>MarshalAs</c>) and the facts (ownership and <c>inout</c>). These are the
/// things a source-generated or runtime marshaller does today that the twin's primitive-only
/// signature cannot express; whoever calls the twin takes them over.
/// </summary>
public static class SwiftMarshallingObligations
{
    public static ImmutableArray<SwiftMarshallingObligation> Of(
        SwiftPlanKind kind, SwiftCallFacts facts, SwiftManagedSignature managed, ImmutableArray<SwiftManagedValue> parameters,
        SwiftManagedValue returned, SwiftManagedView view, ISwiftValueLowering lowering)
    {
        var list = new List<SwiftMarshallingObligation>();
        for (int i = 0; i < managed.Parameters.Length && i < parameters.Length; i++)
        {
            var declared = managed.Parameters[i];
            var value = parameters[i];
            var simple = SimpleName(declared.Type);
            if (simple == "SafeHandle" && value.Kind == SwiftManagedValueKind.Primitive)
                list.Add(new(SwiftMarshallingObligationKind.SafeHandleAddRefRelease, i,
                    "DangerousAddRef before the call, pass DangerousGetHandle(), DangerousRelease after it returns or throws"));
            bool isBool = simple is "bool" or "Boolean" && value.Kind == SwiftManagedValueKind.Primitive;
            var refKind = declared.RefKind == SwiftManagedRefKind.None ? "ref" : declared.RefKind.ToString().ToLowerInvariant();
            if (value.ByReference && value.Kind == SwiftManagedValueKind.Primitive)
            {
                // A marshaller hands a by-reference bool over as the address of a native temporary it
                // converts in and back; with none, the address is the managed byte itself, already 0 or 1.
                if (isBool && managed.Marshalling != SwiftManagedMarshalling.None)
                    list.Add(new(SwiftMarshallingObligationKind.NormalizeBool, i,
                        $"{refKind} bool: pass the address of {BoolReferent(declared.MarshalAs)}" +
                        (declared.RefKind == SwiftManagedRefKind.Out ? "" : " holding true or false") +
                        (declared.RefKind == SwiftManagedRefKind.In ? "" : ", and copy it back after the call, non-zero is true") +
                        $" (MarshalAs {declared.MarshalAs ?? "none"})"));
                else
                    list.Add(new(SwiftMarshallingObligationKind.PinByReference, i,
                        $"{refKind} {declared.Type}: pin the referent for the call and pass its address"));
            }
            else if (isBool)
                list.Add(new(SwiftMarshallingObligationKind.NormalizeBool, i,
                    $"pass 1 or 0 as a {value.Layout!.Leaves[0].Size}-byte integer (MarshalAs {declared.MarshalAs ?? "none"})"));
            switch (value.Kind)
            {
                case SwiftManagedValueKind.Aggregate:
                {
                    var pieces = view.Arguments.Where(a => a.Parameter == i && a.Role == SwiftTwinRole.Ordinary).ToList();
                    if (pieces.Any(a => a.Source == SwiftPieceSource.Address))
                        list.Add(new(SwiftMarshallingObligationKind.AggregateAddress, i,
                            $"{value.Type}: pass the address of a copy that stays alive and unmoved for the call"));
                    else
                        list.Add(new(SwiftMarshallingObligationKind.AggregatePieces, i,
                            $"{value.Type}: pass its bytes at {string.Join(", ", pieces.Select(p => $"{p.Offset}:{p.Size}"))}"));
                    break;
                }
                case SwiftManagedValueKind.IndirectResult:
                    list.Add(new(SwiftMarshallingObligationKind.IndirectResultAddress, i,
                        "pass SwiftIndirectResult.Value: uninitialized storage of the result's size the callee initializes"));
                    break;
                case SwiftManagedValueKind.Self:
                    list.Add(new(SwiftMarshallingObligationKind.SelfValue, i, "pass SwiftSelf.Value"));
                    break;
                case SwiftManagedValueKind.Error:
                    list.Add(new(SwiftMarshallingObligationKind.ErrorSlot, i, kind == SwiftPlanKind.Reverse
                        ? "the callback stores a thrown error in the slot it is given and leaves it null otherwise"
                        : "pass the address of a SwiftError slot the shim zeroes; a non-null slot after the call is the thrown error, owned by the caller"));
                    break;
            }
        }

        if (SimpleName(managed.ReturnType) is "bool" or "Boolean" && returned.Kind == SwiftManagedValueKind.Primitive)
            list.Add(new(SwiftMarshallingObligationKind.NormalizeBool, null,
                $"read the result as a {returned.Layout!.Leaves[0].Size}-byte integer, non-zero is true (MarshalAs {managed.ReturnMarshalAs ?? "none"})"));
        if (view.Result.Kind == SwiftResultKind.Direct)
            list.Add(new(SwiftMarshallingObligationKind.ResultBuffer, null,
                $"supply a {view.Result.Size}-byte buffer for {view.Result.ManagedType} and read the result from it after the call"));

        // What the Swift side states: who owns what crosses.
        if (facts.IsComplete)
        {
            var self = SwiftPassingClassifier.SelfConvention(facts, lowering);
            if (self.Ownership == SwiftPassingOwnership.Consumed)
                list.Add(new(SwiftMarshallingObligationKind.OwnedTransfer, null, "the callee consumes self (+1)") { Formal = -1 });
            if (self.Convention == SwiftSelfConvention.Inout)
                list.Add(new(SwiftMarshallingObligationKind.InoutExclusive, null, "self is inout: exclusive access for the call") { Formal = -1 });
            var passings = SwiftPassingClassifier.ParameterPassings(facts, lowering);
            for (int j = 0; j < passings.Count; j++)
            {
                if (passings[j].Ownership == SwiftPassingOwnership.Consumed)
                    list.Add(new(SwiftMarshallingObligationKind.OwnedTransfer, null, $"the callee consumes formal {j} (+1)") { Formal = j });
                if (passings[j].Kind == SwiftParameterPassingKind.Inout)
                    list.Add(new(SwiftMarshallingObligationKind.InoutExclusive, null, $"formal {j} is inout: exclusive access for the call") { Formal = j });
            }
        }
        return list.ToImmutableArray();
    }

    /// <summary>The native temporary a marshaller gives a by-reference bool, by its <c>MarshalAs</c>.</summary>
    private static string BoolReferent(string? marshalAs) => marshalAs switch
    {
        "I1" or "U1" => "a 1-byte temporary",
        null or "I4" or "U4" or "Bool" => "a 4-byte temporary",
        _ => $"a temporary of the representation MarshalAs {marshalAs} names",
    };

    private static string SimpleName(string spelling)
    {
        var type = SyntaxFactory.ParseTypeName(spelling);
        if (type is Microsoft.CodeAnalysis.CSharp.Syntax.RefTypeSyntax refType)
            type = refType.Type;
        return type is Microsoft.CodeAnalysis.CSharp.Syntax.PredefinedTypeSyntax predefined
            ? predefined.Keyword.ValueText
            : SwiftManagedTypeIndex.SimpleName(type);
    }
}
