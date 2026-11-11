// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>
/// The Swift-side facts one Swift-convention transition needs: what the callee is, how it takes
/// self, how it takes each parameter, what it returns and which effects it has. Facts are
/// semantic — they say what Swift declared, never where a value lands in a register. The physical
/// outcome (which loadable values Swift passes indirectly, which registers a lowered aggregate
/// occupies) is the planner's job, decided against swiftc's own lowering, so facts never encode a
/// size rule or a register.
/// <para>
/// Every field is filled from a typed source — the ABI JSON through the parser's model, a
/// type-metadata contract, a value-witness slot or a closure type — and never parsed back out of
/// rendered C#. Two facts records are equal when every fact is equal, so identical transitions
/// deduplicate by value.
/// </para>
/// </summary>
public sealed record SwiftCallFacts
{
    /// <summary>
    /// The facts schema version. Bump it on any change to the shape or meaning of a field, so a
    /// serialized record is never read under a different schema than the one that wrote it.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>The schema version this record was produced under.</summary>
    public int FactsVersion { get; init; } = CurrentVersion;

    /// <summary>What is called.</summary>
    public required SwiftCallTarget Target { get; init; }

    /// <summary>How the callee takes self.</summary>
    public required SwiftSelfFacts Self { get; init; }

    /// <summary>The formal parameters, in Swift order. Self, the error and an indirect result are not here.</summary>
    public required ImmutableArray<SwiftParameterFacts> Parameters { get; init; }

    /// <summary>The formal result.</summary>
    public required SwiftResultFacts Result { get; init; }

    /// <summary>Effects and features of the callee's function type.</summary>
    public required SwiftCallEffects Effects { get; init; }

    /// <summary>
    /// The generic signature in scope at the callee, the parent type's parameters included. Each
    /// parameter is passed as a metadata pointer and each conformance requirement as a witness
    /// table, after the formal parameters. Empty for a non-generic callee.
    /// </summary>
    public SwiftGenericSignatureFacts Generics { get; init; } = SwiftGenericSignatureFacts.Empty;

    /// <summary>
    /// The stable identity a ledger entry or a plan cites. A transition that calls a symbol is
    /// identified by that symbol; one that calls a function pointer by its pointer source.
    /// </summary>
    public string Id => Target.Symbol ?? Target.PointerSource ?? Target.Kind.ToString();

    /// <summary>
    /// Facts the source could not determine, each with its reason. Empty means complete. A
    /// non-empty list is never silently treated as complete: the planner refuses on it.
    /// </summary>
    public ImmutableArray<string> Unknowns { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>True when every fact was determined.</summary>
    public bool IsComplete => Unknowns.IsDefaultOrEmpty;

    public bool Equals(SwiftCallFacts? other) =>
        other is not null
        && FactsVersion == other.FactsVersion
        && Target == other.Target
        && Self == other.Self
        && Result == other.Result
        && Effects == other.Effects
        && Generics == other.Generics
        && Parameters.AsSpan().SequenceEqual(other.Parameters.AsSpan())
        && Unknowns.AsSpan().SequenceEqual(other.Unknowns.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FactsVersion);
        hash.Add(Target);
        hash.Add(Self);
        hash.Add(Result);
        hash.Add(Effects);
        foreach (var parameter in Parameters)
            hash.Add(parameter);
        hash.Add(Generics);
        foreach (var unknown in Unknowns)
            hash.Add(unknown);
        return hash.ToHashCode();
    }
}

/// <summary>What kind of Swift entity a transition calls.</summary>
public enum SwiftCallTargetKind
{
    /// <summary>A module-level function.</summary>
    FreeFunction,

    /// <summary>A method of a type, instance or static, including an operator.</summary>
    Method,

    /// <summary>An initializer: the allocating entry (<c>fC</c>) or, for a class, the initializing one (<c>fc</c>).</summary>
    Initializer,

    /// <summary>A property or subscript accessor; <see cref="SwiftCallTarget.Accessor"/> says which.</summary>
    Accessor,

    /// <summary>A type-metadata accessor (<c>…Ma</c>).</summary>
    MetadataAccessor,

    /// <summary>A value-witness function called through a value-witness table slot.</summary>
    ValueWitness,

