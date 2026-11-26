// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.SwiftDeclarationCompletenessGate.cs — the Swift declaration record's completeness gate for
// `nuke binding-tests --compile-only`.
//
// The package merge compares the copies' Swift declaration records ({namespace}.swift-declarations.json)
// and refuses a merge in which a C# name of one copy means another Swift declaration in the other
// (SWIFTBIND138). It can only check the names a record holds. This gate reads the compiled compile-check
// assembly and fails when a public or protected type or member of a BindingTests module is neither named
// by its module's record nor matched by one of the named fixed-name rules
// (SwiftDeclarationRecordCompleteness.Rules, each with the reason no Swift declaration can move what it
// matches), and when a record key names nothing the assembly declares. A newly emitted kind therefore
// fails here until its emitter records it or a rule is written for it.
//
// Its negative controls run every time: the record with one recorded member's key removed must fail
// naming that member, the record with a key the assembly does not declare must fail naming that key, and
// a key respelling a recorded method's parameters, with no unrecorded overload left for it, must fail.
// A checker that stopped reading either side would otherwise pass vacuously.
//
// Fail-closed and unconditional, with no `--permissive` arm: a policy, not a baseline.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BindingsGeneration;
using Serilog;

partial class Build
{
    /// <summary>The BindingTests modules whose records the completeness gate checks, with their record files.</summary>
    IEnumerable<(string Namespace, string Record)> SwiftDeclarationRecords() => new[]
    {
        ("SwiftBindingsTestLib", (string)(BtOutputDir / ("SwiftBindingsTestLib" + SwiftDeclarationRecordFormat.FileSuffix))),
        ("SwiftBindingsTestLibDependency", (string)(BtOutputDir / ("SwiftBindingsTestLibDependency" + SwiftDeclarationRecordFormat.FileSuffix))),
    };

    /// <summary>
    /// Asserts that every public or protected type and member of each BindingTests module in the compiled
    /// compile check is named by the module's Swift declaration record or matched by a named fixed-name
    /// rule, and that every record key names a declared member. Invoked from the --compile-only path once
    /// the compile check has built the assembly.
    /// </summary>
    void RunSwiftDeclarationCompletenessGate()
    {
        Log.Information("=========================================");
        Log.Information(" Swift declaration record completeness gate");
        Log.Information("=========================================");

        var assembly = CompileCheckOutputDir / "CompileCheck.dll";
        if (!File.Exists(assembly))
            throw new Exception($"Swift declaration completeness gate: {assembly} not found; the compile check did not produce it.");
        var image = File.ReadAllBytes(assembly);

        var failures = new List<string>();
        foreach (var (ns, recordPath) in SwiftDeclarationRecords())
        {
            if (!File.Exists(recordPath))
                throw new Exception($"Swift declaration completeness gate: {recordPath} not found; the regeneration did not write the {ns} record.");
            var record = File.ReadAllBytes(recordPath);
            var report = SwiftDeclarationRecordCompleteness.Check(image, ns, record);
            Log.Information("  {Namespace}: {Listed} public type(s) and member(s), {Recorded} named by the record, {Unresolved} unresolved key(s), {Uncovered} uncovered",
                ns, report.Listed, report.Recorded, report.UnresolvedKeys.Count, report.Uncovered.Count);
            foreach (var (rule, count) in report.ByRule.Where(r => r.Value > 0))
                Log.Information("    rule {Rule}: {Count}", rule, count);
            foreach (var member in report.Uncovered.Take(40))
                Log.Error("    ✗ uncovered {Kind} {Member}", member.Kind, member);
            if (report.Uncovered.Count > 40)
                Log.Error("    … and {More} more uncovered", report.Uncovered.Count - 40);
            foreach (var key in report.UnresolvedKeys.Take(40))
                Log.Error("    ✗ unresolved key {Key}", key);
            if (report.Listed == 0 || report.Recorded == 0)
                failures.Add($"{ns}: the check read {report.Listed} member(s) and resolved {report.Recorded}, so it read nothing");
            if (!report.Passed)
                failures.Add($"{ns}: {report.Uncovered.Count} uncovered member(s), {report.UnresolvedKeys.Count} unresolved key(s)");

            failures.AddRange(RunCompletenessNegativeControls(image, ns, record).Select(f => $"{ns}: {f}"));
        }

        if (failures.Count > 0)
            throw new Exception(
                "Swift declaration completeness gate: " + string.Join(" | ", failures) +
                ". Record the member where it is emitted (ModuleEmissionContext.RecordSwiftDeclaration), or add a " +
                "justified fixed-name rule to SwiftDeclarationRecordCompleteness.Rules.");
        Log.Information("  ✓ every public type and member is recorded or named by a fixed-name rule; every negative control failed as designed");
    }

