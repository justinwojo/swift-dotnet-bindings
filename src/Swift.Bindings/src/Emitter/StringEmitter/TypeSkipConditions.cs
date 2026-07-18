// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// The condition kinds that make a type handler refuse to emit a type declaration
/// entirely. See <see cref="TypeSkipConditions"/> for the single evaluation order.
/// </summary>
public enum TypeSkipConditionKind
{
    /// <summary>Generic constraint on an unsupported framework protocol (SwiftUI, Combine).
    /// Predicate: <see cref="GenericTypeEmitter.TryGetUnsupportedConstraint"/>.</summary>
    UnsupportedGenericConstraint,

    /// <summary>Swift variadic generic parameter pack (<c>each T</c>) — no C# equivalent.
    /// Predicate: <see cref="GenericTypeEmitter.TryGetVariadicGenericParameter"/>.</summary>
    VariadicGenericParameterPack,

    /// <summary>Frozen Buffer-projected struct with a stored field whose inline size is not
    /// derivable cross-compile. Predicate: <see cref="FrozenStructHandler.HasIndeterminateBufferLayout"/>.</summary>
    IndeterminateBufferLayout,

    /// <summary>Frozen by-value struct whose emitted IntPtr-word optional fields shift a stored
    /// field off its Swift packed offset. Predicate: <see cref="FrozenStructHandler.HasSubWordOptionalLayoutMismatch"/>.</summary>
    SubWordOptionalLayoutMismatch,

    /// <summary>Generic type whose ABI declares conformances the emitter cannot lower into
    /// PWT arguments. Predicate: <see cref="PInvokeHelperContext.HasIndeterminatePwtShape"/>.</summary>
    IndeterminatePwtShape,
}

