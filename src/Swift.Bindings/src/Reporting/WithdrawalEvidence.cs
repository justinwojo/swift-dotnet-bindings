// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using BindingsGeneration.Diagnostics;
using Newtonsoft.Json;

namespace BindingsGeneration;

/// <summary>Immutable publication snapshot of the controller's settled denylist, never ingestion or emitter faults.</summary>
public sealed class WithdrawalEvidence
{
    [JsonProperty("schemaVersion")]
    public int SchemaVersion => 1;

    [JsonProperty("identityFormat")]
    public string IdentityFormat => CanonicalIdentityCodec.IdentityFormat;

    [JsonProperty("loopStatus")]
    public string LoopStatus { get; }

    [JsonProperty("configuredPlanes")]
    public ImmutableArray<string> ConfiguredPlanes { get; }

    [JsonProperty("unitIds")]
    public ImmutableArray<string> UnitIds { get; }

    [JsonProperty("notRunReason", NullValueHandling = NullValueHandling.Ignore)]
    public string? NotRunReason { get; }

    [JsonIgnore]
    public ImmutableArray<string> Descriptions { get; }

    private WithdrawalEvidence(string status, ImmutableArray<string> planes,
        ImmutableArray<RecoveryUnitId> denylist, string? notRunReason = null)
    {
        LoopStatus = status;
        ConfiguredPlanes = planes;
        NotRunReason = notRunReason;
        UnitIds = denylist.Select(unit => unit.Canonical).Order(StringComparer.Ordinal).ToImmutableArray();
        if (UnitIds.Distinct(StringComparer.Ordinal).Count() != UnitIds.Length ||
            UnitIds.Any(id => !RecoveryUnitId.TryParse(id, out var parsed) || parsed.Canonical != id))
            throw new ArgumentException("The controller denylist must contain unique canonical recovery units.", nameof(denylist));
        // Descriptions deliberately retain multiplicity: distinct overloads can share a display name.
        Descriptions = denylist.Select(unit => unit.Describe()).Order(StringComparer.Ordinal).ToImmutableArray();
    }

    public static WithdrawalEvidence NotRun() => new("not-run", ImmutableArray<string>.Empty,
        ImmutableArray<RecoveryUnitId>.Empty, "no-verification-planes");

    public static WithdrawalEvidence FromController(WrapperRecoveryResult result, bool swiftConfigured, bool csharpConfigured)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!swiftConfigured && !csharpConfigured)
            throw new ArgumentException("A controller result requires at least one configured verification plane.");
        if (result.Denylist.IsDefault || (result.Converged && result.Cause != WrapperRecoveryFailureCause.None))
            throw new ArgumentException("Inconsistent controller result.", nameof(result));
        var planes = ImmutableArray.CreateBuilder<string>();
        if (csharpConfigured) planes.Add("csharp");
        if (swiftConfigured) planes.Add("swift");
        return new WithdrawalEvidence(result.Converged ? "converged" : "failed", planes.ToImmutable(), result.Denylist);
    }
}
