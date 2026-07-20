// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

// ---------------------------------------------------------------------------------------------
// Input inventory — the receipt-neutral description of what a run was handed.
// ---------------------------------------------------------------------------------------------
public class InputInventoryTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ingestion-inv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void DeriveFrameworkSearchPath_FrameworkWrapped_ReturnsSliceDir()
    {
        // <slice>/Foo.framework/Foo  ->  slice dir is the .framework's parent.
        var dylib = Path.Combine("/root", "ios-arm64", "Foo.framework", "Foo");
        var slice = InputInventory.DeriveFrameworkSearchPath(dylib);
        Assert.Equal(Path.Combine("/root", "ios-arm64"), slice);
    }

    [Fact]
    public void DeriveFrameworkSearchPath_BareBinary_ReturnsDylibDirectory()
    {
        // <slice>/libFoo.dylib  ->  slice dir is the dylib's own directory (NOT dirname(dirname)).
        var dylib = Path.Combine("/root", "ios-arm64", "libFoo.dylib");
        var slice = InputInventory.DeriveFrameworkSearchPath(dylib);
        Assert.Equal(Path.Combine("/root", "ios-arm64"), slice);
    }

    [Fact]
    public void DeriveFrameworkSearchPath_Null_ReturnsNull()
    {
        Assert.Null(InputInventory.DeriveFrameworkSearchPath(null));
        Assert.Null(InputInventory.DeriveFrameworkSearchPath(""));
    }

    [Fact]
    public void LocateModuleSwiftInterface_PrefersPublicOverPrivateAndPackage()
    {
        var root = MakeTempDir();
        try
        {
            var modulesDir = Path.Combine(root, "Foo.framework", "Modules", "Foo.swiftmodule");
            Directory.CreateDirectory(modulesDir);
            var pub = Path.Combine(modulesDir, "arm64-apple-ios.swiftinterface");
            File.WriteAllText(Path.Combine(modulesDir, "arm64-apple-ios.private.swiftinterface"), "private");
            File.WriteAllText(Path.Combine(modulesDir, "arm64-apple-ios.package.swiftinterface"), "package");
            File.WriteAllText(pub, "public");

            var located = InputInventory.LocateModuleSwiftInterface(root, "Foo");
            Assert.Equal(pub, located);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LocateModuleSwiftInterface_BareBinarySliceRootModulesDir()
    {
        var root = MakeTempDir();
        try
        {
            var modulesDir = Path.Combine(root, "Modules", "Foo.swiftmodule");
            Directory.CreateDirectory(modulesDir);
            var pub = Path.Combine(modulesDir, "x.swiftinterface");
            File.WriteAllText(pub, "public");

            Assert.Equal(pub, InputInventory.LocateModuleSwiftInterface(root, "Foo"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LocateModuleSwiftInterface_MissingOrNull_ReturnsNull()
    {
        Assert.Null(InputInventory.LocateModuleSwiftInterface(null, "Foo"));
        Assert.Null(InputInventory.LocateModuleSwiftInterface("/does/not/exist", "Foo"));
    }

    [Fact]
    public void FromCliInvocation_NoDependencies_PopulatesPrimary()
    {
        var inv = InputInventory.FromCliInvocation(
            "MyModule", primarySwiftInterfacePath: null, primaryDylibPath: null,
            primaryAbiJsonPath: null, primaryTbdPath: null, primaryXcframeworkPath: null,
            resolvedDependencies: null);

        Assert.Equal("MyModule", inv.Primary.ModuleName);
        Assert.Equal(InputSource.Primary, inv.Primary.Source);
        Assert.Empty(inv.Dependencies);
        Assert.Single(inv.AllModules());
    }

    [Fact]
    public void WithConverterProvenance_IsAdvisoryOnly_AttachesIdentityWithoutTouchingArtifacts()
    {
        var inv = InputInventory.FromCliInvocation(
            "MyModule", null, null, null, null, null, resolvedDependencies: null);

        var overlaid = inv.WithConverterProvenance(new Dictionary<string, string>
        {
            ["MyModule"] = "converter-run-42",
            ["Absent"] = "ignored",
        });

        Assert.Equal("converter-run-42", overlaid.Primary.ProvenanceIdentity);
        // Advisory only: no artifact path was invented.
        Assert.Null(overlaid.Primary.SwiftInterfacePath);
        Assert.Null(overlaid.Primary.BinaryPath);
        // The original is unchanged (records are immutable).
        Assert.Null(inv.Primary.ProvenanceIdentity);
    }

    [Fact]
    public void FindModule_ReturnsPrimaryOrDependencyOrNull()
    {
        var inv = new InputInventory
        {
            Primary = new InputModuleArtifacts { ModuleName = "P", Source = InputSource.Primary },
            Dependencies = new[] { new InputModuleArtifacts { ModuleName = "D", Source = InputSource.ExplicitDependency } },
        };

        Assert.Equal("P", inv.FindModule("P")!.ModuleName);
        Assert.Equal("D", inv.FindModule("D")!.ModuleName);
        Assert.Null(inv.FindModule("Missing"));
    }
}

// ---------------------------------------------------------------------------------------------
// Binding input graph — closure verdicts over the module-compilation-import edges.
// ---------------------------------------------------------------------------------------------
public class BindingInputGraphTests
{
    private static InputModuleArtifacts Mod(string name, InputSource src, string? binary = null, string? pkg = null) =>
        new() { ModuleName = name, Source = src, BinaryPath = binary, ManagedPackageId = pkg };

    private static ImportEdge Import(string module, bool exported = false, bool implOnly = false,
        ImportAccess access = ImportAccess.Plain) =>
        new() { ModuleName = module, Access = access, IsExported = exported, IsImplementationOnly = implOnly,
                InterfacePath = "/tmp/x.swiftinterface", Line = 1 };

    private static BindingInputGraph Build(
        InputInventory inv,
        Dictionary<string, IReadOnlyList<ImportEdge>> importsByModule,
        Func<string, InputSource?>? classify = null) =>
        BindingInputGraph.Build(
            inv,
            readImportEdges: m => importsByModule.TryGetValue(m.ModuleName, out var e) ? e : Array.Empty<ImportEdge>(),
            classifyUnsupplied: classify ?? (_ => null));

    [Fact]
    public void ClosedGraph_SuppliedImportTarget_NoUnresolvedCandidates()
    {
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = new[] { Mod("Base", InputSource.ExplicitDependency) },
        };
        var graph = Build(inv, new() { ["Bridge"] = new[] { Import("Base", exported: true) } });

        Assert.Empty(graph.UnresolvedPublicCompileImports());
    }

    [Fact]
    public void MissingImport_Unclassified_IsAnUnresolvedCandidate()
    {
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = Array.Empty<InputModuleArtifacts>(),
        };
        var graph = Build(inv, new() { ["Bridge"] = new[] { Import("Base", exported: true) } });

        var candidates = graph.UnresolvedPublicCompileImports();
        var edge = Assert.Single(candidates);
        Assert.Equal("Bridge", edge.FromModule);
        Assert.Equal("Base", edge.ToModule);
    }

    [Fact]
    public void SdkClassifiedImport_IsNotACandidate()
    {
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = Array.Empty<InputModuleArtifacts>(),
        };
        var graph = Build(inv,
            new() { ["Bridge"] = new[] { Import("UIKit") } },
            classify: m => m == "UIKit" ? InputSource.AppleSdk : (InputSource?)null);

        Assert.Empty(graph.UnresolvedPublicCompileImports());
    }

    [Fact]
    public void NonPublicUnresolvedImport_IsExcludedFromCandidates()
    {
        // @_implementationOnly + non-public access imports are never re-emitted into the wrapper, so a
        // missing one can't break the compile — must NOT become an obligation (absl/grpc C++ siblings).
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = Array.Empty<InputModuleArtifacts>(),
        };
        var graph = Build(inv, new()
        {
            ["Bridge"] = new[]
            {
                Import("absl", implOnly: true),
                Import("grpc", access: ImportAccess.Internal),
                Import("leveldb", access: ImportAccess.Package),
            },
        });

        Assert.Empty(graph.UnresolvedPublicCompileImports());
    }

    [Fact]
    public void SelfImport_IsIgnored()
    {
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = Array.Empty<InputModuleArtifacts>(),
        };
        var graph = Build(inv, new() { ["Bridge"] = new[] { Import("Bridge") } });

        Assert.Empty(graph.CompileImportEdges);
        Assert.Empty(graph.UnresolvedPublicCompileImports());
    }

    [Fact]
    public void DuplicateImporterMissingPairs_AreDeduped()
    {
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = Array.Empty<InputModuleArtifacts>(),
        };
        var graph = Build(inv, new()
        {
            ["Bridge"] = new[] { Import("Base"), Import("Base"), Import("Base", exported: true) },
        });

        Assert.Single(graph.UnresolvedPublicCompileImports());
    }

    [Fact]
    public void PrimaryToDependency_NativeLinkAndManagedRefEdges_ArePopulated()
    {
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = new[] { Mod("Base", InputSource.ExplicitDependency, binary: "/tmp/libBase.dylib", pkg: "SwiftBindings.Base") },
        };
        var graph = Build(inv, new Dictionary<string, IReadOnlyList<ImportEdge>>());

        Assert.Contains(graph.Edges, e => e.Kind == BindingInputEdgeKind.NativeRuntimeLink
            && e.FromModule == "Bridge" && e.ToModule == "Base");
        Assert.Contains(graph.Edges, e => e.Kind == BindingInputEdgeKind.ManagedBindingPackageReference
            && e.FromModule == "Bridge" && e.ToModule == "Base");
    }

    [Fact]
    public void TopologicalOrder_DependencyBeforeDependent()
    {
        // Bridge imports Base — Base must be built (and packed) first.
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = new[] { Mod("Base", InputSource.ExplicitDependency) },
        };
        var graph = Build(inv, new() { ["Bridge"] = new[] { Import("Base", exported: true) } });

        Assert.Equal(new[] { "Base", "Bridge" }, graph.TopologicalOrder());
    }

    [Fact]
    public void TopologicalOrder_ExcludesUnresolvedAndSdkModules()
    {
        // A supplied primary that imports both a supplied dep (Base) and an SDK module (UIKit): the
        // order is over BUILT modules only, so UIKit — never built by this run — is absent.
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = new[] { Mod("Base", InputSource.ExplicitDependency) },
        };
        var graph = Build(inv,
            new() { ["Bridge"] = new[] { Import("Base"), Import("UIKit") } },
            classify: m => m == "UIKit" ? InputSource.AppleSdk : (InputSource?)null);

        var order = graph.TopologicalOrder();
        Assert.Equal(new[] { "Base", "Bridge" }, order);
        Assert.DoesNotContain("UIKit", order);
    }

    [Fact]
    public void TopologicalOrder_TransitiveDepOfDep_OrdersDeepestFirst()
    {
        // Bridge imports Mid, Mid imports Base — a supplied dependency importing another supplied
        // dependency. The compile-import edge carries that transitive order: Base, Mid, Bridge.
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = new[]
            {
                Mod("Mid", InputSource.ExplicitDependency),
                Mod("Base", InputSource.ExplicitDependency),
            },
        };
        var graph = Build(inv, new()
        {
            ["Bridge"] = new[] { Import("Mid") },
            ["Mid"] = new[] { Import("Base") },
        });

        Assert.Equal(new[] { "Base", "Mid", "Bridge" }, graph.TopologicalOrder());
    }

    [Fact]
    public void TopologicalOrder_IndependentSiblings_BreakTiesLexically()
    {
        // Two supplied deps the primary imports but which don't depend on each other: deterministic
        // lexical tie-break, dependencies before the dependent.
        var inv = new InputInventory
        {
            Primary = Mod("Zeta", InputSource.Primary),
            Dependencies = new[]
            {
                Mod("Beta", InputSource.ExplicitDependency),
                Mod("Alpha", InputSource.ExplicitDependency),
            },
        };
        var graph = Build(inv, new() { ["Zeta"] = new[] { Import("Alpha"), Import("Beta") } });

        Assert.Equal(new[] { "Alpha", "Beta", "Zeta" }, graph.TopologicalOrder());
    }

    [Fact]
    public void SuppliedImportDependencies_AreRealImportEdgesOnly_NotUnprunedManagedRefs()
    {
        // Bridge is HANDED two supplied deps (Base, Unused) — both get a managed-package-reference edge
        // — but its interface imports only Base. The import-derived dependency map must list Base only:
        // the unpruned managed edge to Unused must NOT appear, else a corpus-wide union of these maps
        // (each primary handed every co-located sibling) would fabricate a mutual-dependency cycle.
        var inv = new InputInventory
        {
            Primary = Mod("Bridge", InputSource.Primary),
            Dependencies = new[]
            {
                Mod("Base", InputSource.ExplicitDependency, pkg: "SwiftBindings.Base"),
                Mod("Unused", InputSource.ExplicitDependency, pkg: "SwiftBindings.Unused"),
            },
        };
        var graph = Build(inv, new() { ["Bridge"] = new[] { Import("Base") } });

        var deps = graph.SuppliedImportDependencies();
        Assert.Equal(new[] { "Base" }, deps["Bridge"]);
        Assert.Empty(deps["Base"]);
        Assert.Empty(deps["Unused"]);
    }

    [Fact]
    public void TopologicalOrder_LonePrimary_OrdersToItself()
    {
        // A lone primary with no dependencies is the only supplied module, so it orders to just itself
        // and has no import dependencies.
        var graph = BindingInputGraph.Build(
            new InputInventory
            {
                Primary = Mod("Solo", InputSource.Primary),
                Dependencies = Array.Empty<InputModuleArtifacts>(),
            },
            readImportEdges: _ => Array.Empty<ImportEdge>(),
            classifyUnsupplied: _ => null);

        Assert.Equal(new[] { "Solo" }, graph.TopologicalOrder());
        Assert.Empty(graph.SuppliedImportDependencies()["Solo"]);
    }

    [Fact]
    public void TopologicalOrder_ImportCycle_FallsBackToLexicalOrder()
    {
        // A real Swift import graph cannot cycle, but the fallback must stay deterministic and total if
        // one ever appears: primary Alef imports Bet, supplied dep Bet imports Alef back. No dependency-
        // first order exists, so TopologicalOrder degrades to the lexical order of all supplied modules
        // (every module present exactly once) rather than throwing or dropping one.
        var inv = new InputInventory
        {
            Primary = Mod("Alef", InputSource.Primary),
            Dependencies = new[] { Mod("Bet", InputSource.ExplicitDependency) },
        };
        var graph = Build(inv, new()
        {
            ["Alef"] = new[] { Import("Bet") },
            ["Bet"] = new[] { Import("Alef") },
        });

        Assert.Equal(new[] { "Alef", "Bet" }, graph.TopologicalOrder());
    }
}

