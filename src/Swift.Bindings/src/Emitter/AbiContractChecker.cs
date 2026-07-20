// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Post-generation ABI contract checker.
// Validates generated C# P/Invoke declarations against ABI safety rules. Every
// violation it reports fails generation: an emitted binding that binds a Swift
// symbol under the wrong convention, library, or argument carrier compiles
// cleanly and then misbehaves at the first call, which is precisely the outcome
// generation must never ship.
//
// Checks implemented:
//   SWIFTBIND090 (CC-001): CallConvSwift param whose carrier can't match Swift's
//   SWIFTBIND091 (CC-002): CallConvSwift return whose carrier can't match Swift's
//   SWIFTBIND092 (Tj):     Dispatch thunk bound to a library that can't export it
//   SWIFTBIND093 (CC-003): @_cdecl wrapper entry point targeting original library
//   SWIFTBIND094 (CC-004): CallConvCdecl targeting mangled Swift symbol
//
// WHAT THIS CHECKER CAN AND CANNOT SEE. It reads emitted text, so it reasons about
// symbol/convention/library pairing — never about Swift's declared signature. Two
// consequences shape every rule below:
//
//   1. The declared C# signature is NOT what reaches the Swift trampoline. Every
//      P/Invoke here is [LibraryImport], whose source generator marshals each
//      argument in managed code and forwards [UnmanagedCallConv] to an extern
//      whose parameters are already unmanaged. A `SafeHandle` parameter arrives
//      as the `nint` the marshaller produced — exactly the pointer Swift expects
//      for a class reference or a resilient value's address. So "the declared
//      type is a managed reference" is not an ABI fault, and CC-001/CC-002 judge
//      the LOWERED carrier instead (see IsAbiIncompatibleCarrier).
//   2. What the C# compiler already rejects is not this checker's job. LibraryImport
//      fails the build for any type it cannot marshal at all, so a type that
//      survives compilation always has *some* carrier. Only a carrier that is
//      structurally wrong for Swift — a one-word C string pointer where Swift
//      passes a two-word String value — is reported here.
//
// Both rules therefore trade recall for precision on purpose: an unrecognised
// type is treated as compatible. Proving the carrier right needs the Swift
// signature, which arrives with typed call-plan validation, not with text.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Post-generation ABI contract validation results.
/// </summary>
public sealed record AbiCheckResult
{
    /// <summary>All detected violations.</summary>
    public required ImmutableArray<AbiCheckViolation> Violations { get; init; }

    /// <summary>Number of P/Invokes analyzed.</summary>
    public int PInvokeCount { get; init; }

    /// <summary>True if no fatal violations were found.</summary>
    public bool IsClean => Violations.IsEmpty;
}

/// <summary>
/// Thrown when post-generation ABI validation finds a violation. Generation fails
/// rather than writing the module: each violation means an emitted member binds a
/// native symbol in a way that compiles and then misbehaves when called, and there
/// is no way to ship that surface soundly. Recovery — dropping just the affected
/// members and reporting them — replaces this hard failure once the emitter can
/// re-emit against a denylist.
/// </summary>
public sealed class AbiContractViolationException : Exception
{
    public AbiContractViolationException(string moduleName, ImmutableArray<AbiCheckViolation> violations)
        : this(moduleName, violations.Select(v => new AbiAttributedViolation(v, null)).ToImmutableArray())
    {
    }

    /// <summary>
    /// Owner-carrying constructor. Each violation is paired with the declaring artifact typed validation
    /// resolved it to (null for a text-only backstop violation on a call no plan backs), so the
    /// verify-recover loop can attribute a droppable culprit and withdraw it; a null-owner violation
    /// resolves to nothing and fails the module closed, the sound default.
    /// </summary>
    internal AbiContractViolationException(
        string moduleName, ImmutableArray<AbiAttributedViolation> attributed)
        : base(BuildMessage(moduleName, attributed.Select(a => a.Violation).ToImmutableArray()))
    {
        ModuleName = moduleName;
        Violations = attributed.Select(a => a.Violation).ToImmutableArray();
        Attributed = attributed;
    }

    /// <summary>The Swift module whose emitted binding failed validation.</summary>
    public string ModuleName { get; }

    /// <summary>Every violation that caused the failure, in detection order.</summary>
    public ImmutableArray<AbiCheckViolation> Violations { get; }

    /// <summary>
    /// Each violation paired with the artifact that owns it (null when unattributed). Consumed by the
    /// verify-recover loop to resolve a droppable culprit; not part of the public surface.
    /// </summary>
    internal ImmutableArray<AbiAttributedViolation> Attributed { get; }

    private static string BuildMessage(string moduleName, ImmutableArray<AbiCheckViolation> violations)
    {
        var detail = string.Join(Environment.NewLine, violations.Select(v => "  " + v.Describe()));
        return $"SWIFTBIND095: ABI contract validation found {violations.Length} violation(s) in the " +
               $"generated binding for '{moduleName}'. Each one binds a native symbol in a way that " +
               $"compiles but is wrong at the call boundary, so generation fails instead of writing a " +
               $"binding that breaks on first use. Report this as a generator bug." +
               Environment.NewLine + detail;
    }
}

/// <summary>
/// Thrown when the one-directional disagreement invariant fires: the text scan flagged a violation on a
/// P/Invoke that IS backed by a typed <see cref="AbiCallPlan"/>, yet typed plan-vs-descriptor validation
/// passed it. During the transition the text scan is a completeness cross-check over the plan-backed
/// subset, so text-fail / typed-pass is never a recoverable binding fault — it means the generator's own
/// machinery disagrees with itself, and exactly one of three things is wrong: session-04 plan population
/// (the plan does not describe the call it should), the typed comparison (it missed a fault text caught),
/// or the text scan itself (a false positive). Any of those is a generator bug, so this fails the module
/// closed loudly and is never auto-resolved — distinct from <see cref="AbiContractViolationException"/>,
/// which the verify-recover loop may recover by withdrawing the affected member. This type is never caught
/// by that loop.
/// </summary>
public sealed class AbiValidationInvariantException : Exception
{
    public AbiValidationInvariantException(
        string moduleName, ImmutableArray<AbiCheckViolation> disagreements)
        : base(BuildMessage(moduleName, disagreements))
    {
        ModuleName = moduleName;
        Disagreements = disagreements;
    }

