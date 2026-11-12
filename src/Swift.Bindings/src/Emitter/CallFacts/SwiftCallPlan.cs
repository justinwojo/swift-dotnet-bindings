// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BindingsGeneration;

/// <summary>What a plan bridges.</summary>
public enum SwiftPlanKind
{
    /// <summary>A forward call of a Swift symbol: <c>SBSR_&lt;hash&gt;(O…, [ret], [self], [err])</c>.</summary>
    Forward,

    /// <summary>A forward call of a thick closure through its function pointer: <c>SBSR_thick_&lt;hash&gt;(O…, [ret], ctx, [err], fn)</c>.</summary>
    ForwardThick,

    /// <summary>
    /// A reverse stub Swift calls as a closure function: <c>SBSRR_&lt;hash&gt;</c> receives the closure's
    /// arguments and calls the managed callback <c>target(O…, [ret], [err], payload)</c>.
    /// </summary>
    Reverse,
}

/// <summary>What a twin argument carries.</summary>
public enum SwiftTwinRole
{
    /// <summary>An ordinary argument the callee reads in place.</summary>
    Ordinary,

    /// <summary>The address of an indirect result.</summary>
    IndirectResult,

    /// <summary>The address of a buffer a direct multi-piece result is stored into, at the managed result's offsets.</summary>
    ResultBuffer,

    /// <summary>The self (context) value.</summary>
    Self,

    /// <summary>The address of the managed error slot.</summary>
    Error,

    /// <summary>A thick closure's function pointer.</summary>
    Function,

    /// <summary>A reverse callback's payload, read from the closure context.</summary>
    Payload,
}

/// <summary>Where an ordinary twin argument's value comes from in the managed shim.</summary>
public enum SwiftPieceSource
{
    /// <summary>The primitive itself.</summary>
    Value,

    /// <summary><see cref="SwiftTwinArgument.Size"/> bytes of an aggregate at <see cref="SwiftTwinArgument.Offset"/>.</summary>
    Piece,

    /// <summary>The address of the aggregate (a copy the callee may read).</summary>
    Address,

    /// <summary>A trailing pointer the shim supplies.</summary>
    Trailing,
}

/// <summary>One argument of the twin: what it carries, which managed parameter it comes from, and its class and size.</summary>
/// <param name="Parameter">The managed declaration's parameter it comes from; null for a trailing pointer with none.</param>
/// <param name="Unread">True for an ordinary argument the callee takes nothing at: the managed declaration passes more than the callee reads.</param>
/// <param name="UnreadReason">For an unread argument of a forward plan, why passing it is benign: what the callee takes instead.</param>
public sealed record SwiftTwinArgument(
    SwiftTwinRole Role,
    int? Parameter,
    string ManagedType,
    SwiftPieceSource Source,
    int Offset,
    int Size,
    SwiftRegisterClass Class,
    bool Signed = false,
    bool Unread = false,
    string? UnreadReason = null)
{
    [JsonIgnore]
    public SwiftArgumentSlot Slot => new(Class, Size);
}

/// <summary>How a call's result comes back.</summary>
public enum SwiftResultKind
{
    Void,

    /// <summary>One primitive in the first result register.</summary>
    Scalar,

    /// <summary>Up to four pieces in the result registers, which the bridge stores into the result buffer.</summary>
    Direct,

    /// <summary>Through the address in the indirect-result register.</summary>
    Indirect,
}

/// <summary>One piece of a direct result: the bytes of the managed result it fills.</summary>
public sealed record SwiftPlanPiece(int Offset, int Size, SwiftRegisterClass Class);

/// <summary>The managed side of a call's result.</summary>
/// <param name="Size">For a scalar, its size; for a direct result, the managed buffer's size.</param>
/// <param name="Signed">For an integer scalar, whether the managed type is signed (how a small one is re-extended).</param>
public sealed record SwiftResultView(
    SwiftResultKind Kind,
    string ManagedType,
    int Size,
    SwiftRegisterClass? Class,
    bool Signed,
    ImmutableArray<SwiftPlanPiece> Pieces)
{
    public bool Equals(SwiftResultView? other) =>
        other is not null && Kind == other.Kind && ManagedType == other.ManagedType && Size == other.Size
        && Class == other.Class && Signed == other.Signed && Pieces.AsSpan().SequenceEqual(other.Pieces.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Kind, ManagedType, Size, Pieces.Length);
}

/// <summary>
/// The managed view of a plan (design invariant I6): the twin's arguments in order, trailing
/// pointers included, with every aggregate piece's offset and width, and the result layout. The IL
/// is shared by every architecture, so this must be the same on each.
/// </summary>
public sealed record SwiftManagedView(ImmutableArray<SwiftTwinArgument> Arguments, SwiftResultView Result)
{
    public bool Equals(SwiftManagedView? other) =>
        other is not null && Result == other.Result && Arguments.AsSpan().SequenceEqual(other.Arguments.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Arguments.Length, Result);
}

/// <summary>A move a bridge makes before or after the call: from one operand to another.</summary>
public sealed record SwiftMove(SwiftTwinRole Role, SwiftOperand From, SwiftOperand To);

/// <summary>A direct result piece: the result register it comes back in, and the buffer bytes it fills.</summary>
public sealed record SwiftResultMove(SwiftOperand Register, int BufferOffset, int Size);

/// <summary>How the result comes back on one architecture.</summary>
/// <param name="Register">For a scalar, its register.</param>
/// <param name="ExtendTo32">For an integer scalar narrower than 32 bits: <c>zero</c> or <c>sign</c>, as the caller expects (forward: the C caller; reverse: Swift).</param>
public sealed record SwiftNativeResult(SwiftResultKind Kind, SwiftOperand? Register, string? ExtendTo32, ImmutableArray<SwiftResultMove> Moves)
{
    public bool Equals(SwiftNativeResult? other) =>
        other is not null && Kind == other.Kind && Register == other.Register && ExtendTo32 == other.ExtendTo32
        && Moves.AsSpan().SequenceEqual(other.Moves.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Kind, Register, Moves.Length);
}