    /// <summary>A Swift closure invoked through its function pointer, with its context as self.</summary>
    ClosureInvocation,

    /// <summary>A Swift function the generator synthesized into the wrapper; its producer supplies the facts.</summary>
    SyntheticWrapper,
}

/// <summary>Which accessor of a property or subscript.</summary>
public enum SwiftAccessorKind
{
    None,
    Get,
    Set,

    /// <summary>The <c>modify</c> coroutine.</summary>
    Modify,

    /// <summary>The <c>read</c> coroutine.</summary>
    Read,
}

/// <summary>Which entry of the callee a symbol names.</summary>
public enum SwiftEntryKind
{
    /// <summary>The implementation itself, or the only entry there is.</summary>
    Implementation,

    /// <summary>The dispatch thunk (<c>…Tj</c>), which dispatches through the class vtable or the protocol witness table.</summary>
    DispatchThunk,

    /// <summary>A function pointer, not a symbol.</summary>
    FunctionPointer,
}

/// <summary>One platform's availability floor, as declared.</summary>
public sealed record SwiftAvailabilityFloor(string Platform, string Version);

/// <summary>The identity of the callee.</summary>
public sealed record SwiftCallTarget
{
    public required SwiftCallTargetKind Kind { get; init; }

    public SwiftAccessorKind Accessor { get; init; } = SwiftAccessorKind.None;

    public SwiftEntryKind Entry { get; init; } = SwiftEntryKind.Implementation;

    /// <summary>The mangled symbol called, or null for a function-pointer call.</summary>
    public string? Symbol { get; init; }

    /// <summary>
    /// For a function-pointer call, where the pointer comes from, as a stable name: a value-witness
    /// slot (<c>vwt.initializeWithCopy</c>) or a closure function (<c>closure</c>). Null for a symbol call.
    /// </summary>
    public string? PointerSource { get; init; }

    /// <summary>The Swift module that defines the callee, when it has one.</summary>
    public string? Module { get; init; }

    /// <summary>The module-qualified Swift name of the declaring type, or null for a free function or a pointer call.</summary>
    public string? DeclaringType { get; init; }

    /// <summary>The member's Swift name (a property's or subscript's name for an accessor).</summary>
    public string? MemberName { get; init; }

    /// <summary>
    /// Declared introduction floors, one per platform that declares one, sorted by platform. A
    /// runtime-support floor (one the generator derives) is not a declared floor and is not here.
    /// </summary>
    public ImmutableArray<SwiftAvailabilityFloor> Availability { get; init; } = ImmutableArray<SwiftAvailabilityFloor>.Empty;

    /// <summary>
    /// True when the member is also reached through a dispatch thunk (<c>…Tj</c>): a protocol
    /// requirement, or an overridable instance member of a non-final class.
    /// </summary>
    public bool HasDispatchThunk { get; init; }

    public bool Equals(SwiftCallTarget? other) =>
        other is not null
        && Kind == other.Kind && Accessor == other.Accessor && Entry == other.Entry
        && Symbol == other.Symbol && PointerSource == other.PointerSource && Module == other.Module
        && DeclaringType == other.DeclaringType && MemberName == other.MemberName
        && HasDispatchThunk == other.HasDispatchThunk
        && Availability.AsSpan().SequenceEqual(other.Availability.AsSpan());

    public override int GetHashCode() =>
        HashCode.Combine(Kind, Accessor, Entry, Symbol, PointerSource, Module, DeclaringType, HashCode.Combine(MemberName, HasDispatchThunk));
}

/// <summary>What the callee receives as self, semantically.</summary>
public enum SwiftCallSelfKind
{
    /// <summary>No self: a free function, a metadata accessor, a value witness.</summary>
    None,

    /// <summary>A class instance, an actor, or a class-bound generic self.</summary>
    Reference,

    /// <summary>A thick metatype: a class's static member or allocating initializer, a protocol extension's static member.</summary>
    ThickMetatype,

    /// <summary>A thin metatype: a struct's or enum's static member or initializer. It occupies no register.</summary>
    ThinMetatype,

    /// <summary>A value: an instance member of a struct or enum, or of a non-class-bound protocol extension.</summary>
    Value,

    /// <summary>A closure context, passed as self to the closure's function.</summary>
    ClosureContext,
}

