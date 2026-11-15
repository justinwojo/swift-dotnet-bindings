// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="SwiftRouteMarkers"/>: the SB0001 marker of a member bridge routing moved onto native
/// bridges is settled from the final C# — dropped, or reduced to what else it said — and the marker of
/// a member that still makes a direct Swift-convention call is kept.
/// </summary>
public sealed class SwiftRouteMarkersTests
{
    private const string Sentence =
        "No @_cdecl wrapper or native thunk available (the declaring type is generic in a way the wrapper cannot specialize). P/Invoke calling convention may not match Swift ABI";

    private const string Hide = "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]";

    private static string Marker(string message, string id = "SB0001") =>
        $"[global::System.Obsolete(\"{message}\", DiagnosticId = \"{id}\", UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting\")]";

    /// <summary>
    /// A module whose native class holds a bridge twin (a C-convention import of an <c>SBSR_</c>
    /// symbol behind the shim routing writes) and a direct Swift-convention import.
    /// </summary>
    private static string Module(string members) => $$"""
        namespace M
        {
            internal static unsafe partial class Native
            {
                internal static long Routed(long a) => Routed__SBSR_0123456789abcdef(a);

                [global::System.Runtime.InteropServices.LibraryImport("MSwiftBindings", EntryPoint = "SBSR_0123456789abcdef")]
                private static partial long Routed__SBSR_0123456789abcdef(long a);

                [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
                [global::System.Runtime.InteropServices.LibraryImport("MSwiftBindings", EntryPoint = "$sDIRECT")]
                internal static partial long Direct(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);

                [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
                [global::System.Runtime.InteropServices.LibraryImport("MSwiftBindings", EntryPoint = "$sPLAIN")]
                internal static partial long Plain(long a, nint b);

                [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
                [global::System.Runtime.InteropServices.LibraryImport("M", EntryPoint = "$s1M3BoxVMa")]
                internal static partial nint Metadata(nint request, nint element);
            }

            public sealed class Box
            {
        {{members}}
            }
        }
        """;

    private static (SwiftTransitionGuards Guards, string Rewritten) Settle(string members)
    {
        var source = Module(members);
        var guards = SwiftTransitionGuards.Plan("M", [source]);
        return (guards, guards.Rewrite(source));
    }

    // ---- the message ----

    [Theory]
    [InlineData(Sentence + ".", "")]
    [InlineData("Deprecated: use other. " + Sentence + ".", "Deprecated: use other.")]
    [InlineData(Sentence + ". P/Invoke entry point not exported by the library. This method will throw EntryPointNotFoundException at runtime.",
        "P/Invoke entry point not exported by the library. This method will throw EntryPointNotFoundException at runtime.")]
    [InlineData("No @_cdecl wrapper or native thunk available. P/Invoke calling convention may not match Swift ABI.", "")]
    [InlineData("No @_cdecl wrapper or native thunk available (a parameter (or its element) is a tuple). P/Invoke calling convention may not match Swift ABI.", "")]
    public void TheSentence_IsRemovedAndTheRestKept(string message, string expected) =>
        Assert.Equal(expected, SwiftRouteMarkers.Strip(message));

    [Fact]
    public void AMessageWithoutTheSentence_IsNotStripped() =>
        Assert.Null(SwiftRouteMarkers.Strip("This member cannot be called through its wrapper. It throws."));

    // ---- which members settle ----

    [Fact]
    public void AMemberReachingOnlyABridge_LosesItsMarkerAndItsHide()
    {
        var (guards, rewritten) = Settle($$"""
                    /// <summary>Doc.</summary>
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Get(long a) => Native.Routed(a);
            """);

        var settled = Assert.Single(guards.SettledMarkers);
        Assert.Equal("Box.Get(long)", settled.Display);
        Assert.Equal(["SBSR_0123456789abcdef"], settled.Bridges.ToArray());
        Assert.DoesNotContain("SB0001", rewritten);
        Assert.DoesNotContain("EditorBrowsable", rewritten);
        Assert.Contains($"{SwiftRouteMarkers.NotePrefix} SBSR_0123456789abcdef: Box.Get(long) makes no direct Swift-convention call besides metadata accessors.", rewritten);
        // The doc comment still documents the member: the note sits between them, and no attribute follows the member.
        Assert.Contains("/// <summary>Doc.</summary>", rewritten);
        Assert.Empty(CSharpSyntaxTree.ParseText(rewritten).GetDiagnostics());
    }

