// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.PackedResidualGate.cs — the residual-guard check over the managed assemblies `nuke pack`
// ships. The compile-only step checks the compile-check tree: the Debug build of Swift.Runtime and
// SwiftBindings.Apple for the platform it generated. The packages carry other builds: Release, every
// framework the projects target (the supplement's and the runtime's macOS builds, the runtime's
// portable one), each a separate assembly. This step opens the produced nupkgs and holds every
// lib/<framework>/ copy to the same rule: each known-unsafe direct Swift transition is reachable
// only through a member that refuses first on the runtimes that mishandle it. The supplement's
// copies are also tripwired against the guard sidecars its generated types were built with.
//
// Fail-closed and unconditional in Pack, with no baseline and no opt-out. Two controls keep it from
// passing vacuously: a seeded assembly holding one unguarded self-register transition, packed the
// same way, must fail the same check; and each package must carry the expected framework count,
// every copy must hold Swift transitions, and each supplement copy must have its ledger.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.TransitionGate;

partial class Build
{
    const string RuntimeAssemblyFile = "Swift.Runtime.dll";
    const string AppleSupplementAssemblyFile = "SwiftBindings.Apple.dll";

    AbsolutePath PackedResidualGuardsDir => RootDirectory / "artifacts" / "packed-residual-guards";

