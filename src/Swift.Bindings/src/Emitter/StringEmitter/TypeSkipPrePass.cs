// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Pre-emission pass that predicts which top-level and nested types a module will
/// skip and records them into <see cref="ReportCollector"/> BEFORE any member is
/// emitted. Member gates (<see cref="ValidationRuleSet.ReferencesUnsupportedModule"/>
/// in particular) consult the collector's skipped-type set so signatures referencing
/// a not-yet-declared skipped type can be pruned in the same pass they're emitted —
/// without this, members referencing e.g. MusicKit's skipped generic
/// <c>MusicRelationshipProperty&lt;Source, Target&gt;</c> slip through and produce
/// CS0234 at C# compile time.
///
/// The prediction evaluates the same shared condition list the handlers use
/// (<see cref="TypeSkipConditions.FirstMatch"/>), so a new handler skip condition is
/// picked up here automatically instead of needing a hand-kept mirror — the historical
/// failure mode was a condition added on the handler side only, leaving members
/// referencing the newly-skipped type to leak through again.
/// </summary>
public static class TypeSkipPrePass
{
    /// <summary>
    /// Walk <paramref name="moduleDecl"/> and every nested type, recording each type
    /// whose handler will skip it to <see cref="ReportCollector"/>. Must be invoked
    /// after <see cref="ReportCollector.Start"/> and before type handlers run.
    /// </summary>
    public static void Run(ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
    {
        ArgumentNullException.ThrowIfNull(moduleDecl);
        ArgumentNullException.ThrowIfNull(typeDatabase);

        foreach (var typeDecl in moduleDecl.Types)
            Visit(typeDecl, typeDatabase, ancestorSkippedName: null);
    }

    private static void Visit(TypeDecl typeDecl, ITypeDatabase typeDatabase, string? ancestorSkippedName)
    {
        // Self-check first — a nested type may hit its own skip predicate, in which
        // case that reason is more specific than AncestorSkipped and we prefer it.
        var selfSkipped = TryRecordSkip(typeDecl, typeDatabase);

        // If an ancestor was skipped but this type wasn't caught by its own predicate,
        // record it as skipped because the parent's declaration will never be emitted.
        // Without this propagation, signatures referencing Parent.Nested leak past the
        // member gate and produce dangling CS0234 references.
        if (!selfSkipped && ancestorSkippedName is not null)
        {
            ReportCollector.RecordTypeSkipped(
                typeDecl,
                SkipReason.AncestorSkipped,
                $"Parent type '{ancestorSkippedName}' is skipped");
        }

        // Protocol nested types are not emitted by ProtocolHandler; mirror
        // ReportCollector.CountTypeAndMembers which also skips them.
        if (typeDecl is ProtocolDecl)
            return;

        var skippedNameForChildren = selfSkipped
            ? typeDecl.SwiftTypeName.ModuleQualifiedName
            : (ancestorSkippedName is not null
                ? ancestorSkippedName
                : null);

        foreach (var nested in typeDecl.Types)
            Visit(nested, typeDatabase, skippedNameForChildren);
    }

    private static bool TryRecordSkip(TypeDecl typeDecl, ITypeDatabase typeDatabase)
    {
        var match = TypeSkipConditions.FirstMatch(typeDecl, typeDatabase, out _);
        if (match is null)
            return false;

        // Pre-pass wording is intentionally compact (the handler-side comment carries the
        // long-form rationale); RecordTypeSkipped dedups per type key, so because this
        // pre-pass runs first ITS reason/details are what the binding report carries.
        var (reason, details) = match.Kind switch
        {
            TypeSkipConditionKind.EmitterFault => (
                SkipReason.EmitterFault,
                match.FaultDetails!),

            TypeSkipConditionKind.UnsupportedGenericConstraint => (
                AppleFrameworkRegistry.GetUnsupportedConstraintSkipReason(match.UnsupportedConstraint!.Module),
                $"Unsupported generic constraint: {match.UnsupportedConstraint.ModuleQualifiedName}"),

            TypeSkipConditionKind.VariadicGenericParameterPack => (
                SkipReason.UnsupportedSignature,
                $"Variadic generic parameter pack '{match.VariadicParameter}' has no C# equivalent."),

            TypeSkipConditionKind.IndeterminateBufferLayout => (
                SkipReason.IndeterminateStructLayout,
                "Frozen struct has a stored field whose blitted Buffer size is not derivable cross-compile (generic value-type instantiation)."),

            TypeSkipConditionKind.SubWordOptionalLayoutMismatch => (
                SkipReason.IndeterminateStructLayout,
                "Frozen value struct mixes sub-word Optional<primitive> fields whose 8-byte IntPtr-word emission diverges from the Swift packed field offsets; a by-value pass would corrupt the field."),

            TypeSkipConditionKind.IndeterminatePwtShape => (
                SkipReason.IndeterminatePwtShape,
                string.Join(
                    "; ",
                    match.PInvokeContext!.UnresolvedPwtConstraints.Select(c =>
                        $"{c.GenericParamCsName}: {c.ProtocolModuleQualifiedName} ({c.Reason})"))),

            _ => throw new InvalidOperationException(
                $"Unhandled type-skip condition '{match.Kind}' for type '{typeDecl.Name}'. " +
                "A new condition added to TypeSkipConditions.FirstMatch needs a formatting arm here."),
        };

        ReportCollector.RecordTypeSkipped(typeDecl, reason, details);
        return true;
    }
}
