// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// This file is link-compiled into projects that do not enable nullable reference types, so the
// annotations below are opted into locally rather than inherited from a csproj.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BindingsGeneration;

/// <summary>What the OS load-safety gate found in one wrapper slice.</summary>
internal sealed record OsLoadVerdict(int OsResidentLoadCount, int WeakOsResidentLoadCount, IReadOnlyList<string> Failures);

/// <summary>
/// The pure verdict behind the OS load-safety gate (load-safety property P1: a missing OS symbol can
/// never stop a wrapper loading). Every dylib a wrapper slice loads from under <c>/System/</c> must be
/// loaded with <c>LC_LOAD_WEAK_DYLIB</c>, which also makes every import from it weak; and the slice
/// must load at least one, so a read that found no load commands at all cannot pass. BCL-only plus
/// the shared <see cref="OsFrameworkWeakLinking"/> parser, so the unit tests link-compile it.
/// </summary>
internal static class OsLoadSafetyAudit
{
    public static OsLoadVerdict EvaluateLoads(string leg, string binary, IReadOnlyList<OsFrameworkWeakLinking.LoadedDylib> loads)
    {
        var failures = new List<string>();
        var os = loads.Where(l => OsFrameworkWeakLinking.IsOsResident(l.InstallName)).ToList();
        var weak = os.Count(l => l.Kind == OsFrameworkWeakLinking.LoadKind.Weak);
        if (os.Count == 0)
            failures.Add($"{leg}: {binary} loads no OS-resident dylib at all; the load commands were not read, " +
                         "or the wrapper no longer links the OS frameworks this gate exists to check.");
        foreach (var strong in OsFrameworkWeakLinking.StrongOsResidentLoads(loads))
            failures.Add($"{leg}: {binary} loads {strong} strongly; every /System/ dylib a wrapper loads must be weak-linked.");
        return new OsLoadVerdict(os.Count, weak, failures);
    }
}
