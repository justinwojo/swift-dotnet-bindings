// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    // Detection of dep-module / public-type name collisions. The corresponding fix routes
    // through SwiftWrapperCompiler.PrecompileCollidingModule (which already knows how to
    // patch the bound interface to strip `<Module>.` qualifiers); this detector is the
    // missing piece that finds which dep modules need the patch.
    //
    // GTMSessionFetcher case: ObjC-only xcframework that exports `@interface GTMSessionFetcher`
    // in its umbrella header — Swift import surfaces a class with the same name as the
    // module. The detector must catch both Swift (swiftinterface) and ObjC (.h) shapes.

    public class DepModuleCollisionDetectorHasSwiftPublicTypeWithNameTests
    {
        [Fact]
        public void HasSwiftPublicTypeWithName_PublicClass_Detects()
        {
            var text = """
                // swift-interface-format-version: 1.0
                import Foundation
                public class Foo {
                  public var bar: Int { get }
                }
                """;
            Assert.True(DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, "Foo"));
        }

        [Fact]
        public void HasSwiftPublicTypeWithName_FinalPublicClass_Detects()
        {
            var text = "final public class GTMSessionFetcher { }";
            Assert.True(DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, "GTMSessionFetcher"));
        }

        [Fact]
        public void HasSwiftPublicTypeWithName_PublicStructEnumActorProtocol_Detects()
        {
            foreach (var kw in new[] { "struct", "enum", "actor", "protocol" })
            {
                var text = $"public {kw} Mod {{ }}";
                Assert.True(
                    DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, "Mod"),
                    $"Expected detection for 'public {kw}'");
            }
        }

        [Theory]
        // Modifier BEFORE the access keyword (the R6-5 miss): swiftc emits these forms in
        // a .swiftinterface and the `final`-only allowance silently dropped them.
        [InlineData("indirect public enum Modular { }", "Modular")]
        [InlineData("nonisolated public class Noniso { }", "Noniso")]
        [InlineData("nonisolated(unsafe) public class Noniso { }", "Noniso")]
        // Combined / either-order modifier runs must still match.
        [InlineData("public final class Late { }", "Late")]
        [InlineData("@objc final public class Mixed { }", "Mixed")]
        public void HasSwiftPublicTypeWithName_ModifierBeforeAccessKeyword_Detects(string text, string module)
        {
            Assert.True(
                DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, module),
                $"Expected detection for: {text}");
        }

        [Fact]
        public void HasSwiftPublicTypeWithName_ModifierBeforeAccess_PrefixMatch_NoFalsePositive()
        {
            // The added modifier run must not weaken the trailing word-boundary guard.
            Assert.False(
                DepModuleCollisionDetector.HasSwiftPublicTypeWithName("indirect public enum ModularKit { }", "Modular"));
        }

        [Fact]
        public void HasSwiftPublicTypeWithName_ModuleNameMismatch_NoDetection()
        {
            var text = "public class Bar { }";
            Assert.False(DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, "Foo"));
        }

        [Fact]
        public void HasSwiftPublicTypeWithName_PrefixMatch_NoFalsePositive()
        {
            // module=Foo, declaration is `public class FooBar` — word boundary must reject.
            var text = "public class FooBar { }";
            Assert.False(DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, "Foo"));
        }

        [Fact]
        public void HasSwiftPublicTypeWithName_InternalType_NoDetection()
        {
            // No `public` modifier — must not match. The collision only happens when
            // the dep exports the type publicly.
            var text = "class Foo { }";
            Assert.False(DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, "Foo"));
        }

        [Fact]
        public void HasSwiftPublicTypeWithName_EmptyInputs_ReturnsFalse()
        {
            Assert.False(DepModuleCollisionDetector.HasSwiftPublicTypeWithName("", "Foo"));
            Assert.False(DepModuleCollisionDetector.HasSwiftPublicTypeWithName("public class Foo {}", ""));
        }

        // --- public typealias (module-level alias shadows the module name in type scope) ---

        [Fact]
        public void HasSwiftPublicTypeWithName_PublicTypealias_Detects()
        {
            // A module-level public alias introduces the name into type scope the same way a
            // nominal type does, so `<Name>.X` resolves into the alias rather than the module.
            var text = "public typealias Foo = SomeOther";
            Assert.True(DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, "Foo"));
        }

        [Fact]
        public void HasSwiftPublicTypeWithName_PublicTypealias_PrefixMatch_NoFalsePositive()
        {
            // Word boundary: `public typealias FooBar` must not match module "Foo".
            var text = "public typealias FooBar = X";
            Assert.False(DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, "Foo"));
        }

        [Fact]
        public void HasSwiftPublicTypeWithName_InternalTypealias_NoDetection()
        {
            // Non-public typealias is not exported; collision only fires for public/open.
            var text = "typealias Foo = X";
            Assert.False(DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, "Foo"));
        }

        [Theory]
        [InlineData("  public typealias Foo = Bar")]
        [InlineData("@available(iOS 15.0, *) public typealias Foo = Bar")]
        [InlineData("\t@objc public typealias Foo = Bar")]
        public void HasSwiftPublicTypeWithName_PublicTypealias_LeadingAttributesOrWhitespace_Detects(string text)
        {
            Assert.True(
                DepModuleCollisionDetector.HasSwiftPublicTypeWithName(text, "Foo"),
                $"Expected detection for: {text}");
        }
    }

    public class DepModuleCollisionDetectorHasObjCInterfaceInHeadersTests
    {
        [Fact]
        public void HasObjCInterfaceInHeaders_UmbrellaHeaderInterface_Detects()
        {
            using var headersDir = new TempDir();
            File.WriteAllText(Path.Combine(headersDir.Path, "Foo.h"),
                "#import <Foundation/Foundation.h>\n@interface Foo : NSObject\n@end\n");
            Assert.True(DepModuleCollisionDetector.HasObjCInterfaceInHeaders(headersDir.Path, "Foo"));
        }

        [Fact]
        public void HasObjCInterfaceInHeaders_NonUmbrellaHeader_DetectsViaFallthrough()
        {
            using var headersDir = new TempDir();
            // Umbrella exists but only #imports other headers — common Apple pattern.
            File.WriteAllText(Path.Combine(headersDir.Path, "GTMSessionFetcher.h"),
                "// Umbrella\n#import \"Internals.h\"\n");
            File.WriteAllText(Path.Combine(headersDir.Path, "Internals.h"),
                "@interface GTMSessionFetcher : NSObject\n@end\n");
            Assert.True(DepModuleCollisionDetector.HasObjCInterfaceInHeaders(headersDir.Path, "GTMSessionFetcher"));
        }

        [Fact]
        public void HasObjCInterfaceInHeaders_CategoryInterface_Detects()
        {
            // `@interface Foo (Cat)` still makes Swift import a class named Foo.
            // Category matching is acceptable and the collision risk is real.
            using var headersDir = new TempDir();
            File.WriteAllText(Path.Combine(headersDir.Path, "Foo.h"),
                "@interface Foo (DepCategory)\n- (void)bar;\n@end\n");
            Assert.True(DepModuleCollisionDetector.HasObjCInterfaceInHeaders(headersDir.Path, "Foo"));
        }

        [Fact]
        public void HasObjCInterfaceInHeaders_ForwardDeclOnly_NoDetection()
        {
            // `@class Foo;` is a forward declaration — does NOT prove the module
            // exports a type named Foo. Must not trigger.
            using var headersDir = new TempDir();
            File.WriteAllText(Path.Combine(headersDir.Path, "Bar.h"),
                "@class Foo;\n@interface Bar : NSObject\n@end\n");
            Assert.False(DepModuleCollisionDetector.HasObjCInterfaceInHeaders(headersDir.Path, "Foo"));
        }

        [Fact]
        public void HasObjCInterfaceInHeaders_PrefixMatch_NoFalsePositive()
        {
            // module=Foo, declaration is `@interface FooBar` — word boundary must reject.
            using var headersDir = new TempDir();
            File.WriteAllText(Path.Combine(headersDir.Path, "Foo.h"),
                "@interface FooBar : NSObject\n@end\n");
            Assert.False(DepModuleCollisionDetector.HasObjCInterfaceInHeaders(headersDir.Path, "Foo"));
        }

        [Fact]
        public void HasObjCInterfaceInHeaders_NoHeadersDirectory_ReturnsFalse()
        {
            Assert.False(DepModuleCollisionDetector.HasObjCInterfaceInHeaders("/nonexistent/path", "Foo"));
        }

        [Fact]
        public void HasObjCInterfaceInHeaders_EmptyModuleName_ReturnsFalse()
        {
            using var headersDir = new TempDir();
            File.WriteAllText(Path.Combine(headersDir.Path, "Foo.h"), "@interface Foo @end");
            Assert.False(DepModuleCollisionDetector.HasObjCInterfaceInHeaders(headersDir.Path, ""));
        }
    }

    // End-to-end integration tests that build a realistic xcframework directory
    // layout on disk and walk the public Detect()/TryDetectCollision() API.
    // Complements the inline regex tests above by exercising the path-resolution
    // (FindSwiftInterfaceForDep / FindHeadersDirForDep) logic that the inline
    // tests bypass — this is the bug shape GTMSessionFetcher hits in production.
    public class DepModuleCollisionDetectorIntegrationTests
    {
        [Fact]
        public void Detect_ObjCOnlyDep_UmbrellaHeaderInterfaceCollision_Flagged()
        {
            // GTMSessionFetcher shape: ObjC-only xcframework. Headers/ dir contains
            // an umbrella header named <Module>.h whose body declares
            // `@interface <Module>`. The detector must walk into the resolved sim
            // framework dir and pick this up.
            using var tmp = new TempDir();
            var (dep, _) = BuildObjCOnlyDepFixture(tmp.Path, "GTMSessionFetcher", umbrellaHasInterface: true);

            var collisions = DepModuleCollisionDetector.Detect(
                new[] { dep }, PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Single(collisions);
            Assert.Equal("GTMSessionFetcher", collisions[0]);
        }

        [Fact]
        public void Detect_ObjCOnlyDep_NonUmbrellaHeaderInterface_FlaggedViaFallthrough()
        {
            // Apple-pattern: umbrella header only `#import`s sibling headers, and
            // the real `@interface <Module>` lives in one of those siblings. The
            // detector must fall through to the broader headers walk.
            using var tmp = new TempDir();
            var (dep, headersDir) = BuildObjCOnlyDepFixture(tmp.Path, "Dep", umbrellaHasInterface: false);
            File.WriteAllText(Path.Combine(headersDir, "Internals.h"),
                "@interface Dep : NSObject\n@end\n");

            var collisions = DepModuleCollisionDetector.Detect(
                new[] { dep }, PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Single(collisions);
            Assert.Equal("Dep", collisions[0]);
        }

        [Fact]
        public void Detect_SwiftDep_PublicTypeWithModuleName_Flagged()
        {
            // Swift-dep shape: the dep xcframework slice ships a
            // <Module>.swiftmodule/<arch>.swiftinterface that declares
            // `public class <ModuleName>`. The detector should prefer this
            // path over header scanning.
            using var tmp = new TempDir();
            var dep = BuildSwiftDepFixture(tmp.Path, "Foo",
                swiftInterfaceBody: "import Foundation\npublic class Foo { public init() {} }\n");

            var collisions = DepModuleCollisionDetector.Detect(
                new[] { dep }, PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Single(collisions);
            Assert.Equal("Foo", collisions[0]);
        }

        [Fact]
        public void Detect_SwiftDep_NoCollidingType_NotFlagged()
        {
            // Swift dep that doesn't declare a public type matching its module
            // name — the typical, healthy case. Must NOT be flagged.
            using var tmp = new TempDir();
            var dep = BuildSwiftDepFixture(tmp.Path, "Foo",
                swiftInterfaceBody: "import Foundation\npublic class Bar { public init() {} }\n");

            var collisions = DepModuleCollisionDetector.Detect(
                new[] { dep }, PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Empty(collisions);
        }

        [Fact]
        public void Detect_MultipleDeps_OnlyCollidingFlagged()
        {
            // Mixed list: one healthy dep + one colliding dep. Detector must
            // return only the colliding one and preserve order.
            using var tmp = new TempDir();
            var healthy = BuildSwiftDepFixture(Path.Combine(tmp.Path, "h"), "Healthy",
                swiftInterfaceBody: "import Foundation\npublic class Helper { public init() {} }\n");
            var (colliding, _) = BuildObjCOnlyDepFixture(Path.Combine(tmp.Path, "c"), "Colliding",
                umbrellaHasInterface: true);

            var collisions = DepModuleCollisionDetector.Detect(
                new[] { healthy, colliding }, PlatformInfoFactory.Create(ApplePlatform.iOS),
                NullLogger.Instance);

            Assert.Single(collisions);
            Assert.Equal("Colliding", collisions[0]);
        }

        [Fact]
        public void Detect_DepWithEmptyModuleName_Skipped()
        {
            // Defensive: a malformed FrameworkDependencyInfo with an empty
            // ModuleName must be skipped rather than crashing the detector.
            using var tmp = new TempDir();
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = tmp.Path,
                ModuleName = "",
                SimulatorFrameworkSearchPath = tmp.Path,
                IsObjCOnly = true,
            };

            var collisions = DepModuleCollisionDetector.Detect(
                new[] { dep }, PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Empty(collisions);
        }

        [Fact]
        public void DetectPerSlice_DeviceOnlySliceWithCollision_FlagsDeviceNotSimulator()
        {
            // Slice-shape coverage: a dep where ONLY the device slice exists (no
            // simulator search path). The device list must contain the collision,
            // the simulator list must remain empty — the wrapper-compile for sim
            // would otherwise apply the qualifier strip to a slice that doesn't
            // need it.
            using var tmp = new TempDir();
            var deviceSearch = Path.Combine(tmp.Path, "dev");
            var headersDir = Path.Combine(deviceSearch, "DeviceDep.framework", "Headers");
            Directory.CreateDirectory(headersDir);
            File.WriteAllText(Path.Combine(headersDir, "DeviceDep.h"),
                "#import <Foundation/Foundation.h>\n@interface DeviceDep : NSObject\n@end\n");
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = Path.Combine(tmp.Path, "DeviceDep.xcframework"),
                ModuleName = "DeviceDep",
                DeviceFrameworkSearchPath = deviceSearch,
                IsObjCOnly = true,
            };

            var sliced = DepModuleCollisionDetector.DetectPerSlice(
                new[] { dep }, PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Empty(sliced.Simulator);
            Assert.Single(sliced.Device);
            Assert.Equal("DeviceDep", sliced.Device[0]);
        }

        [Fact]
        public void DetectPerSlice_SimulatorSliceClean_DeviceSliceCollides_DeviceOnly()
        {
            // Both slices present, only the device slice exposes the collision
            // (e.g. simulator stubs vs device-only ObjC). The per-slice detector
            // must put the collision in Device only — putting it in Simulator
            // would over-patch the simulator wrapper-compile.
            using var tmp = new TempDir();
            var simSearch = Path.Combine(tmp.Path, "sim");
            var simHeaders = Path.Combine(simSearch, "SliceSplit.framework", "Headers");
            Directory.CreateDirectory(simHeaders);
            File.WriteAllText(Path.Combine(simHeaders, "SliceSplit.h"),
                "// Simulator slice — no @interface SliceSplit here\n");

            var deviceSearch = Path.Combine(tmp.Path, "dev");
            var deviceHeaders = Path.Combine(deviceSearch, "SliceSplit.framework", "Headers");
            Directory.CreateDirectory(deviceHeaders);
            File.WriteAllText(Path.Combine(deviceHeaders, "SliceSplit.h"),
                "#import <Foundation/Foundation.h>\n@interface SliceSplit : NSObject\n@end\n");

            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = Path.Combine(tmp.Path, "SliceSplit.xcframework"),
                ModuleName = "SliceSplit",
                SimulatorFrameworkSearchPath = simSearch,
                DeviceFrameworkSearchPath = deviceSearch,
                IsObjCOnly = true,
            };

            var sliced = DepModuleCollisionDetector.DetectPerSlice(
                new[] { dep }, PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Empty(sliced.Simulator);
            Assert.Single(sliced.Device);
            Assert.Equal("SliceSplit", sliced.Device[0]);
        }

        [Fact]
        public void DetectPerSlice_BothSlicesCollide_FlagsBoth()
        {
            // Common case: an xcframework whose simulator and device slices both
            // ship the same colliding @interface. Both lists must contain the
            // module — required so each wrapper-compile applies the patch.
            using var tmp = new TempDir();
            var simSearch = Path.Combine(tmp.Path, "sim");
            var simHeaders = Path.Combine(simSearch, "BothDep.framework", "Headers");
            Directory.CreateDirectory(simHeaders);
            File.WriteAllText(Path.Combine(simHeaders, "BothDep.h"),
                "#import <Foundation/Foundation.h>\n@interface BothDep : NSObject\n@end\n");

            var deviceSearch = Path.Combine(tmp.Path, "dev");
            var deviceHeaders = Path.Combine(deviceSearch, "BothDep.framework", "Headers");
            Directory.CreateDirectory(deviceHeaders);
            File.WriteAllText(Path.Combine(deviceHeaders, "BothDep.h"),
                "#import <Foundation/Foundation.h>\n@interface BothDep : NSObject\n@end\n");

            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = Path.Combine(tmp.Path, "BothDep.xcframework"),
                ModuleName = "BothDep",
                SimulatorFrameworkSearchPath = simSearch,
                DeviceFrameworkSearchPath = deviceSearch,
                IsObjCOnly = true,
            };

            var sliced = DepModuleCollisionDetector.DetectPerSlice(
                new[] { dep }, PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Equal(new[] { "BothDep" }, sliced.Simulator);
            Assert.Equal(new[] { "BothDep" }, sliced.Device);
        }

        [Fact]
        public void DetectPerSlice_SwiftDep_DeviceOnlySwiftInterfaceCollides_DeviceOnly()
        {
            // Slice-shape coverage for Swift deps: the device slice declares
            // `public class Foo` in its `.swiftinterface` while the simulator slice
            // declares no colliding type. The per-slice detector must reach the
            // Swift dep code path (TryDetectCollisionInSlice over a `.swiftinterface`)
            // and place the module in Device only, leaving Simulator empty —
            // mirroring the ObjC slice-split tests for the Swift-dep variant the
            // session brief flagged as untested.
            using var tmp = new TempDir();
            var simSearch = Path.Combine(tmp.Path, "sim");
            var simModuleDir = Path.Combine(simSearch, "Foo.framework", "Modules", "Foo.swiftmodule");
            Directory.CreateDirectory(simModuleDir);
            File.WriteAllText(
                Path.Combine(simModuleDir, "arm64-apple-ios-simulator.swiftinterface"),
                "import Foundation\npublic class Bar { public init() {} }\n");

            var deviceSearch = Path.Combine(tmp.Path, "dev");
            var deviceModuleDir = Path.Combine(deviceSearch, "Foo.framework", "Modules", "Foo.swiftmodule");
            Directory.CreateDirectory(deviceModuleDir);
            File.WriteAllText(
                Path.Combine(deviceModuleDir, "arm64-apple-ios.swiftinterface"),
                "import Foundation\npublic class Foo { public init() {} }\n");

            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = Path.Combine(tmp.Path, "Foo.xcframework"),
                ModuleName = "Foo",
                SimulatorFrameworkSearchPath = simSearch,
                DeviceFrameworkSearchPath = deviceSearch,
                IsObjCOnly = false,
            };

            var sliced = DepModuleCollisionDetector.DetectPerSlice(
                new[] { dep }, PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Empty(sliced.Simulator);
            Assert.Single(sliced.Device);
            Assert.Equal("Foo", sliced.Device[0]);
        }

        // Build a minimal ObjC-only dep xcframework rooted at `parent`:
        //   <parent>/sim/<Module>.framework/Headers/<Module>.h
        // Returns the FrameworkDependencyInfo + the absolute Headers/ dir so
        // tests can drop extra sibling headers in for the fallthrough case.
        private static (FrameworkDependencyInfo dep, string headersDir) BuildObjCOnlyDepFixture(
            string parent, string moduleName, bool umbrellaHasInterface)
        {
            var simSearch = Path.Combine(parent, "sim");
            var headersDir = Path.Combine(simSearch, moduleName + ".framework", "Headers");
            Directory.CreateDirectory(headersDir);
            var umbrella = Path.Combine(headersDir, moduleName + ".h");
            if (umbrellaHasInterface)
            {
                File.WriteAllText(umbrella,
                    $"#import <Foundation/Foundation.h>\n@interface {moduleName} : NSObject\n@end\n");
            }
            else
            {
                File.WriteAllText(umbrella, "// Umbrella only — re-imports siblings\n");
            }

            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = Path.Combine(parent, moduleName + ".xcframework"),
                ModuleName = moduleName,
                SimulatorFrameworkSearchPath = simSearch,
                IsObjCOnly = true,
            };
            return (dep, headersDir);
        }

        // Build a minimal Swift dep xcframework slice with a real .swiftinterface.
        private static FrameworkDependencyInfo BuildSwiftDepFixture(
            string parent, string moduleName, string swiftInterfaceBody)
        {
            var simSearch = Path.Combine(parent, "sim");
            var modulesDir = Path.Combine(simSearch, moduleName + ".framework", "Modules",
                moduleName + ".swiftmodule");
            Directory.CreateDirectory(modulesDir);
            // `.private.swiftinterface` is preferred when present — the detector
            // looks for the public one too; both code paths converge in
            // FindSwiftInterfaceForDep. Use the public name to also cover
            // libraries that don't ship a private variant.
            File.WriteAllText(
                Path.Combine(modulesDir, "arm64-apple-ios-simulator.swiftinterface"),
                swiftInterfaceBody);

            return new FrameworkDependencyInfo
            {
                XCFrameworkPath = Path.Combine(parent, moduleName + ".xcframework"),
                ModuleName = moduleName,
                SimulatorFrameworkSearchPath = simSearch,
                IsObjCOnly = false,
            };
        }
    }

    /// <summary>Disposable temp directory for header-scan tests.</summary>
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "swiftbindings-depcollision-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
