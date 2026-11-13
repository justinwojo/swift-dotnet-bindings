// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Swift.Runtime;

namespace BindingsGeneration;

/// <summary>
/// The binding report's rows for the members that refuse, on some runtimes, because they reach a
/// direct Swift-convention transition no native bridge carries (<see cref="SwiftTransitionGuards"/>).
/// A row is a member the guard sidecar lists whose guard the final C# still holds, and whose guard
/// class still gives that guard a shape to refuse: a member whose transitions were all routed through
/// bridges after generation refuses nowhere and has no row.
/// </summary>
public static class SwiftTransitionGuardReport
{
    /// <summary>The <see cref="DegradedMemberItem.WrapperReason"/> of every row.</summary>
    public const string Reason = "swift_transition_guard";

    public static void Apply(BindingReport report, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        report.DegradedMembers.RemoveAll(item => string.Equals(item.DiagnosticId, SwiftTransitionGuards.MemberCode, StringComparison.Ordinal));
        var rows = new List<DegradedMemberItem>();
        if (Directory.Exists(outputDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(outputDirectory, SwiftTransitionGuardDocument.FilePattern).Order(StringComparer.Ordinal))
            {
                SwiftTransitionGuardDocument document;
                try
                {
                    document = SwiftTransitionGuardDocument.Parse(File.ReadAllText(path), path);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
                {
                    continue;
                }
                rows.AddRange(RowsOf(document, outputDirectory));
            }
        }
        rows.Sort((a, b) =>
        {
            var byType = string.CompareOrdinal(a.ContainingType, b.ContainingType);
            if (byType != 0)
                return byType;
            var byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.PublicApiKey, b.PublicApiKey);
        });
        report.DegradedMembers.AddRange(rows);
        DirectSwiftSelfExposureCollector.RecomputeSummary(report);
    }

    private static IEnumerable<DegradedMemberItem> RowsOf(SwiftTransitionGuardDocument document, string outputDirectory)
    {
        var @namespace = document.Namespace;
        var masksFile = SwiftTransitionGuards.MasksFileName(@namespace);
        var masksPath = Path.Combine(outputDirectory, masksFile);
        var masks = SwiftTransitionGuards.ParseMasks(File.Exists(masksPath) ? File.ReadAllText(masksPath) : null)
            .ToDictionary(m => m.Name, m => m.Facts, StringComparer.Ordinal);
        var transitions = document.Transitions.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // The guards the final C# holds, by the member they name and the constants they read.
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new[] { $"{@namespace}.cs" }
            .Concat(Directory.EnumerateFiles(outputDirectory, SplitFileNaming.TypeFileGlob(@namespace)).Select(Path.GetFileName).Cast<string>().Order(StringComparer.Ordinal))
            .Where(f => !string.Equals(f, masksFile, StringComparison.Ordinal) && File.Exists(Path.Combine(outputDirectory, f)));
        foreach (var file in files)
        {
            var source = File.ReadAllText(Path.Combine(outputDirectory, file));
            foreach (var guard in SwiftTransitionGuards.GuardsOf([source], [.. masks.Select(kv => new SwiftTransitionGuards.Mask(kv.Key, kv.Value, ""))]))
                held.TryAdd(GuardKey(guard.Member, guard.Transitions), file);
        }

        foreach (var site in document.Sites.Where(s => s.Visible))
        {
            if (!held.TryGetValue(GuardKey(site.Display, site.Transitions), out var file))
                continue;
            var reached = site.Transitions.Where(n => masks.GetValueOrDefault(n) != SwiftTransitionFacts.None).ToList();
            if (reached.Count == 0)
                continue;
            var facts = reached.Aggregate(SwiftTransitionFacts.None, (all, n) => all | masks[n]);
            var why = reached.Select(n => transitions.GetValueOrDefault(n)?.Residual).Where(r => r is not null).Distinct(StringComparer.Ordinal);
            yield return new DegradedMemberItem
            {
                Kind = site.Kind switch
                {
                    "Property" => BindingItemKind.Property,
                    "Indexer" => BindingItemKind.Subscript,
                    "Operator" => BindingItemKind.Operator,
                    _ => BindingItemKind.Method,
                },
                Name = site.Name,
                ContainingType = site.ContainingType,
                DiagnosticId = SwiftTransitionGuards.MemberCode,
                IsAttributeEmitted = false,
                Accessor = site.Kind is "Property" or "Indexer" or "Event" ? AccessorOf(site.Display) : null,
                PublicApiKey = site.Key,
                WrapperReason = Reason,
                WrapperReasonDescription =
                    $"{site.Display} refuses with SwiftTransitionUnsupportedException, before any native work, on runtimes that do not execute a " +
                    $"direct Swift-convention transition of shape {facts} correctly; it has no native bridge: {string.Join("; ", why)}",
                ReasonProvenance = "final_transition_guard",
                EmitterSite = nameof(SwiftTransitionGuards),
                GeneratedFile = file,
            };
        }
    }

    private static string GuardKey(string member, IEnumerable<string> transitions) =>
        member + "|" + string.Join(",", transitions.Order(StringComparer.Ordinal));

    private static string? AccessorOf(string display)
    {
        var match = Regex.Match(display, @"\.(get|set|init|add|remove)$");
        return match.Success ? match.Groups[1].Value : null;
    }
}
