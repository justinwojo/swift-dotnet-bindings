// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    // Detection of cross-Apple-framework dep edges from .swiftinterface `import` lines.
    // Used by apple-framework-mode (SwiftAppleFrameworkTarget) so the SDK can auto-inject
    // <PackageReference> items for transitive Apple binding-package deps.
    //
    // Behavior cliff: registry lookup is the gate. Modules without a registered packageId
    // (markers like Swift, _Concurrency, simd, and Apple SDK modules that don't ship as
    // a standalone binding package) MUST be silently dropped — only modules with a known
    // SwiftBindings.Apple.<Module> package emit a dep edge.

    public class AppleFrameworkImportDetectorExtractImportsTests
    {
        [Fact]
        public void ExtractImports_RealityKitSwiftinterfaceShape_ExtractsAllImports()
        {
            // Verbatim header shape from RealityKit's stock SDK swiftinterface
            // — `@_exported import` and bare `import` lines must both be detected.
            var text = """
                // swift-interface-format-version: 1.0
                // swift-compiler-version: Apple Swift version 6.2
                // swift-module-flags: -target arm64e-apple-ios26.2 -enable-objc-interop
                import ARKit
                import Combine
                import CoreGraphics
                import Foundation
                @_exported import RealityFoundation
                import Swift
                import UIKit
                import _Concurrency
                import _StringProcessing
                import simd
                """;

            var result = AppleFrameworkImportDetector.ExtractImports(text);

            Assert.Contains("ARKit", result);
            Assert.Contains("Combine", result);
            Assert.Contains("Foundation", result);
            Assert.Contains("RealityFoundation", result);
            Assert.Contains("Swift", result);
            Assert.Contains("_Concurrency", result);
            Assert.Contains("simd", result);
        }

        [Fact]
        public void ExtractImports_DeduplicatesRepeatedImports()
        {
            var text = """
                import Foundation
                import UIKit
                import Foundation
                """;

            var result = AppleFrameworkImportDetector.ExtractImports(text);

            Assert.Equal(2, result.Count);
            Assert.Equal("Foundation", result[0]);
            Assert.Equal("UIKit", result[1]);
        }

        [Fact]
        public void ExtractImports_PreservesFirstSeenOrder()
        {
            // Order matters indirectly: ResolveDependencies sorts alphabetically before
            // emission, but ExtractImports itself preserves insertion order so callers
            // that don't go through ResolveDependencies still get a stable list.
            var text = """
                import Zeta
                import Alpha
                import Mu
                """;

            var result = AppleFrameworkImportDetector.ExtractImports(text);

            Assert.Equal(new[] { "Zeta", "Alpha", "Mu" }, result);
        }

        [Fact]
        public void ExtractImports_HandlesAttributedImports()
        {
            // Attribute prefixes (@_exported, @_implementationOnly, @_spi(...)) must not
            // hide the underlying import line.
            var text = """
                @_exported import Foundation
                @_implementationOnly import UIKit
                @_spi(Internal) import CoreFoundation
                """;

            var result = AppleFrameworkImportDetector.ExtractImports(text);

            Assert.Equal(new[] { "Foundation", "UIKit", "CoreFoundation" }, result);
        }

        [Fact]
        public void ExtractImports_HandlesAccessModifierImports()
        {
            // SE-0409 access-controlled imports (`public import`, `private import`, etc.)
            // are emitted in Xcode 26.2 SDK swiftinterfaces (e.g. ImagePlayground).
            // Without modifier handling, these lines would silently miss extraction,
            // dropping any registered packageId behind them from the nuspec dep set.
            var text = """
                public import CoreGraphics
                private import Foundation
                internal import UIKit
                fileprivate import Combine
                open import Metal
                package import ARKit
                """;

            var result = AppleFrameworkImportDetector.ExtractImports(text);

            Assert.Equal(
                new[] { "CoreGraphics", "Foundation", "UIKit", "Combine", "Metal", "ARKit" },
                result);
        }

        [Fact]
        public void ExtractImports_HandlesAccessModifierWithSubmember()
        {
            // `public import PencilKit.PKDrawing` (verbatim from ImagePlayground swiftinterface)
            // must resolve to the leading module name.
            var text = """
                public import PencilKit.PKDrawing
                public import CoreImage.CIFilterBuiltins
                """;

            var result = AppleFrameworkImportDetector.ExtractImports(text);

            Assert.Equal(new[] { "PencilKit", "CoreImage" }, result);
        }

        [Fact]
        public void ExtractImports_HandlesAttributePlusAccessModifier()
        {
            // Both orderings of access modifier + attribute appear in real swiftinterfaces.
            // ImagePlayground emits `@_exported public import ImagePlayground`; older
            // shapes invert it. Both must succeed.
            var text = """
                @_exported public import ImagePlayground
                public @_exported import RealityFoundation
                @_spi(Internal) public import _MarketplaceKit_UIKit
                """;

            var result = AppleFrameworkImportDetector.ExtractImports(text);

            Assert.Equal(
                new[] { "ImagePlayground", "RealityFoundation", "_MarketplaceKit_UIKit" },
                result);
        }

        [Fact]
        public void ExtractImports_HandlesSubmemberImports()
        {
            // `import struct Foundation.URL` should resolve to the leading module
            // (Foundation), not the submember.
            var text = """
                import struct Foundation.URL
                import class UIKit.UIView
                import enum Combine.Publishers
                """;

            var result = AppleFrameworkImportDetector.ExtractImports(text);

            Assert.Equal(new[] { "Foundation", "UIKit", "Combine" }, result);
        }

        [Fact]
        public void ExtractImports_EmptyOrWhitespace_ReturnsEmpty()
        {
            Assert.Empty(AppleFrameworkImportDetector.ExtractImports(string.Empty));
            Assert.Empty(AppleFrameworkImportDetector.ExtractImports("   \n\t  "));
            Assert.Empty(AppleFrameworkImportDetector.ExtractImports(null!));
        }

        [Fact]
        public void ExtractImports_NoImportsInBody_ReturnsEmpty()
        {
            // `import` appearing inside a body (not at start of line) must not match.
            // Without the multiline anchor, we'd accidentally pick up things like
            // `imports: [...]` in a comment.
            var text = """
                public func process(imports: [String]) {
                  // some import-related work
                }
                """;

            var result = AppleFrameworkImportDetector.ExtractImports(text);

            Assert.Empty(result);
        }
    }

    public class AppleFrameworkImportDetectorExtractNonPublicImportsTests
    {
        // The non-public extractor is the negative filter that prevents the
        // wrapper emitter from re-emitting `@_implementationOnly` / `private` /
        // `internal` / `fileprivate` / `package` siblings. A regex regression
        // here silently breaks the absl-style C++-only-sibling drop and the
        // wrapper compile fails with "no such module".

        [Fact]
        public void ExtractNonPublicImports_RecognizesEachAttributeOrAccessModifier()
        {
            var text =
                "@_implementationOnly import absl\n" +
                "private import grpc\n" +
                "internal import leveldb\n" +
                "fileprivate import openssl_grpc\n" +
                "package import grpcpp\n" +
                "import Foundation\n" +
                "public struct Marker {}\n";

            var nonPublic = AppleFrameworkImportDetector.ExtractNonPublicImports(text);

            Assert.Contains("absl", nonPublic);
            Assert.Contains("grpc", nonPublic);
            Assert.Contains("leveldb", nonPublic);
            Assert.Contains("openssl_grpc", nonPublic);
            Assert.Contains("grpcpp", nonPublic);
            // `import Foundation` is plain-public — must NOT appear here.
            Assert.DoesNotContain("Foundation", nonPublic);
        }

        [Fact]
        public void ExtractNonPublicImports_IgnoresPublicAndExportedImports()
        {
            // `@_exported` and `public` are public-shape imports — they propagate
            // to consumers and must not appear in the non-public set.
            var text =
                "@_exported import RealityFoundation\n" +
                "public import UIKit\n" +
                "import Foundation\n";

            var nonPublic = AppleFrameworkImportDetector.ExtractNonPublicImports(text);

            Assert.Empty(nonPublic);
        }

        [Fact]
        public void ExtractNonPublicImports_EmptyOrNullInput_ReturnsEmpty()
        {
            Assert.Empty(AppleFrameworkImportDetector.ExtractNonPublicImports(string.Empty));
            Assert.Empty(AppleFrameworkImportDetector.ExtractNonPublicImports(null!));
        }
    }

    public class AppleFrameworkImportDetectorResolveDependenciesTests
    {
        [Fact]
        public void ResolveDependencies_RealityKitImports_OnlyEmitsRealityFoundation()
        {
            // The flagship case: RealityKit imports many modules but only RealityFoundation
            // has a registered packageId (the rest — Foundation, UIKit, ARKit, simd, etc. —
            // are markers or Apple SDK modules without a standalone binding package).
            var imports = new[]
            {
                "ARKit", "Combine", "CoreGraphics", "Foundation", "GameController",
                "GroupActivities", "Metal", "MultipeerConnectivity", "RealityFoundation",
                "Swift", "UIKit", "_Concurrency", "_StringProcessing",
                "_SwiftConcurrencyShims", "simd",
            };

            var result = AppleFrameworkImportDetector.ResolveDependencies(imports, "RealityKit", "26.2.1");

            Assert.Single(result);
            Assert.Equal("RealityFoundation", result[0].ModuleName);
            Assert.Equal("SwiftBindings.Apple.RealityFoundation", result[0].PackageId);
            Assert.Equal("[26.2.1,26.3.0)", result[0].VersionRange);
        }

        [Fact]
        public void ResolveDependencies_FiltersSelfReference()
        {
            // If a swiftinterface (somehow) imports its own module, it must be dropped —
            // the auto-injected PackageReference would create a circular dep edge in the
            // emitted nuspec.
            var imports = new[] { "RealityKit", "RealityFoundation" };

            var result = AppleFrameworkImportDetector.ResolveDependencies(imports, "RealityKit", "26.2.1");

            Assert.Single(result);
            Assert.Equal("RealityFoundation", result[0].ModuleName);
        }

        [Fact]
        public void ResolveDependencies_DropsMarkersAndUnregistered()
        {
            // Markers (Swift, _Concurrency, simd) and Apple SDK modules without a packageId
            // (Foundation, UIKit, etc.) are silently dropped.
            var imports = new[]
            {
                "Swift", "_Concurrency", "_StringProcessing", "simd", "__ObjC",
                "Foundation", "UIKit", "ARKit",
                "MyCustomLib", "SomeRandomThirdPartyModule",
            };

            var result = AppleFrameworkImportDetector.ResolveDependencies(imports, "RealityKit", "26.2.1");

            Assert.Empty(result);
        }

        [Fact]
        public void ResolveDependencies_SortsAlphabeticallyForDeterminism()
        {
            // ResolveDependencies sorts by module name so the emitted stdout (and the
            // injected PackageReference items, and the nuspec dependencies group) is
            // deterministic regardless of swiftinterface line order.
            var imports = new[] { "RealityFoundation", "RealityKit" };

            var result = AppleFrameworkImportDetector.ResolveDependencies(imports, "Other", "26.2.1");

            Assert.Equal(2, result.Count);
            Assert.Equal("RealityFoundation", result[0].ModuleName);
            Assert.Equal("RealityKit", result[1].ModuleName);
        }

        [Fact]
        public void ResolveDependencies_DeduplicatesByModuleName()
        {
            // ExtractImports already dedups, but ResolveDependencies must defend itself
            // for callers that pass arbitrary IEnumerable<string>.
            var imports = new[] { "RealityFoundation", "RealityFoundation", "RealityFoundation" };

            var result = AppleFrameworkImportDetector.ResolveDependencies(imports, "RealityKit", "26.2.1");

            Assert.Single(result);
        }

        [Fact]
        public void ResolveDependencies_EmptyImports_ReturnsEmpty()
        {
            var result = AppleFrameworkImportDetector.ResolveDependencies(
                System.Array.Empty<string>(), "RealityKit", "26.2.1");
            Assert.Empty(result);
        }
    }

    public class AppleFrameworkImportDetectorComputeBoundedVersionRangeTests
    {
        // Each Apple SDK train (Xcode minor release) produces a coordinated set of
        // SwiftBindings.Apple.<Module> packages at the same Y.Z; the bounded range
        // [X.Y.Z, X.(Y+1).0) ensures cross-framework deps within a train resolve
        // to a consistent set without floating into the next train.

        [Theory]
        [InlineData("26.2.1", "[26.2.1,26.3.0)")]
        [InlineData("26.0.0", "[26.0.0,26.1.0)")]
        [InlineData("26.9.5", "[26.9.5,26.10.0)")]
        [InlineData("27.0.0", "[27.0.0,27.1.0)")]
        // Two-component input ("major.minor") is permitted — the lower bound is taken
        // verbatim (NuGet treats "26.2" as "26.2.0") and the upper bound increments minor.
        [InlineData("26.2", "[26.2,26.3.0)")]
        public void ComputeBoundedVersionRange_ValidVersion_ReturnsExpected(
            string appleVersion, string expectedRange)
        {
            Assert.Equal(expectedRange, AppleFrameworkImportDetector.ComputeBoundedVersionRange(appleVersion));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("26")]              // single component
        [InlineData("notaversion")]     // no dots
        [InlineData("notaversion.beta")] // no numeric prefix
        [InlineData("26.notnumeric")]   // non-numeric minor
        public void ComputeBoundedVersionRange_InvalidVersion_Throws(string appleVersion)
        {
            Assert.Throws<System.ArgumentException>(() =>
                AppleFrameworkImportDetector.ComputeBoundedVersionRange(appleVersion));
        }
    }

    public class AppleFrameworkImportDetectorDetectTests
    {
        // The Detect convenience wrapper round-trips the file → text → imports →
        // dep-edges pipeline. Use a temp file so the test doesn't depend on a live
        // Apple SDK install (CI runs Linux; only the MSBuild target shells out on Mac).

        [Fact]
        public void Detect_RealityKitShapedSwiftinterface_EmitsRealityFoundationDep()
        {
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "apple-import-detector-tests-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                var path = System.IO.Path.Combine(tempDir, "test.swiftinterface");
                System.IO.File.WriteAllText(path, """
                    // swift-interface-format-version: 1.0
                    import ARKit
                    import Foundation
                    @_exported import RealityFoundation
                    import Swift
                    import simd
                    """);

                var result = AppleFrameworkImportDetector.Detect(path, "RealityKit", "26.2.1");

                Assert.Single(result);
                Assert.Equal("RealityFoundation", result[0].ModuleName);
                Assert.Equal("SwiftBindings.Apple.RealityFoundation", result[0].PackageId);
                Assert.Equal("[26.2.1,26.3.0)", result[0].VersionRange);
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Detect_NonPublicImportOfRegisteredModule_StillEmitsDep()
        {
            // DELIBERATE BEHAVIOR LOCK: a non-public import (@_implementationOnly /
            // private / internal import) of a REGISTERED module must STILL produce a
            // dep edge. Unlike the wrapper re-emission path (ModuleHandler, which drops
            // non-public imports via ExtractNonPublicImports so swiftc doesn't try to
            // `import` a C++-only sibling), dependency detection must keep them: this
            // binding's compiled dylib links the module at runtime regardless of import
            // visibility, so the consumer's package must transitively pull its binding
            // package or hit DllNotFound. If this test ever "fixes" non-public imports
            // out of the dep set, it has reintroduced that shipping hazard.
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "apple-import-detector-tests-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                var path = System.IO.Path.Combine(tempDir, "test.swiftinterface");
                System.IO.File.WriteAllText(path, """
                    // swift-interface-format-version: 1.0
                    import Foundation
                    @_implementationOnly import RealityFoundation
                    import Swift
                    """);

                var result = AppleFrameworkImportDetector.Detect(path, "RealityKit", "26.2.1");

                Assert.Single(result);
                Assert.Equal("RealityFoundation", result[0].ModuleName);
                Assert.Equal("SwiftBindings.Apple.RealityFoundation", result[0].PackageId);
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Detect_NonexistentFile_Throws()
        {
            var bogusPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "definitely-does-not-exist-" + System.Guid.NewGuid().ToString("N") + ".swiftinterface");

            Assert.Throws<System.IO.FileNotFoundException>(() =>
                AppleFrameworkImportDetector.Detect(bogusPath, "RealityKit", "26.2.1"));
        }
    }
}
