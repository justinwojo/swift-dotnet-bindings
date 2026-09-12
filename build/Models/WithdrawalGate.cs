// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Fail-closed consumer of the settled generator recovery ledger.</summary>
public static class WithdrawalGate
{
    public static bool IsPureObjCUmbrella(string? target, string? module) =>
        target == "ObjCUmbrella" && module == "ObjCUmbrella";

    public static Identity ParseIdentity(string canonical)
    {
        if (!BindingsGeneration.CanonicalIdentityCodec.TryParseUnit(canonical, out var unit))
            throw new InvalidDataException("Malformed withdrawal identity");
        return new Identity(unit!.Canonical, unit.Decl.Module, unit.Describe());
    }
    public sealed record Identity(string Canonical, string Module, string Description);
    public sealed record Evidence(string[] UnitIds, string ReportPath);

    // Supplied by the generator's source-linked canonical codec. Never approximate identities
    // with report descriptions, member names, or hashes.
    public static Evidence Read(string path, string module, IEnumerable<string> expectedPlanes,
        Func<string, Identity> parseIdentity, bool externalVerificationPassed = false)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            RejectDuplicateProperties(root);
            if (root.GetProperty("module").GetString() != module)
                throw new InvalidDataException("wrong module");
            var evidence = root.GetProperty("withdrawalEvidence");
            if (evidence.GetProperty("schemaVersion").GetInt32() != 1 ||
                evidence.GetProperty("identityFormat").GetString() != "RecoveryUnitId.Canonical/v1")
                throw new InvalidDataException("unsupported withdrawal schema or identity format");
            var status = evidence.GetProperty("loopStatus").GetString();
            var expected = expectedPlanes.ToArray();
            if (status != "converged" && !(status == "not-run" && expected.Length == 0 &&
                externalVerificationPassed && evidence.GetProperty("notRunReason").GetString() == "no-verification-planes"))
                throw new InvalidDataException("recovery loop did not converge and lacks supported external verification");
            var planes = Strings(evidence.GetProperty("configuredPlanes"));
            if (planes.Any(plane => plane is not ("swift" or "csharp")) ||
                (status == "converged" && planes.Length == 0) ||
                planes.Distinct(StringComparer.Ordinal).Count() != planes.Length ||
                !planes.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("configured verification planes differ from invocation");
            var ids = Strings(evidence.GetProperty("unitIds"));
            if (status == "not-run" && ids.Length != 0)
                throw new InvalidDataException("not-run evidence cannot contain recovered withdrawals");
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
                !ids.SequenceEqual(ids.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("withdrawal identities must be unique and sorted");
            var parsed = ids.Select(parseIdentity).ToArray();
            if (parsed.Where((id, i) => id.Canonical != ids[i] || id.Module != module).Any())
                throw new InvalidDataException("noncanonical or foreign-module withdrawal identity");
            var descriptions = Strings(root.GetProperty("withdrawnUnits"));
            if (!descriptions.Order(StringComparer.Ordinal).SequenceEqual(
                    parsed.Select(id => id.Description).Order(StringComparer.Ordinal)))
                throw new InvalidDataException("withdrawal description multiset disagrees with identities");
            return new Evidence(ids, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"Withdrawal evidence {module} at {path}: {ex.Message}", ex);
        }
    }

    static string[] Strings(JsonElement element) => element.EnumerateArray().Select(value =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()! : throw new InvalidDataException("null/empty/non-string evidence entry")).ToArray();

    public static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("duplicate JSON property: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    public static void RequireZero(Evidence evidence)
    {
        if (evidence.UnitIds.Length != 0)
            throw new InvalidDataException($"Healthy withdrawal gate {evidence.ReportPath}: " +
                string.Join(", ", evidence.UnitIds));
    }

    public static void RequirePureObjCManifest(string path, string module)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            RejectDuplicateProperties(root);
            const string reason = "Pure-ObjC binding: no Swift generation phase runs, so the manifest carries only the ObjC skip section.";
            if (root.GetProperty("SchemaVersion").GetInt32() != 3 ||
                root.GetProperty("Module").GetString() != module ||
                root.GetProperty("Status").GetString() != "Partial" ||
                root.GetProperty("PartialReason").GetString() != reason ||
                root.GetProperty("Generation").ValueKind != JsonValueKind.Null ||
                root.GetProperty("Emission").ValueKind != JsonValueKind.Null ||
                root.GetProperty("Wrapper").ValueKind != JsonValueKind.Null ||
                root.GetProperty("ObjC").ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("artifact manifest is not an explicit pure-ObjC result");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"Pure-ObjC evidence {module} at {path}: {ex.Message}", ex);
        }
    }

    public static bool Compare(WithdrawalPolicy? accepted, string profile, Evidence current, string? module = null)
    {
        if (accepted == null || accepted.SchemaVersion != 1 || accepted.Profile != profile ||
            accepted.Exceptions == null)
            throw new InvalidDataException($"Missing/unsupported exact withdrawal policy for {profile}");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exception in accepted.Exceptions)
        {
            if (exception == null || !ids.Add(exception.UnitId) ||
                new[] { exception.UnitId, exception.Reason, exception.Reproducer, exception.Diagnostic,
                    exception.Disposition, exception.Acceptance, exception.ReopenTrigger, exception.ToolchainScope }
                    .Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException($"Malformed withdrawal exception for {profile}");
            var identity = ParseIdentity(exception.UnitId);
            if (identity.Canonical != exception.UnitId || (module != null && identity.Module != module))
                throw new InvalidDataException($"Noncanonical or foreign-module exception for {profile}");
        }
        var additions = current.UnitIds.Where(id => !ids.Contains(id)).ToArray();
        if (additions.Length != 0)
            throw new InvalidDataException($"New withdrawals for {profile}: {string.Join(", ", additions)}");
        return current.UnitIds.Length < ids.Count;
    }
}

public sealed record WithdrawalPolicy(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("exceptions")] WithdrawalException[] Exceptions);
public sealed record WithdrawalException(
    [property: JsonPropertyName("unitId")] string UnitId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("reproducer")] string Reproducer,
    [property: JsonPropertyName("diagnostic")] string Diagnostic,
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("acceptance")] string Acceptance,
    [property: JsonPropertyName("reopenTrigger")] string ReopenTrigger,
    [property: JsonPropertyName("toolchainScope")] string ToolchainScope);