// ---------------------------------------------------------------------------------------------
// Closure preflight — adjudication of unresolved candidates via an injectable probe.
// ---------------------------------------------------------------------------------------------
public class BindingInputClosurePreflightTests
{
    private sealed class FakeProbe : IModuleImportProbe
    {
        private readonly Func<string, ImportProbeOutcome> _f;
        public FakeProbe(Func<string, ImportProbeOutcome> f) => _f = f;
        public ImportProbeOutcome Probe(string moduleName, IReadOnlyList<string> roots) => _f(moduleName);
    }

    // Builds an inventory whose primary swiftinterface (on disk) declares `@_exported import <missing>`.
    private static (InputInventory Inv, string Dir) PrimaryImporting(string missingModule, string? provenance = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ingestion-pf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var iface = Path.Combine(dir, "Bridge.swiftinterface");
        File.WriteAllText(iface, "// header\n@_exported import " + missingModule + "\n");

        var inv = new InputInventory
        {
            Primary = new InputModuleArtifacts
            {
                ModuleName = "Bridge",
                Source = InputSource.Primary,
                SwiftInterfacePath = iface,
                ProvenanceIdentity = provenance,
            },
            Dependencies = Array.Empty<InputModuleArtifacts>(),
        };
        return (inv, dir);
    }

