// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. ABI Diff Detection Tests

    public class SimulatorOnlyDetectTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        private static string WriteAbiJson(string dir, string filename, string json)
        {
            var path = Path.Combine(dir, filename);
            File.WriteAllText(path, json);
            return path;
        }

        private static string MakeAbiJson(params string[] typeBlocks)
        {
            return "{\"ABIRoot\":{\"children\":[" + string.Join(",", typeBlocks) + "]}}";
        }

        private static string MakeTypeDecl(string name, params string[] members)
        {
            return "{\"kind\":\"TypeDecl\",\"name\":\"" + name + "\",\"children\":[" + string.Join(",", members) + "]}";
        }

        private static string MakeFunction(string name, string mangledName)
        {
            return "{\"kind\":\"Function\",\"name\":\"" + name + "\",\"mangledName\":\"" + mangledName + "\"}";
        }

        private static string MakeVar(string name, string mangledName)
        {
            return "{\"kind\":\"Var\",\"name\":\"" + name + "\",\"mangledName\":\"" + mangledName + "\"}";
        }

        private static string MakeConstructor(string name, string mangledName)
        {
            return "{\"kind\":\"Constructor\",\"name\":\"" + name + "\",\"mangledName\":\"" + mangledName + "\"}";
        }

        /// <summary>
        /// Computes the hash as it appears in thunk .globl lines (lowercase hex).
        /// Applies constructor patching (c→C) when isConstructor=true.
        /// </summary>
        private static string ThunkHash(string mangledName, bool isConstructor = false)
        {
            var patched = isConstructor && mangledName.Length > 0 && mangledName[^1] == 'c'
                ? mangledName[..^1] + "C"
                : mangledName;
            return EmitterUtility.DeterministicHash8(patched).ToLowerInvariant();
        }

        /// <summary>
        /// Computes the hash as it appears in @_cdecl wrapper names (uppercase hex).
        /// Applies constructor patching (c→C) when isConstructor=true.
        /// </summary>
        private static string WrapperHash(string mangledName, bool isConstructor = false)
        {
            var patched = isConstructor && mangledName.Length > 0 && mangledName[^1] == 'c'
                ? mangledName[..^1] + "C"
                : mangledName;
            return EmitterUtility.DeterministicHash8(patched);
        }

        [Fact]
        public void Detect_FindsSimulatorOnlyFunctions()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var sim = WriteAbiJson(dir, "sim.json", MakeAbiJson(
                    MakeTypeDecl("MyType",
                        MakeFunction("shared", "$s_shared"),
                        MakeFunction("simOnly", "$s_simOnly"))));
                var dev = WriteAbiJson(dir, "dev.json", MakeAbiJson(
                    MakeTypeDecl("MyType",
                        MakeFunction("shared", "$s_shared"))));

                var result = SimulatorOnlyMemberDetector.Detect(sim, dev, Logger);
                Assert.Equal(1, result.Count);
                Assert.Contains("MyType.simOnly", result.QualifiedNames);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Detect_FindsSimulatorOnlyConstructors()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var sim = WriteAbiJson(dir, "sim.json", MakeAbiJson(
                    MakeTypeDecl("MyType",
                        MakeConstructor("init", "$s_init_Ac"),
                        MakeFunction("shared", "$s_shared"))));
                var dev = WriteAbiJson(dir, "dev.json", MakeAbiJson(
                    MakeTypeDecl("MyType",
                        MakeFunction("shared", "$s_shared"))));

                var result = SimulatorOnlyMemberDetector.Detect(sim, dev, Logger);
                Assert.Equal(1, result.Count);
                Assert.Contains("MyType.init", result.QualifiedNames);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Detect_ConstructorPatch_cToC()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                // Constructor mangled name ends with 'c' → should be patched to 'C'
                var mangledName = "$s_MyType_initABc";
                var sim = WriteAbiJson(dir, "sim.json", MakeAbiJson(
                    MakeTypeDecl("MyType",
                        MakeConstructor("init", mangledName))));
                var dev = WriteAbiJson(dir, "dev.json", MakeAbiJson(
                    MakeTypeDecl("MyType")));

                var result = SimulatorOnlyMemberDetector.Detect(sim, dev, Logger);
                Assert.Equal(1, result.Count);

                // Verify the hash uses the patched mangled name (C, not c)
                var expectedHash = EmitterUtility.DeterministicHash8("$s_MyType_initABC");
                Assert.Single(result._entries);
                Assert.Equal(expectedHash, result._entries[0].Hash);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Detect_FindsSimulatorOnlyVars()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var sim = WriteAbiJson(dir, "sim.json", MakeAbiJson(
                    MakeTypeDecl("MyType",
                        MakeVar("simProp", "$s_simProp"))));
                var dev = WriteAbiJson(dir, "dev.json", MakeAbiJson(
                    MakeTypeDecl("MyType")));

                var result = SimulatorOnlyMemberDetector.Detect(sim, dev, Logger);
                Assert.Equal(1, result.Count);
                Assert.Contains("MyType.simProp", result.QualifiedNames);

                // Var entries must have null hash — property @_cdecl wrappers use name-based
                // naming (SBW_Get_/SBW_Set_), not hash-based, so hash matching would fail.
                Assert.Single(result._entries);
                Assert.Null(result._entries[0].Hash);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Detect_DistinguishesOverloadsByMangledName()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var sim = WriteAbiJson(dir, "sim.json", MakeAbiJson(
                    MakeTypeDecl("MyType",
                        MakeFunction("foo", "$s_foo_Int"),
                        MakeFunction("foo", "$s_foo_String"))));
                var dev = WriteAbiJson(dir, "dev.json", MakeAbiJson(
                    MakeTypeDecl("MyType",
                        MakeFunction("foo", "$s_foo_Int"))));

                var result = SimulatorOnlyMemberDetector.Detect(sim, dev, Logger);
                Assert.Equal(1, result.Count);
                Assert.Contains("MyType.foo", result.QualifiedNames);
                // Only the sim-only overload's hash should be present
                Assert.Single(result._entries);
                Assert.Equal(WrapperHash("$s_foo_String"), result._entries[0].Hash);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Detect_NoSimOnlyWhenSlicesMatch()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var json = MakeAbiJson(
                    MakeTypeDecl("MyType",
                        MakeFunction("foo", "$s_foo"),
                        MakeVar("bar", "$s_bar")));
                var sim = WriteAbiJson(dir, "sim.json", json);
                var dev = WriteAbiJson(dir, "dev.json", json);

                var result = SimulatorOnlyMemberDetector.Detect(sim, dev, Logger);
                Assert.Equal(0, result.Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Detect_ReturnsEmptyWhenNoDevicePath()
        {
            var result = SimulatorOnlyMemberDetector.Detect("/nonexistent/sim.json", null, Logger);
            Assert.Equal(0, result.Count);
        }
    }

    #endregion

    #region B. Simulator Guard Tests

    public class SimulatorOnlyGuardTests
    {
        private static SimulatorOnlyResult MakeResult(params (string QualifiedName, string PatchedMangledName)[] members)
        {
            var result = new SimulatorOnlyResult();
            foreach (var (q, m) in members)
                result.Add(q, m);
            return result;
        }

        [Fact]
        public void ApplySimulatorGuards_GuardsConstructorWrappers()
        {
            var mangledName = "$s_MyType_initABC";
            var hash = EmitterUtility.DeterministicHash8(mangledName);

            var content = string.Join("\n", new[]
            {
                "// Constructor @_cdecl wrapper for MyModule.MyType.init.",
                "@_cdecl(\"SBW_MyModule_MyType_init_" + hash + "\")",
                "func SBW_MyModule_MyType_init_" + hash + "() -> UnsafeMutableRawPointer {",
                "    return Unmanaged.passRetained(MyType()).toOpaque()",
                "}",
                "",
                "// Method @_cdecl wrapper for MyModule.MyType.shared.",
                "@_cdecl(\"SBW_MyModule_MyType_shared_AABBCCDD\")",
                "func SBW_MyModule_MyType_shared_AABBCCDD() -> Int32 {",
                "    return 0",
                "}"
            });

            var simOnly = MakeResult(("MyType.init", mangledName));
            var (result, count) = SimulatorOnlyMemberDetector.ApplySimulatorGuards(content, "MyModule", simOnly);

            Assert.Equal(1, count);
            Assert.Contains("#if targetEnvironment(simulator)", result);
            Assert.Contains("#endif", result);
        }

        [Fact]
        public void ApplySimulatorGuards_OnlyGuardsSimOnlyOverload()
        {
            // Two overloads of "foo": foo(Int) is shared, foo(String) is simulator-only
            var sharedMangled = "$s_foo_Int";
            var simOnlyMangled = "$s_foo_String";
            var sharedHash = EmitterUtility.DeterministicHash8(sharedMangled);
            var simOnlyHash = EmitterUtility.DeterministicHash8(simOnlyMangled);

            var content = string.Join("\n", new[]
            {
                "// Method @_cdecl wrapper for MyModule.MyType.foo.",
                "@_cdecl(\"SBW_MyModule_MyType_foo_" + sharedHash + "\")",
                "func SBW_MyModule_MyType_foo_" + sharedHash + "(a: Int32) -> Int32 {",
                "    return MyType().foo(a)",
                "}",
                "",
                "// Method @_cdecl wrapper for MyModule.MyType.foo.",
                "@_cdecl(\"SBW_MyModule_MyType_foo_" + simOnlyHash + "\")",
                "func SBW_MyModule_MyType_foo_" + simOnlyHash + "(a: UnsafeRawPointer) -> Int32 {",
                "    return MyType().foo(String(a))",
                "}"
            });

            var simOnly = MakeResult(("MyType.foo", simOnlyMangled));
            var (result, count) = SimulatorOnlyMemberDetector.ApplySimulatorGuards(content, "MyModule", simOnly);

            // Only the simulator-only overload (foo_String) should be guarded
            Assert.Equal(1, count);
            // The shared overload's @_cdecl should NOT be guarded
            var lines = result.Split('\n');
            bool sharedIsGuarded = false;
            bool inGuard = false;
            foreach (var line in lines)
            {
                if (line.Contains("#if targetEnvironment(simulator)")) inGuard = true;
                if (line.Contains("#endif")) inGuard = false;
                if (inGuard && line.Contains(sharedHash)) sharedIsGuarded = true;
            }
            Assert.False(sharedIsGuarded, "Shared overload should not be guarded");
        }

        [Fact]
        public void ApplySimulatorGuards_FallbackForPropertyWithoutHash()
        {
            // Property wrappers don't include hashes — should fall back to name matching
            var content = string.Join("\n", new[]
            {
                "// Property getter @_cdecl wrapper for MyModule.MyType.simProp.",
                "@_cdecl(\"SBW_Get_MyModule_MyType_simProp\")",
                "func SBW_Get_MyModule_MyType_simProp() -> Int32 {",
                "    return MyType().simProp",
                "}"
            });

            // Empty mangled name → no hash, uses name-based fallback
            var simOnly = MakeResult(("MyType.simProp", ""));
            var (result, count) = SimulatorOnlyMemberDetector.ApplySimulatorGuards(content, "MyModule", simOnly);

            Assert.Equal(1, count);
            Assert.Contains("#if targetEnvironment(simulator)", result);
        }

        [Fact]
        public void ApplySimulatorGuards_MixedHashedAndUnhashed()
        {
            // P2 regression: mixed result set — one hashed member, one unhashed property.
            // Both should be guarded, not just the hashed one.
            var methodMangled = "$s_foo_simOnly";
            var methodHash = EmitterUtility.DeterministicHash8(methodMangled);

            var content = string.Join("\n", new[]
            {
                "// Property getter @_cdecl wrapper for MyModule.MyType.simProp.",
                "@_cdecl(\"SBW_Get_MyModule_MyType_simProp\")",
                "func SBW_Get_MyModule_MyType_simProp() -> Int32 {",
                "    return MyType().simProp",
                "}",
                "",
                "// Method @_cdecl wrapper for MyModule.MyType.foo.",
                "@_cdecl(\"SBW_MyModule_MyType_foo_" + methodHash + "\")",
                "func SBW_MyModule_MyType_foo_" + methodHash + "() -> Int32 {",
                "    return MyType().foo()",
                "}"
            });

            var simOnly = new SimulatorOnlyResult();
            simOnly.Add("MyType.simProp", "");           // unhashed property
            simOnly.Add("MyType.foo", methodMangled);     // hashed method

            var (result, count) = SimulatorOnlyMemberDetector.ApplySimulatorGuards(content, "MyModule", simOnly);

            // BOTH should be guarded
            Assert.Equal(2, count);
        }

        [Fact]
        public void ApplySimulatorGuards_NoOp_WhenEmptyResult()
        {
            var content = "some content";
            var (result, count) = SimulatorOnlyMemberDetector.ApplySimulatorGuards(
                content, "Module", new SimulatorOnlyResult());
            Assert.Equal(content, result);
            Assert.Equal(0, count);
        }
    }

    #endregion

    #region C. Thunk Assembly Filtering Tests

    public class SimulatorOnlyThunkFilterTests
    {
        private static SimulatorOnlyResult MakeResult(params (string QualifiedName, string PatchedMangledName)[] members)
        {
            var result = new SimulatorOnlyResult();
            foreach (var (q, m) in members)
                result.Add(q, m);
            return result;
        }

        /// <summary>
        /// Builds a realistic thunk block. The .globl hash is lowercase FNV-1a of swiftMangledName.
        /// The branch target is the original Swift symbol (for thunks that call Swift directly).
        /// </summary>
        private static string MakeThunkBlock(string moduleName, string swiftMangledName, bool simple = false)
        {
            var hash = EmitterUtility.DeterministicHash8(swiftMangledName).ToLowerInvariant();
            if (simple)
            {
                return string.Join("\n", new[]
                {
                    $".globl _thunk_{moduleName}_{hash}",
                    ".p2align 2",
                    $"_thunk_{moduleName}_{hash}:",
                    $"    b       _$s{swiftMangledName}",
                    ""
                });
            }
            return string.Join("\n", new[]
            {
                $".globl _thunk_{moduleName}_{hash}",
                ".p2align 2",
                $"_thunk_{moduleName}_{hash}:",
                "    stp     x29, x30, [sp, #-16]!",
                $"    bl      _$s{swiftMangledName}",
                "    ldp     x29, x30, [sp], #16",
                "    ret",
                ""
            });
        }

        [Fact]
        public void FilterThunkAssembly_HashMatching_OnlyRemovesSimOnlyOverload()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                // Two overloads: foo(Int) is shared, foo(String) is sim-only
                // Both use the PATCHED mangled name for hash computation
                var sharedMangled = "$s_foo_Int";
                var simOnlyMangled = "$s_foo_String";

                var asm = MakeThunkBlock("Module", sharedMangled) + "\n" +
                          MakeThunkBlock("Module", simOnlyMangled);

                var asmFile = Path.Combine(dir, "Module.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                var simOnly = MakeResult(("MyType.foo", simOnlyMangled));
                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                Assert.NotNull(result);
                Assert.Equal(1, result!.Value.RemovedCount);

                // The shared overload's thunk must be preserved
                var filtered = File.ReadAllText(result.Value.FilteredPath);
                var sharedHash = EmitterUtility.DeterministicHash8(sharedMangled).ToLowerInvariant();
                var simHash = EmitterUtility.DeterministicHash8(simOnlyMangled).ToLowerInvariant();
                Assert.Contains(sharedHash, filtered);
                Assert.DoesNotContain(simHash, filtered);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_HashMatching_DistinguishesNestedTypes()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                // Payments.Card.id and Identity.Card.id — same leaf type name
                var paymentsMangled = "$s_Payments_Card_id_get";
                var identityMangled = "$s_Identity_Card_id_get";

                var asm = MakeThunkBlock("Module", paymentsMangled) + "\n" +
                          MakeThunkBlock("Module", identityMangled);

                var asmFile = Path.Combine(dir, "Module.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                // Only Identity.Card.id is simulator-only
                var simOnly = MakeResult(("Identity.Card.id", identityMangled));
                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                Assert.NotNull(result);
                Assert.Equal(1, result!.Value.RemovedCount);

                // Payments.Card.id thunk must be preserved
                var filtered = File.ReadAllText(result.Value.FilteredPath);
                var paymentsHash = EmitterUtility.DeterministicHash8(paymentsMangled).ToLowerInvariant();
                var identityHash = EmitterUtility.DeterministicHash8(identityMangled).ToLowerInvariant();
                Assert.Contains(paymentsHash, filtered);
                Assert.DoesNotContain(identityHash, filtered);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_MatchesRealThunkFormat()
        {
            // Regression: thunks use lowercase hash in .globl line, and branch to
            // the original Swift symbol (not SBW_ wrapper). Verify matching works.
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var simMangled = "$s20Module26IdentityVerificationSheetC30simulatorDocumentCameraImages";
                var sharedMangled = "$s20Module26IdentityVerificationSheetC7present";

                var asm = MakeThunkBlock("Module", simMangled) + "\n" +
                          MakeThunkBlock("Module", sharedMangled);

                var asmFile = Path.Combine(dir, "Module.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                var simOnly = MakeResult(("IdentityVerificationSheet.simulatorDocumentCameraImages", simMangled));
                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                Assert.NotNull(result);
                Assert.Equal(1, result!.Value.RemovedCount);

                var filtered = File.ReadAllText(result.Value.FilteredPath);
                Assert.Contains("present", filtered);
                Assert.DoesNotContain("simulatorDocumentCameraImages", filtered);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_Fallback_TokenAwareMatching()
        {
            // Hashless fallback uses length-prefixed matching: "3Foo" and "2id" in mangled symbols.
            // This prevents "Foo" matching inside "FooBar" etc.
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                // Mangled symbols use length-prefix encoding: 3Foo = "Foo" (3 chars), 2id = "id" (2 chars)
                var asm = string.Join("\n", new[]
                {
                    ".globl _thunk_Module_aaa",
                    ".p2align 2",
                    "_thunk_Module_aaa:",
                    "    stp     x29, x30, [sp, #-16]!",
                    "    bl      _$s6Module3FooC2idSivg",
                    "    ldp     x29, x30, [sp], #16",
                    "    ret",
                    "",
                    ".globl _thunk_Module_bbb",
                    ".p2align 2",
                    "_thunk_Module_bbb:",
                    "    stp     x29, x30, [sp, #-16]!",
                    "    bl      _$s6Module3BarC2idSivg",
                    "    ldp     x29, x30, [sp], #16",
                    "    ret",
                    ""
                });
                var asmFile = Path.Combine(dir, "Module.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                // Empty mangled name triggers fallback; matches "3Foo" and "2id"
                var simOnly = MakeResult(("Foo.id", ""));
                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                Assert.NotNull(result);
                Assert.Equal(1, result!.Value.RemovedCount);

                // Bar.id thunk preserved (has "3Bar", not "3Foo")
                var filtered = File.ReadAllText(result.Value.FilteredPath);
                Assert.Contains("3BarC2id", filtered);
                Assert.DoesNotContain("3FooC2id", filtered);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_Fallback_NoSubstringCollision()
        {
            // "Identity.Card.id" should NOT match a thunk for "IdentityCard.identifier"
            // because length-prefixed matching requires "8Identity", "4Card", "2id" — not
            // "12IdentityCard" and "10identifier"
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var asm = string.Join("\n", new[]
                {
                    ".globl _thunk_Module_aaa",
                    ".p2align 2",
                    "_thunk_Module_aaa:",
                    "    stp     x29, x30, [sp, #-16]!",
                    // "12IdentityCard" and "10identifier" — NOT "8Identity" + "4Card" + "2id"
                    "    bl      _$s6Module12IdentityCardC10identifierSSvg",
                    "    ldp     x29, x30, [sp], #16",
                    "    ret",
                    ""
                });
                var asmFile = Path.Combine(dir, "Module.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                // "Identity.Card.id" looks for "8Identity", "4Card", "2id" — none match
                var simOnly = MakeResult(("Identity.Card.id", ""));
                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                // No filtering — the thunk is for a completely different type
                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_MixedHashedAndUnhashed()
        {
            // P2 regression: mixed result — hashed method + unhashed property
            // Both should trigger thunk removal
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var methodMangled = "$s_foo_sim";

                // Thunk 1: matches via hash (method)
                var thunk1 = MakeThunkBlock("Module", methodMangled);

                // Thunk 2: matches via fallback (unhashed property — uses synthetic hash "bbb")
                var thunk2 = string.Join("\n", new[]
                {
                    ".globl _thunk_Module_bbb",
                    ".p2align 2",
                    "_thunk_Module_bbb:",
                    "    stp     x29, x30, [sp, #-16]!",
                    "    bl      _$s6Module3FooC7simPropSivg",
                    "    ldp     x29, x30, [sp], #16",
                    "    ret",
                    ""
                });

                // Thunk 3: should NOT be removed
                var thunk3 = string.Join("\n", new[]
                {
                    ".globl _thunk_Module_ccc",
                    ".p2align 2",
                    "_thunk_Module_ccc:",
                    "    stp     x29, x30, [sp, #-16]!",
                    "    bl      _$s6Module3BarC6sharedSivg",
                    "    ldp     x29, x30, [sp], #16",
                    "    ret",
                    ""
                });

                var asm = thunk1 + "\n" + thunk2 + "\n" + thunk3;
                var asmFile = Path.Combine(dir, "Module.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                var simOnly = new SimulatorOnlyResult();
                simOnly.Add("MyType.foo", methodMangled);  // hashed
                simOnly.Add("Foo.simProp", "");             // unhashed, uses fallback

                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                Assert.NotNull(result);
                Assert.Equal(2, result!.Value.RemovedCount);

                var filtered = File.ReadAllText(result.Value.FilteredPath);
                Assert.Contains("BarC6shared", filtered);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_TailCallThunk_DoesNotSwallowNextBlock()
        {
            // Tail-call thunks use "b" (not "bl...ret"). The block parser must stop
            // at the next .globl instead of scanning forward for a "ret" that belongs
            // to a later block.
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var simMangled = "$s_simOnly_func";
                var sharedMangled = "$s_shared_func";

                // Thunk 1: tail-call form (simulator-only) — just "b", no ret
                var asm = MakeThunkBlock("Module", simMangled, simple: true) +
                          // Thunk 2: multi-instruction (shared) — should NOT be removed
                          MakeThunkBlock("Module", sharedMangled, simple: false);

                var asmFile = Path.Combine(dir, "Module.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                var simOnly = MakeResult(("MyType.simFunc", simMangled));
                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                Assert.NotNull(result);
                Assert.Equal(1, result!.Value.RemovedCount);

                // The shared thunk must be preserved
                var filtered = File.ReadAllText(result.Value.FilteredPath);
                var sharedHash = EmitterUtility.DeterministicHash8(sharedMangled).ToLowerInvariant();
                Assert.Contains(sharedHash, filtered);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_ConsecutiveTailCallThunks()
        {
            // Two consecutive tail-call thunks: only the sim-only one should be removed
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var simMangled = "$s_sim_tail";
                var sharedMangled = "$s_shared_tail";

                var asm = MakeThunkBlock("Module", simMangled, simple: true) +
                          MakeThunkBlock("Module", sharedMangled, simple: true);

                var asmFile = Path.Combine(dir, "Module.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                var simOnly = MakeResult(("MyType.simTail", simMangled));
                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                Assert.NotNull(result);
                Assert.Equal(1, result!.Value.RemovedCount);

                var filtered = File.ReadAllText(result.Value.FilteredPath);
                var sharedHash = EmitterUtility.DeterministicHash8(sharedMangled).ToLowerInvariant();
                Assert.Contains(sharedHash, filtered);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_Fallback_DistinguishesNestedTypes()
        {
            // Hashless fallback uses length-prefixed matching.
            // "Identity.Card.id" → looks for "8Identity", "4Card", "2id"
            // "Payments.Card.id" → has "8Payments", "4Card", "2id"
            // The "8Identity" won't match in the Payments thunk block.
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                // Both mangled symbols have "4Card" and "2id", but differ in parent type prefix
                var asm = string.Join("\n", new[]
                {
                    ".globl _thunk_Module_aaa",
                    ".p2align 2",
                    "_thunk_Module_aaa:",
                    "    stp     x29, x30, [sp, #-16]!",
                    "    bl      _$s6Module8PaymentsO4CardC2idSivg",
                    "    ldp     x29, x30, [sp], #16",
                    "    ret",
                    "",
                    ".globl _thunk_Module_bbb",
                    ".p2align 2",
                    "_thunk_Module_bbb:",
                    "    stp     x29, x30, [sp, #-16]!",
                    "    bl      _$s6Module8IdentityO4CardC2idSivg",
                    "    ldp     x29, x30, [sp], #16",
                    "    ret",
                    ""
                });
                var asmFile = Path.Combine(dir, "Module.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                // Only Identity.Card.id is sim-only (no hash → uses fallback)
                // Looks for "8Identity" + "4Card" + "2id"
                var simOnly = MakeResult(("Identity.Card.id", ""));
                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                Assert.NotNull(result);
                Assert.Equal(1, result!.Value.RemovedCount);

                // Payments.Card.id must be preserved (has "8Payments", not "8Identity")
                var filtered = File.ReadAllText(result.Value.FilteredPath);
                Assert.Contains("8Payments", filtered);
                Assert.DoesNotContain("8Identity", filtered);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_Fallback_SwiftSubstitutionCompression()
        {
            // Swift mangling compresses shared prefixes between module and type names.
            // Module "StripeIdentity" + type "IdentityVerificationSheet" mangles as
            // "14StripeIdentity0B17VerificationSheet" — "Identity" shared prefix replaced by "0B".
            // The suffix "17VerificationSheet" still appears, so suffix matching must find it.
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                var asm = string.Join("\n", new[]
                {
                    ".globl _thunk_StripeIdentity_af0eb15e",
                    ".p2align 2",
                    "_thunk_StripeIdentity_af0eb15e:",
                    "    stp     x20, x19, [sp, #-32]!",
                    "    stp     x29, x30, [sp, #16]",
                    "    add     x29, sp, #16",
                    "    mov     x0, #0",
                    "    bl      _$s14StripeIdentity0B17VerificationSheetCMa",
                    "    mov     x20, x0",
                    "    bl      _$s14StripeIdentity0B17VerificationSheetC29simulatorDocumentCameraImagesSaySo7UIImageCGvgZ",
                    "    ldp     x29, x30, [sp, #16]",
                    "    ldp     x20, x19, [sp], #32",
                    "    ret",
                    "",
                    ".globl _thunk_StripeIdentity_other",
                    ".p2align 2",
                    "_thunk_StripeIdentity_other:",
                    "    stp     x29, x30, [sp, #-16]!",
                    "    bl      _$s14StripeIdentity0B17VerificationSheetC30verificationSessionClientSecretSSvg",
                    "    ldp     x29, x30, [sp], #16",
                    "    ret",
                    ""
                });
                var asmFile = Path.Combine(dir, "StripeIdentity.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                // "IdentityVerificationSheet" is substituted to "0B17VerificationSheet" in the mangled name.
                // Empty mangled name triggers fallback. Suffix matching should find "17VerificationSheet"
                // even though "29IdentityVerificationSheet" doesn't appear literally.
                var simOnly = MakeResult(("IdentityVerificationSheet.simulatorDocumentCameraImages", ""));
                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                Assert.NotNull(result);
                Assert.Equal(1, result!.Value.RemovedCount);

                // The other thunk (verificationSessionClientSecret) must be preserved
                var filtered = File.ReadAllText(result.Value.FilteredPath);
                Assert.Contains("verificationSessionClientSecret", filtered);
                Assert.DoesNotContain("simulatorDocumentCameraImages", filtered);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_Fallback_DoesNotMatchUnrelatedTypeWithSharedSuffix()
        {
            // Codex review: "AddressVerificationSheet.simulatorDocumentCameraImages" must NOT match
            // a sim-only entry for "IdentityVerificationSheet.simulatorDocumentCameraImages" just
            // because both share the suffix "VerificationSheet". The suffix match requires a Swift
            // substitution pattern (uppercase letter) immediately before the length-prefixed suffix.
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                // This thunk is for AddressVerificationSheet — no substitution, full name present
                var asm = string.Join("\n", new[]
                {
                    ".globl _thunk_Module_aaa",
                    ".p2align 2",
                    "_thunk_Module_aaa:",
                    "    stp     x29, x30, [sp, #-16]!",
                    "    bl      _$s6Module24AddressVerificationSheetC29simulatorDocumentCameraImagesSaySo7UIImageCGvgZ",
                    "    ldp     x29, x30, [sp], #16",
                    "    ret",
                    ""
                });
                var asmFile = Path.Combine(dir, "Module.arm64.s");
                File.WriteAllText(asmFile, asm);

                var outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                // Sim-only entry is for IdentityVerificationSheet, not AddressVerificationSheet
                var simOnly = MakeResult(("IdentityVerificationSheet.simulatorDocumentCameraImages", ""));
                var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(asmFile, simOnly, outDir);

                // Must NOT filter the thunk — it belongs to a different type
                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FilterThunkAssembly_NoOp_WhenEmptyResult()
        {
            var result = SimulatorOnlyMemberDetector.FilterThunkAssembly(
                "/nonexistent", new SimulatorOnlyResult(), "/nonexistent");
            Assert.Null(result);
        }
    }

    #endregion
}