    [Fact]
    public void AMemberReachingABridgeThroughAHelper_Settles()
    {
        var (guards, _) = Settle($$"""
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Get(long a) => Helper(a) + 1;
                    private static long Helper(long a) => Native.Routed(a);
            """);
        Assert.Equal("Box.Get(long)", Assert.Single(guards.SettledMarkers).Display);
    }

    [Theory]
    // A direct import with a shape the guards refuse on.
    [InlineData("Native.Routed(a) + Native.Direct(a, default)")]
    // A direct import with no such shape still calls Swift directly.
    [InlineData("Native.Routed(a) + Native.Plain(a, 0)")]
    public void AMemberStillMakingADirectCall_KeepsItsMarker(string body)
    {
        var (guards, rewritten) = Settle($$"""
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Get(long a) => {{body}};
            """);
        Assert.Empty(guards.SettledMarkers);
        Assert.Contains("DiagnosticId = \"SB0001\"", rewritten);
        Assert.Contains("EditorBrowsable", rewritten);
    }

    [Fact]
    public void AMemberAlsoReachingItsTypesMetadataAccessor_Settles()
    {
        // A member of a generic type builds its type's metadata through the accessor before it calls
        // the bridge, as every member of that type does, marked or not.
        var (guards, rewritten) = Settle($$"""
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Get(long a) => Native.Routed(a + Native.Metadata(0, 0));
            """);
        Assert.Equal("Box.Get(long)", Assert.Single(guards.SettledMarkers).Display);
        Assert.DoesNotContain("SB0001", rewritten);
    }

    [Fact]
    public void AMemberReachingNoBridge_KeepsItsMarker()
    {
        var (guards, rewritten) = Settle($$"""
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Get(long a) => a + 1;
                    public long Other(long a) => Native.Routed(a);
            """);
        Assert.Empty(guards.SettledMarkers);
        Assert.Contains("DiagnosticId = \"SB0001\"", rewritten);
    }

    [Fact]
    public void AnUncallableMarker_IsNeverSettled()
    {
        var (guards, rewritten) = Settle($$"""
                    {{Marker("This member cannot be called on this runtime.", "SB0009")}}
                    public long Get(long a) => Native.Routed(a);
            """);
        Assert.Empty(guards.SettledMarkers);
        Assert.Contains("SB0009", rewritten);
    }

    [Fact]
    public void AProperty_SettlesWhenEveryAccessorReachesOnlyBridges()
    {
        var (guards, rewritten) = Settle($$"""
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Value
                    {
                        get => Native.Routed(1);
                        set => Native.Routed(value);
                    }
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Mixed
                    {
                        get => Native.Routed(1);
                        set => Native.Plain(value, 0);
                    }
            """);
        Assert.Equal("Box.Value", Assert.Single(guards.SettledMarkers).Display);
        Assert.Single(rewritten.Split("DiagnosticId = \"SB0001\"").Skip(1));
    }

    // ---- what is left of the marker ----

    [Fact]
    public void ALibraryDeprecation_StaysAsAPlainObsolete()
    {
        var (_, rewritten) = Settle($$"""
                    {{Marker("Deprecated: use other. " + Sentence + ".")}}
                    {{Hide}}
                    public long Get(long a) => Native.Routed(a);
            """);
        Assert.Contains("[global::System.Obsolete(\"Deprecated: use other.\")]", rewritten);
        Assert.DoesNotContain("DiagnosticId", rewritten);
        Assert.DoesNotContain("EditorBrowsable", rewritten);
    }

    [Fact]
    public void AMissingSymbol_StaysUnderSB0002()
    {
        var (_, rewritten) = Settle($$"""
                    {{Marker(Sentence + ". P/Invoke entry point not exported by the library. This method will throw EntryPointNotFoundException at runtime.")}}
                    {{Hide}}
                    public long Get(long a) => Native.Routed(a);
            """);
        Assert.Contains("DiagnosticId = \"SB0002\"", rewritten);
        Assert.Contains("\"P/Invoke entry point not exported by the library. This method will throw EntryPointNotFoundException at runtime.\"", rewritten);
        Assert.DoesNotContain("SB0001", rewritten);
    }