/// <summary>How the callee treats a value or reference self.</summary>
public enum SwiftSelfMutation
{
    /// <summary>Self is not a value or reference (none, a metatype, a closure context).</summary>
    NotApplicable,

    /// <summary>The default: self is borrowed (guaranteed).</summary>
    Nonmutating,

    /// <summary><c>mutating</c>, a setter, or <c>modify</c>: self is inout.</summary>
    Mutating,

    /// <summary><c>consuming</c>: the callee takes ownership of self.</summary>
    Consuming,

    /// <summary><c>borrowing</c>: explicitly guaranteed.</summary>
    Borrowing,
}

/// <summary>Why the callee's self may be abstract.</summary>
public enum SwiftSelfAbstraction
{
    /// <summary>A concrete, non-generic declaring type.</summary>
    Concrete,

    /// <summary>A member of a generic type: it uses the unspecialized ABI.</summary>
    GenericParent,

    /// <summary>A protocol extension member: self is the generic <c>Self</c>.</summary>
    ProtocolExtension,

    /// <summary>A protocol requirement called through its dispatch thunk.</summary>
    ProtocolRequirement,
}

/// <summary>The callee's self.</summary>
public sealed record SwiftSelfFacts
{
    public static readonly SwiftSelfFacts None = new()
    {
        Kind = SwiftCallSelfKind.None,
        Mutation = SwiftSelfMutation.NotApplicable,
        Abstraction = SwiftSelfAbstraction.Concrete,
    };

    public required SwiftCallSelfKind Kind { get; init; }

    public required SwiftSelfMutation Mutation { get; init; }

    public required SwiftSelfAbstraction Abstraction { get; init; }

    /// <summary>The self type, or null when there is no self.</summary>
    public SwiftTypeFacts? Type { get; init; }
}

/// <summary>A formal parameter's ownership convention, as Swift declares it.</summary>
public enum SwiftParameterConvention
{
    /// <summary>Borrowed: the caller keeps ownership.</summary>
    Guaranteed,

    /// <summary>Consumed: the caller hands over a +1 value.</summary>
    Owned,

    /// <summary><c>inout</c>: the callee reads and writes the caller's storage.</summary>
    Inout,
}

/// <summary>Where a parameter's convention came from.</summary>
public enum SwiftConventionSource
{
    /// <summary>
    /// The declaration kind's default: owned for an initializer's or a setter's parameters (a
    /// subscript setter's indices included), guaranteed otherwise, and guaranteed for a
    /// non-escaping closure everywhere.
    /// </summary>
    Default,

    /// <summary>Spelled in the declaration: <c>inout</c>, <c>__owned</c>/<c>consuming</c>, <c>__shared</c>/<c>borrowing</c>.</summary>
    Explicit,
}

/// <summary>Whether a parameter is a closure, and whether it escapes.</summary>
public enum SwiftClosureKind
{
    NotClosure,
    Escaping,
    NonEscaping,
}

/// <summary>One formal parameter.</summary>
public sealed record SwiftParameterFacts
{
    /// <summary>Zero-based position among the formal parameters, in Swift order.</summary>
    public required int Index { get; init; }

    /// <summary>The argument label or internal name, for reading; never used to match.</summary>
    public string? Name { get; init; }

    public required SwiftTypeFacts Type { get; init; }

    public required SwiftParameterConvention Convention { get; init; }

    public required SwiftConventionSource ConventionSource { get; init; }

    public SwiftClosureKind Closure { get; init; } = SwiftClosureKind.NotClosure;

    public bool IsVariadic { get; init; }
}

/// <summary>The formal result.</summary>
public sealed record SwiftResultFacts
{
    public static readonly SwiftResultFacts Void = new() { Type = SwiftTypeFacts.Void };

    public required SwiftTypeFacts Type { get; init; }

    /// <summary>True for a void result.</summary>
    public bool IsVoid => Type.Category == SwiftTypeCategory.Void;

    /// <summary>A direct result is always returned owned (+1) to the caller.</summary>
    public SwiftParameterConvention Convention => SwiftParameterConvention.Owned;

    /// <summary>True for a failable initializer, whose result is <c>Optional&lt;Self&gt;</c>.</summary>
    public bool IsFailableInitializer { get; init; }
}

