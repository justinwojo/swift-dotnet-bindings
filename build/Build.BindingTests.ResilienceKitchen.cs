// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.ResilienceKitchen.cs — the durable BindingTests gate for the wrapper
// verify-recover loop. UNLIKE the opt-in --partial-success-kitchen leg, this runs UNCONDITIONALLY
// inside `nuke binding-tests --compile-only`, so CI's fail-closed compile gate exercises the loop
// on every invocation.
//
// The fixture (BindingTests/Sources/ResilienceKitchen/) interleaves a STRUCTURALLY HOSTILE member —
// an implicitly-unwrapped-optional stored property on a GENERIC class, which the emitter binds wrong
// (the synthesized witness-protocol conformance does not compile) — with HEALTHY siblings inside the
// same type. The verify-recover loop must withdraw ONLY the broken accessor group and keep every
// healthy sibling intact. This is a LOOP-CONTAINED family: its root cause is deliberately NOT fixed,
// so a natural compile → attribute → withdraw shape stays exercised here (a predictively-skipped
// shape would fire before emission and the loop would never see it).
//
// The gate builds the fixture TWICE from one source tree: a HOSTILE slice (with the RESILIENCE_HOSTILE
// Swift define) and a hostile-free CONTROL slice. It then asserts:
//   1. generation of the hostile fixture exits 0 (a module with a broken shape still generates);
//   2. the emitted C# compiles (a real dotnet build);
//   3. every hostile member carries a withdrawal row — Reason=EmitterFault, the withdrawal wording in
//      Details, RecoveryStage=SwiftCompile, CauseOwner=Generator, an accessor-group root cause, and no
//      false cascade parent;
//   4. the healthy siblings survive with names and collision suffixes IDENTICAL to the control run
//      (the withdrawal must not perturb the surviving surface);
//   5. no dangling P/Invoke — the hostile names appear only inside `// Unsupported:` comments, never as
//      a live member/entry point, the final wrapper carries no hostile symbol, and the generator log
//      never emitted SWIFTBIND108;
//   6. the positive-control type is emitted (the binding is a genuine PARTIAL success, not a shell).
//
// It is fail-closed on all of the above.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    const string ResilienceModule = "ResilienceKitchen";
    const string ResilienceWrapperModule = "ResilienceKitchenSwiftBindings";
    const string ResilienceHostileDefine = "RESILIENCE_HOSTILE";

    // The members the emitter binds wrong (IUO-on-generic-class); the loop must withdraw exactly
    // these and nothing else. Swift member names — the withdrawal rows and `// Unsupported:` comments
    // key off them, and their C# projection (HostileWidget/HostileSecond) is caught by the same
    // case-insensitive dangling scan.
    static readonly string[] ResilienceHostileMembers = { "hostileWidget", "hostileSecond" };

    // Fully-bindable type with no hostile members: its presence proves a genuine PARTIAL binding.
    static readonly string[] ResiliencePositiveControls = { "KitchenPlain" };

    // Every healthy sibling that shares a type with a hostile member (KitchenBox / KitchenPair). Each
    // MUST survive the withdrawal and stay bound. Asserting these ABSOLUTELY — not just that the hostile
    // and control surfaces agree — is what stops a shared regression that drops the same siblings from
    // BOTH slices (e.g. "skip every property on a generic class") from passing false-green. These are the
    // PROJECTED C# member names (properties keep their PascalCased Swift name; a nullary value-returning
    // method like describeTag() picks up the noun→Get prefix → GetDescribeTag). Matched with word
    // boundaries so `Count`/`Tag` hit the standalone property, not the substring inside GetPeekCount/
    // GetDescribeTag — each name must be independently present.
    static readonly string[] ResilienceHealthySiblings =
        { "Tag", "HealthyWidget", "GetDescribeTag", "First", "Count", "GetPeekCount" };

    AbsolutePath ResilienceSourceDir => BindingTestsDir / "Sources" / ResilienceModule;
    AbsolutePath ResilienceScratch => RootDirectory / "artifacts" / "resilience-kitchen";

    // Runs on every --compile-only invocation (see the CompileOnly branch in Build.BindingTests.cs).
    void RunResilienceKitchenGate()
    {
        Log.Information("=================================================");
        Log.Information(" BindingTests — wrapper verify-recover resilience gate");
        Log.Information("=================================================");

        EnsureGeneratorBuilt();

        var scratch = ResilienceScratch;
        if (Directory.Exists(scratch)) scratch.DeleteDirectory();
        scratch.CreateDirectory();

        // Build both slices from the one source tree: HOSTILE (with the define) and CONTROL (without).
        var hostileXcframework = BuildResilienceKitchenXcframework(scratch / "hostile-build", hostile: true);
        var controlXcframework = BuildResilienceKitchenXcframework(scratch / "control-build", hostile: false);

        // Generate both. The hostile run's exit code is the primary product signal.
        var hostileOut = scratch / "hostile-output";
        var controlOut = scratch / "control-output";
        hostileOut.CreateDirectory();
        controlOut.CreateDirectory();

        var (hostileExit, hostileLog) = RunResilienceKitchenGenerator(hostileXcframework, hostileOut);
        if (hostileExit != 0)
        {
            Log.Error("resilience-kitchen: generator exited {ExitCode} (expected 0). A module with a broken shape must "
                + "still generate a clean partial binding via the verify-recover loop.", hostileExit);
            foreach (var line in hostileLog) Log.Error("  [generator] {Line}", line);
            throw new Exception($"resilience-kitchen: hostile generator exit {hostileExit} ≠ 0 (fail-closed).");
        }

        var (controlExit, _) = RunResilienceKitchenGenerator(controlXcframework, controlOut);
        if (controlExit != 0)
            throw new Exception($"resilience-kitchen: control generator exit {controlExit} ≠ 0 — the hostile-free fixture must generate cleanly.");

        // SWIFTBIND108 (dangling wrapper EntryPoint) is a hard integrity failure; exit 0 already implies
        // it did not fire, but assert on the log too so a future demotion can never slip one through.
        if (hostileLog.Any(l => l.Contains("SWIFTBIND108", StringComparison.Ordinal)))
            throw new Exception("resilience-kitchen: generator log contains SWIFTBIND108 (dangling wrapper EntryPoint) — integrity must stay hard.");

        // (2) The emitted C# of the hostile fixture must compile.
        AssertResilienceKitchenCompiles(hostileOut);

        // (3) Every hostile member carries a correct withdrawal row.
        AssertResilienceWithdrawalRows(hostileOut / "binding-report.json");

        // (4) Healthy siblings survive with names + collision suffixes identical to the control run.
        AssertResilienceHealthySiblingsStable(hostileOut, controlOut);

        // (5) No dangling P/Invoke: hostile names appear only in `// Unsupported:` comments, and the
        //     final wrapper carries no hostile symbol.
        AssertResilienceNoDanglingWrapper(hostileOut);

        // (6) The positive control is emitted — the binding is a genuine partial success.
        AssertResiliencePositiveControls(hostileOut);

        Log.Information("resilience-kitchen PASSED — generator exit 0, C# compiles, {Count} hostile member(s) withdrawn "
            + "(EmitterFault/SwiftCompile), healthy siblings stable, no dangling wrapper.", ResilienceHostileMembers.Length);
    }

    // Builds a single simulator-slice xcframework from the fixture source, optionally with the
    // RESILIENCE_HOSTILE define (which conditionally compiles the hostile IUO members). Reuses the
    // shared CompileModuleSlice recipe so the generator sees exactly the artifact shape it consumes
    // for the main test lib.
    AbsolutePath BuildResilienceKitchenXcframework(AbsolutePath buildRoot, bool hostile)
    {
        if (Directory.Exists(buildRoot)) buildRoot.DeleteDirectory();
        buildRoot.CreateDirectory();

        var ios = ApplePlatform.IOS;
        var sdkPath = XcRun.GetSdkPath(ios.SimulatorSdkName);
        var simBuildDir = buildRoot / ios.SimulatorSliceId;
        var frameworkDir = simBuildDir / $"{ResilienceModule}.framework";

        var sources = Directory.GetFiles(ResilienceSourceDir, "*.swift", SearchOption.AllDirectories).ToList();
        if (sources.Count == 0)
            throw new Exception($"resilience-kitchen: no Swift sources found under {ResilienceSourceDir}.");

        Log.Information("=== resilience-kitchen: building {Module} simulator slice ({Variant}) ===",
            ResilienceModule, hostile ? "hostile" : "control");
        CompileModuleSlice(
            ResilienceModule, ios.SimulatorTarget, sdkPath,
            ios.SimulatorModuleSuffix, ios.MinOsVersion, ios.SimulatorPlistPlatform,
            frameworkDir, sources, frameworkSearchPaths: new[] { simBuildDir.ToString() },
            swiftDefines: hostile ? new[] { ResilienceHostileDefine } : null);

        var xcframeworkPath = buildRoot / $"{ResilienceModule}.xcframework";
        if (Directory.Exists(xcframeworkPath)) xcframeworkPath.DeleteDirectory();
        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
            .AddFrameworkPath(frameworkDir)
            .SetOutputPath(xcframeworkPath));

        return xcframeworkPath;
    }

    (int ExitCode, IReadOnlyList<string> Log) RunResilienceKitchenGenerator(AbsolutePath xcframework, AbsolutePath outputDir)
    {
        var genArgs = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{xcframework}\"",
            $"-o \"{outputDir}\"",
            $"--async-library {ResilienceWrapperModule}",
        };

        var proc = ProcessTasks.StartProcess("dotnet", string.Join(" ", genArgs),
            workingDirectory: BindingTestsDir, logOutput: false);
        proc.WaitForExit();
        return (proc.ExitCode, proc.Output.Select(o => o.Text).ToList());
    }

    // Compile-checks the generated C# with a hermetic csproj referencing only Swift.Runtime +
    // Swift.Bindings.Apple — the same shape as the partial-success-kitchen gate, scoped to this output.
    void AssertResilienceKitchenCompiles(AbsolutePath outputDir)
    {
        var csprojDir = outputDir / ".compile-check";
        if (Directory.Exists(csprojDir)) csprojDir.DeleteDirectory();
        csprojDir.CreateDirectory();
        var csprojPath = csprojDir / "ResilienceCompileCheck.csproj";

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFramework>net10.0-ios</TargetFramework>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <Nullable>enable</Nullable>
                <NoWarn>CS0169;CS0649;CA1418;CA1420</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="{SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj"}" />
                <ProjectReference Include="{SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj"}" />
              </ItemGroup>
              <ItemGroup>
                <Compile Include="{outputDir / $"{ResilienceModule}.cs"}" />
                <Compile Include="{outputDir / $"{ResilienceModule}.Types.*.cs"}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(csprojPath, csproj);

        Log.Information("=== resilience-kitchen: compile-checking generated C# ===");
        DotNetBuild(s => s
            .SetProjectFile(csprojPath)
            .SetConfiguration("Debug")
            .SetVerbosity(DotNetVerbosity.quiet));
        Log.Information("  generated C# compiled cleanly.");
    }

    // (3) Each hostile member must carry a wrapper-withdrawal row with the right attribution.
    void AssertResilienceWithdrawalRows(AbsolutePath reportPath)
    {
        if (!File.Exists(reportPath))
            throw new Exception($"resilience-kitchen: binding-report.json missing at {reportPath}.");

        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("SkippedItems", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new Exception("resilience-kitchen: binding-report.json has no SkippedItems array — the loop produced no withdrawal rows.");

        foreach (var member in ResilienceHostileMembers)
        {
            var row = items.EnumerateArray().FirstOrDefault(it =>
                it.TryGetProperty("Name", out var n) && n.GetString() == member);
            if (row.ValueKind != JsonValueKind.Object)
                throw new Exception($"resilience-kitchen: no SkippedItems row for hostile member '{member}' — it must be withdrawn, not silently dropped or (worse) emitted.");

            string Str(string prop) => row.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

            var reason = Str("Reason");
            if (reason != "EmitterFault")
                throw new Exception($"resilience-kitchen: hostile member '{member}' Reason='{reason}' (expected EmitterFault). The withdrawal must be attributed to the emitter.");

            var details = Str("Details");
            if (!details.Contains("Withdrawn by wrapper verify-recover", StringComparison.Ordinal))
                throw new Exception($"resilience-kitchen: hostile member '{member}' Details missing the withdrawal wording — got: {details}");

            var stage = Str("RecoveryStage");
            if (stage != "SwiftCompile")
                throw new Exception($"resilience-kitchen: hostile member '{member}' RecoveryStage='{stage}' (expected SwiftCompile — the wrapper failed to compile).");

            var owner = Str("CauseOwner");
            if (owner != "Generator")
                throw new Exception($"resilience-kitchen: hostile member '{member}' CauseOwner='{owner}' (expected Generator).");

            var rootCauseId = Str("RootCauseId");
            if (!rootCauseId.EndsWith("!accessor-group", StringComparison.Ordinal))
                throw new Exception($"resilience-kitchen: hostile member '{member}' RootCauseId='{rootCauseId}' (expected an '!accessor-group' root — the withdrawn unit is a property accessor group).");

            // These withdrawals are their OWN root cause, not cascade victims of another unit.
            if (row.TryGetProperty("CascadeFrom", out var cascade) && cascade.ValueKind != JsonValueKind.Null)
                throw new Exception($"resilience-kitchen: hostile member '{member}' has a non-null CascadeFrom='{cascade}' — it is a root withdrawal, not a cascade.");

            Log.Information("  ✓ withdrawal row: {Member} — {Reason}/{Stage}, root {Root}", member, reason, stage, rootCauseId);
        }
    }

    // (4) The healthy surviving surface must be byte-for-byte the same set of public declarations as a
    //     hostile-free control run — proving the withdrawal perturbs neither names nor collision suffixes.
    void AssertResilienceHealthySiblingsStable(AbsolutePath hostileOut, AbsolutePath controlOut)
    {
        var hostilePublic = CollectPublicMemberLines(hostileOut);
        var controlPublic = CollectPublicMemberLines(controlOut);

        var onlyInControl = controlPublic.Except(hostilePublic).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var onlyInHostile = hostilePublic.Except(controlPublic).OrderBy(s => s, StringComparer.Ordinal).ToList();

        if (onlyInControl.Count > 0 || onlyInHostile.Count > 0)
        {
            foreach (var l in onlyInControl) Log.Error("  ✗ healthy surface LOST in hostile run: {Line}", l);
            foreach (var l in onlyInHostile) Log.Error("  ✗ surface ADDED only in hostile run: {Line}", l);
            throw new Exception($"resilience-kitchen: the emitted public surface differs between the hostile and control runs "
                + $"({onlyInControl.Count} lost, {onlyInHostile.Count} added). Withdrawal must not perturb healthy siblings' "
                + "names or collision suffixes (and must not leave a dangling member).");
        }

        // ABSOLUTE positive control: relative equality above only proves the two slices AGREE. Assert each
        // named healthy sibling is genuinely PRESENT as a public member in BOTH — otherwise a shared
        // regression that drops the same siblings from both surfaces would leave the sets equal and slip
        // through green.
        foreach (var (label, surface) in new[] { ("hostile", hostilePublic), ("control", controlPublic) })
        {
            var absent = ResilienceHealthySiblings
                .Where(name => !surface.Any(line => System.Text.RegularExpressions.Regex.IsMatch(
                    line, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b")))
                .ToList();
            if (absent.Count > 0)
                throw new Exception($"resilience-kitchen: the {label} surface is missing healthy sibling(s) "
                    + $"[{string.Join(", ", absent)}] that must survive the withdrawal — a regression dropped a member "
                    + "the loop was supposed to keep. Relative hostile≡control equality alone would not have caught this.");
        }

        Log.Information("  ✓ healthy siblings stable: {Count} public declaration(s) identical to the control run; "
            + "all {Named} named siblings present in both slices.", hostilePublic.Count, ResilienceHealthySiblings.Length);
    }

    // Normalized set of every emitted `public` declaration across the module's generated C#. Nested
    // P/Invoke declarations are `internal static partial`, so they are naturally excluded.
    static HashSet<string> CollectPublicMemberLines(AbsolutePath outputDir)
    {
        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(outputDir, $"{ResilienceModule}*.cs", SearchOption.TopDirectoryOnly))
        {
            foreach (var raw in File.ReadLines(file))
            {
                var trimmed = raw.Trim();
                if (!trimmed.StartsWith("public ", StringComparison.Ordinal)) continue;
                // Collapse interior whitespace so cosmetic spacing can't spoof a diff.
                var normalized = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s+", " ");
                lines.Add(normalized);
            }
        }
        return lines;
    }

    // (5) No dangling wrapper: every emitted mention of a hostile name is a `// Unsupported:` comment,
    //     and the final wrapper Swift carries no hostile symbol.
    void AssertResilienceNoDanglingWrapper(AbsolutePath outputDir)
    {
        foreach (var file in Directory.EnumerateFiles(outputDir, $"{ResilienceModule}*.cs", SearchOption.TopDirectoryOnly))
        {
            foreach (var raw in File.ReadLines(file))
            {
                foreach (var member in ResilienceHostileMembers)
                {
                    if (raw.IndexOf(member, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (raw.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    throw new Exception($"resilience-kitchen: dangling reference to withdrawn member '{member}' in a live "
                        + $"(non-comment) line of {Path.GetFileName(file)}:\n    {raw.Trim()}");
                }
            }
        }

        // The healthy siblings emit `@_cdecl` wrappers, so this file MUST exist — treat its absence as a
        // hard failure rather than skipping the leak scan (a fail-open `if (File.Exists)` would let a
        // vanished wrapper pass this claim silently).
        var wrapper = outputDir / $"{ResilienceModule}.Wrapper.swift";
        if (!File.Exists(wrapper))
            throw new Exception($"resilience-kitchen: expected wrapper Swift '{wrapper.Name}' is missing — the healthy "
                + "siblings' @_cdecl wrappers must be re-emitted after the withdrawal.");
        var wrapperText = File.ReadAllText(wrapper);
        var leaked = ResilienceHostileMembers.Where(m => wrapperText.Contains(m, StringComparison.Ordinal)).ToList();
        if (leaked.Count > 0)
            throw new Exception($"resilience-kitchen: the final wrapper still carries withdrawn symbol(s): {string.Join(", ", leaked)}. "
                + "The verify-recover loop must re-emit the wrapper without the withdrawn units.");
        Log.Information("  ✓ no dangling wrapper: withdrawn members appear only in `// Unsupported:` comments.");
    }

    void AssertResiliencePositiveControls(AbsolutePath outputDir)
    {
        var emitted = string.Concat(Directory
            .EnumerateFiles(outputDir, $"{ResilienceModule}*.cs", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText));

        var missing = ResiliencePositiveControls
            .Where(t => !System.Text.RegularExpressions.Regex.IsMatch(
                emitted, $@"\b(class|struct)\s+{System.Text.RegularExpressions.Regex.Escape(t)}\b"))
            .ToList();

        if (missing.Count > 0)
            throw new Exception($"resilience-kitchen: positive-control type(s) not emitted: {string.Join(", ", missing)} — "
                + "the withdrawal took out more than the hostile members.");
        Log.Information("  ✓ positive controls emitted: {Controls}", string.Join(", ", ResiliencePositiveControls));
    }
}
