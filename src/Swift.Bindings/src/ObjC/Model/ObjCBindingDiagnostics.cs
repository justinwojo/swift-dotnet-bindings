// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public enum ObjCSkipReason
{
    UnresolvableType,
    UnavailableApi,
    UnsupportedConstruct,
    AccessibilityConflict,
    DuplicateSignature,
}

public sealed record ObjCSkippedSymbol(
    string SymbolKind,
    string SymbolName,
    ObjCSkipReason Reason,
    string Detail);

/// <summary>
/// Collects diagnostics about skipped symbols during ObjC binding emission.
/// Provides a summary at pipeline end for transparency about binding coverage.
/// </summary>
public sealed class ObjCBindingDiagnostics
{
    private readonly List<ObjCSkippedSymbol> _skipped = [];

    public IReadOnlyList<ObjCSkippedSymbol> SkippedSymbols => _skipped;

    public void RecordSkip(string symbolKind, string symbolName, ObjCSkipReason reason, string detail)
    {
        _skipped.Add(new ObjCSkippedSymbol(symbolKind, symbolName, reason, detail));
    }

    /// <summary>
    /// Logs a summary of skipped symbols at info level.
    /// </summary>
    public void LogSummary(ILogger logger)
    {
        if (_skipped.Count == 0)
        {
            logger.LogInformation("  Diagnostics: no symbols skipped.");
            return;
        }

        var byReason = _skipped.GroupBy(s => s.Reason).OrderByDescending(g => g.Count());
        logger.LogInformation("  Diagnostics: {Count} symbol(s) skipped:", _skipped.Count);
        foreach (var group in byReason)
        {
            var count = group.Count();
            logger.LogInformation("    {Reason}: {Count}", group.Key, count);
            foreach (var sym in group.Take(10))
                logger.LogInformation("      - {Kind} '{Name}': {Detail}", sym.SymbolKind, sym.SymbolName, sym.Detail);
            if (count > 10)
                logger.LogInformation("      ... and {More} more", count - 10);
        }
    }
}
