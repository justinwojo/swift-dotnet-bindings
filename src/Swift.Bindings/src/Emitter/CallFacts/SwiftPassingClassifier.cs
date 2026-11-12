// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>How a Swift callee receives self, physically and by ownership, for one member.</summary>
public enum SwiftSelfConvention
{
    /// <summary>No self register: a free function, a struct's or enum's static member (thin metatype).</summary>
    None,

    /// <summary>A class instance, a class-bound existential or a closure context: one pointer in the self register.</summary>
    Reference,

    /// <summary>A thick metatype, a class allocating initializer's included: one pointer in the self register.</summary>
    Metatype,

    /// <summary>
    /// A mutating method, setter or <c>modify</c>: the address of the caller's real storage in the
    /// self register. Writes through it must land in that storage, so it is never a temporary.
    /// </summary>
    Inout,

    /// <summary>
    /// A nonmutating self that Swift passes indirectly (address-only, resilient, generic-abstracted,
    /// or loadable but over Swift's direct-piece limit): an address in the self register. The callee
    /// borrows; a bitwise temporary is acceptable.
    /// </summary>
    IndirectBorrowed,

    /// <summary>A consuming self that Swift passes indirectly: the address of a +1 value the caller no longer destroys.</summary>
    IndirectOwned,

    /// <summary>
    /// A loadable self that Swift passes directly: its lowered pieces at Swift's self position (last
    /// among the formal parameters) in the ordinary arguments, with no self register.
    /// </summary>
    Lowered,
}

/// <summary>How one formal parameter reaches the callee.</summary>
public enum SwiftParameterPassingKind
{
    /// <summary>The value's lowered pieces in ordinary argument positions.</summary>
    Direct,

    /// <summary>The address of a value the callee borrows (guaranteed); a bitwise temporary is acceptable.</summary>
    IndirectBorrowed,

    /// <summary>The address of a +1 value the callee consumes (owned); the caller no longer destroys it.</summary>
    IndirectOwned,

    /// <summary>The address of the caller's real storage (<c>inout</c>); writes must land there.</summary>
    Inout,
}

/// <summary>What the call does to the ownership of the value it is handed.</summary>
public enum SwiftPassingOwnership
{
    /// <summary>Nothing is handed over: no self, or a metatype.</summary>
    NotApplicable,

    /// <summary>The callee borrows (+0); the caller keeps ownership.</summary>
    Borrowed,

    /// <summary>The callee borrows the caller's storage mutably (<c>inout</c>, a mutating self).</summary>
    BorrowedMutable,

    /// <summary>The callee consumes a +1 value; the caller must not destroy it afterwards.</summary>
    Consumed,
}

/// <summary>Whether Swift passes a value directly, as pieces, or through an address.</summary>
public enum SwiftLoweringOutcome
{
    /// <summary>The answer is not known; a classification that needs it refuses.</summary>
    Unknown,

    /// <summary>Directly, as <see cref="SwiftLoweringAnswer.Pieces"/> register-sized pieces.</summary>
    Direct,

    /// <summary>Through an address.</summary>
    Indirect,
}

/// <summary>The physical passing of one Swift type as a parameter (or a nonmutating self).</summary>
/// <param name="Outcome">Direct, indirect, or unknown.</param>
/// <param name="Pieces">For <see cref="SwiftLoweringOutcome.Direct"/>, how many pieces; null when not counted.</param>
/// <param name="Reason">Why, for an indirect or unknown answer, or which source decided.</param>
public sealed record SwiftLoweringAnswer(SwiftLoweringOutcome Outcome, int? Pieces, string? Reason)
{
    /// <summary>
    /// For a direct answer whose pieces are known, each piece with its IR type, in order; empty
    /// otherwise (and for a value with no storage).
    /// </summary>
    public ImmutableArray<SwiftLoweredPiece> Layout { get; init; } = ImmutableArray<SwiftLoweredPiece>.Empty;

