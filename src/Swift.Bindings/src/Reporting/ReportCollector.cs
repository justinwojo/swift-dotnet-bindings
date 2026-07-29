// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// In-process collector for binding generation report data.
/// </summary>
/// <remarks>
/// <para>
/// A report "session" is scoped to the current async control flow (<see cref="AsyncLocal{T}"/>).
/// <see cref="Start"/> installs a fresh <see cref="ReportSession"/> that owns ALL report state —
/// the <see cref="BindingReport"/>, every dedup collection, and the lock — and <see cref="Reset"/>
/// clears it. Previously only an <c>AsyncLocal&lt;bool&gt;</c> "is active" flag flowed with the
/// async context while the report and its collections were process-global static fields, so two
/// concurrent emission runs (or two parallel unit tests) each saw <c>IsActive == true</c> yet shared
/// one report — and either run's <c>Start()</c> silently wiped the other's data. Moving the whole
/// session onto the AsyncLocal gives each control flow its own isolated report, and a sequential run
/// in the same flow is cleanly superseded by the next <see cref="Start"/>. The static surface is a
/// deliberate facade over that flow-scoped state so ambient call sites that carry no emission context
/// (e.g. the class-ancestor skip walk reaching <see cref="IsTypeSkipped(string)"/>) keep working
/// without threading a context through every site.
/// </para>
/// <para>
/// Member dedup is keyed on <see cref="MemberDiagnosticIdentity"/>, which is
/// overload-stable: two distinct overloads (e.g. <c>foo(_:Int)</c> vs
/// <c>foo(_:String)</c>) skipped for different reasons each record their own
/// <see cref="SkippedItem"/> entry. Legacy callers that pass only
/// <c>(kind, name, containingDecl)</c> still produce a valid identity, but
/// without parameter labels/types — overloads collapse on those paths until
/// the call site is migrated to a decl-aware overload.
/// </para>
/// </remarks>
public static class ReportCollector
{
    /// <summary>
    /// The report state for the current async control flow. Null when no session is active.
    /// </summary>
    private static readonly AsyncLocal<ReportSession?> Session = new();

    private static ReportSession? Current => Session.Value;

    /// <summary>
    /// All per-session report state. Owned by a single async control flow at a time so nothing
    /// bleeds across concurrent or sequential emission runs.
    /// </summary>
    private sealed class ReportSession
    {
        internal readonly object Sync = new();
        internal readonly BindingReport Report;

        /// <summary>
        /// The module the totals were computed from, kept so the post-emission reconciliation can
        /// walk the same declarations again and account for members that whole-type suppression
        /// meant were never enumerated. Read only after emission finishes, so it cannot influence
        /// what is emitted.
        /// </summary>
        internal readonly ModuleDecl Module;

        internal readonly HashSet<string> EmittedTypeKeys = new(StringComparer.Ordinal);
        internal readonly HashSet<string> SkippedTypeKeys = new(StringComparer.Ordinal);

        /// <summary>
        /// Why each skipped type was skipped, keyed the same way as <see cref="SkippedTypeKeys"/>.
        /// The reconciliation needs the parent's reason to decide whether its unenumerated members
        /// were ever public surface, and recovering that by scanning the flat skip list would be a
        /// name match rather than the recorded fact.
        /// </summary>
        internal readonly Dictionary<string, SkipReason> SkippedTypeReasons = new(StringComparer.Ordinal);
        internal readonly HashSet<MemberDiagnosticIdentity> EmittedMemberIdentities = new();
        internal readonly HashSet<MemberDiagnosticIdentity> SkippedMemberIdentities = new();
        internal readonly HashSet<MemberDiagnosticIdentity> SynthesizedMemberIdentities = new();

        // Finding 53: ambient accumulators for the two previously-silent degradation mechanisms.
        // They live on the session (not on ModuleEmissionContext like SWIFTBIND023) because their
        // emission sites — the `// Unsupported:` comment chokepoint and the scattered closure
        // `?? "object"` fallbacks — have no ModuleEmissionContext in scope, but the flow-scoped
        // ReportCollector session is the ambient sink they all reach without threading. Dedup keeps
        // the loud diagnostic to one warning per distinct drop / degraded type.
        internal readonly HashSet<string> UnsupportedCommentDropEntries = new(StringComparer.Ordinal);
        internal readonly HashSet<string> ObjectDegradationEntries = new(StringComparer.Ordinal);