/// <summary>
/// A bridge's frame on one architecture. On arm64: the registers saved in pairs from the frame
/// record down (<c>stp x29, x30</c> first, so the CFA is <c>fp + 16</c>), then <see cref="LocalBytes"/>
/// below them. On x86_64: <c>push rbp</c> then the saved registers in order, then
/// <see cref="LocalBytes"/> (alignment padding included).
/// </summary>
/// <param name="SavedRegisters">In save order; on arm64 consecutive entries are one pair.</param>
/// <param name="LocalBytes">The stack the bridge reserves below its saved registers: the outgoing argument area and its locals, keeping <c>sp</c> 16-aligned at the call.</param>
/// <param name="OutgoingStackBytes">The outgoing stack-argument area at the bottom of the locals, copied from (or built for) the callee.</param>
/// <param name="ErrorSlot">Forward: the register that holds the managed error slot's address across the call. Reverse: null (the slot is a frame local; see <see cref="SwiftNativePlan.AfterCall"/>).</param>
/// <param name="ResultBuffer">Forward: the register that holds the result buffer's address across the call.</param>
/// <param name="Locals">Reverse: the frame locals (error slot, result buffer), each as a <see cref="SwiftOperandKind.Local"/> operand.</param>
public sealed record SwiftFrame(
    ImmutableArray<string> SavedRegisters,
    int LocalBytes,
    int OutgoingStackBytes,
    string? ErrorSlot,
    string? ResultBuffer,
    ImmutableArray<SwiftMove> Locals)
{
    public bool Equals(SwiftFrame? other) =>
        other is not null && LocalBytes == other.LocalBytes && OutgoingStackBytes == other.OutgoingStackBytes
        && ErrorSlot == other.ErrorSlot && ResultBuffer == other.ResultBuffer
        && SavedRegisters.AsSpan().SequenceEqual(other.SavedRegisters.AsSpan())
        && Locals.AsSpan().SequenceEqual(other.Locals.AsSpan());

    public override int GetHashCode() => HashCode.Combine(LocalBytes, OutgoingStackBytes, SavedRegisters.Length);
}

/// <summary>
/// One architecture's native plan: where every argument arrives and where the callee reads it (the
/// prefix, proved equal), the stack bytes of the prefix (S_O), the special-register moves, the
/// result moves and the frame.
/// </summary>
/// <param name="Incoming">Where each argument arrives. Forward: one per twin argument, in order. Reverse: the closure's ordinary arguments, then each special register it receives.</param>
/// <param name="Outgoing">Where the callee reads each argument. Forward: one per ordinary argument the callee takes. Reverse: one per twin argument.</param>
/// <param name="StackPrefixBytes">S_O: the stack bytes the ordinary prefix occupies, which the bridge copies to its own outgoing area.</param>
/// <param name="Moves">The special values a bridge moves into place before the call, in order.</param>
/// <param name="ZeroError">
/// True when the error is zeroed before the call. Forward: the error register (the callee writes it
/// only when it throws). Reverse: the frame's error slot (the callback writes it only when it throws).
/// </param>
/// <param name="AfterCall">
/// The moves a bridge makes after the call returns, before it returns itself, in order. Reverse: the
/// error slot into the error register, which the C callback leaves as it found it. Forward: none (the
/// error register is stored through <see cref="SwiftFrame.ErrorSlot"/>).
/// </param>
public sealed record SwiftNativePlan(
    SwiftArchitecture Architecture,
    ImmutableArray<SwiftOperand> Incoming,
    ImmutableArray<SwiftOperand> Outgoing,
    int StackPrefixBytes,
    ImmutableArray<SwiftMove> Moves,
    bool ZeroError,
    SwiftNativeResult Result,
    ImmutableArray<SwiftMove> AfterCall,
    SwiftFrame Frame)
{
    public bool Equals(SwiftNativePlan? other) =>
        other is not null && Architecture == other.Architecture && StackPrefixBytes == other.StackPrefixBytes
        && ZeroError == other.ZeroError && Result == other.Result && Frame == other.Frame
        && Incoming.AsSpan().SequenceEqual(other.Incoming.AsSpan())
        && Outgoing.AsSpan().SequenceEqual(other.Outgoing.AsSpan())
        && Moves.AsSpan().SequenceEqual(other.Moves.AsSpan())
        && AfterCall.AsSpan().SequenceEqual(other.AfterCall.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Architecture, Incoming.Length, Outgoing.Length, StackPrefixBytes);
}

/// <summary>
/// Everything a bridge is made from, and nothing about who uses it: the kind, the target symbol (for
/// a forward call), the managed view and each architecture's native plan. Its canonical JSON is what
/// the plan's hash, and so the bridge's name, is computed from; two transitions with the same body
/// share one bridge.
/// </summary>
public sealed record SwiftCallPlanBody(
    SwiftPlanKind Kind,
    string? Target,
    SwiftManagedView Managed,
    ImmutableArray<SwiftNativePlan> Architectures)
{
    public bool Equals(SwiftCallPlanBody? other) =>
        other is not null && Kind == other.Kind && Target == other.Target && Managed == other.Managed
        && Architectures.AsSpan().SequenceEqual(other.Architectures.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Kind, Target, Managed);

    /// <summary>The canonical form: compact JSON, properties in declaration order, enums by name.</summary>
    public string Canonical() => JsonSerializer.Serialize(this, SwiftCallPlanCanonicalJsonContext.Default.SwiftCallPlanBody);

    /// <summary>
    /// The SHA-256, lower-case hex, of the plan version and the canonical form: a plan made under a
    /// different schema never shares a bridge name with this one.
    /// </summary>
    public string Sha256() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v{SwiftCallPlan.Version}:{Canonical()}"))).ToLowerInvariant();
}

/// <summary>A plan: its body, the hash of the body and the bridge symbol named after it.</summary>
public sealed record SwiftCallPlan(string Hash, string Symbol, SwiftCallPlanBody Body)
{
    /// <summary>The schema the plan was made under (<see cref="Version"/>).</summary>
    public int PlanVersion { get; init; } = Version;

    /// <summary>The plan schema. Bump it on any change to a plan's shape or meaning.</summary>
    public const int Version = 1;

    /// <summary>The length of the hash prefix a bridge symbol carries.</summary>
    public const int SymbolHashLength = 16;

    /// <summary>The prefix of every forward bridge symbol (thin and thick).</summary>
    public const string ForwardPrefix = "SBSR_";

    /// <summary>The prefix of every reverse bridge symbol.</summary>
    public const string ReversePrefix = "SBSRR_";

    /// <summary>True when <paramref name="entryPoint"/> names a forward bridge.</summary>
    public static bool IsForwardBridgeSymbol(string? entryPoint) =>
        entryPoint is not null && entryPoint.StartsWith(ForwardPrefix, StringComparison.Ordinal);

    public static SwiftCallPlan Of(SwiftCallPlanBody body)
    {
        var hash = body.Sha256();
        var prefix = body.Kind switch
        {
            SwiftPlanKind.Forward => ForwardPrefix,
            SwiftPlanKind.ForwardThick => ForwardPrefix + "thick_",
            _ => ReversePrefix,
        };
        return new SwiftCallPlan(hash, prefix + hash[..SymbolHashLength], body);
    }

    /// <summary>The twin's canonical parameter and return types, from the managed view (layout, not spelling).</summary>
    public SwiftTwinSignature Twin => SwiftTwinSignature.Of(Body.Managed);
}

/// <summary>The semantic conventions a plan was made under, from the facts: kept on each use, not in the shared plan.</summary>
public sealed record SwiftPlanSemantics(
    SwiftSelfConvention? Self,
    SwiftPassingOwnership SelfOwnership,
    ImmutableArray<string> Parameters,
    bool Throws,
    string Result)
{
    public bool Equals(SwiftPlanSemantics? other) =>
        other is not null && Self == other.Self && SelfOwnership == other.SelfOwnership && Throws == other.Throws
        && Result == other.Result && Parameters.AsSpan().SequenceEqual(other.Parameters.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Self, Throws, Result, Parameters.Length);
}

