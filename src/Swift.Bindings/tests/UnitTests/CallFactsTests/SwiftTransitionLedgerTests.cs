// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The transition coverage ledger: one entry per forward Swift-convention P/Invoke the emitted C#
/// declares, named as the IL names it, with the facts its producer recorded or why there are none.
/// </summary>
public class SwiftTransitionLedgerTests
{
    private const string Lib = "TestModuleSwiftBindings";

    private static SwiftCallFacts Facts(string symbol, bool complete = true, SwiftWrapperSynthesizer? synthesizer = SwiftWrapperSynthesizer.OptionalBuffer) => new()
    {
        Target = new SwiftCallTarget
        {
            Kind = synthesizer is null ? SwiftCallTargetKind.FreeFunction : SwiftCallTargetKind.SyntheticWrapper,
            Symbol = symbol,
            Synthesizer = synthesizer,
        },
        Self = SwiftSelfFacts.None,
        Parameters = ImmutableArray<SwiftParameterFacts>.Empty,
        Result = SwiftResultFacts.Void,
        Effects = new SwiftCallEffects(),
        Unknowns = complete ? ImmutableArray<string>.Empty : ImmutableArray.Create("parameter 0: type 'X' is not known"),
    };

    private static SwiftTransitionRecord Record(string method, string entryPoint, SwiftCallFacts? facts, string library = Lib) => new()
    {
        MethodName = method,
        EntryPoint = entryPoint,
        Library = library,
        Facts = facts,
        Source = facts?.Target.Kind == SwiftCallTargetKind.SyntheticWrapper ? SwiftCallFactsSource.Synthesized : SwiftCallFactsSource.Declared,
        Reason = facts is null ? "the producer had nothing to describe" : null,
    };

    private const string Source = """
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        namespace Acme.Widgets
        {
            public partial class Box<T>
            {
                internal static partial class PInvokes
                {
                    [LibraryImport("TestModuleSwiftBindings", EntryPoint = "SBSW_a")]
                    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                    internal static partial void A(nint x);

                    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvSwift) })]
                    [DllImport("TestModuleSwiftBindings", EntryPoint = "SBSW_b")]
                    internal static extern void B(nint x);

                    [LibraryImport("TestModuleSwiftBindings", EntryPoint = "SBSW_c")]
                    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
                    internal static partial void C(nint x);

                    [LibraryImport("TestModuleSwiftBindings")]
                    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                    internal static partial void NamedByItself();
                }
            }
        }
        """;

    [Fact]
    public void Build_ListsEverySwiftConventionImportAsTheILNamesIt()
    {
        var ledger = SwiftTransitionLedger.Build("TestModule", Source, []);

        Assert.Equal(
            new[]
            {
                "Acme.Widgets.Box`1+PInvokes::A SBSW_a",
                "Acme.Widgets.Box`1+PInvokes::B SBSW_b",
                "Acme.Widgets.Box`1+PInvokes::NamedByItself NamedByItself",
            },
            ledger.Entries.Select(e => e.Key));
        Assert.Empty(ledger.Issues);
    }

    [Fact]
    public void Build_NamesAnEscapedIdentifierWithoutItsEscapeAsMetadataDoes()
    {
        const string source = """
            namespace @event.Bindings
            {
                class @class
                {
                    [LibraryImport("L", EntryPoint = "S")]
                    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                    static partial void @fixed();
                }
            }
            """;

        var ledger = SwiftTransitionLedger.Build("TestModule", source, []);

        Assert.Equal("event.Bindings.class::fixed", Assert.Single(ledger.Entries).Member);
    }

    [Fact]
    public void Build_ADeclarationNoProducerRecordedIsUnplannedWithTheReason()
    {
        var ledger = SwiftTransitionLedger.Build("TestModule", Source,
        [
            Record("A", "SBSW_a", Facts("SBSW_a")),
            Record("NamedByItself", "NamedByItself", null),
        ]);

        var a = ledger.Entries.Single(e => e.EntryPoint == "SBSW_a");
        Assert.Equal(SwiftTransitionLedgerStatus.Complete, a.Status);
        Assert.Equal(SwiftCallFactsSource.Synthesized, a.FactsSource);
        Assert.Equal(SwiftWrapperSynthesizer.OptionalBuffer, a.Synthesizer);

        var seededMissing = ledger.Entries.Single(e => e.EntryPoint == "SBSW_b");
        Assert.Equal(SwiftTransitionLedgerStatus.Unplanned, seededMissing.Status);
        Assert.Null(seededMissing.FactsId);
        Assert.Equal("no producer recorded this declaration", Assert.Single(seededMissing.Reasons));

        var noFacts = ledger.Entries.Single(e => e.EntryPoint == "NamedByItself");
        Assert.Equal(SwiftTransitionLedgerStatus.Unplanned, noFacts.Status);
        Assert.Equal("the producer had nothing to describe", Assert.Single(noFacts.Reasons));

        Assert.Equal(1, ledger.Count(SwiftTransitionLedgerStatus.Complete));
        Assert.Equal(2, ledger.Count(SwiftTransitionLedgerStatus.Unplanned));
    }