    /// <summary>True when <see cref="Layout"/> holds the pieces: a direct answer that counted them.</summary>
    public bool HasLayout => Outcome == SwiftLoweringOutcome.Direct && Pieces == Layout.Length;

    public static SwiftLoweringAnswer DirectPieces(int? pieces, string reason) => new(SwiftLoweringOutcome.Direct, pieces, reason);
    public static SwiftLoweringAnswer DirectLayout(ImmutableArray<SwiftLoweredPiece> pieces, string reason) =>
        new(SwiftLoweringOutcome.Direct, pieces.Length, reason) { Layout = pieces };
    public static SwiftLoweringAnswer IndirectBecause(string reason) => new(SwiftLoweringOutcome.Indirect, null, reason);
    public static SwiftLoweringAnswer UnknownBecause(string reason) => new(SwiftLoweringOutcome.Unknown, null, reason);

    public bool Equals(SwiftLoweringAnswer? other) =>
        other is not null && Outcome == other.Outcome && Pieces == other.Pieces && Reason == other.Reason
        && Layout.AsSpan().SequenceEqual(other.Layout.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Outcome, Pieces, Reason, Layout.Length);
}

/// <summary>
/// Answers whether Swift passes a loadable type directly or through an address. The classifier asks
/// only about types whose outcome the facts cannot settle on their own: frozen structs and enums,
/// tuples and class-bound existentials, whose answer depends on their lowered piece count against
/// Swift's four-piece limit. The answer is physical, so it belongs to swiftc's lowering; the P7
/// oracle pins it.
/// </summary>
public interface ISwiftValueLowering
{
    /// <summary>How Swift passes a value of <paramref name="type"/> as a parameter.</summary>
    SwiftLoweringAnswer Lower(SwiftTypeFacts type);

    /// <summary>
    /// How Swift returns a value of <paramref name="type"/>. It differs from
    /// <see cref="Lower"/> only for a tuple, which a parameter list splits element by element and a
    /// result keeps whole.
    /// </summary>
    SwiftLoweringAnswer LowerResult(SwiftTypeFacts type) => Lower(type);

    /// <summary>
    /// <see cref="Lower"/> or <see cref="LowerResult"/> under the callee's generic signature, which
    /// says whether a generic parameter the type spells is bound to classes.
    /// </summary>
    SwiftLoweringAnswer Lower(SwiftTypeFacts type, SwiftGenericSignatureFacts? generics, bool asResult) =>
        asResult ? LowerResult(type) : Lower(type);
}

/// <summary>A lowering that knows nothing: every question it is asked is unknown, so the classifier refuses.</summary>
public sealed class UnknownSwiftValueLowering : ISwiftValueLowering
{
    public static readonly UnknownSwiftValueLowering Instance = new();

    private UnknownSwiftValueLowering() { }

    public SwiftLoweringAnswer Lower(SwiftTypeFacts type) =>
        SwiftLoweringAnswer.UnknownBecause($"no lowering was supplied for {type.Category} '{type.Spelling}'");
}

/// <summary>The classified self of one transition: its convention and what the call does to its ownership.</summary>
/// <param name="Convention">The convention, or null when refused.</param>
/// <param name="Ownership">The ownership effect; <see cref="SwiftPassingOwnership.NotApplicable"/> when refused.</param>
/// <param name="Pieces">For <see cref="SwiftSelfConvention.Lowered"/>, the piece count when known.</param>
/// <param name="Refusal">Why no convention could be decided, or null.</param>
public sealed record SwiftSelfPassing(SwiftSelfConvention? Convention, SwiftPassingOwnership Ownership, int? Pieces, string? Refusal)
{
    public bool IsDecided => Convention is not null;

    internal static SwiftSelfPassing Decided(SwiftSelfConvention convention, SwiftPassingOwnership ownership, int? pieces = null) =>
        new(convention, ownership, pieces, null);

