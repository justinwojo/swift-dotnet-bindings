// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class SurfaceAccountingWriter
{
    public static void Write(SurfaceAccountingResult result, string outputDirectory, string schemaPath)
    {
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            throw new IOException($"Surface-accounting output already exists: {outputDirectory}");
        if (!File.Exists(schemaPath))
            throw new FileNotFoundException("Surface-accounting schema file is missing.", schemaPath);

        Directory.CreateDirectory(outputDirectory);
        File.Copy(schemaPath, Path.Combine(outputDirectory, "schema.json"));
        WriteCapture(result, result.Old, outputDirectory);
        WriteCapture(result, result.Tip, outputDirectory);

        var comparison = result.Comparison;
        var comparisonDirectory = Path.Combine(outputDirectory, "comparisons", comparison.Summary.ComparisonId);
        Directory.CreateDirectory(comparisonDirectory);
        WriteJson(Path.Combine(comparisonDirectory, "comparison.json"), comparison.Summary);
        WriteJsonLines(Path.Combine(comparisonDirectory, "changes.jsonl"), comparison.Changes);
        WriteJsonLines(Path.Combine(comparisonDirectory, "crosswalk.jsonl"), comparison.Crosswalks);
        File.WriteAllText(
            Path.Combine(comparisonDirectory, "summary.md"),
            RenderSummary(result),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ValidateArtifacts(outputDirectory, schemaPath);
    }

    public static string RenderSummary(SurfaceAccountingResult result)
    {
        var summary = result.Comparison.Summary;
        var builder = new StringBuilder();
        builder.AppendLine("# Surface accounting comparison");
        builder.AppendLine();
        builder.AppendLine($"- Schema: `{summary.Schema}`");
        builder.AppendLine($"- Comparison: `{summary.ComparisonId}`");
        builder.AppendLine($"- Old capture: `{summary.OldCaptureId}`");
        builder.AppendLine($"- Tip capture: `{summary.TipCaptureId}`");
        builder.AppendLine($"- Comparable targets: {summary.ComparableTargets}/{summary.RequiredTargets}");
        builder.AppendLine($"- Complete: {summary.Complete.ToString().ToLowerInvariant()}");
        builder.AppendLine();
        builder.AppendLine("## Named public changes");
        builder.AppendLine();
        builder.AppendLine($"Additions: {summary.PublicAdditions}; removals: {summary.PublicRemovals}; " +
            $"shape changes: {summary.ShapeChanges}; accessor losses: {summary.AccessorLosses}.");
        RenderChanges(builder, result.Comparison.Changes.Where(c => c.Axis == "public"));
        builder.AppendLine();
        builder.AppendLine("## Dispatch changes");
        builder.AppendLine();
        builder.AppendLine($"Raw manifest symbol changes: {summary.ManifestSymbolChanges}; " +
            $"dispatch changes requiring attribution: {summary.DispatchRetargets}.");
        RenderChanges(builder, result.Comparison.Changes.Where(c => c.Axis == "dispatch"));
        builder.AppendLine();
        builder.AppendLine("## Withdrawal changes");
        builder.AppendLine();
        builder.AppendLine($"Additions: {summary.WithdrawalAdditions}; removals: {summary.WithdrawalRemovals}.");
        RenderChanges(builder, result.Comparison.Changes.Where(c => c.Axis == "withdrawal"));
        builder.AppendLine();
        builder.AppendLine("## Counter bridge");
        builder.AppendLine();
        builder.AppendLine("Report counters and syntax declarations use different units. They are listed per target in each " +
            "capture's `coverage.json`; additions never cancel removals, and no public total is forced to equal a report-counter delta.");
        builder.AppendLine();
        builder.AppendLine("## Coverage limits");
        builder.AppendLine();
        builder.AppendLine($"Input equivalent: {summary.InputEquivalent.ToString().ToLowerInvariant()}; " +
            $"toolchain equivalent: {summary.ToolchainEquivalent.ToString().ToLowerInvariant()}; " +
            $"unresolved changes: {summary.UnresolvedChanges}.");
        foreach (var coverage in result.Old.Coverage.Concat(result.Tip.Coverage)
                     .Where(c => c.Errors.Count > 0)
                     .OrderBy(c => c.CaptureId, StringComparer.Ordinal)
                     .ThenBy(c => c.TargetKey.TargetName, StringComparer.Ordinal))
        {
            builder.AppendLine($"- `{coverage.CaptureId}/{coverage.TargetKey.TargetName}`: {string.Join("; ", coverage.Errors)}");
        }
        return builder.ToString();
    }

    private static void WriteCapture(
        SurfaceAccountingResult result,
        SurfaceCaptureArtifacts capture,
        string outputDirectory)
    {
        var directory = Path.Combine(outputDirectory, "evidence", capture.Capture.CaptureId);
        Directory.CreateDirectory(directory);
        var corpus = new
        {
            schema = SurfaceAccountingSchema.Artifact,
            result.Request.CorpusId,
            result.Request.ManifestSha256,
            inputLockSha256 = result.Request.InputLockSha256,
            result.Request.ExpectedTargetCount,
            targets = result.Request.Targets.OrderBy(t => t.Key.TargetName, StringComparer.Ordinal),
            excludedTargets = Array.Empty<object>(),
        };
        WriteJson(Path.Combine(directory, "corpus.json"), corpus);
        File.Copy(result.Request.InputLockPath, Path.Combine(directory, "input-lock.json"));
        WriteJson(Path.Combine(directory, "capture.json"), capture.Capture);
        WriteJsonLines(Path.Combine(directory, "files.jsonl"), capture.Files);
        WriteJsonLines(Path.Combine(directory, "members.jsonl"), capture.Members);
        WriteJsonLines(Path.Combine(directory, "withdrawals.jsonl"), capture.Withdrawals);
        WriteJson(Path.Combine(directory, "coverage.json"), capture.Coverage);
    }

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(
            path,
            SurfaceCanonicalJson.Serialize(value) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static void WriteJsonLines<T>(string path, IEnumerable<T> values)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var value in values)
            writer.WriteLine(SurfaceCanonicalJson.Serialize(value, indented: false));
    }

    private static void RenderChanges(StringBuilder builder, IEnumerable<SurfaceChange> changes)
    {
        var any = false;
        foreach (var change in changes)
        {
            any = true;
            var keys = change.OldPublicKeys.Concat(change.TipPublicKeys).Distinct(StringComparer.Ordinal).ToList();
            var identity = keys.Count == 0
                ? change.OriginIds.FirstOrDefault() ?? change.ChangeId
                : string.Join(" → ", keys);
            builder.AppendLine($"- `{change.TargetKey.TargetName}` {change.Classification}: `{identity}` ({change.Certainty})");
        }
        if (!any) builder.AppendLine("No changes.");
    }

    private static void ValidateArtifacts(string outputDirectory, string schemaPath)
    {
        using var schema = SurfaceCanonicalJson.ParseStrictFile(schemaPath);
        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*.json", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(path) is not ("schema.json" or "input-lock.json")))
        {
            using var document = SurfaceCanonicalJson.ParseStrictFile(path);
            ValidateSchema(document.RootElement, schema.RootElement, schema.RootElement, path);
        }

        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*.jsonl", SearchOption.AllDirectories))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    throw new InvalidDataException($"{path}:{lineNumber}: blank JSONL record.");
                using var document = SurfaceCanonicalJson.ParseStrict(
                    Encoding.UTF8.GetBytes(line),
                    $"{path}:{lineNumber}");
                ValidateSchema(document.RootElement, schema.RootElement, schema.RootElement, $"{path}:{lineNumber}");
            }
        }
    }

    private static void ValidateSchema(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string location)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            ValidateSchema(instance, ResolveReference(rootSchema, reference.GetString()!, location), rootSchema, location);
            return;
        }
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var matches = oneOf.EnumerateArray().Count(option => SchemaMatches(instance, option, rootSchema, location));
            if (matches != 1)
                throw new InvalidDataException($"{location}: expected exactly one schema match, found {matches}.");
            return;
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(instance, type))
            throw new InvalidDataException($"{location}: JSON value does not match required type {type.GetRawText()}.");
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(instance, constant))
            throw new InvalidDataException($"{location}: JSON value does not match required constant {constant.GetRawText()}.");
        if (schema.TryGetProperty("enum", out var enumeration)
            && !enumeration.EnumerateArray().Any(value => JsonElement.DeepEquals(instance, value)))
            throw new InvalidDataException($"{location}: JSON value is outside the allowed enum.");

        if (instance.ValueKind == JsonValueKind.String)
        {
            var value = instance.GetString()!;
            if (schema.TryGetProperty("minLength", out var minLength) && value.Length < minLength.GetInt32())
                throw new InvalidDataException($"{location}: string is shorter than {minLength.GetInt32()}.");
            if (schema.TryGetProperty("pattern", out var pattern)
                && !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant))
                throw new InvalidDataException($"{location}: string does not match required pattern.");
            if (schema.TryGetProperty("format", out var format)
                && string.Equals(format.GetString(), "date-time", StringComparison.Ordinal)
                && !DateTimeOffset.TryParse(value, out _))
                throw new InvalidDataException($"{location}: string is not a date-time.");
        }
        if (instance.ValueKind == JsonValueKind.Number
            && schema.TryGetProperty("minimum", out var minimum)
            && instance.GetDecimal() < minimum.GetDecimal())
            throw new InvalidDataException($"{location}: number is below its minimum.");

        if (instance.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var minItems)
                && instance.GetArrayLength() < minItems.GetInt32())
                throw new InvalidDataException($"{location}: array has fewer than {minItems.GetInt32()} item(s).");
            if (schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in instance.EnumerateArray())
                    ValidateSchema(item, items, rootSchema, $"{location}/{index++}");
            }
        }

        if (instance.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var property in required.EnumerateArray().Select(value => value.GetString()!))
            {
                if (!instance.TryGetProperty(property, out _))
                    throw new InvalidDataException($"{location}: required property '{property}' is missing.");
            }
        }
        if (!schema.TryGetProperty("properties", out var properties)) return;
        foreach (var property in instance.EnumerateObject())
        {
            if (properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateSchema(property.Value, propertySchema, rootSchema, $"{location}/{property.Name}");
                continue;
            }
            if (schema.TryGetProperty("additionalProperties", out var additional)
                && additional.ValueKind == JsonValueKind.False)
                throw new InvalidDataException($"{location}: unexpected property '{property.Name}'.");
        }
    }

    private static bool SchemaMatches(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string location)
    {
        try
        {
            ValidateSchema(instance, schema, rootSchema, location);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static JsonElement ResolveReference(JsonElement rootSchema, string reference, string location)
    {
        if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal))
            throw new InvalidDataException($"{location}: unsupported schema reference '{reference}'.");
        var name = reference["#/$defs/".Length..];
        if (!rootSchema.GetProperty("$defs").TryGetProperty(name, out var resolved))
            throw new InvalidDataException($"{location}: unknown schema reference '{reference}'.");
        return resolved;
    }

    private static bool MatchesType(JsonElement instance, JsonElement type)
        => type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(candidate => MatchesType(instance, candidate))
            : type.GetString() switch
            {
                "object" => instance.ValueKind == JsonValueKind.Object,
                "array" => instance.ValueKind == JsonValueKind.Array,
                "string" => instance.ValueKind == JsonValueKind.String,
                "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
                "number" => instance.ValueKind == JsonValueKind.Number,
                "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => instance.ValueKind == JsonValueKind.Null,
                _ => false,
            };
}
