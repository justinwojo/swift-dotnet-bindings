// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Checked model for the downstream qualification JSONL emitted by consumer test harnesses.
/// A receipt contains one <c>cell</c> record per attempted library followed by exactly one
/// <c>terminal</c> record whose counters and outcome agree with those cells.
/// </summary>
public sealed record DownstreamQualificationReceipt
{
    public const int CurrentSchemaVersion = 1;

    public sealed record Cell(
        string Platform,
        string Library,
        string Outcome,
        int TestsPassed,
        int TestsFailed);

    public sealed record Terminal(
        string Platform,
        int Total,
        int Pass,
        int Fail,
        int Crash,
        int Skip,
        string Outcome);

    public IReadOnlyList<Cell> Cells { get; }
    public Terminal Result { get; }
    public bool Passed => Result.Outcome == "pass";

    private DownstreamQualificationReceipt(IReadOnlyList<Cell> cells, Terminal result)
    {
        Cells = cells;
        Result = result;
    }

    /// <summary>
    /// Parses and validates a complete receipt. Malformed JSON, missing or extra fields,
    /// duplicate library cells, records after the terminal, inconsistent counters, an empty
    /// receipt, and missing/duplicate terminal records all fail closed.
    /// </summary>
    public static DownstreamQualificationReceipt Parse(string jsonl)
    {
        if (string.IsNullOrWhiteSpace(jsonl))
            throw new InvalidDataException("Qualification receipt is empty.");

        var cells = new List<Cell>();
        var libraries = new HashSet<string>(StringComparer.Ordinal);
        Terminal? terminal = null;
        string? platform = null;
        int lineNumber = 0;

        using var reader = new StringReader(jsonl);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                throw Error(lineNumber, "blank records are not permitted");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw Error(lineNumber, $"malformed JSON: {exception.Message}", exception);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw Error(lineNumber, "record must be a JSON object");

                var properties = ReadUniqueProperties(document.RootElement, lineNumber);
                var kind = RequiredString(properties, "kind", lineNumber);
                var schemaVersion = RequiredNonNegativeInt(properties, "schema_version", lineNumber);
                if (schemaVersion != CurrentSchemaVersion)
                    throw Error(lineNumber, $"unsupported schema_version {schemaVersion}");

                switch (kind)
                {
                    case "cell":
                        if (terminal is not null)
                            throw Error(lineNumber, "cell record appears after terminal record");
                        RequireExactProperties(properties, CellProperties, lineNumber);
                        var cell = ParseCell(properties, lineNumber);
                        platform = RequireSamePlatform(platform, cell.Platform, lineNumber);
                        if (!libraries.Add(cell.Library))
                            throw Error(lineNumber, $"duplicate library cell '{cell.Library}'");
                        cells.Add(cell);
                        break;

                    case "terminal":
                        RequireExactProperties(properties, TerminalProperties, lineNumber);
                        if (terminal is not null)
                            throw Error(lineNumber, "duplicate terminal record");
                        terminal = ParseTerminal(properties, lineNumber);
                        platform = RequireSamePlatform(platform, terminal.Platform, lineNumber);
                        break;

                    default:
                        throw Error(lineNumber, $"unknown record kind '{kind}'");
                }
            }
        }

        if (terminal is null)
            throw new InvalidDataException("Qualification receipt is incomplete: terminal record is missing.");
        if (cells.Count == 0)
            throw new InvalidDataException("Qualification receipt is incomplete: at least one cell is required.");

        ValidateTerminal(cells, terminal);
        return new DownstreamQualificationReceipt(cells.AsReadOnly(), terminal);
    }

    public static DownstreamQualificationReceipt Load(string path)
        => Parse(File.ReadAllText(path));

    private static Cell ParseCell(IReadOnlyDictionary<string, JsonElement> properties, int lineNumber)
    {
        var outcome = RequiredString(properties, "outcome", lineNumber);
        if (outcome is not ("pass" or "fail" or "crash" or "skip"))
            throw Error(lineNumber, $"invalid cell outcome '{outcome}'");

        var testsPassed = RequiredNonNegativeInt(properties, "tests_passed", lineNumber);
        var testsFailed = RequiredNonNegativeInt(properties, "tests_failed", lineNumber);
        if (outcome == "pass" && testsFailed != 0)
            throw Error(lineNumber, "a passing cell must have tests_failed equal to zero");

        return new Cell(
            RequiredString(properties, "platform", lineNumber),
            RequiredString(properties, "library", lineNumber),
            outcome,
            testsPassed,
            testsFailed);
    }

    private static Terminal ParseTerminal(IReadOnlyDictionary<string, JsonElement> properties, int lineNumber)
    {
        var outcome = RequiredString(properties, "outcome", lineNumber);
        if (outcome is not ("pass" or "fail"))
            throw Error(lineNumber, $"invalid terminal outcome '{outcome}'");

        return new Terminal(
            RequiredString(properties, "platform", lineNumber),
            RequiredNonNegativeInt(properties, "total", lineNumber),
            RequiredNonNegativeInt(properties, "pass", lineNumber),
            RequiredNonNegativeInt(properties, "fail", lineNumber),
            RequiredNonNegativeInt(properties, "crash", lineNumber),
            RequiredNonNegativeInt(properties, "skip", lineNumber),
            outcome);
    }

    private static void ValidateTerminal(IReadOnlyList<Cell> cells, Terminal terminal)
    {
        var expectedPass = cells.Count(cell => cell.Outcome == "pass");
        var expectedFail = cells.Count(cell => cell.Outcome == "fail");
        var expectedCrash = cells.Count(cell => cell.Outcome == "crash");
        var expectedSkip = cells.Count(cell => cell.Outcome == "skip");

        if (terminal.Total != cells.Count || terminal.Pass != expectedPass ||
            terminal.Fail != expectedFail || terminal.Crash != expectedCrash ||
            terminal.Skip != expectedSkip)
        {
            throw new InvalidDataException(
                "Qualification terminal counters do not match cell records: " +
                $"expected total={cells.Count} pass={expectedPass} fail={expectedFail} " +
                $"crash={expectedCrash} skip={expectedSkip}; received total={terminal.Total} " +
                $"pass={terminal.Pass} fail={terminal.Fail} crash={terminal.Crash} skip={terminal.Skip}.");
        }

        var expectedOutcome = expectedPass == cells.Count ? "pass" : "fail";
        if (!string.Equals(terminal.Outcome, expectedOutcome, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Qualification terminal outcome must be '{expectedOutcome}' for its cell results, " +
                $"but was '{terminal.Outcome}'.");
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadUniqueProperties(JsonElement element, int lineNumber)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
                throw Error(lineNumber, $"duplicate property '{property.Name}'");
        }
        return properties;
    }

    private static string RequiredString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        int lineNumber)
    {
        if (!properties.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw Error(lineNumber, $"'{name}' must be a string");
        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result))
            throw Error(lineNumber, $"'{name}' must not be empty");
        return result;
    }

    private static int RequiredNonNegativeInt(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        int lineNumber)
    {
        if (!properties.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result) || result < 0)
        {
            throw Error(lineNumber, $"'{name}' must be a nonnegative 32-bit integer");
        }
        return result;
    }

    private static string RequireSamePlatform(string? expected, string actual, int lineNumber)
    {
        if (expected is not null && !string.Equals(expected, actual, StringComparison.Ordinal))
            throw Error(lineNumber, $"platform '{actual}' does not match '{expected}'");
        return actual;
    }

    private static void RequireExactProperties(
        IReadOnlyDictionary<string, JsonElement> properties,
        IReadOnlySet<string> expected,
        int lineNumber)
    {
        var missing = expected.Where(name => !properties.ContainsKey(name)).OrderBy(name => name).ToArray();
        var unknown = properties.Keys.Where(name => !expected.Contains(name)).OrderBy(name => name).ToArray();
        if (missing.Length != 0 || unknown.Length != 0)
        {
            throw Error(lineNumber,
                $"record properties do not match the contract (missing: {string.Join(", ", missing)}; " +
                $"unknown: {string.Join(", ", unknown)})");
        }
    }

    private static InvalidDataException Error(int lineNumber, string message, Exception? inner = null)
        => new($"Qualification receipt line {lineNumber}: {message}.", inner);

    private static readonly IReadOnlySet<string> CellProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "kind", "schema_version", "platform", "library", "outcome", "tests_passed", "tests_failed"
    };

    private static readonly IReadOnlySet<string> TerminalProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "kind", "schema_version", "platform", "total", "pass", "fail", "crash", "skip", "outcome"
    };
}
