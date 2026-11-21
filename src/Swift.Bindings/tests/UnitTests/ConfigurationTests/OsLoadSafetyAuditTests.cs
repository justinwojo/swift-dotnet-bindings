// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// The P2 half of the OS load-safety gate: every import or direct P/Invoke of a Swift symbol a
    /// covered provider does not ship must be weak and belong to a declaration the binding report
    /// records as floored; plus the supplied inventory the BindingTests dependency is generated with.
    /// </summary>
    public class OsLoadSafetyAuditTests
    {
        private const string Kit = "/System/Library/Frameworks/CryptoKit.framework/CryptoKit";
        private const string Lib = "@rpath/Lib.framework/Lib";

        private static readonly OsFrameworkWeakLinking.LoadedDylib[] Loads =
        {
            new(Kit, OsFrameworkWeakLinking.LoadKind.Weak),
            new("/usr/lib/swift/libswiftCore.dylib", OsFrameworkWeakLinking.LoadKind.Load),
            new(Lib, OsFrameworkWeakLinking.LoadKind.Weak),
        };

        private static OsFrameworkWeakLinking.ImportedSymbol Import(string symbol, string from, bool weak = true) =>
            new("arm64", symbol, from, weak);

        // CryptoKit ships $sShipped; nothing else is covered.
        private static CoveredProvider? Coverage(string installName) =>
            installName == Kit ? new CoveredProvider(Kit, s => s == "$sShipped") : null;

        [Theory]
        [InlineData("ios-arm64", "ios")]
        [InlineData("ios-arm64_x86_64-simulator", "ios-simulator")]
        [InlineData("tvos-arm64_x86_64-simulator", "tvos-simulator")]
        [InlineData("ios-arm64_x86_64-maccatalyst", "maccatalyst")]
        [InlineData("macos-arm64_x86_64", "macos")]
        public void SliceVariant_ReadsTheXcframeworkSliceId(string slice, string expected)
            => Assert.Equal(expected, OsLoadSafetyAudit.SliceVariant(slice));

        [Fact]
        public void EvaluateImports_ReturnsEveryWeakAbsentImport_AndCountsOnlyCoveredSwiftImports()
        {
            var verdict = OsLoadSafetyAudit.EvaluateImports("W", new[]
            {
                Import("_$sShipped", "CryptoKit", weak: false),
                Import("_$sAbsent", "CryptoKit"),
                Import("_swift_release", "libswiftCore", weak: false),
                Import("_$sLibThing", "Lib", weak: false),
                Import("_$sCoalesced", "<weak-def-coalesce>", weak: false),
            }, Loads, Coverage);

            Assert.Empty(verdict.Failures);
            Assert.Equal(2, verdict.CoveredSwiftImports);
            var absent = Assert.Single(verdict.Absent);
            Assert.Equal(("arm64", "_$sAbsent", Kit), (absent.Arch, absent.Symbol, absent.InstallName));
        }

        [Fact]
        public void EvaluateImports_FailsAStrongAbsentImport_AndAnUnloadedProvider_WhateverTheReportsSay()
        {
            var verdict = OsLoadSafetyAudit.EvaluateImports("W", new[]
            {
                Import("_$sAbsent", "CryptoKit", weak: false),
                Import("_$sElsewhere", "SwiftUI"),
            }, Loads, Coverage);

            Assert.Equal(2, verdict.Failures.Count);
            Assert.Contains(verdict.Failures, f => f.Contains("_$sAbsent", StringComparison.Ordinal) && f.Contains("strongly", StringComparison.Ordinal));
            Assert.Contains(verdict.Failures, f => f.Contains("does not load", StringComparison.Ordinal));
            Assert.Empty(verdict.Absent);
        }

        [Fact]
        public void AttributeAbsentImports_MapsEachExportToTheAbsentImportsItsCodeReaches_AndFailsClosed()
        {
            // _SBW_a calls the stub of $sA; nothing exported reaches $sB; $sC is imported only on x86_64;
            // $sD is imported on arm64, but nothing the read of the image follows references it.
            var reach = WrapperImportReach.Parse(
                new[] { "  0 __text           00000100 0000000000001000 TEXT" },
                new[] { "0000000000001000 T _SBW_a", "0000000000001010 t _orphan" },
                new[]
                {
                    "_SBW_a:", "    1000:\tbl\t0x2000 ; symbol stub for: _$sA",
                    "_orphan:", "    1010:\tbl\t0x2008 ; symbol stub for: _$sB",
                },
                Array.Empty<string>());
            var absent = new[]
            {
                new AbsentSymbol("arm64", "_$sA", Kit, "26.2"),
                new AbsentSymbol("arm64", "_$sB", Kit, "26.2"),
                new AbsentSymbol("x86_64", "_$sC", Kit, "26.2"),
                new AbsentSymbol("x86_64", "_$sA", Kit, "26.2"),
                new AbsentSymbol("arm64", "_$sD", Kit, "26.2"),
            };

            var verdict = OsLoadSafetyAudit.AttributeAbsentImports("W sim", absent, reach);

            Assert.Equal(new[] { "_SBW_a" }, verdict.ByExport.Keys.ToArray());
            Assert.Equal("_$sA", Assert.Single(verdict.ByExport["_SBW_a"]).Symbol);
            Assert.Equal(3, verdict.Failures.Count);
            Assert.Contains(verdict.Failures, f => f.Contains("[arm64]: _$sD", StringComparison.Ordinal));
            Assert.Contains(verdict.Failures, f => f.Contains("_$sB", StringComparison.Ordinal) && f.Contains("_orphan", StringComparison.Ordinal));
            Assert.Contains(verdict.Failures, f => f.Contains("[x86_64]", StringComparison.Ordinal) && f.Contains("_$sC", StringComparison.Ordinal));
        }

        [Fact]
        public void DirectAbsentSymbol_NamesADirectSwiftCallIntoACoveredProviderThatLacksIt()
        {
            Assert.Null(OsLoadSafetyAudit.DirectAbsentSymbol(Kit, "$sShipped", Coverage));
            Assert.Null(OsLoadSafetyAudit.DirectAbsentSymbol(Kit, "swift_retain", Coverage));
            Assert.Null(OsLoadSafetyAudit.DirectAbsentSymbol(Lib, "$sUncovered", Coverage));
            Assert.Equal("_$sAbsent", OsLoadSafetyAudit.DirectAbsentSymbol(Kit, "_$sAbsent", Coverage)?.Symbol);
        }

        [Fact]
        public void Resolve_PrefersASuppliedProvider_ThenCoversEveryOsResidentPath()
        {
            var supplied = new[]
            {
                new OsSymbolInventory.Provider(Lib, "Lib", null, "26.0", "99.0", new HashSet<string> { "$sLibA" }),
            };
            Func<string, bool> shipped = s => s == "$sOs";

            Assert.True(OsLoadSafetyAudit.Resolve(Lib, supplied, shipped)!.Ships("$sLibA"));
            Assert.Equal(Lib, OsLoadSafetyAudit.Resolve("Lib", supplied, shipped)!.InstallName);
            Assert.Equal(Lib, OsLoadSafetyAudit.Resolve("/out/Lib.framework/Lib", supplied, shipped)!.InstallName);
            Assert.True(OsLoadSafetyAudit.Resolve(Kit, supplied, shipped)!.Ships("$sOs"));
            Assert.NotNull(OsLoadSafetyAudit.Resolve("/usr/lib/swift/libswiftCore.dylib", supplied, shipped));
            Assert.Null(OsLoadSafetyAudit.Resolve("@rpath/Other.framework/Other", supplied, shipped));
            Assert.Null(OsLoadSafetyAudit.Resolve("__Internal", supplied, shipped));
        }

        [Fact]
        public void Resolve_CoversTheBareNameOfAnOsFramework_ThatASystemBindingCallsThrough()
        {
            Func<string, bool> shipped = s => s == "$sOs";
            Func<string, bool> isOsFramework = name => name == "CryptoKit";

            var provider = OsLoadSafetyAudit.Resolve("CryptoKit", Array.Empty<OsSymbolInventory.Provider>(), shipped, isOsFramework);
            Assert.Equal(Kit, provider!.InstallName);
            Assert.True(provider.Ships("$sOs"));
            Assert.Null(OsLoadSafetyAudit.Resolve("Lib", Array.Empty<OsSymbolInventory.Provider>(), shipped, isOsFramework));
            Assert.Null(OsLoadSafetyAudit.Resolve("CryptoKit", Array.Empty<OsSymbolInventory.Provider>(), shipped));
        }

        [Fact]
        public void BuildSuppliedInventory_ListsEverySwiftSymbolButTheOmitted_AsALoadableProvider()
        {
            var nm = new[] { "_$sLib6absentSivg", "_$sLib4keptSivg", "_$sLib6absentSivs", "_objc_thing", "  _$sLib5otherSSvg  " };
            var json = OsLoadSafetyAudit.BuildSuppliedInventory(nm, Lib, "Lib", "26.0", "99.0", new[] { "absent" }, out var omitted);

            Assert.Equal(2, omitted["absent"]);
            var path = Path.Combine(Path.GetTempPath(), "supplied-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            try
            {
                var provider = Assert.Single(OsSymbolInventory.Load(path, OsSymbolInventory.Shipped).Providers);
                Assert.Equal((Lib, "Lib", "26.0", "99.0"), (provider.InstallName, provider.ModuleName, provider.OsVersion, provider.Floor));
                Assert.True(provider.Symbols.SetEquals(new[] { "$sLib4keptSivg", "$sLib5otherSSvg" }));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void BuildSuppliedInventory_FailsARuleThatMatchesNothing_AndAnEmptyRead()
        {
            Assert.Throws<InvalidOperationException>(() => OsLoadSafetyAudit.BuildSuppliedInventory(
                new[] { "_$sLib4keptSivg" }, Lib, "Lib", "26.0", "99.0", new[] { "renamed" }, out _));
            Assert.Throws<InvalidOperationException>(() => OsLoadSafetyAudit.BuildSuppliedInventory(
                new[] { "_objc_only" }, Lib, "Lib", "26.0", "99.0", Array.Empty<string>(), out _));
        }

        [Fact]
        public void ReadRaisedDeclarations_FindsEveryRaisedFloorWhereverTheReportNestsIt()
        {
            const string report = """
                { "Generation": { "Report": { "InferredOsFloors": [
                    { "Declaration": "Lib.P.raised", "Symbol": "$sA", "Symbols": [ "$sA", "$sB" ], "Raised": true },
                    { "Declaration": "Lib.P.guarded", "Symbol": "$sC", "Symbols": [ "$sC" ], "Raised": false } ] } },
                  "Modules": [ { "InferredOsFloors": [ { "Declaration": "Dep.Q", "Symbol": "$sD", "Raised": true } ] } ] }
                """;

            Assert.Equal(new[] { "Lib.P.raised", "Dep.Q" }, OsLoadSafetyAudit.ReadRaisedDeclarations(report));
        }
    }
}
