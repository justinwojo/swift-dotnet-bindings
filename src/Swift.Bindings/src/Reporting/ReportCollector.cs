// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// In-process collector for binding generation report data.
/// </summary>
public static class ReportCollector
{
    private static readonly object Sync = new();
    private static readonly AsyncLocal<bool> SessionActive = new();
    private static BindingReport? _report;

    private static readonly HashSet<string> EmittedTypeKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SkippedTypeKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> EmittedMemberKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SkippedMemberKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SynthesizedMemberKeys = new(StringComparer.Ordinal);

    public static bool IsActive => SessionActive.Value && _report != null;

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
            _report.EmittedMembers = EmittedMemberKeys.Count;
            _report.SkippedMembers = SkippedMemberKeys.Count;
            _report.SynthesizedMembers = SynthesizedMemberKeys.Count;

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

    public static void RecordMemberEmitted(BindingItemKind kind, string name, BaseDecl? containingDecl)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            var key = GetMemberKey(kind, name, containingDecl);
            if (SkippedMemberKeys.Contains(key))
                return;

            EmittedMemberKeys.Add(key);
        }
    }

    public static void RecordMemberSkipped(BindingItemKind kind, string name, BaseDecl? containingDecl, SkipReason reason, string? details = null)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            var key = GetMemberKey(kind, name, containingDecl);
            if (EmittedMemberKeys.Contains(key) || !SkippedMemberKeys.Add(key))
                return;

            _report.SkippedItems.Add(new SkippedItem
            {
                Kind = kind,
                Name = name,
                ContainingType = GetContainingTypeName(containingDecl),
                Reason = reason,
                Details = details,
                RecommendedWorkaround = WorkaroundRecommendations.GetRecommendation(reason),
            });
        }
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

            // Use simple key (same as CountTypeAndMembers which counts distinct names).
            // Overloaded methods with different mangled names share a single simple key,
            // matching the distinct-name counting in CalculateTotals.
            var key = GetMemberKey(kind, name, containingDecl);
            EmittedMemberKeys.Add(key);

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

    public static void RecordMemberSynthesized(BindingItemKind kind, string name, BaseDecl? containingDecl)
    {
        if (!SessionActive.Value || _report == null)
            return;

        lock (Sync)
        {
            if (_report == null)
                return;

            var key = GetMemberKey(kind, name, containingDecl);
            if (SkippedMemberKeys.Contains(key))
                return;

            SynthesizedMemberKeys.Add(key);
        }
    }

    private static void ResetUnsafe()
    {
        _report = null;
        EmittedTypeKeys.Clear();
        SkippedTypeKeys.Clear();
        EmittedMemberKeys.Clear();
        SkippedMemberKeys.Clear();
        SynthesizedMemberKeys.Clear();
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
        // Count distinct member names per kind, matching the deduplication in the recording
        // HashSets (keyed by "Kind:ContainingType:Name"). Overloaded methods/constructors
        // with the same name share a single key and should be counted once.
        // Protocol subscripts are all recorded under the single name "subscript",
        // so overloads share one HashSet key. Count at most 1 to match.
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

    private static string GetMemberKey(BindingItemKind kind, string name, BaseDecl? containingDecl) =>
        $"{kind}:{GetContainingTypeName(containingDecl)}:{name}";

    private static string? GetContainingTypeName(BaseDecl? containingDecl) =>
        containingDecl switch
        {
            TypeDecl typeDecl => typeDecl.SwiftTypeName.ModuleQualifiedName,
            ModuleDecl moduleDecl => moduleDecl.Name,
            null => null,
            _ => containingDecl.Name
        };
}
