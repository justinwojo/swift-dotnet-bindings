// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.OsLoadSafetyGate.cs — "a missing OS symbol can never stop a wrapper loading" (load-safety P1)
// for the wrappers this build links itself, and the gate that reads every built wrapper slice back.
//
// Swift weak-links a reference from the imported declaration's availability alone. An SDK member
// declared without a floor that an older OS does not ship is therefore a strong import, and dyld then
// refuses the whole wrapper, and the app with it, on that OS. The generator's wrapper links weak-link
// every OS-resident framework (OsFrameworkWeakLinking); the harness-built BindingTests wrapper and the
// Apple supplement link the same way through WeakLinkOsFrameworks below.
//
// The gate runs in every `--compile-only` invocation after the thunk weak-link gate. For every slice
// of both BindingTests wrappers it requires every `/System/` dylib load to be LC_LOAD_WEAK_DYLIB, and
// requires the wrapper to load at least one OS framework so the check cannot pass on an empty read.
// One strong load is sound and accepted: an umbrella that re-exports a library only its allowed
// clients may link (SwiftUI re-exports SwiftUICore). A symbol of that library binds through the
// umbrella, and ld then keeps the umbrella's load strong whatever -weak_framework asks, while every
// import from it stays weak, so a missing symbol still binds to null. The umbrella itself ships on
// every supported OS. The SDK's stubs decide which loads qualify.
// Fail-closed and unconditional, with no `--permissive` arm: a soundness property, not a baseline.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BindingsGeneration;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    /// <summary><c>otool -l</c> of <paramref name="binary"/>.</summary>
    string ReadOtoolLoadCommands(AbsolutePath binary) =>
        string.Join("\n", XcRunTool($"otool -l \"{binary}\"", logOutput: false, logInvocation: false)
            .Where(o => o.Type == OutputType.Std).Select(o => o.Text));

    /// <summary>Reads <paramref name="binary"/>'s dylib load commands with <c>otool -l</c>.</summary>
    IReadOnlyList<OsFrameworkWeakLinking.LoadedDylib> ReadDylibLoads(AbsolutePath binary) =>
        OsFrameworkWeakLinking.ParseLoadCommands(ReadOtoolLoadCommands(binary));

    /// <summary>Reads <paramref name="binary"/>'s imports with <c>dyld_info -imports</c>.</summary>
    IReadOnlyList<OsFrameworkWeakLinking.ImportedSymbol> ReadDylibImports(AbsolutePath binary) =>
        OsFrameworkWeakLinking.ParseImports(string.Join("\n",
            XcRunTool($"dyld_info -imports \"{binary}\"", logOutput: false, logInvocation: false)
                .Where(o => o.Type == OutputType.Std).Select(o => o.Text)));

    readonly Dictionary<string, string> _sdkPaths = new(StringComparer.Ordinal);

    /// <summary>The path of the SDK <paramref name="binary"/> was built for, from its <c>LC_BUILD_VERSION</c>.</summary>
    string SdkPathOf(AbsolutePath binary)
    {
        var sdk = OsFrameworkWeakLinking.BuildPlatformSdk(ReadOtoolLoadCommands(binary))
            ?? throw new Exception($"{binary}: otool -l shows no LC_BUILD_VERSION platform, so its SDK is unknown.");
        lock (_sdkPaths)
        {
            if (!_sdkPaths.TryGetValue(sdk, out var path))
            {
                path = string.Join("", XcRunTool($"--sdk {sdk} --show-sdk-path", logOutput: false, logInvocation: false)
                    .Where(o => o.Type == OutputType.Std).Select(o => o.Text)).Trim();
                if (path.Length == 0)
                    throw new Exception($"xcrun --sdk {sdk} --show-sdk-path returned an empty path.");
                _sdkPaths[sdk] = path;
            }
            return path;
        }
    }

    /// <summary>
    /// The OS-resident dylibs <paramref name="binary"/> loads strongly that a missing symbol would fail
    /// (<see cref="OsFrameworkWeakLinking.UnsafeStrongLoads"/>): every strong load except an umbrella the
    /// linker keeps strong for a restricted re-export, all of whose imports are weak.
    /// </summary>
    IReadOnlyList<string> UnsafeStrongOsResidentLoads(AbsolutePath binary, IReadOnlyList<string> strong) =>
        strong.Count == 0 ? strong : OsFrameworkWeakLinking.UnsafeStrongLoads(strong, ReadDylibImports(binary), SdkPathOf(binary));

    /// <summary>
    /// Load-safety P1 for a wrapper this build links: reads the linked <paramref name="binary"/>'s load
    /// commands and, when an OS-resident framework came out strong, relinks once through
    /// <paramref name="relink"/> with that set weak-linked, then requires every <c>/System/</c> load still
    /// strong to be one a missing symbol cannot fail (an umbrella the linker keeps strong for a restricted
    /// re-export, every import from it weak). <paramref name="relink"/> receives the swiftc arguments to
    /// append and returns whether the relink succeeded.
    /// </summary>
    void WeakLinkOsFrameworks(string label, AbsolutePath binary, Func<IReadOnlyList<string>, bool> relink)
    {
        var strong = OsFrameworkWeakLinking.StrongOsResidentLoads(ReadDylibLoads(binary));
        if (strong.Count == 0)
            return;
        var frameworks = strong.Select(OsFrameworkWeakLinking.FrameworkName).OfType<string>()
            .Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Log.Information("{Label}: relinking to weak-link the OS framework(s) it loaded strongly: {Frameworks}",
            label, string.Join(", ", frameworks));
        var args = frameworks.SelectMany(f => new[] { "-Xlinker", "-weak_framework", "-Xlinker", f }).ToList();
        if (frameworks.Count == 0 || !relink(args))
            throw new Exception(OsFrameworkWeakLinking.StillStrongMessage(label, binary, strong));
        var still = OsFrameworkWeakLinking.StrongOsResidentLoads(ReadDylibLoads(binary));
        var unsafeLoads = UnsafeStrongOsResidentLoads(binary, still);
        if (unsafeLoads.Count > 0)
            throw new Exception(OsFrameworkWeakLinking.StillStrongMessage(label, binary, unsafeLoads));
        if (still.Count > 0)
            Log.Information("{Label}: the linker keeps {Loads} loaded strongly (an umbrella re-exporting a library only its " +
                            "allowed clients may link); every import from it is weak, so a missing symbol binds to null",
                label, string.Join(", ", still));
    }

    /// <summary>
    /// Reads every slice of both BindingTests wrappers back and requires every OS-resident dylib load to
    /// be weak. Invoked from the --compile-only path once both wrappers are built.
    /// </summary>
    void RunOsLoadSafetyGate()
    {
        Log.Information("=========================================");
        Log.Information(" OS load-safety gate");
        Log.Information("=========================================");

        var depWrapper = $"{DepModuleName}SwiftBindings";
        var legs = new[]
        {
            (Leg: WrapperModule, Xcframework: BtOutputDir / $"{WrapperModule}.xcframework"),
            (Leg: depWrapper, Xcframework: BtOutputDir / $"{depWrapper}.xcframework"),
        };

        var failures = new List<string>();
        foreach (var (leg, xcframework) in legs)
        {
            var binaries = Directory.Exists(xcframework)
                ? Directory.GetDirectories(xcframework)
                    .Select(slice => (AbsolutePath)Path.Combine(slice, $"{leg}.framework", leg))
                    .Where(b => File.Exists(b))
                    .OrderBy(b => b.ToString(), StringComparer.Ordinal)
                    .ToList()
                : new List<AbsolutePath>();
            if (binaries.Count == 0)
            {
                failures.Add($"{leg}: no wrapper slice found under {xcframework}.");
                continue;
            }
            foreach (var binary in binaries)
            {
                var verdict = OsLoadSafetyAudit.EvaluateLoads(
                    leg, binary, ReadDylibLoads(binary), ReadDylibImports(binary), SdkPathOf(binary));
                Log.Information("{Leg} {Slice}: {Os} OS-resident load(s), {Weak} weak, {Kept} kept strong by the linker with every import weak ({KeptLoads})",
                    leg, Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(binary))),
                    verdict.OsResidentLoadCount, verdict.WeakOsResidentLoadCount, verdict.SafeStrongLoads.Count,
                    string.Join(", ", verdict.SafeStrongLoads.Select(OsFrameworkWeakLinking.ImportLeaf)));
                failures.AddRange(verdict.Failures);
            }
        }

        if (failures.Count > 0)
        {
            foreach (var f in failures)
                Log.Error("  {Failure}", f);
            throw new Exception(
                $"OS load-safety gate: {failures.Count} failure(s). A strong load of an OS framework makes dyld refuse " +
                "the whole wrapper, and the app with it, on an OS that lacks one of the symbols it imports — see the lines above.");
        }

        Log.Information("OS load-safety gate: PASSED");
    }
}
