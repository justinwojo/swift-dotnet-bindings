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
        var totalMembers = moduleDecl.Methods.Count + moduleDecl.Properties.Count;

        foreach (var typeDecl in moduleDecl.Types)
            CountTypeAndMembers(typeDecl, ref totalTypes, ref totalMembers);

        foreach (var protocolDecl in moduleDecl.Protocols)
            CountTypeAndMembers(protocolDecl, ref totalTypes, ref totalMembers);

        return (totalTypes, totalMembers);
    }

    private static void CountTypeAndMembers(TypeDecl typeDecl, ref int totalTypes, ref int totalMembers)
    {
        totalTypes++;
        totalMembers += typeDecl.Methods.Count + typeDecl.Properties.Count + typeDecl.Operators.Count + typeDecl.Subscripts.Count;

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
