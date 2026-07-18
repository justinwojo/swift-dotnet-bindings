// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;

using BindingsGeneration;

namespace BindingsGeneration.Diagnostics;

/// <summary>
/// Attributes a failed wrapper compile's diagnostics to the recovery units that caused them,
/// applying cascade hygiene and classifying global failures instead of blaming a declaration.
/// </summary>
/// <remarks>
/// <para>
/// The engine is language-neutral: it consumes structured diagnostics and an ordered list of
/// provenance steps, so the same code attributes a Swift wrapper compile today and an in-process C#
/// probe later. The Swift specifics live in the steps and in the parser, not here.
/// </para>
/// <para>
/// Three rules make the culprit set trustworthy. Only a group's <em>primary</em> is attributed —
/// notes ride along as evidence, never as independent culprits. Culprits are <em>distinct by
/// unit</em>, so a cascade of many errors inside one broken member collapses to a single denylist
/// increment. And a global failure — a missing input module, a toolchain fault — is
/// <em>classified</em> by cause rather than attributed to whatever source line it happened to point
/// at, because withdrawing a declaration would not fix an input the author never supplied.
/// </para>
/// </remarks>
public sealed class DiagnosticAttributor
{
    private static readonly Regex MissingModule = new(
        @"no such module '([^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyList<IProvenanceStep> _steps;

    /// <summary>Builds an attributor over the given provenance steps, tried in priority order.</summary>
    public DiagnosticAttributor(IEnumerable<IProvenanceStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps.ToList();
    }

    /// <summary>Attributes a parsed diagnostic stream to culprit units.</summary>
    public AttributionResult Attribute(IReadOnlyList<DiagnosticGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var decisions = ImmutableArray.CreateBuilder<AttributedDiagnostic>(groups.Count);
        var culprits = ImmutableArray.CreateBuilder<RecoveryUnitId>();
        var seen = new HashSet<RecoveryUnitId>();

        foreach (var group in groups)
        {
            var primary = group.Primary;

            if (TryClassify(primary, out var owner, out var detail))
            {
                decisions.Add(new AttributedDiagnostic
                {
                    Diagnostic = group,
                    Kind = AttributionKind.Classification,
                    Owner = owner,
                    ClassificationDetail = detail,
                    Source = ProvenanceSource.None,
                });
                continue;
            }

            var hit = Resolve(primary);
            if (hit is { } h)
            {
                decisions.Add(new AttributedDiagnostic
                {
                    Diagnostic = group,
                    Kind = AttributionKind.Unit,
                    Artifact = h.Artifact,
                    Unit = h.Unit,
                    Source = h.Source,
                });

                // Batch: distinct-by-unit, and only an error can name a culprit — a warning does not
                // fail the compile, so it never earns a denylist increment.
                if (group.IsError && seen.Add(h.Unit))
                    culprits.Add(h.Unit);
            }
            else
            {
                decisions.Add(new AttributedDiagnostic
                {
                    Diagnostic = group,
                    Kind = AttributionKind.Unattributed,
                    Source = ProvenanceSource.None,
                });
            }
        }

        return new AttributionResult
        {
            Diagnostics = decisions.ToImmutable(),
            Culprits = culprits.ToImmutable(),
            Fingerprint = DiagnosticFingerprint.Compute(groups),
        };
    }

    private ProvenanceHit? Resolve(CompilerDiagnostic primary)
    {
        foreach (var step in _steps)
        {
            if (step.TryResolve(primary, out var hit))
                return hit;
        }

        return null;
    }

    // Priority 5: a diagnostic that names a failure of the inputs rather than of a declaration is
    // classified, not attributed. A missing module means the author did not supply an xcframework
    // the wrapper needs — the existing dependency guidance already tells them how to fix it — so it
    // is owned by InputConfiguration, never charged to the import line's nearest member.
    private static bool TryClassify(CompilerDiagnostic diagnostic, out CauseOwner owner, out string? detail)
    {
        var match = MissingModule.Match(diagnostic.Message);
        if (match.Success)
        {
            owner = CauseOwner.InputConfiguration;
            detail = match.Groups[1].Value;
            return true;
        }

        owner = CauseOwner.Unknown;
        detail = null;
        return false;
    }
}
