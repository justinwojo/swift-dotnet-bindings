// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SwiftBindings.TransitionGate;

/// <summary>
/// Renders gate results as the machine-readable report (<c>swift-transition-report.json</c>) and
/// the human summary. The JSON is written with <see cref="Utf8JsonWriter"/>, so it needs no
/// reflection and the schema is exactly what this file spells.
///
/// <para>Report schema 1: <c>{ schema, tool, passed, totals: {R1, R2, R3, R4, R5, inspectionErrors},
/// assemblies: [ { path, assemblyName, mvid, passed, planSource: {kind, location} | null, counts:
/// {…}, inventory: {…}, violations: [ { rule, site, member, signature, reason, entryPoint?,
/// library?, ilOffset?, classes[] } ], inspectionErrors: [ { member, message } ] } ] }</c>.</para>
///
/// <para>Site inventory schema 1 (<c>swift-transition-inventory.json</c>, <see cref="ToInventoryJson"/>):
/// <c>{ schema, tool, assemblies: [ { path, assemblyName, mvid, sites: [ { kind, member, token,
/// convention, signature, entryPoint?, library?, ilOffset?, rules[] } ] } ] }</c>, one row per
/// transition site, safe or not; <c>token</c> is the member's metadata token in hex.</para>
///
/// <para>Rows are sorted, so two runs over the same assemblies produce identical files.</para>
/// </summary>
public static class SwiftTransitionReport
{
    public const int SchemaVersion = 1;

    public static IReadOnlyDictionary<TransitionRule, int> CountByRule(IEnumerable<TransitionViolation> violations)
    {
        var counts = Enum.GetValues<TransitionRule>().ToDictionary(r => r, _ => 0);
        foreach (var violation in violations)
            counts[violation.Rule]++;
        return counts;
    }

    public static string ToJson(IReadOnlyList<AssemblyTransitionReport> reports)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", SchemaVersion);
            writer.WriteString("tool", "swift-transition-gate");
            writer.WriteBoolean("passed", reports.All(r => r.Passed));
            writer.WriteStartObject("totals");
            foreach (var (rule, count) in CountByRule(reports.SelectMany(r => r.Violations)))
                writer.WriteNumber(rule.ToString(), count);
            writer.WriteNumber("inspectionErrors", reports.Sum(r => r.InspectionErrors.Length));
            writer.WriteEndObject();

