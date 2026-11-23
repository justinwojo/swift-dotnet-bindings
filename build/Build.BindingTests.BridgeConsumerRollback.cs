// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.BridgeConsumerRollback.cs — --bridge-consumer's rollback mode: a binding whose
// verified bridge relink was rolled back packs, and is consumed, as a safe partial binding.
//
// The generator's test-only switch (SWIFT_BINDINGS_FORCE_BRIDGE_ROLLBACK) refuses the verified relink,
// so the first compile's wrapper goes back in place, the C# back to generation's own guarded imports,
// and the call-plan sidecar records the withdrawal: why, from which library, and each restored
// transition with its shapes and the engines that refuse it. The binding's transition manifest
// embeds that sidecar, so the record ships inside the assembly and is covered by the manifest hash.
// The contract: the package is safe to consume, and the named members refuse, before Swift runs, on
// the named engines. The record explains the gaps; it exempts nothing.
//
// The mode proves it on every family the run names:
// - a fresh rollback packs (SWIFTBIND129 names the members and engines, no SWIFTBIND133), and its
//   evidence, read from the nupkg alone over every shipped slice, passes the accepting check and fails
//   the strict one, which admits no gap;
// - a rollback after a build that routed (the same project packed routed first, then regenerated
//   with the switch) packs the same way, with no bridge or manifest hash of the routed build left in
//   any shipped slice;
// - doctored copies of the rolled-back package (an unrecorded gap, an inconsistent record, a dangling
//   bridge import, a missing guard, the routed build's wrapper beside the rolled-back assembly) are
//   refused by the engine and by the packaged SDK and Runtime tasks themselves (SWIFTBIND133 deletes
//   the package; SWIFTBIND132 fails the consumer), each for its own reason, while the undoctored
//   package passes both;
// - a clean app consumes the rolled-back package through one PackageReference, seeing only the
//   shipped evidence: the consumer check records the binding Verified as a partial binding, the
//   members that need no bridge return the right values, and the withdrawn getter throws before Swift
//   runs on every engine its record names (it runs on NativeAOT, which handles both of its shapes).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;
using SwiftBindings.TransitionGate;

partial class Build
{
    /// <summary>One doctored copy of the rolled-back package and what refusing it must name.</summary>
    sealed record BcfRollbackControl(string Name, BridgeWithdrawalControl? Doctor, string Expect, string Remedy);

    static readonly BcfRollbackControl[] BcfRollbackControls =
    [
        new("unrecorded gap", BridgeWithdrawalControl.UnrecordedGap, "kept its direct import", "Regenerate the binding"),
        new("inconsistent record", BridgeWithdrawalControl.InconsistentRecord, "does not show that transition executable and restored", "a clean rebuild does not fix"),
        new("dangling bridge import", BridgeWithdrawalControl.DanglingBridgeImport, "which no manifest routes", "a clean rebuild does not fix"),
        new("missing guard", BridgeWithdrawalControl.MissingGuard, "residual guard", "a clean rebuild does not fix"),
        // The rolled-back assembly beside the wrapper slices of the build that routed: their bridges
        // and manifest hash are no manifest's.
        new("native hash mismatch", null, "exports SBSR_", "rebuild the binding clean"),
    ];

