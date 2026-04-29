// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// In-process collector for binding generation report data.
/// </summary>
/// <remarks>
/// Member dedup is keyed on <see cref="MemberDiagnosticIdentity"/>, which is
/// overload-stable: two distinct overloads (e.g. <c>foo(_:Int)</c> vs
/// <c>foo(_:String)</c>) skipped for different reasons each record their own
/// <see cref="SkippedItem"/> entry. Legacy callers that pass only
/// <c>(kind, name, containingDecl)</c> still produce a valid identity, but
/// without parameter labels/types — overloads collapse on those paths until
/// the call site is migrated to a decl-aware overload.
/// </remarks>
public static class ReportCollector
{
    private static readonly object Sync = new();
    private static readonly AsyncLocal<bool> SessionActive = new();
    private static BindingReport? _report;

    private static readonly HashSet<string> EmittedTypeKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SkippedTypeKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<MemberDiagnosticIdentity> EmittedMemberIdentities = new();
    private static readonly HashSet<MemberDiagnosticIdentity> SkippedMemberIdentities = new();
    private static readonly HashSet<MemberDiagnosticIdentity> SynthesizedMemberIdentities = new();

    public static bool IsActive => SessionActive.Value && _report != null;

    /// <summary>
    /// Query whether a type is known to be skipped. Populated by <see cref="RecordTypeSkipped"/>
    /// and by a pre-emission pass that runs the skip predicates upfront so member gates
    /// can prune signatures referencing types that will never be declared. The key is the
    /// module-qualified name (e.g., "MusicKit.MusicRelationshipProperty"). Returns false
    /// when no session is active.
    /// </summary>
    public static bool IsTypeSkipped(string moduleQualifiedTypeName)
    {
        if (!SessionActive.Value || _report == null)
            return false;
        lock (Sync)
            return SkippedTypeKeys.Contains(moduleQualifiedTypeName);
    }

    /// <summary>
    /// Same as <see cref="IsTypeSkipped(string)"/> but convenient at the <see cref="SwiftTypeName"/>
    /// level. Uses <see cref="SwiftTypeName.ModuleQualifiedName"/> as the key.
    /// </summary>
    public static bool IsTypeSkipped(SwiftTypeName typeName) =>
        IsTypeSkipped(typeName.ModuleQualifiedName);

    public static void Start(ModuleDecl moduleDecl)
    {
        ArgumentNullException.ThrowIfNull(moduleDecl);

        lock (Sync)
        {
            ResetUnsafe();

            _report = new BindingReport
            {
                ModuleName = moduleDecl.Name,
            };
            SessionActive.Value = true;

            (_report.TotalTypes, _report.TotalMembers) = CalculateTotals(moduleDecl);
        }
    }

    public static BindingReport? Complete()
    {
        if (!SessionActive.Value)
            return null;

        lock (Sync)
        {
            if (_report == null)
                return null;

            _report.EmittedTypes = EmittedTypeKeys.Count;
            _report.SkippedTypes = SkippedTypeKeys.Count;
            _report.EmittedMembers = EmittedMemberIdentities.Count;
            _report.SkippedMembers = SkippedMemberIdentities.Count;
            _report.SynthesizedMembers = SynthesizedMemberIdentities.Count;

            // Per-kind breakdown: read directly from each identity's Kind field.
            ComputePerKindCounts(_report);

            // Compute BridgeSummary if there are bridged views
            if (_report.BridgedViews.Count > 0)
                _report.BridgeSummary = ComputeBridgeSummary(_report);

            return _report;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            ResetUnsafe();
        }
        SessionActive.Value = false;
    }

    public static void RecordTypeEmitted(TypeDecl typeDecl)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            var key = GetTypeKey(typeDecl);
            if (SkippedTypeKeys.Contains(key))
                return;

