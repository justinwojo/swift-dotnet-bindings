// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;

namespace BindingsGeneration;

/// <summary>What one physical argument of a Swift call carries.</summary>
public enum SwiftPhysicalArgumentRole
{
    /// <summary>The address the callee writes an indirect result to.</summary>
    IndirectResult,

    /// <summary>One lowered piece of a formal parameter, or a formal parameter's address.</summary>
    Formal,

    /// <summary>One lowered piece of a self Swift passes directly, after the formal parameters.</summary>
    LoweredSelf,

    /// <summary>The metadata of a generic self type, which carries its generic arguments.</summary>
    SelfTypeMetadata,

    /// <summary>The metadata of a generic parameter.</summary>
    GenericMetadata,

    /// <summary>A witness table for a conformance requirement.</summary>
    WitnessTable,

    /// <summary>The self (context) register: a reference, a metatype, or self's address.</summary>
    Context,

    /// <summary>The error register.</summary>
    Error,

    /// <summary>A protocol requirement's trailing <c>Self</c> metadata.</summary>
    WitnessSelfMetadata,

    /// <summary>A protocol requirement's trailing <c>Self</c> witness table.</summary>
    WitnessSelfTable,
}

/// <summary>One physical argument: its IR type and the attributes that place it.</summary>
/// <param name="FormalIndex">For a formal piece, the formal parameter it belongs to.</param>
/// <param name="Detail">What it is, for reading: a generic parameter's name, a requirement.</param>
public sealed record SwiftPhysicalArgument(
    SwiftPhysicalArgumentRole Role,
    string IrType,
    bool NoAlias = false,
    bool StructRet = false,
    bool SwiftSelf = false,
    bool SwiftError = false,
    int? FormalIndex = null,
    string? Detail = null)
{
    /// <summary>The IR type and placing attributes, in a fixed order: <c>ptr noalias sret</c>.</summary>
    public string Normalized
    {
        get
        {
            var sb = new StringBuilder(IrType);
            if (NoAlias) sb.Append(" noalias");
            if (StructRet) sb.Append(" sret");
            if (SwiftSelf) sb.Append(" swiftself");
            if (SwiftError) sb.Append(" swifterror");
            return sb.ToString();
        }
    }

    public override string ToString() => Detail is null ? Normalized : $"{Normalized} ({Detail})";
}

/// <summary>
/// The physical signature of a synchronous <c>swiftcc</c> call: every argument in the order the
/// callee receives it, and the IR type of its direct result.
/// </summary>
public sealed record SwiftPhysicalSignature(ImmutableArray<SwiftPhysicalArgument> Arguments, string Result)
{
    /// <summary>The signature as swiftc's IR spells it, attributes normalized: <c>i64 (ptr noalias sret, i64)</c>.</summary>
    public string Normalized => $"{Result} ({string.Join(", ", Arguments.Select(a => a.Normalized))})";

    public bool Equals(SwiftPhysicalSignature? other) =>
        other is not null && Result == other.Result && Arguments.AsSpan().SequenceEqual(other.Arguments.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Result, Arguments.Length);

    public override string ToString() => Normalized;
}

/// <summary>A predicted physical signature, or why there is none.</summary>
public sealed record SwiftPhysicalPrediction(SwiftPhysicalSignature? Signature, string? Refusal)
{
    public bool IsPredicted => Signature is not null;

    internal static SwiftPhysicalPrediction Refused(string reason) => new(null, reason);
}

/// <summary>
/// Predicts the physical signature swiftc gives a synchronous Swift callee from its
/// <see cref="SwiftCallFacts"/>: the order IRGen expands a call's parameters in (indirect results,
/// the formal parameters with a directly passed self last among them, the generic metadata and
/// witness tables the callee cannot find elsewhere, the self register, the error register, a
/// protocol requirement's trailing <c>Self</c> metadata and witness table), each formal and the
/// result lowered by the <see cref="ISwiftValueLowering"/> the oracle pins. It predicts only what
/// it models and refuses everything else.
/// </summary>
public static class SwiftPhysicalSignaturePredictor
{
    private static readonly SwiftPhysicalArgument Address = new(SwiftPhysicalArgumentRole.Formal, "ptr", NoAlias: true);