    [Fact]
    public void Build_ARecordForADeclarationThatWasNotEmittedIsNotListed()
    {
        var ledger = SwiftTransitionLedger.Build("TestModule", Source,
        [
            Record("Dropped", "SBSW_dropped", Facts("SBSW_dropped")),
            Record("A", "SBSW_a", Facts("SBSW_a"), library: "SomeOtherLibrary"),
        ]);

        Assert.DoesNotContain(ledger.Entries, e => e.EntryPoint == "SBSW_dropped");
        // The record names a different library, so it does not describe this declaration.
        Assert.Equal(SwiftTransitionLedgerStatus.Unplanned, ledger.Entries.Single(e => e.EntryPoint == "SBSW_a").Status);
        Assert.Empty(ledger.Facts);
    }

    [Fact]
    public void Build_IncompleteFactsCarryTheirUnknownsAsReasons()
    {
        var ledger = SwiftTransitionLedger.Build("TestModule", Source, [Record("A", "SBSW_a", Facts("SBSW_a", complete: false))]);

        var a = ledger.Entries.Single(e => e.EntryPoint == "SBSW_a");
        Assert.Equal(SwiftTransitionLedgerStatus.Incomplete, a.Status);
        Assert.Contains("is not known", Assert.Single(a.Reasons));
    }

    [Fact]
    public void Build_TwoProducersRecordingDifferentFactsForOneDeclarationIsAnIssue()
    {
        var ledger = SwiftTransitionLedger.Build("TestModule", Source,
        [
            Record("A", "SBSW_a", Facts("SBSW_a")),
            Record("A", "SBSW_a", Facts("SBSW_a", synthesizer: SwiftWrapperSynthesizer.AsyncBridge)),
        ]);

        Assert.Contains(ledger.Issues, i => i.Contains("Box`1+PInvokes::A") && i.Contains("different facts"));
    }

    [Fact]
    public void Build_ADeclarationInAnInactiveRegionIsAnIssueNotASilentOmission()
    {
        const string source = """
            class Holder
            {
            #if NEVER_DEFINED
                [LibraryImport("L", EntryPoint = "S")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                static partial void Hidden();
            #endif
            }
            """;

        var ledger = SwiftTransitionLedger.Build("TestModule", source, []);

        Assert.Empty(ledger.Entries);
        Assert.Contains(ledger.Issues, i => i.Contains("inactive preprocessor region"));
    }

    [Fact]
    public void Compare_FindsSitesWithoutEntriesAndEntriesWithoutSitesCountingMultiplicity()
    {
        var ledger = SwiftTransitionLedger.Build("TestModule", Source, []);
        var a = ("Acme.Widgets.Box`1+PInvokes::A", "SBSW_a");
        var b = ("Acme.Widgets.Box`1+PInvokes::B", "SBSW_b");
        var named = ("Acme.Widgets.Box`1+PInvokes::NamedByItself", "NamedByItself");

        var (missing, extra) = ledger.Compare([a, b, named]);
        Assert.Empty(missing);
        Assert.Empty(extra);

        // The IL has A twice and no B: one A and the unknown site lack entries; B lacks a site.
        (missing, extra) = ledger.Compare([a, a, named, ("Acme.Widgets.Other::X", "SBSW_x")]);
        Assert.Equal(new[] { "Acme.Widgets.Box`1+PInvokes::A SBSW_a", "Acme.Widgets.Other::X SBSW_x" }, missing);
        Assert.Equal(new[] { "Acme.Widgets.Box`1+PInvokes::B SBSW_b" }, extra);
    }

    [Fact]
    public void ToJson_CarriesTheTotalsTheEntriesAndTheCitedFacts()
    {
        var registered = SyntheticSwiftFunction.FromDeclaration(
            "@_silgen_name(\"SBSW_a\") public func a(_ x: Swift.Int) {", SwiftWrapperSynthesizer.OptionalBuffer, Lib);
        var ledger = SwiftTransitionLedger.Build("TestModule", Source,
            [Record("A", "SBSW_a", Facts("SBSW_a"))], registered: [registered]);

        using var json = JsonDocument.Parse(ledger.ToJson());
        var root = json.RootElement;
        Assert.Equal(SwiftTransitionLedger.SchemaVersion, root.GetProperty("schema").GetInt32());
        Assert.Equal(SwiftCallFacts.CurrentVersion, root.GetProperty("factsVersion").GetInt32());
        var totals = root.GetProperty("totals");
        Assert.Equal(3, totals.GetProperty("transitions").GetInt32());
        Assert.Equal(1, totals.GetProperty("complete").GetInt32());
        Assert.Equal(2, totals.GetProperty("unplanned").GetInt32());
        Assert.Equal(1, totals.GetProperty("registeredBySynthesizer").GetProperty("OptionalBuffer").GetInt32());
        var a = root.GetProperty("transitions").EnumerateArray().Single(e => e.GetProperty("entryPoint").GetString() == "SBSW_a");
        Assert.Equal("Acme.Widgets.Box`1+PInvokes::A", a.GetProperty("member").GetString());
        Assert.Equal("SBSW_a", a.GetProperty("factsId").GetString());
        Assert.Equal("Synthesized", a.GetProperty("factsSource").GetString());
        Assert.Equal("SBSW_a", Assert.Single(root.GetProperty("facts").EnumerateArray()).GetProperty("target").GetProperty("symbol").GetString());
    }
}