        // Canonical DeclId per comment-drop description, when the emitting site had a declaration
        // in scope. Keyed by the same description that drives the dedup set above, so the two
        // stay in lockstep; first writer wins, which is deterministic because the description is
        // itself derived from the decl.
        internal readonly Dictionary<string, string> UnsupportedCommentDropDeclIds = new(StringComparer.Ordinal);

        // F10 Stage 20: Apple-framework reference types bridged to their ObjC class purely by the
        // naming-convention heuristic (no database record) — see MarshallingHelpers.IsObjCPrefixBridgeCandidate.
        // Unlike the two degradation sets above, this is a SUCCESSFUL bridge, so it is recorded for
        // observability only (binding-report.json) and never surfaced as a loud warning.
        internal readonly HashSet<string> ObjCPrefixBridgeEntries = new(StringComparer.Ordinal);

        // CSM "recovered" facts: a skipped open-generic member (keyed by its containing-type + name, the
        // same identity the skip row carries) whose consumer surface a closed CSM projection actually
        // emitted. Accumulated during emission — the property skip is recorded in-body while the CSM
        // projection is a post-body hook — then joined onto the matching SkippedItem in Complete(). This
        // is an emission fact (a projection was emitted), not a name-pattern guess, so a skip row without
        // a RecoveredBy annotation genuinely is unreachable.
        internal readonly Dictionary<(string? ContainingType, string Name), List<string>> RecoveredMembers = new();

        internal ReportSession(BindingReport report, ModuleDecl module)
        {
            Report = report;
            Module = module;
        }
    }

    public static bool IsActive => Current != null;

    /// <summary>
    /// Query whether a type is known to be skipped. Populated by <see cref="RecordTypeSkipped"/>
    /// and by a pre-emission pass that runs the skip predicates upfront so member gates
    /// can prune signatures referencing types that will never be declared. The key is the
    /// module-qualified name (e.g., "MusicKit.MusicRelationshipProperty"). Returns false
    /// when no session is active.
    /// </summary>
    public static bool IsTypeSkipped(string moduleQualifiedTypeName)
    {
        var session = Current;
        if (session == null)
            return false;
        lock (session.Sync)
            return session.SkippedTypeKeys.Contains(moduleQualifiedTypeName);
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

        var report = new BindingReport
        {
            ModuleName = moduleDecl.Name,
        };
        (report.TotalTypes, report.TotalMembers) = CalculateTotals(moduleDecl);

        // Installing a fresh session supersedes any prior one on this flow without touching
        // another flow's session — the old cross-run wipe is gone.
        Session.Value = new ReportSession(report, moduleDecl);
    }