/// <summary>A planner's answer for one transition: a plan, or the refusals that stopped it.</summary>
public sealed record SwiftPlanOutcome(SwiftCallPlan? Plan, ImmutableArray<SwiftPlanRefusal> Refusals, SwiftPlanSemantics? Semantics)
{
    public bool IsPlanned => Plan is not null;

    /// <summary>
    /// When planned, the callee's predicted physical signature (normalized LLVM IR, the form the
    /// lowering oracle reads swiftc's definitions in): what the plan's evidence must show swiftc gives.
    /// </summary>
    public string? Callee { get; init; }

    /// <summary>When planned, what the managed caller of the twin must preserve around the call.</summary>
    public ImmutableArray<SwiftMarshallingObligation> Obligations { get; init; } = ImmutableArray<SwiftMarshallingObligation>.Empty;

    public static SwiftPlanOutcome Refused(params SwiftPlanRefusal[] refusals) =>
        new(null, refusals.ToImmutableArray(), null);
}

/// <summary>What one architecture's planning is given: its convention, its Swift lowering, and the lowering function L.</summary>
/// <param name="Lowering">How Swift passes each type on this architecture.</param>
/// <param name="AggregateLowering">The function L that splits a managed aggregate's layout into pieces on this architecture.</param>
public sealed record SwiftArchitectureModel(
    SwiftArchitecture Architecture,
    ISwiftValueLowering Lowering,
    Func<SwiftLeafLayout, ImmutableArray<SwiftLoweredPiece>> AggregateLowering)
{
    /// <summary>The two architectures bridges are emitted for, with one Swift lowering: L is the same on both 64-bit Apple targets.</summary>
    public static ImmutableArray<SwiftArchitectureModel> Default(ISwiftValueLowering lowering) =>
    [
        new(SwiftArchitecture.Arm64, lowering, SwiftAggregateLowering.Lower),
        new(SwiftArchitecture.X86_64, lowering, SwiftAggregateLowering.Lower),
    ];
}

/// <summary>
/// Plans a Swift transition's bridge (<see cref="SwiftCallPlan"/>) from its <see cref="SwiftCallFacts"/>
/// and its managed signature, on each architecture: it classifies the managed carriers, lowers them
/// with L, matches them piece by piece against the callee's predicted physical signature, assigns
/// both conventions, proves the prefix lands where the callee reads it, and records the special
/// moves, the result plan and the frame. It refuses, with a reason, anything it cannot prove; a
/// refusal on any architecture refuses the transition everywhere, as does a managed view that
/// differs between architectures.
/// </summary>
/// <remarks>
/// This is a prediction gate the freeze policy allows: the misplacement it prevents compiles.
/// </remarks>
public sealed class SwiftCallPlanner
{
    /// <summary>The context words a reverse stub reads (the closure context's layout, §5.10's target contract).</summary>
    public const int ContextTargetOffset = 16;

    public const int ContextPayloadOffset = 24;

    private readonly SwiftManagedCarrierClassifier _classifier;
    private readonly ImmutableArray<SwiftArchitectureModel> _architectures;

    public SwiftCallPlanner(SwiftManagedTypeIndex index, ImmutableArray<SwiftArchitectureModel> architectures)
    {
        _classifier = new SwiftManagedCarrierClassifier(index);
        _architectures = architectures;
    }

    public ImmutableArray<SwiftArchitectureModel> Architectures => _architectures;

    /// <summary>Plans a forward call of <paramref name="target"/>, or of a thick closure when it is null.</summary>
    public SwiftPlanOutcome PlanForward(SwiftCallFacts? facts, SwiftManagedSignature managed, string? target) =>
        Plan(target is null ? SwiftPlanKind.ForwardThick : SwiftPlanKind.Forward, facts, managed, target);

    /// <summary>Plans the reverse stub that calls the managed callback <paramref name="managed"/> as the closure <paramref name="facts"/> describes.</summary>
    public SwiftPlanOutcome PlanReverse(SwiftCallFacts? facts, SwiftManagedSignature managed) =>
        Plan(SwiftPlanKind.Reverse, facts, managed, null);

    private SwiftPlanOutcome Plan(SwiftPlanKind kind, SwiftCallFacts? facts, SwiftManagedSignature managed, string? target)
    {
        if (facts is null)
            return SwiftPlanOutcome.Refused(new SwiftPlanRefusal(SwiftPlanRefusalKind.Facts, "the transition has no facts"));
        if (SwiftSynchronousCall.Refusal(facts) is { } notSynchronous)
            return SwiftPlanOutcome.Refused(new SwiftPlanRefusal(SwiftPlanRefusalKind.NotSynchronous, notSynchronous));
        if (!facts.IsComplete)
            return SwiftPlanOutcome.Refused(new SwiftPlanRefusal(SwiftPlanRefusalKind.Facts, $"the facts are incomplete: {string.Join("; ", facts.Unknowns)}"));
        if (kind != SwiftPlanKind.Forward && facts.Target.Kind != SwiftCallTargetKind.ClosureInvocation)
            return SwiftPlanOutcome.Refused(new SwiftPlanRefusal(SwiftPlanRefusalKind.Facts, $"a {kind} plan needs a closure's facts, not a {facts.Target.Kind}'s"));
        if (_architectures.IsDefaultOrEmpty)
            return SwiftPlanOutcome.Refused(new SwiftPlanRefusal(SwiftPlanRefusalKind.Facts, "no architecture to plan for"));

        var refusals = new List<SwiftPlanRefusal>();
        foreach (var model in _architectures.Where(a => a.Architecture == SwiftArchitecture.Arm64e))
            refusals.Add(new SwiftPlanRefusal(SwiftPlanRefusalKind.Arm64e, "pointer-authenticated (arm64e) bridges are not emitted until their emission has tests", model.Architecture));

        // Step 1: classify the managed carriers (the same IL on every architecture).
        var parameters = managed.Parameters.Select(p => _classifier.Classify(p, managed)).ToImmutableArray();
        var returned = _classifier.ClassifyReturn(managed);
        foreach (var refused in parameters.Append(returned).Where(v => v.Kind == SwiftManagedValueKind.Refused))
            refusals.Add(refused.Refusal!);
        if (refusals.Count > 0)
            return Refuse(refusals);

        var views = new List<(SwiftArchitecture Architecture, SwiftManagedView View)>();
        var natives = ImmutableArray.CreateBuilder<SwiftNativePlan>();
        SwiftPlanSemantics? semantics = null;
        string? callee = null;
        foreach (var model in _architectures.Where(a => a.Architecture != SwiftArchitecture.Arm64e))
        {
            var attempt = PlanArchitecture(kind, facts, managed, parameters, returned, model);
            if (attempt.Refusal is { } refusal)
            {
                refusals.Add(refusal with { Architecture = model.Architecture });
                continue;
            }
            views.Add((model.Architecture, attempt.View!));
            natives.Add(attempt.Native!);
            semantics ??= attempt.Semantics;
            callee ??= attempt.Callee;
        }

        // I6: the managed view is the IL, which every architecture shares.
        if (views.Count > 1)
        {
            var first = views[0];
            foreach (var other in views.Skip(1))
            {
                if (other.View != first.View)
                    refusals.Add(new SwiftPlanRefusal(SwiftPlanRefusalKind.ManagedViewDisagreement,
                        $"the managed view on {other.Architecture} ({Describe(other.View)}) differs from {first.Architecture}'s ({Describe(first.View)})"));
            }
        }
        if (refusals.Count > 0)
            return Refuse(refusals);

        var body = new SwiftCallPlanBody(kind, kind == SwiftPlanKind.Forward ? target : null, views[0].View, natives.ToImmutable());
        var lowering = _architectures.First(a => a.Architecture != SwiftArchitecture.Arm64e).Lowering;
        return new SwiftPlanOutcome(SwiftCallPlan.Of(body), ImmutableArray<SwiftPlanRefusal>.Empty, semantics)
        {
            Callee = callee,
            Obligations = SwiftMarshallingObligations.Of(kind, facts, managed, parameters, returned, views[0].View, lowering),
        };
    }

