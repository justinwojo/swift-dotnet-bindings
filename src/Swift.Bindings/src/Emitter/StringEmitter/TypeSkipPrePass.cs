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
/// The predicates duplicated here MUST match the handler-time decisions exactly:
///
///   - <see cref="GenericTypeEmitter.TryGetUnsupportedConstraint"/>: generic type with
///     a constraint on an unsupported framework protocol (SwiftUI, Combine).
///   - <see cref="PInvokeHelperContext.HasIndeterminatePwtShape"/>: generic type whose
///     ABI declares conformances the emitter cannot lower into PWT arguments.
///
/// If a handler introduces a new skip condition it MUST be mirrored here, otherwise
/// members referencing the newly-skipped type will start leaking through again.
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
        // Condition 1: unsupported generic constraint (SwiftUI, Combine).
        if (GenericTypeEmitter.TryGetUnsupportedConstraint(typeDecl, out var unsupportedConstraint))
        {
            var reason = AppleFrameworkRegistry.GetUnsupportedConstraintSkipReason(unsupportedConstraint.Module);

            ReportCollector.RecordTypeSkipped(
                typeDecl,
                reason,
                $"Unsupported generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
            return true;
        }

        // Condition 2: generic type whose metadata-accessor ABI cannot be lowered.
        // Only generic types produce a non-null helper context — non-generics are never
        // skipped by this gate.
        var ctx = PInvokeHelperContext.CreateIfGeneric(typeDecl, typeDatabase);
        if (ctx != null && ctx.HasIndeterminatePwtShape)
        {
            var details = string.Join(
                "; ",
                ctx.UnresolvedPwtConstraints.Select(c =>
                    $"{c.GenericParamCsName}: {c.ProtocolModuleQualifiedName} ({c.Reason})"));

            ReportCollector.RecordTypeSkipped(
                typeDecl,
                SkipReason.IndeterminatePwtShape,
                details);
            return true;
        }

        // Condition 3: frozen value-with-memory struct (projected as a blitted-Buffer class)
        // whose Buffer layout cannot be sized cross-compile because a stored field is a generic
        // value-type instantiation (e.g. ClosedRange<Int>, Result<T,E>). Mirrors
        // FrozenStructHandler's early skip — without it, members passing or returning the struct
        // by value would reference a {Type}.Buffer that is never emitted.
        if (typeDecl is StructDecl structDecl &&
            FrozenStructHandler.HasIndeterminateBufferLayout(structDecl, typeDatabase))
        {
            ReportCollector.RecordTypeSkipped(
                typeDecl,
                SkipReason.IndeterminateStructLayout,
                "Frozen struct has a stored field whose blitted Buffer size is not derivable cross-compile (generic value-type instantiation).");
            return true;
        }

        return false;
    }
}
