// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.NativeFrameGate.cs — "every function we write in assembly unwinds" ship gate for
// `nuke binding-tests --compile-only`.
//
// The generator writes two kinds of function in assembly: the native Swift bridges (SBSR_) and the
// native thunks (thunk_<Module>_<hash>). Both run between a managed caller and Swift code, so an
// unwinder stopping inside one — a crash report, a debugger, a profiler, a runtime's stack walk —
// reads the unwind information the wrapper's own link wrote for it. A function the table leaves
// uncovered, or covers with a rule its code does not build, stops or corrupts that walk.
//
// The gate runs the generator's linked check (`--validate-linked-bridges`) over every slice and
// architecture of both wrappers: each exported bridge is held to its plan, and every exported thunk
// to the same frame and unwind checks (SwiftBridgeValidator) at entry, at each call and at exit,
// with full PC-range coverage. A positive control requires the main wrapper to export native thunks,
// so the gate cannot pass by checking none.
//
// Fail-closed and unconditional, with no `--permissive` arm: a soundness property of the emitted
// artifact, not a baseline.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    /// <summary>
    /// Validates the frame and unwind information of every bridge and thunk both linked wrappers
    /// export, on every slice and architecture. Invoked from the --compile-only path once both
    /// wrappers are built.
    /// </summary>
    void RunNativeFrameGate()
    {
        Log.Information("=========================================");
        Log.Information(" Native frame gate");
        Log.Information("=========================================");

        var platform = ResolvedPlatform;
        var depWrapper = $"{DepModuleName}SwiftBindings";
        var legs = new[]
        {
            (Wrapper: WrapperModule, Plans: BtOutputDir / $"{ModuleName}.swift-call-plans.json", RequireThunks: true),
            (Wrapper: depWrapper, Plans: BtOutputDir / $"{DepModuleName}.swift-call-plans.json", RequireThunks: false),
        };

        EnsureGeneratorBuilt(ensureSwiftInterfaceParser: false);
        var scratch = BtOutputDir / ".native-frame-gate";
        if (Directory.Exists(scratch))
            scratch.DeleteDirectory();
        scratch.CreateDirectory();
        try
        {
            foreach (var (wrapper, plans, requireThunks) in legs)
            {
                var xcframework = BtOutputDir / $"{wrapper}.xcframework";
                if (!File.Exists(plans))
                    throw new Exception($"Native frame gate: {wrapper}: the plan sidecar {plans} is missing.");
                var images = Directory.Exists(xcframework)
                    ? Directory.GetDirectories(xcframework).Order(StringComparer.Ordinal)
                        .Select(d => Path.Combine(d, $"{wrapper}.framework", wrapper))
                        .Where(File.Exists)
                        .ToList()
                    : new List<string>();
                if (images.Count == 0)
                    throw new Exception($"Native frame gate: {wrapper}: no framework binary under {xcframework}.");

                // Positive control: the main wrapper binds members through native thunks.
                if (requireThunks)
                {
                    foreach (var image in images)
                    {
                        var thunks = XcRunTool($"nm -gU \"{image}\"", logOutput: false, logInvocation: false)
                            .Count(o => o.Type == OutputType.Std && o.Text.Contains(" _thunk_", StringComparison.Ordinal));
                        if (thunks == 0)
                            throw new Exception($"Native frame gate: {image} exports no native thunk; the gate would check none.");
                        Log.Information("  {Image}: {Thunks} exported thunk symbol(s)", image, thunks);
                    }
                }

                var arguments = new List<string>
                {
                    $"\"{GeneratorDll}\"", $"--swift-call-plans \"{plans}\"", $"-o \"{scratch / wrapper}\"", $"--platform {platform.Name}",
                };
                arguments.AddRange(images.Select(i => $"--validate-linked-bridges \"{i}\""));
                RunGenerator(arguments, $"Native frame gate: the linked check of {wrapper}");
                Log.Information("  {Wrapper}: bridges and thunks validated over {Count} slice(s)", wrapper, images.Count);
            }
        }
        finally
        {
            if (Directory.Exists(scratch))
                scratch.DeleteDirectory();
        }
        Log.Information("Native frame gate: PASSED");
    }
}