    private static bool NoSdkModules(string _) => false;

    [Fact]
    public void ClassifyUnsupplied_RuntimeBuiltin_And_Sdk_And_Unresolved()
    {
        Assert.Equal(InputSource.RuntimeBuiltin, BindingInputClosurePreflight.ClassifyUnsupplied("Swift", _ => false));
        Assert.Equal(InputSource.RuntimeBuiltin, BindingInputClosurePreflight.ClassifyUnsupplied("_Concurrency", _ => false));
        Assert.Equal(InputSource.AppleSdk, BindingInputClosurePreflight.ClassifyUnsupplied("UIKit", m => m == "UIKit"));
        Assert.Null(BindingInputClosurePreflight.ClassifyUnsupplied("TotallyUnknown", _ => false));
    }

    [Fact]
    public void ProbeConfirmsMissing_ProducesObligationWithAllFields()
    {
        var (inv, dir) = PrimaryImporting("IngestionBase");
        try
        {
            var probe = new FakeProbe(m => ImportProbeOutcome.Missing(m)); // confirms the same name
            var verdict = BindingInputClosurePreflight.Run(inv, NoSdkModules, probe, NullLogger.Instance);

            Assert.False(verdict.IsClosed);
            var ob = Assert.Single(verdict.Obligations);
            Assert.Equal("IngestionBase", ob.MissingModule);
            Assert.Equal("Bridge", ob.ImporterModule);
            Assert.EndsWith("Bridge.swiftinterface", ob.InterfacePath);
            Assert.Equal(2, ob.Line);

            var report = ob.Format();
            Assert.Contains("IngestionBase", report);
            Assert.Contains("Bridge", report);
            Assert.Contains("required module not supplied; conversion provenance unavailable", report);
            Assert.Contains("@_exported", report);
            Assert.DoesNotContain("conversion failed to produce", report);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ProbeResolvable_DropsCandidate_GraphIsClosed()
    {
        var (inv, dir) = PrimaryImporting("SomeUncataloguedSdkModule");
        try
        {
            var probe = new FakeProbe(_ => ImportProbeOutcome.Resolvable);
            var verdict = BindingInputClosurePreflight.Run(inv, NoSdkModules, probe, NullLogger.Instance);

            Assert.True(verdict.IsClosed);
            Assert.Empty(verdict.Obligations);
            Assert.False(verdict.HadUnadjudicatedCandidates);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ProbeInconclusive_DefersToCompile_ClosedButUnadjudicated()
    {
        var (inv, dir) = PrimaryImporting("Mystery");
        try
        {
            var probe = new FakeProbe(_ => ImportProbeOutcome.Inconclusive);
            var verdict = BindingInputClosurePreflight.Run(inv, NoSdkModules, probe, NullLogger.Instance);

            Assert.True(verdict.IsClosed);
            Assert.Empty(verdict.Obligations);
            Assert.True(verdict.HadUnadjudicatedCandidates);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ProbeMissingDifferentName_DoesNotMisattribute()
    {
        // The probe reports a DIFFERENT (transitive) module missing than the candidate we asked about —
        // reporting the candidate would misattribute, so defer to the compile (advisory), no obligation.
        var (inv, dir) = PrimaryImporting("IngestionBase");
        try
        {
            var probe = new FakeProbe(_ => ImportProbeOutcome.Missing("SomeDeepTransitive"));
            var verdict = BindingInputClosurePreflight.Run(inv, NoSdkModules, probe, NullLogger.Instance);

            Assert.True(verdict.IsClosed);
            Assert.Empty(verdict.Obligations);
            Assert.True(verdict.HadUnadjudicatedCandidates);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NoProbe_DefersToCompile_ClosedButUnadjudicated()
    {
        var (inv, dir) = PrimaryImporting("IngestionBase");
        try
        {
            var verdict = BindingInputClosurePreflight.Run(inv, NoSdkModules, probe: null, NullLogger.Instance);

            Assert.True(verdict.IsClosed);
            Assert.True(verdict.HadUnadjudicatedCandidates);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Obligation_WithProvenanceIdentity_IncludesItReceiptNeutrally()
    {
        var (inv, dir) = PrimaryImporting("IngestionBase", provenance: "converter-run-7");
        try
        {
            var probe = new FakeProbe(m => ImportProbeOutcome.Missing(m));
            var verdict = BindingInputClosurePreflight.Run(inv, NoSdkModules, probe, NullLogger.Instance);

            var ob = Assert.Single(verdict.Obligations);
            Assert.Contains("converter-run-7", ob.Provenance);
            Assert.DoesNotContain("conversion failed to produce", ob.Provenance);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RunOrFail_ClosedGraph_ReturnsTrue()
    {
        var (inv, dir) = PrimaryImporting("IngestionBase");
        try
        {
            var probe = new FakeProbe(_ => ImportProbeOutcome.Resolvable);
            Assert.True(BindingInputClosurePreflight.RunOrFail(inv, NoSdkModules, probe, NullLogger.Instance));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RunOrFail_ProvenMissing_ReturnsFalse()
    {
        var (inv, dir) = PrimaryImporting("IngestionBase");
        try
        {
            var probe = new FakeProbe(m => ImportProbeOutcome.Missing(m));
            Assert.False(BindingInputClosurePreflight.RunOrFail(inv, NoSdkModules, probe, NullLogger.Instance));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RunOrFail_NoPrimarySwiftInterface_SkipsAdvisoryReturnsTrue()
    {
        var inv = new InputInventory
        {
            Primary = new InputModuleArtifacts { ModuleName = "Bridge", Source = InputSource.Primary, SwiftInterfacePath = null },
            Dependencies = Array.Empty<InputModuleArtifacts>(),
        };
        var probe = new FakeProbe(m => ImportProbeOutcome.Missing(m));
        Assert.True(BindingInputClosurePreflight.RunOrFail(inv, NoSdkModules, probe, NullLogger.Instance));
    }

    [Theory]
    [InlineData(null)]                 // no probe at all
    [InlineData("inconclusive")]       // probe cannot decide
    [InlineData("different-name")]     // probe reports a DIFFERENT transitive missing
    public void UnadjudicatedDeferral_RecordsInfoNotDegradation(string? mode)
    {
        // The fail-open contract: a candidate the preflight could NOT prove absent is deferred to the
        // wrapper compile. That deferral must NOT be recorded as a degradation — under --strict-inputs a
        // degradation escalates to a fatal SWIFTBIND027, which would turn the deliberate deferral into a
        // false early failure. Only a probe confirming the SAME module absent is a hard failure, and that
        // path is the SWIFTBIND119 obligation (never this one).
        var (inv, dir) = PrimaryImporting("IngestionBase");
        try
        {
            InputResolutionReport.Reset();
            IModuleImportProbe? probe = mode switch
            {
                null => null,
                "inconclusive" => new FakeProbe(_ => ImportProbeOutcome.Inconclusive),
                _ => new FakeProbe(_ => ImportProbeOutcome.Missing("SomeDeepTransitive")),
            };

            var verdict = BindingInputClosurePreflight.Run(inv, NoSdkModules, probe, NullLogger.Instance);

            Assert.True(verdict.IsClosed);
            Assert.True(verdict.HadUnadjudicatedCandidates);
            Assert.False(InputResolutionReport.HasDegradations);
            Assert.Contains(InputResolutionReport.Decisions,
                d => d.Severity == InputResolutionSeverity.Info && d.Category == InputResolutionCategory.Dependency);
        }
        finally
        {
            InputResolutionReport.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProvenMissingObligation_DoesNotRecordDegradation()
    {
        // The hard-failure path surfaces via the SWIFTBIND119 log, not the input-resolution report, so it
        // must not leave a --strict-inputs degradation behind that would double-fail as SWIFTBIND027.
        var (inv, dir) = PrimaryImporting("IngestionBase");
        try
        {
            InputResolutionReport.Reset();
            var probe = new FakeProbe(m => ImportProbeOutcome.Missing(m));
            var verdict = BindingInputClosurePreflight.Run(inv, NoSdkModules, probe, NullLogger.Instance);

            Assert.False(verdict.IsClosed);
            Assert.False(InputResolutionReport.HasDegradations);
        }
        finally
        {
            InputResolutionReport.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CollectFrameworkSearchRoots_IncludesSliceParentAndNestedFrameworks()
    {
        // The probe's -F roots must be a generous SUPERSET of the wrapper compile's: the slice dir, its
        // parent (sibling co-location), and each <slice>/*.framework/Frameworks nested dir.
        var root = Path.Combine(Path.GetTempPath(), "ingestion-roots-" + Guid.NewGuid().ToString("N"));
        var slice = Path.Combine(root, "ios-arm64-simulator");
        var nested = Path.Combine(slice, "Bridge.framework", "Frameworks");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(slice, "Base.framework")); // a sibling co-located under the slice
        try
        {
            var inv = new InputInventory
            {
                Primary = new InputModuleArtifacts
                {
                    ModuleName = "Bridge",
                    Source = InputSource.Primary,
                    FrameworkSearchPath = slice,
                },
                Dependencies = Array.Empty<InputModuleArtifacts>(),
            };

            var roots = BindingInputClosurePreflight.CollectFrameworkSearchRoots(inv);

            Assert.Contains(Path.GetFullPath(slice), roots);
            Assert.Contains(Path.GetFullPath(root), roots);   // slice parent (sibling co-location)
            Assert.Contains(Path.GetFullPath(nested), roots); // nested framework Frameworks dir
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
