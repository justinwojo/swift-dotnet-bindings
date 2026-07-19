// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// The parity-drift gate. It emits real binding csprojs that exercise every branch of the
    /// project emitter, reads back every element the emitter can write into a top-level
    /// PropertyGroup/ItemGroup, and asserts each is classified in
    /// <see cref="CSharpProbeParityChecklist.KnownCsprojElements"/>. So a newly emitted csproj
    /// property cannot silently diverge the in-process probe's compilation from the real build:
    /// adding one to the emitter without classifying it here fails this test and forces a
    /// deliberate MirroredInProbe / NotReproducibleInProcess decision.
    /// </summary>
    public class CSharpProbeParityDriftTests
    {
        [Fact]
        public void EveryEmittedCsprojElement_IsClassifiedInTheParityChecklist()
        {
            var emitted = new SortedSet<string>();
            foreach (var csproj in EmitAllBranchesAndCollectCsprojPaths())
            {
                foreach (var name in TopLevelConfigElementNames(csproj))
                    emitted.Add(name);
            }

            // Sanity: the maximal fixtures must actually have exercised the broad surface, otherwise
            // this gate would pass vacuously.
            Assert.Contains("OutputType", emitted);
            Assert.Contains("Compile", emitted);
            Assert.Contains("NativeReference", emitted);
            Assert.Contains("PackageReference", emitted);
            Assert.Contains("BundleResource", emitted);
            Assert.Contains("TargetsForTfmSpecificBuildOutput", emitted);

            var unclassified = emitted
                .Where(n => !CSharpProbeParityChecklist.KnownCsprojElements.ContainsKey(n))
                .ToList();

            Assert.True(
                unclassified.Count == 0,
                "The binding project emitter writes csproj element(s) with no parity classification: " +
                string.Join(", ", unclassified) +
                ". Add each to CSharpProbeParityChecklist.KnownCsprojElements with a deliberate " +
                "MirroredInProbe / NotReproducibleInProcess decision so the in-process probe can't " +
                "silently diverge from the real build.");
        }

        // Collect element names that are direct children of a top-level <PropertyGroup> or
        // <ItemGroup> (the csproj configuration surface). Elements nested inside a <Target> (pack-time
        // MSBuild machinery — MSBuild/Output/Error/BuildOutputInPackage) are intentionally excluded:
        // they never shape the C# compilation the probe reproduces.
        private static IEnumerable<string> TopLevelConfigElementNames(string csprojPath)
        {
            var doc = XDocument.Load(csprojPath);
            var root = doc.Root!;
            foreach (var group in root.Elements()
                         .Where(e => e.Name.LocalName is "PropertyGroup" or "ItemGroup"))
            {
                foreach (var child in group.Elements())
                    yield return child.Name.LocalName;
            }
        }

        private static IEnumerable<string> EmitAllBranchesAndCollectCsprojPaths()
        {
            var paths = new List<string>();

            // Fixture 1 — maximal: wrapper + dynamic source xcfw + dependency + Apple supplement +
            // resource bundle + ObjC companion, all on at once so the emit covers every branch.
            {
                var dir = CreateTempDir();
                var module = "Kitchen";
                var sourceXcfw = Path.Combine(dir, "..", $"{module}.xcframework");
                Directory.CreateDirectory(sourceXcfw);
                var wrapperXcfw = Path.Combine(dir, $"{module}SwiftBindings.xcframework");
                Directory.CreateDirectory(wrapperXcfw);

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = module,
                    Metadata = Meta(module),
                    SourceXCFrameworkPath = sourceXcfw,
                    SourceNativeLinkage = NativeLinkage.Dynamic,
                    WrapperXCFrameworkPath = wrapperXcfw,
                    // Published runtime version so the PackageReference path is taken too.
                    SwiftRuntimeVersion = "0.18.0",
                    Dependencies = new[]
                    {
                        new FrameworkDependencyInfo
                        {
                            XCFrameworkPath = "/path/Dep.xcframework",
                            ModuleName = "Dep",
                            PackageVersion = "1.2.3",
                        },
                    },
                    EmitsAppleSupplementReference = true,
                    AppleSupplementVersion = "26.0.0",
                    ResourceBundleNames = new[] { "KitchenResources" },
                    ObjCProjectFileName = "Kitchen.ObjC.Swift.iOS.csproj",
                }, NullLogger.Instance);

                paths.Add(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
            }

            // Fixture 2 — dev sentinel (default runtime version) so the ProjectReference runtime-wiring
            // branch is exercised too.
            {
                var dir = CreateTempDir();
                var module = "DevLocal";
                var sourceXcfw = Path.Combine(dir, "..", $"{module}.xcframework");
                Directory.CreateDirectory(sourceXcfw);
                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = module,
                    Metadata = Meta(module),
                    SourceXCFrameworkPath = sourceXcfw,
                    // SwiftRuntimeVersion left null -> dev sentinel -> ProjectReference + fallback PackageReference.
                }, NullLogger.Instance);
                paths.Add(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
            }

            return paths;
        }

        private static XCFrameworkMetadata Meta(string module) => new()
        {
            LibraryVersion = "1.0.0",
            PackageVersion = "1.0.0",
            IsVersionPlaceholder = false,
            MinimumOSVersion = "15.0",
            EffectiveMinimumOSVersion = "15.0",
            SdkVersion = null,
            ModuleName = module,
            Platforms = new List<string>(),
        };

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"paritydrift_{System.Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
