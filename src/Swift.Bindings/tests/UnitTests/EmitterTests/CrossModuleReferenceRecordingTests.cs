// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Covers the rule that the emitted csproj's reference set is derived from the projections
    /// the emitted C# actually names — the shared mechanism behind two shipped defects: a binding
    /// that named <c>Swift.Foundation.Measurement&lt;T&gt;</c> without referencing
    /// <c>SwiftBindings.Apple</c>, and one that named <c>Swift.RealityFoundation.Entity</c>
    /// without referencing <c>SwiftBindings.Apple.RealityFoundation</c>. Both produced C# that the
    /// consumer could not compile (CS0234) while the generator exited 0.
    /// </summary>
    public class SupplementNamespaceOwnershipTests
    {
        [Theory]
        // Exact namespace matches.
        [InlineData("Swift.Foundation", true)]
        [InlineData("Swift.CryptoKit", true)]
        [InlineData("Swift.SwiftUI", true)]
        [InlineData("Swift.ActivityKit", true)]
        [InlineData("Swift.ManagedSettings", true)]
        // Nested namespaces: a manifest entry whose declaration path folds into the namespace
        // (Swift.CryptoKit.P256.Signing.ECDSASignature) still needs the supplement package.
        [InlineData("Swift.Foundation.Locale", true)]
        [InlineData("Swift.CryptoKit.P256.Signing", true)]
        // The bare Swift namespace is SwiftBindings.Runtime's too and is always referenced, so it
        // carries no supplement signal. Treating it as owned would attach a supplement reference
        // to every binding that names any runtime type — i.e. all of them.
        [InlineData("Swift", false)]
        // A dot is required at the boundary: these are unrelated namespaces that merely share a
        // textual prefix with an owned one.
        [InlineData("Swift.FoundationModels", false)]
        [InlineData("Swift.SwiftUIX", false)]
        // Unrelated projections.
        [InlineData("Foundation", false)]
        [InlineData("System", false)]
        [InlineData("MapLibre", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsSupplementOwnedNamespace_ClassifiesByNamespaceBoundary(string? ns, bool expected)
        {
            Assert.Equal(expected, AppleSupplementResolver.IsSupplementOwnedNamespace(ns));
        }

        [Fact]
        public void OwnedNamespaces_MatchesWhatTheSupplementActuallyDeclares()
        {
            // The ownership list is hand-maintained, and the cost of it drifting is silent: a
            // namespace the supplement gains but this list misses produces a binding that names
            // the supplement without referencing it, which fails only in the CONSUMER's build.
            // So derive the truth from both places the supplement's surface comes from — its
            // hand-written sources, and the manifest the build-time emitter turns into more
            // sources — and require an exact match.
            var repoRoot = LocateRepoRoot();

            var declared = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var dir in new[] { "Sources", "Shims" })
            {
                var path = Path.Combine(repoRoot, "src", "Swift.Bindings.Apple", dir);
                if (!Directory.Exists(path))
                    continue;
                foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                {
                    foreach (Match m in Regex.Matches(
                        File.ReadAllText(file), @"^\s*namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Multiline))
                    {
                        declared.Add(m.Groups[1].Value);
                    }
                }
            }

            var manifestPath = Path.Combine(
                repoRoot, "src", "Swift.Bindings.Sdk", "tools", "apple-types-manifest", "manifest.json");
            Assert.True(File.Exists(manifestPath), $"Apple types manifest not found at {manifestPath}");
            var manifestNamespaces = ((JContainer)JToken.Parse(File.ReadAllText(manifestPath)))
                .DescendantsAndSelf()
                .OfType<JProperty>()
                .Where(p => p.Name.Contains("namespace", StringComparison.OrdinalIgnoreCase) &&
                            p.Value.Type == JTokenType.String)
                .Select(p => p.Value.Value<string>())
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
            Assert.NotEmpty(manifestNamespaces);
            foreach (var ns in manifestNamespaces)
                declared.Add(ns!);

            // The bare Swift namespace is excluded by design — see IsSupplementOwnedNamespace.
            declared.Remove("Swift");

            Assert.Equal(
                declared.ToArray(),
                AppleSupplementResolver.OwnedNamespaces.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        private static string LocateRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }

    /// <summary>
    /// The two-arm recorder that turns a resolved <see cref="TypeRecord"/> into csproj references.
    /// Both collectors are <c>[ThreadStatic]</c>, so each test resets them on entry and exit.
    /// </summary>
    public class ResolvedReferenceRecorderTests : IAsyncLifetime
    {
        public Task InitializeAsync()
        {
            AppleSupplementReferences.Reset();
            CrossModuleBindingReferences.Reset();
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => InitializeAsync();

        [Fact]
        public void Record_HandRolledSupplementCanonical_RecordsSupplementReference()
        {
            // Foundation.Measurement is the shape that produced the shipped defect: it is a
            // hand-rolled canonical compiled straight into Swift.Bindings.Apple/Sources/, and is
            // therefore DELIBERATELY absent from the manifest. Keying the supplement reference off
            // manifest membership misses it; keying off the resolved projection catches it.
            ResolvedReferenceRecorder.Record(
                Record("Foundation.Measurement", "Swift.Foundation", "Measurement"), "test");

            Assert.True(AppleSupplementReferences.Any);
            Assert.Contains("Foundation.Measurement", AppleSupplementReferences.Current);
        }

        [Fact]
        public void Record_SameSwiftModuleDifferentProjection_DoesNotRecordSupplement()
        {
            // The oracle must be the projection, not the module. FoundationDatabase.xml projects
            // Foundation identities into four different managed homes, so "the type came from
            // Foundation" says nothing about which assembly the consumer must reference.
            // Foundation.NSOperationQueue lands in the ObjC workload's Foundation namespace,
            // which ships with Microsoft.iOS and needs no supplement reference.
            ResolvedReferenceRecorder.Record(
                Record("Foundation.NSOperationQueue", "Foundation", "NSOperationQueue"), "test");
            // Foundation.Date is a legacy canonical pinned to SwiftBindings.Runtime.
            ResolvedReferenceRecorder.Record(
                Record("Foundation.Date", "Swift", "SwiftDate"), "test");

            Assert.False(AppleSupplementReferences.Any);
        }

        [Fact]
        public void Record_AlwaysRecordsTheDeclaringSwiftModule()
        {
            // Arm 2 records unconditionally and defers filtering to read time, where
            // AppleFrameworkImportDetector.ResolveDependencies drops the module being generated
            // and every module with no registered binding package.
            ResolvedReferenceRecorder.Record(
                Record("RealityFoundation.Entity", "Swift.RealityFoundation", "Entity"), "test");
            ResolvedReferenceRecorder.Record(
                Record("Foundation.Measurement", "Swift.Foundation", "Measurement"), "test");

            Assert.Equal(
                new[] { "Foundation", "RealityFoundation" },
                CrossModuleBindingReferences.Current.ToArray());
        }

        [Fact]
        public void Record_NullRecord_IsIgnored()
        {
            // Skip-style resolution results carry a null record; recording must be a no-op rather
            // than throwing on a path every successful resolution runs through.
            ResolvedReferenceRecorder.Record(null, "test");

            Assert.False(AppleSupplementReferences.Any);
            Assert.False(CrossModuleBindingReferences.Any);
        }

        [Fact]
        public void CrossModuleBindingReferences_RoundTrip_DedupesAndResets()
        {
            Assert.False(CrossModuleBindingReferences.Any);

            CrossModuleBindingReferences.Record("RealityFoundation", "test:A");
            CrossModuleBindingReferences.Record("RealityFoundation", "test:B");
            CrossModuleBindingReferences.Record("Foundation", "test:C");
            CrossModuleBindingReferences.Record(null, "test:D");
            CrossModuleBindingReferences.Record("", "test:E");

            Assert.Equal(
                new[] { "Foundation", "RealityFoundation" },
                CrossModuleBindingReferences.Current.ToArray());

            CrossModuleBindingReferences.Reset();
            Assert.False(CrossModuleBindingReferences.Any);
        }

        private static TypeRecord Record(string swiftIdentity, string managedNamespace, string managedName) =>
            new()
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(managedNamespace, managedName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftIdentity),
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct,
            };
    }

    /// <summary>
    /// The third reference chokepoint: <c>TypeProjectionFactory.TryProjectConcreteClassFallback</c>,
    /// which names a foreign module's class WITHOUT a <see cref="TypeRecord"/> — so neither
    /// <c>TypeResolver.TryResolve</c> nor the database cascade fires, and the two recorded chokepoints
    /// never see it.
    /// </summary>
    /// <remarks>
    /// <c>RealityFoundation</c> is the shape that makes this consequential rather than theoretical: in
    /// <c>apple-frameworks.json</c> it carries BOTH <c>concreteClassFallback: true</c> and
    /// <c>packageId: SwiftBindings.Apple.RealityFoundation</c>. A generator run without
    /// <c>RealityFoundationDatabase.xml</c> leaves <c>Entity</c> unresolved, routes through the
    /// fallback, emits public C# naming <c>RealityFoundation.Entity</c>, and — before the fix — recorded
    /// nothing, so the csproj shipped with no <c>PackageReference</c> and the consumer hit CS0246.
    /// An empty database is exactly that reach, not an artificial one.
    /// </remarks>
    public class ConcreteClassFallbackReferenceRecordingTests : IAsyncLifetime
    {
        private readonly TypeProjectionFactory _factory = new();

        public Task InitializeAsync()
        {
            AppleSupplementReferences.Reset();
            CrossModuleBindingReferences.Reset();
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => InitializeAsync();

        [Theory]
        // Both callers of the fallback: the Optional path and the collection-element path. The
        // recording sits below the shared guard chain so neither can regress independently.
        [InlineData("Swift.Optional")]
        [InlineData("Swift.Array")]
        public void Project_ConcreteClassFallback_RecordsTheSiblingBindingPackage(string container)
        {
            var typeSpec = new NamedTypeSpec(container, new NamedTypeSpec("RealityFoundation.Entity"));

            var projection = _factory.Project(typeSpec, CreateContext());

            Assert.NotNull(projection);
            Assert.Contains("RealityFoundation", CrossModuleBindingReferences.Current);
        }

        [Fact]
        public void Project_ConcreteClassFallback_DoesNotRecordASupplementReference()
        {
            // Arm 1 is deliberately absent from RecordUnresolvedModuleReference: a fallback yields no
            // resolved projection namespace to key on, and no concreteClassFallback module projects
            // into a supplement-owned namespace. If one is ever added that does, this goes red and the
            // supplement arm is owed.
            _factory.Project(
                new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("RealityFoundation.Entity")),
                CreateContext());

            Assert.False(AppleSupplementReferences.Any);
        }

        [Fact]
        public void Project_LocalUnresolvedClass_RecordsNothing()
        {
            // The fallback is registry-gated, so a non-Apple module never reaches the recorder.
            // Recording every unresolved name would flood the csproj with references to packages
            // that do not exist.
            _factory.Project(
                new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Widget")),
                CreateContext());

            Assert.False(CrossModuleBindingReferences.Any);
        }

        private static ProjectionContext CreateContext() =>
            new() { TypeDatabase = new EmptyTypeDatabase(), IsParameter = false };

        /// <summary>An empty database — every lookup misses, forcing the fallback.</summary>
        private sealed class EmptyTypeDatabase : ITypeDatabase
        {
            public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;

            public bool TryGetTypeRecord(
                SwiftTypeName swiftTypeName,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TypeRecord? record)
            {
                record = null;
                return false;
            }

            public string GetLibraryPath(string moduleName) => "";
            public string? AsyncLibraryName => null;
            public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
        }
    }

    /// <summary>
    /// The csproj side: sibling Apple binding packages become <c>PackageReference</c> items.
    /// </summary>
    public class BindingProjectSiblingReferenceTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_SiblingAppleBindingPackage_EmitsBoundedPackageReference()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "RealityKit", new[]
                {
                    new DetectedAppleFrameworkDependency
                    {
                        ModuleName = "RealityFoundation",
                        PackageId = "SwiftBindings.Apple.RealityFoundation",
                        VersionRange = "[26.2.8,26.3.0)",
                    },
                });

                Assert.Contains(
                    "<PackageReference Include=\"SwiftBindings.Apple.RealityFoundation\" Version=\"[26.2.8,26.3.0)\" />",
                    content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_NoSiblingPackages_EmitsNoAppleSiblingReference()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "RealityKit", siblings: null);
                Assert.DoesNotContain("SwiftBindings.Apple.", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SiblingAlreadyCoveredByDependency_EmitsOnePackageReference()
        {
            // NuGet errors on a duplicate PackageReference, so the two reference sources must not
            // both emit the same package id. Only reachable when an xcframework-mode dependency
            // resolves to an Apple binding package id, but the failure mode is a hard restore
            // error rather than a warning, so the dedup is asserted rather than assumed.
            var dir = CreateTempDir();
            try
            {
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/path/to/RealityFoundation.xcframework",
                        ModuleName = "RealityFoundation",
                        PackageId = "SwiftBindings.Apple.RealityFoundation",
                        PackageVersion = "26.2.8",
                        IsObjCOnly = false,
                    },
                };
                var siblings = new[]
                {
                    new DetectedAppleFrameworkDependency
                    {
                        ModuleName = "RealityFoundation",
                        PackageId = "SwiftBindings.Apple.RealityFoundation",
                        VersionRange = "[26.2.8,26.3.0)",
                    },
                };

                var content = EmitAndRead(dir, "RealityKit", siblings, deps);

                var occurrences = Regex.Matches(
                    content,
                    "<PackageReference Include=\"SwiftBindings\\.Apple\\.RealityFoundation\"").Count;
                Assert.Equal(1, occurrences);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitAndRead(
            string dir,
            string module,
            IReadOnlyList<DetectedAppleFrameworkDependency>? siblings,
            IReadOnlyList<FrameworkDependencyInfo>? dependencies = null)
        {
            var sourceXcfwPath = Path.Combine(dir, "..", $"{module}.xcframework");
            Directory.CreateDirectory(sourceXcfwPath);

            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = new XCFrameworkMetadata
                {
                    LibraryVersion = "1.0.0",
                    PackageVersion = "1.0.0",
                    IsVersionPlaceholder = false,
                    MinimumOSVersion = "15.0",
                    EffectiveMinimumOSVersion = "15.0",
                    SdkVersion = null,
                    ModuleName = module,
                    Platforms = new List<string>(),
                },
                SourceXCFrameworkPath = sourceXcfwPath,
                Dependencies = dependencies,
                AppleSiblingPackageReferences = siblings,
            }, _logger);

            return File.ReadAllText(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_sibling_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