    public static SwiftPhysicalPrediction Predict(SwiftCallFacts facts, ISwiftValueLowering lowering)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(lowering);
        if (SwiftSynchronousCall.Refusal(facts) is { } notSynchronous)
            return SwiftPhysicalPrediction.Refused(notSynchronous);
        if (!facts.IsComplete)
            return SwiftPhysicalPrediction.Refused($"the facts are incomplete: {string.Join("; ", facts.Unknowns)}");
        if (facts.Target.Kind is SwiftCallTargetKind.ClosureInvocation or SwiftCallTargetKind.ValueWitness or SwiftCallTargetKind.MetadataAccessor)
            return SwiftPhysicalPrediction.Refused($"a {facts.Target.Kind} is not a declared Swift function this predictor models");

        var arguments = ImmutableArray.CreateBuilder<SwiftPhysicalArgument>();
        var generics = facts.Generics;

        // The result: direct pieces, or an address before everything else.
        string result;
        if (facts.Result.IsVoid)
        {
            result = "void";
        }
        else
        {
            var physical = SwiftPassingClassifier.PhysicalResult(facts.Result.Type, generics, lowering);
            switch (physical.Outcome)
            {
                case SwiftLoweringOutcome.Direct when physical.HasLayout:
                    result = ResultType(physical.Layout);
                    break;
                case SwiftLoweringOutcome.Indirect:
                    arguments.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.IndirectResult, "ptr", NoAlias: true, StructRet: true));
                    result = "void";
                    break;
                default:
                    return SwiftPhysicalPrediction.Refused($"result ('{facts.Result.Type.Spelling}'): {physical.Reason}");
            }
        }

        // The formal parameters, in Swift order.
        for (int i = 0; i < facts.Parameters.Length; i++)
        {
            var passing = SwiftPassingClassifier.ParameterPassing(facts, i, lowering);
            switch (passing.Kind)
            {
                case SwiftParameterPassingKind.Direct:
                {
                    var physical = SwiftPassingClassifier.Physical(facts.Parameters[i].Type, generics, lowering);
                    if (!physical.HasLayout)
                        return SwiftPhysicalPrediction.Refused($"parameter {i} ('{facts.Parameters[i].Type.Spelling}'): its pieces are not known");
                    foreach (var piece in physical.Layout)
                        arguments.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.Formal, piece.IrType, FormalIndex: i));
                    break;
                }
                case SwiftParameterPassingKind.IndirectBorrowed or SwiftParameterPassingKind.IndirectOwned:
                    arguments.Add(Address with { FormalIndex = i });
                    break;
                case SwiftParameterPassingKind.Inout:
                    arguments.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.Formal, "ptr", FormalIndex: i));
                    break;
                default:
                    return SwiftPhysicalPrediction.Refused(passing.Refusal ?? $"parameter {i} has no passing");
            }
        }

        // Self: lowered pieces close the formal list; anything else takes the self register.
        var self = SwiftPassingClassifier.SelfConvention(facts, lowering);
        if (!self.IsDecided)
            return SwiftPhysicalPrediction.Refused(self.Refusal ?? "self has no convention");
        SwiftPhysicalArgument? context = null;
        switch (self.Convention)
        {
            case SwiftSelfConvention.None:
                break;
            case SwiftSelfConvention.Lowered:
            {
                var physical = SwiftPassingClassifier.PhysicalSelf(facts.Self, generics, lowering);
                if (!physical.HasLayout)
                    return SwiftPhysicalPrediction.Refused($"self ('{facts.Self.Type!.Spelling}'): its pieces are not known");
                foreach (var piece in physical.Layout)
                    arguments.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.LoweredSelf, piece.IrType));
                break;
            }
            case SwiftSelfConvention.IndirectBorrowed or SwiftSelfConvention.IndirectOwned:
                context = new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.Context, "ptr", NoAlias: true, SwiftSelf: true);
                break;
            default:
                // A reference, a thick metatype, a closure context, an inout self: one pointer.
                context = new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.Context, "ptr", SwiftSelf: true);
                break;
        }

        var (leading, trailing, polymorphicRefusal) = Polymorphic(facts, self.Convention!.Value);
        if (polymorphicRefusal is not null)
            return SwiftPhysicalPrediction.Refused(polymorphicRefusal);
        arguments.AddRange(leading);
        if (context is not null)
        {
            arguments.Add(context);
        }
        else if (facts.Effects.Throws)
        {
            // A throwing callee with no self still has the self register in its signature, unused,
            // so the error register follows it at its fixed position.
            arguments.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.Context, "ptr", SwiftSelf: true, Detail: "unused"));
        }
        if (facts.Effects.Throws)
            arguments.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.Error, "ptr", NoAlias: true, SwiftError: true));
        arguments.AddRange(trailing);
        return new SwiftPhysicalPrediction(new SwiftPhysicalSignature(arguments.ToImmutable(), result), null);
    }

    /// <summary>The IR type of a direct result: nothing, one piece, or a literal struct of the pieces.</summary>
    public static string ResultType(ImmutableArray<SwiftLoweredPiece> pieces) => pieces.Length switch
    {
        0 => "void",
        1 => pieces[0].IrType,
        _ => "{ " + string.Join(", ", pieces.Select(p => p.IrType)) + " }",
    };

    /// <summary>
    /// The generic arguments the callee takes: the metadata of each generic parameter and a witness
    /// table for each conformance requirement, in signature order, except those it finds elsewhere.
    /// A generic value self passes its own type's metadata first instead of its type's arguments; a
    /// class instance self, a class metatype self and a bound generic class instance parameter carry
    /// their arguments in their metadata. A protocol requirement takes <c>Self</c>'s metadata and
    /// witness table last, after the error register.
    /// </summary>
    private static (ImmutableArray<SwiftPhysicalArgument> Leading, ImmutableArray<SwiftPhysicalArgument> Trailing, string? Refusal) Polymorphic(
        SwiftCallFacts facts, SwiftSelfConvention selfConvention)
    {
        var generics = facts.Generics;
        var leading = ImmutableArray.CreateBuilder<SwiftPhysicalArgument>();
        var trailing = ImmutableArray.CreateBuilder<SwiftPhysicalArgument>();
        if (generics.IsEmpty)
        {
            if (facts.Self.Abstraction is SwiftSelfAbstraction.GenericParent or SwiftSelfAbstraction.ProtocolRequirement or SwiftSelfAbstraction.ProtocolExtension)
                return (default, default, $"self is {facts.Self.Abstraction} but the callee has no generic signature");
            return (leading.ToImmutable(), trailing.ToImmutable(), null);
        }
        if (generics.Requirements.Any(r => r.Kind == SwiftGenericRequirementKind.SameType))
            return (default, default, "a same-type requirement identifies generic parameters, which this predictor does not model");

        var inherited = generics.Parameters.Where(p => !generics.OwnParameters.Contains(p)).ToHashSet(StringComparer.Ordinal);
        // The parameters whose metadata the callee reads from self or from a class argument instead.
        // Self's metadata also carries the conformances in its type's own signature; a class
        // argument's carries its class's, which the facts do not record.
        var fulfilled = new HashSet<string>(StringComparer.Ordinal);
        var fulfilledBySelf = new HashSet<string>(StringComparer.Ordinal);
        switch (facts.Self.Abstraction)
        {
            case SwiftSelfAbstraction.GenericParent:
                switch (facts.Self.Kind)
                {
                    case SwiftCallSelfKind.Value when selfConvention == SwiftSelfConvention.Lowered:
                        // A self passed in pieces carries no metadata: its type's generic
                        // arguments and conformances are passed like the member's own.
                        break;
                    case SwiftCallSelfKind.Value:
                        // The self type's metadata carries its generic arguments and conformances.
                        leading.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.SelfTypeMetadata, "ptr", Detail: facts.Self.Type?.Spelling));
                        fulfilledBySelf.UnionWith(inherited);
                        break;
                    case SwiftCallSelfKind.Reference or SwiftCallSelfKind.ThickMetatype:
                        // A class instance's or class metatype's metadata carries them.
                        fulfilledBySelf.UnionWith(inherited);
                        break;
                    case SwiftCallSelfKind.ThinMetatype:
                        // A struct's static member or initializer has no self value to read them from.
                        break;
                    default:
                        return (default, default, $"a generic-parent self of kind {facts.Self.Kind} is not modeled");
                }
                break;
            case SwiftSelfAbstraction.ProtocolRequirement:
            {
                // The witness_method convention: Self's metadata and witness table come last.
                if (generics.Parameters.IsDefaultOrEmpty || inherited.Count != 1)
                    return (default, default, "a protocol requirement's signature does not have exactly one inherited parameter (Self)");
                var selfParameter = generics.Parameters[0];
                if (!inherited.Contains(selfParameter))
                    return (default, default, "a protocol requirement's first generic parameter is not Self");
                fulfilledBySelf.Add(selfParameter);
                trailing.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.WitnessSelfMetadata, "ptr", Detail: "Self"));
                trailing.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.WitnessSelfTable, "ptr", Detail: "Self witness table"));
                break;
            }
        }

        fulfilled.UnionWith(fulfilledBySelf);

        // A bound generic class instance parameter carries its arguments in its metadata, and each
        // bound generic nominal argument its own in turn. An inout one is only an address, which
        // the callee does not read metadata through.
        foreach (var parameter in facts.Parameters)
        {
            if (parameter.Type.Category != SwiftTypeCategory.Class || parameter.Convention == SwiftParameterConvention.Inout)
                continue;
            foreach (var argument in GenericArgumentsReachable(parameter.Type.Spelling))
                if (generics.Parameters.Contains(argument))
                    fulfilled.Add(argument);
        }

        foreach (var parameter in generics.Parameters)
        {
            if (!fulfilled.Contains(parameter))
                leading.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.GenericMetadata, "ptr", Detail: parameter));
        }
        foreach (var requirement in generics.Requirements)
        {
            if (requirement.Kind != SwiftGenericRequirementKind.Conformance)
                continue;
            if (requirement.TargetKind == SwiftRequirementTargetKind.Unknown)
                return (default, default, $"whether '{requirement.Subject}: {requirement.Target}' takes a witness table is not known");
            var root = requirement.Subject.Split('.')[0];
            if (fulfilledBySelf.Contains(root))
            {
                // Self's metadata (or the Self witness table) carries the conformances of its type's
                // own signature; one the member adds, a constrained extension's, is passed.
                if (requirement.IsInherited)
                    continue;
            }
            else if (fulfilled.Contains(root))
            {
                if (requirement.PassesWitnessTable)
                    return (default, default, $"whether a class argument's metadata carries the witness table for '{requirement.Subject}: {requirement.Target}' is not recorded");
                continue;
            }
            if (requirement.PassesWitnessTable)
                leading.Add(new SwiftPhysicalArgument(SwiftPhysicalArgumentRole.WitnessTable, "ptr", Detail: $"{requirement.Subject}: {requirement.Target}"));
        }
        return (leading.ToImmutable(), trailing.ToImmutable(), null);
    }

    /// <summary>
    /// The bare names among a bound generic spelling's generic arguments, and among those of each
    /// bound generic nominal argument, recursively: <c>M.K&lt;τ_0_0, Swift.Optional&lt;τ_1_0&gt;&gt;</c>
    /// gives <c>τ_0_0</c> and <c>τ_1_0</c>. A nested type's metadata carries its enclosing types'
    /// arguments too, so <c>M.Outer&lt;τ_0_0&gt;.Inner&lt;τ_0_1&gt;</c> gives both. Each is read out of
    /// the metadata it is an argument of; a function, tuple or existential argument is not searched.
    /// </summary>
    private static IEnumerable<string> GenericArgumentsReachable(string spelling)
    {
        TypeSpec? spec;
        try
        {
            spec = TypeSpecParser.Parse(SwiftCallFactsBuilder.BracketAttributeArguments(spelling));
        }
        catch (TypeSpecParseException)
        {
            return Array.Empty<string>();
        }
        var found = new List<string>();
        void Walk(NamedTypeSpec named)
        {
            foreach (var argument in named.GenericParameters)
            {
                if (argument is not NamedTypeSpec { IsAny: false } nominal)
                    continue;
                if (nominal is { GenericParameters.Count: 0, InnerType: null })
                    found.Add(nominal.Name);
                else
                    Walk(nominal);
            }
            if (named.InnerType is { } inner)
                Walk(inner);
        }
        if (spec is NamedTypeSpec root)
            Walk(root);
        return found;
    }
}
