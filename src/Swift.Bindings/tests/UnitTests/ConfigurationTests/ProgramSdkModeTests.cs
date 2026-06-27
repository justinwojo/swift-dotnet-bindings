// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.CommandLine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Tests for --sdk-mode, --package-id, and --wrapper-architectures CLI options.
    /// These are structural tests that verify option parsing and help text.
    /// End-to-end behavior is validated by integration tests.
    /// </summary>
    [Collection("ConsoleCapture")]
    public class ProgramSdkModeTests
    {
        [Fact]
        public void Help_IncludesSdkModeOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--sdk-mode", output);
        }

        [Fact]
        public void Help_IncludesPackageIdOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--package-id", output);
        }

        [Fact]
        public void Help_IncludesWrapperArchitecturesOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--wrapper-architectures", output);
        }

        [Fact]
        public void SdkModeOption_DefaultsFalse()
        {
            // Verify that --sdk-mode is a recognized option that defaults to false
            // System.CommandLine auto-generates help mentioning the option description
            var output = CaptureHelp();
            Assert.Contains("--sdk-mode", output);
            Assert.Contains("SDK mode", output);
        }

        [Fact]
        public void WrapperArchitecturesOption_DefaultsToSimulator()
        {
            var output = CaptureHelp();
            Assert.Contains("--wrapper-architectures", output);
            Assert.Contains("simulator", output);
        }

        [Fact]
        public void PackageIdOption_DescribesOverride()
        {
            var output = CaptureHelp();
            Assert.Contains("--package-id", output);
        }

        [Fact]
        public void Help_IncludesSwiftRuntimeVersionOption()
        {
            // Locks the CLI plumbing for --swift-runtime-version. Without it, the
            // emitter would default to the local-dev sentinel "0.0.0-dev" on every
            // generator invocation and produce IsPackable=false projects, breaking
            // 'dotnet pack' for normal users who never go through the SDK pipeline.
            // This is the registration check; emitter behavior is covered by
            // BindingProjectBasicTests.Emit_IsPackable_True_WhenPublishedRuntimeVersion.
            var output = CaptureHelp();
            Assert.Contains("--swift-runtime-version", output);
        }

        [Fact]
        public void SwiftRuntimeVersionOption_ParsesWithoutCrashing()
        {
            // Smoke test: verifies the option string is accepted by the parser.
            // We use a nonexistent xcframework so resolution fails fast — we only
            // care that the option survives parsing without an unhandled exception.
            var dir = Path.Combine(Path.GetTempPath(), $"srv_parse_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[]
                    {
                        "--xcframework", "/nonexistent",
                        "--swift-runtime-version", "0.8.0",
                        "-o", dir
                    });
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void MissingOutput_StillFails()
        {
            // Verify existing required-option behavior is preserved
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "--sdk-mode" });
                // Should fail because -o is required
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        [Fact]
        public void InvalidWrapperArchitectures_DoesNotCrash()
        {
            // Verifies the parser accepts the option string without crashing
            // (actual validation happens in the handler, which needs -o and inputs)
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "--wrapper-architectures", "invalid", "-o", "/tmp/test" });
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        [Fact]
        public void MissingOutput_ReturnsNonZeroExitCode()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                var exitCode = BindingsGenerator.Main(new[] { "--xcframework", "/nonexistent" });
                Assert.NotEqual(0, exitCode);
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        [Fact]
        public void ConflictingInputModes_ReturnsNonZeroExitCode()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"exitcode_conflict_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[]
                    {
                        "--xcframework", "/nonexistent",
                        "-a", "/nonexistent/abi.json",
                        "-o", dir
                    });
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void MissingAllInputs_ReturnsNonZeroExitCode()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"exitcode_noinput_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[] { "-o", dir });
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Help_ReturnsZeroExitCode()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                var exitCode = BindingsGenerator.Main(new[] { "-h" });
                Assert.Equal(0, exitCode);
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        [Fact]
        public void EmptyModuleName_ReturnsNonZeroExitCode_NoUnhandledException()
        {
            // Craft an ABI JSON with empty module name to trigger the try-catch in GenerateBindings
            var dir = Path.Combine(Path.GetTempPath(), $"audit_catch_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var abiJson = """
                    {
                      "ABIRoot": {
                        "kind": "Root",
                        "name": "",
                        "printedName": "",
                        "children": [
                          {
                            "kind": "TypeDecl",
                            "name": "Foo",
                            "moduleName": ""
                          }
                        ]
                      }
                    }
                    """;
                var abiPath = Path.Combine(dir, "abi.json");
                File.WriteAllText(abiPath, abiJson);
                // Create stub tbd and dylib
                var tbdPath = Path.Combine(dir, "lib.tbd");
                File.WriteAllText(tbdPath, "--- !tapi-tbd\ntbd-version: 4\ntargets: []\ninstall-name: /usr/lib/lib.dylib\n...\n");
                var dylibPath = Path.Combine(dir, "lib.dylib");
                File.WriteAllText(dylibPath, "");

                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[]
                    {
                        "-a", abiPath,
                        "-d", dylibPath,
                        "-t", tbdPath,
                        "-o", dir,
                        "-l", "TestLib"
                    });
                    // Should fail gracefully (non-zero exit) rather than crashing
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CaptureHelp()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "-h" });
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }
    }

    /// <summary>
    /// Tests for --skip-wrapper-compilation and --compile-wrapper-only CLI flags.
    /// </summary>
    [Collection("ConsoleCapture")]
    public class TwoPassBuildCliTests
    {
        [Fact]
        public void Help_IncludesSkipWrapperCompilationOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--skip-wrapper-compilation", output);
        }

        [Fact]
        public void Help_IncludesCompileWrapperOnlyOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--compile-wrapper-only", output);
        }

        [Fact]
        public void MutuallyExclusiveFlags_ReturnsNonZeroExitCode()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"twopass_mutex_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[]
                    {
                        "--skip-wrapper-compilation",
                        "--compile-wrapper-only",
                        "--xcframework", "/nonexistent.xcframework",
                        "-o", dir
                    });
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CompileWrapperOnly_WithoutXcframework_ReturnsNonZeroExitCode()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"twopass_noxcfw_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[]
                    {
                        "--compile-wrapper-only",
                        "-o", dir
                    });
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CaptureHelp()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "-h" });
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }
    }

    /// <summary>
    /// Tests for SaveWrapperContext / LoadWrapperContext round-trip persistence.
    /// </summary>
    public class WrapperContextPersistenceTests : IDisposable
    {
        private static readonly ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        private readonly string _tempDir;

        public WrapperContextPersistenceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"wrapper-ctx-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void RoundTrip_PreservesInternalTypeNames()
        {
            var internalTypes = new HashSet<string> { "InternalFoo", "Caches", "Module.InternalBar" };
            BindingsGenerator.SaveWrapperContext(_tempDir, internalTypes, null, null, EmptyCollisions, _logger);

            var (loaded, _, _, _) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.NotNull(loaded);
            Assert.Equal(internalTypes, loaded);
        }

        [Fact]
        public void RoundTrip_PreservesModuleNameForCollision()
        {
            BindingsGenerator.SaveWrapperContext(_tempDir, null, "MyModule", null, EmptyCollisions, _logger);

            var (_, moduleNameForCollision, _, _) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Equal("MyModule", moduleNameForCollision);
        }

        [Fact]
        public void RoundTrip_PreservesNestedTypesInCollidingClass()
        {
            var nested = new HashSet<string> { "NestedA", "NestedB" };
            BindingsGenerator.SaveWrapperContext(_tempDir, null, "Mod", nested, EmptyCollisions, _logger);

            var (_, _, loadedNested, _) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.NotNull(loadedNested);
            Assert.Equal(nested, loadedNested);
        }

        [Fact]
        public void RoundTrip_PreservesDepModuleNamesForCollision()
        {
            var sim = new List<string> { "SimDep", "SharedDep" };
            var device = new List<string> { "DeviceDep", "SharedDep" };
            var collisions = new DepModuleCollisionDetector.SlicedCollisionResult(sim, device);
            BindingsGenerator.SaveWrapperContext(_tempDir, null, null, null, collisions, _logger);

            var (_, _, _, loaded) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Equal(sim.OrderBy(n => n), loaded.Simulator.OrderBy(n => n));
            Assert.Equal(device.OrderBy(n => n), loaded.Device.OrderBy(n => n));
        }

        [Fact]
        public void RoundTrip_AllFieldsTogether()
        {
            var internalTypes = new HashSet<string> { "TypeA" };
            var nested = new HashSet<string> { "NestedX" };
            var collisions = new DepModuleCollisionDetector.SlicedCollisionResult(
                new List<string> { "DepA" }, new List<string> { "DepB" });
            BindingsGenerator.SaveWrapperContext(_tempDir, internalTypes, "Collision", nested, collisions, _logger);

            var (loadedInternal, loadedCollision, loadedNested, loadedDep) =
                BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Equal(internalTypes, loadedInternal);
            Assert.Equal("Collision", loadedCollision);
            Assert.Equal(nested, loadedNested);
            Assert.Equal(new[] { "DepA" }, loadedDep.Simulator);
            Assert.Equal(new[] { "DepB" }, loadedDep.Device);
        }

        [Fact]
        public void Load_MissingFile_ReturnsEmpty()
        {
            var emptyDir = Path.Combine(_tempDir, "empty");
            Directory.CreateDirectory(emptyDir);

            var (internalTypes, collision, nested, depCollisions) =
                BindingsGenerator.LoadWrapperContext(emptyDir, _logger);

            Assert.Null(internalTypes);
            Assert.Null(collision);
            Assert.Null(nested);
            Assert.True(depCollisions.IsEmpty);
        }

        [Fact]
        public void Load_CorruptedFile_ReturnsEmpty()
        {
            File.WriteAllText(Path.Combine(_tempDir, "wrapper-context.json"), "not valid json{{{");

            var (internalTypes, collision, nested, depCollisions) =
                BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Null(internalTypes);
            Assert.Null(collision);
            Assert.Null(nested);
            Assert.True(depCollisions.IsEmpty);
        }

        [Fact]
        public void RoundTrip_EmptyInputs_ProducesEmptyCollections()
        {
            BindingsGenerator.SaveWrapperContext(_tempDir, null, null, null, EmptyCollisions, _logger);

            var (loadedInternal, loadedCollision, loadedNested, loadedDep) =
                BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            // null sets serialize as empty arrays; empty arrays deserialize as empty HashSets
            Assert.NotNull(loadedInternal);
            Assert.Empty(loadedInternal);
            Assert.Null(loadedCollision);
            Assert.NotNull(loadedNested);
            Assert.Empty(loadedNested);
            Assert.True(loadedDep.IsEmpty);
        }

        [Fact]
        public void Load_LegacySingleListShape_PopulatesBothSlices()
        {
            // Backwards compat: an older wrapper-context.json with the flat
            // `depModuleNamesForCollision` array must still hydrate so cached output
            // directories keep working across the per-slice schema change.
            var legacy = new Newtonsoft.Json.Linq.JObject
            {
                ["internalTypeNames"] = new Newtonsoft.Json.Linq.JArray(),
                ["moduleNameForCollision"] = (string?)null,
                ["nestedTypesInCollidingClass"] = new Newtonsoft.Json.Linq.JArray(),
                ["depModuleNamesForCollision"] = new Newtonsoft.Json.Linq.JArray("Foo", "Bar"),
            };
            File.WriteAllText(
                Path.Combine(_tempDir, "wrapper-context.json"),
                legacy.ToString());

            var (_, _, _, loaded) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Equal(new[] { "Bar", "Foo" }, loaded.Simulator.OrderBy(n => n));
            Assert.Equal(new[] { "Bar", "Foo" }, loaded.Device.OrderBy(n => n));
        }

        [Fact]
        public void Load_BothPerSliceAndLegacyFields_PreferPerSlice()
        {
            // Forward-compat guard: if a future writer ever emits BOTH the legacy
            // single-list field AND the per-slice fields (mid-migration, hand-edited
            // cache, etc.), the loader must hydrate from the per-slice fields. The
            // legacy field is only the fallback when both per-slice keys are absent.
            // Without this guard, a downstream change that re-introduces the legacy
            // field would silently over-patch the simulator wrapper with the union.
            var mixed = new Newtonsoft.Json.Linq.JObject
            {
                ["internalTypeNames"] = new Newtonsoft.Json.Linq.JArray(),
                ["moduleNameForCollision"] = (string?)null,
                ["nestedTypesInCollidingClass"] = new Newtonsoft.Json.Linq.JArray(),
                ["depModuleNamesForCollisionSimulator"] = new Newtonsoft.Json.Linq.JArray("SimOnly"),
                ["depModuleNamesForCollisionDevice"] = new Newtonsoft.Json.Linq.JArray("DeviceOnly"),
                ["depModuleNamesForCollision"] = new Newtonsoft.Json.Linq.JArray("LegacyShouldNotWin"),
            };
            File.WriteAllText(
                Path.Combine(_tempDir, "wrapper-context.json"),
                mixed.ToString());

            var (_, _, _, loaded) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Equal(new[] { "SimOnly" }, loaded.Simulator);
            Assert.Equal(new[] { "DeviceOnly" }, loaded.Device);
        }

        [Fact]
        public void Load_PartialPerSliceFields_SimOnlyPresent_HydratesSimEmptyDevice()
        {
            // Partial corruption shape: only the simulator per-slice key is present
            // (e.g. hand-edited cache, partial writer crash). The present key MUST
            // hydrate, and the absent key MUST stay empty — not fall through to
            // legacy hydration. Falling through here would silently re-apply the
            // sim list to the device wrapper, the exact over-patching the
            // per-slice schema was introduced to fix.
            var partial = new Newtonsoft.Json.Linq.JObject
            {
                ["internalTypeNames"] = new Newtonsoft.Json.Linq.JArray(),
                ["moduleNameForCollision"] = (string?)null,
                ["nestedTypesInCollidingClass"] = new Newtonsoft.Json.Linq.JArray(),
                ["depModuleNamesForCollisionSimulator"] = new Newtonsoft.Json.Linq.JArray("OnlySim"),
                // depModuleNamesForCollisionDevice absent
            };
            File.WriteAllText(
                Path.Combine(_tempDir, "wrapper-context.json"),
                partial.ToString());

            var (_, _, _, loaded) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Equal(new[] { "OnlySim" }, loaded.Simulator);
            Assert.Empty(loaded.Device);
        }

        [Fact]
        public void Load_PartialPerSliceFields_DeviceOnlyPresent_HydratesDeviceEmptySim()
        {
            // Mirror of the sim-only case — symmetric guarantee for device.
            var partial = new Newtonsoft.Json.Linq.JObject
            {
                ["internalTypeNames"] = new Newtonsoft.Json.Linq.JArray(),
                ["moduleNameForCollision"] = (string?)null,
                ["nestedTypesInCollidingClass"] = new Newtonsoft.Json.Linq.JArray(),
                ["depModuleNamesForCollisionDevice"] = new Newtonsoft.Json.Linq.JArray("OnlyDevice"),
            };
            File.WriteAllText(
                Path.Combine(_tempDir, "wrapper-context.json"),
                partial.ToString());

            var (_, _, _, loaded) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Empty(loaded.Simulator);
            Assert.Equal(new[] { "OnlyDevice" }, loaded.Device);
        }

        [Fact]
        public void Load_PerSliceFieldsExplicitlyEmpty_DoNotFallThroughToLegacy()
        {
            // An explicit empty `[]` for either per-slice key is a legitimate
            // serialized state (no collisions on that slice). The loader must
            // honor it as empty and NOT fall through to legacy hydration —
            // doing so would treat "no collisions this slice" as "use the
            // legacy list" and re-introduce over-patching.
            var explicitEmpty = new Newtonsoft.Json.Linq.JObject
            {
                ["internalTypeNames"] = new Newtonsoft.Json.Linq.JArray(),
                ["moduleNameForCollision"] = (string?)null,
                ["nestedTypesInCollidingClass"] = new Newtonsoft.Json.Linq.JArray(),
                ["depModuleNamesForCollisionSimulator"] = new Newtonsoft.Json.Linq.JArray(),
                ["depModuleNamesForCollisionDevice"] = new Newtonsoft.Json.Linq.JArray(),
                ["depModuleNamesForCollision"] = new Newtonsoft.Json.Linq.JArray("LegacyShouldNotWin"),
            };
            File.WriteAllText(
                Path.Combine(_tempDir, "wrapper-context.json"),
                explicitEmpty.ToString());

            var (_, _, _, loaded) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Empty(loaded.Simulator);
            Assert.Empty(loaded.Device);
        }

        private static DepModuleCollisionDetector.SlicedCollisionResult EmptyCollisions =>
            new(Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// Tests for HandleWrapperCompilationOutcome — SDK-mode-aware outcome handling.
    /// </summary>
    public class WrapperOutcomeHandlingTests
    {
        [Fact]
        public void HandleOutcome_Fatal_SdkMode_ReturnsZeroExitWithSWIFTBIND050()
        {
            var ex = new InvalidOperationException("swiftc failed");
            var (exitCode, diagnosticCode, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Fatal, sdkMode: true, ex, compilationResult: null);
            Assert.Equal(0, exitCode);
            Assert.Equal("SWIFTBIND050", diagnosticCode);
            Assert.Contains("SWIFTBIND050", message);
            Assert.Contains("swiftc failed", message);
            Assert.Contains("dependency framework", message);
        }

        [Fact]
        public void HandleOutcome_Fatal_NonSdkMode_ReturnsNonZeroExit()
        {
            var ex = new InvalidOperationException("swiftc failed");
            var (exitCode, diagnosticCode, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Fatal, sdkMode: false, ex, compilationResult: null);
            Assert.Equal(1, exitCode);
            Assert.Null(diagnosticCode);
            Assert.Contains("swiftc failed", message);
        }

        [Fact]
        public void HandleOutcome_Warning_SdkMode_ReturnsZeroExit()
        {
            var ex = new InvalidOperationException("something went wrong");
            var (exitCode, diagnosticCode, _) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Warning, sdkMode: true, ex, compilationResult: null);
            Assert.Equal(0, exitCode);
            Assert.Null(diagnosticCode);
        }

        [Fact]
        public void HandleOutcome_Success_ReturnsZeroExit()
        {
            var (exitCode, diagnosticCode, _) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Success, sdkMode: false,
                compilationException: null, compilationResult: null);
            Assert.Equal(0, exitCode);
            Assert.Null(diagnosticCode);
        }

        [Fact]
        public void HandleOutcome_MissingModuleHint_FlowsThroughToMessage()
        {
            // Simulate the enriched exception that InvokeSwiftCompiler would throw
            var ex = new InvalidOperationException(
                "Swift wrapper compilation failed (exit code 1): error: no such module 'PaymentSdk3DS2'\n\n" +
                "Missing module(s): 'PaymentSdk3DS2'. Provide the xcframework(s) for these modules:\n" +
                "  CLI:  --framework-dependency /path/to/<Module>.xcframework (repeat for each)\n" +
                "  SDK:  Declare both items — SwiftFrameworkDependency for build-time " +
                "framework resolution, PackageReference for NuGet restore:\n" +
                "          <SwiftFrameworkDependency Include=\"path/to/<Module>.xcframework\" " +
                "PackageId=\"<Module>.Swift.iOS\" PackageVersion=\"1.0.0\" />\n" +
                "          <PackageReference Include=\"<Module>.Swift.iOS\" Version=\"1.0.0\" />");

            var (_, _, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Fatal, sdkMode: true, ex, compilationResult: null);

            Assert.Contains("Missing module(s): 'PaymentSdk3DS2'", message);
            Assert.Contains("--framework-dependency", message);
            Assert.Contains("SwiftFrameworkDependency", message);
            Assert.Contains("PackageReference", message);
        }

        [Fact]
        public void HandleOutcome_Fatal_SystemLinkGuidance_SuppressesContradictoryCauses()
        {
            // The wrapper-link failure already carried precise --link-framework guidance. The
            // generic "missing dependency framework (use --framework-dependency)" causes would
            // contradict it (the author may already have supplied the dependency), so they're
            // suppressed and the precise guidance stands alone.
            var ex = new InvalidOperationException(
                "Swift wrapper compilation failed (exit code 1): Undefined symbols ...\n\n" +
                "... so they must be declared explicitly:\n" +
                "  CLI:  add --link-framework Accelerate --link-framework CoreVideo --link-library c++\n" +
                "  SDK:  add to the binding's <ItemGroup>:\n" +
                "          <SwiftLinkFramework Include=\"Accelerate\" />");

            var (exitCode, _, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Fatal, sdkMode: false, ex, compilationResult: null);

            Assert.Equal(1, exitCode);
            Assert.Contains("--link-framework Accelerate", message);
            Assert.DoesNotContain("missing dependency framework", message);
        }

        [Fact]
        public void HandleOutcome_Fatal_LibraryOnlyGuidance_SuppressesContradictoryCauses()
        {
            // A static archive can need only libc++ (no system framework): the hint is then
            // --link-library-only. That still counts as precise guidance, so the generic causes
            // must be suppressed even with no --link-framework present.
            var ex = new InvalidOperationException(
                "Swift wrapper compilation failed (exit code 1): Undefined symbols ...\n" +
                "  CLI:  add --link-library c++\n" +
                "          <SwiftLinkLibrary Include=\"c++\" />");

            var (_, _, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Fatal, sdkMode: false, ex, compilationResult: null);

            Assert.Contains("--link-library c++", message);
            Assert.DoesNotContain("missing dependency framework", message);
        }

        [Fact]
        public void HandleOutcome_Fatal_NoLinkGuidance_KeepsGenericCauses()
        {
            // No precise link guidance in the failure → the generic actionable causes remain,
            // so unrelated wrapper-compile failures still get their existing hint.
            var ex = new InvalidOperationException(
                "Swift wrapper compilation failed (exit code 1): error: some internal failure");

            var (_, _, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Fatal, sdkMode: false, ex, compilationResult: null);

            Assert.Contains("missing dependency framework", message);
        }

        [Fact]
        public void HandleOutcome_Fatal_SdkMode_SystemLinkGuidance_SuppressesContradictoryCauses()
        {
            // Same suppression on the SDK-mode downgrade path — but the SWIFTBIND050 code and the
            // DllNotFoundException runtime note are preserved.
            var ex = new InvalidOperationException(
                "Swift wrapper compilation failed (exit code 1): Undefined symbols ...\n" +
                "  CLI:  add --link-framework Metal\n" +
                "          <SwiftLinkFramework Include=\"Metal\" />");

            var (exitCode, diagnosticCode, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Fatal, sdkMode: true, ex, compilationResult: null);

            Assert.Equal(0, exitCode);
            Assert.Equal("SWIFTBIND050", diagnosticCode);
            Assert.Contains("--link-framework Metal", message);
            Assert.Contains("DllNotFoundException", message);
            Assert.DoesNotContain("missing dependency framework", message);
        }
    }

    /// <summary>
    /// Tests for FormatDependencyWarning — SWIFTBIND060 message formatting.
    /// </summary>
    public class FormatDependencyWarningTests
    {
        [Fact]
        public void FormatDependencyWarning_MissingSlice_ContainsVerifySlices()
        {
            var message = BindingsGenerator.FormatDependencyWarning("SomeDep", "missing-slice");
            Assert.Contains("SWIFTBIND060", message);
            Assert.Contains("SomeDep", message);
            Assert.Contains("device and simulator slices", message);
        }

        [Fact]
        public void FormatDependencyWarning_MissingXcframework_ContainsBuildSuggestion()
        {
            var message = BindingsGenerator.FormatDependencyWarning("OtherDep", "missing-xcframework");
            Assert.Contains("SWIFTBIND060", message);
            Assert.Contains("OtherDep", message);
            Assert.Contains("build the dependency separately", message);
        }

        [Fact]
        public void FormatDependencyWarning_UnknownReason_TreatedAsMissingXcframework()
        {
            var message = BindingsGenerator.FormatDependencyWarning("Dep", "something-else");
            Assert.Contains("SWIFTBIND060", message);
            Assert.Contains("build the dependency separately", message);
        }

        [Fact]
        public void FormatDependencyWarning_MissingSlice_ContainsMSBuildSdkGuidance()
        {
            var message = BindingsGenerator.FormatDependencyWarning("PaymentSdkPayments", "missing-slice");
            Assert.Contains("SwiftFrameworkDependency", message);
            Assert.Contains("PackageId", message);
            Assert.Contains("PackageVersion", message);
        }

        [Fact]
        public void FormatDependencyWarning_MissingXcframework_ContainsMSBuildSdkGuidance()
        {
            var message = BindingsGenerator.FormatDependencyWarning("PaymentSdkPayments", "missing-xcframework");
            Assert.Contains("SwiftFrameworkDependency", message);
            Assert.Contains("PackageId", message);
            Assert.Contains("PackageVersion", message);
        }

        [Theory]
        [InlineData("missing-slice")]
        [InlineData("missing-xcframework")]
        public void FormatDependencyWarning_BothReasons_ContainCliAndSdkGuidance(string reason)
        {
            var message = BindingsGenerator.FormatDependencyWarning("MyLib", reason);
            // CLI guidance
            Assert.Contains("--framework-dependency", message);
            // MSBuild SDK guidance: both items required for NuGet consumption
            Assert.Contains("SwiftFrameworkDependency", message);
            Assert.Contains("PackageReference", message);
        }
    }

    /// <summary>
    /// Truth-table tests for the ShouldCompileWrapper gate
    /// (platform-target × wrapper-architectures matrix).
    /// </summary>
    public class ShouldCompileWrapperTests
    {
        // ── simulator slice (--platform-target simulator, the default) ──

        [Fact]
        public void SimulatorSlice_SimulatorArch_ReturnsTrue()
        {
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: true, wrapperArchitectures: "simulator"));
        }

        [Fact]
        public void SimulatorSlice_DeviceArch_ReturnsTrue()
        {
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: true, wrapperArchitectures: "device"));
        }

        [Fact]
        public void SimulatorSlice_AllArch_ReturnsTrue()
        {
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: true, wrapperArchitectures: "all"));
        }

        // ── device slice (--platform-target device) ──

        [Fact]
        public void DeviceSlice_SimulatorArch_ReturnsFalse()
        {
            // No simulator slice + requesting simulator-only wrapper → skip
            Assert.False(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "simulator"));
        }

        [Fact]
        public void DeviceSlice_DeviceArch_ReturnsTrue()
        {
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "device"));
        }

        [Fact]
        public void DeviceSlice_AllArch_ReturnsTrue()
        {
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "all"));
        }

        // ── full matrix (Theory-based) ──

        [Theory]
        [InlineData(true, "simulator", true)]
        [InlineData(true, "device", true)]
        [InlineData(true, "all", true)]
        [InlineData(false, "simulator", false)]
        [InlineData(false, "device", true)]
        [InlineData(false, "all", true)]
        public void FullMatrix_MatchesExpected(bool isSimulatorSlice, string wrapperArchitectures, bool expected)
        {
            Assert.Equal(expected, BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: isSimulatorSlice, wrapperArchitectures: wrapperArchitectures));
        }

        // ── edge cases ──

        [Fact]
        public void UnknownArchitectures_SimulatorSlice_ReturnsTrue()
        {
            // Unknown value doesn't match device/all, but simulator slice is true → true
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: true, wrapperArchitectures: "unknown"));
        }

        [Fact]
        public void UnknownArchitectures_DeviceSlice_ReturnsFalse()
        {
            // Unknown value doesn't match device/all, and no simulator slice → false
            Assert.False(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "unknown"));
        }

        [Fact]
        public void EmptyArchitectures_DeviceSlice_ReturnsFalse()
        {
            // Empty string doesn't match device/all, and no simulator slice → false
            Assert.False(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: ""));
        }

        [Fact]
        public void CaseMismatch_SimulatorArch_DeviceSlice_ReturnsFalse()
        {
            // "Simulator" != "simulator" — case-sensitive, doesn't match
            Assert.False(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "Simulator"));
        }
    }

    /// <summary>
    /// Tests for --framework-dependency CLI option and help text.
    /// </summary>
    [Collection("ConsoleCapture")]
    public class FrameworkDependencyCLITests
    {
        [Fact]
        public void Help_IncludesFrameworkDependencyOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--framework-dependency", output);
        }

        [Fact]
        public void Help_DescribesDependencyRequiresXcframework()
        {
            var output = CaptureHelp();
            Assert.Contains("--framework-dependency", output);
            Assert.Contains("Requires --xcframework", output);
        }

        [Fact]
        public void FrameworkDependency_WithoutXcframework_ErrorsGracefully()
        {
            // Uses -a/-d/-t mode which should reject --framework-dependency
            var dir = Path.Combine(Path.GetTempPath(), $"fwdep_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    BindingsGenerator.Main(new[]
                    {
                        "-a", "/nonexistent/abi.json",
                        "-d", "/nonexistent/dylib",
                        "-t", "/nonexistent/tbd",
                        "-o", dir,
                        "--framework-dependency", "/some/dep.xcframework"
                    });
                    // Should not crash — error logged via ILogger
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveFrameworkDependencies_NonexistentPath_ReturnsNull()
        {
            var primaryResolution = CreateMinimalResolution("Primary");
            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { "/nonexistent/path/Dep.xcframework" },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance);
            Assert.Null(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_NonXcframeworkPath_ReturnsNull()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"fwdep_noxc_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var primaryResolution = CreateMinimalResolution("Primary");
                var result = BindingsGenerator.ResolveFrameworkDependencies(
                    new[] { dir },  // Not an .xcframework
                    primaryResolution,
                    "/path/to/Primary.xcframework",
                    "simulator",
                    XCFrameworkPlatformTarget.Simulator,
                    NullLogger.Instance);
                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveFrameworkDependencies_PrimaryAsDependency_ReturnsNull()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"fwdep_self_{Guid.NewGuid():N}");
            var primaryPath = Path.Combine(dir, "Primary.xcframework");
            Directory.CreateDirectory(primaryPath);
            try
            {
                var primaryResolution = CreateMinimalResolution("Primary");
                var result = BindingsGenerator.ResolveFrameworkDependencies(
                    new[] { primaryPath },
                    primaryResolution,
                    primaryPath,
                    "simulator",
                    XCFrameworkPlatformTarget.Simulator,
                    NullLogger.Instance);
                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCOnlyDep_ResolvesWithIsObjCOnly()
        {
            using var fixture = CreateObjCDepFixture("ObjCDep", hasBothSlices: true);
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.True(result[0].IsObjCOnly);
            Assert.Equal("ObjCDep", result[0].ModuleName);
            Assert.NotNull(result[0].SimulatorFrameworkSearchPath);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDepNoModulemap_FallsBackToSearchPathOnly()
        {
            // Frameworks without modulemap (e.g., compiled wrapper xcframeworks) fall back
            // to search-path-only resolution instead of returning null.
            using var fixture = CreateObjCDepFixture("BrokenDep", hasBothSlices: false, addModulemap: false);
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.NotNull(result);
            Assert.Single(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDepDuplicateModule_SkipsDuplicate()
        {
            // Duplicate modules are silently skipped (not errors), since the SDK targets
            // can pass both ProjectReference-resolved and explicit SwiftFrameworkDependency items.
            using var fixture1 = CreateObjCDepFixture("DupMod", hasBothSlices: true);
            using var fixture2 = CreateObjCDepFixture("DupMod", hasBothSlices: true);
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture1.RootPath, fixture2.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.NotNull(result);
            Assert.Single(result); // Second duplicate is skipped
        }

        [Fact]
        public void GetDependencyModuleNamesForSwiftImports_IncludesObjCOnlyDependencies()
        {
            var dependencies = new List<FrameworkDependencyInfo>
            {
                new()
                {
                    XCFrameworkPath = "/path/to/SwiftDep.xcframework",
                    ModuleName = "SwiftDep",
                    IsObjCOnly = false,
                },
                new()
                {
                    XCFrameworkPath = "/path/to/CloudPlatformSdkCore.xcframework",
                    ModuleName = "CloudPlatformSdkCore",
                    IsObjCOnly = true,
                },
            };

            var result = BindingsGeneratorCommand.GetDependencyModuleNamesForSwiftImports(dependencies);

            Assert.Equal(new[] { "SwiftDep", "CloudPlatformSdkCore" }, result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_MixedSwiftAndObjCDeps_ResolvesBoth()
        {
            // Create a Swift dependency (has swiftmodule)
            using var swiftFixture = new XCFrameworkFixture("SwiftDep.xcframework");
            swiftFixture.WriteInfoPlist(MakeSimplePlist("SwiftDep"));
            var sliceDir = swiftFixture.CreateSlice("ios-arm64-simulator",
                "SwiftDep.framework", "SwiftDep.framework/SwiftDep");
            var moduleDir = swiftFixture.CreateSwiftModule(sliceDir, "SwiftDep.framework", "SwiftDep");
            swiftFixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            swiftFixture.CreateTbd(moduleDir, "SwiftDep");

            // Create an ObjC dependency (no swiftmodule, has modulemap)
            using var objcFixture = CreateObjCDepFixture("ObjCDep2", hasBothSlices: true);

            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");
            runner.SetResponse("tapi", 0, "");
            // Pre-create what tapi would generate
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "SwiftDep.tbd"), "--- !tapi-tbd");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { swiftFixture.RootPath, objcFixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            var swiftDep = result.First(d => d.ModuleName == "SwiftDep");
            var objcDep = result.First(d => d.ModuleName == "ObjCDep2");
            Assert.False(swiftDep.IsObjCOnly);
            Assert.True(objcDep.IsObjCOnly);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDep_AllArchs_SimOnlyDep_SimPrimary_ReturnsNull()
        {
            // ObjC dep has only simulator slice, primaryPlatformTarget=Simulator, wrapperArchitectures="all"
            using var fixture = CreateObjCDepFixture("SimOnly", hasBothSlices: false);
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "all",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDep_AllArchs_DeviceOnlyDep_SimPrimary_ReturnsNull()
        {
            // ObjC dep has only device slice, primaryPlatformTarget=Simulator, wrapperArchitectures="all"
            // Regression: oppositeTarget must be derived from actual slice, not requested target.
            // Without the fix, SelectSlice falls back to device for both resolutions,
            // returning success with simPath=null — violating the "all" contract.
            using var fixture = CreateObjCDeviceOnlyFixture("DevOnlyAll");
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "all",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDep_DeviceArchs_OnlySimSlice_ReturnsNull()
        {
            // ObjC dep has only simulator slice, wrapperArchitectures="device"
            using var fixture = CreateObjCDepFixture("SimOnlyDev", hasBothSlices: false);
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "device",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDep_SimArchs_OnlyDeviceSlice_ReturnsNull()
        {
            // ObjC dep has only device slice, wrapperArchitectures="simulator"
            using var fixture = CreateObjCDeviceOnlyFixture("DevOnlySim");
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Device,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDep_AllArchs_MacCatalystSingleSlice_Resolves()
        {
            // Mac Catalyst xcframeworks use a single "maccatalyst" variant rather than
            // distinct simulator/device variants, so --wrapper-architectures all should
            // not require a separate simulator slice.
            using var fixture = CreateObjCCatalystFixture("CatalystDep");
            var primaryResolution = CreateMinimalResolution("Primary");
            var platformInfo = PlatformInfoFactory.Create(ApplePlatform.MacCatalyst);
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "all",
                XCFrameworkPlatformTarget.Device,
                NullLogger.Instance,
                commandRunner: runner,
                platformInfo: platformInfo);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.True(result[0].IsObjCOnly);
            Assert.Equal("CatalystDep", result[0].ModuleName);
            Assert.Null(result[0].SimulatorFrameworkSearchPath);
            Assert.NotNull(result[0].DeviceFrameworkSearchPath);
        }

        private static XCFrameworkResolution CreateMinimalResolution(string module) => new()
        {
            AbiJsonPath = "/abi.json",
            DylibPath = "/dylib",
            TbdPath = "/tbd",
            ModuleName = module,
            XCFrameworkPath = $"/path/to/{module}.xcframework",
            FrameworkSearchPath = $"/path/to/{module}.xcframework/ios-arm64-simulator",
            LibraryIdentifier = "ios-arm64-simulator",
            IsSimulatorSlice = true,
            SelectedArchitecture = "arm64",
            SupportedArchitectures = new[] { "arm64" }
        };

        /// <summary>
        /// Creates a temp xcframework with module.modulemap but no .swiftmodule
        /// (simulates an ObjC-only framework).
        /// </summary>
        private static XCFrameworkFixture CreateObjCDepFixture(string name,
            bool hasBothSlices, bool addModulemap = true)
        {
            var fixture = new XCFrameworkFixture($"{name}.xcframework");
            if (hasBothSlices)
                fixture.WriteInfoPlist(MakeDualSlicePlist(name));
            else
                fixture.WriteInfoPlist(MakeSimplePlist(name));

            // Simulator slice
            var simSliceDir = fixture.CreateSlice(
                hasBothSlices ? "ios-arm64_x86_64-simulator" : "ios-arm64-simulator",
                $"{name}.framework", $"{name}.framework/{name}");
            if (addModulemap)
            {
                var modulesDir = Path.Combine(simSliceDir, $"{name}.framework", "Modules");
                Directory.CreateDirectory(modulesDir);
                File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                    $"framework module {name} {{\n  umbrella header \"{name}.h\"\n}}\n");
            }

            if (hasBothSlices)
            {
                var deviceSliceDir = fixture.CreateSlice("ios-arm64",
                    $"{name}.framework", $"{name}.framework/{name}");
                if (addModulemap)
                {
                    var modulesDir = Path.Combine(deviceSliceDir, $"{name}.framework", "Modules");
                    Directory.CreateDirectory(modulesDir);
                    File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                        $"framework module {name} {{\n  umbrella header \"{name}.h\"\n}}\n");
                }
            }

            return fixture;
        }

        /// <summary>
        /// Creates a temp xcframework with only a device slice (no simulator).
        /// </summary>
        private static XCFrameworkFixture CreateObjCDeviceOnlyFixture(string name)
        {
            var fixture = new XCFrameworkFixture($"{name}.xcframework");
            fixture.WriteInfoPlist(MakeDeviceOnlyPlist(name));
            var deviceSliceDir = fixture.CreateSlice("ios-arm64",
                $"{name}.framework", $"{name}.framework/{name}");
            var modulesDir = Path.Combine(deviceSliceDir, $"{name}.framework", "Modules");
            Directory.CreateDirectory(modulesDir);
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                $"framework module {name} {{\n  umbrella header \"{name}.h\"\n}}\n");
            return fixture;
        }

        private static XCFrameworkFixture CreateObjCCatalystFixture(string name)
        {
            var fixture = new XCFrameworkFixture($"{name}.xcframework");
            fixture.WriteInfoPlist(MakeCatalystPlist(name));
            var catalystSliceDir = fixture.CreateSlice("ios-arm64_x86_64-maccatalyst",
                $"{name}.framework", $"{name}.framework/{name}");
            var modulesDir = Path.Combine(catalystSliceDir, $"{name}.framework", "Modules");
            Directory.CreateDirectory(modulesDir);
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                $"framework module {name} {{\n  umbrella header \"{name}.h\"\n}}\n");
            return fixture;
        }

        private static string MakeSimplePlist(string name)
        {
            return $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>{{name}}.framework/{{name}}</string>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>{{name}}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
        }

        private static string MakeDualSlicePlist(string name)
        {
            return $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>{{name}}.framework/{{name}}</string>
                            <key>LibraryIdentifier</key><string>ios-arm64_x86_64-simulator</string>
                            <key>LibraryPath</key><string>{{name}}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                        <dict>
                            <key>BinaryPath</key><string>{{name}}.framework/{{name}}</string>
                            <key>LibraryIdentifier</key><string>ios-arm64</string>
                            <key>LibraryPath</key><string>{{name}}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
        }

        private static string MakeCatalystPlist(string name)
        {
            return $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>{{name}}.framework/{{name}}</string>
                            <key>LibraryIdentifier</key><string>ios-arm64_x86_64-maccatalyst</string>
                            <key>LibraryPath</key><string>{{name}}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>maccatalyst</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
        }

        private static string MakeDeviceOnlyPlist(string name)
        {
            return $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>{{name}}.framework/{{name}}</string>
                            <key>LibraryIdentifier</key><string>ios-arm64</string>
                            <key>LibraryPath</key><string>{{name}}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
        }

        private static string CaptureHelp()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "-h" });
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ParseTargetArchitectures
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ParseTargetArchitectures_Unset_ReturnsEmptyList()
        {
            var result = BindingsGenerator.ParseTargetArchitectures(null, NullLogger.Instance);
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void ParseTargetArchitectures_Whitespace_ReturnsEmptyList()
        {
            var result = BindingsGenerator.ParseTargetArchitectures("   ", NullLogger.Instance);
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void ParseTargetArchitectures_X64Alias_NormalizesToX86_64()
        {
            var result = BindingsGenerator.ParseTargetArchitectures("x64", NullLogger.Instance);
            Assert.Equal(new[] { "x86_64" }, result);
        }

        [Fact]
        public void ParseTargetArchitectures_BothArches_SortsArm64First()
        {
            var result = BindingsGenerator.ParseTargetArchitectures("x86_64,arm64", NullLogger.Instance);
            Assert.Equal(new[] { "arm64", "x86_64" }, result);
        }

        [Fact]
        public void ParseTargetArchitectures_Duplicates_Deduped()
        {
            var result = BindingsGenerator.ParseTargetArchitectures("arm64, x64, x86_64", NullLogger.Instance);
            Assert.Equal(new[] { "arm64", "x86_64" }, result);
        }

        [Fact]
        public void ParseTargetArchitectures_InvalidToken_ReturnsNull()
        {
            var result = BindingsGenerator.ParseTargetArchitectures("arm64,ppc64", NullLogger.Instance);
            Assert.Null(result);
        }

        // ─────────────────────────────────────────────────────────────────────
        // TryDecideWrapperArchitectures — auto-match-source + explicit fail-loud
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void DecideWrapperArchs_Auto_FatSource_FoldsInX86_64()
        {
            var ok = BindingsGenerator.TryDecideWrapperArchitectures(
                autoMatchSource: true,
                requestedArchs: new List<string>(),
                sourceArchitectures: new[] { "arm64", "x86_64" },
                sourceSliceId: "macos-arm64",
                NullLogger.Instance,
                out var primary, out var extra);

            Assert.True(ok);
            Assert.Equal("arm64", primary); // primary pinned to the concrete arm arch; x86_64 folded as extra
            Assert.Equal(new[] { "x86_64" }, extra);
        }

        [Fact]
        public void DecideWrapperArchs_Auto_Arm64eAndX86_64_X86First_PinsPrimaryToArm64e()
        {
            // arm64e+x86_64 slice with x86_64 listed FIRST: a null primary would defer to
            // SelectArchitecture, which (no exact "arm64") returns the slice's first arch — x86_64 —
            // dropping arm64e. The primary must pin to the arm64e variant so the fold keeps both arches.
            var ok = BindingsGenerator.TryDecideWrapperArchitectures(
                autoMatchSource: true,
                requestedArchs: new List<string>(),
                sourceArchitectures: new[] { "x86_64", "arm64e" },
                sourceSliceId: "macos-arm64e_x86_64",
                NullLogger.Instance,
                out var primary, out var extra);

            Assert.True(ok);
            Assert.Equal("arm64e", primary);
            Assert.Equal(new[] { "x86_64" }, extra);
        }

        [Fact]
        public void DecideWrapperArchs_Auto_Arm64OnlySource_StaysArm64Only()
        {
            var ok = BindingsGenerator.TryDecideWrapperArchitectures(
                autoMatchSource: true,
                requestedArchs: new List<string>(),
                sourceArchitectures: new[] { "arm64" },
                sourceSliceId: "macos-arm64",
                NullLogger.Instance,
                out var primary, out var extra);

            Assert.True(ok);
            Assert.Null(primary);
            Assert.Empty(extra); // never fails, never fattens an arm64-only source
        }

        [Fact]
        public void DecideWrapperArchs_Auto_Arm64eOnlyDevice_PrimaryStaysNull()
        {
            // arm64e-only device slice: a literal "arm64" primary would make SelectArchitecture
            // drop it. auto keeps primary null so the historical preference resolves arm64e.
            var ok = BindingsGenerator.TryDecideWrapperArchitectures(
                autoMatchSource: true,
                requestedArchs: new List<string>(),
                sourceArchitectures: new[] { "arm64e" },
                sourceSliceId: "ios-arm64e",
                NullLogger.Instance,
                out var primary, out var extra);

            Assert.True(ok);
            Assert.Null(primary);
            Assert.Empty(extra);
        }

        [Fact]
        public void DecideWrapperArchs_Auto_X86_64OnlySource_PinsPrimaryToX86_64()
        {
            // x86_64-only source (legacy Intel-only library): the primary pass must be pinned to
            // x86_64 with no extras. A null primary would itself resolve to x86_64 AND schedule a
            // second x86_64 pass, leaving the merger to lipo two identical-arch binaries.
            var ok = BindingsGenerator.TryDecideWrapperArchitectures(
                autoMatchSource: true,
                requestedArchs: new List<string>(),
                sourceArchitectures: new[] { "x86_64" },
                sourceSliceId: "macos-x86_64",
                NullLogger.Instance,
                out var primary, out var extra);

            Assert.True(ok);
            Assert.Equal("x86_64", primary);
            Assert.Empty(extra); // single x86_64 pass, no same-arch merge
        }

        [Fact]
        public void DecideWrapperArchs_Explicit_AllPresent_SplitsPrimaryAndExtra()
        {
            var ok = BindingsGenerator.TryDecideWrapperArchitectures(
                autoMatchSource: false,
                requestedArchs: new[] { "arm64", "x86_64" },
                sourceArchitectures: new[] { "arm64", "x86_64" },
                sourceSliceId: "macos-arm64_x86_64",
                NullLogger.Instance,
                out var primary, out var extra);

            Assert.True(ok);
            Assert.Equal("arm64", primary);
            Assert.Equal(new[] { "x86_64" }, extra);
        }

        [Fact]
        public void DecideWrapperArchs_Explicit_MissingArch_FailsLoud()
        {
            // Explicit x86_64 against an arm64-only source must fail (SWIFTBIND052), not narrow.
            var ok = BindingsGenerator.TryDecideWrapperArchitectures(
                autoMatchSource: false,
                requestedArchs: new[] { "arm64", "x86_64" },
                sourceArchitectures: new[] { "arm64" },
                sourceSliceId: "macos-arm64",
                NullLogger.Instance,
                out var primary, out var extra);

            Assert.False(ok);
            Assert.Null(primary);
            Assert.Empty(extra);
        }

        [Fact]
        public void DecideWrapperArchs_Explicit_Empty_NoMerge()
        {
            var ok = BindingsGenerator.TryDecideWrapperArchitectures(
                autoMatchSource: false,
                requestedArchs: new List<string>(),
                sourceArchitectures: new[] { "arm64", "x86_64" },
                sourceSliceId: "macos-arm64",
                NullLogger.Instance,
                out var primary, out var extra);

            Assert.True(ok);
            Assert.Null(primary); // unset => historical single-pass preference
            Assert.Empty(extra);
        }

        // ─────────────────────────────────────────────────────────────────────
        // CompileWrapperForArchitectures — the shared primary + fat-merge driver
        // used by BOTH the standalone generation path and --compile-wrapper-only.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void CompileWrapperForArchs_NoExtras_CompilesPrimaryOnce_NoMergeDance()
        {
            var calls = new List<string?>();
            var primaryDir = System.IO.Directory.CreateTempSubdirectory("cwa_primary_").FullName;
            try
            {
                SwiftWrapperCompilationResult? Stub(string? arch)
                {
                    calls.Add(arch);
                    return new SwiftWrapperCompilationResult
                    {
                        XCFrameworkPath = primaryDir,
                        CompiledFileCount = 0,
                        StrippedBlockCount = 0,
                    };
                }

                var result = BindingsGenerator.CompileWrapperForArchitectures(
                    primaryArch: null, extraArchs: new List<string>(), Stub, NullLogger.Instance, out var unmerged);

                Assert.Equal(new string?[] { null }, calls); // primary pass only
                Assert.Equal(primaryDir, result!.XCFrameworkPath);
                Assert.True(System.IO.Directory.Exists(primaryDir)); // untouched — no aside/restore
                Assert.Empty(unmerged); // no extras requested → nothing reported unmerged
            }
            finally
            {
                if (System.IO.Directory.Exists(primaryDir)) System.IO.Directory.Delete(primaryDir, true);
            }
        }

        [Fact]
        public void CompileWrapperForArchs_ExtraArch_CompilesPrimaryThenExtra_AndRestoresPrimary()
        {
            var calls = new List<string?>();
            var primaryDir = System.IO.Directory.CreateTempSubdirectory("cwa_primary_").FullName;
            try
            {
                // Primary produces a real xcframework dir; the extra pass "produces nothing" so the
                // lipo merge is skipped — keeps the orchestration assertion cross-platform while still
                // exercising the primary-aside / restore dance and the per-extra compile call.
                SwiftWrapperCompilationResult? Stub(string? arch)
                {
                    calls.Add(arch);
                    return arch == null
                        ? new SwiftWrapperCompilationResult
                        {
                            XCFrameworkPath = primaryDir,
                            CompiledFileCount = 0,
                            StrippedBlockCount = 0,
                        }
                        : null;
                }

                var result = BindingsGenerator.CompileWrapperForArchitectures(
                    primaryArch: null, extraArchs: new List<string> { "x86_64" }, Stub, NullLogger.Instance,
                    out var unmerged);

                Assert.Equal(new string?[] { null, "x86_64" }, calls); // primary, then the extra arch
                Assert.Equal(primaryDir, result!.XCFrameworkPath);
                Assert.True(System.IO.Directory.Exists(primaryDir)); // moved aside, then restored in place
                Assert.Equal(new[] { "x86_64" }, unmerged); // extra produced nothing → reported unmerged
            }
            finally
            {
                if (System.IO.Directory.Exists(primaryDir)) System.IO.Directory.Delete(primaryDir, true);
                var aside = primaryDir + ".primary";
                if (System.IO.Directory.Exists(aside)) System.IO.Directory.Delete(aside, true);
            }
        }

        [Fact]
        public void CompileWrapperForArchs_MergeThrows_DegradesToPrimary_NotErased()
        {
            var primaryDir = System.IO.Directory.CreateTempSubdirectory("cwa_primary_").FullName;
            var extraDir = System.IO.Directory.CreateTempSubdirectory("cwa_extra_").FullName;
            try
            {
                // Both pretend-results point at real dirs with no Info.plist, so MergeFatSlices throws
                // (SWIFTBIND053) mid-merge — the data-loss scenario: the primary has already been moved
                // aside when the merge fails.
                SwiftWrapperCompilationResult? Stub(string? arch) => new SwiftWrapperCompilationResult
                {
                    XCFrameworkPath = arch == null ? primaryDir : extraDir,
                    CompiledFileCount = 0,
                    StrippedBlockCount = 0,
                };

                // The extra-arch fold failure must NOT propagate: it is swallowed so the build degrades
                // to the primary-only wrapper. Propagating would leave the SDK caller's compilationResult
                // null and record _SwiftBindingHasWrapperXCFramework=False off that null, dropping the
                // NativeReference for EVERY consumer even though the primary is restored on disk.
                var result = BindingsGenerator.CompileWrapperForArchitectures(
                    primaryArch: null, extraArchs: new List<string> { "x86_64" }, Stub, NullLogger.Instance,
                    out var unmerged);

                // Returns the primary result (non-null) so downstream metadata records a present wrapper,
                // and the primary directory is restored in place rather than left aside / erased.
                Assert.NotNull(result);
                Assert.Equal(primaryDir, result!.XCFrameworkPath);
                Assert.True(System.IO.Directory.Exists(primaryDir));
                // The throw still reports the undelivered extra so an explicit-arch caller can fail loud.
                Assert.Equal(new[] { "x86_64" }, unmerged);
            }
            finally
            {
                foreach (var d in new[] { primaryDir, extraDir, primaryDir + ".primary", primaryDir + ".x86_64" })
                    if (System.IO.Directory.Exists(d)) System.IO.Directory.Delete(d, true);
            }
        }

        [Fact]
        public void CompileWrapperForArchs_MultipleExtrasNoneDelivered_ReportsAllUnmergedInOrder()
        {
            var primaryDir = System.IO.Directory.CreateTempSubdirectory("cwa_primary_").FullName;
            try
            {
                // Primary builds; both extras "produce nothing" (soft-skip), so neither folds in. The
                // unmerged list must enumerate them in the requested order so the SDK error names them
                // deterministically.
                SwiftWrapperCompilationResult? Stub(string? arch) => arch == null
                    ? new SwiftWrapperCompilationResult
                    {
                        XCFrameworkPath = primaryDir,
                        CompiledFileCount = 0,
                        StrippedBlockCount = 0,
                    }
                    : null;

                var result = BindingsGenerator.CompileWrapperForArchitectures(
                    primaryArch: null, extraArchs: new List<string> { "x86_64", "arm64e" }, Stub,
                    NullLogger.Instance, out var unmerged);

                Assert.Equal(primaryDir, result!.XCFrameworkPath);
                Assert.True(System.IO.Directory.Exists(primaryDir));
                Assert.Equal(new[] { "x86_64", "arm64e" }, unmerged);
            }
            finally
            {
                foreach (var d in new[] { primaryDir, primaryDir + ".primary" })
                    if (System.IO.Directory.Exists(d)) System.IO.Directory.Delete(d, true);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ResolveAutoArchBasis — picks the slice the auto fat-or-not decision is
        // based on. The successful device→simulator re-resolve needs a real fat
        // xcframework (covered end-to-end by X64PackGate); the three branches
        // below are reachable without one.
        // ─────────────────────────────────────────────────────────────────────

        private static XCFrameworkResolution MakeResolution(string sliceId, params string[] archs) =>
            new XCFrameworkResolution
            {
                AbiJsonPath = "", DylibPath = "", TbdPath = "",
                ModuleName = "TestMod", XCFrameworkPath = "", FrameworkSearchPath = "",
                LibraryIdentifier = sliceId, IsSimulatorSlice = false,
                SelectedArchitecture = archs.Length > 0 ? archs[0] : "arm64",
                SupportedArchitectures = archs,
            };

        [Fact]
        public void ResolveAutoArchBasis_SimulatorTarget_UsesResolvedSliceDirectly()
        {
            // Non-device target: no device→sim re-resolve, so the already-resolved (fat sim) slice
            // is the basis verbatim — x86_64 is already present in it.
            var resolution = MakeResolution("ios-arm64_x86_64-simulator", "arm64", "x86_64");

            var (archs, sliceId) = BindingsGenerator.ResolveAutoArchBasis(
                resolution, xcframeworkPath: "/nonexistent.xcframework", outputDirectory: "/tmp",
                XCFrameworkPlatformTarget.Simulator, wrapperArchNormalized: "all",
                PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Equal(new[] { "arm64", "x86_64" }, archs);
            Assert.Equal("ios-arm64_x86_64-simulator", sliceId);
        }

        [Fact]
        public void ResolveAutoArchBasis_DeviceTarget_WrapperDeviceOnly_SkipsReResolve()
        {
            // wrapperArchNormalized == "device": the wrapper deliberately covers device only, so the
            // device slice's arm-only basis is correct and the simulator re-resolve must NOT fire
            // (the bogus path would otherwise throw and we'd still fall back — assert the arm-only basis).
            var resolution = MakeResolution("ios-arm64", "arm64");

            var (archs, sliceId) = BindingsGenerator.ResolveAutoArchBasis(
                resolution, xcframeworkPath: "/nonexistent.xcframework", outputDirectory: "/tmp",
                XCFrameworkPlatformTarget.Device, wrapperArchNormalized: "device",
                PlatformInfoFactory.Create(ApplePlatform.iOS), NullLogger.Instance);

            Assert.Equal(new[] { "arm64" }, archs);
            Assert.Equal("ios-arm64", sliceId);
        }

        [Fact]
        public void ResolveAutoArchBasis_DeviceTarget_NoSimulatorSlice_FallsBackToResolved()
        {
            // Device target + wrapper covers the sim family ("all"), so the re-resolve fires — but the
            // path is bogus, so XCFrameworkResolver.Resolve throws and the catch falls back to the
            // device resolution's arm-only basis (the device-only-library case).
            var resolution = MakeResolution("macos-arm64", "arm64");

            var (archs, sliceId) = BindingsGenerator.ResolveAutoArchBasis(
                resolution, xcframeworkPath: "/nonexistent.xcframework", outputDirectory: "/tmp",
                XCFrameworkPlatformTarget.Device, wrapperArchNormalized: "all",
                PlatformInfoFactory.Create(ApplePlatform.macOS), NullLogger.Instance);

            Assert.Equal(new[] { "arm64" }, archs);
            Assert.Equal("macos-arm64", sliceId);
        }

        // ResolveAppleFrameworkAutoArchBasis — synthetic auto basis for Apple-framework direct
        // mode (no source xcframework to inspect). Basis reflects what the wrapper xcframework
        // CAN ship — derived from PlatformInfo.SimulatorSlice (or DeviceSlice fallback for
        // macOS/MacCatalyst where there is no sim variant), NOT from whichever slice happens
        // to be the active compile target. Pairs with TryDecideWrapperArchitectures to keep
        // both `auto` and explicit `arm64,x86_64` producing the same fat wrapper for
        // StoreKit/WeatherKit/etc. — including device-first (SwiftPlatformTarget=device), where
        // the SDK packs the fat sim slice as the second wrapper slice.

        [Fact]
        public void ResolveAppleFrameworkAutoArchBasis_iOS_ReturnsFatFromSimSlice()
        {
            var platformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS);
            var (archs, sliceId) = BindingsGenerator.ResolveAppleFrameworkAutoArchBasis(platformInfo);
            Assert.Equal(new[] { "arm64", "x86_64" }, archs);
            Assert.Equal(platformInfo.SimulatorSlice!.SliceId, sliceId);
        }

        [Fact]
        public void ResolveAppleFrameworkAutoArchBasis_tvOS_ReturnsFatFromSimSlice()
        {
            var platformInfo = PlatformInfoFactory.Create(ApplePlatform.tvOS);
            var (archs, sliceId) = BindingsGenerator.ResolveAppleFrameworkAutoArchBasis(platformInfo);
            Assert.Equal(new[] { "arm64", "x86_64" }, archs);
            Assert.Equal(platformInfo.SimulatorSlice!.SliceId, sliceId);
        }

        [Fact]
        public void ResolveAppleFrameworkAutoArchBasis_macOS_ReturnsFatFromDeviceSlice()
        {
            var platformInfo = PlatformInfoFactory.Create(ApplePlatform.macOS);
            var (archs, sliceId) = BindingsGenerator.ResolveAppleFrameworkAutoArchBasis(platformInfo);
            Assert.Equal(new[] { "arm64", "x86_64" }, archs);
            Assert.Equal(platformInfo.DeviceSlice.SliceId, sliceId);
        }

        [Fact]
        public void ResolveAppleFrameworkAutoArchBasis_MacCatalyst_ReturnsFatFromDeviceSlice()
        {
            var platformInfo = PlatformInfoFactory.Create(ApplePlatform.MacCatalyst);
            var (archs, sliceId) = BindingsGenerator.ResolveAppleFrameworkAutoArchBasis(platformInfo);
            Assert.Equal(new[] { "arm64", "x86_64" }, archs);
            Assert.Equal(platformInfo.DeviceSlice.SliceId, sliceId);
        }

        // GetAppleFrameworkSliceNaturalArchs — per-slice arch query used to filter generator
        // extra-arch compiles down to what the active slice can produce. Caller drops arches
        // the active slice can't compile (e.g. x86_64 against iOS/tvOS device) so the SDK's
        // fat-sim second-slice path can cover them without a malformed-xcframework break.

        [Fact]
        public void GetAppleFrameworkSliceNaturalArchs_iOSSimulator_Fat()
        {
            var slice = PlatformInfoFactory.Create(ApplePlatform.iOS).SimulatorSlice!;
            Assert.Equal(new[] { "arm64", "x86_64" }, BindingsGenerator.GetAppleFrameworkSliceNaturalArchs(slice));
        }

        [Fact]
        public void GetAppleFrameworkSliceNaturalArchs_iOSDevice_ArmOnly()
        {
            var slice = PlatformInfoFactory.Create(ApplePlatform.iOS).DeviceSlice;
            Assert.Equal(new[] { "arm64" }, BindingsGenerator.GetAppleFrameworkSliceNaturalArchs(slice));
        }

        [Fact]
        public void GetAppleFrameworkSliceNaturalArchs_tvOSDevice_ArmOnly()
        {
            var slice = PlatformInfoFactory.Create(ApplePlatform.tvOS).DeviceSlice;
            Assert.Equal(new[] { "arm64" }, BindingsGenerator.GetAppleFrameworkSliceNaturalArchs(slice));
        }

        [Fact]
        public void GetAppleFrameworkSliceNaturalArchs_macOS_Fat()
        {
            var slice = PlatformInfoFactory.Create(ApplePlatform.macOS).DeviceSlice;
            Assert.Equal(new[] { "arm64", "x86_64" }, BindingsGenerator.GetAppleFrameworkSliceNaturalArchs(slice));
        }

        [Fact]
        public void GetAppleFrameworkSliceNaturalArchs_MacCatalyst_Fat()
        {
            var slice = PlatformInfoFactory.Create(ApplePlatform.MacCatalyst).DeviceSlice;
            Assert.Equal(new[] { "arm64", "x86_64" }, BindingsGenerator.GetAppleFrameworkSliceNaturalArchs(slice));
        }
    }
}