    private static SwiftPlanOutcome Refuse(List<SwiftPlanRefusal> refusals) =>
        new(null, refusals.Distinct().ToImmutableArray(), null);

    private static string Describe(SwiftManagedView view) =>
        string.Join(", ", view.Arguments.Select(a => a.Source == SwiftPieceSource.Piece ? $"{a.Role}:{a.Class}{a.Size}@{a.Offset}" : $"{a.Role}:{a.Class}{a.Size}"))
        + $" -> {view.Result.Kind}";

    private sealed record ArchitectureAttempt(SwiftManagedView? View, SwiftNativePlan? Native, SwiftPlanSemantics? Semantics, SwiftPlanRefusal? Refusal)
    {
        public string? Callee { get; init; }

        public static ArchitectureAttempt Fail(SwiftPlanRefusalKind kind, string reason) => new(null, null, null, new SwiftPlanRefusal(kind, reason));
    }

    /// <summary>Steps 2–5 on one architecture.</summary>
    private ArchitectureAttempt PlanArchitecture(
        SwiftPlanKind kind, SwiftCallFacts facts, SwiftManagedSignature managed, ImmutableArray<SwiftManagedValue> parameters, SwiftManagedValue returned, SwiftArchitectureModel model)
    {
        var convention = SwiftCallingConvention.For(model.Architecture);

        // The callee's physical signature.
        var prediction = SwiftPhysicalSignaturePredictor.Predict(facts, model.Lowering);
        if (!prediction.IsPredicted)
            return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.Lowering, prediction.Refusal!);
        var physical = prediction.Signature!;
        bool swiftIndirect = physical.Arguments.Any(a => a.Role == SwiftPhysicalArgumentRole.IndirectResult);
        bool swiftSelf = physical.Arguments.Any(a => a.Role == SwiftPhysicalArgumentRole.Context && a.Detail != "unused");
        bool swiftError = physical.Arguments.Any(a => a.Role == SwiftPhysicalArgumentRole.Error);
        var swiftOrdinary = physical.Arguments
            .Where(a => a.Role is not (SwiftPhysicalArgumentRole.IndirectResult or SwiftPhysicalArgumentRole.Context or SwiftPhysicalArgumentRole.Error))
            .ToList();
        var swiftSlots = new List<SwiftArgumentSlot>();
        foreach (var argument in swiftOrdinary)
        {
            if (SwiftCallingConvention.SlotOfIrType(argument.IrType) is not { } slot)
                return ArchitectureAttempt.Fail(
                    argument.IrType.StartsWith('<') ? SwiftPlanRefusalKind.Vector : SwiftPlanRefusalKind.UnsupportedScalar,
                    $"the callee takes a {argument.IrType} argument, which is not one scalar register");
            swiftSlots.Add(slot);
        }

