// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The transition ledger cross-check, end to end: one C# source is both read by the ledger (as the
/// generator reads the C# it emitted) and compiled and scanned as IL (as the transition gate reads the
/// built assembly). Every transition shape the generator renders must join, kind by kind, with the
/// IL's naming of the member that holds it; a missing, extra, unplanned or misclassified entry fails.
/// </summary>
public class LedgerCrossCheckTests
{
    private const string Source = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Swift;
        [assembly: DisableRuntimeMarshalling]
        namespace Acme.Widgets
        {
            public enum Mode { A, B }

            public static unsafe class Box
            {
                [DllImport("lib", EntryPoint = "prim")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static extern nint Prim(nint a, Mode m, void* p, delegate* unmanaged[Swift]<nint, void> f);

                [DllImport("lib", EntryPoint = "special")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static extern void Special(nint a, SwiftSelf self);

                [DllImport("lib", EntryPoint = "SBW_wrap")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
                internal static extern void Wrap(SwiftIndirectResult result, nint a);

                // The shape LibraryImport's generator gives an import: the declared signature on the
                // outer method, the lowered one on a local extern.
                [LibraryImport("lib", EntryPoint = "lowered")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static bool Lowered(SafeHandle handle)
                {
                    return __PInvoke(handle.DangerousGetHandle()) != 0;

                    [DllImport("lib", EntryPoint = "lowered")]
                    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                    static extern int __PInvoke(nint handle);
                }

                [DllImport("lib", EntryPoint = "marshalled")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static extern void Marshalled(SafeHandle handle);

                [DllImport("lib", EntryPoint = "SBW_plain")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
                internal static extern void Plain(nint a);

                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
                private static void Callback(nint a, SwiftSelf context) { }

                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                private static void CdeclCallback(nint a, nint context) { }

                private static readonly delegate* unmanaged[Swift]<nint, SwiftSelf, void> s_Callback = &Callback;
                private static readonly delegate* unmanaged[Cdecl]<nint, nint, void> s_CdeclCallback = &CdeclCallback;

                public static nint CallbackAddress => (nint)(delegate* unmanaged[Swift]<nint, SwiftSelf, void>)&Callback;

                public static Action Returned(nint fn, nint context)
                {
                    Action invoker = () =>
                    {
                        var _fp = (delegate* unmanaged[Swift]<void*, SwiftSelf, void>)fn;
                        _fp(null, new SwiftSelf((void*)context));
                    };
                    return invoker;
                }

                public static void Local(nint fn)
                {
                    Call(fn);
                    static void Call(nint p) => ((delegate* unmanaged[Swift]<nint, void>)p)(1);
                }
            }

            public sealed unsafe class Holder<T>
            {
                public static void Call(nint p) => ((delegate* unmanaged[Swift]<nint, void>)p)(2);
            }
        }
        """;

    private static SwiftCallFacts Facts(string symbol, string? pointerSource = null) => new()
    {
        Target = new SwiftCallTarget
        {
            Kind = pointerSource is null ? SwiftCallTargetKind.FreeFunction : SwiftCallTargetKind.ClosureInvocation,
            Symbol = pointerSource is null ? symbol : null,
            PointerSource = pointerSource,
        },
        Self = SwiftSelfFacts.None,
        Parameters = ImmutableArray<SwiftParameterFacts>.Empty,
        Result = SwiftResultFacts.Void,
        Effects = new SwiftCallEffects(),
    };

    private static SwiftTransitionRecord Import(string method, string entryPoint) => new()
    {
        MethodName = method,
        EntryPoint = entryPoint,
        Library = "lib",
        Facts = Facts(entryPoint),
        Source = SwiftCallFactsSource.Declared,
    };

    private static SwiftCalliRecord Calli(string type) => new() { FunctionPointerType = type, Facts = Facts("", "closure of type " + type) };

    private static readonly SwiftTransitionRecord[] AllImports =
    [
        Import("Prim", "prim"), Import("Special", "special"), Import("Wrap", "SBW_wrap"),
        Import("Lowered", "lowered"), Import("Marshalled", "marshalled"),
    ];

    private static readonly SwiftCallbackRecord[] AllCallbacks =
    [
        new() { CallbackName = "Callback", Facts = Facts("", "closure argument 'f' of $sOwner"), Owner = new SwiftClosureOwner("$sOwner", "f", 0, SwiftParameterConvention.Guaranteed, SwiftClosureKind.Escaping) },
    ];

    private static readonly SwiftCalliRecord[] AllCallis =
    [
        // Producers spell the pointer type unqualified; the emitted C# may qualify it.
        Calli("delegate* unmanaged[Swift]<void*, SwiftSelf, void>"),
        Calli("delegate* unmanaged[Swift]<nint, void>"),
    ];

    private static readonly Lazy<AssemblyTransitionReport> Il = new(() =>
        SwiftTransitionScanner.Scan(SwiftTransitionScannerTests.Compile(Source, "LedgerFixture"), "LedgerFixture.dll", SwiftTransitionScannerTests.Options()));

    private static SwiftTransitionLedger Ledger(
        IEnumerable<SwiftTransitionRecord>? imports = null, IEnumerable<SwiftCallbackRecord>? callbacks = null, IEnumerable<SwiftCalliRecord>? callis = null) =>
        SwiftTransitionLedger.Build("Acme", Source, imports ?? AllImports, callbacks: callbacks ?? AllCallbacks, callis: callis ?? AllCallis);

    private static LedgerCrossCheckResult Check(string ledgerJson) => LedgerCrossCheck.Check(Il.Value, [("Acme.swift-transition-ledger.json", ledgerJson)]);

    [Fact]
    public void EveryTransitionShapeJoinsTheIL_KindByKind()
    {
        var ledger = Ledger();
        var result = Check(ledger.ToJson());

        Assert.True(result.Passed, LedgerCrossCheck.ToSummary(result));
        Assert.Equal(4, result.IlSites[LedgerTransitionKind.ForwardPInvoke]);
        Assert.Equal(1, result.IlSites[LedgerTransitionKind.CdeclSpecialPInvoke]);
        Assert.Equal(1, result.IlSites[LedgerTransitionKind.ReverseCallback]);
        Assert.Equal(2, result.IlSites[LedgerTransitionKind.CallbackAddress]);
        Assert.Equal(3, result.IlSites[LedgerTransitionKind.ForwardCalli]);
        Assert.Equal(result.IlSites, result.LedgerEntries);
        Assert.Empty(result.Incomplete);
    }

    [Fact]
    public void Ledger_NamesEachSiteAsTheILDoes()
    {
        var keys = Ledger().Entries.Select(e => $"{e.Transition} {e.Key}").ToList();

        Assert.Contains("ForwardPInvoke Acme.Widgets.Box::Prim prim", keys);
        Assert.Contains("ForwardCalli Acme.Widgets.Holder`1::Call", keys);
        Assert.Contains("CdeclSpecialPInvoke Acme.Widgets.Box::Wrap SBW_wrap", keys);
        Assert.Contains("ReverseCallback Acme.Widgets.Box::Callback", keys);
        // A static field initializer runs in the type initializer; an expression-bodied property in its getter.
        Assert.Contains("CallbackAddress Acme.Widgets.Box::.cctor Acme.Widgets.Box::Callback", keys);
        Assert.Contains("CallbackAddress Acme.Widgets.Box::get_CallbackAddress Acme.Widgets.Box::Callback", keys);
        Assert.Contains("ForwardCalli Acme.Widgets.Box::Returned.<lambda>", keys);
        Assert.Contains("ForwardCalli Acme.Widgets.Box::Local.Call", keys);
        // A C-convention callback and an import with no Swift register type are not Swift transitions.
        Assert.DoesNotContain(keys, k => k.Contains("CdeclCallback") || k.Contains("SBW_plain"));
    }

    [Fact]
    public void Ledger_MarksPrimitiveOnlyForwardTransitions()
    {
        var entries = Ledger().Entries;

        Assert.Equal(SwiftTransitionCarriers.Primitive, entries.Single(e => e.EntryPoint == "prim").Carriers);
        Assert.Equal(SwiftTransitionCarriers.NonPrimitive, entries.Single(e => e.EntryPoint == "special").Carriers);
        // A source-generated import passes a SafeHandle as its handle; a runtime-marshalled one passes the object.
        Assert.Equal(SwiftTransitionCarriers.Primitive, entries.Single(e => e.EntryPoint == "lowered").Carriers);
        Assert.Equal(SwiftTransitionCarriers.NonPrimitive, entries.Single(e => e.EntryPoint == "marshalled").Carriers);
        Assert.Equal(SwiftTransitionCarriers.Primitive, entries.Single(e => e.Member.EndsWith("Local.Call")).Carriers);
        Assert.Equal(SwiftTransitionCarriers.NonPrimitive, entries.Single(e => e.Member.EndsWith("Returned.<lambda>")).Carriers);
        Assert.Equal("SwiftIndirectResult", Assert.Single(entries.Single(e => e.EntryPoint == "SBW_wrap").Specials!.Value));
    }

    [Fact]
    public void ACallbackAndItsAddressCarryTheClosureFactsAndTheOwnersConvention()
    {
        var entries = Ledger().Entries.Where(e => e.Transition is SwiftTransitionKind.ReverseCallback or SwiftTransitionKind.CallbackAddress).ToList();

        Assert.Equal(3, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(SwiftTransitionLedgerStatus.Complete, e.Status);
            Assert.Equal(SwiftCallFactsSource.ClosureType, e.FactsSource);
            Assert.Equal("closure argument 'f' of $sOwner", e.FactsId);
            Assert.Equal(SwiftParameterConvention.Guaranteed, e.Owner!.Convention);
        });
    }

    [Fact]
    public void ASeededMissingEntryFails()
    {
        var json = JsonNode.Parse(Ledger().ToJson())!;
        var transitions = json["transitions"]!.AsArray();
        var dropped = transitions.First(t => (string?)t!["transition"] == "ForwardCalli")!;
        transitions.Remove(dropped);

        var result = Check(json.ToJsonString());

        Assert.False(result.Passed);
        Assert.Equal($"ForwardCalli {(string?)dropped["member"]}", Assert.Single(result.MissingFromLedger));
        Assert.Empty(result.MissingFromIl);
    }

    [Fact]
    public void AnEntryNoILSiteMatchesFails()
    {
        var json = JsonNode.Parse(Ledger().ToJson())!;
        var transitions = json["transitions"]!.AsArray();
        transitions.Add(JsonNode.Parse(transitions[0]!.ToJsonString()));

        var result = Check(json.ToJsonString());

        Assert.False(result.Passed);
        Assert.Single(result.MissingFromIl);
    }

    [Fact]
    public void ATransitionNoProducerRecordedIsUnplannedAndFails()
    {
        var result = Check(Ledger(callis: [AllCallis[0]]).ToJson());

        // Both calls through the unrecorded pointer type are unplanned.
        Assert.False(result.Passed);
        Assert.Equal(2, result.Unplanned.Length);
        Assert.Contains(result.Unplanned, l => l.Contains("Box::Local.Call"));
        Assert.Contains(result.Unplanned, l => l.Contains("Holder`1::Call"));
        Assert.All(result.Unplanned, l => Assert.Contains("no producer recorded", l));
    }

    [Fact]
    public void ACConventionRegisterTypeImportWithNoSwiftFactsIsJoinedButNotFailed()
    {
        // It calls a C entry point the bindings export, not a Swift function: there is no Swift call to describe.
        var result = Check(Ledger(imports: AllImports.Where(i => i.EntryPoint != "SBW_wrap")).ToJson());

        Assert.True(result.Passed, LedgerCrossCheck.ToSummary(result));
        Assert.Empty(result.Unplanned);
        Assert.Contains("Box::Wrap SBW_wrap", Assert.Single(result.UnplannedCdeclSpecial));
        Assert.Equal(result.IlSites, result.LedgerEntries);
    }

    [Fact]
    public void AHandWrittenTypesTransitionsAreListedApartInsteadOfJoined()
    {
        var json = JsonNode.Parse(Ledger().ToJson())!;
        var transitions = json["transitions"]!.AsArray();
        transitions.Remove(transitions.Single(t => ((string?)t!["member"])!.StartsWith("Acme.Widgets.Holder`1::", StringComparison.Ordinal)));

        var result = LedgerCrossCheck.Check(Il.Value, [("Acme.swift-transition-ledger.json", json.ToJsonString())], ["Acme.Widgets.Holder`1"]);

        Assert.True(result.Passed, LedgerCrossCheck.ToSummary(result));
        Assert.Equal("ForwardCalli Acme.Widgets.Holder`1::Call", Assert.Single(result.HandWritten));
    }

    [Fact]
    public void AHandWrittenTypeWithNoTransitionFails()
    {
        var result = LedgerCrossCheck.Check(Il.Value, [("Acme.swift-transition-ledger.json", Ledger().ToJson())], ["Acme.Widgets.Gone"]);

        Assert.False(result.Passed);
        Assert.Contains("Acme.Widgets.Gone", Assert.Single(result.Problems));
    }

    [Fact]
    public void ACarrierVerdictTheILDisagreesWithFails()
    {
        var json = JsonNode.Parse(Ledger().ToJson())!;
        var prim = json["transitions"]!.AsArray().First(t => (string?)t!["entryPoint"] == "prim")!;
        prim["carriers"] = "NonPrimitive";

        var result = Check(json.ToJsonString());

        Assert.False(result.Passed);
        Assert.Contains("Box::Prim prim", Assert.Single(result.CarrierMismatches));
    }

    [Fact]
    public void NoLedgerFailsClosed()
    {
        var result = LedgerCrossCheck.Check(Il.Value, []);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void ALedgerOfAnotherSchemaFailsClosed()
    {
        var json = JsonNode.Parse(Ledger().ToJson())!;
        json["schema"] = 1;

        var result = Check(json.ToJsonString());

        Assert.False(result.Passed);
        Assert.Contains("schema 1", Assert.Single(result.Problems));
    }

    [Fact]
    public void ClosuresOfDifferentTypesThroughOnePointerTypeKeepEveryCandidatesFacts()
    {
        var other = AllCallis[1] with { Facts = Facts("", "closure of type (Swift.Int) -> ()") };
        var ledger = Ledger(callis: [.. AllCallis, other]);
        var entry = ledger.Entries.Single(e => e.Member.EndsWith("Local.Call"));

        Assert.Equal(SwiftTransitionLedgerStatus.Incomplete, entry.Status);
        Assert.Contains("2 closures of different Swift types", Assert.Single(entry.Reasons));
        Assert.Null(entry.FactsId);
        Assert.Equal(2, entry.Candidates!.Value.Length);
        Assert.All(entry.Candidates!.Value, id => Assert.Contains(ledger.Facts, f => f.Id == id));

        var result = Check(ledger.ToJson());
        Assert.True(result.Passed, LedgerCrossCheck.ToSummary(result));
        Assert.Contains(result.Incomplete, l => l.Contains("Local.Call"));
    }

    [Theory]
    [InlineData("facts")]
    [InlineData("factsId")]
    public void APlannedEntryWhoseFactsTheLedgerDoesNotHoldFails(string removed)
    {
        var json = JsonNode.Parse(Ledger().ToJson())!;
        if (removed == "facts")
            json["facts"] = new JsonArray();
        else
            foreach (var t in json["transitions"]!.AsArray())
                t!.AsObject().Remove("factsId");

        var result = Check(json.ToJsonString());

        Assert.False(result.Passed);
        Assert.NotEmpty(result.LedgerIssues);
        Assert.All(result.LedgerIssues, l => Assert.Contains("facts", l));
    }

    [Theory]
    [InlineData("A.B+<>c__DisplayClass22_0::M.<lambda>", "A.B::M.<lambda>")]
    [InlineData("A.B+<>c::M.<lambda>", "A.B::M.<lambda>")]
    [InlineData("A.B`1+<>c__DisplayClass3_0`1::M.<lambda>", "A.B`1::M.<lambda>")]
    [InlineData("A.B+<Run>d__4::MoveNext", "A.B::Run")]
    [InlineData("A.B+Inner::M", "A.B+Inner::M")]
    public void NormalizeIlMember_FoldsCompilerGeneratedTypesIntoTheDeclaringType(string il, string expected) =>
        Assert.Equal(expected, LedgerCrossCheck.NormalizeIlMember(il));
}
