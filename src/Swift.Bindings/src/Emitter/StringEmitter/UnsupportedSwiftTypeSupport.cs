// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

internal static class UnsupportedSwiftTypeSupport
{
    public static bool TryFindFallbackInfo(
        ITypeDatabase typeDatabase,
        ClosureHandler closureHandler,
        TypeSpec typeSpec,
        out TypeDatabaseExtensions.AnyTypeFallbackInfo fallbackInfo)
    {
        // Thin "first match" wrapper over the shared walker — preserves the historical
        // short-circuit semantics (direct fallback, then unsupported-closure, then the
        // first degraded nested type in declaration order) that ~18 call sites rely on to
        // decide whether a member needs a single [UnsupportedSwiftType] flag.
        var sink = new List<TypeDatabaseExtensions.AnyTypeFallbackInfo>(1);
        CollectFallbackInfos(typeDatabase, closureHandler, typeSpec, sink, firstOnly: true);
        if (sink.Count > 0)
        {
            fallbackInfo = sink[0];
            return true;
        }

        fallbackInfo = default;
        return false;
    }

    /// <summary>
    /// Walks <paramref name="typeSpec"/> recursively and appends every AnyType fallback it finds
    /// to <paramref name="sink"/> (direct fallback, unsupported closures, and degraded generic /
    /// tuple / closure-arg / closure-return positions). When <paramref name="firstOnly"/> is true
    /// the walk stops at the first match — that mode backs <see cref="TryFindFallbackInfo"/> and is
    /// behaviourally identical to the previous standalone implementation. When false the walk is
    /// exhaustive, which is what the per-distinct-type SWIFTBIND023 diagnostic needs: a member like
    /// <c>func f(_ a: any P, _ b: any Q)</c> degrades TWO distinct existentials, but a first-match
    /// scan only ever sees <c>any P</c>, leaving <c>any Q</c> silently degraded if it never happens
    /// to occupy a first position elsewhere. Sharing one walker keeps the two modes from drifting.
    /// </summary>
    private static void CollectFallbackInfos(
        ITypeDatabase typeDatabase,
        ClosureHandler closureHandler,
        TypeSpec typeSpec,
        List<TypeDatabaseExtensions.AnyTypeFallbackInfo> sink,
        bool firstOnly)
    {
        if (typeDatabase.TryGetAnyTypeFallbackInfo(typeSpec, out var directFallback))
        {
            sink.Add(directFallback.Value);
            if (firstOnly)
                return;
        }

        if (typeSpec is ClosureTypeSpec closureTypeSpec && !closureHandler.IsSupportedClosure(closureTypeSpec))
        {
            sink.Add(new TypeDatabaseExtensions.AnyTypeFallbackInfo(
                "Unsupported closure fallback",
                closureTypeSpec.ToString()));
            if (firstOnly)
                return;
        }

        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                foreach (var genericParameter in namedTypeSpec.GenericParameters)
                {
                    CollectFallbackInfos(typeDatabase, closureHandler, genericParameter, sink, firstOnly);
                    if (firstOnly && sink.Count > 0)
                        return;
                }
                break;
            case TupleTypeSpec tupleTypeSpec:
                foreach (var element in tupleTypeSpec.Elements)
                {
                    CollectFallbackInfos(typeDatabase, closureHandler, element, sink, firstOnly);
                    if (firstOnly && sink.Count > 0)
                        return;
                }
                break;
            case ClosureTypeSpec nestedClosureTypeSpec:
                CollectFallbackInfos(typeDatabase, closureHandler, nestedClosureTypeSpec.Arguments, sink, firstOnly);
                if (firstOnly && sink.Count > 0)
                    return;

                CollectFallbackInfos(typeDatabase, closureHandler, nestedClosureTypeSpec.ReturnType, sink, firstOnly);
                if (firstOnly && sink.Count > 0)
                    return;
                break;
        }
    }

    /// <summary>
    /// Records EVERY distinct protocol-existential degradation found across a member's type
    /// positions (return + each parameter / index) onto <paramref name="emissionContext"/>, so the
    /// loud SWIFTBIND023 diagnostic fires once per distinct degraded existential rather than only for
    /// the first one a member's single <c>[UnsupportedSwiftType]</c> flag happens to name.
    ///
    /// <para>The consumer-facing attribute is still one-per-member (a flag), but the diagnostic
    /// promise in <see cref="ModuleEmissionContext.DegradedExistentials"/> is per-distinct-type — and
    /// the first-match scan that drives the attribute cannot keep that promise for an existential that
    /// only ever appears as a second-or-later degraded position, or (for concrete subscripts) as an
    /// index parameter the attribute scan skipped entirely. This closes both gaps. Only
    /// <see cref="TypeDatabaseExtensions.AnyTypeFallbackInfo.ExistentialFallbackReason"/> fallbacks are
    /// recorded; unrelated fallbacks (unsupported closures, unknown generics) are not existential
    /// degradations. Dedup is handled by <see cref="ModuleEmissionContext.TryRecordExistentialDegradation"/>,
    /// so positions already flagged by an <see cref="EmitAttribute"/> call are harmless repeats.</para>
    /// </summary>
    public static void RecordExistentialDegradations(
        ModuleEmissionContext? emissionContext,
        ITypeDatabase typeDatabase,
        ClosureHandler closureHandler,
        IEnumerable<TypeSpec?> memberPositions)
    {
        if (emissionContext == null)
            return;

        var sink = new List<TypeDatabaseExtensions.AnyTypeFallbackInfo>();
        foreach (var position in memberPositions)
        {
            if (position == null)
                continue;

            sink.Clear();
            CollectFallbackInfos(typeDatabase, closureHandler, position, sink, firstOnly: false);
            foreach (var info in sink)
            {
                if (info.Reason == TypeDatabaseExtensions.AnyTypeFallbackInfo.ExistentialFallbackReason)
                    emissionContext.TryRecordExistentialDegradation(info.SwiftType);
            }
        }
    }

    public static void EmitAttribute(
        CSharpWriter csWriter,
        TypeDatabaseExtensions.AnyTypeFallbackInfo fallbackInfo,
        ModuleEmissionContext? emissionContext = null)
    {
        csWriter.WriteLine(
            $"[global::Swift.UnsupportedSwiftType(\"{EscapeStringLiteral(fallbackInfo.Reason)}\", \"{EscapeStringLiteral(fallbackInfo.SwiftType)}\")]");

        // Defect E: a protocol existential `any P` that the resolver couldn't project degrades to
        // `object`. The consumer-facing attribute above records WHY for downstream tools, but the
        // degradation was previously silent at generation time. Record it on the emission context so
        // EmissionReportEmitter.Emit raises one loud SWIFTBIND023 warning per distinct type. Only the
        // existential reason qualifies — unrelated fallbacks (closures, unknown generics) are not
        // existential degradations and must not raise the diagnostic.
        if (emissionContext != null
            && fallbackInfo.Reason == TypeDatabaseExtensions.AnyTypeFallbackInfo.ExistentialFallbackReason)
        {
            emissionContext.TryRecordExistentialDegradation(fallbackInfo.SwiftType);
        }
    }

    internal static string EscapeStringLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