            writer.WriteStartArray("assemblies");
            foreach (var report in reports)
                WriteAssembly(writer, report);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteAssembly(Utf8JsonWriter writer, AssemblyTransitionReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("path", report.Path);
        writer.WriteString("assemblyName", report.AssemblyName);
        writer.WriteString("mvid", report.Mvid.ToString("D"));
        writer.WriteBoolean("passed", report.Passed);
        if (report.PlanSource is { } plan)
        {
            writer.WriteStartObject("planSource");
            writer.WriteString("kind", plan.Kind.ToString());
            writer.WriteString("location", plan.Location);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("planSource");
        }
        writer.WriteStartObject("counts");
        foreach (var (rule, count) in CountByRule(report.Violations))
            writer.WriteNumber(rule.ToString(), count);
        writer.WriteEndObject();

        writer.WriteStartObject("inventory");
        foreach (var (name, value) in InventoryFields(report.Inventory))
            writer.WriteNumber(name, value);
        writer.WriteEndObject();

        writer.WriteStartArray("violations");
        foreach (var v in report.Violations)
        {
            writer.WriteStartObject();
            writer.WriteString("rule", v.Rule.ToString());
            writer.WriteString("site", v.Site.ToString());
            writer.WriteString("member", v.Member);
            writer.WriteString("signature", v.Signature);
            writer.WriteString("reason", v.Reason);
            if (v.EntryPoint is not null)
                writer.WriteString("entryPoint", v.EntryPoint);
            if (v.Library is not null)
                writer.WriteString("library", v.Library);
            if (v.IlOffset is { } offset)
                writer.WriteNumber("ilOffset", offset);
            writer.WriteStartArray("classes");
            foreach (var c in v.Classes.IsDefault ? Array.Empty<string>() : (IEnumerable<string>)v.Classes)
                writer.WriteStringValue(c);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("inspectionErrors");
        foreach (var e in report.InspectionErrors)
        {
            writer.WriteStartObject();
            writer.WriteString("member", e.Member);
            writer.WriteString("message", e.Message);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>The per-site inventory: every transition site of every assembly, with the rules it violates.</summary>
    public static string ToInventoryJson(IReadOnlyList<AssemblyTransitionReport> reports)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", SchemaVersion);
            writer.WriteString("tool", "swift-transition-gate");
            writer.WriteStartArray("assemblies");
            foreach (var report in reports)
            {
                writer.WriteStartObject();
                writer.WriteString("path", report.Path);
                writer.WriteString("assemblyName", report.AssemblyName);
                writer.WriteString("mvid", report.Mvid.ToString("D"));
                writer.WriteStartArray("sites");
                foreach (var site in report.Sites)
                {
                    writer.WriteStartObject();
                    writer.WriteString("kind", site.Kind.ToString());
                    writer.WriteString("member", site.Member);
                    writer.WriteString("token", $"0x{site.MemberToken:X8}");
                    writer.WriteString("convention", site.Convention.ToString());
                    writer.WriteString("signature", site.Signature);
                    if (site.EntryPoint is not null)
                        writer.WriteString("entryPoint", site.EntryPoint);
                    if (site.Library is not null)
                        writer.WriteString("library", site.Library);
                    if (site.IlOffset is { } offset)
                        writer.WriteNumber("ilOffset", offset);
                    if (site.Target is not null)
                        writer.WriteString("target", site.Target);
                    writer.WriteStartArray("rules");
                    foreach (var rule in site.Rules)
                        writer.WriteStringValue(rule.ToString());
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>The inventory as ordered (camelCase name, value) pairs.</summary>
    public static IEnumerable<(string Name, int Value)> InventoryFields(TransitionInventory i)
    {
        yield return ("swiftPInvokes", i.SwiftPInvokes);
        yield return ("swiftPInvokesPrimitiveOnly", i.SwiftPInvokesPrimitiveOnly);
        yield return ("swiftPInvokesSpecialBearing", i.SwiftPInvokesSpecialBearing);
        yield return ("swiftPInvokesAggregateOnly", i.SwiftPInvokesAggregateOnly);
        yield return ("swiftPInvokesWithSwiftSelf", i.SwiftPInvokesWithSwiftSelf);
        yield return ("swiftPInvokesWithSwiftSelfGeneric", i.SwiftPInvokesWithSwiftSelfGeneric);
        yield return ("swiftSelfGenericLowered", i.SwiftSelfGenericLowered);
        yield return ("swiftSelfGenericByAddress", i.SwiftSelfGenericByAddress);
        yield return ("swiftSelfGenericPlacementUnknown", i.SwiftSelfGenericPlacementUnknown);
        yield return ("swiftPInvokesWithSwiftError", i.SwiftPInvokesWithSwiftError);
        yield return ("swiftPInvokesWithSwiftIndirectResult", i.SwiftPInvokesWithSwiftIndirectResult);
        yield return ("nonSwiftPInvokes", i.NonSwiftPInvokes);
        yield return ("nonSwiftPInvokesWithSwiftSpecial", i.NonSwiftPInvokesWithSwiftSpecial);
        yield return ("nonSwiftPInvokesWithSwiftErrorPointer", i.NonSwiftPInvokesWithSwiftErrorPointer);
        yield return ("bridgeTwins", i.BridgeTwins);
        yield return ("swiftUnmanagedCallersOnly", i.SwiftUnmanagedCallersOnly);
        yield return ("nonSwiftUnmanagedCallersOnly", i.NonSwiftUnmanagedCallersOnly);
        yield return ("swiftCalliSites", i.SwiftCalliSites);
        yield return ("swiftCallbackAddresses", i.SwiftCallbackAddresses);
        yield return ("nonSwiftUnmanagedCalliSites", i.NonSwiftUnmanagedCalliSites);
        yield return ("managedCalliSites", i.ManagedCalliSites);
        yield return ("methodBodies", i.MethodBodies);
        yield return ("instructions", i.Instructions);
    }

    /// <summary>The human summary: per-assembly counts and inventory, then every violation.</summary>
    public static string ToSummary(IReadOnlyList<AssemblyTransitionReport> reports)
    {
        var text = new StringBuilder();
        var totals = CountByRule(reports.SelectMany(r => r.Violations));
        var errors = reports.Sum(r => r.InspectionErrors.Length);
        text.AppendLine($"Swift transition gate: {(reports.All(r => r.Passed) ? "PASSED" : "FAILED")}");
        text.AppendLine("  totals: " + string.Join(", ", totals.Select(kv => $"{kv.Key}={kv.Value}")) + $", inspection errors={errors}");
        foreach (var report in reports)
        {
            text.AppendLine();
            text.AppendLine($"== {report.AssemblyName} ({report.Path})");
            text.AppendLine("  plan source: " + (report.PlanSource is { } plan ? $"{plan.Kind} {plan.Location}" : "none"));
            text.AppendLine("  " + string.Join(", ", CountByRule(report.Violations).Select(kv => $"{kv.Key}={kv.Value}")) +
                            $", inspection errors={report.InspectionErrors.Length}");
            text.AppendLine("  inventory: " + string.Join(", ", InventoryFields(report.Inventory).Select(f => $"{f.Name}={f.Value}")));
            foreach (var v in report.Violations)
            {
                var where = v.EntryPoint is null ? "" : $" -> {v.EntryPoint}";
                var offset = v.IlOffset is { } o ? $" @IL_{o:X4}" : "";
                text.AppendLine($"  {v.Rule} [{v.Site}] {v.Member}{offset}{where}");
                text.AppendLine($"      {v.Signature}");
                text.AppendLine($"      {v.Reason}");
            }
            foreach (var e in report.InspectionErrors)
                text.AppendLine($"  ERROR {e.Member}: {e.Message}");
        }
        return text.ToString();
    }
}