    /// <summary>The Swift module whose emitted binding tripped the invariant.</summary>
    public string ModuleName { get; }

    /// <summary>The text-scan violations on plan-backed calls that typed validation did not confirm.</summary>
    public ImmutableArray<AbiCheckViolation> Disagreements { get; }

    private static string BuildMessage(
        string moduleName, ImmutableArray<AbiCheckViolation> disagreements)
    {
        var detail = string.Join(Environment.NewLine, disagreements.Select(v => "  " + v.Describe()));
        return $"SWIFTBIND096: ABI validation disagreement invariant fired for '{moduleName}': the text scan " +
               $"reported {disagreements.Length} violation(s) on P/Invoke(s) that a typed AbiCallPlan backs, " +
               $"but typed plan-vs-descriptor validation passed them. On the plan-backed subset the text scan " +
               $"is a cross-check that must agree, so this is a generator invariant failure — one of the plan " +
               $"population, the typed comparison, or the text scan is wrong. Failing closed; this is never " +
               $"auto-resolved. Report this as a generator bug." + Environment.NewLine + detail;
    }
}

/// <summary>
/// The outcome of validating a whole module: the recoverable violations (typed, plus text-only backstop
/// violations on calls no plan backs), each paired with its owning artifact for the verify-recover loop.
/// A firing of the one-directional disagreement invariant is signalled out-of-band via
/// <see cref="AbiValidationInvariantException"/> rather than returned here.
/// </summary>
public sealed record AbiValidationResult
{
    /// <summary>All recoverable violations, deduplicated, in detection order.</summary>
    public required ImmutableArray<AbiCheckViolation> Violations { get; init; }

    /// <summary>Number of P/Invokes the text scan analysed.</summary>
    public int PInvokeCount { get; init; }

    /// <summary>True if no recoverable violation was found.</summary>
    public bool IsClean => Violations.IsEmpty;

    /// <summary>Each recoverable violation paired with its owning artifact (null when unattributed).</summary>
    internal ImmutableArray<AbiAttributedViolation> Attributed { get; init; } =
        ImmutableArray<AbiAttributedViolation>.Empty;
}

/// <summary>A violation paired with the declaring artifact that owns it, or null when unattributed.</summary>
internal readonly record struct AbiAttributedViolation(AbiCheckViolation Violation, ArtifactId? Owner);

/// <summary>
/// A single ABI contract violation detected in generated output.
/// </summary>
public sealed record AbiCheckViolation
{
    /// <summary>SWIFTBIND diagnostic code (e.g., "SWIFTBIND090").</summary>
    public required string DiagnosticCode { get; init; }

    /// <summary>Short rule identifier (e.g., "CC-001").</summary>
    public required string RuleId { get; init; }

    /// <summary>The P/Invoke method name that violates the rule.</summary>
    public required string MethodName { get; init; }

    /// <summary>The entry point symbol.</summary>
    public required string EntryPoint { get; init; }

    /// <summary>Human-readable explanation.</summary>
    public required string Explanation { get; init; }

    /// <summary>Affected parameter/return type names.</summary>
    public ImmutableArray<string> AffectedElements { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Single-line rendering carrying every field needed to act on the violation:
    /// diagnostic code, rule, the offending managed member, the native symbol it binds,
    /// and the specific parameter/return elements at fault. Used verbatim both by the
    /// warn-only log line and by the blocking failure report, so the two can never drift.
    /// </summary>
    public string Describe()
    {
        var elements = AffectedElements.IsEmpty
            ? ""
            : $" [{string.Join(", ", AffectedElements)}]";
        return $"{DiagnosticCode} ({RuleId}): {MethodName} -> {EntryPoint}: {Explanation}{elements}";
    }
}

/// <summary>
/// Validates generated C# output against ABI safety contracts.
/// Runs after code generation but before file write.
/// </summary>
public static class AbiContractChecker
{
    // ── Carriers that cannot match Swift's, whatever the Swift signature says ──

    // A managed string is marshalled by LibraryImport into a pointer to a C string
    // (UTF-8 or UTF-16 per StringMarshalling) — one word. Swift passes a String as a
    // two-word _StringObject by value, so the callee reads the pointer as the first
    // word and whatever follows in the next register as the second. No Swift signature
    // makes that pairing correct, which is why it can be judged from the C# type alone.
    // Bindings carry Swift strings as the blittable SwiftString.Buffer instead.
    private static readonly HashSet<string> CStringCarrierTypes = new(StringComparer.Ordinal)
    {
        "string", "String", "System.String", "global::System.String",
    };

    // Suffixes an embedded library name can carry when it is a file path rather than a
    // bare module name, longest-first so ".framework" wins over any shorter match.
    private static readonly string[] LibraryFileExtensions = { ".framework", ".dylib", ".tbd", ".so", ".a" };

    // Prefixes a library file carries that its module name does not. Swift's own overlays are
    // "libswift" + module ("libswiftDispatch" is module Dispatch, "libswift_Concurrency" is
    // module _Concurrency), while an ordinary Unix library is "lib" + module. Every reading is
    // offered as a candidate rather than picked between — order carries no precedence — because
    // the same string cannot be reduced correctly under a single rule.
    private static readonly string[] LibraryNamePrefixes = { "libswift", "lib" };

    // ── Regex patterns for P/Invoke extraction ──

    // Matches both unqualified and global:: qualified UnmanagedCallConv attributes.
    // Captures the calling convention type name (e.g., "CallConvSwift" or "CallConvCdecl").
    //
    // The generator emits the CallConvs array in three interchangeable C# forms, and the
    // checker must recognize all of them — missing one mis-defaults the convention to
    // Cdecl, which silently disables CC-001/CC-002 and fires a false CC-004:
    //   1. new global::System.Type[] { typeof(global::System.…CallConvSwift) }  (PInvokeEmitHelper — always fully-qualified)
    //   2. new[] { typeof(…CallConvSwift) }                                     (PInvokeHelperEmitter, KvoExtensionEmitter — target-typed)
    //   3. [typeof(CallConvSwift)]                                              (AppleTypesCsEmitter — C# 12 collection expression)
    // The unqualified `new Type[] { typeof(CallConvSwift) }` spelling is no longer emitted
    // (a user type named `Type` would capture it), but the optional-group regex still
    // accepts it so the checker keeps working against previously-generated sources.
    // The optional `new …`/`new[]` prefix covers 1–2; the `[{[]`/`[}]]` bracket pair covers
    // either the `{ … }` array initializer or the `[ … ]` collection expression. Being lax
    // about pairing { with ] is harmless: inputs are only ever generator-emitted attributes.
    private static readonly Regex CallingConvRegex = new(
        @"\[(?:global::System\.Runtime\.InteropServices\.)?UnmanagedCallConv\(CallConvs\s*=\s*(?:new\s+(?:global::System\.)?Type\[\]|new\[\])?\s*[\{\[]\s*typeof\((?:global::System\.Runtime\.CompilerServices\.)?(CallConv\w+)\)\s*[\}\]]\)\]",
        RegexOptions.Compiled);