    internal static SwiftSelfPassing Refused(string reason) => new(null, SwiftPassingOwnership.NotApplicable, null, reason);
}

/// <summary>The classified passing of one formal parameter.</summary>
/// <param name="Index">The parameter's position among the formal parameters.</param>
/// <param name="Kind">How it is passed, or null when refused.</param>
/// <param name="Ownership">The ownership effect; <see cref="SwiftPassingOwnership.NotApplicable"/> when refused.</param>
/// <param name="Pieces">For <see cref="SwiftParameterPassingKind.Direct"/>, the piece count when known.</param>
/// <param name="Refusal">Why no passing could be decided, or null.</param>
public sealed record SwiftParameterPassing(int Index, SwiftParameterPassingKind? Kind, SwiftPassingOwnership Ownership, int? Pieces, string? Refusal)
{
    public bool IsDecided => Kind is not null;
}

/// <summary>
/// Classifies how a Swift callee takes self (<see cref="SelfConvention"/>) and each formal parameter
/// (<see cref="ParameterPassing"/>) from its <see cref="SwiftCallFacts"/>. Classification is per
/// (member, type), not per type: the same small frozen struct is lowered into a getter, passed by
/// address to a setter or mutating method, and handed over +1 to a consuming method.
/// <para>
/// Two rules decide who answers what. The facts settle everything semantic: whether self is mutated
/// or consumed, a parameter's convention, and the categories whose physical passing does not depend
/// on layout (a class reference is one direct piece; an unconstrained generic parameter, an opaque
/// type, a non-class-bound existential and a resilient struct or enum are always indirect). Whether a
/// loadable value goes in pieces or through an address depends on its lowering, which the supplied
/// <see cref="ISwiftValueLowering"/> answers; an unknown answer refuses rather than guesses. And
/// <see cref="SwiftSelfConvention.Inout"/> is never inferred from size: only a mutating member makes self inout.
/// </para>
/// <para>
/// Incomplete facts refuse: the classifier never treats a fact the source could not determine as
/// settled.
/// </para>
/// </summary>
public static class SwiftPassingClassifier
{
    /// <summary>How the callee takes self.</summary>
    public static SwiftSelfPassing SelfConvention(SwiftCallFacts facts, ISwiftValueLowering lowering)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(lowering);
        if (!facts.IsComplete)
            return SwiftSelfPassing.Refused($"the facts are incomplete: {string.Join("; ", facts.Unknowns)}");