/// <summary>
/// A matched type-level skip condition plus the payload its consumers need to format
/// their report entry / source comment / log message.
/// </summary>
public sealed class TypeSkipMatch
{
    public required TypeSkipConditionKind Kind { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="TypeSkipConditionKind.UnsupportedGenericConstraint"/>.</summary>
    public SwiftTypeName? UnsupportedConstraint { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="TypeSkipConditionKind.VariadicGenericParameterPack"/>.</summary>
    public string? VariadicParameter { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="TypeSkipConditionKind.IndeterminatePwtShape"/>.</summary>
    public PInvokeHelperContext? PInvokeContext { get; init; }

    /// <summary>
    /// Identity of the type this match was evaluated against. Stamped by
    /// <see cref="TypeSkipConditions.FirstMatch"/> on every arm, so a consumer that receives only
    /// the match still knows exactly which declaration it describes.
    /// </summary>
    public DeclId? Subject { get; init; }
}

/// <summary>
/// The single authoritative list of type-level skip conditions — the reasons a type
/// handler refuses to emit a type declaration at all. Three consumer families depend
/// on agreeing about this list, and each used to hand-mirror it (which drifted):
///
///   1. The four type handlers (class, enum, frozen struct, non-frozen struct) decide
///      at emission time whether to skip the type and how to report it
///      (<see cref="EmitHandlerTypeSkip"/>).
///   2. <see cref="TypeSkipPrePass"/> predicts the same decisions BEFORE any member is
///      emitted so member gates can prune signatures referencing a skipped type.
///   3. <see cref="SilentTombstoneRegistrar"/> must return "not a tombstone" for any
///      type that will never be emitted — a stale registration trips
///      <see cref="EmissionReportEmitter.AssertSilentTombstoneInvariant"/> at generation time.
///
/// A new type-level skip condition is added HERE: a <see cref="TypeSkipConditionKind"/>
/// member plus an arm in <see cref="FirstMatch"/>. All consumers then see it without
/// further mirroring; the per-consumer formatting switches fail loudly (throw) on an
/// unhandled kind rather than silently mis-predicting.
///
/// Evaluation order is observable (a type matching two conditions is reported under
/// the FIRST): decl-only predicates run before ones that consult the type database,
/// and the struct-layout conditions run before the PWT-shape gate so the recorded
/// reason names the more specific structural defect.
/// </summary>
public static class TypeSkipConditions
{
    /// <summary>
    /// Evaluates the skip-condition list against <paramref name="typeDecl"/> and returns
    /// the first match, or null when no condition applies (the handler will emit the type).
    /// <paramref name="pinvokeContext"/> receives the <see cref="PInvokeHelperContext"/>
    /// built for the PWT-shape check (null for non-generic types, and unassigned-null when
    /// an earlier condition matched first) so emitting callers can reuse it instead of
    /// re-flattening conformances.
    /// </summary>
    public static TypeSkipMatch? FirstMatch(
        TypeDecl typeDecl, ITypeDatabase typeDatabase, out PInvokeHelperContext? pinvokeContext)
    {
        ArgumentNullException.ThrowIfNull(typeDecl);
        ArgumentNullException.ThrowIfNull(typeDatabase);
        pinvokeContext = null;

        // Identity of what is being judged, stamped on whichever arm matches. Computed once so
        // every arm reports the same subject and no arm can forget to set it.
        var subject = DeclIdFactory.ForType(typeDecl);

        if (GenericTypeEmitter.TryGetUnsupportedConstraint(typeDecl, out var unsupportedConstraint))
        {
            return new TypeSkipMatch
            {
                Kind = TypeSkipConditionKind.UnsupportedGenericConstraint,
                UnsupportedConstraint = unsupportedConstraint,
                Subject = subject,
            };
        }

        if (GenericTypeEmitter.TryGetVariadicGenericParameter(typeDecl, out var variadicParam))
        {
            return new TypeSkipMatch
            {
                Kind = TypeSkipConditionKind.VariadicGenericParameterPack,
                VariadicParameter = variadicParam,
                Subject = subject,
            };
        }

        // Struct-layout conditions. Both predicates self-gate on the struct's projection
        // (Buffer-projected vs by-value frozen), so evaluating them for every StructDecl
        // is safe regardless of which struct handler will run.
        if (typeDecl is StructDecl structDecl)
        {
            if (FrozenStructHandler.HasIndeterminateBufferLayout(structDecl, typeDatabase))
                return new TypeSkipMatch { Kind = TypeSkipConditionKind.IndeterminateBufferLayout, Subject = subject };

            if (FrozenStructHandler.HasSubWordOptionalLayoutMismatch(structDecl, typeDatabase))
                return new TypeSkipMatch { Kind = TypeSkipConditionKind.SubWordOptionalLayoutMismatch, Subject = subject };
        }

        // Only generic types produce a non-null helper context — non-generics are never
        // skipped by the PWT-shape gate.
        pinvokeContext = PInvokeHelperContext.CreateIfGeneric(typeDecl, typeDatabase);
        if (pinvokeContext is { HasIndeterminatePwtShape: true })
        {
            return new TypeSkipMatch
            {
                Kind = TypeSkipConditionKind.IndeterminatePwtShape,
                PInvokeContext = pinvokeContext,
                Subject = subject,
            };
        }

        return null;
    }

    /// <summary>
    /// Ancestor-gate variant of the condition list: true when <paramref name="ancestor"/> —
    /// a class reached through a ResolvedSuperclass walk — will never be emitted, so it
    /// must not be referenced as a C# base type, walked for inherited members, or treated
    /// as a conformance source (a skipped base referenced as `: Base` is a CS0246 in the
    /// generated binding).
    ///
    /// This cannot simply call <see cref="FirstMatch"/>: ancestor walks run in contexts
    /// that don't carry an <see cref="ITypeDatabase"/>. Instead it checks the two
    /// decl-only class conditions directly (unsupported constraint, variadic pack) and
    /// covers every database-dependent condition — today the PWT-shape gate, plus any
    /// future one — via <see cref="ReportCollector.IsTypeSkipped(SwiftTypeName)"/>, which
    /// <see cref="TypeSkipPrePass"/> populates from <see cref="FirstMatch"/> before any
    /// type handler runs. The two struct-layout conditions can never apply here because a
    /// class's ancestor is always a class. The direct decl-only checks also keep the
    /// answer correct in contexts where no collector session is active.
    /// </summary>
    public static bool ClassAncestorWillBeSkipped(ClassDecl ancestor)
    {
        ArgumentNullException.ThrowIfNull(ancestor);
        return GenericTypeEmitter.TryGetUnsupportedConstraint(ancestor, out _)
            || GenericTypeEmitter.TryGetVariadicGenericParameter(ancestor, out _)
            || ReportCollector.IsTypeSkipped(ancestor.SwiftTypeName);
    }

    /// <summary>
    /// Handler-side skip emission: records the skip against the binding report, writes the
    /// skipped-type comment into the generated C#, and logs a warning — with the exact
    /// per-condition wording each surface has always used. Callers MUST invoke this (and
    /// return) BEFORE <see cref="ReportCollector.RecordTypeEmitted"/>, otherwise the
    /// collector's emitted-key suppression swallows the skip record.
    /// </summary>
    public static void EmitHandlerTypeSkip(
        CSharpWriter csWriter, TypeDecl typeDecl, TypeSkipMatch match, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(match);

        switch (match.Kind)
        {
            case TypeSkipConditionKind.UnsupportedGenericConstraint:
            {
                var constraint = match.UnsupportedConstraint!;
                var reason = AppleFrameworkRegistry.GetUnsupportedConstraintSkipReason(constraint.Module);
                ReportCollector.RecordTypeSkipped(typeDecl, reason, $"Unsupported generic constraint: {constraint.ModuleQualifiedName}");
                UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, reason, $"generic constraint: {constraint.ModuleQualifiedName}", match.Subject);
                logger.LogWarning(
                    "Skipping type '{TypeName}' - generic constraint references unsupported protocol '{Protocol}' from module '{Module}'.",
                    typeDecl.Name,
                    constraint.Name,
                    constraint.Module);
                break;
            }

            case TypeSkipConditionKind.VariadicGenericParameterPack:
            {
                var variadicParam = match.VariadicParameter!;
                ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.UnsupportedSignature, $"Variadic generic parameter pack '{variadicParam}' has no C# equivalent.");
                UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.UnsupportedSignature, $"variadic generic parameter pack '{variadicParam}' (Swift `{variadicParam}` / `repeat {variadicParam}`) has no C# equivalent.", match.Subject);
                logger.LogWarning(
                    "Skipping type '{TypeName}' - variadic generic parameter pack '{Variadic}' has no C# equivalent.",
                    typeDecl.Name,
                    variadicParam);
                break;
            }

            case TypeSkipConditionKind.IndeterminateBufferLayout:
            {
                // A guessed Buffer layout would mis-size the blitted field and corrupt the
                // heap, so the handler fails closed (see HasIndeterminateBufferLayout).
                const string detail = "stored property has a generic value-type layout whose per-instantiation size is not derivable cross-compile (e.g. ClosedRange<Bound>, Result<Success,Failure>); a blitted Buffer would mis-size the field.";
                ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.IndeterminateStructLayout, detail);
                UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.IndeterminateStructLayout, detail, match.Subject);
                logger.LogWarning(
                    "Skipping frozen struct '{TypeName}' - indeterminate Buffer layout (unsizeable generic value-type stored field).",
                    typeDecl.Name);
                break;
            }