/// <summary>The broad kind of a Swift type, enough to tell a planner which classifier it needs.</summary>
public enum SwiftTypeCategory
{
    Void,
    Struct,
    Enum,
    Class,

    /// <summary>A protocol existential (<c>any P</c>, a protocol composition, <c>Any</c>, <c>AnyObject</c>).</summary>
    Existential,

    /// <summary>A generic parameter or a dependent member of one: always passed indirectly.</summary>
    GenericParameter,

    Tuple,
    Closure,
    Metatype,

    /// <summary>A named type the type database does not know. Recorded, never guessed.</summary>
    Unresolved,

    /// <summary>A raw machine word (a metadata request, a metadata or witness-table pointer, an enum tag).</summary>
    MachineWord,

    /// <summary>An opaque type (<c>some P</c>): one concrete type the callee chooses, known to the caller only through its metadata.</summary>
    Opaque,
}

/// <summary>A Swift type as the facts see it.</summary>
public sealed record SwiftTypeFacts
{
    public static readonly SwiftTypeFacts Void = new() { Spelling = "()", Category = SwiftTypeCategory.Void };

    /// <summary>The module-qualified Swift spelling, for reading and for matching facts to facts.</summary>
    public required string Spelling { get; init; }

    public required SwiftTypeCategory Category { get; init; }

    /// <summary>
    /// For a struct or enum, whether its layout is frozen (known to clients), when the source says.
    /// Null when the source does not say or the category has no layout of its own.
    /// </summary>
    public bool? IsFrozen { get; init; }

    /// <summary>For a class or existential, whether it is bound to classes (a single reference).</summary>
    public bool IsClassBound { get; init; }

    /// <summary>Creates a raw machine-word type.</summary>
    public static SwiftTypeFacts Word(string spelling) => new() { Spelling = spelling, Category = SwiftTypeCategory.MachineWord };
}

/// <summary>Effects and features of the callee's function type.</summary>
public sealed record SwiftCallEffects
{
    public static readonly SwiftCallEffects None = new();

    public bool Throws { get; init; }

    /// <summary>The thrown type of a typed <c>throws(E)</c>, or null for untyped throws or none.</summary>
    public string? TypedThrows { get; init; }

    public bool IsAsync { get; init; }

    /// <summary>A <c>read</c> or <c>modify</c> coroutine accessor.</summary>
    public bool IsCoroutine { get; init; }

    /// <summary>A parameter pack (<c>each T</c>) in the callee's generic signature.</summary>
    public bool HasParameterPacks { get; init; }
}

/// <summary>What a generic requirement asks of its subject.</summary>
public enum SwiftGenericRequirementKind
{
    /// <summary><c>T: P</c>. A witness table argument, unless <c>P</c> is an Objective-C protocol or a marker.</summary>
    Conformance,

    /// <summary><c>T == U</c>. No argument: it identifies two parameters.</summary>
    SameType,
}

/// <summary>One requirement of a generic signature.</summary>
/// <param name="Subject">The constrained type: a parameter, or a member path rooted at one (<c>τ_0_0.Element</c>).</param>
/// <param name="Target">The protocol, or the type the subject equals.</param>
public sealed record SwiftGenericRequirementFacts(string Subject, string Target, SwiftGenericRequirementKind Kind);

/// <summary>The generic signature in scope at a callee.</summary>
public sealed record SwiftGenericSignatureFacts
{
    public static readonly SwiftGenericSignatureFacts Empty = new();

    /// <summary>The generic parameters in canonical (<c>τ_depth_index</c>) form, outermost first.</summary>
    public ImmutableArray<string> Parameters { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>The requirements in declaration order.</summary>
    public ImmutableArray<SwiftGenericRequirementFacts> Requirements { get; init; } = ImmutableArray<SwiftGenericRequirementFacts>.Empty;

    /// <summary>True when the signature has no parameters.</summary>
    public bool IsEmpty => Parameters.IsDefaultOrEmpty;

    public bool Equals(SwiftGenericSignatureFacts? other) =>
        other is not null
        && Parameters.AsSpan().SequenceEqual(other.Parameters.AsSpan())
        && Requirements.AsSpan().SequenceEqual(other.Requirements.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Parameters.Length, Requirements.Length);
}