    // Matches both unqualified and global:: qualified LibraryImport attributes.
    // Captures library name and entry point.
    private static readonly Regex LibraryImportRegex = new(
        @"\[(?:global::System\.Runtime\.InteropServices\.)?LibraryImport\(""([^""]+)""\s*,\s*EntryPoint\s*=\s*""([^""]+)""",
        RegexOptions.Compiled);

    // Matches P/Invoke signature with any visibility (private/internal/public),
    // optional new/unsafe modifiers. Captures return type, method name, and params.
    // Params may be empty or multiline (handled separately).
    private static readonly Regex PInvokeSignatureStartRegex = new(
        @"(?:private|internal|public)\s+static\s+(?:new\s+)?(?:unsafe\s+)?partial\s+(\S+)\s+(\w+)\(",
        RegexOptions.Compiled);

    private static readonly Regex ClassDeclRegex = new(
        @"(?:public|internal)\s+(?:sealed\s+)?partial\s+(?:class|struct)\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Validate generated C# output against ABI contracts.
    /// </summary>
    /// <param name="csOutput">The generated C# source text.</param>
    /// <param name="moduleName">The Swift module name.</param>
    /// <param name="logger">Logger for emitting SWIFTBIND warnings.</param>
    /// <param name="wrapperLibraryName">
    /// The wrapper library this run emits against, when one is configured. Supplying it lets
    /// CC-003 recognise a wrapper whose name does not follow the usual shape.
    /// </param>
    /// <returns>Validation result with any detected violations.</returns>
    public static AbiCheckResult Validate(
        string csOutput, string moduleName, ILogger logger, string? wrapperLibraryName = null)
    {
        var pinvokes = ExtractPInvokes(csOutput, moduleName, wrapperLibraryName);

        // Equivalent to GenerationMode.XCFramework: a wrapper library name is configured
        // exactly when a companion wrapper carries the @_cdecl thunks. CC-003 is the one
        // rule whose premise depends on that wrapper existing.
        var hasWrapperLibrary = !string.IsNullOrEmpty(wrapperLibraryName);

        var deduplicated = ComputeViolations(pinvokes, hasWrapperLibrary);

        // Log warnings for each violation
        foreach (var violation in deduplicated)
        {
            logger.LogWarning("{Detail}", violation.Describe());
        }

        return new AbiCheckResult
        {
            Violations = deduplicated,
            PInvokeCount = pinvokes.Length,
        };
    }

    /// <summary>
    /// Runs every ABI rule over a set of extracted P/Invokes and returns the deduplicated violations —
    /// the shared core of the text scan, so <see cref="Validate"/> and <see cref="ValidateModule"/> can
    /// never drift in what they flag. Dedup is by (RuleId, MethodName, EntryPoint): the same C# method name
    /// may legally recur across containing types under different entry points, and collapsing those to a
    /// single hit would silently drop a distinct text violation before <see cref="ValidateModule"/> can
    /// reconcile it against the typed oracle at the same granularity — starving the disagreement-invariant
    /// and no-plan-backstop checks of a call they must see.
    /// </summary>
    internal static ImmutableArray<AbiCheckViolation> ComputeViolations(
        ImmutableArray<PInvokeInfo> pinvokes, bool hasWrapperLibrary)
    {
        var violations = new List<AbiCheckViolation>();

        foreach (var pinvoke in pinvokes)
        {
            violations.AddRange(CheckCC001_NonBlittableParams(pinvoke));
            violations.AddRange(CheckCC002_NonBlittableReturn(pinvoke));
            violations.AddRange(CheckCC003_CdeclTargetsWrongLib(pinvoke, hasWrapperLibrary));
            violations.AddRange(CheckCC004_CdeclMangledSymbol(pinvoke));
            violations.AddRange(CheckTjThunkCrossModule(pinvoke));
        }

        // De-duplicate by (RuleId, MethodName, EntryPoint) — the same identity ValidateModule reconciles on,
        // so a distinct text violation is never collapsed away before the invariant/backstop checks run.
        return violations
            .GroupBy(v => (v.RuleId, v.MethodName, v.EntryPoint))
            .Select(g => g.First())
            .ToImmutableArray();
    }

    /// <summary>
    /// Validates a whole module: typed plan-vs-descriptor validation over the plan-backed subset (the
    /// primary oracle) reconciled against the text scan (a completeness cross-check and a defense-in-depth
    /// backstop for the calls no plan yet backs). Every rule the text scan runs, typed validation runs
    /// too, from the recorded <see cref="AbiCallPlan"/>s rather than by re-parsing the emitted C#.
    /// </summary>
    /// <remarks>
    /// The one-directional disagreement invariant: on the plan-backed subset a text-flagged violation
    /// typed did not confirm (text-fail / typed-pass) is a generator invariant failure — thrown as
    /// <see cref="AbiValidationInvariantException"/>, loud and never auto-resolved. The opposite polarity
    /// (typed-fail / text-pass) is NOT an invariant failure: it is new recall working as designed, so the
    /// typed violation is reported (attributable via its owner) and the text scan's silence is only
    /// logged. Text violations on calls NO plan backs are reported as backstop violations with no owner.
    /// </remarks>
    public static AbiValidationResult ValidateModule(
        string csOutput,
        IReadOnlyCollection<AbiCallPlan> plans,
        string moduleName,
        ILogger logger,
        string? wrapperLibraryName = null)
    {
        var hasWrapperLibrary = !string.IsNullOrEmpty(wrapperLibraryName);

        // Typed validation over the plan-backed subset — the primary oracle. Each violation carries the
        // plan's owning artifact for loop attribution.
        var typed = ValidatePlans(plans, moduleName, wrapperLibraryName);
        var typedKeys = new HashSet<(string, string, string)>();
        foreach (var t in typed)
            typedKeys.Add((t.Violation.RuleId, t.Violation.MethodName, t.Violation.EntryPoint));

        // Text scan over the whole emitted module — the cross-check and backstop.
        var textPinvokes = ExtractPInvokes(csOutput, moduleName, wrapperLibraryName);
        var textViolations = ComputeViolations(textPinvokes, hasWrapperLibrary);

        // A call is plan-backed when a recorded plan shares its (MethodName, EntryPoint).
        var planBacked = new HashSet<(string, string)>();
        foreach (var p in plans)
            planBacked.Add((p.MethodName, p.EntryPoint));

        var recoverable = new List<AbiAttributedViolation>(typed);
        var invariantFirings = new List<AbiCheckViolation>();

        foreach (var vx in textViolations)
        {
            if (!planBacked.Contains((vx.MethodName, vx.EntryPoint)))
            {
                // No plan backs this call — the text scan is the only oracle. Report it as a backstop
                // violation with no owner (attribution resolves to nothing → fails the module closed).
                recoverable.Add(new AbiAttributedViolation(vx, null));
                continue;
            }

            if (typedKeys.Contains((vx.RuleId, vx.MethodName, vx.EntryPoint)))
                continue; // agreement — already covered by the typed violation

            // Plan-backed, text flagged it, typed did not: the disagreement invariant.
            invariantFirings.Add(vx);
        }

        if (invariantFirings.Count > 0)
            throw new AbiValidationInvariantException(moduleName, invariantFirings.ToImmutableArray());

        // Dedup at the finest correct granularity — (RuleId, MethodName, EntryPoint) — preferring the
        // owner-carrying (typed) copy so the loop keeps an attributable culprit when a typed violation and
        // a text backstop coincide.
        var deduped = recoverable
            .GroupBy(a => (a.Violation.RuleId, a.Violation.MethodName, a.Violation.EntryPoint))
            .Select(g => g.OrderByDescending(a => a.Owner.HasValue).First())
            .ToImmutableArray();

        foreach (var a in deduped)
            logger.LogWarning("{Detail}", a.Violation.Describe());

        return new AbiValidationResult
        {
            Violations = deduped.Select(a => a.Violation).ToImmutableArray(),
            Attributed = deduped,
            PInvokeCount = textPinvokes.Length,
        };
    }

    /// <summary>
    /// Runs every ABI rule over the typed <see cref="AbiCallPlan"/>s — building a <see cref="PInvokeInfo"/>
    /// from each plan's recorded facts (resolved convention, lowered carriers, library, entry point)
    /// rather than by re-parsing emitted text — and pairs each violation with its plan's owning artifact.
    /// CC-004 ($s symbol under Cdecl) is structurally unreachable here: a plan's convention is already
    /// resolved through <see cref="PInvokeEmitHelper.SelectCallingConvention"/>, which coerces a $s symbol
    /// to Swift CC, so the 91-false-positive CC-004 class cannot fire on a plan.
    /// </summary>
    internal static ImmutableArray<AbiAttributedViolation> ValidatePlans(
        IReadOnlyCollection<AbiCallPlan> plans, string moduleName, string? wrapperLibraryName)
    {
        var hasWrapperLibrary = !string.IsNullOrEmpty(wrapperLibraryName);
        var wrapperLibName = moduleName + "SwiftBindings";

        var attributed = new List<AbiAttributedViolation>();
        foreach (var plan in plans)
        {
            var info = ToPInvokeInfo(plan, wrapperLibName, wrapperLibraryName);
            foreach (var v in CheckCC001_NonBlittableParams(info))
                attributed.Add(new AbiAttributedViolation(v, info.Owner));
            foreach (var v in CheckCC002_NonBlittableReturn(info))
                attributed.Add(new AbiAttributedViolation(v, info.Owner));
            foreach (var v in CheckCC003_CdeclTargetsWrongLib(info, hasWrapperLibrary))
                attributed.Add(new AbiAttributedViolation(v, info.Owner));
            foreach (var v in CheckCC004_CdeclMangledSymbol(info))
                attributed.Add(new AbiAttributedViolation(v, info.Owner));
            foreach (var v in CheckTjThunkCrossModule(info))
                attributed.Add(new AbiAttributedViolation(v, info.Owner));
        }

        return attributed.ToImmutableArray();
    }

    /// <summary>
    /// Builds a <see cref="PInvokeInfo"/> from a typed <see cref="AbiCallPlan"/>, so the same Check* rules
    /// the text scan runs can be applied to the plan. The convention is the plan's already-resolved
    /// convention rendered as the attribute type name the rules compare; the parameters carry synthetic
    /// positional names (the rules judge the carrier type only).
    /// </summary>
    internal static PInvokeInfo ToPInvokeInfo(
        AbiCallPlan plan, string wrapperLibName, string? configuredWrapperLibrary)
    {
        var convention = plan.CallingConvention == PInvokeCallingConvention.Swift
            ? "CallConvSwift"
            : "CallConvCdecl";

        var parameters = plan.ParameterCarriers
            .Select((carrier, i) => new PInvokeParamInfo { CSharpType = carrier, Name = "p" + i })
            .ToImmutableArray();

        return new PInvokeInfo
        {
            MethodName = plan.MethodName,
            EntryPoint = plan.EntryPoint,
            CallingConvention = convention,
            TargetLibrary = ClassifyLibrary(plan.Library, wrapperLibName, configuredWrapperLibrary),
            LibraryName = plan.Library,
            ReturnType = plan.ReturnCarrier,
            Parameters = parameters,
            ContainingClass = null,
            Owner = plan.Owner,
        };
    }

    // ── Check implementations ──

    /// <summary>
    /// CC-001: a CallConvSwift P/Invoke declares a parameter whose marshalled carrier cannot be
    /// what the Swift symbol reads. See <see cref="IsAbiIncompatibleCarrier"/> for why this is
    /// judged on the lowered carrier rather than on the declared type's blittability.
    /// </summary>
    internal static ImmutableArray<AbiCheckViolation> CheckCC001_NonBlittableParams(PInvokeInfo pinvoke)
    {
        if (pinvoke.CallingConvention != "CallConvSwift")
            return ImmutableArray<AbiCheckViolation>.Empty;

        var incompatible = pinvoke.Parameters
            .Where(p => IsAbiIncompatibleCarrier(p.CSharpType))
            .ToList();

        if (incompatible.Count == 0)
            return ImmutableArray<AbiCheckViolation>.Empty;

        return ImmutableArray.Create(new AbiCheckViolation
        {
            DiagnosticCode = "SWIFTBIND090",
            RuleId = "CC-001",
            MethodName = pinvoke.MethodName,
            EntryPoint = pinvoke.EntryPoint,
            Explanation = $"'{pinvoke.MethodName}' calls the Swift symbol '{pinvoke.EntryPoint}' under the Swift " +
                $"calling convention, but {incompatible.Count} parameter(s) marshal to a pointer to a C string " +
                $"where Swift passes a two-word String value. The callee would read the pointer as the first " +
                $"word and an unrelated register as the second. Report this as a generator bug.",
            AffectedElements = incompatible
                .Select(p => $"{p.CSharpType} {p.Name} (marshals to a C string pointer; Swift expects a String value)")
                .ToImmutableArray(),
        });
    }

    /// <summary>
    /// CC-002: a CallConvSwift P/Invoke declares a return type whose marshalled carrier cannot be
    /// what the Swift symbol produces. Same carrier reasoning as CC-001, applied to the result.
    /// </summary>
    internal static ImmutableArray<AbiCheckViolation> CheckCC002_NonBlittableReturn(PInvokeInfo pinvoke)
    {
        if (pinvoke.CallingConvention != "CallConvSwift")
            return ImmutableArray<AbiCheckViolation>.Empty;

        if (pinvoke.ReturnType == "void" || !IsAbiIncompatibleCarrier(pinvoke.ReturnType))
            return ImmutableArray<AbiCheckViolation>.Empty;

        return ImmutableArray.Create(new AbiCheckViolation
        {
            DiagnosticCode = "SWIFTBIND091",
            RuleId = "CC-002",
            MethodName = pinvoke.MethodName,
            EntryPoint = pinvoke.EntryPoint,
            Explanation = $"'{pinvoke.MethodName}' calls the Swift symbol '{pinvoke.EntryPoint}' under the Swift " +
                $"calling convention and marshals the result from a pointer to a C string, but Swift returns a " +
                $"two-word String value. The second word would be lost. Report this as a generator bug.",
            AffectedElements = ImmutableArray.Create(
                $"return: {pinvoke.ReturnType} (marshals from a C string pointer; Swift returns a String value)"),
        });
    }

    /// <summary>
    /// CC-003: @_cdecl wrapper P/Invoke (SBW_ entry point) targeting original library instead of wrapper.
    /// </summary>
    /// <remarks>
    /// Only checkable when a companion wrapper library is configured. The rule's whole premise —
    /// "SBW_ symbols live in the wrapper library, so binding one elsewhere cannot resolve" — needs
    /// a wrapper to be true of. Without one the generator deliberately binds SBW_ symbols against
    /// the module's own library, so there is no wrong library to point at and the checker has no
    /// model of where those symbols live. Reporting there would flag every such binding.
    /// </remarks>
    internal static ImmutableArray<AbiCheckViolation> CheckCC003_CdeclTargetsWrongLib(
        PInvokeInfo pinvoke, bool hasWrapperLibrary)
    {
        if (!hasWrapperLibrary)
            return ImmutableArray<AbiCheckViolation>.Empty;

        if (pinvoke.CallingConvention != "CallConvCdecl")
            return ImmutableArray<AbiCheckViolation>.Empty;

        // SBW_ entry point should target wrapper library, not original
        if (!pinvoke.EntryPoint.StartsWith("SBW_"))
            return ImmutableArray<AbiCheckViolation>.Empty;

        if (pinvoke.TargetLibrary != TargetLibraryKind.OriginalLibrary)
            return ImmutableArray<AbiCheckViolation>.Empty;

        return ImmutableArray.Create(new AbiCheckViolation
        {
            DiagnosticCode = "SWIFTBIND093",
            RuleId = "CC-003",
            MethodName = pinvoke.MethodName,
            EntryPoint = pinvoke.EntryPoint,
            Explanation = $"'{pinvoke.MethodName}' binds the wrapper entry point '{pinvoke.EntryPoint}' against " +
                $"library '{pinvoke.LibraryName}', but SBW_ symbols are emitted only into the generated wrapper " +
                $"library. The original library does not export it, so the call would throw " +
                $"EntryPointNotFoundException on first use. Report this as a generator bug.",
            AffectedElements = ImmutableArray.Create($"bound library: {pinvoke.LibraryName}"),
        });
    }

    /// <summary>
    /// CC-004: CallConvCdecl targeting a mangled Swift symbol ($s...).
    /// C calling convention + Swift symbol = register mismatch.
    /// </summary>
    internal static ImmutableArray<AbiCheckViolation> CheckCC004_CdeclMangledSymbol(PInvokeInfo pinvoke)
    {
        if (pinvoke.CallingConvention != "CallConvCdecl")
            return ImmutableArray<AbiCheckViolation>.Empty;

        if (!pinvoke.EntryPoint.StartsWith(ManglingProbes.StablePrefix))
            return ImmutableArray<AbiCheckViolation>.Empty;

        return ImmutableArray.Create(new AbiCheckViolation
        {
            DiagnosticCode = "SWIFTBIND094",
            RuleId = "CC-004",
            MethodName = pinvoke.MethodName,
            EntryPoint = pinvoke.EntryPoint,
            Explanation = $"'{pinvoke.MethodName}' binds the mangled Swift symbol '{pinvoke.EntryPoint}' under the C " +
                $"calling convention. A Swift-mangled symbol expects Swift's register assignment (self in x20, " +
                $"error in x21, indirect result in x8); calling it as C misplaces those arguments. " +
                $"Report this as a generator bug.",
            AffectedElements = ImmutableArray.Create($"declared convention: {pinvoke.CallingConvention}"),
        });
    }

    /// <summary>
    /// Tj dispatch-thunk library pairing: a class's vtable dispatch thunk is emitted into the
    /// dylib of the module that declares the class, so its mangled symbol names the only library
    /// that can export it. Binding it against any other library resolves to nothing at load time.
    /// </summary>
    /// <remarks>
    /// The comparison is against the P/Invoke's own target library, never the module being
    /// emitted: a binding legitimately calls a dependency's thunk through that dependency's
    /// library, and keying the check on the emitting module would report every such call. Only
    /// dispatch thunks are checked — extension methods dispatch statically and get no Tj suffix,
    /// so a symbol reaching here always names its declaring module first.
    /// </remarks>
    internal static ImmutableArray<AbiCheckViolation> CheckTjThunkCrossModule(
        ImmutableArray<PInvokeInfo> pinvokes)
    {
        var violations = new List<AbiCheckViolation>();
        foreach (var pinvoke in pinvokes)
            violations.AddRange(CheckTjThunkCrossModule(pinvoke));
        return violations.ToImmutableArray();
    }

    /// <summary>
    /// The Tj cross-module rule for a single P/Invoke. The check is entirely per-call — the earlier
    /// whole-set form only looped this — so the same rule runs identically whether it reaches here from
    /// the text scan (<see cref="ComputeViolations"/>) or from typed plan validation
    /// (<see cref="ValidatePlans"/>).
    /// </summary>
    internal static ImmutableArray<AbiCheckViolation> CheckTjThunkCrossModule(PInvokeInfo pinvoke)
    {
        // Only check mangled symbols targeting original library
        if (!pinvoke.EntryPoint.StartsWith(ManglingProbes.StablePrefix))
            return ImmutableArray<AbiCheckViolation>.Empty;
        if (pinvoke.TargetLibrary != TargetLibraryKind.OriginalLibrary)
            return ImmutableArray<AbiCheckViolation>.Empty;

        // Must be a Tj dispatch thunk
        if (!pinvoke.EntryPoint.EndsWith(ManglingProbes.DispatchThunkSuffix))
            return ImmutableArray<AbiCheckViolation>.Empty;

        // Extract module name from mangled symbol
        var extractedModule = ExtractModuleFromMangledSymbol(pinvoke.EntryPoint);
        if (extractedModule == null)
            return ImmutableArray<AbiCheckViolation>.Empty;

        if (LibraryIdentityMatchesModule(pinvoke.LibraryName, extractedModule))
            return ImmutableArray<AbiCheckViolation>.Empty;

        return ImmutableArray.Create(new AbiCheckViolation
        {
            DiagnosticCode = "SWIFTBIND092",
            RuleId = "Tj-XM",
            MethodName = pinvoke.MethodName,
            EntryPoint = pinvoke.EntryPoint,
            Explanation = $"'{pinvoke.MethodName}' binds the dispatch thunk '{pinvoke.EntryPoint}', which " +
                $"module '{extractedModule}' declares and therefore exports, against library " +
                $"'{pinvoke.LibraryName}'. That library does not contain the symbol, so the call would " +
                $"throw EntryPointNotFoundException on first use. Report this as a generator bug.",
            AffectedElements = ImmutableArray.Create(
                $"symbol module: {extractedModule}",
                $"bound library: {pinvoke.LibraryName}"),
        });
    }

    // ── P/Invoke text extraction ──

    /// <summary>
    /// Extract P/Invoke declarations from generated C# source text.
    /// Anchors on [LibraryImport] (always present), looks backwards for calling
    /// convention (optional), and forward for signature (possibly multiline).
    /// Handles both unqualified and global:: qualified attribute forms, and
    /// private/internal/public visibility modifiers.
    /// </summary>
    internal static ImmutableArray<PInvokeInfo> ExtractPInvokes(
        string sourceText, string moduleName, string? wrapperLibraryName = null)
    {
        var results = new List<PInvokeInfo>();
        var lines = sourceText.Split('\n');
        string? currentClass = null;
        var wrapperLibName = moduleName + "SwiftBindings";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();

            // Track current class context
            var classMatch = ClassDeclRegex.Match(line);
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups[1].Value;
                continue;
            }

            // Anchor on [LibraryImport] — always present in every P/Invoke
            var libMatch = LibraryImportRegex.Match(line);
            if (!libMatch.Success)
                continue;

            var libraryName = libMatch.Groups[1].Value;
            var entryPoint = libMatch.Groups[2].Value;

            // Find the [UnmanagedCallConv] attribute adjacent to [LibraryImport]. Emitters
            // are NOT consistent about attribute order: the main method P/Invoke path
            // (PInvokeEmitHelper) emits [UnmanagedCallConv] BEFORE [LibraryImport], but the
            // generic-metadata accessor helper (PInvokeHelperEmitter) emits [LibraryImport]
            // FIRST and [UnmanagedCallConv] on the FOLLOWING line. A backward-only scan
            // silently misses the latter and mis-defaults it to Cdecl — which both disables
            // the CC-001/CC-002 blittability checks for a genuine CallConvSwift P/Invoke AND
            // fires a false CC-004 on its $s… mangled symbol. So scan BOTH directions: the
            // backward pass below, plus a forward pass folded into the signature scan.
            string? callingConvention = null;
            for (int k = Math.Max(0, i - 3); k < i; k++)
            {
                var prevLine = lines[k].TrimStart();
                var callConvMatch = CallingConvRegex.Match(prevLine);
                if (callConvMatch.Success)
                {
                    callingConvention = callConvMatch.Groups[1].Value;
                    break;
                }
            }

            // Look forward for the signature — may be on the next line or span multiple
            // lines — and, while skipping intervening attribute lines, pick up a
            // [UnmanagedCallConv] that follows [LibraryImport] (only if the backward scan
            // didn't already resolve one). The scan stops at this P/Invoke's signature, so
            // it can never absorb the next declaration's convention attribute.
            string? returnType = null;
            string? methodName = null;
            string? paramsStr = null;

            for (int j = i + 1; j < Math.Min(i + 8, lines.Length); j++)
            {
                var scanLine = lines[j].TrimStart();

                // Skip attribute lines (e.g., [return: MarshalAs(...)]), but first try to
                // resolve a not-yet-found calling convention emitted after [LibraryImport].
                if (scanLine.StartsWith("["))
                {
                    if (callingConvention == null)
                    {
                        var fwdCallConvMatch = CallingConvRegex.Match(scanLine);
                        if (fwdCallConvMatch.Success)
                            callingConvention = fwdCallConvMatch.Groups[1].Value;
                    }
                    continue;
                }

                var sigMatch = PInvokeSignatureStartRegex.Match(scanLine);
                if (sigMatch.Success)
                {
                    returnType = sigMatch.Groups[1].Value;
                    methodName = sigMatch.Groups[2].Value;

                    // Extract parameter string — may be single-line or multiline
                    var afterParen = scanLine.Substring(sigMatch.Index + sigMatch.Length);
                    paramsStr = ExtractParameterString(afterParen, lines, j);
                    break;
                }
            }

            // If no [UnmanagedCallConv] found in either direction, the runtime uses the
            // platform default (C calling convention), not Swift. All real generator paths
            // that emit $s... symbols also emit [UnmanagedCallConv(CallConvSwift)]. The
            // no-attribute emitters (EnumHandler, SBW_Free helpers) are wrapper/C ABI paths.
            callingConvention ??= "CallConvCdecl";

            if (returnType == null || methodName == null)
                continue;

            // Classify target library
            var targetLibrary = ClassifyLibrary(libraryName, wrapperLibName, wrapperLibraryName);

            var parameters = ParseParameters(paramsStr ?? "");

            results.Add(new PInvokeInfo
            {
                MethodName = methodName,
                EntryPoint = entryPoint,
                CallingConvention = callingConvention,
                TargetLibrary = targetLibrary,
                LibraryName = libraryName,
                ReturnType = returnType,
                Parameters = parameters,
                ContainingClass = currentClass,
            });
        }