    [Fact]
    public void AHideWrittenForAnotherReason_StaysAheadOfTheMarker()
    {
        var (guards, rewritten) = Settle($$"""
                    {{Hide}}
                    {{Marker(Sentence + ".")}}
                    public long Get(long a) => Native.Routed(a);
            """);
        Assert.Single(guards.SettledMarkers);
        Assert.Contains("EditorBrowsable", rewritten);
        Assert.DoesNotContain("SB0001", rewritten);
    }

    // ---- the written count ----

    [Fact]
    public void EachSettledMarker_MustBeWrittenOnce()
    {
        var source = Module($$"""
                    {{Marker(Sentence + ".")}}
                    public long Get(long a) => Native.Routed(a);
            """);
        var guards = SwiftTransitionGuards.Plan("M", [source]);
        Assert.Throws<System.InvalidOperationException>(guards.VerifyWritten);
        guards.Rewrite(source, written: true);
        guards.VerifyWritten();
    }

    // ---- the wrapper compile's routing ----

    [Fact]
    public void TheWrapperCompile_JournalsEachSettlementSoItCanBeUndone()
    {
        var file = "M.Types.Box.cs";
        var routed = Module($$"""
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Get(long a) => Native.Routed(a);
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Keep(long a) => Native.Plain(a, 0);
            """);
        var (settled, markers) = SwiftBridgeFinalizer.SettleMarkers([file], new Dictionary<string, string> { [file] = routed });

        var marker = Assert.Single(markers);
        Assert.Equal(file, marker.File);
        Assert.Equal("Box.Get(long)", marker.Member);
        Assert.Equal("SBSR_0123456789abcdef", marker.Symbol);
        Assert.Single(settled[file].Split("DiagnosticId = \"SB0001\"").Skip(1));

        var journal = new SwiftBridgeRoutingJournal { Schema = SwiftBridgeRoutingJournal.SchemaVersion, Module = "M", Library = "MSwiftBindings", Rewrites = [], Markers = markers };
        var reread = SwiftBridgeRoutingJournal.Parse(journal.ToJson());
        Assert.Equal(routed, reread.Revert(file, settled[file]));
        // A build stopped between the journal and the C# leaves the marker as generated: accepted as undone.
        Assert.Equal(routed, reread.Revert(file, routed));
    }

    [Fact]
    public void AJournalWithoutMarkers_ReadsAsSettlingNone()
    {
        var json = $$"""{ "schema": {{SwiftBridgeRoutingJournal.SchemaVersion}}, "module": "M", "library": "MSwiftBindings", "rewrites": [] }""";
        var journal = SwiftBridgeRoutingJournal.Parse(json);
        Assert.Empty(journal.AllEdits);
        Assert.Equal("text", journal.Revert("M.cs", "text"));
    }

    // ---- the report ----

    [Fact]
    public void TheReport_KeepsARowAnotherFileStillMarksUnderTheSameName()
    {
        // A type's members may span files; a file with no note still says which members stay marked.
        var (_, settledFile) = Settle($$"""
                    {{Marker(Sentence + ".")}}
                    public long Get(long a) => Native.Routed(a);
            """);
        var markedFile = Module($$"""
                    {{Marker(Sentence + ".")}}
                    public long Get(long a) => Native.Plain(a, 0);
            """);
        var directory = System.IO.Directory.CreateTempSubdirectory("route-markers-");
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory.FullName, "M.Types.Box.cs"), settledFile);
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory.FullName, "M.Types.Box.Other.cs"), markedFile);
        var rows = new List<DegradedMemberItem>
        {
            new() { Kind = BindingItemKind.Method, Name = "Get", ContainingType = "M.Box", DiagnosticId = "SB0001" },
        };

        try
        {
            Assert.False(SwiftTransitionGuardReport.WithdrawSettledMarkers(rows, directory.FullName));
            Assert.Single(rows);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheReport_ReadsSettledAndStillMarkedMembersFromTheCSharp()
    {
        var (_, rewritten) = Settle($$"""
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Get(long a) => Native.Routed(a);
                    {{Marker(Sentence + ".")}}
                    {{Hide}}
                    public long Keep(long a) => Native.Plain(a, 0);
            """);
        var (settled, marked) = SwiftRouteMarkers.MembersOf(rewritten);
        Assert.Equal([("Box", "Get")], settled);
        Assert.Equal([("Box", "Keep")], marked);
    }
}