        var self = facts.Self;
        switch (self.Kind)
        {
            case SwiftCallSelfKind.None:
            case SwiftCallSelfKind.ThinMetatype:
                return SwiftSelfPassing.Decided(SwiftSelfConvention.None, SwiftPassingOwnership.NotApplicable);

            case SwiftCallSelfKind.ThickMetatype:
                return SwiftSelfPassing.Decided(SwiftSelfConvention.Metatype, SwiftPassingOwnership.NotApplicable);

            case SwiftCallSelfKind.ClosureContext:
                // A thick closure's context is passed guaranteed, as one pointer in the self register.
                return SwiftSelfPassing.Decided(SwiftSelfConvention.Reference, SwiftPassingOwnership.Borrowed);

            case SwiftCallSelfKind.Reference:
                return self.Mutation switch
                {
                    SwiftSelfMutation.Nonmutating or SwiftSelfMutation.Borrowing =>
                        SwiftSelfPassing.Decided(SwiftSelfConvention.Reference, SwiftPassingOwnership.Borrowed),
                    SwiftSelfMutation.Consuming =>
                        SwiftSelfPassing.Decided(SwiftSelfConvention.Reference, SwiftPassingOwnership.Consumed),
                    _ => SwiftSelfPassing.Refused($"a reference self cannot be {self.Mutation}"),
                };

            case SwiftCallSelfKind.Value:
                return ValueSelf(self, facts.Generics, lowering);

            default:
                return SwiftSelfPassing.Refused($"self kind {self.Kind} has no convention");
        }
    }

    /// <summary>How the callee takes the formal parameter at <paramref name="index"/>.</summary>
    public static SwiftParameterPassing ParameterPassing(SwiftCallFacts facts, int index, ISwiftValueLowering lowering)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(lowering);
        if (index < 0 || index >= facts.Parameters.Length)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"the callee has {facts.Parameters.Length} formal parameter(s)");
        if (!facts.IsComplete)
            return new(index, null, SwiftPassingOwnership.NotApplicable, null, $"the facts are incomplete: {string.Join("; ", facts.Unknowns)}");

        var parameter = facts.Parameters[index];
        if (parameter.Convention == SwiftParameterConvention.Inout)
            return new(index, SwiftParameterPassingKind.Inout, SwiftPassingOwnership.BorrowedMutable, null, null);

        var ownership = parameter.Convention == SwiftParameterConvention.Owned
            ? SwiftPassingOwnership.Consumed
            : SwiftPassingOwnership.Borrowed;
        var physical = Physical(parameter.Type, facts.Generics, lowering);
        return physical.Outcome switch
        {
            SwiftLoweringOutcome.Direct => new(index, SwiftParameterPassingKind.Direct, ownership, physical.Pieces, null),
            SwiftLoweringOutcome.Indirect => new(index,
                ownership == SwiftPassingOwnership.Consumed ? SwiftParameterPassingKind.IndirectOwned : SwiftParameterPassingKind.IndirectBorrowed,
                ownership, null, null),
            _ => new(index, null, SwiftPassingOwnership.NotApplicable, null,
                $"parameter {index} ('{parameter.Type.Spelling}'): {physical.Reason}"),
        };
    }

    /// <summary>Every formal parameter's passing, in Swift order.</summary>
    public static IReadOnlyList<SwiftParameterPassing> ParameterPassings(SwiftCallFacts facts, ISwiftValueLowering lowering)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var result = new SwiftParameterPassing[facts.Parameters.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = ParameterPassing(facts, i, lowering);
        return result;
    }

    /// <summary>
    /// The physical passing of a type, from the facts where they settle it and from
    /// <paramref name="lowering"/> where only the layout can.
    /// </summary>
    /// <remarks>
    /// Without the generic signature in scope, a generic parameter not recorded as class-bound is
    /// unknown: the facts record class-boundness for a protocol's <c>Self</c> but not for a
    /// constrained parameter such as <c>T: AnyObject</c>.
    /// </remarks>
    public static SwiftLoweringAnswer Physical(SwiftTypeFacts type, ISwiftValueLowering lowering) =>
        Physical(type, generics: null, lowering);

    /// <summary>
    /// The physical passing of a type under the generic signature in scope. A generic parameter
    /// not recorded as class-bound is address-only only when the signature has no requirement
    /// at all; any requirement (<c>T: AnyObject</c>, <c>T: SomeClass</c>, <c>T == SomeClass</c>,
    /// a class-bound protocol) could make it a single reference, which the facts do not record.
    /// </summary>
    public static SwiftLoweringAnswer Physical(SwiftTypeFacts type, SwiftGenericSignatureFacts? generics, ISwiftValueLowering lowering) =>
        Physical(type, generics, lowering, asResult: false);

    /// <summary>How Swift returns a value of <paramref name="type"/>: the parameter rules, except that a tuple stays whole.</summary>
    public static SwiftLoweringAnswer PhysicalResult(SwiftTypeFacts type, SwiftGenericSignatureFacts? generics, ISwiftValueLowering lowering) =>
        Physical(type, generics, lowering, asResult: true);

    private static readonly ImmutableArray<SwiftLoweredPiece> OnePointer =
        ImmutableArray.Create(new SwiftLoweredPiece(0, 8, SwiftScalarKind.Pointer));

    private static readonly ImmutableArray<SwiftLoweredPiece> TwoPointers =
        ImmutableArray.Create(new SwiftLoweredPiece(0, 8, SwiftScalarKind.Pointer), new SwiftLoweredPiece(8, 8, SwiftScalarKind.Pointer));

    private static SwiftLoweringAnswer Physical(SwiftTypeFacts type, SwiftGenericSignatureFacts? generics, ISwiftValueLowering lowering, bool asResult)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(lowering);
        switch (type.Category)
        {
            case SwiftTypeCategory.Void:
                return SwiftLoweringAnswer.DirectLayout(ImmutableArray<SwiftLoweredPiece>.Empty, "an empty tuple has no pieces");
            case SwiftTypeCategory.Class:
            case SwiftTypeCategory.CFunctionPointer:
                return SwiftLoweringAnswer.DirectLayout(OnePointer, $"a {type.Category} is one pointer");
            case SwiftTypeCategory.MachineWord:
                return SwiftLoweringAnswer.DirectLayout(ImmutableArray.Create(new SwiftLoweredPiece(0, 8, SwiftScalarKind.Int64)), "a machine word is one 64-bit integer");
            case SwiftTypeCategory.Closure:
                return type.Representation switch
                {
                    SwiftFunctionRepresentation.Thick => SwiftLoweringAnswer.DirectLayout(TwoPointers, "a thick function is a function pointer and a context"),
                    SwiftFunctionRepresentation.Block => SwiftLoweringAnswer.DirectLayout(OnePointer, "a block is one reference"),
                    SwiftFunctionRepresentation.Thin => SwiftLoweringAnswer.DirectLayout(OnePointer, "a thin function is one function pointer"),
                    SwiftFunctionRepresentation.C => SwiftLoweringAnswer.DirectLayout(OnePointer, "a C function pointer is one pointer"),
                    _ => SwiftLoweringAnswer.UnknownBecause($"the representation of the function type '{type.Spelling}' is not recorded"),
                };
            case SwiftTypeCategory.Metatype:
            {
                // A thick metatype is one pointer and a thin one none; either is direct.
                var answer = lowering.Lower(type, generics, asResult);
                return answer.Outcome == SwiftLoweringOutcome.Direct ? answer : SwiftLoweringAnswer.DirectPieces(null, "a metatype is direct");
            }
            case SwiftTypeCategory.GenericParameter when type.IsClassBound:
                return SwiftLoweringAnswer.DirectLayout(OnePointer, "a class-bound generic parameter is one reference");
            case SwiftTypeCategory.GenericParameter when generics is { Requirements.IsEmpty: true }:
                return SwiftLoweringAnswer.IndirectBecause("an unconstrained generic parameter is address-only");
            case SwiftTypeCategory.GenericParameter when generics is not null && generics.Parameters.Contains(type.Spelling):
                return generics.IsClassBound(type.Spelling) switch
                {
                    true => SwiftLoweringAnswer.DirectLayout(OnePointer, "a class-bound generic parameter is one reference"),
                    false => SwiftLoweringAnswer.IndirectBecause($"the generic parameter '{type.Spelling}' is not bound to classes, so it is address-only"),
                    null => SwiftLoweringAnswer.UnknownBecause($"whether the generic parameter '{type.Spelling}' is class-bound is not recorded in the facts"),
                };
            case SwiftTypeCategory.GenericParameter:
                return SwiftLoweringAnswer.UnknownBecause(
                    $"whether the generic parameter '{type.Spelling}' is class-bound is not recorded in the facts");
            case SwiftTypeCategory.Opaque:
                return SwiftLoweringAnswer.IndirectBecause("an opaque type is address-only to its caller");
            case SwiftTypeCategory.Existential when !type.IsClassBound && !IsErrorExistential(type):
                return SwiftLoweringAnswer.IndirectBecause("an opaque existential is address-only");
            case SwiftTypeCategory.Struct or SwiftTypeCategory.Enum when type.IsFrozen == false:
            {
                // The lowering knows the layouts swiftc uses for types a database records as
                // resilient (an imported C struct, a standard-library buffer pointer); otherwise a
                // resilient type is address-only.
                var answer = lowering.Lower(type, generics, asResult);
                return answer.Outcome != SwiftLoweringOutcome.Unknown
                    ? answer
                    : SwiftLoweringAnswer.IndirectBecause($"a resilient {type.Category.ToString().ToLowerInvariant()} is address-only to a client");
            }
            case SwiftTypeCategory.Unresolved:
                return SwiftLoweringAnswer.UnknownBecause($"type '{type.Spelling}' is unresolved");
            default:
                // A frozen struct or enum, a tuple, a class-bound existential: its layout decides.
                return lowering.Lower(type, generics, asResult);
        }
    }

    /// <summary>
    /// <c>any Error</c>: Swift boxes it, so it is one reference, unlike any other existential that
    /// is not class-bound.
    /// </summary>
    private static bool IsErrorExistential(SwiftTypeFacts type)
    {
        var spelling = type.Spelling.StartsWith("any ", StringComparison.Ordinal) ? type.Spelling[4..] : type.Spelling;
        return spelling is "Swift.Error" or "Error";
    }

    /// <summary>
    /// How a value self's type lowers: a generic type's self is its unspecialized type, which
    /// the lowering answers under the callee's signature.
    /// </summary>
    public static SwiftLoweringAnswer PhysicalSelf(SwiftSelfFacts self, SwiftGenericSignatureFacts? generics, ISwiftValueLowering lowering)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(self.Type);
        return self.Abstraction == SwiftSelfAbstraction.GenericParent
            ? Physical(self.Type, generics, lowering)
            : Physical(self.Type, lowering);
    }

    private static SwiftSelfPassing ValueSelf(SwiftSelfFacts self, SwiftGenericSignatureFacts generics, ISwiftValueLowering lowering)
    {
        if (self.Mutation == SwiftSelfMutation.Mutating)
            return SwiftSelfPassing.Decided(SwiftSelfConvention.Inout, SwiftPassingOwnership.BorrowedMutable);

        bool consuming = self.Mutation == SwiftSelfMutation.Consuming;
        if (self.Mutation is not (SwiftSelfMutation.Nonmutating or SwiftSelfMutation.Borrowing or SwiftSelfMutation.Consuming))
            return SwiftSelfPassing.Refused($"a value self cannot be {self.Mutation}");
        var ownership = consuming ? SwiftPassingOwnership.Consumed : SwiftPassingOwnership.Borrowed;
        var indirect = consuming ? SwiftSelfConvention.IndirectOwned : SwiftSelfConvention.IndirectBorrowed;

        // A protocol's Self is a generic parameter in unspecialized code: always an address.
        if (self.Abstraction is SwiftSelfAbstraction.ProtocolRequirement or SwiftSelfAbstraction.ProtocolExtension)
            return SwiftSelfPassing.Decided(indirect, ownership);
        if (self.Type is null)
            return SwiftSelfPassing.Refused("a value self has no type");

        // A generic type's self is its unspecialized type: an address unless its layout is the
        // same whatever its parameters are bound to (a frozen struct whose stored properties do
        // not hold one), which the lowering answers under the callee's signature.
        var physical = PhysicalSelf(self, generics, lowering);
        return physical.Outcome switch
        {
            SwiftLoweringOutcome.Direct => SwiftSelfPassing.Decided(SwiftSelfConvention.Lowered, ownership, physical.Pieces),
            SwiftLoweringOutcome.Indirect => SwiftSelfPassing.Decided(indirect, ownership),
            _ => SwiftSelfPassing.Refused($"self ('{self.Type.Spelling}'): {physical.Reason}"),
        };
    }
}