        return results.ToImmutableArray();
    }

    /// <summary>
    /// Extract the parameter string from the portion after the opening paren,
    /// handling multiline signatures by accumulating until ");".
    /// </summary>
    private static string ExtractParameterString(string afterParen, string[] lines, int currentLine)
    {
        // Check if the closing ");' is on the same line
        var closeIdx = FindClosingParen(afterParen);
        if (closeIdx >= 0)
            return afterParen.Substring(0, closeIdx).Trim();

        // Multiline: accumulate until we find ");" across subsequent lines
        var accumulated = new System.Text.StringBuilder(afterParen.TrimEnd());
        for (int j = currentLine + 1; j < Math.Min(currentLine + 20, lines.Length); j++)
        {
            var nextLine = lines[j].Trim();
            var closeInNext = FindClosingParen(nextLine);
            if (closeInNext >= 0)
            {
                accumulated.Append(' ');
                accumulated.Append(nextLine.Substring(0, closeInNext).Trim());
                break;
            }
            accumulated.Append(' ');
            accumulated.Append(nextLine);
        }

        return accumulated.ToString().Trim();
    }

    /// <summary>
    /// Find the index of the closing ");" in a string, respecting nesting of parens
    /// (for delegate* types like delegate* unmanaged[Cdecl]&lt;int, void&gt;).
    /// Returns -1 if not found.
    /// </summary>
    private static int FindClosingParen(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(': depth++; break;
                case ')':
                    if (depth > 0)
                        depth--;
                    else
                        return i; // This is the closing paren of the signature
                    break;
            }
        }
        return -1;
    }

    // ── Classification helpers ──

    /// <summary>
    /// Decide which library a P/Invoke targets. <paramref name="configuredWrapperLibrary"/> is the
    /// wrapper library the generator was actually told to emit against, so a wrapper named anything
    /// at all is recognised; the name-shape tests are only a fallback for runs that never configured
    /// one. Classifying on the library name alone (never the entry point) is what lets CC-003 catch
    /// a wrapper symbol pointed at the original library.
    /// </summary>
    /// <remarks>
    /// When a wrapper IS configured it is the sole wrapper: the name-shape fallback must not also
    /// run, or a symbol misrouted to some *other* library that merely ends in "SwiftBindings" is
    /// classified as correctly-wrapper-bound and CC-003 stays silent on a genuine
    /// EntryPointNotFoundException.
    /// </remarks>
    internal static TargetLibraryKind ClassifyLibrary(
        string libraryName, string wrapperLibName, string? configuredWrapperLibrary)
    {
        if (libraryName.Contains("libswiftCore") || libraryName.Contains("SwiftCore"))
            return TargetLibraryKind.SwiftCore;

        if (!string.IsNullOrEmpty(configuredWrapperLibrary))
        {
            return libraryName == configuredWrapperLibrary
                ? TargetLibraryKind.WrapperLibrary
                : TargetLibraryKind.OriginalLibrary;
        }

        if (libraryName == "SwiftBindings" ||
            libraryName.EndsWith("SwiftBindings") || libraryName == wrapperLibName)
            return TargetLibraryKind.WrapperLibrary;

        return TargetLibraryKind.OriginalLibrary;
    }

    /// <summary>
    /// True when LibraryImport will lower this declared type into a carrier that no Swift
    /// signature can be expecting. Deliberately narrow: the check runs on the declared managed
    /// type, but the value that reaches Swift is the marshalled one, so only types whose
    /// marshalling is wrong for *every* Swift counterpart can be judged here. Anything else —
    /// including SafeHandle payloads, which lower to the plain pointer Swift wants — needs the
    /// Swift signature to judge and is treated as compatible.
    /// </summary>
    private static bool IsAbiIncompatibleCarrier(string csharpType)
    {
        var baseType = StripTypeModifiers(csharpType);
        return CStringCarrierTypes.Contains(baseType);
    }

    /// <summary>
    /// Index of the ']' closing the '[' at position 0, accounting for nested brackets so an
    /// attribute carrying an array or collection-expression argument is skipped whole.
    /// Returns -1 when the text is unbalanced.
    /// </summary>
    private static int FindMatchingBracket(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '[') depth++;
            else if (text[i] == ']' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// Decide whether an embedded library name can legitimately be the library of
    /// <paramref name="moduleName"/>. The generator embeds whatever it was given: a bare module
    /// name in xcframework mode, but the dylib path itself when no library name is supplied
    /// ("use the provided library name, or fall back to the dylib path"). Comparing a module
    /// name to a path string reports every dispatch thunk in that mode.
    /// </summary>
    /// <remarks>
    /// The comparison enumerates every identity the library string could reasonably be read as
    /// and accepts if any names the module, rather than reducing it to one answer. A single
    /// reduction cannot serve both readings that occur in practice: "libswiftDispatch.dylib" is
    /// module Dispatch, while "libMyLib.dylib" is module MyLib, so a fixed prefix rule
    /// mis-reduces one of them and blocks a correct binding. Widening the accepted set can only
    /// cost detection of a library whose own file name matches the module — which is nearly
    /// always the right library anyway — whereas a wrong reduction fails valid output outright,
    /// so the ambiguity is resolved toward accepting.
    /// </remarks>
    internal static bool LibraryIdentityMatchesModule(string libraryName, string moduleName)
    {
        foreach (var candidate in EnumerateLibraryIdentities(libraryName))
        {
            if (string.Equals(candidate, moduleName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Yield the identities an embedded library name could be read as: the string itself, its
    /// path leaf, that leaf with trailing file extensions and version components peeled off one
    /// at a time, and each of those with a library prefix removed. Duplicates are not filtered —
    /// callers scan for membership.
    /// </summary>
    internal static IEnumerable<string> EnumerateLibraryIdentities(string libraryName)
    {
        var name = libraryName.Trim();
        if (name.Length == 0)
            yield break;

        yield return name;

        var lastSeparator = name.LastIndexOf('/');
        var stem = lastSeparator >= 0 ? name.Substring(lastSeparator + 1) : name;

        // Peel one trailing component per round so versioned install names reduce however they
        // are spelled — "libMyLib.1.dylib" and "libMyLib.dylib.1" both reach "libMyLib".
        while (stem.Length > 0)
        {
            yield return stem;

            foreach (var prefix in LibraryNamePrefixes)
            {
                if (stem.Length > prefix.Length && stem.StartsWith(prefix, StringComparison.Ordinal))
                    yield return stem.Substring(prefix.Length);
            }

            var shortened = TryStripTrailingComponent(stem);
            if (shortened == null)
                yield break;

            stem = shortened;
        }
    }

    /// <summary>
    /// Remove one trailing file extension or numeric version component, or return null when the
    /// name carries neither. Only all-digit components are treated as versions, so the "Kit" in
    /// "MyKit" and the "1" in "libMyLib.1" are told apart.
    /// </summary>
    private static string? TryStripTrailingComponent(string name)
    {
        foreach (var extension in LibraryFileExtensions)
        {
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.Ordinal))
                return name.Substring(0, name.Length - extension.Length);
        }

        var lastDot = name.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == name.Length - 1)
            return null;

        for (var i = lastDot + 1; i < name.Length; i++)
        {
            if (name[i] < '0' || name[i] > '9')
                return null;
        }

        return name.Substring(0, lastDot);
    }

    private static string StripTypeModifiers(string type)
    {
        var result = type.Trim();
        // [MarshalAs(...)] and friends sit in front of the type text the signature regex
        // captured; the carrier is whatever follows them.
        while (result.StartsWith("[", StringComparison.Ordinal))
        {
            var close = FindMatchingBracket(result);
            if (close < 0) break;
            result = result.Substring(close + 1).TrimStart();
        }
        if (result.StartsWith("ref ")) result = result.Substring(4);
        if (result.StartsWith("out ")) result = result.Substring(4);
        if (result.StartsWith("in ")) result = result.Substring(3);
        // A nullable annotation is a compile-time-only marker; the carrier is the same.
        if (result.EndsWith("?")) result = result.Substring(0, result.Length - 1).TrimEnd();
        // Strip generic suffix so a constructed type is judged by its definition.
        var angleIdx = result.IndexOf('<');
        if (angleIdx > 0)
            return result.Substring(0, angleIdx);
        return result;
    }

    private static ImmutableArray<PInvokeParamInfo> ParseParameters(string paramsStr)
    {
        if (string.IsNullOrWhiteSpace(paramsStr))
            return ImmutableArray<PInvokeParamInfo>.Empty;

        var results = new List<PInvokeParamInfo>();
        var paramParts = SplitParameters(paramsStr);

        for (int idx = 0; idx < paramParts.Count; idx++)
        {
            var trimmed = paramParts[idx].Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Parse "Type name" or "ref Type name"
            var typeAndName = trimmed;
            if (typeAndName.StartsWith("ref ") || typeAndName.StartsWith("out "))
                typeAndName = typeAndName.Substring(4).TrimStart();

            var lastSpace = typeAndName.LastIndexOf(' ');
            if (lastSpace < 0) continue;

            var csType = typeAndName.Substring(0, lastSpace).Trim();
            var name = typeAndName.Substring(lastSpace + 1).Trim();

            results.Add(new PInvokeParamInfo
            {
                CSharpType = csType,
                Name = name,
            });
        }

        return results.ToImmutableArray();
    }

    private static List<string> SplitParameters(string paramsStr)
    {
        var results = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < paramsStr.Length; i++)
        {
            switch (paramsStr[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case '(' when paramsStr[i..].StartsWith("("):
                    depth++; break;
                case ')': if (depth > 0) depth--; break;
                case ',' when depth == 0:
                    results.Add(paramsStr.Substring(start, i - start));
                    start = i + 1;
                    break;
            }
        }

        if (start < paramsStr.Length)
            results.Add(paramsStr.Substring(start));

        return results;
    }

    /// <summary>
    /// Extract the module name from a mangled Swift symbol.
    /// Format: $s{length}{moduleName}... where {length} is the decimal character count.
    /// </summary>
    internal static string? ExtractModuleFromMangledSymbol(string entryPoint)
    {
        if (!entryPoint.StartsWith(ManglingProbes.StablePrefix))
            return null;

        int i = 2; // skip "$s"
        int length = 0;
        while (i < entryPoint.Length && char.IsDigit(entryPoint[i]))
        {
            length = length * 10 + (entryPoint[i] - '0');
            i++;
        }

        if (length == 0 || i + length > entryPoint.Length)
            return null;

        return entryPoint.Substring(i, length);
    }

    // ── Internal types ──

    internal enum TargetLibraryKind
    {
        OriginalLibrary,
        WrapperLibrary,
        SwiftCore,
    }

    internal sealed record PInvokeInfo
    {
        public required string MethodName { get; init; }
        public required string EntryPoint { get; init; }
        public required string CallingConvention { get; init; }
        public required TargetLibraryKind TargetLibrary { get; init; }
        public required string LibraryName { get; init; }
        public required string ReturnType { get; init; }
        public required ImmutableArray<PInvokeParamInfo> Parameters { get; init; }
        public string? ContainingClass { get; init; }

        /// <summary>
        /// The declaring artifact this P/Invoke hangs off, when it was built from a typed
        /// <see cref="AbiCallPlan"/> (via <see cref="ToPInvokeInfo"/>). Null for a text-extracted
        /// P/Invoke, which carries no owner — the text scan cannot know it.
        /// </summary>
        public ArtifactId? Owner { get; init; }
    }

    internal sealed record PInvokeParamInfo
    {
        public required string CSharpType { get; init; }
        public required string Name { get; init; }
    }
}