            EmittedTypeKeys.Add(key);
        }
    }

    public static void RecordTypeSkipped(TypeDecl typeDecl, SkipReason reason, string? details = null)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            var key = GetTypeKey(typeDecl);
            if (EmittedTypeKeys.Contains(key) || !SkippedTypeKeys.Add(key))
                return;

            _report.SkippedItems.Add(new SkippedItem
            {
                Kind = BindingItemKind.Type,
                Name = typeDecl.Name,
                ContainingType = typeDecl.ParentDecl is TypeDecl parentType ? parentType.SwiftTypeName.ModuleQualifiedName : null,
                Reason = reason,
                Details = details,
                RecommendedWorkaround = WorkaroundRecommendations.GetRecommendation(reason),
            });
        }
    }

    /// <summary>
    /// Legacy entry point — builds a coarse <see cref="MemberDiagnosticIdentity"/>
    /// from <c>(kind, name, containingDecl)</c>. Overloads with the same base
    /// name share an identity here. Migrate to a decl-aware overload
    /// (<see cref="RecordMemberEmitted(MethodDecl, BaseDecl?)"/> etc.) when the
    /// caller has the full declaration in scope.
    /// </summary>
    public static void RecordMemberEmitted(BindingItemKind kind, string name, BaseDecl? containingDecl)
        => RecordMemberEmittedInternal(MemberDiagnosticIdentity.FromMember(kind, name, containingDecl));

    /// <summary>
    /// Decl-aware entry point — captures the full method signature so
    /// overloaded methods record distinctly.
    /// </summary>
    public static void RecordMemberEmitted(MethodDecl methodDecl, BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(methodDecl);
        RecordMemberEmittedInternal(MemberDiagnosticIdentity.FromMethod(methodDecl, containingDecl));
    }

    /// <summary>
    /// Decl-aware entry point for emitted properties. Pairs with
    /// <see cref="RecordMemberSkipped(PropertyDecl, SkipReason, string?, AccessorKind)"/>
    /// so emitted/skipped identities match when both sides use the decl-aware path.
    /// </summary>
    public static void RecordMemberEmitted(
        PropertyDecl propertyDecl,
        AccessorKind accessor = AccessorKind.None,
        BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(propertyDecl);
        RecordMemberEmittedInternal(MemberDiagnosticIdentity.FromProperty(propertyDecl, accessor, containingDecl));
    }

    /// <summary>
    /// Decl-aware entry point for emitted subscripts. Pairs with
    /// <see cref="RecordMemberSkipped(SubscriptDecl, SkipReason, string?, AccessorKind)"/>.
    /// Captures index parameter labels/types so overloaded subscripts record distinctly.
    /// </summary>
    public static void RecordMemberEmitted(
        SubscriptDecl subscriptDecl,
        AccessorKind accessor = AccessorKind.None,
        BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(subscriptDecl);
        RecordMemberEmittedInternal(MemberDiagnosticIdentity.FromSubscript(subscriptDecl, accessor, containingDecl));
    }

    /// <summary>
    /// Decl-aware entry point for emitted operators. Pairs with
    /// <see cref="RecordMemberSkipped(OperatorDecl, SkipReason, string?)"/>.
    /// Captures the underlying method's parameter signature so overloaded
    /// operators record distinctly.
    /// </summary>
    public static void RecordMemberEmitted(OperatorDecl operatorDecl, BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(operatorDecl);
        RecordMemberEmittedInternal(MemberDiagnosticIdentity.FromOperator(operatorDecl, containingDecl));
    }

    /// <summary>
    /// Legacy entry point — see <see cref="RecordMemberEmitted(BindingItemKind, string, BaseDecl?)"/>
    /// for overload-collapse caveats.
    /// </summary>
    public static void RecordMemberSkipped(BindingItemKind kind, string name, BaseDecl? containingDecl, SkipReason reason, string? details = null)
        => RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromMember(kind, name, containingDecl),
            displayName: name,
            containingDecl,
            reason,
            details);

    /// <summary>
    /// Decl-aware entry point — captures the full method signature so two
    /// overloads of <c>foo(_ x: Int)</c> and <c>foo(_ s: String)</c> skipped
    /// for different reasons each land in <see cref="BindingReport.SkippedItems"/>.
    /// </summary>
    public static void RecordMemberSkipped(MethodDecl methodDecl, SkipReason reason, string? details = null)
    {
        ArgumentNullException.ThrowIfNull(methodDecl);
        RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromMethod(methodDecl),
            displayName: methodDecl.Name,
            methodDecl.ParentDecl,
            reason,
            details);
    }

    /// <summary>
    /// Decl-aware entry point for properties. Pass an explicit
    /// <paramref name="accessor"/> when distinguishing per-accessor skips
    /// (e.g. setter rejected but getter emitted).
    /// </summary>
    public static void RecordMemberSkipped(
        PropertyDecl propertyDecl,
        SkipReason reason,
        string? details = null,
        AccessorKind accessor = AccessorKind.None)
    {
        ArgumentNullException.ThrowIfNull(propertyDecl);
        RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromProperty(propertyDecl, accessor),
            displayName: propertyDecl.Name,
            propertyDecl.ParentDecl,
            reason,
            details);
    }

    /// <summary>
    /// Decl-aware entry point for subscripts. Pass
    /// <see cref="AccessorKind.SubscriptGetter"/> /
    /// <see cref="AccessorKind.SubscriptSetter"/> to distinguish per-accessor
    /// skips on the same subscript shape.
    /// </summary>
    public static void RecordMemberSkipped(
        SubscriptDecl subscriptDecl,
        SkipReason reason,
        string? details = null,
        AccessorKind accessor = AccessorKind.None)
    {
        ArgumentNullException.ThrowIfNull(subscriptDecl);
        RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromSubscript(subscriptDecl, accessor),
            displayName: subscriptDecl.Name,
            subscriptDecl.ParentDecl,
            reason,
            details);
    }

    /// <summary>
    /// Decl-aware entry point for operators. Captures the parameter
    /// signature from the underlying method so overloaded operators record
    /// distinctly.
    /// </summary>
    public static void RecordMemberSkipped(OperatorDecl operatorDecl, SkipReason reason, string? details = null)
    {
        ArgumentNullException.ThrowIfNull(operatorDecl);
        RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromOperator(operatorDecl),
            displayName: operatorDecl.OperatorSymbol,
            operatorDecl.ParentDecl,
            reason,
            details);
    }

    public static void RecordMemberWrapped(
        BindingItemKind kind, string name, string? mangledName,
        BaseDecl? containingDecl, string wrapperKind, string? details = null)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            // RecordMemberWrapped intentionally uses the legacy coarse identity
            // (no parameter info) so overloaded wrapped inits dedup to one
            // emitted entry — matching the distinct-name counting in
            // CalculateTotals. The WrappedItems list itself records each
            // overload distinctly via the mangled name.
            var identity = MemberDiagnosticIdentity.FromMember(kind, name, containingDecl);
            EmittedMemberIdentities.Add(identity);

            _report.WrappedItems.Add(new WrappedItem
            {
                Kind = kind,
                Name = name,
                MangledName = mangledName,
                ContainingType = GetContainingTypeName(containingDecl),
                WrapperKind = wrapperKind,
                Details = details,
            });
        }
    }

    public static void RecordBridgedView(string viewName, string moduleName, string initClassification, string bridgeStatus)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            _report.BridgedViews.Add(new BridgedViewItem
            {
                ViewName = viewName,
                ModuleName = moduleName,
                InitClassification = initClassification,
                BridgeStatus = bridgeStatus,
            });
        }
    }

    public static void RecordThemeBridged(string className, string propertyName, string propertyType)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            _report.ThemeBridgedProperties.Add(new ThemeBridgedItem
            {
                ClassName = className,
                PropertyName = propertyName,
                PropertyType = propertyType,
            });
        }
    }

    /// <summary>
    /// Legacy entry point — see <see cref="RecordMemberEmitted(BindingItemKind, string, BaseDecl?)"/>
    /// for overload-collapse caveats.
    /// </summary>
    public static void RecordMemberSynthesized(BindingItemKind kind, string name, BaseDecl? containingDecl)
        => RecordMemberSynthesizedInternal(MemberDiagnosticIdentity.FromMember(kind, name, containingDecl));

    /// <summary>
    /// Decl-aware entry point — captures the full method signature so
    /// overloaded synthesized methods record distinctly.
    /// </summary>
    public static void RecordMemberSynthesized(MethodDecl methodDecl, BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(methodDecl);
        RecordMemberSynthesizedInternal(MemberDiagnosticIdentity.FromMethod(methodDecl, containingDecl));
    }

    /// <summary>
    /// Decl-aware entry point for synthesized properties.
    /// </summary>
    public static void RecordMemberSynthesized(
        PropertyDecl propertyDecl,
        AccessorKind accessor = AccessorKind.None,
        BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(propertyDecl);
        RecordMemberSynthesizedInternal(MemberDiagnosticIdentity.FromProperty(propertyDecl, accessor, containingDecl));
    }

    /// <summary>
    /// Decl-aware entry point for synthesized operators.
    /// </summary>
    public static void RecordMemberSynthesized(OperatorDecl operatorDecl, BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(operatorDecl);
        RecordMemberSynthesizedInternal(MemberDiagnosticIdentity.FromOperator(operatorDecl, containingDecl));
    }

    private static void RecordMemberEmittedInternal(MemberDiagnosticIdentity identity)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            if (SkippedMemberIdentities.Contains(identity))
                return;

            EmittedMemberIdentities.Add(identity);
        }
    }

    private static void RecordMemberSkippedInternal(
        MemberDiagnosticIdentity identity,
        string displayName,
        BaseDecl? containingDecl,
        SkipReason reason,
        string? details)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            if (EmittedMemberIdentities.Contains(identity) || !SkippedMemberIdentities.Add(identity))
                return;

            _report.SkippedItems.Add(new SkippedItem
            {
                Kind = identity.Kind,
                Name = displayName,
                ContainingType = GetContainingTypeName(containingDecl),
                Reason = reason,
                Details = details,
                RecommendedWorkaround = WorkaroundRecommendations.GetRecommendation(reason),
            });
        }
    }

    private static void RecordMemberSynthesizedInternal(MemberDiagnosticIdentity identity)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            if (SkippedMemberIdentities.Contains(identity))
                return;

            SynthesizedMemberIdentities.Add(identity);
        }
    }

    private static void ResetUnsafe()
    {
        _report = null;
        EmittedTypeKeys.Clear();
        SkippedTypeKeys.Clear();
        EmittedMemberIdentities.Clear();
        SkippedMemberIdentities.Clear();
        SynthesizedMemberIdentities.Clear();
    }

    private static (int totalTypes, int totalMembers) CalculateTotals(ModuleDecl moduleDecl)
    {
        var totalTypes = 0;
        var totalMembers = moduleDecl.Methods.Where(m => !m.IsAccessor).Select(m => m.Name).Distinct().Count()
                        + moduleDecl.Properties.Select(p => p.Name).Distinct().Count();

        // moduleDecl.Types already includes ProtocolDecl instances (ProtocolDecl : TypeDecl),
        // so we don't separately iterate moduleDecl.Protocols to avoid double-counting.
        foreach (var typeDecl in moduleDecl.Types)
            CountTypeAndMembers(typeDecl, ref totalTypes, ref totalMembers);

        return (totalTypes, totalMembers);
    }

    private static void CountTypeAndMembers(TypeDecl typeDecl, ref int totalTypes, ref int totalMembers)
    {
        totalTypes++;
        // Count distinct member names per kind. Legacy callers (those still
        // using the (kind, name, containingDecl) overloads) collapse overloads
        // under the coarse identity; this distinct-name count keeps the
        // totals comparable to those legacy emitted/skipped counts. Decl-aware
        // callers populate per-overload identities — for those paths the
        // emitted/skipped counts can exceed the totals reported here, which
        // is the expected outcome of the overload-stable identity fix.
        // Protocol subscripts are all recorded under the single name "subscript",
        // so overloads share one HashSet key when invoked through the legacy path.
        totalMembers += typeDecl.Methods.Where(m => !m.IsAccessor).Select(m => m.Name).Distinct().Count()
                      + typeDecl.Properties.Select(p => p.Name).Distinct().Count()
                      + typeDecl.Operators.Select(o => o.Name).Distinct().Count()
                      + (typeDecl is ProtocolDecl && typeDecl.Subscripts.Count > 0 ? 1 : 0);

        // Protocol nested types are not currently emitted by ProtocolHandler.
        if (typeDecl is ProtocolDecl)
            return;

        foreach (var nestedType in typeDecl.Types)
        {
            CountTypeAndMembers(nestedType, ref totalTypes, ref totalMembers);
        }
    }

    private static string GetTypeKey(TypeDecl typeDecl) =>
        typeDecl.SwiftTypeName.ModuleQualifiedName;

    private static void ComputePerKindCounts(BindingReport report)
    {
        foreach (var identity in EmittedMemberIdentities)
        {
            report.EmittedMembersByKind[identity.Kind] = report.EmittedMembersByKind.GetValueOrDefault(identity.Kind) + 1;
        }
        foreach (var identity in SkippedMemberIdentities)
        {
            report.SkippedMembersByKind[identity.Kind] = report.SkippedMembersByKind.GetValueOrDefault(identity.Kind) + 1;
        }
    }

    private static BridgeSummary ComputeBridgeSummary(BindingReport report)
    {
        var views = report.BridgedViews;
        var generated = views.Count(v => v.BridgeStatus == "Generated");
        var template = views.Count(v => v.BridgeStatus == "TemplatePending");
        var hintSkipped = views.Count(v => v.BridgeStatus == "HintSkipped");
        var total = views.Count;

        return new BridgeSummary
        {
            TotalViews = total,
            Generated = generated,
            Template = template,
            HintSkipped = hintSkipped,
            GeneratedPercent = total > 0 ? Math.Round(100.0 * generated / total, 1) : 0,
        };
    }

    private static string? GetContainingTypeName(BaseDecl? containingDecl) =>
        containingDecl switch
        {
            TypeDecl typeDecl => typeDecl.SwiftTypeName.ModuleQualifiedName,
            ModuleDecl moduleDecl => moduleDecl.Name,
            null => null,
            _ => containingDecl.Name
        };
}