    IEnumerable<string> RunBridgeConsumerRollback(BcfFamily family, AbsolutePath nupkgDir, AbsolutePath xcframework, BcfRoute element)
    {
        var label = $"[rollback] {family.Platform}";
        var failures = new List<string>();
        var feed = family.Root / "rollback-feed";
        feed.CreateDirectory();
        try
        {
            // A fresh rollback.
            var freshDir = family.Root / "rollback-binding";
            freshDir.CreateDirectory();
            WriteBridgeConsumerBinding(family, freshDir, nupkgDir, xcframework, packable: true);
            var fresh = PackRolledBackBinding(family, $"{label} fresh", freshDir, "rollback-fresh");
            var binding = BridgeConsumerRolledBackBinding(family, $"{label} fresh", freshDir / "obj" / "Release", BcfModule, appRids: []);
            AssertRolledBackPackageEvidence(family, $"{label} fresh", fresh, nupkgDir);
            File.Copy(fresh, feed / fresh.Name);

            // A rollback after a build that routed: the same project packed routed, then regenerated
            // (its fingerprint stamp removed) and packed with the switch.
            var reroutedDir = family.Root / "rollback-after-routed-binding";
            reroutedDir.CreateDirectory();
            WriteBridgeConsumerBinding(family, reroutedDir, nupkgDir, xcframework, packable: true);
            var routed = PackRoutedBinding(family, $"{label} routed", reroutedDir);
            var stamps = Directory.GetFiles(reroutedDir / "obj", "swift-binding.stamp", SearchOption.AllDirectories);
            if (stamps.Length == 0)
                throw new Exception($"{label}: the routed build left no fingerprint stamp under {reroutedDir / "obj"}, so a regeneration cannot be forced.");
            foreach (var stamp in stamps)
                File.Delete(stamp);
            var afterRouted = PackRolledBackBinding(family, $"{label} after routed", reroutedDir, "rollback-after-routed");
            BridgeConsumerRolledBackBinding(family, $"{label} after routed", reroutedDir / "obj" / "Release", BcfModule, appRids: []);
            AssertRolledBackPackageEvidence(family, $"{label} after routed", afterRouted, nupkgDir);

            // The negative controls, through the engine and the packaged tasks.
            failures.AddRange(RunRollbackControls(family, label, fresh, routed, nupkgDir));

            // A clean consumer of the fresh package, which sees only what it ships.
            foreach (var platform in family.Platforms)
            {
                EvictBridgeConsumerBindingPackage();
                var appDir = family.Root / $"rollback-app-{platform.Name}";
                appDir.CreateDirectory();
                WriteBridgeConsumerApp(appDir, BcfMode.Rollback, platform, element, xcframework, projectReference: null);
                File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, feed, "BridgeConsumerFixture.*"));
                var appFailures = RunBridgeConsumerApp(family, BcfMode.Rollback, appDir, platform, element, _ => binding).ToList();
                if (!platform.OnDevice || platform.MonoAot || RuntimeLane.DeviceCoreClr)
                    appFailures.AddRange(RolledBackConsumerCheckProblems(appDir).Select(p => $"{label} {platform.Name}: {p}"));
                failures.AddRange(appFailures);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{label}: {ex.Message}");
        }
        finally
        {
            // The rolled-back package shares the package mode's id and version: no later restore may reuse it.
            EvictBridgeConsumerBindingPackage();
        }
        return failures;
    }

    // `dotnet pack` with the rollback switch, through a fresh MSBuild node so no reused node holds an
    // older environment. The pack must succeed: the generator warns SWIFTBIND129 naming the switch and
    // the engines the restored members refuse on, the pack check names the partial binding, and nothing
    // is refused.
    AbsolutePath PackRolledBackBinding(BcfFamily family, string label, AbsolutePath fixtureDir, string tag)
    {
        var output = fixtureDir.Parent / $"{fixtureDir.Name}-output";
        if (Directory.Exists(output)) output.DeleteDirectory();
        var (exitCode, lines) = PackBridgeConsumerBinding(family, fixtureDir, output, tag, forceRollback: true);
        var log = family.Receipts / $"{tag}-pack.log";
        var withdrawn = lines.FirstOrDefault(l => l.Contains("SWIFTBIND129", StringComparison.Ordinal) && l.Contains(BcfForceRollbackVariable, StringComparison.Ordinal));
        if (withdrawn is null)
            throw new Exception($"{label}: the pack never reported the forced rollback (SWIFTBIND129 naming {BcfForceRollbackVariable}); see {log}.");
        if (!withdrawn.Contains("packs as a partial binding", StringComparison.Ordinal) || !withdrawn.Contains("refuses on", StringComparison.Ordinal))
            throw new Exception($"{label}: SWIFTBIND129 does not name the withdrawn members and the engines they refuse on: {withdrawn.Trim()}");
        var refusal = lines.FirstOrDefault(l => l.Contains("error SWIFTBIND133", StringComparison.Ordinal));
        if (exitCode != 0 || refusal is not null)
            throw new Exception($"{label}: the pack of a rolled-back binding must succeed as a partial binding; it exited {exitCode}" +
                $"{(refusal is null ? "" : $": {refusal.Trim()}")}; see {log}.");
        if (!lines.Any(l => l.Contains("packs as a partial binding", StringComparison.Ordinal) && l.Contains("BridgeConsumerFixture.dll", StringComparison.Ordinal)))
            throw new Exception($"{label}: the pack check did not report the binding as a partial binding; see {log}.");
        var nupkg = output / $"{BcfPackageId}.{BcfLaneVersion}.nupkg";
        if (!File.Exists(nupkg))
            throw new Exception($"{label}: the pack succeeded but wrote no {nupkg.Name}; see {log}.");
        Log.Information("  {Label}: packed as a partial binding — {Warning}", label, withdrawn.Trim());
        return nupkg;
    }

    // `dotnet pack` without the switch: the binding routes its bridges. Returns a copy of the routed
    // package outside the project's output, which the next pack replaces.
    AbsolutePath PackRoutedBinding(BcfFamily family, string label, AbsolutePath fixtureDir)
    {
        var output = fixtureDir.Parent / $"{fixtureDir.Name}-output";
        var (exitCode, _) = PackBridgeConsumerBinding(family, fixtureDir, output, "rollback-routed", forceRollback: false);
        var nupkg = output / $"{BcfPackageId}.{BcfLaneVersion}.nupkg";
        if (exitCode != 0 || !File.Exists(nupkg))
            throw new Exception($"{label}: the routed pack failed (exit {exitCode}); see {family.Receipts / "rollback-routed-pack.log"}.");
        var plans = Directory.GetFiles(fixtureDir / "obj" / "Release", $"{BcfModule}.swift-call-plans.json", SearchOption.AllDirectories);
        if (plans.Length != 1 || !BcfBridged((AbsolutePath)plans[0]).Any())
            throw new Exception($"{label}: the routed build routes no bridge, so the rollback after it proves nothing.");
        var kept = family.Root / "rollback-routed.nupkg";
        File.Copy(nupkg, kept, overwrite: true);
        output.DeleteDirectory();
        return kept;
    }

    (int ExitCode, List<string> Lines) PackBridgeConsumerBinding(BcfFamily family, AbsolutePath fixtureDir, AbsolutePath output, string tag, bool forceRollback)
    {
        EvictBridgeConsumerBindingPackage();
        Log.Information("=== bridge-consumer [rollback] {Platform}: packing {Project} {How} ===", family.Platform, fixtureDir.Name,
            forceRollback ? "with its bridge relink rolled back" : "routed");
        var environment = new Dictionary<string, string>(EnvironmentInfo.Variables, StringComparer.Ordinal);
        if (forceRollback)
            environment[BcfForceRollbackVariable] = "1";
        else
            environment.Remove(BcfForceRollbackVariable);
        var pack = ProcessTasks.StartProcess("dotnet",
            $"pack \"{fixtureDir / $"{BcfModule}.csproj"}\" -c Release -o \"{output}\" -nodeReuse:false -nologo -v:detailed",
            workingDirectory: fixtureDir, environmentVariables: environment, logOutput: false);
        pack.WaitForExit();
        var lines = pack.Output.Select(o => o.Text).ToList();
        File.WriteAllLines(family.Receipts / $"{tag}-pack.log", lines);
        return (pack.ExitCode, lines);
    }

    // The rolled-back package's evidence, read from the nupkg alone over every shipped slice: the
    // accepting check passes, and the strict one, which admits no routing gap, refuses it for the
    // rollback it records.
    void AssertRolledBackPackageEvidence(BcfFamily family, string label, AbsolutePath nupkg, AbsolutePath nupkgDir)
    {
        using var scratch = new BcfScratchDirectory("rollback-evidence");
        var (assemblies, natives) = LoadRolledBackPackage(nupkg, nupkgDir, scratch.Path);
        if (natives.Count == 0)
            throw new Exception($"{label}: {nupkg.Name} ships no {BcfWrapperModule} slice.");
        var accepting = PackageTransitionEvidence.Check(assemblies, natives, requireRoutedBridges: false, scratch.Path / "accepting", acceptWithdrawals: true);
        if (!accepting.Passed)
            throw new Exception($"{label}: the rolled-back package's evidence does not hold up as its withdrawal: {string.Join("; ", accepting.Problems.Take(5))}");
        var strict = PackageTransitionEvidence.Check(assemblies, natives, requireRoutedBridges: false, scratch.Path / "strict");
        if (strict.Passed || !strict.Problems.Any(p => p.Contains("routing", StringComparison.Ordinal) && p.Contains(BcfForceRollbackVariable, StringComparison.Ordinal)))
            throw new Exception($"{label}: the strict check must refuse the rolled-back package for its routing gaps; " +
                (strict.Passed ? "it passed" : string.Join("; ", strict.Problems.Take(3))));
        Log.Information("  {Label}: {Assemblies} assembly(ies) and {Slices} {Wrapper} slice(s) hold up as the withdrawal; the strict check refuses it ({Problem})",
            label, assemblies.Count, natives.Count, BcfWrapperModule, strict.Problems.First(p => p.Contains("routing", StringComparison.Ordinal)));
        File.WriteAllLines(family.Receipts / $"{label.Split(' ').Last()}-evidence.txt",
            natives.Select(n => $"{n.DisplayPath}: clean").Prepend($"{nupkg.Name}: accepted as a partial binding; strict: {strict.Problems.Length} problem(s)"));
    }

    (List<PackagedAssembly> Assemblies, List<PackagedNative> Natives) LoadRolledBackPackage(AbsolutePath nupkg, AbsolutePath nupkgDir, AbsolutePath scratch)
    {
        var package = scratch / "package";
        ZipFile.ExtractToDirectory(nupkg, package);
        var references = BcfRuntimePackages(nupkgDir).Select((r, i) =>
        {
            var directory = scratch / $"reference-{i}";
            ZipFile.ExtractToDirectory(r, directory);
            return directory;
        }).ToList();
        return (LoadPackagedAssemblies(package, $"{BcfModule}.dll", references),
            LoadPackagedNatives(package, new PackageEvidenceSubject($"{BcfModule}.dll", NativeRoot: null, BcfWrapperModule, RoutesBridges: false)));
    }

    AbsolutePath[] BcfRuntimePackages(AbsolutePath nupkgDir) =>
        new[] { nupkgDir / $"SwiftBindings.Runtime.{BcfLaneVersion}.nupkg", nupkgDir / $"SwiftBindings.Apple.{BcfLaneAppleVersion}.nupkg" }
            .Where(r => File.Exists(r)).ToArray();

    // Each control: the rolled-back package doctored one way, then checked by the engine (accepting,
    // as the pack and consumer checks read it) and by the SDK's pack task and the Runtime's consumer
    // task as the packages ship them. Each must refuse for the control's own reason; the undoctored
    // package must pass both tasks.
    IEnumerable<string> RunRollbackControls(BcfFamily family, string label, AbsolutePath fresh, AbsolutePath routed, AbsolutePath nupkgDir)
    {
        var failures = new List<string>();
        using var scratch = new BcfScratchDirectory("rollback-controls");
        var tasks = StageTransitionTasks(nupkgDir, scratch.Path / "tasks");
        var (assemblies, _) = LoadRolledBackPackage(fresh, nupkgDir, scratch.Path / "fresh");
        var assembly = assemblies.FirstOrDefault(a => a.DisplayPath.Contains($"-{family.Platform}", StringComparison.Ordinal)) ?? assemblies[0];
        var entry = Path.GetRelativePath(scratch.Path / "fresh" / "package", assembly.DisplayPath).Replace('\\', '/');
        var receipts = new List<string>();

        // The positive control: the package as packed passes both tasks.
        var positive = RunTransitionTasks(tasks, scratch.Path / "positive", fresh, nupkgDir, family);
        if (positive.Pack.ExitCode != 0 || !positive.Pack.Lines.Any(l => l.Contains("packs as a partial binding", StringComparison.Ordinal)) || !File.Exists(positive.Package))
            failures.Add($"{label} positive control: the pack task did not accept the rolled-back package as a partial binding (exit {positive.Pack.ExitCode}).");
        if (positive.Consume.ExitCode != 0 || !positive.Consume.Lines.Any(l => l.Contains("is a partial binding", StringComparison.Ordinal)))
            failures.Add($"{label} positive control: the consumer task did not accept the rolled-back binding as a partial binding (exit {positive.Consume.ExitCode}).");
        receipts.Add($"positive: pack exit {positive.Pack.ExitCode}, consumer exit {positive.Consume.ExitCode}");

        foreach (var control in BcfRollbackControls)
        {
            var name = $"{label} control {control.Name}";
            var directory = scratch.Path / control.Name.Replace(' ', '-');
            directory.CreateDirectory();
            var doctored = directory / fresh.Name;
            File.Copy(fresh, doctored);
            byte[] image = assembly.Image;
            List<PackagedNative> natives;
            using (var zip = ZipFile.Open(doctored, ZipArchiveMode.Update))
            {
                if (control.Doctor is { } doctor)
                {
                    image = BridgeWithdrawalControls.Doctor(assembly.Image, assembly.DisplayPath, assembly.ReferenceDirectories, doctor);
                    ReplaceZipEntry(zip, entry, image);
                }
                else
                {
                    // Every wrapper slice the routed package ships, in place of the rolled-back ones.
                    using var routedZip = ZipFile.OpenRead(routed);
                    var slices = routedZip.Entries.Where(e => IsWrapperBinary(e.FullName)).ToList();
                    if (slices.Count == 0)
                        throw new Exception($"{name}: the routed package ships no {BcfWrapperModule} slice.");
                    foreach (var slice in slices)
                    {
                        using var stream = slice.Open();
                        using var memory = new MemoryStream();
                        stream.CopyTo(memory);
                        ReplaceZipEntry(zip, slice.FullName, memory.ToArray());
                    }
                }
            }
            using (var zip = ZipFile.OpenRead(doctored))
                natives = zip.Entries.Where(e => IsWrapperBinary(e.FullName)).Select(e =>
                {
                    using var stream = e.Open();
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    return new PackagedNative(BcfWrapperModule, e.FullName, memory.ToArray(), NativeCopyOfEntry(e.FullName));
                }).ToList();

            // The engine, as the pack check (images required) and an app's check (not) read it.
            var doctoredAssembly = assembly with { Image = image };
            var pack = PackageTransitionEvidence.Check([doctoredAssembly], natives, requireRoutedBridges: false, directory / "engine-pack", acceptWithdrawals: true);
            if (pack.Passed || !pack.Problems.Any(p => p.Contains(control.Expect, StringComparison.Ordinal)))
                failures.Add($"{name}: the engine's pack reading did not refuse it with '{control.Expect}': " +
                    (pack.Passed ? "it passed" : string.Join("; ", pack.Problems.Take(3))));
            var consumer = ConsumerTransitionCheck.Check(doctoredAssembly, natives, requireNativeImages: false, directory / "engine-consumer");
            if (consumer.Verdict != ConsumerTransitionVerdict.Refused || !consumer.Problems.Any(p => p.Contains(control.Expect, StringComparison.Ordinal)))
                failures.Add($"{name}: the engine's consumer reading did not refuse it with '{control.Expect}': {consumer.Verdict}; " +
                    string.Join("; ", consumer.Problems.Take(3)));

            // The packaged tasks.
            var run = RunTransitionTasks(tasks, directory / "tasks", doctored, nupkgDir, family);
            var packError = run.Pack.Lines.FirstOrDefault(l => l.Contains("error SWIFTBIND133", StringComparison.Ordinal));
            if (run.Pack.ExitCode == 0 || packError is null || !packError.Contains(control.Expect, StringComparison.Ordinal) && !run.Pack.Lines.Any(l => l.Contains(control.Expect, StringComparison.Ordinal)))
                failures.Add($"{name}: the SDK's pack task did not refuse the package with SWIFTBIND133 naming '{control.Expect}' (exit {run.Pack.ExitCode}).");
            else if (!packError.Contains(control.Remedy, StringComparison.Ordinal))
                failures.Add($"{name}: SWIFTBIND133 does not give the control's remedy ('{control.Remedy}'): {packError.Trim()}");
            if (File.Exists(run.Package))
                failures.Add($"{name}: the pack task refused the package but left it in place.");
            var consumeError = run.Consume.Lines.FirstOrDefault(l => l.Contains("error SWIFTBIND132", StringComparison.Ordinal));
            if (run.Consume.ExitCode == 0 || consumeError is null || !run.Consume.Lines.Any(l => l.Contains(control.Expect, StringComparison.Ordinal)))
                failures.Add($"{name}: the Runtime's consumer task did not refuse the binding with SWIFTBIND132 naming '{control.Expect}' (exit {run.Consume.ExitCode}).");
            File.WriteAllLines(family.Receipts / $"rollback-control-{control.Name.Replace(' ', '-')}.log",
                run.Pack.Lines.Prepend("== pack task ==").Concat(run.Consume.Lines.Prepend("== consumer task ==")));
            receipts.Add($"{control.Name}: engine pack {(pack.Passed ? "passed" : "refused")}, engine consumer {consumer.Verdict}, " +
                $"pack task exit {run.Pack.ExitCode}, consumer task exit {run.Consume.ExitCode}");
            Log.Information("  {Name}: refused by the engine and both tasks — {Problem}", name,
                pack.Problems.FirstOrDefault(p => p.Contains(control.Expect, StringComparison.Ordinal)) ?? "(see the log)");
        }
        File.WriteAllLines(family.Receipts / "rollback-controls.txt", receipts);
        return failures;
    }

    // A wrapper slice's binary: a Mac framework may be versioned, so the binary is matched by name
    // anywhere under the framework, as the package mode reads the packed wrapper.
    static bool IsWrapperBinary(string entry) =>
        entry.Contains($"/{BcfWrapperModule}.framework/", StringComparison.Ordinal) && Path.GetFileName(entry) == BcfWrapperModule;

    static void ReplaceZipEntry(ZipArchive zip, string name, byte[] content)
    {
        var existing = zip.GetEntry(name) ?? throw new Exception($"the package holds no {name} to replace.");
        existing.Delete();
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }

    /// <summary>The packaged copies of the SDK's and the Runtime's transition tasks, each beside its dependencies.</summary>
    sealed record BcfTransitionTasks(AbsolutePath PackTask, AbsolutePath ConsumerTask);

    BcfTransitionTasks StageTransitionTasks(AbsolutePath nupkgDir, AbsolutePath scratch)
    {
        AbsolutePath Stage(string package, string folder, string into)
        {
            var nupkg = nupkgDir / $"{package}.{BcfLaneVersion}.nupkg";
            var target = scratch / into;
            using var zip = ZipFile.OpenRead(nupkg);
            var entries = zip.Entries.Where(e => e.FullName.StartsWith(folder, StringComparison.Ordinal) && e.Name.Length > 0).ToList();
            if (!entries.Any(e => e.Name == "SwiftBindings.Tasks.dll"))
                throw new Exception($"{nupkg.Name} ships no {folder}SwiftBindings.Tasks.dll.");
            foreach (var e in entries)
            {
                var path = target / e.FullName[folder.Length..];
                path.Parent.CreateDirectory();
                e.ExtractToFile(path, overwrite: true);
            }
            return target / "SwiftBindings.Tasks.dll";
        }
        return new BcfTransitionTasks(Stage("SwiftBindings.Sdk", "Sdk/tasks/net10.0/", "sdk"), Stage("SwiftBindings.Runtime", "buildTransitive/tasks/net10.0/", "runtime"));
    }

    sealed record BcfTaskRun((int ExitCode, List<string> Lines) Pack, (int ExitCode, List<string> Lines) Consume, AbsolutePath Package);

    // Both tasks over one package, each from a project that names its inputs as items: the pack task
    // reads the nupkg (and deletes it if refused, so it reads a copy); the consumer task reads the
    // binding's assembly, the runtime's and the Apple supplement's, and the wrapper's frameworks, as an
    // app of this lane's Mono or CoreCLR engine resolves them from the package.
    BcfTaskRun RunTransitionTasks(BcfTransitionTasks tasks, AbsolutePath directory, AbsolutePath nupkg, AbsolutePath nupkgDir, BcfFamily family)
    {
        directory.CreateDirectory();
        var package = directory / "checked" / nupkg.Name;
        package.Parent.CreateDirectory();
        File.Copy(nupkg, package);
        var extracted = directory / "extracted";
        ZipFile.ExtractToDirectory(nupkg, extracted);
        var references = BcfRuntimePackages(nupkgDir).Select((r, i) =>
        {
            var target = directory / $"reference-{i}";
            ZipFile.ExtractToDirectory(r, target);
            return target;
        }).ToList();
        var assembly = Directory.GetFiles(extracted / "lib", $"{BcfModule}.dll", SearchOption.AllDirectories).Single();
        var framework = NuGet.Frameworks.NuGetFramework.ParseFolder(Path.GetFileName(Path.GetDirectoryName(assembly)!));
        var libs = references.Select(r => NearestLibDirectory(r, framework)).OfType<string>().ToList();
        var compile = libs.SelectMany(l => Directory.GetFiles(l, "*.dll")).Prepend(assembly).ToList();
        var natives = Directory.GetDirectories(extracted, $"{BcfWrapperModule}.xcframework", SearchOption.AllDirectories).ToList();
        if (natives.Count == 0)
            natives = Directory.GetDirectories(extracted, $"{BcfWrapperModule}.framework", SearchOption.AllDirectories).ToList();
        if (natives.Count == 0)
            throw new Exception($"{nupkg.Name} ships no {BcfWrapperModule} framework for the consumer task to check.");
        var engine = RuntimeLane.IsNet11 ? "the .NET 11 CoreCLR interpreter (Debug)" : "Mono";

        static string Items(string name, IEnumerable<string> paths) =>
            string.Concat(paths.Select(p => $"    <{name} Include=\"{System.Security.SecurityElement.Escape(p)}\" />\n"));
        (int, List<string>) Run(string target, string body)
        {
            var project = directory / $"{target}.proj";
            File.WriteAllText(project, $"""
                <Project>
                  <UsingTask TaskName="SwiftBindings.Tasks.SwiftTransitionPackageCheck" AssemblyFile="{tasks.PackTask}" />
                  <UsingTask TaskName="SwiftBindings.Tasks.SwiftTransitionConsumerCheck" AssemblyFile="{tasks.ConsumerTask}" />
                  <ItemGroup>
                {body}
                  </ItemGroup>
                  <Target Name="Check">
                    <SwiftTransitionPackageCheck Condition="'{target}' == 'pack'" Packages="@(Package)" ReferenceDirectories="@(ReferenceDirectory)" ScratchDirectory="{directory / target}-scratch" />
                    <SwiftTransitionConsumerCheck Condition="'{target}' == 'consumer'" CompileReferences="@(CompileReference)" NativeReferences="@(NativeReference)" Engine="{engine}"
                        CacheFile="{directory / target}-scratch/swiftbindings-transition-check.json" ScratchDirectory="{directory / target}-scratch/scan" />
                  </Target>
                </Project>
                """);
            var process = ProcessTasks.StartProcess("dotnet", $"msbuild \"{project}\" -t:Check -nologo -nodeReuse:false -v:n",
                workingDirectory: directory, logOutput: false);
            process.WaitForExit();
            return (process.ExitCode, process.Output.Select(o => o.Text).ToList());
        }

        var packRun = Run("pack", Items("Package", [package]) + Items("ReferenceDirectory", libs));
        var consumeRun = Run("consumer", Items("CompileReference", compile) + Items("NativeReference", natives));
        return new BcfTaskRun(packRun, consumeRun, package);
    }

    // What the clean consumer's own transition check recorded under obj/: the binding Verified, carrying
    // its withdrawal, with every withdrawn member named.
    static IEnumerable<string> RolledBackConsumerCheckProblems(AbsolutePath appDir)
    {
        var caches = Directory.Exists(appDir / "obj")
            ? Directory.GetFiles(appDir / "obj", "swiftbindings-transition-check.json", SearchOption.AllDirectories)
            : [];
        if (caches.Length == 0)
        {
            yield return "the consumer's transition check left no cache under obj/: it did not run";
            yield break;
        }
        foreach (var cache in caches)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(cache));
            var binding = document.RootElement.GetProperty("Results").EnumerateObject().Select(r => r.Value)
                .FirstOrDefault(r => r.GetProperty("Name").GetString() == BcfModule);
            if (binding.ValueKind != JsonValueKind.Object)
            {
                yield return $"the consumer's transition check ({cache}) did not inspect {BcfModule}";
                continue;
            }
            var verdict = binding.GetProperty("Verdict").GetString();
            var withdrawals = binding.TryGetProperty("Withdrawals", out var w) ? w.EnumerateArray().ToList() : [];
            if (verdict != "Verified")
                yield return $"the consumer's transition check classed {BcfModule} {verdict}; a coherent rollback must be Verified";
            else if (withdrawals.Count != 1 || withdrawals[0].GetProperty("Members").GetInt32() == 0
                     || !withdrawals[0].GetProperty("Reason").GetString()!.Contains(BcfForceRollbackVariable, StringComparison.Ordinal))
                yield return $"the consumer's transition check accepted {BcfModule} without the withdrawal it ships ({withdrawals.Count} recorded)";
            else
                Log.Information("  consumer check: {Module} Verified as a partial binding — {Refusals}", BcfModule, withdrawals[0].GetProperty("Refusals").GetString());
        }
    }

    // The rolled-back binding builds under `obj`: each sidecar plans members but routes none and records
    // the withdrawal (relink failed, naming the switch) with every gap it explains listed, each routing
    // journal rewrites nothing, and every direct transition the compiled assembly makes is guarded with
    // the shapes the import carries (no guard left that a routing settled to refuse nothing).
    BcfBinding BridgeConsumerRolledBackBinding(BcfFamily family, string label, AbsolutePath obj, string assemblyName, IReadOnlyList<string> appRids)
    {
        var plansName = $"{BcfModule}.swift-call-plans.json";
        var builds = (Directory.Exists(obj) ? Directory.GetFiles(obj, plansName, SearchOption.AllDirectories) : [])
            .Select(p => BcfBindingDirectory((AbsolutePath)p)).Distinct().OrderBy(d => d.ToString(), StringComparer.Ordinal).ToList();
        string? RidOf(AbsolutePath bindingDir) => appRids.Contains(bindingDir.Parent.Name) ? bindingDir.Parent.Name : null;
        var expected = appRids.Count > 1 ? appRids.Count : 1;
        if (builds.Count(b => appRids.Count <= 1 || RidOf(b) is not null) != expected || builds.Count(b => appRids.Count > 1 && RidOf(b) is null) > 1)
            throw new Exception($"expected {expected} binding build(s) under {obj}{(appRids.Count > 1 ? $" (one per RID: {string.Join(", ", appRids)}), plus at most the outer build's" : "")}; " +
                $"found {builds.Count}: {string.Join(", ", builds)}.");

        BcfBinding? chosen = null;
        foreach (var bindingDir in builds)
        {
            var outer = appRids.Count > 1 && RidOf(bindingDir) is null;
            var copyLabel = $"{label}{(appRids.Count > 1 ? $" {RidOf(bindingDir) ?? "outer build"}" : "")}";
            AbsolutePath One(string fileName)
            {
                var found = Directory.GetFiles(bindingDir, fileName, SearchOption.AllDirectories);
                if (found.Length != 1)
                    throw new Exception($"{copyLabel}: expected one {fileName} under {bindingDir}, found {found.Length}.");
                return (AbsolutePath)found[0];
            }
            var plans = One(plansName);
            var receipt = One("swift-lowering-receipt.json");
            var journal = One($"{BcfModule}.swift-bridge-routing.json");
            var tag = $"rollback-{obj.Parent.Parent.Name}-{obj.Name.ToLowerInvariant()}{(appRids.Count > 1 ? $"-{RidOf(bindingDir) ?? "outer"}" : "")}";
            foreach (var file in new[] { plans, receipt, journal })
                File.Copy(file, family.Receipts / $"{tag}-{file.Name}", overwrite: true);

            var root = JsonDocument.Parse(File.ReadAllText(plans)).RootElement;
            var coverage = root.GetProperty("coverage").EnumerateArray().ToList();
            var planned = coverage.Count(e => e.GetProperty("status").GetString() == "Planned");
            var withdrawn = coverage.Where(e => e.TryGetProperty("bridgeRefusal", out var r) && r.ValueKind == JsonValueKind.String
                && r.GetString()!.Contains(BcfForceRollbackVariable, StringComparison.Ordinal)).ToList();
            var bridged = BcfBridged(plans).Select(e => e.GetProperty("member").GetString()).ToList();
            if (planned == 0)
                throw new Exception($"{copyLabel}: the rolled-back build plans no member, so it proves nothing: {plans}.");
            if (withdrawn.Count == 0)
                throw new Exception($"{copyLabel}: no transition records the forced rollback as why it kept its direct import: {plans}.");
            if (bridged.Count > 0)
                throw new Exception($"{copyLabel}: the rolled-back build still routes {bridged.Count} member(s) through bridges: {string.Join(", ", bridged)}.");
            if (!root.TryGetProperty("bridgeWithdrawal", out var record) || record.ValueKind != JsonValueKind.Object)
                throw new Exception($"{copyLabel}: the rolled-back build's call plans record no bridge withdrawal: {plans}.");
            var listed = record.GetProperty("transitions").EnumerateArray()
                .Select(t => $"{t.GetProperty("member").GetString()}|{t.GetProperty("entryPoint").GetString()}").ToHashSet(StringComparer.Ordinal);
            var gaps = withdrawn.Select(e => $"{e.GetProperty("member").GetString()}|{e.GetProperty("entryPoint").GetString()}").ToHashSet(StringComparer.Ordinal);
            if (record.GetProperty("cause").GetString() != "RelinkFailed" || record.GetProperty("library").GetString() != BcfWrapperModule
                || !record.GetProperty("reason").GetString()!.Contains(BcfForceRollbackVariable, StringComparison.Ordinal) || !listed.SetEquals(gaps))
                throw new Exception($"{copyLabel}: the withdrawal record does not match the build (cause {record.GetProperty("cause").GetString()}, " +
                    $"library {record.GetProperty("library").GetString()}, {listed.Count} listed for {gaps.Count} withdrawn gap(s)): {plans}.");
            var rewrites = JsonDocument.Parse(File.ReadAllText(journal)).RootElement.GetProperty("rewrites").GetArrayLength();
            if (rewrites != 0)
                throw new Exception($"{copyLabel}: the rolled-back build's routing journal still records {rewrites} rewrite(s): {journal}.");
            if (outer)
                continue;

            var (guardCalls, refusingNothing) = AssertBridgeConsumerResidualGuards(family, copyLabel, tag, bindingDir.Parent / $"{assemblyName}.dll",
                One($"{BcfModule}.swift-transition-guards.json").Parent, plans);
            if (guardCalls == 0 || refusingNothing != 0)
                throw new Exception($"{copyLabel}: the rolled-back assembly has {guardCalls} guard call(s), {refusingNothing} of them refusing nothing; " +
                    "every import must keep the guard of its own shapes.");
            Log.Information("  {Label}: rolled back — {Planned} planned member(s), {Withdrawn} withdrawn and recorded, none routed, {Guards} guard call(s) all able to refuse",
                copyLabel, planned, withdrawn.Count, guardCalls);
            chosen ??= new BcfBinding(plans, receipt, Settle: null);
        }
        return chosen!;
    }

    /// <summary>A temporary directory, deleted on dispose.</summary>
    sealed class BcfScratchDirectory : IDisposable
    {
        public BcfScratchDirectory(string prefix)
        {
            Path = (AbsolutePath)System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Path.CreateDirectory();
        }

        public AbsolutePath Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Path.DeleteDirectory();
        }
    }
}