        // The managed view: ordinary pieces, then the trailing pointers in the encoding's order.
        var ordinary = new List<SwiftTwinArgument>();
        int? indirectParameter = null, selfParameter = null, errorParameter = null;
        for (int i = 0; i < parameters.Length; i++)
        {
            var value = parameters[i];
            switch (value.Kind)
            {
                case SwiftManagedValueKind.IndirectResult:
                    if (indirectParameter is not null)
                        return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.SpecialRegisterMismatch, "the managed signature takes two indirect results");
                    indirectParameter = i;
                    break;
                case SwiftManagedValueKind.Self:
                    if (selfParameter is not null)
                        return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.SpecialRegisterMismatch, "the managed signature takes two selves");
                    selfParameter = i;
                    break;
                case SwiftManagedValueKind.Error:
                    if (errorParameter is not null)
                        return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.SpecialRegisterMismatch, "the managed signature takes two errors");
                    errorParameter = i;
                    break;
                case SwiftManagedValueKind.Primitive:
                {
                    var leaf = value.Layout!.Leaves[0];
                    ordinary.Add(new SwiftTwinArgument(SwiftTwinRole.Ordinary, i, value.Type, SwiftPieceSource.Value, 0, leaf.Size,
                        ClassOf(leaf.Kind), value.Signed && IsInteger(leaf.Kind)));
                    break;
                }
                case SwiftManagedValueKind.Aggregate:
                {
                    var pieces = Lower(model, value.Layout!, out var lowerError);
                    if (lowerError is not null)
                        return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.ExplicitLayout, $"parameter {i} ({value.Type}): {lowerError}");
                    if (SwiftAggregateLowering.PassesIndirectly(pieces))
                        ordinary.Add(new SwiftTwinArgument(SwiftTwinRole.Ordinary, i, value.Type, SwiftPieceSource.Address, 0, 8, SwiftRegisterClass.General));
                    else if (PastTheEnd(pieces, value.ManagedSize) is { } overrun)
                        return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.PrefixMismatch, $"parameter {i} ({value.Type}) {overrun}");
                    else
                        ordinary.AddRange(pieces.Select(p => new SwiftTwinArgument(SwiftTwinRole.Ordinary, i, value.Type, SwiftPieceSource.Piece,
                            p.Offset, p.Size, ClassOf(p.Kind))));
                    break;
                }
                default:
                    return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.NonBlittable, $"parameter {i} ({value.Type}) is a {value.Kind}");
            }
        }

        // The result, managed side.
        var resultView = ManagedResult(returned, indirectParameter, model, out var resultError);
        if (resultError is not null)
            return ArchitectureAttempt.Fail(resultError.Value.Kind, resultError.Value.Reason);

        // Match the result with the callee's.
        bool managedIndirect = resultView!.Kind == SwiftResultKind.Indirect;
        if (managedIndirect != swiftIndirect)
            return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.ResultMismatch, managedIndirect
                ? $"the managed signature takes an indirect result ({resultView.ManagedType}); the callee returns {physical.Result} directly"
                : $"the callee returns indirectly; the managed signature returns {resultView.ManagedType}");
        var swiftResultPieces = physical.ResultPieces;
        switch (resultView.Kind)
        {
            case SwiftResultKind.Void when physical.Result != "void":
                return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.ResultMismatch, $"the managed signature returns nothing; the callee returns {physical.Result}");
            case SwiftResultKind.Scalar:
            {
                if (swiftResultPieces.Length != 1)
                    return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.ResultMismatch, $"the managed signature returns one {resultView.ManagedType}; the callee returns {physical.Result}");
                var slot = SwiftCallingConvention.SlotOf(swiftResultPieces[0]);
                if (slot.Class != resultView.Class || slot.Size != resultView.Size)
                    return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.ResultMismatch, $"the managed result {resultView.ManagedType} ({resultView.Class} {resultView.Size}) is not the callee's {physical.Result}");
                break;
            }
            case SwiftResultKind.Direct:
            {
                if (swiftResultPieces.Length != resultView.Pieces.Length)
                    return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.ResultMismatch, $"the managed result {resultView.ManagedType} lowers to {resultView.Pieces.Length} piece(s); the callee returns {physical.Result}");
                for (int i = 0; i < swiftResultPieces.Length; i++)
                {
                    var swiftPiece = swiftResultPieces[i];
                    var managedPiece = resultView.Pieces[i];
                    var slot = SwiftCallingConvention.SlotOf(swiftPiece);
                    if (slot.Class != managedPiece.Class || slot.Size != managedPiece.Size || swiftPiece.Offset != managedPiece.Offset)
                        return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.ResultMismatch,
                            $"result piece {i}: the managed {resultView.ManagedType} has {managedPiece.Class} {managedPiece.Size} at {managedPiece.Offset}; the callee returns {swiftPiece.IrType} at {swiftPiece.Offset}");
                }
                break;
            }
        }

        // Match the special registers.
        bool managedSelf = selfParameter is not null;
        if (kind == SwiftPlanKind.ForwardThick && !managedSelf)
            return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.SpecialRegisterMismatch, "a thick closure call passes no context");
        if (managedSelf != swiftSelf)
            return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.SpecialRegisterMismatch, managedSelf
                ? "the managed signature passes a self the callee does not take"
                : "the callee takes a self the managed signature does not pass");
        if ((errorParameter is not null) != swiftError)
            return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.SpecialRegisterMismatch, swiftError
                ? "the callee throws but the managed signature has no error"
                : "the managed signature takes an error but the callee does not throw");

        // Match the ordinary prefix piece by piece: class, size, and the bytes each carries.
        if (ordinary.Count < swiftOrdinary.Count)
            return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.PrefixMismatch,
                $"the managed signature passes {ordinary.Count} ordinary argument(s); the callee takes {swiftOrdinary.Count} ({physical.Normalized})");
        for (int i = 0; i < swiftOrdinary.Count; i++)
        {
            var mine = ordinary[i];
            var theirs = swiftSlots[i];
            if (mine.Class != theirs.Class || mine.Size != theirs.Size)
                return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.PrefixMismatch,
                    $"argument {i}: the managed {mine.ManagedType} gives {mine.Class} {mine.Size}; the callee takes {swiftOrdinary[i].IrType} ({swiftOrdinary[i].Role})");
            if (mine.Source == SwiftPieceSource.Piece && swiftOrdinary[i].Piece is { } swiftPiece && swiftPiece.Offset != mine.Offset)
                return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.PrefixMismatch,
                    $"argument {i}: the managed {mine.ManagedType} piece is at offset {mine.Offset}; the callee's is at {swiftPiece.Offset}");
            if (RoleConflict(managed, mine, swiftOrdinary[i]) is { } conflict)
                return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.PrefixMismatch, $"argument {i}: {conflict} ({physical.Normalized})");
        }
        // The managed declaration may pass more than the callee reads. A forward plan carries the
        // surplus only when the facts show what the callee takes in its place; anything else is a
        // misbinding that happens to fit.
        if (ordinary.Count > swiftOrdinary.Count)
        {
            var surplus = ordinary.Skip(swiftOrdinary.Count).ToList();
            string? why = null;
            if (kind != SwiftPlanKind.Reverse)
            {
                why = UnreadReason(facts, managed, surplus);
                if (why is null)
                    return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.PrefixMismatch,
                        $"the managed signature passes {surplus.Count} ordinary argument(s) past the {swiftOrdinary.Count} the callee takes "
                        + $"({string.Join(", ", surplus.Select(a => a.Parameter is { } p ? managed.Parameters[p].Name : a.ManagedType).Distinct())}), "
                        + $"and the facts do not show them benign ({physical.Normalized})");
            }
            for (int i = swiftOrdinary.Count; i < ordinary.Count; i++)
                ordinary[i] = ordinary[i] with { Unread = true, UnreadReason = why };
        }

        var semantics = Semantics(facts, model.Lowering);
        var attempt = kind == SwiftPlanKind.Reverse
            ? Reverse(convention, ordinary, swiftSlots, resultView, errorParameter, parameters, semantics)
            : Forward(kind, convention, ordinary, swiftSlots, resultView, indirectParameter, selfParameter, errorParameter, parameters, semantics);
        return attempt with { Callee = physical.Normalized };
    }

    /// <summary>
    /// Why a managed argument cannot be what the callee reads in this slot, or null. Class and size
    /// cannot tell one pointer from another: a type's metadata and a witness table are both a
    /// <c>ptr</c>, so a declaration that drops one table and passes a trailing metatype instead
    /// fits the callee register for register while handing it the wrong object. The generated
    /// declarations name these arguments for what they are (<c>metatype</c>, <c>…Metadata</c>,
    /// <c>…PWT</c>); a name that says metadata in a witness-table slot, or a witness table in a
    /// metadata slot, is that misbinding. Only a contradiction refuses: an argument with any other
    /// name, or in any other slot, is left to the class and size match.
    /// </summary>
    internal static string? RoleConflict(SwiftManagedSignature managed, SwiftTwinArgument argument, SwiftPhysicalArgument slot)
    {
        if (argument.Source != SwiftPieceSource.Value || argument.Parameter is not { } p || p < 0 || p >= managed.Parameters.Length)
            return null;
        var name = managed.Parameters[p].Name;
        bool namesMetadata = name == "metatype" || (name.Length > "Metadata".Length && name.EndsWith("Metadata", StringComparison.Ordinal));
        bool namesWitnessTable = name.Length > "PWT".Length && name.EndsWith("PWT", StringComparison.Ordinal);
        var detail = slot.Detail is { } d ? $" {d}" : "";
        if (namesMetadata && slot.Role is SwiftPhysicalArgumentRole.WitnessTable or SwiftPhysicalArgumentRole.WitnessSelfTable)
            return $"the managed '{name}' is type metadata; the callee reads a witness table here ({slot.Role}{detail})";
        if (namesWitnessTable && slot.Role is SwiftPhysicalArgumentRole.GenericMetadata or SwiftPhysicalArgumentRole.SelfTypeMetadata or SwiftPhysicalArgumentRole.WitnessSelfMetadata)
            return $"the managed '{name}' is a witness table; the callee reads type metadata here ({slot.Role}{detail})";
        return null;
    }

    /// <summary>The reason a self that is a thin metatype makes a trailing metatype argument benign.</summary>
    public const string ThinMetatypeReason =
        "the callee's self is a thin metatype, which occupies no register: the managed declaration's trailing metatype is not read";

    /// <summary>The reason a self that carries its type's metadata makes trailing type-metadata arguments benign.</summary>
    public const string FulfilledMetadataReason =
        "the callee recovers its type's generic arguments from self: the managed declaration's trailing type-metadata arguments are not read";

    /// <summary>
    /// Why the ordinary arguments the callee does not take are benign to pass, or null. Each must be a
    /// whole pointer-sized value, and the facts must show what the callee reads instead: a thin
    /// metatype self, which takes no register (the managed declaration passes it as a trailing
    /// <c>metatype</c>); or a self that carries the type's metadata (a class instance's isa, or the
    /// self-type metadata a generic value's member takes), which fulfils the type-level generic
    /// arguments the managed declaration passes as <c>…Metadata</c>. The names only corroborate the
    /// structure; neither is enough alone.
    /// </summary>
    internal static string? UnreadReason(SwiftCallFacts facts, SwiftManagedSignature managed, IReadOnlyList<SwiftTwinArgument> surplus)
    {
        if (surplus.Count == 0)
            return null;
        if (surplus.Any(a => a.Source != SwiftPieceSource.Value || a.Class != SwiftRegisterClass.General || a.Size != 8
                || a.Parameter is not { } p || p < 0 || p >= managed.Parameters.Length || managed.Parameters[p].RefKind != SwiftManagedRefKind.None))
            return null;
        var names = surplus.Select(a => managed.Parameters[a.Parameter!.Value].Name).ToList();
        if (facts.Self.Kind == SwiftCallSelfKind.ThinMetatype && surplus.Count == 1 && names[0] == "metatype")
            return ThinMetatypeReason;
        int typeLevel = facts.Generics.IsEmpty ? 0 : facts.Generics.Parameters.Length - facts.Generics.OwnParameters.Length;
        if (facts.Self.Kind is SwiftCallSelfKind.Reference or SwiftCallSelfKind.Value
            && typeLevel > 0 && surplus.Count <= typeLevel
            && names.All(n => n.Length > "Metadata".Length && n.EndsWith("Metadata", StringComparison.Ordinal)))
            return FulfilledMetadataReason;
        return null;
    }

    /// <summary>
    /// Why a managed value of <paramref name="managedSize"/> bytes cannot travel as these pieces, or
    /// null. Swift widens a run of small fields to a power of two (an Int32 and a tag byte are one
    /// eight-byte integer), so a piece can run past the managed value's end. Copying only the value's
    /// bytes is sound only if the Swift value ends there too, and the facts carry its pieces but not
    /// its size: a five-byte carrier would match a Swift Int64 as well.
    /// </summary>
    private static string? PastTheEnd(ImmutableArray<SwiftLoweredPiece> pieces, int managedSize) =>
        pieces.FirstOrDefault(p => p.Offset + p.Size > managedSize) is { Size: > 0 } piece
            ? $"is {managedSize} bytes, and its piece at offset {piece.Offset} spans {piece.Size}, past its end"
            : null;

    private static ImmutableArray<SwiftLoweredPiece> Lower(SwiftArchitectureModel model, SwiftLeafLayout layout, out string? error)
    {
        error = null;
        try
        {
            return model.AggregateLowering(layout);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return ImmutableArray<SwiftLoweredPiece>.Empty;
        }
    }

    private static SwiftResultView? ManagedResult(SwiftManagedValue returned, int? indirectParameter, SwiftArchitectureModel model, out (SwiftPlanRefusalKind Kind, string Reason)? error)
    {
        error = null;
        var empty = ImmutableArray<SwiftPlanPiece>.Empty;
        switch (returned.Kind)
        {
            case SwiftManagedValueKind.Void:
                return indirectParameter is not null
                    ? new SwiftResultView(SwiftResultKind.Indirect, "SwiftIndirectResult", 0, null, false, empty)
                    : new SwiftResultView(SwiftResultKind.Void, returned.Type, 0, null, false, empty);
            case SwiftManagedValueKind.Primitive when indirectParameter is null:
            {
                var leaf = returned.Layout!.Leaves[0];
                return new SwiftResultView(SwiftResultKind.Scalar, returned.Type, leaf.Size, ClassOf(leaf.Kind), returned.Signed && IsInteger(leaf.Kind), empty);
            }
            case SwiftManagedValueKind.Aggregate when indirectParameter is null:
            {
                var pieces = Lower(model, returned.Layout!, out var lowerError);
                if (lowerError is not null)
                {
                    error = (SwiftPlanRefusalKind.ExplicitLayout, $"result ({returned.Type}): {lowerError}");
                    return null;
                }
                // More than four pieces come back through an address the caller supplies.
                if (SwiftAggregateLowering.PassesIndirectly(pieces))
                    return new SwiftResultView(SwiftResultKind.Indirect, returned.Type, returned.ManagedSize, null, false, empty);
                if (pieces.IsEmpty)
                    return new SwiftResultView(SwiftResultKind.Void, returned.Type, 0, null, false, empty);
                if (PastTheEnd(pieces, returned.ManagedSize) is { } overrun)
                {
                    error = (SwiftPlanRefusalKind.ResultMismatch, $"the result ({returned.Type}) {overrun}");
                    return null;
                }
                return new SwiftResultView(SwiftResultKind.Direct, returned.Type, returned.ManagedSize, null, false,
                    pieces.Select(p => new SwiftPlanPiece(p.Offset, p.Size, ClassOf(p.Kind))).ToImmutableArray());
            }
            default:
                error = (SwiftPlanRefusalKind.SpecialRegisterMismatch, $"the managed signature returns {returned.Type} and also takes an indirect result");
                return null;
        }
    }

    /// <summary>The forward bridge: the prefix stays put; the trailing pointers move into the special registers.</summary>
    private static ArchitectureAttempt Forward(
        SwiftPlanKind kind, SwiftCallingConvention convention, List<SwiftTwinArgument> ordinary, List<SwiftArgumentSlot> swiftSlots,
        SwiftResultView result, int? indirectParameter, int? selfParameter, int? errorParameter,
        ImmutableArray<SwiftManagedValue> parameters, SwiftPlanSemantics semantics)
    {
        var arguments = new List<SwiftTwinArgument>(ordinary);
        SwiftTwinArgument Trailing(SwiftTwinRole role, int? parameter, string type) =>
            new(role, parameter, type, SwiftPieceSource.Trailing, 0, 8, SwiftRegisterClass.General);
        if (result.Kind == SwiftResultKind.Indirect)
            arguments.Add(Trailing(SwiftTwinRole.IndirectResult, indirectParameter, indirectParameter is null ? result.ManagedType : parameters[indirectParameter.Value].Type));
        else if (result.Kind == SwiftResultKind.Direct)
            arguments.Add(Trailing(SwiftTwinRole.ResultBuffer, null, result.ManagedType));
        if (selfParameter is { } self)
            arguments.Add(Trailing(SwiftTwinRole.Self, self, parameters[self].Type));
        if (errorParameter is { } error)
            arguments.Add(Trailing(SwiftTwinRole.Error, error, parameters[error].Type));
        if (kind == SwiftPlanKind.ForwardThick)
            arguments.Add(Trailing(SwiftTwinRole.Function, null, "function pointer"));

        // Step 3: assign. Incoming is the C convention over the whole twin; outgoing is swiftcc over
        // the callee's ordinary arguments.
        var (incoming, _) = convention.Assign(arguments.Select(a => a.Slot));
        var (outgoing, _) = convention.Assign(swiftSlots);
        var (prefix, prefixStack) = convention.Assign(ordinary.Select(a => a.Slot));

        // Step 4: prove the prefix lands where the callee reads it.
        for (int i = 0; i < outgoing.Length; i++)
        {
            if (incoming[i] != outgoing[i])
                return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.PrefixMismatch,
                    $"argument {i} arrives in {incoming[i]} but the callee reads it from {outgoing[i]}");
        }
        for (int i = 0; i < prefix.Length; i++)
        {
            if (incoming[i] != prefix[i])
                return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.PrefixMismatch, $"argument {i} moves when the trailing pointers are added");
        }

        // Step 5: record the moves, the result and the frame.
        bool usesSelf = selfParameter is not null, throws = errorParameter is not null;
        bool usesBuffer = result.Kind == SwiftResultKind.Direct;
        string? errorSlot = null, bufferRegister = null;
        var saved = new List<string>();
        if (convention.Architecture == SwiftArchitecture.Arm64)
        {
            saved.AddRange(["x29", "x30"]);
            if (usesSelf || throws)
                saved.AddRange(["x20", "x19"]);
            if (throws || usesBuffer)
                saved.AddRange(["x22", "x21"]);
            if (throws)
                errorSlot = "x19";
            if (usesBuffer)
                bufferRegister = "x22";
        }
        else
        {
            saved.Add("rbp");
            if (throws)
                saved.AddRange(["rbx", "r12"]);
            if (usesSelf)
                saved.Add("r13");
            if (usesBuffer)
                saved.Add("r14");
            if (throws)
                errorSlot = "rbx";
            if (usesBuffer)
                bufferRegister = "r14";
        }

        var moves = ImmutableArray.CreateBuilder<SwiftMove>();
        for (int i = ordinary.Count; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var to = argument.Role switch
            {
                SwiftTwinRole.IndirectResult => convention.IndirectResult,
                SwiftTwinRole.ResultBuffer => bufferRegister!,
                SwiftTwinRole.Self => convention.Self,
                SwiftTwinRole.Error => errorSlot!,
                SwiftTwinRole.Function => convention.Function,
                _ => throw new InvalidOperationException($"no destination for a trailing {argument.Role}"),
            };
            moves.Add(new SwiftMove(argument.Role, incoming[i], SwiftOperand.InRegister(to, 8)));
        }

        var native = ResultPlan(convention, result, bufferRegister);
        if (native is null)
            return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.ResultMismatch, "the direct result needs more result registers than swiftcc has");

        int outgoingBytes = SwiftLeafLayout.AlignUp(prefixStack, 8);
        int localBytes = convention.Architecture == SwiftArchitecture.Arm64
            ? SwiftLeafLayout.AlignUp(outgoingBytes, 16)
            // After the return address and the pushes, sp is 16-aligned at the call.
            : SwiftLeafLayout.AlignUp(outgoingBytes + 8 * (saved.Count - 1), 16) - 8 * (saved.Count - 1);
        var frame = new SwiftFrame(saved.ToImmutableArray(), localBytes, outgoingBytes, errorSlot, bufferRegister, ImmutableArray<SwiftMove>.Empty);
        var view = new SwiftManagedView(arguments.ToImmutableArray(), result);
        var plan = new SwiftNativePlan(convention.Architecture, incoming, outgoing, prefixStack, moves.ToImmutable(), throws, native, ImmutableArray<SwiftMove>.Empty, frame);
        return new ArchitectureAttempt(view, plan, semantics, null);
    }

    /// <summary>
    /// The reverse stub: the closure's ordinary arguments stay put; the managed callback's trailing
    /// arguments (indirect result or result buffer, error slot, payload) follow them, and its target
    /// is read from the context.
    /// </summary>
    private static ArchitectureAttempt Reverse(
        SwiftCallingConvention convention, List<SwiftTwinArgument> ordinary, List<SwiftArgumentSlot> swiftSlots,
        SwiftResultView result, int? errorParameter,
        ImmutableArray<SwiftManagedValue> parameters, SwiftPlanSemantics semantics)
    {
        // The context is matched already: a closure always takes one, in the self register.
        if (ordinary.Any(a => a.Unread))
            return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.PrefixMismatch, "the managed callback takes more ordinary arguments than the closure passes");

        var arguments = new List<SwiftTwinArgument>(ordinary);
        SwiftTwinArgument Trailing(SwiftTwinRole role, int? parameter, string type) =>
            new(role, parameter, type, SwiftPieceSource.Trailing, 0, 8, SwiftRegisterClass.General);
        if (result.Kind == SwiftResultKind.Indirect)
            arguments.Add(Trailing(SwiftTwinRole.IndirectResult, null, result.ManagedType));
        else if (result.Kind == SwiftResultKind.Direct)
            arguments.Add(Trailing(SwiftTwinRole.ResultBuffer, null, result.ManagedType));
        if (errorParameter is { } error)
            arguments.Add(Trailing(SwiftTwinRole.Error, error, parameters[error].Type));
        arguments.Add(Trailing(SwiftTwinRole.Payload, null, "payload"));

        // Incoming is swiftcc over the closure's arguments plus its special registers; outgoing is the
        // C convention over the callback twin.
        var (swiftIncoming, prefixStack) = convention.Assign(swiftSlots);
        var (outgoing, outgoingStack) = convention.Assign(arguments.Select(a => a.Slot));
        for (int i = 0; i < swiftIncoming.Length; i++)
        {
            if (swiftIncoming[i] != outgoing[i])
                return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.PrefixMismatch,
                    $"argument {i} arrives in {swiftIncoming[i]} but the callback reads it from {outgoing[i]}");
        }

        bool throws = errorParameter is not null;
        int outgoingBytes = SwiftLeafLayout.AlignUp(outgoingStack, 8);
        int cursor = outgoingBytes;
        var locals = ImmutableArray.CreateBuilder<SwiftMove>();
        SwiftOperand? errorLocal = null, bufferLocal = null;
        if (throws)
        {
            errorLocal = new SwiftOperand(SwiftOperandKind.Local, null, cursor, 8);
            locals.Add(new SwiftMove(SwiftTwinRole.Error, errorLocal, errorLocal));
            cursor += 8;
        }
        if (result.Kind == SwiftResultKind.Direct)
        {
            cursor = SwiftLeafLayout.AlignUp(cursor, 8);
            bufferLocal = new SwiftOperand(SwiftOperandKind.Local, null, cursor, result.Size);
            locals.Add(new SwiftMove(SwiftTwinRole.ResultBuffer, bufferLocal, bufferLocal));
            cursor += SwiftLeafLayout.AlignUp(result.Size, 8);
        }

        var incoming = swiftIncoming.ToBuilder();
        if (result.Kind == SwiftResultKind.Indirect)
            incoming.Add(SwiftOperand.InRegister(convention.IndirectResult, 8));
        incoming.Add(SwiftOperand.InRegister(convention.Self, 8));
        if (throws)
            incoming.Add(SwiftOperand.InRegister(convention.Error, 8));

        var context = convention.Self;
        var moves = ImmutableArray.CreateBuilder<SwiftMove>();
        for (int i = ordinary.Count; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var from = argument.Role switch
            {
                SwiftTwinRole.IndirectResult => SwiftOperand.InRegister(convention.IndirectResult, 8),
                SwiftTwinRole.ResultBuffer => bufferLocal!,
                SwiftTwinRole.Error => errorLocal!,
                SwiftTwinRole.Payload => new SwiftOperand(SwiftOperandKind.ContextField, context, ContextPayloadOffset, 8),
                _ => throw new InvalidOperationException($"no source for a trailing {argument.Role}"),
            };
            moves.Add(new SwiftMove(argument.Role, from, outgoing[i]));
        }
        moves.Add(new SwiftMove(SwiftTwinRole.Function,
            new SwiftOperand(SwiftOperandKind.ContextField, context, ContextTargetOffset, 8), SwiftOperand.InRegister(convention.Function, 8)));

        // The callback returns void or one scalar in the C convention; a direct multi-piece result
        // comes back through the buffer and is loaded into swiftcc's result registers.
        SwiftNativeResult native;
        switch (result.Kind)
        {
            case SwiftResultKind.Scalar:
                native = new SwiftNativeResult(SwiftResultKind.Scalar, SwiftOperand.InRegister(convention.ScalarResult(result.Class!.Value), result.Size), ExtendTo32(result), ImmutableArray<SwiftResultMove>.Empty);
                break;
            case SwiftResultKind.Direct:
                var registers = convention.AssignResult(result.Pieces.Select(p => new SwiftArgumentSlot(p.Class, p.Size)));
                if (registers is null)
                    return ArchitectureAttempt.Fail(SwiftPlanRefusalKind.ResultMismatch, "the direct result needs more result registers than swiftcc has");
                native = new SwiftNativeResult(SwiftResultKind.Direct, null, null,
                    result.Pieces.Select((p, i) => new SwiftResultMove(registers.Value[i], p.Offset, p.Size)).ToImmutableArray());
                break;
            default:
                native = new SwiftNativeResult(result.Kind, null, null, ImmutableArray<SwiftResultMove>.Empty);
                break;
        }

        var saved = convention.Architecture == SwiftArchitecture.Arm64 ? ImmutableArray.Create("x29", "x30") : ImmutableArray.Create("rbp");
        int localBytes = SwiftLeafLayout.AlignUp(cursor, 16);
        var frame = new SwiftFrame(saved, localBytes, outgoingBytes, null, null, locals.ToImmutable());
        var view = new SwiftManagedView(arguments.ToImmutableArray(), result);
        var afterCall = throws
            ? ImmutableArray.Create(new SwiftMove(SwiftTwinRole.Error, errorLocal!, SwiftOperand.InRegister(convention.Error, 8)))
            : ImmutableArray<SwiftMove>.Empty;
        var plan = new SwiftNativePlan(convention.Architecture, incoming.ToImmutable(), outgoing, prefixStack, moves.ToImmutable(), throws, native, afterCall, frame);
        return new ArchitectureAttempt(view, plan, semantics, null);
    }

    /// <summary>
    /// How an integer scalar result narrower than 32 bits is re-extended in its register: what was
    /// written may leave the upper bits undefined (System V does), and the reader expects them extended.
    /// </summary>
    private static string? ExtendTo32(SwiftResultView result) =>
        result.Class == SwiftRegisterClass.General && result.Size < 4 ? (result.Signed ? "sign" : "zero") : null;

    /// <summary>The forward result plan: the scalar register and its re-extension, or each piece's register and buffer bytes.</summary>
    private static SwiftNativeResult? ResultPlan(SwiftCallingConvention convention, SwiftResultView result, string? bufferRegister)
    {
        switch (result.Kind)
        {
            case SwiftResultKind.Scalar:
            {
                var register = SwiftOperand.InRegister(convention.ScalarResult(result.Class!.Value), result.Size);
                return new SwiftNativeResult(SwiftResultKind.Scalar, register, ExtendTo32(result), ImmutableArray<SwiftResultMove>.Empty);
            }
            case SwiftResultKind.Direct:
            {
                var registers = convention.AssignResult(result.Pieces.Select(p => new SwiftArgumentSlot(p.Class, p.Size)));
                if (registers is null)
                    return null;
                return new SwiftNativeResult(SwiftResultKind.Direct, null, null,
                    result.Pieces.Select((p, i) => new SwiftResultMove(registers.Value[i], p.Offset, p.Size)).ToImmutableArray());
            }
            default:
                return new SwiftNativeResult(result.Kind, null, null, ImmutableArray<SwiftResultMove>.Empty);
        }
    }

    private static SwiftPlanSemantics Semantics(SwiftCallFacts facts, ISwiftValueLowering lowering)
    {
        var self = SwiftPassingClassifier.SelfConvention(facts, lowering);
        var parameters = SwiftPassingClassifier.ParameterPassings(facts, lowering)
            .Select(p => p.Kind is { } k ? $"{k}/{p.Ownership}" : $"refused: {p.Refusal}")
            .ToImmutableArray();
        string result;
        if (facts.Result.IsVoid)
            result = "Void";
        else
        {
            var physical = SwiftPassingClassifier.PhysicalResult(facts.Result.Type, facts.Generics, lowering);
            result = physical.Outcome.ToString();
        }
        return new SwiftPlanSemantics(self.Convention, self.Ownership, parameters, facts.Effects.Throws, result);
    }

    private static SwiftRegisterClass ClassOf(SwiftScalarKind kind) =>
        kind is SwiftScalarKind.Float or SwiftScalarKind.Double ? SwiftRegisterClass.Float : SwiftRegisterClass.General;

    private static bool IsInteger(SwiftScalarKind kind) =>
        kind is SwiftScalarKind.Int8 or SwiftScalarKind.Int16 or SwiftScalarKind.Int32 or SwiftScalarKind.Int64;
}