    void AssertPackedResidualGuards(AbsolutePath outputDir, string version, string appleVersion, bool skipApple)
    {
        Log.Information("=========================================");
        Log.Information(" Packed residual-guard check");
        Log.Information("=========================================");
        var output = PackedResidualGuardsDir;
        output.CreateOrCleanDirectory();
        var failed = new List<string>();

        failed.AddRange(CheckSeededPackedResidualControl(output));

        var runtimeNupkg = outputDir / $"SwiftBindings.Runtime.{version}.nupkg";
        var runtimeFrameworks = ProjectTargetFrameworks(SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj");
        var runtime = PackedResidualCheck.Check(runtimeNupkg, RuntimeAssemblyFile, _ => Array.Empty<byte[]>(), _ => null);
        failed.AddRange(ReportPackedResiduals(runtime, runtimeFrameworks, output));

        if (!skipApple)
        {
            var runtimeImages = PackedResidualCheck.ReadLibEntries(runtimeNupkg, RuntimeAssemblyFile)
                .ToDictionary(e => PackedResidualCheck.ProjectFramework(e.Framework), e => e.Image, StringComparer.Ordinal);
            var appleNupkg = outputDir / $"SwiftBindings.Apple.{appleVersion}.nupkg";
            var appleFrameworks = ProjectTargetFrameworks(AppleSupplementDir / "Swift.Bindings.Apple.csproj");
            var ledgers = new Dictionary<string, AbsolutePath>(StringComparer.Ordinal);
            var apple = PackedResidualCheck.Check(appleNupkg, AppleSupplementAssemblyFile,
                framework => runtimeImages.TryGetValue(PackedResidualCheck.ProjectFramework(framework), out var image)
                    ? new[] { image }
                    : throw new Exception($"Packed residual-guard check: the runtime package has no {PackedResidualCheck.ProjectFramework(framework)} build for the supplement's {framework}."),
                framework => ledgers[framework] = AppleSupplementDir / "obj" / "Release" / PackedResidualCheck.ProjectFramework(framework) / "AppleTypes",
                HandWrittenResidualTypes(AppleSupplementAssemblyFile));
            failed.AddRange(ReportPackedResiduals(apple, appleFrameworks, output));
            // The sidecar tripwire reads the directory the pack's own build wrote; an absent ledger
            // means it read nothing, and an empty directory would agree with any hazard-free scan.
            foreach (var (framework, directory) in ledgers)
                if (!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*.swift-transition-ledger.json").Any())
                {
                    Log.Error("  control: the supplement's {Framework} build left no transition ledger in {Directory}", framework, directory);
                    failed.Add($"{AppleSupplementAssemblyFile} {framework} (no ledger)");
                }
        }

        if (failed.Count > 0)
            throw new Exception($"Packed residual-guard check FAILED for {string.Join(", ", failed)} — see the lines above and {output}.");
        Log.Information("Packed residual-guard check: PASSED; {Output}", output);
    }

    /// <summary>
    /// Logs and writes each packed assembly's account, and returns what failed: the check itself, a
    /// copy that holds no Swift transition at all (the scan read nothing), or a framework count that
    /// differs from what the project targets (a build the package lost, or one nothing here built).
    /// </summary>
    static IEnumerable<string> ReportPackedResiduals(IReadOnlyList<PackedAssemblyResidual> residuals, IReadOnlyCollection<string> expectedFrameworks, AbsolutePath output)
    {
        var failed = new List<string>();
        var package = residuals.FirstOrDefault()?.Package ?? "(no package)";
        var frameworks = residuals.Select(r => PackedResidualCheck.ProjectFramework(r.Framework)).Order(StringComparer.Ordinal).ToArray();
        if (!frameworks.SequenceEqual(expectedFrameworks.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            Log.Error("  control: {Package} carries [{Found}], but its project targets [{Expected}]",
                package, string.Join(", ", frameworks), string.Join(", ", expectedFrameworks));
            failed.Add($"{package} (frameworks)");
        }
        foreach (var residual in residuals)
        {
            var summary = PackedResidualCheck.ToSummary(residual);
            File.WriteAllText(output / $"{Path.GetFileNameWithoutExtension(residual.Package)}.{residual.Framework}.txt", summary);
            Log.Information("[{Package} {Framework}] residual guards: {Verdict} ({Imports} Swift import(s), {Twins} twin(s), {Hazards} hazard(s), {Holders} holder(s), {Guards} guard call(s))",
                residual.Package, residual.Framework, residual.Passed ? "passed" : "FAILED", residual.Report.Inventory.SwiftPInvokes,
                residual.Report.Inventory.BridgeTwins, residual.Result.Hazards.Length, residual.Result.Holders.Length, residual.Result.GuardCalls);
            foreach (var violation in residual.Result.Violations)
                Log.Error("  {Rule} {Member}: {Reason}", violation.Rule, violation.Member, violation.Reason);
            foreach (var error in residual.Result.Errors.Concat(residual.Report.InspectionErrors))
                Log.Error("  inspection error {Member}: {Message}", error.Member, error.Message);
            foreach (var problem in residual.SidecarProblems)
                Log.Error("  sidecar {Problem}", problem);
            if (!residual.Passed)
                failed.Add($"{residual.Package} {residual.Framework}");
            if (residual.Report.Inventory.SwiftPInvokes + residual.Report.Inventory.BridgeTwins == 0)
            {
                Log.Error("  control: {Package} {Framework} holds no Swift-convention import or bridge twin: the scan read nothing", residual.Package, residual.Framework);
                failed.Add($"{residual.Package} {residual.Framework} (no Swift transitions)");
            }
        }
        return failed;
    }

    /// <summary>
    /// The positive control: one public member calling an untyped-self Swift-convention import with no
    /// guard, compiled here and packed under <c>lib/</c> like a shipped assembly, must fail the same
    /// check with an unguarded holder. A check that stopped reading packages, or stopped seeing
    /// hazards, fails here instead of passing every real package.
    /// </summary>
    static IEnumerable<string> CheckSeededPackedResidualControl(AbsolutePath output)
    {
        const string source = """
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Swift;
            [assembly: DisableRuntimeMarshalling]
            public static class SeededResidual
            {
                [DllImport("Seeded", EntryPoint = "$s6Seeded5ProbeC5valueSivg")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                private static extern nint Value(SwiftSelf self);

                public static unsafe nint Read(nint handle) => Value(new SwiftSelf((void*)handle));
            }
            """;
        var frameworkDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && path.StartsWith(frameworkDirectory, StringComparison.Ordinal))
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("SeededResidual",
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success)
            throw new Exception("Packed residual-guard check: the seeded control does not compile: " +
                                string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var nupkg = output / "Seeded.Residual.0.0.0.nupkg";
        using (var archive = ZipFile.Open(nupkg, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("lib/net10.0-ios26.0/SeededResidual.dll").Open())
            entry.Write(image.ToArray());
        var seeded = PackedResidualCheck.Check(nupkg, "SeededResidual.dll", _ => Array.Empty<byte[]>(), _ => null);
        File.WriteAllText(output / "seeded-control.txt", string.Concat(seeded.Select(PackedResidualCheck.ToSummary)));
        var caught = seeded.Length == 1
                     && seeded[0].Report.Inventory.SwiftPInvokes == 1
                     && seeded[0].Result.Violations.Any(v => v.Rule == ResidualGuardCheck.UnguardedRule && v.Member.StartsWith("SeededResidual::Read", StringComparison.Ordinal));
        if (caught)
        {
            Log.Information("  control: the seeded unguarded self-register transition fails the check");
            return Array.Empty<string>();
        }
        Log.Error("  control: the seeded unguarded self-register transition passed the check; see {Path}", output / "seeded-control.txt");
        return new[] { "seeded control (not caught)" };
    }

    /// <summary>The target frameworks a project builds, as its obj directory names them.</summary>
    static string[] ProjectTargetFrameworks(AbsolutePath project) =>
        System.Xml.Linq.XDocument.Load(project).Descendants("TargetFrameworks")
            .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
}