    public static BindingReport? Complete()
    {
        var session = Current;
        if (session == null)
            return null;

        lock (session.Sync)
        {
            var report = session.Report;

            // Account for members that whole-type suppression meant were never enumerated. This has
            // to run before the counts below are read, and it deliberately runs at completion rather
            // than at each suppression site: by now every emitted/skipped fact is settled, so the
            // pass adds a row only where nothing else claimed the member — and because emission has
            // already finished, nothing it records can feed back into what was generated.
            ReconcileSuppressedParentMembers(session);

            report.EmittedTypes = session.EmittedTypeKeys.Count;
            report.SkippedTypes = session.SkippedTypeKeys.Count;
            report.EmittedMembers = session.EmittedMemberIdentities.Count;
            report.SkippedMembers = session.SkippedMemberIdentities.Count;
            report.SynthesizedMembers = session.SynthesizedMemberIdentities.Count;

            // Finding 53: flow the ambient degradation accumulators onto the report (sorted for
            // deterministic output) so they survive Reset() and drive the loud SWIFTBIND025/026
            // diagnostics emitted from the report block.
            report.UnsupportedCommentDrops.AddRange(
                session.UnsupportedCommentDropEntries.OrderBy(x => x, StringComparer.Ordinal));
            // Parallel identity-carrying view of the same drops, in the same order.
            report.UnsupportedCommentDropDetails.AddRange(
                session.UnsupportedCommentDropEntries
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .Select(description => new UnsupportedCommentDropItem
                    {
                        Description = description,
                        DeclId = session.UnsupportedCommentDropDeclIds.GetValueOrDefault(description),
                    }));
            report.ObjectDegradations.AddRange(
                session.ObjectDegradationEntries.OrderBy(x => x, StringComparer.Ordinal));
            // F10 Stage 20: flow the ObjC-prefix bridge guesses onto the report (sorted) so they
            // survive Reset() and round-trip into binding-report.json via the manifest.
            report.ObjCPrefixBridges.AddRange(
                session.ObjCPrefixBridgeEntries.OrderBy(x => x, StringComparer.Ordinal));

            // Join the accumulated CSM-recovery facts onto their skip rows: a member skipped on the open
            // shell whose typed surface a closed projection actually emitted is annotated (and reclassified
            // to SkipDisposition.Recovered by the item-aware classifier) instead of reading as a plain,
            // unreachable skip. Match on (ContainingType, Name) — the same identity the skip row carries —
            // so the linkage is exact, not a name-pattern guess.
            if (session.RecoveredMembers.Count > 0)
            {
                foreach (var item in report.SkippedItems)
                {
                    if (session.RecoveredMembers.TryGetValue((item.ContainingType, item.Name), out var recoverers))
                        item.RecoveredBy = recoverers.OrderBy(x => x, StringComparer.Ordinal).ToList();
                }
            }

            // Per-kind breakdown: read directly from each identity's Kind field.
            ComputePerKindCounts(session, report);

            // Compute BridgeSummary if there are bridged views
            if (report.BridgedViews.Count > 0)
                report.BridgeSummary = ComputeBridgeSummary(report);

            return report;
        }
    }

    public static void Reset()
    {
        // Discarding the session is a full clear for this flow; another flow's session is untouched.
        Session.Value = null;
    }

    public static void RecordTypeEmitted(TypeDecl typeDecl)
    {
        var session = Current;
        if (session == null)
            return;

        lock (session.Sync)
        {
            var key = GetTypeKey(typeDecl);
            if (session.SkippedTypeKeys.Contains(key))
                return;

            session.EmittedTypeKeys.Add(key);
        }
    }

    public static void RecordTypeSkipped(
        TypeDecl typeDecl,
        SkipReason reason,
        string? details = null,
        SourcePosition? position = null)
    {
        var session = Current;
        if (session == null)
            return;

        // Best-effort: when the caller did not supply an explicit override, fall back to
        // whatever swiftinterface position the parser stamped on the decl.
        position ??= typeDecl.Position;

        lock (session.Sync)
        {
            var key = GetTypeKey(typeDecl);
            if (session.EmittedTypeKeys.Contains(key) || !session.SkippedTypeKeys.Add(key))
                return;

            session.SkippedTypeReasons[key] = reason;
            session.Report.SkippedItems.Add(new SkippedItem
            {
                Kind = BindingItemKind.Type,
                Name = typeDecl.Name,
                ContainingType = typeDecl.ParentDecl is TypeDecl parentType ? parentType.SwiftTypeName.ModuleQualifiedName : null,
                Reason = reason,
                Details = details,
                RecommendedWorkaround = WorkaroundRecommendations.GetRecommendation(reason),
                Position = position,
                DeclId = DeclIdFactory.ForType(typeDecl).Canonical,
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
    public static void RecordMemberSkipped(
        BindingItemKind kind,
        string name,
        BaseDecl? containingDecl,
        SkipReason reason,
        string? details = null,
        SourcePosition? position = null)
        => RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromMember(kind, name, containingDecl),
            displayName: name,
            containingDecl,
            reason,
            details,
            // Legacy entry: caller passes the containing decl, not the member, so the best
            // available source is the parent's position (member-line positions belong on the
            // member decl itself, which this entry doesn't have access to).
            position ?? containingDecl?.Position);

    /// <summary>
    /// Decl-aware entry point — captures the full method signature so two
    /// overloads of <c>foo(_ x: Int)</c> and <c>foo(_ s: String)</c> skipped
    /// for different reasons each land in <see cref="BindingReport.SkippedItems"/>.
    /// </summary>
    public static void RecordMemberSkipped(
        MethodDecl methodDecl,
        SkipReason reason,
        string? details = null,
        SourcePosition? position = null)
    {
        ArgumentNullException.ThrowIfNull(methodDecl);
        RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromMethod(methodDecl),
            displayName: methodDecl.Name,
            methodDecl.ParentDecl,
            reason,
            details,
            position ?? methodDecl.Position);
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
        AccessorKind accessor = AccessorKind.None,
        SourcePosition? position = null)
    {
        ArgumentNullException.ThrowIfNull(propertyDecl);
        RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromProperty(propertyDecl, accessor),
            displayName: propertyDecl.Name,
            propertyDecl.ParentDecl,
            reason,
            details,
            position ?? propertyDecl.Position);
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
        AccessorKind accessor = AccessorKind.None,
        SourcePosition? position = null)
    {
        ArgumentNullException.ThrowIfNull(subscriptDecl);
        RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromSubscript(subscriptDecl, accessor),
            displayName: subscriptDecl.Name,
            subscriptDecl.ParentDecl,
            reason,
            details,
            position ?? subscriptDecl.Position);
    }

