// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

// These use the real emitter, driver, fragment maps and re-emission. The compiler delegate is
// deliberately a model control; the retained Swift fixture supplies independent native/compiler proof.
[Collection("ReportCollector")]
public class NativeDefaultOverloadDriverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalWithdrawal_PreservesActualDefaultBundleAndManifest(bool freeFunction)
    {
        using var h = new Harness(freeFunction);
        h.Render();
        var trim = h.TrimUnit();
        var cs = h.Text(trim, OutputPlane.CSharp);
        var swift = h.Text(trim, OutputPlane.Swift);
        var manifest = Assert.Single(h.Context.ApiManifestEntries, e => e.Key.Contains("Enroll(string)"));
        Assert.Contains("public ", cs);
        Assert.Contains("LibraryImport", cs);
        Assert.Contains("_dbw_", swift);
        Assert.Contains("@_cdecl", swift);
        Assert.NotEqual(h.Original, trim);

        h.Render(h.Original);
        Assert.Equal(cs, h.Text(trim, OutputPlane.CSharp));
        Assert.Equal(swift, h.Text(trim, OutputPlane.Swift));
        Assert.DoesNotContain("public ", h.Text(h.Original, OutputPlane.CSharp));
        Assert.Contains("Withdrawn by wrapper verify-recover", h.CSharp);
        Assert.Equal(manifest.Value, h.Context.ApiManifestEntries[manifest.Key]);
        Assert.Single(h.Context.ApiManifestEntries, e => e.Key.Contains("Enroll("));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrimWithdrawal_PreservesOriginal_AndCannotReturnAfterOriginalWithdrawal(bool freeFunction)
    {
        using var h = new Harness(freeFunction);
        h.Render();
        var trim = h.TrimUnit();
        var primary = h.Text(h.Original, OutputPlane.Swift);
        Assert.Contains("@_cdecl", primary);

        h.Render(trim);
        Assert.Equal(primary, h.Text(h.Original, OutputPlane.Swift));
        Assert.DoesNotContain("LibraryImport", h.Text(trim, OutputPlane.CSharp));
        Assert.Empty(h.Text(trim, OutputPlane.Swift));
        Assert.DoesNotContain(h.Context.ApiManifestEntries, e => e.Key.Contains("Enroll(string)"));

        h.Render(trim, h.Original);
        Assert.DoesNotContain("LibraryImport", h.Text(trim, OutputPlane.CSharp));
        Assert.DoesNotContain("public ", h.Text(h.Original, OutputPlane.CSharp));
        Assert.Empty(h.Text(trim, OutputPlane.Swift));
        Assert.DoesNotContain(h.Context.ApiManifestEntries, e => e.Key.Contains("Enroll("));
    }

    [Fact]
    public void NoPriorCompletedProducer_DoesNotInventDefaultOverload()
    {
        using var h = new Harness(false);
        h.Render(h.Original);
        Assert.DoesNotContain("Enroll(", h.CSharp);
        Assert.DoesNotContain(h.Context.CompletedDefaultOverloadRecipes, e => e.Key == h.Original.Decl);
    }

    [Fact]
    public void ParentWithdrawal_DoesNotReplayChildren()
    {
        using var h = new Harness(false);
        h.Render();
        var trim = h.TrimUnit();
        h.Render(RecoveryUnitId.Create(DeclIdFactory.ForType(h.Registry), RecoveryScope.TypeSurface));
        Assert.DoesNotContain("Enroll(", h.CSharp);
        Assert.Empty(h.Text(trim, OutputPlane.Swift));
    }

    [Fact]
    public void CSharpDiagnosticOnTrim_AttributesItsOwnLeaf_AndWithdrawalKeepsPrimary()
    {
        using var h = new Harness(false);
        h.Render();
        var trim = h.TrimUnit();
        h.RejectCSharp = trim;
        var attribution = h.RenderRaw();
        Assert.NotNull(attribution);
        Assert.Equal(trim, Assert.Single(attribution!.Culprits));
        h.RejectCSharp = null;
        h.Render(trim);
        Assert.Contains("public ", h.Text(h.Original, OutputPlane.CSharp));
        Assert.DoesNotContain("LibraryImport", h.Text(trim, OutputPlane.CSharp));
        Assert.Contains("Withdrawn by C# verify-recover", h.CSharp);
    }

    [Fact]
    public void RecipeOutputs_AreRestoredWithAnAbandonedContextSnapshot()
    {
        using var h = new Harness(false);
        var baseline = ModuleEmissionStateSnapshot.Capture(h.Context);
        h.Render();
        Assert.Contains(h.Context.CompletedDefaultOverloadRecipes, e => e.Key == h.Original.Decl);
        baseline.Restore();
        Assert.Empty(h.Context.CompletedDefaultOverloadRecipes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CSharpDiagnosticOnOriginal_ReplaysOnlyItsPreviouslyCompletedNativeDefault(bool freeFunction)
    {
        using var h = new Harness(freeFunction);
        h.RejectCSharp = h.Original;
        var attribution = h.RenderRaw();
        Assert.NotNull(attribution);
        Assert.Equal(h.Original, Assert.Single(attribution!.Culprits));
        var trim = h.TrimUnit();
        var managed = h.Text(trim, OutputPlane.CSharp);
        var native = h.Text(trim, OutputPlane.Swift);
        Assert.Contains("LibraryImport", managed);
        Assert.Contains("@_cdecl", native);

        h.RejectCSharp = null;
        h.Render(h.Original);
        Assert.Equal(managed, h.Text(trim, OutputPlane.CSharp));
        Assert.Equal(native, h.Text(trim, OutputPlane.Swift));
        Assert.DoesNotContain("public ", h.Text(h.Original, OutputPlane.CSharp));
        Assert.Contains("Withdrawn by C# verify-recover", h.CSharp);
        Assert.Single(h.Context.ApiManifestEntries, e => e.Key.Contains("Enroll(string)"));
    }

    [Fact]
    public void LaterContainedFault_DiscardsCompletedRecipeBeforeRetryAndDriverPublication()
    {
        using var h = new Harness(false);
        var later = DeclIdFactory.ForMethod(h.Registry.Methods.Single(m => m.Name == "reset"));
        DefaultOverloadReplayRecipe? abandonedRecipe = null;
        bool retrySawCleanOutputs = false;
        int laterFaults = 0;
        using (EmitterFaultInjector.Install(id =>
        {
            if (id == h.Original.Decl && abandonedRecipe != null)
                retrySawCleanOutputs = !h.Context.CompletedDefaultOverloadRecipes.ContainsKey(h.Original.Decl);
            if (id != later)
                return null;
            // reset follows enroll in the actual fixture: prove its producer completed before
            // throwing, rather than merely manufacturing an empty-attempt rollback control.
            if (h.Context.CompletedDefaultOverloadRecipes.TryGetValue(h.Original.Decl, out var recipe))
                abandonedRecipe = recipe;
            laterFaults++;
            return new InvalidOperationException("Fault after completed native-default producer");
        }))
            h.Render();

        Assert.Equal(1, laterFaults);
        Assert.NotNull(abandonedRecipe);
        Assert.True(retrySawCleanOutputs);
        var completedRecipe = h.Context.CompletedDefaultOverloadRecipes[h.Original.Decl];
        Assert.NotSame(abandonedRecipe, completedRecipe);
        var trim = h.TrimUnit();
        var managed = h.Text(trim, OutputPlane.CSharp);
        Assert.Contains("LibraryImport", managed);

        // A subsequent compiler-withdrawal render must consume the completed retry's recipe,
        // never the first failed attempt's object retained prematurely by the driver.
        h.Render(h.Original);
        Assert.Same(completedRecipe, h.Context.CompletedDefaultOverloadRecipes[h.Original.Decl]);
        Assert.Equal(managed, h.Text(trim, OutputPlane.CSharp));
    }

    [Fact]
    public void BisectionOriginalWithdrawal_PreservesHealthyTrimInProbesAndSettledRender()
    {
        using var h = new Harness(false);
        h.Render();
        var trim = h.TrimUnit();
        var recipe = h.Context.CompletedDefaultOverloadRecipes[h.Original.Decl];
        h.ObservedTrim = trim;
        h.RejectOriginalWithoutAttribution = true;
        var failed = h.RenderRaw();
        Assert.NotNull(failed);
        Assert.True(failed!.HasUnattributedError);
        Assert.Empty(failed.Culprits);
        Assert.Contains(h.Original, h.CandidateUnits());
        // Keep this fixture within the real search's single-digit budget, without extending it
        // or selecting artificial candidates if the shared fixture grows beyond that bound.
        Assert.InRange(h.CandidateUnits().Count, 2, 32);
        h.CompileObservations.Clear();

        var outcome = h.AttemptBisection();
        Assert.True(outcome.DidIsolate);
        Assert.Equal(h.Original, Assert.Single(outcome.Isolated));
        Assert.InRange(outcome.ProbesUsed, 1, BoundedBisectionSearch.DefaultProbeBudget);
        var withdrawnProbes = h.CompileObservations.Where(o => !o.OriginalPresent).ToList();
        Assert.NotEmpty(withdrawnProbes);
        Assert.Contains(withdrawnProbes, o => o.TrimPresent);
        Assert.Contains(h.CompileObservations, o => o.OriginalPresent);

        h.Render(outcome.Isolated.ToArray());
        var settled = h.CompileObservations.Last();
        Assert.False(settled.OriginalPresent);
        Assert.True(settled.TrimPresent);
        Assert.Contains("LibraryImport", h.Text(trim, OutputPlane.CSharp));
        Assert.Single(h.Context.ApiManifestEntries, e => e.Key.Contains("Enroll(string)"));
        Assert.Single(h.Context.ApiManifestEntries, e => e.Key.Contains("Enroll("));
        Assert.Contains("bisection", h.CSharp);

        // Returning to an undiscarded original reads the pre-probe recipe, not an input
        // accidentally published by an intermediate probe. This is a model control, not a
        // claim that the real controller removes withdrawals from its monotonic denylist.
        h.RejectOriginalWithoutAttribution = false;
        h.Render();
        Assert.Same(recipe, h.Context.CompletedDefaultOverloadRecipes[h.Original.Decl]);
    }

    [Fact]
    public void BisectionTrimFailure_OriginalFirst_IsolatesTrimAndKeepsHealthyOriginalAndSibling()
    {
        using var h = new Harness(false);
        h.Render();
        var trim = h.TrimUnit();
        var originalCs = h.Text(h.Original, OutputPlane.CSharp);
        var originalSwift = h.Text(h.Original, OutputPlane.Swift);
        var sibling = RecoveryUnitId.Create(
            DeclIdFactory.ForMethod(h.Registry.Methods.Single(m => m.Name == "reset")), RecoveryScope.LeafApi);
        var siblingCs = h.Text(sibling, OutputPlane.CSharp);
        var siblingSwift = h.Text(sibling, OutputPlane.Swift);
        Assert.Contains("LibraryImport", originalCs);
        Assert.Contains("LibraryImport", siblingCs);
        Assert.Contains("@_cdecl", originalSwift);
        Assert.Contains("@_cdecl", siblingSwift);

        h.ObservedTrim = trim;
        h.RejectTrimWithoutAttribution = true;
        var failed = h.RenderRaw();
        Assert.NotNull(failed);
        Assert.True(failed!.HasUnattributedError);
        Assert.Empty(failed.Culprits);
        var candidates = h.CandidateUnits().ToList();
        Assert.Contains(h.Original, candidates);
        Assert.Contains(trim, candidates);
        Assert.True(candidates.IndexOf(h.Original) < candidates.IndexOf(trim),
            "the real producer must place the original before its independent default trim");
        Assert.InRange(candidates.Count, 2, 32);
        h.CompileObservations.Clear();

        var outcome = h.AttemptBisection();
        Assert.True(outcome.DidIsolate);
        Assert.Equal(trim, Assert.Single(outcome.Isolated));
        Assert.InRange(outcome.ProbesUsed, 1, BoundedBisectionSearch.DefaultProbeBudget);
        // Removing only the original must leave the failing trim visible. Conversely, the
        // sufficiency probe must keep the original while removing the actual failing trim.
        Assert.Contains(h.CompileObservations, o => !o.OriginalPresent && o.TrimPresent);
        Assert.Contains(h.CompileObservations, o => o.OriginalPresent && !o.TrimPresent);

        h.Render(outcome.Isolated.ToArray());
        Assert.Equal(originalCs, h.Text(h.Original, OutputPlane.CSharp));
        Assert.Equal(originalSwift, h.Text(h.Original, OutputPlane.Swift));
        Assert.Equal(siblingCs, h.Text(sibling, OutputPlane.CSharp));
        Assert.Equal(siblingSwift, h.Text(sibling, OutputPlane.Swift));
        Assert.DoesNotContain("LibraryImport", h.Text(trim, OutputPlane.CSharp));
        Assert.Empty(h.Text(trim, OutputPlane.Swift));
        Assert.DoesNotContain(h.Context.ApiManifestEntries, e => e.Key.Contains("Enroll(string)"));
        Assert.Single(h.Context.ApiManifestEntries, e => e.Key.Contains("Enroll("));
        Assert.Contains("bisection", h.CSharp);
    }

    [Theory]
    [InlineData((int)EmitterFaultOrigin.EmitterException, true, false)]
    [InlineData((int)EmitterFaultOrigin.AbiRecoveryWithdrawal, true, false)]
    [InlineData((int)EmitterFaultOrigin.IngestionWithdrawal, true, false)]
    [InlineData((int)EmitterFaultOrigin.BisectionIsolatedWithdrawal, true, true)]
    [InlineData((int)EmitterFaultOrigin.RecoveryWithdrawal, true, true)]
    [InlineData((int)EmitterFaultOrigin.CSharpRecoveryWithdrawal, true, true)]
    [InlineData((int)EmitterFaultOrigin.BisectionIsolatedWithdrawal, false, false)]
    [InlineData((int)EmitterFaultOrigin.RecoveryWithdrawal, false, false)]
    [InlineData((int)EmitterFaultOrigin.CSharpRecoveryWithdrawal, false, false)]
    public void ReplayOriginGate_RequiresAllowedOriginAndCompletedRecipe(int originValue, bool hasRecipe, bool mayReplay)
    {
        using var h = new Harness(false);
        h.Render();
        var source = h.Registry.Methods.Single(m => m.Name == "enroll");
        Assert.Equal(h.Original.Decl, DeclIdFactory.ForMethod(source));
        var context = new ModuleEmissionContext();
        if (hasRecipe)
            context.SeedDefaultOverloadRecipes(h.Context.CompletedDefaultOverloadRecipes);
        var database = FixtureModuleFactory.BuildTypeDatabase(source.ModuleDecl!);
        database.AsyncLibraryName = "ContainmentFixtureSwiftBindings";
        var csText = new StringWriter();
        var swiftText = new StringWriter();
        var signatures = new HashSet<string>();
        var shapes = new Dictionary<string, int>();
        var origin = (EmitterFaultOrigin)originValue;
        using var attempt = EmissionAttempt.Begin(WrapperDenylistSeed.Build(
            new HashSet<RecoveryUnitId> { h.Original }, _ => origin));
        // This is the production gate with actual prior producer inputs when supplied.
        // The allowed origins remain explicit; absent recipes and other origins still refuse.
        ReportCollector.Reset();
        try
        {
            NativeDefaultOverloadRecovery.TryEmit(source, new CSharpWriter(csText), new SwiftWriter(swiftText),
                database, TypeHandlerContext.Empty with { EmissionContext = context },
                siblingPropertyNames: null, signatures, shapes, NullLogger.Instance);
        }
        finally { ReportCollector.Complete(); ReportCollector.Reset(); }
        Assert.True(EmissionAttempt.TryGetFault(h.Original.Decl, out var originalFault));
        Assert.Equal(origin, originalFault.Origin);
        if (mayReplay)
        {
            Assert.Contains("LibraryImport", csText.ToString());
            Assert.Contains("@_cdecl", swiftText.ToString());
            Assert.NotEmpty(signatures);
            Assert.Contains(context.CompletedDefaultOverloadRecipes, e => e.Key == h.Original.Decl);
        }
        else
        {
            Assert.Empty(csText.ToString());
            Assert.Empty(swiftText.ToString());
            Assert.Empty(signatures);
            Assert.Empty(shapes);
            Assert.Empty(context.CompletedDefaultOverloadRecipes);
            Assert.Empty(context.AbiCallPlans);
            Assert.Empty(context.ApiManifestEntries);
        }
    }

    [Fact]
    public void ConstructorPrimaryAndNativeDefault_AreIndependentCompletedCandidates()
    {
        using var h = new Harness(false, constructor: true);
        h.Render();
        var trim = h.TrimUnit();
        Assert.NotEqual(h.Original, trim);
        foreach (var unit in new[] { h.Original, trim })
        {
            Assert.Contains(unit, h.CandidateUnits());
            Assert.Contains(h.Context.FragmentSet!.AllFragments, f => f.Owner.Unit == unit
                && f.Plane == OutputPlane.CSharp && f.IsWholeScope && f.Text.Contains("LibraryImport"));
            Assert.Contains("@_cdecl", h.Text(unit, OutputPlane.Swift));
            Assert.Contains(h.Context.AbiCallPlans, plan => plan.Owner?.Decl == unit.Decl);
        }
        var nativeTrim = h.Text(trim, OutputPlane.Swift);
        Assert.Contains("_dbw_", nativeTrim);
        var managedTrim = h.Text(trim, OutputPlane.CSharp);

        h.Render(trim);
        Assert.Contains(h.Original, h.CandidateUnits());
        Assert.Contains("LibraryImport", h.Text(h.Original, OutputPlane.CSharp));
        Assert.DoesNotContain("LibraryImport", h.Text(trim, OutputPlane.CSharp));
        Assert.Empty(h.Text(trim, OutputPlane.Swift));

        // This harness can select a different model denial on a subsequent render. Both
        // directions use the same captured constructor producer recipe and real driver.
        h.Render(h.Original);
        Assert.Equal(managedTrim, h.Text(trim, OutputPlane.CSharp));
        Assert.Equal(nativeTrim, h.Text(trim, OutputPlane.Swift));
        Assert.DoesNotContain("LibraryImport", h.Text(h.Original, OutputPlane.CSharp));
    }

    [Fact]
    public void AccessorPrimaryScopes_PreserveTheEnclosingPropertyOwner()
    {
        using var h = new Harness(false);
        h.Render();
        var property = h.Registry.Properties.Single(p => p.Name == "name");
        var propertyId = DeclIdFactory.ForProperty(property);
        var group = RecoveryUnitId.ForAccessorGroup(propertyId);
        Assert.Equal(2, property.Accessors.Count);
        Assert.All(property.Accessors, a => Assert.True(a.Method.WasEmitted));
        Assert.Contains(group, h.CandidateUnits());
        Assert.Contains(h.Context.FragmentSet!.AllFragments, f => f.Owner.Unit == group
            && f.Plane == OutputPlane.CSharp && f.IsWholeScope && f.Text.Contains("LibraryImport"));
        Assert.Contains(h.Context.AbiCallPlans, plan => plan.Owner?.Decl == propertyId);
        var accessorIds = property.Accessors.Select(a => DeclIdFactory.ForMethod(a.Method)).ToHashSet();
        Assert.DoesNotContain(h.CandidateUnits(), u => accessorIds.Contains(u.Decl));
        Assert.DoesNotContain(h.Context.FragmentSet.AllFragments, f => accessorIds.Contains(f.Owner.Artifact.Decl));
        Assert.DoesNotContain(h.Context.AbiCallPlans, plan => plan.Owner is { } owner && accessorIds.Contains(owner.Decl));
        var otherPrimary = h.Text(h.Original, OutputPlane.CSharp);
        h.Render(group);
        Assert.DoesNotContain("LibraryImport", h.Text(group, OutputPlane.CSharp));
        Assert.Empty(h.Text(group, OutputPlane.Swift));
        Assert.Equal(otherPrimary, h.Text(h.Original, OutputPlane.CSharp));
    }

    private sealed class Harness : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "native-default-recovery-" + Guid.NewGuid().ToString("N"));
        private readonly InEmissionDriver driver;
        public ModuleEmissionContext Context { get; } = new();
        public TypeDecl Registry { get; }
        public RecoveryUnitId Original { get; }
        public RecoveryUnitId? RejectCSharp { get; set; }
        public bool RejectOriginalWithoutAttribution { get; set; }
        public bool RejectTrimWithoutAttribution { get; set; }
        public RecoveryUnitId? ObservedTrim { get; set; }
        public List<(bool OriginalPresent, bool TrimPresent)> CompileObservations { get; } = new();
        public string CSharp => string.Concat(Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).OrderBy(p => p).Select(File.ReadAllText));

        public Harness(bool freeFunction, bool constructor = false)
        {
            Directory.CreateDirectory(directory);
            var module = FixtureModuleFactory.BuildModule("ContainmentFixture");
            Registry = module.Types.Single(t => t.Name == "Registry");
            var method = Registry.Methods.Single(m => m.Name == "enroll");
            if (constructor)
            {
                // Model a normal allocating class initializer using the shared declaration
                // factory; native/compiler identity qualification belongs to the real fixture.
                Registry.Methods.Remove(method);
                method = TestDecls.Method("init", isConstructor: true,
                    returnType: new NamedTypeSpec(Registry.SwiftTypeName.ModuleQualifiedName),
                    parameters: new[] { TestDecls.Param("subject", new NamedTypeSpec("Swift.String")),
                        TestDecls.Param("tag", new NamedTypeSpec("Swift.Int"), hasDefault: true) },
                    module: module.Name);
                method.ParentDecl = Registry;
                method.ModuleDecl = module;
                foreach (var arg in method.CSSignature)
                {
                    arg.ParentDecl = method;
                    arg.ModuleDecl = module;
                }
                Registry.Methods.Add(method);
            }
            if (freeFunction)
            {
                Registry.Methods.Remove(method);
                method.ParentDecl = module;
                method.MethodType = MethodType.Static;
                module.Methods.Add(method);
            }
            Original = RecoveryUnitId.Create(DeclIdFactory.ForMethod(method), RecoveryScope.LeafApi);
            var database = FixtureModuleFactory.BuildTypeDatabase(module);
            database.AsyncLibraryName = "ContainmentFixtureSwiftBindings";
            void Rebuild()
            {
                var engine = new ConcreteSpecializationEngine(database, module.Name);
                engine.IndexModuleConformances(module);
                Context.SpecializationEngine = engine;
                Context.Marshaling = new MarshalingContext(module, database, engine) { EmissionContext = Context };
            }
            driver = new InEmissionDriver(module, Context, database, NullLogger.Instance,
                () => new StringEmitter(directory, database, new NullLoggerFactory()), Rebuild,
                _ => Compile(),
                new WrapperRecoveryCompileRequest(directory, null, null, null,
                    new DepModuleCollisionDetector.SlicedCollisionResult(Array.Empty<string>(), Array.Empty<string>())),
                preRender: () =>
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                        File.Delete(file);
                },
                verifyCsharp: _ => Verify());
        }

        public RecoveryUnitId TrimUnit() => Assert.Single(Context.FragmentSet!.EmittedArtifacts
            // DeclId preserves the complete CSSignature, including its return entry.
            .Where(a => a.Decl.Name == Original.Decl.Name && a.Decl.ParameterTypes.Length == 2)
            .Select(a => RecoveryUnitClassifier.ClassifyArtifact(a).Unit).Distinct());

        public string Text(RecoveryUnitId unit, OutputPlane plane) => string.Concat(Context.FragmentSet!.Files
            .OrderBy(e => e.Key).SelectMany(e => e.Value.Intervals)
            .Where(i => i.Fragment.Owner.Unit == unit && i.Fragment.Plane == plane)
            .Select(i => i.Fragment.Text));

        public void Render(params RecoveryUnitId[] denied) => Assert.Null(RenderRaw(denied));

        public AttributionResult? RenderRaw(params RecoveryUnitId[] denied)
        {
            ReportCollector.Reset();
            try { return driver.RenderCompileAttribute(denied.ToHashSet()); }
            finally { ReportCollector.Complete(); ReportCollector.Reset(); }
        }

        public IReadOnlyList<RecoveryUnitId> CandidateUnits() => driver
            .BuildBisectionCandidateGroups(new HashSet<RecoveryUnitId>()).SelectMany(g => g).ToList();

        public BisectionOutcome AttemptBisection()
        {
            ReportCollector.Reset();
            try { return driver.AttemptBisection(new HashSet<RecoveryUnitId>()); }
            finally { ReportCollector.Complete(); ReportCollector.Reset(); }
        }

        private WrapperCompileDiagnostics Compile()
        {
            bool originalPresent = Text(Original, OutputPlane.Swift).Contains("@_cdecl", StringComparison.Ordinal);
            bool trimPresent = ObservedTrim is { } trim && Text(trim, OutputPlane.Swift).Contains("@_cdecl", StringComparison.Ordinal);
            CompileObservations.Add((originalPresent, trimPresent));
            if ((RejectOriginalWithoutAttribution && originalPresent)
                || (RejectTrimWithoutAttribution && trimPresent))
            {
                var diagnostic = new DiagnosticGroup
                {
                    Primary = CompilerDiagnostic.Global(DiagnosticSeverity.Error, "Injected unattributed model failure"),
                };
                return WrapperCompileDiagnostics.Failed(
                    new[] { new WrapperSliceDiagnostics("model", false, new[] { diagnostic }) },
                    Array.Empty<WrapperFileProvenance>());
            }
            if (RejectOriginalWithoutAttribution || RejectTrimWithoutAttribution)
            {
                // Do not let a probe that removed every native function pass vacuously.
                // This is a source-surface model; real compiler provenance remains separate.
                bool hasNativeSurface = Context.FragmentSet!.Files.SelectMany(f => f.Value.Intervals)
                    .Any(i => i.Fragment.Plane == OutputPlane.Swift
                        && (i.Fragment.Text.Contains("@_cdecl", StringComparison.Ordinal)
                            || i.Fragment.Text.Contains("@_silgen_name", StringComparison.Ordinal)));
                if (!hasNativeSurface)
                    return WrapperCompileDiagnostics.NoWrapperSurfaceOutcome(
                        "Model probe removed all native function surface", null, Array.Empty<WrapperFileProvenance>());
            }
            return WrapperCompileDiagnostics.Clean(null, Array.Empty<WrapperSliceDiagnostics>(), Array.Empty<WrapperFileProvenance>());
        }

        private CSharpVerificationResult Verify()
        {
            if (RejectCSharp is not { } target)
                return new CSharpVerificationResult(CSharpVerificationOutcome.Clean, Array.Empty<CSharpCompileDiagnostic>());
            var hit = Context.FragmentSet!.Files.SelectMany(file => file.Value.Intervals
                .Where(i => i.Fragment.Plane == OutputPlane.CSharp && i.Fragment.Owner.Unit == target && i.Length > 0)
                .Select(i => (file.Key, Interval: i))).First();
            var path = Path.Combine(directory, hit.Key);
            var text = File.ReadAllText(path);
            var line = text.AsSpan(0, hit.Interval.Start).Count('\n') + 1;
            var lastNewline = hit.Interval.Start == 0 ? -1 : text.LastIndexOf('\n', hit.Interval.Start - 1);
            var column = hit.Interval.Start - lastNewline;
            return new CSharpVerificationResult(CSharpVerificationOutcome.CompileErrors,
                new[] { new CSharpCompileDiagnostic("CS0246", CSharpDiagnosticSeverity.Error, path,
                    line, column, line, column + 1, "Injected trim-only compiler failure") });
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
