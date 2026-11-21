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

        private static readonly HashSet<string> Guarded = new(StringComparer.Ordinal) { "$sGuarded" };

        [Theory]
        [InlineData("ios-arm64", "ios")]
        [InlineData("ios-arm64_x86_64-simulator", "ios-simulator")]
        [InlineData("tvos-arm64_x86_64-simulator", "tvos-simulator")]
        [InlineData("ios-arm64_x86_64-maccatalyst", "maccatalyst")]
        [InlineData("macos-arm64_x86_64", "macos")]
        public void SliceVariant_ReadsTheXcframeworkSliceId(string slice, string expected)
            => Assert.Equal(expected, OsLoadSafetyAudit.SliceVariant(slice));

        [Fact]
        public void EvaluateImports_PassesAGuardedWeakAbsentImport_AndCountsOnlyCoveredSwiftImports()
        {
            var verdict = OsLoadSafetyAudit.EvaluateImports("W", new[]
            {
                Import("_$sShipped", "CryptoKit", weak: false),
                Import("_$sGuarded", "CryptoKit"),
                Import("_swift_release", "libswiftCore", weak: false),
                Import("_$sLibThing", "Lib", weak: false),
                Import("_$sCoalesced", "<weak-def-coalesce>", weak: false),
            }, Loads, Coverage, Guarded);

            Assert.Empty(verdict.Failures);
            Assert.Equal(2, verdict.CoveredSwiftImports);
            Assert.Equal(new[] { "$sGuarded" }, verdict.GuardedAbsent);
        }

        [Fact]
        public void EvaluateImports_FailsAStrongAbsentImport_AnUnguardedOne_AndAnUnloadedProvider()
        {
            var verdict = OsLoadSafetyAudit.EvaluateImports("W", new[]
            {
                Import("_$sGuarded", "CryptoKit", weak: false),
                Import("_$sUnguarded", "CryptoKit"),
                Import("_$sElsewhere", "SwiftUI"),
            }, Loads, Coverage, Guarded);

            Assert.Equal(3, verdict.Failures.Count);
            Assert.Contains(verdict.Failures, f => f.Contains("_$sGuarded", StringComparison.Ordinal) && f.Contains("strongly", StringComparison.Ordinal));
            Assert.Contains(verdict.Failures, f => f.Contains("_$sUnguarded", StringComparison.Ordinal) && f.Contains("no declaration", StringComparison.Ordinal));
            Assert.Contains(verdict.Failures, f => f.Contains("does not load", StringComparison.Ordinal));
            Assert.Empty(verdict.GuardedAbsent);
        }

        [Fact]
        public void EvaluatePInvokes_HoldsADirectCallIntoAnAbsentSymbolToAFlooredDeclaration()
        {
            var verdict = OsLoadSafetyAudit.EvaluatePInvokes("A.dll", new[]
            {
                (Kit, "$sShipped", "T.a"),
                (Kit, "_$sGuarded", "T.b"),
                (Kit, "$sUnguarded", "T.c"),
                (Kit, "swift_retain", "T.d"),
                (Lib, "$sUncovered", "T.e"),
            }, Coverage, Guarded);

            Assert.Equal(3, verdict.CoveredSwiftImports);
            Assert.Equal(new[] { "$sGuarded" }, verdict.GuardedAbsent);
            var failure = Assert.Single(verdict.Failures);
            Assert.Contains("T.c", failure);
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
        public void ReadGuardedSymbols_FindsEveryInferredFloorWhereverTheReportNestsIt()
        {
            const string report = """
                { "Generation": { "Report": { "InferredOsFloors": [
                    { "Declaration": "Lib.P.raised", "Symbol": "$sA", "Symbols": [ "$sA", "$sB" ], "Raised": true },
                    { "Declaration": "Lib.P.guarded", "Symbol": "$sC", "Symbols": [ "$sC" ], "Raised": false } ] } },
                  "Modules": [ { "InferredOsFloors": [ { "Declaration": "Dep.Q", "Symbol": "$sD", "Raised": true } ] } ] }
                """;
            var (symbols, raised) = OsLoadSafetyAudit.ReadGuardedSymbols(report);

            Assert.True(symbols.SetEquals(new[] { "$sA", "$sB", "$sC", "$sD" }));
            Assert.Equal(new[] { "Lib.P.raised", "Dep.Q" }, raised);
        }
    }
}