    /// <summary>
    /// The gate's negative controls on one module: dropping a recorded member's key must leave that member
    /// uncovered, a key the assembly does not declare must be reported unresolved, and so must a key
    /// respelling a recorded method's parameters. Returns the controls
    /// that did not fail as designed.
    /// </summary>
    static IEnumerable<string> RunCompletenessNegativeControls(byte[] image, string ns, byte[] record)
    {
        var root = JsonNode.Parse(record)!.AsObject();
        var members = root["members"]?.AsObject();
        if (members is null || members.Count == 0)
        {
            yield return "negative controls: the record has no member keys to remove";
            yield break;
        }

        // Control 1: remove the key of a member no rule would cover, so only the record names it.
        var index = SwiftDeclarationRecordKeys.Index(image, ns);
        var module = root["module"]?.GetValue<string>();
        var swiftTypes = (root["types"]?.AsObject().Select(t => t.Key) ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        // A member is picked only if no rule could match it once its key is gone.
        var scope = new SwiftDeclarationRecordCompleteness.Scope(index, ns, module) { Image = image };
        string? dropped = null;
        SwiftDeclarationRecordKeys.Member? droppedMember = null;
        foreach (var (key, _) in members)
        {
            var parsed = SwiftDeclarationRecordKeys.Parse(key);
            if (parsed is null)
                continue;
            var resolved = index.Resolve(parsed, module, swiftTypes);
            if (resolved.Count == 1 && !SwiftDeclarationRecordCompleteness.Rules.Any(r => r.Matches(resolved[0], scope))
                && members.Count(m => SwiftDeclarationRecordKeys.Parse(m.Key) is { } other && index.Resolve(other, module, swiftTypes).Contains(resolved[0])) == 1)
            {
                dropped = key;
                droppedMember = resolved[0];
                break;
            }
        }
        if (dropped is null)
        {
            yield return "negative control 1: no recorded member is named only by its own key, so nothing could be removed";
        }
        else
        {
            var doctored = JsonNode.Parse(record)!.AsObject();
            doctored["members"]!.AsObject().Remove(dropped);
            var report = SwiftDeclarationRecordCompleteness.Check(image, ns, JsonSerializer.SerializeToUtf8Bytes(doctored));
            // Each Check reads the assembly afresh, so the member is found again by its kind and signature.
            if (report.Passed || !report.Uncovered.Any(m => m.Kind == droppedMember!.Kind && m.ToString() == droppedMember.ToString()))
                yield return $"negative control 1: removing {dropped} did not leave {droppedMember} uncovered";
            else
                Log.Information("    negative control 1 failed as designed: removing {Key} leaves {Member} uncovered", dropped, report.Uncovered[0]);
        }

        // Control 3: a key that spells a recorded method's parameters differently, where no unrecorded
        // member of that name, kind and parameter count is left for it to stand for, is unresolved.
        var target = index.Members.FirstOrDefault(m => m.Kind == "method" && m.Parameters.Length > 0
            && index.MembersOf(m.ParentPath).Count(o => o.Kind == "method" && o.Name == m.Name && o.Parameters.Length == m.Parameters.Length) == 1
            && members.Any(e => SwiftDeclarationRecordKeys.Parse(e.Key) is { } k && index.Resolve(k, module, swiftTypes).Contains(m)));
        if (target is null)
        {
            yield return "negative control 3: no recorded method with parameters has a unique name and parameter count";
        }
        else
        {
            var respelled = $"{target.ParentPath}.{target.Name}({string.Join(",", target.Parameters.Select(_ => "__SwiftDeclarationCompletenessGhost"))})";
            var withRespelled = JsonNode.Parse(record)!.AsObject();
            withRespelled["members"]!.AsObject()[respelled] = "func Ghost.respelled(_:)";
            var respelledReport = SwiftDeclarationRecordCompleteness.Check(image, ns, JsonSerializer.SerializeToUtf8Bytes(withRespelled));
            if (!respelledReport.UnresolvedKeys.Any(k => k.StartsWith(respelled, StringComparison.Ordinal)))
                yield return $"negative control 3: the key {respelled}, beside the recorded {target}, was not reported unresolved";
            else
                Log.Information("    negative control 3 failed as designed: {Key} is unresolved beside the recorded {Member}", respelled, target);
        }

        // Control 2: a key that names nothing the assembly declares.
        const string ghost = "__SwiftDeclarationCompletenessGhost.Missing(int)";
        var withGhost = JsonNode.Parse(record)!.AsObject();
        withGhost["members"]!.AsObject()[ghost] = "func Ghost.missing(_:)";
        var ghostReport = SwiftDeclarationRecordCompleteness.Check(image, ns, JsonSerializer.SerializeToUtf8Bytes(withGhost));
        if (!ghostReport.UnresolvedKeys.Any(k => k.StartsWith(ghost, StringComparison.Ordinal)))
            yield return $"negative control 2: the key {ghost} was not reported unresolved";
        else
            Log.Information("    negative control 2 failed as designed: {Key} is unresolved", ghost);
    }
}
