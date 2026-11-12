// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Assertions a producer of synthesized Swift wrapper functions is held to: every function it
/// writes with <c>@_silgen_name</c> is described in the emission context under its own synthesizer,
/// and the description is the function the Swift text declares.
/// </summary>
internal static class SyntheticRegistration
{
    private static readonly Regex SilgenName = new(@"@_silgen_name\(""(?<symbol>[^""]+)""\)", RegexOptions.Compiled);

    /// <summary>
    /// The functions <paramref name="synthesizer"/> registered, after asserting there is at least one,
    /// that the Swift text declares each, and that no registration was refused or conflicted.
    /// </summary>
    public static IReadOnlyList<SyntheticSwiftFunction> AssertRegistered(
        ModuleEmissionContext context, string swift, SwiftWrapperSynthesizer synthesizer)
    {
        Assert.Empty(context.SyntheticSwiftFunctionConflicts);
        var written = SilgenName.Matches(swift).Select(m => m.Groups["symbol"].Value).ToHashSet();
        var registered = context.SyntheticSwiftFunctions.Where(f => f.Synthesizer == synthesizer).ToList();
        Assert.True(registered.Count > 0, $"{synthesizer} registered no function; the Swift text declares: {string.Join(", ", written)}");
        Assert.All(registered, f => Assert.Contains(f.Symbol, written));
        return registered;
    }

    /// <summary>
    /// Asserts every <c>@_silgen_name</c> function the Swift text defines with a body whose symbol
    /// starts with <paramref name="symbolPrefix"/> is registered. A producer that writes one and
    /// forgets to describe it fails here.
    /// </summary>
    public static void AssertEveryWrittenIsRegistered(ModuleEmissionContext context, string swift, string symbolPrefix)
    {
        var written = SilgenName.Matches(swift)
            .Select(m => m.Groups["symbol"].Value)
            .Where(s => s.StartsWith(symbolPrefix, System.StringComparison.Ordinal))
            .Distinct()
            .ToList();
        Assert.NotEmpty(written);
        foreach (var symbol in written)
            Assert.True(context.TryGetSyntheticSwiftFunction(symbol, out _), $"'{symbol}' is written but not registered");
    }

    /// <summary>
    /// Builds the transition ledger of <paramref name="csharp"/> from the context's records and
    /// asserts every Swift-convention P/Invoke that binds a function <paramref name="synthesizer"/>
    /// registered carries that function's facts. Returns those entries.
    /// </summary>
    public static IReadOnlyList<SwiftTransitionLedgerEntry> AssertBoundWithFacts(
        ModuleEmissionContext context, string csharp, SwiftWrapperSynthesizer synthesizer)
    {
        var ledger = SwiftTransitionLedger.Build("TestModule", "partial class __Fragment {\n" + csharp + "\n}", context.SwiftTransitionRecords);
        var symbols = context.SyntheticSwiftFunctions.Where(f => f.Synthesizer == synthesizer).Select(f => f.Symbol).ToHashSet();
        var bound = ledger.Entries.Where(e => symbols.Contains(e.EntryPoint)).ToList();
        Assert.NotEmpty(bound);
        Assert.All(bound, e =>
        {
            Assert.NotEqual(SwiftTransitionLedgerStatus.Unplanned, e.Status);
            Assert.Equal(SwiftCallFactsSource.Synthesized, e.FactsSource);
            Assert.Equal(synthesizer, e.Synthesizer);
        });
        return bound;
    }
}