            case TypeSkipConditionKind.SubWordOptionalLayoutMismatch:
            {
                // A by-value pass would read the shifted field's bytes from the wrong slot
                // and corrupt the value, so the handler fails closed (see
                // HasSubWordOptionalLayoutMismatch).
                const string detail = "frozen value struct mixes sub-word Optional<primitive> fields whose 8-byte IntPtr-word emission places a stored field at a different byte offset than the Swift packed layout; a by-value pass would read the field from the wrong slot and corrupt it.";
                ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.IndeterminateStructLayout, detail);
                UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.IndeterminateStructLayout, detail, match.Subject);
                logger.LogWarning(
                    "Skipping frozen struct '{TypeName}' - sub-word Optional field packing makes the by-value C# layout diverge from Swift.",
                    typeDecl.Name);
                break;
            }

            case TypeSkipConditionKind.IndeterminatePwtShape:
                // The gate re-checks HasIndeterminatePwtShape (true here) and performs the
                // record + comment + log with its established wording.
                TypeMetadataAccessorSkipGate.ShouldSkip(typeDecl, match.PInvokeContext!, csWriter, logger);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unhandled type-skip condition '{match.Kind}' for type '{typeDecl.Name}'. " +
                    "A new condition added to TypeSkipConditions.FirstMatch needs a formatting arm here.");
        }
    }
}
