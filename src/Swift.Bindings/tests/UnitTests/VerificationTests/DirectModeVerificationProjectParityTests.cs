// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// The soundness premise of verifying an Apple system-framework binding INSIDE the verify-recover
    /// loop: the project the loop compiles each round is emitted separately from the project the
    /// binding actually ships, and the loop's verdict is only meaningful if the two present the same
    /// managed compilation. The in-loop project deliberately omits the native and packaging inputs —
    /// the wrapper/bridge NativeReferences (the wrapper is not built yet mid-loop) and any resource
    /// bundles — on the claim that a managed C# compile cannot see them.
    ///
    /// This pins that claim on the emitter: emit both shapes from otherwise identical direct-mode
    /// inputs and assert every managed-compilation input agrees — all project properties, and the
    /// Compile / PackageReference / ProjectReference / Reference / AssemblyAttribute item sets. Only
    /// native and packaging items may differ. If a future emitter change lets one of the omitted
    /// inputs move a Compile item, a package reference, or a property, the loop would start grading a
    /// project the consumer never builds, and this goes red.
    /// </summary>
    public class DirectModeVerificationProjectParityTests
    {
        // Items a managed C# compile actually consumes. Everything else a binding csproj carries is
        // native linkage or packaging payload, which is where the two shapes are allowed to diverge.
        private static readonly HashSet<string> ManagedCompileItemNames = new()
        {
            "Compile", "PackageReference", "ProjectReference", "Reference", "AssemblyAttribute",
        };

        [Fact]
        public void InLoopVerificationProject_AndShippedDirectProject_PresentTheSameManagedCompile()
        {
            const string module = "SystemFrameworkParity";

            // The in-loop verification shape: no source xcframework (direct mode resolves the binary
            // through dyld), no wrapper/bridge yet, no resource bundles, no ObjC companion.
            var verificationDir = CreateTempDir();
            BindingProjectEmitter.Emit(DirectModeOptions(verificationDir, module), NullLogger.Instance);

            // The shipped shape: same managed inputs, plus every native/packaging input the loop omits.
            var shippedDir = CreateTempDir();
            var wrapperXcfw = Path.Combine(shippedDir, $"{module}SwiftBindings.xcframework");
            Directory.CreateDirectory(wrapperXcfw);
            var bridgeXcfw = Path.Combine(shippedDir, $"{module}Bridge.xcframework");
            Directory.CreateDirectory(bridgeXcfw);
            var shipped = DirectModeOptions(shippedDir, module);
            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = shipped.OutputDirectory,
                ModuleName = shipped.ModuleName,
                Metadata = shipped.Metadata,
                SourceXCFrameworkPath = shipped.SourceXCFrameworkPath,
                SourceNativeLinkage = shipped.SourceNativeLinkage,
                SwiftRuntimeVersion = shipped.SwiftRuntimeVersion,
                Dependencies = shipped.Dependencies,
                ResolvedNamespace = shipped.ResolvedNamespace,
                ObjCProjectFileName = shipped.ObjCProjectFileName,
                EmitsAppleSupplementReference = shipped.EmitsAppleSupplementReference,
                AppleSupplementVersion = shipped.AppleSupplementVersion,
                AppleSiblingPackageReferences = shipped.AppleSiblingPackageReferences,
                // The native/packaging inputs the in-loop project leaves off.
                WrapperXCFrameworkPath = wrapperXcfw,
                BridgeXCFrameworkPath = bridgeXcfw,
                HasBridgeSwift = true,
                ResourceBundleNames = new[] { $"{module}Resources" },
            }, NullLogger.Instance);

            var verification = XDocument.Load(CsprojPath(verificationDir, module)).Root!;
            var shippedDoc = XDocument.Load(CsprojPath(shippedDir, module)).Root!;

            // Non-vacuity: the two fixtures must really differ, or "they agree" proves nothing.
            Assert.DoesNotContain("NativeReference", ItemNames(verification));
            Assert.Contains("NativeReference", ItemNames(shippedDoc));
            Assert.Contains("BundleResource", ItemNames(shippedDoc));

            // Properties: every project property must agree, name AND value.
            Assert.Equal(Properties(verification), Properties(shippedDoc));

            // Managed compilation inputs: identical item sets.
            foreach (var itemName in ManagedCompileItemNames)
            {
                var inLoop = ManagedItems(verification, itemName);
                var ships = ManagedItems(shippedDoc, itemName);
                Assert.True(
                    inLoop.SetEquals(ships),
                    $"The in-loop verification project and the shipped direct-mode project disagree on " +
                    $"<{itemName}> items, so the loop would grade a different managed compile than the " +
                    $"consumer builds. In-loop only: [{string.Join("; ", inLoop.Except(ships))}]. " +
                    $"Shipped only: [{string.Join("; ", ships.Except(inLoop))}].");
            }

            // And the fixtures must have exercised a real managed surface, not an empty one.
            Assert.NotEmpty(ManagedItems(verification, "Compile"));
            Assert.NotEmpty(ManagedItems(verification, "PackageReference"));
        }

        // The managed-relevant inputs both emission sites carry in direct mode. Source xcframework is
        // null and the linkage stays at its default because an Apple system framework is resolved by
        // dyld, not packaged; dependencies are null because direct mode has no --framework-dependency
        // edges; the Apple supplement and its sibling packages are the references the emitted C# needs
        // to compile at all.
        private static BindingProjectEmitterOptions DirectModeOptions(string dir, string module) => new()
        {
            OutputDirectory = dir,
            ModuleName = module,
            Metadata = Meta(module),
            SourceXCFrameworkPath = null,
            SourceNativeLinkage = NativeLinkage.Dynamic,
            SwiftRuntimeVersion = "0.18.0",
            Dependencies = null,
            ResolvedNamespace = $"Apple.{module}",
            ObjCProjectFileName = null,
            EmitsAppleSupplementReference = true,
            AppleSupplementVersion = "26.0.0",
            AppleSiblingPackageReferences = new[]
            {
                new DetectedAppleFrameworkDependency
                {
                    ModuleName = "SiblingFramework",
                    PackageId = "SwiftBindings.Apple.SiblingFramework",
                    VersionRange = "[26.2.1,26.3.0)",
                },
            },
        };

        private static string CsprojPath(string dir, string module)
            => Path.Combine(dir, $"{module}.Swift.iOS.csproj");

        // Property name -> value for every top-level PropertyGroup child.
        private static SortedDictionary<string, string> Properties(XElement root)
        {
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
            {
                foreach (var child in group.Elements())
                    properties[child.Name.LocalName] = child.Value.Trim();
            }
            return properties;
        }

        // One comparable string per item of the given name: its Include plus every other attribute,
        // so a changed version range or condition counts as a difference.
        private static HashSet<string> ManagedItems(XElement root, string itemName)
        {
            var items = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in root.Elements().Where(e => e.Name.LocalName == "ItemGroup"))
            {
                foreach (var item in group.Elements().Where(e => e.Name.LocalName == itemName))
                {
                    var attributes = item.Attributes()
                        .OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
                        .Select(a => $"{a.Name.LocalName}={a.Value}");
                    items.Add(string.Join("|", attributes));
                }
            }
            return items;
        }

        private static IReadOnlySet<string> ItemNames(XElement root)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in root.Elements().Where(e => e.Name.LocalName == "ItemGroup"))
            {
                foreach (var item in group.Elements())
                    names.Add(item.Name.LocalName);
            }
            return names;
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
            var dir = Path.Combine(Path.GetTempPath(), $"directparity_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
