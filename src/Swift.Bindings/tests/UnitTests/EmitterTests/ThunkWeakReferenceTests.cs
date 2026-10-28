// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// A native thunk reaches its targets by name in assembly, outside swiftc, so it never gets
    /// swiftc's availability-driven weak linking. Every symbol a thunk branches to must therefore be
    /// declared <c>.weak_reference</c>: a strong import of a symbol newer than the wrapper's
    /// deployment target makes dyld refuse to load the wrapper — and the app that embeds it — on an
    /// older OS, whether or not the member is ever called.
    /// </summary>
    public class ThunkWeakReferenceTests
    {
        private const string Accessor = "_$s4Test3FooCMa";

        private static readonly Regex BranchTarget = new(
            @"^\s+(?:b|bl|jmp|callq)\s+(\S+)\s*$", RegexOptions.Multiline);

        private static readonly Regex WeakReference = new(
            @"^\s*\.weak_reference\s+(\S+)\s*$", RegexOptions.Multiline);

        private static TypeLoweringResult IntSlots(int count, bool indirect = false) => new(
            Enumerable.Range(0, count).Select(i => new RegisterSlot(RegisterFile.Integer, i, 8)).ToArray(),
            IsIndirect: indirect,
            TotalByteSize: count * 8);

        private static ThunkDescriptor Shape(
            string name,
            TypeLoweringResult? returnLowering = null,
            int parameterCount = 0,
            bool isInstance = false,
            bool isStatic = false,
            bool isConstructor = false,
            bool throws = false,
            int zeroExtend = 0) => new(
                ThunkSymbol: $"thunk_Test_{name}",
                SwiftSymbol: $"_$s4Test{name.Length}{name}yyF",
                ReturnLowering: returnLowering,
                SelfLowering: isInstance ? IntSlots(1) : null,
                ParameterCount: parameterCount,
                FloatParameterCount: 0,
                IsInstanceMethod: isInstance,
                IsStaticMethod: isStatic,
                IsConstructor: isConstructor,
                Throws: throws,
                MetadataAccessorSymbol: isStatic || isConstructor ? Accessor : null,
                ReturnZeroExtendFromBytes: zeroExtend);

        /// <summary>One descriptor per template path that emits a branch: tail call, tag zero-extend,
        /// indirect and register-bridged returns, self, swifterror, and the metatype-loading
        /// static and constructor paths.</summary>
        private static readonly ThunkDescriptor[] AllShapes =
        {
            Shape("tailCall", parameterCount: 2),
            Shape("tagReturn", new TypeLoweringResult(new[] { new RegisterSlot(RegisterFile.Integer, 0, 1) }, IsIndirect: false, TotalByteSize: 1), zeroExtend: 1),
            Shape("indirectReturn", IntSlots(5, indirect: true), parameterCount: 1),
            Shape("bridgedReturn", IntSlots(3), parameterCount: 1),
            Shape("instance", parameterCount: 1, isInstance: true),
            Shape("throwingInstance", IntSlots(1), parameterCount: 1, isInstance: true, throws: true),
            Shape("staticMember", IntSlots(1), parameterCount: 1, isStatic: true),
            Shape("staticIndirect", IntSlots(5, indirect: true), isStatic: true),
            Shape("constructor", IntSlots(1), parameterCount: 2, isConstructor: true),
            Shape("throwingConstructor", IntSlots(1), parameterCount: 1, isConstructor: true, throws: true),
        };

        public static IEnumerable<object[]> Shapes() =>
            from arch in new[] { ThunkArch.Arm64, ThunkArch.X86_64 }
            from shape in AllShapes
            select new object[] { arch, shape.ThunkSymbol };

        private static ThunkDescriptor Lookup(string thunkSymbol) =>
            AllShapes.Single(d => d.ThunkSymbol == thunkSymbol);

        [Theory]
        [MemberData(nameof(Shapes))]
        public void EveryBranchTarget_IsWeakReferenced_AndNothingElse(ThunkArch arch, string thunkSymbol)
        {
            var target = ThunkTargetArch.For(arch);
            var descriptor = Lookup(thunkSymbol);
            Assert.True(target.CanEmit(descriptor), $"{arch} must be able to encode {thunkSymbol}");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor, target);

            var branched = BranchTarget.Matches(asm).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var weak = WeakReference.Matches(asm).Select(m => m.Groups[1].Value).ToList();

            Assert.Contains(descriptor.SwiftSymbol, branched);
            Assert.Equal(branched.OrderBy(s => s, StringComparer.Ordinal), weak.Distinct().OrderBy(s => s, StringComparer.Ordinal));
            Assert.Equal(weak.Count, weak.Distinct().Count());
        }

        [Theory]
        [MemberData(nameof(Shapes))]
        public void WeakReferences_SitInsideTheThunkBlock(ThunkArch arch, string thunkSymbol)
        {
            // The device-slice filter (SimulatorOnlyMemberDetector.FilterThunkAssembly) removes a thunk
            // as the lines from its `.globl` up to its `ret` (or up to the next `.globl` for a tail
            // call). A directive outside that span would outlive a removed thunk and name a symbol the
            // device SDK may not define, or be removed along with a neighbouring thunk.
            var asm = ThunkAssemblyEmitter.EmitThunk(Lookup(thunkSymbol), ThunkTargetArch.For(arch));
            var lines = asm.Split('\n').Select(l => l.TrimStart()).ToList();

            int globl = lines.FindIndex(l => l.StartsWith($".globl _{thunkSymbol}", StringComparison.Ordinal));
            int ret = lines.FindIndex(l => l.StartsWith("ret", StringComparison.Ordinal));
            var weakLines = Enumerable.Range(0, lines.Count)
                .Where(i => lines[i].StartsWith(".weak_reference ", StringComparison.Ordinal)).ToList();

            Assert.True(globl >= 0);
            Assert.NotEmpty(weakLines);
            Assert.All(weakLines, i =>
            {
                Assert.True(i > globl, $"line {i} precedes the thunk's .globl");
                if (ret >= 0)
                    Assert.True(i < ret, $"line {i} follows the thunk's ret");
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Real-toolchain check: assemble the emitted thunks, link them against a stand-in library
        // that defines every target, and read the result back the way dyld sees it. The emitter
        // tests above assert the directive; this proves the directive produces weak imports and a
        // weak load command — the property the startup crash depends on (macOS only).
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(ThunkArch.Arm64, "arm64-apple-macos13.0")]
        [InlineData(ThunkArch.X86_64, "x86_64-apple-macos13.0")]
        public void LinkedThunks_ImportEveryTargetWeakly_AndLoadTheLibraryWeakly(ThunkArch arch, string triple)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
            var runner = new SystemCommandRunner();
            var target = ThunkTargetArch.For(arch);

            var dir = Path.Combine(Path.GetTempPath(), $"thunk_weak_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var descriptors = AllShapes.Where(target.CanEmit).ToList();
                var asm = new StringBuilder();
                target.EmitFileHeader(asm, "Test");
                foreach (var d in descriptors)
                    asm.Append(ThunkAssemblyEmitter.EmitThunk(d, target));
                var thunks = Path.Combine(dir, $"Test.{target.ArchTag}.s");
                File.WriteAllText(thunks, asm.ToString());

                var targets = descriptors.Select(d => d.SwiftSymbol)
                    .Concat(descriptors.Where(d => d.MetadataAccessorSymbol != null).Select(d => d.MetadataAccessorSymbol!))
                    .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();

                // Stand-in for the bound framework: defines every target as a bare return.
                var stub = new StringBuilder(".text\n");
                foreach (var symbol in targets)
                    stub.Append($".globl {symbol}\n.p2align 2\n{symbol}:\n    {(arch == ThunkArch.X86_64 ? "retq" : "ret")}\n");
                var stubSource = Path.Combine(dir, "stub.s");
                File.WriteAllText(stubSource, stub.ToString());

                var stubLib = Path.Combine(dir, "libThunkTargets.dylib");
                var (stubExit, _, stubErr) = runner.Run("xcrun",
                    $"clang -dynamiclib -target {triple} \"{stubSource}\" -install_name @rpath/libThunkTargets.dylib -o \"{stubLib}\"",
                    timeoutMs: 60000);
                Assert.True(stubExit == 0 && File.Exists(stubLib), $"linking the stand-in target library failed: {stubErr}");

                var wrapper = Path.Combine(dir, "libWrapper.dylib");
                var (linkExit, _, linkErr) = runner.Run("xcrun",
                    $"clang -dynamiclib -target {triple} \"{thunks}\" -L\"{dir}\" -lThunkTargets -o \"{wrapper}\"",
                    timeoutMs: 60000);
                Assert.True(linkExit == 0, $"linking the emitted thunks failed: {linkErr}");

                var (importsExit, imports, _) = runner.Run("xcrun", $"dyld_info -imports \"{wrapper}\"", timeoutMs: 60000);
                Assert.Equal(0, importsExit);
                var importLines = imports.Split('\n').Where(l => l.Contains("(from libThunkTargets)", StringComparison.Ordinal)).ToList();

                Assert.Equal(targets.Count, importLines.Count);
                Assert.All(targets, symbol =>
                {
                    var line = importLines.SingleOrDefault(l => l.Contains($" {symbol} ", StringComparison.Ordinal));
                    Assert.True(line != null, $"{symbol} is not imported");
                    Assert.Contains("[weak-import]", line);
                });

                var (loadsExit, loads, _) = runner.Run("xcrun", $"otool -l \"{wrapper}\"", timeoutMs: 60000);
                Assert.Equal(0, loadsExit);
                var loadCommands = Regex.Matches(loads, @"cmd (LC_LOAD(?:_WEAK)?_DYLIB)\s+cmdsize \d+\s+name (\S+)")
                    .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value);
                Assert.Equal("LC_LOAD_WEAK_DYLIB", loadCommands["@rpath/libThunkTargets.dylib"]);
            }
            finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
        }
    }
}