    /// <summary>
    /// Decl-aware entry point for operators. Captures the parameter
    /// signature from the underlying method so overloaded operators record
    /// distinctly.
    /// </summary>
    public static void RecordMemberSkipped(
        OperatorDecl operatorDecl,
        SkipReason reason,
        string? details = null,
        SourcePosition? position = null)
    {
        ArgumentNullException.ThrowIfNull(operatorDecl);
        RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromOperator(operatorDecl),
            displayName: operatorDecl.OperatorSymbol,
            operatorDecl.ParentDecl,
            reason,
            details,
            position ?? operatorDecl.Position);
    }

    public static void RecordMemberWrapped(
        BindingItemKind kind, string name, string? mangledName,
        BaseDecl? containingDecl, string wrapperKind, string? details = null)
    {
        var session = Current;
        if (session == null)
            return;

        lock (session.Sync)
        {
            // RecordMemberWrapped intentionally uses the legacy coarse identity
            // (no parameter info) so overloaded wrapped inits dedup to one
            // emitted entry — matching the distinct-name counting in
            // CalculateTotals. The WrappedItems list itself records each
            // overload distinctly via the mangled name.
            var identity = MemberDiagnosticIdentity.FromMember(kind, name, containingDecl);
            session.EmittedMemberIdentities.Add(identity);

            session.Report.WrappedItems.Add(new WrappedItem
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
        var session = Current;
        if (session == null)
            return;

        lock (session.Sync)
        {
            session.Report.BridgedViews.Add(new BridgedViewItem
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
        var session = Current;
        if (session == null)
            return;

        lock (session.Sync)
        {
            session.Report.ThemeBridgedProperties.Add(new ThemeBridgedItem
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
        var session = Current;
        if (session == null)
            return;

        lock (session.Sync)
        {
            if (session.SkippedMemberIdentities.Contains(identity))
                return;

            session.EmittedMemberIdentities.Add(identity);
        }
    }

    private static void RecordMemberSkippedInternal(
        MemberDiagnosticIdentity identity,
        string displayName,
        BaseDecl? containingDecl,
        SkipReason reason,
        string? details,
        SourcePosition? position = null)
    {
        var session = Current;
        if (session == null)
            return;

        lock (session.Sync)
        {
            if (session.EmittedMemberIdentities.Contains(identity) || !session.SkippedMemberIdentities.Add(identity))
                return;

            session.Report.SkippedItems.Add(new SkippedItem
            {
                Kind = identity.Kind,
                Name = displayName,
                ContainingType = GetContainingTypeName(containingDecl),
                Reason = reason,
                Details = details,
                RecommendedWorkaround = WorkaroundRecommendations.GetRecommendation(reason),
                Position = position,
                DeclId = identity.ToDeclId().Canonical,
            });
        }
    }

    /// <summary>
    /// Records that a skipped member's consumer surface was recovered by a closed CSM projection.
    /// <paramref name="baseMember"/> is the ORIGINAL open-generic decl that PropertyHandler skipped (so
    /// its containing-type + name key matches the recorded skip row exactly); <paramref name="projection"/>
    /// names the closed typed projection that recovers it (e.g. <c>MusicLibraryResponse&lt;Album&gt;.items</c>).
    /// Called once per emitted projection; the annotations accumulate and are joined onto the skip row in
    /// <see cref="Complete"/>. No-op outside an active session. Never call this from a name heuristic — only
    /// from the emission site that actually produced the projection.
    /// </summary>
    public static void RecordMemberRecovered(PropertyDecl baseMember, string projection)
    {
        ArgumentNullException.ThrowIfNull(baseMember);
        ArgumentException.ThrowIfNullOrWhiteSpace(projection);

        var session = Current;
        if (session == null)
            return;

        var key = (GetContainingTypeName(baseMember.ParentDecl), baseMember.Name);
        lock (session.Sync)
        {
            if (!session.RecoveredMembers.TryGetValue(key, out var list))
            {
                list = new List<string>();
                session.RecoveredMembers[key] = list;
            }
            if (!list.Contains(projection, StringComparer.Ordinal))
                list.Add(projection);
        }
    }

    private static void RecordMemberSynthesizedInternal(MemberDiagnosticIdentity identity)
    {
        var session = Current;
        if (session == null)
            return;

        lock (session.Sync)
        {
            if (session.SkippedMemberIdentities.Contains(identity))
                return;

            session.SynthesizedMemberIdentities.Add(identity);
        }
    }

    /// <summary>
    /// Records a <c>// Unsupported:</c> comment-drop (Finding 53): a type/member that could not be
    /// bound and was left as a comment in the generated C#. <paramref name="description"/> is the
    /// comment text minus its leading <c>// </c>, used as the dedup key so the loud SWIFTBIND025
    /// diagnostic fires once per distinct drop. No-op outside an active report session.
    /// </summary>
    public static void RecordUnsupportedCommentDrop(string description, DeclId? declId = null)
    {
        var session = Current;
        if (session == null || string.IsNullOrEmpty(description))
            return;

        lock (session.Sync)
        {
            session.UnsupportedCommentDropEntries.Add(description);
            if (declId is { } id)
                session.UnsupportedCommentDropDeclIds.TryAdd(description, id.Canonical);
        }
    }

    /// <summary>
    /// Records a Swift type that degraded to bare <c>object</c> with no <c>[UnsupportedSwiftType]</c>
    /// marker (Finding 53) — e.g. an existential the resolver could not project at a closure
    /// parameter/return position. Surfaced as one loud SWIFTBIND026 per distinct type. No-op outside
    /// an active report session.
    /// </summary>
    public static void RecordObjectDegradation(string swiftType)
    {
        var session = Current;
        if (session == null || string.IsNullOrEmpty(swiftType))
            return;

        lock (session.Sync)
        {
            session.ObjectDegradationEntries.Add(swiftType);
        }
    }

    /// <summary>
    /// Records an Apple-framework reference type that was bridged to its ObjC class purely by the
    /// naming-convention heuristic (<see cref="MarshallingHelpers.IsObjCPrefixBridgeCandidate"/>) —
    /// i.e. it had NO database record and was recognized as an ObjC class by module + prefix alone
    /// (F10 Stage 20). Unlike SWIFTBIND025/026 this is a SUCCESSFUL bridge, not a degradation, so it
    /// is recorded for observability — it surfaces under <c>objcPrefixBridges</c> in
    /// binding-report.json — but is NOT emitted as a loud per-type warning, since the heuristic is
    /// correct and ubiquitous (every <c>UIImage?</c>/<c>NSURL?</c>) and a warning would cry wolf.
    /// No-op outside an active report session.
    /// </summary>
    public static void RecordObjCPrefixBridge(string swiftType)
    {
        var session = Current;
        if (session == null || string.IsNullOrEmpty(swiftType))
            return;

        lock (session.Sync)
        {
            session.ObjCPrefixBridgeEntries.Add(swiftType);
        }
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

    /// <summary>
    /// The declaring type whose suppression a member row is being attributed to, together with the
    /// one fact that decides the row's tier: whether that type was public surface at all.
    /// </summary>
    private readonly record struct SuppressedParentType(string QualifiedName, SkipReason Reason, bool NeverPublic);

    /// <summary>
    /// Name-level key for "this member already has an accounting event". Coarser than
    /// <see cref="MemberDiagnosticIdentity"/> on purpose: emitted and skipped rows are recorded
    /// through a mix of per-overload (decl-aware) and per-name (legacy) entry points, so matching on
    /// the full identity would miss an overload-keyed row and re-record the member. The totals this
    /// reconciliation closes against are themselves per-name, so per-name is also the right grain.
    /// </summary>
    private readonly record struct MemberNameKey(string Module, string DeclPath, BindingItemKind Kind, string BaseName);

    /// <summary>
    /// Accounts for every member the emission walk never reached because the type declaring it was
    /// suppressed as a whole. Whole-type suppression short-circuits before the member loop at every
    /// site that records one (SwiftUI <c>View</c>, <c>@_spi</c>, underscore-internal,
    /// supplement-owned, emitter-faulted, missing-handler, and the shared
    /// <c>TypeSkipConditions</c> set), so those members are counted in
    /// <see cref="BindingReport.TotalMembers"/> yet recorded as neither emitted nor skipped —
    /// invisible to every roll-up computed from the skip list, and most invisible exactly where
    /// suppression removes the most surface.
    /// </summary>
    /// <remarks>
    /// Runs at completion, after every emitted/skipped fact is settled, so a row is added only where
    /// nothing else claimed the member. Purely additive to the report — no emission decision reads
    /// any of this state by the time it runs.
    /// </remarks>
    private static void ReconcileSuppressedParentMembers(ReportSession session)
    {
        if (session.SkippedTypeKeys.Count == 0)
            return;

        var accounted = BuildAccountedMemberKeys(session);
        foreach (var typeDecl in session.Module.Types)
            ReconcileType(session, typeDecl, accounted, suppressedBy: null);
    }

    /// <summary>
    /// Every member that already has an accounting event, at the per-name grain described on
    /// <see cref="MemberNameKey"/>. Synthesized members count as accounted: a synthesized member has
    /// a C# surface, so recording it as lost would be false.
    /// </summary>
    private static HashSet<MemberNameKey> BuildAccountedMemberKeys(ReportSession session)
    {
        var accounted = new HashSet<MemberNameKey>();
        foreach (var identity in session.EmittedMemberIdentities)
            accounted.Add(KeyOf(identity));
        foreach (var identity in session.SkippedMemberIdentities)
            accounted.Add(KeyOf(identity));
        foreach (var identity in session.SynthesizedMemberIdentities)
            accounted.Add(KeyOf(identity));
        return accounted;
    }

    private static MemberNameKey KeyOf(MemberDiagnosticIdentity identity) =>
        new(identity.Module ?? string.Empty, identity.DeclPath ?? string.Empty, identity.Kind, identity.BaseName ?? string.Empty);

    /// <summary>
    /// Walks the declaration tree exactly the way <see cref="CountTypeAndMembers"/> does — same
    /// nesting rule, so the reconciliation cannot visit a type the totals did not count, or miss one
    /// they did.
    /// </summary>
    private static void ReconcileType(
        ReportSession session,
        TypeDecl typeDecl,
        HashSet<MemberNameKey> accounted,
        SuppressedParentType? suppressedBy)
    {
        var key = GetTypeKey(typeDecl);
        if (session.SkippedTypeReasons.TryGetValue(key, out var ownReason))
        {
            // A type's own suppression is a more precise attribution for its members than an
            // ancestor's, so it supersedes any inherited one.
            suppressedBy = new SuppressedParentType(
                key,
                ownReason,
                SkipDispositionClassifier.Classify(ownReason) == SkipDisposition.ExpectedNonPublic);
        }

        if (suppressedBy is { } parent)
            RecordUnaccountedMembers(session, typeDecl, accounted, parent);

        // Protocol nested types are neither emitted nor counted in the totals; mirror that here.
        if (typeDecl is ProtocolDecl)
            return;

        foreach (var nested in typeDecl.Types)
            ReconcileType(session, nested, accounted, suppressedBy);
    }

    /// <summary>
    /// Adds one skip row per counted member of <paramref name="typeDecl"/> that nothing else claimed.
    /// The enumeration mirrors <see cref="CountTypeAndMembers"/> kind for kind, including its
    /// distinct-name collapse, so a fully suppressed type contributes exactly as many rows as it
    /// contributed to <see cref="BindingReport.TotalMembers"/>.
    /// </summary>
    private static void RecordUnaccountedMembers(
        ReportSession session,
        TypeDecl typeDecl,
        HashSet<MemberNameKey> accounted,
        SuppressedParentType parent)
    {
        var details = SuppressedParentSkipCause.Format(parent.QualifiedName, parent.Reason, parent.NeverPublic);

        foreach (var method in typeDecl.Methods.Where(m => !m.IsAccessor).DistinctBy(m => m.Name, StringComparer.Ordinal))
            TryRecordUnaccountedMember(session, accounted, BindingItemKind.Method, method.Name, typeDecl, details, method.Position);

        foreach (var property in typeDecl.Properties.DistinctBy(p => p.Name, StringComparer.Ordinal))
            TryRecordUnaccountedMember(session, accounted, BindingItemKind.Property, property.Name, typeDecl, details, property.Position);

        foreach (var op in typeDecl.Operators.DistinctBy(o => o.Name, StringComparer.Ordinal))
        {
            // An operator is recorded under its SYMBOL by the decl-aware path but counted under its
            // declaration NAME, so both spellings have to be checked before calling it unaccounted.
            if (accounted.Contains(KeyFor(BindingItemKind.Operator, op.OperatorSymbol, typeDecl)))
                continue;
            TryRecordUnaccountedMember(session, accounted, BindingItemKind.Operator, op.Name, typeDecl, details, op.Position);
        }

        // The totals give a protocol's whole subscript family a single slot regardless of overload
        // count, so this contributes at most one row for it.
        if (typeDecl is ProtocolDecl && typeDecl.Subscripts.Count > 0)
        {
            var subscript = typeDecl.Subscripts[0];
            TryRecordUnaccountedMember(
                session, accounted, BindingItemKind.Subscript, subscript.Name, typeDecl, details, subscript.Position);
        }
    }

    private static void TryRecordUnaccountedMember(
        ReportSession session,
        HashSet<MemberNameKey> accounted,
        BindingItemKind kind,
        string name,
        TypeDecl typeDecl,
        string details,
        SourcePosition? position)
    {
        // Add() doubles as the guard: false means the member is already emitted, already skipped, or
        // already recorded by this pass.
        if (!accounted.Add(KeyFor(kind, name, typeDecl)))
            return;

        RecordMemberSkippedInternal(
            MemberDiagnosticIdentity.FromMember(kind, name, typeDecl),
            name,
            typeDecl,
            SkipReason.ParentTypeSuppressed,
            details,
            position);
    }

    private static MemberNameKey KeyFor(BindingItemKind kind, string name, TypeDecl typeDecl) =>
        KeyOf(MemberDiagnosticIdentity.FromMember(kind, name, typeDecl));

    private static string GetTypeKey(TypeDecl typeDecl) =>
        typeDecl.SwiftTypeName.ModuleQualifiedName;

    private static void ComputePerKindCounts(ReportSession session, BindingReport report)
    {
        foreach (var identity in session.EmittedMemberIdentities)
        {
            report.EmittedMembersByKind[identity.Kind] = report.EmittedMembersByKind.GetValueOrDefault(identity.Kind) + 1;
        }
        foreach (var identity in session.SkippedMemberIdentities)
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
        var skipped = views.Count(v => v.BridgeStatus == "Skipped");
        var total = views.Count;

        return new BridgeSummary
        {
            TotalViews = total,
            Generated = generated,
            Template = template,
            HintSkipped = hintSkipped,
            Skipped = skipped,
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
