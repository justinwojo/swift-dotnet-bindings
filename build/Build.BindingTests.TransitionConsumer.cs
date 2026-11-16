// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.TransitionConsumer.cs — the consumer transition check, end to end
// (`binding-tests --bridge-consumer`, mode `transition`).
//
// SwiftBindings.Runtime's buildTransitive targets refuse to build an app against a compiled binding
// whose Swift transitions the app's engine cannot be shown to run safely (SWIFTBIND132). This leg
// builds ordinary consumer apps against three packages from the same throwaway feed the bridge
// consumer packs, on each platform family the run names (the iOS simulator, Mac Catalyst, the tvOS
// simulator), in Debug and Release:
// - the binding the `package` mode just packed with SwiftBindings.Sdk: a valid manifest whose rules
//   hold and whose wrapper slices hold its hash. Builds, and the check must have verified it.
// - a first-party library packed with plain `dotnet pack`, as an older SDK packed bindings: no
//   transition manifest, and a direct Swift-convention import that takes an untyped SwiftSelf.
//   Fails with SWIFTBIND132 naming the assembly, the package and the import.
// - the same without the SwiftSelf import: one primitive-only Swift-convention import and no
//   manifest. Builds, and the check must have classed it primitive-only.
// On the iOS family two controls also run: the unsafe package with its
// <SwiftBindingsTransitionSuppress> item builds with the warning instead of the error, and the same
// unsafe package consumed by a macOS app (CoreCLR, an engine the check leaves alone) builds with no
// diagnostic and no check at all.
// The iOS family also builds the three consumers for the device (ios-arm64), Debug and Release,
// whatever platforms the run names: the default device engine, Mono full-AOT on .NET 10 and CoreCLR
// on .NET 11 (the interpreter in Debug, ReadyToRun in Release). The device builds are unsigned and
// never installed, so they need no phone; the fresh binding's verdict proves the check read its
// device slice.
//
// Each accepted build must show the check ran: its cache under obj/ lists the fixture with the
// expected verdict, so a check that silently stopped running cannot pass as one that accepted.

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
    const string TcLegacyPackageId = "TransitionCheck.LegacyFixture";
    const string TcPrimitivePackageId = "TransitionCheck.PrimitiveFixture";
    const string TcDiagnostic = "SWIFTBIND132";

    /// <summary>One consumer build of the leg and what it must produce.</summary>
    sealed record TcCase(string Name, string PackageId, string Assembly, string Platform, string Rid, string Configuration,
        TcExpect Expect, bool Suppress = false, bool Device = false);

    enum TcExpect { Verified, PrimitiveOnly, Refused, Suppressed, NotChecked }

    /// <summary>
    /// Packs the two hand-written fixtures into <paramref name="feed"/> once per run: the unsafe
    /// one and the primitive-only one, each depending on the feed's SwiftBindings.Runtime as any
    /// binding does, which is how its consumers get the check.
    /// </summary>
    void PackTransitionCheckFixtures(AbsolutePath root, AbsolutePath nupkgDir, AbsolutePath feed)
    {
        feed.CreateDirectory();
        foreach (var (id, unsafeImport) in new[] { (TcLegacyPackageId, true), (TcPrimitivePackageId, false) })
        {
            var dir = root / id;
            dir.CreateDirectory();
            File.WriteAllText(dir / $"{id}.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>{RuntimeLane.Framework}</TargetFramework>
                    <PackageId>{id}</PackageId>
                    <PackageVersion>{BcfLaneVersion}</PackageVersion>
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                    <Nullable>enable</Nullable>
                    <IsPackable>true</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
                    <PackageReference Include="SwiftBindings.Runtime" Version="{BcfLaneVersion}" />
                  </ItemGroup>
                </Project>
                """);
            // An entry point no consumer calls: the check reads metadata and IL, never runs anything.
            var import = unsafeImport
                ? """
                    [DllImport("TransitionCheckFixture", EntryPoint = "$s22TransitionCheckFixture7CounterC5countSivg")]
                    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                    private static extern nint CountOf(SwiftSelf self);

                    public static nint Count(nint handle) => CountOf(new SwiftSelf((void*)handle));
                  """
                : """
                    [DllImport("TransitionCheckFixture", EntryPoint = "$s22TransitionCheckFixture6doubleyS2iF")]
                    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                    private static extern nint Doubled(nint value);

                    public static nint Twice(nint value) => Doubled(value);
                  """;
            File.WriteAllText(dir / "Counter.cs", $$"""
                // Copyright (c) 2026 Justin Wojciechowski.
                // Licensed under the MIT License.
                using System.Runtime.CompilerServices;
                using System.Runtime.InteropServices;
                using System.Runtime.InteropServices.Swift;

                namespace TransitionCheckFixture;

                public static unsafe class Counter
                {
                {{import}}
                }
                """);
            File.WriteAllText(dir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));
            Log.Information("=== bridge-consumer [transition]: packing {Id} without a transition manifest ===", id);
            DotNetPack(s => s
                .SetProject(dir / $"{id}.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(feed)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));
        }
    }

    /// <summary>Removes this run's fixtures from the global packages folder: their id and version repeat every run.</summary>
    void EvictTransitionCheckFixtures()
    {
        var cache = (AbsolutePath)(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
        foreach (var id in new[] { TcLegacyPackageId, TcPrimitivePackageId })
        {
            var dir = cache / id.ToLowerInvariant() / BcfLaneVersion;
            if (Directory.Exists(dir)) dir.DeleteDirectory();
        }
    }

    /// <summary>
    /// Builds the leg's consumers for <paramref name="family"/> against the binding the package mode
    /// packed into <paramref name="bindingFeed"/> and the fixtures in <paramref name="fixtureFeed"/>.
    /// Returns the failures.
    /// </summary>
    IEnumerable<string> RunTransitionCheckConsumers(BcfFamily family, AbsolutePath nupkgDir, AbsolutePath bindingFeed, AbsolutePath fixtureFeed)
    {
        var cases = new List<TcCase>();
        void AddConsumers(string prefix, string tfmPlatform, string rid, bool device)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                cases.Add(new($"{prefix}fresh-{configuration}", BcfPackageId, BcfModule, tfmPlatform, rid, configuration, TcExpect.Verified, Device: device));
                cases.Add(new($"{prefix}unsafe-{configuration}", TcLegacyPackageId, TcLegacyPackageId, tfmPlatform, rid, configuration, TcExpect.Refused, Device: device));
                cases.Add(new($"{prefix}primitive-{configuration}", TcPrimitivePackageId, TcPrimitivePackageId, tfmPlatform, rid, configuration, TcExpect.PrimitiveOnly, Device: device));
            }
        }
        var platform = family.Platforms.FirstOrDefault(p => !p.OnDevice);
        if (platform is not null)
        {
            var rid = platform.Runs[0].Rid;
            AddConsumers("", platform.TfmPlatform, rid, device: false);
            if (family.Platform == "ios")
            {
                cases.Add(new("unsafe-suppressed-Debug", TcLegacyPackageId, TcLegacyPackageId, platform.TfmPlatform, rid, "Debug", TcExpect.Suppressed, Suppress: true));
                // macOS runs CoreCLR's JIT on both .NET 10 and .NET 11, which the check leaves alone.
                cases.Add(new("unsafe-macos-Debug", TcLegacyPackageId, TcLegacyPackageId, "macos", "osx-arm64", "Debug", TcExpect.NotChecked));
            }
        }
        // The device consumers are build-only and unsigned, so they run whether or not the run names
        // --device: Mono full-AOT on .NET 10, the CoreCLR interpreter (Debug) and ReadyToRun (Release)
        // on .NET 11. NativeAOT, the one engine the check leaves alone on a device, is a publish.
        if (family.Platform == "ios")
            AddConsumers("device-", "ios", "ios-arm64", device: true);

        var failures = new List<string>();
        var report = new List<string>();
        foreach (var c in cases)
        {
            var label = $"[transition] {family.Platform} {c.Name} ({RuntimeLane.Framework}, {c.Rid}{(c.Device ? $", {TransitionDeviceEngine(c.Configuration)}" : "")})";
            var appDir = family.Root / "transition" / c.Name;
            if (Directory.Exists(appDir)) appDir.DeleteDirectory();
            appDir.CreateDirectory();
            WriteTransitionCheckApp(appDir, c, nupkgDir, bindingFeed, fixtureFeed);
            Log.Information("=== bridge-consumer {Label}: building, expecting {Expect} ===", label, c.Expect);
            var proc = ProcessTasks.StartProcess("dotnet",
                $"build \"{appDir / "TransitionCheckApp.csproj"}\" -c {c.Configuration} -nodeReuse:false -nologo -v:minimal",
                workingDirectory: appDir, logOutput: false);
            proc.WaitForExit();
            var output = proc.Output.Select(o => o.Text).ToList();
            var log = family.Receipts / "transition" / $"{c.Name}.log";
            log.Parent.CreateDirectory();
            File.WriteAllLines(log, output);
            var problems = TransitionCheckOutcome(c, proc.ExitCode, output, appDir);
            var diagnostic = output.FirstOrDefault(l => l.Contains(TcDiagnostic, StringComparison.Ordinal));
            report.Add($"{family.Platform} {c.Name}: exit {proc.ExitCode}, expected {c.Expect}: {(problems.Count == 0 ? "OK" : "FAILED")}"
                       + (diagnostic is null ? "" : $"\n    {diagnostic.Trim()}"));
            if (problems.Count == 0)
                Log.Information("  ✓ {Label}: {Expect}", label, c.Expect);
            failures.AddRange(problems.Select(p => $"{label}: {p} (log: {log})"));
        }
        File.WriteAllLines(family.Receipts / "transition" / "summary.txt", report);
        return failures;
    }

    /// <summary>The engine a device consumer of this lane builds for, as the check names it.</summary>
    string TransitionDeviceEngine(string configuration)
        => !RuntimeLane.IsNet11 ? "Mono full-AOT" : configuration == "Debug" ? "CoreCLR interpreter" : "CoreCLR ReadyToRun";

    void WriteTransitionCheckApp(AbsolutePath appDir, TcCase c, AbsolutePath nupkgDir, AbsolutePath bindingFeed, AbsolutePath fixtureFeed)
    {
        // macOS 14 is the .NET 11 macOS SDK's floor (and above the .NET 10 one).
        var minimum = c.Platform switch { "maccatalyst" => "17.0", "macos" => "14.0", _ => "15.0" };
        File.WriteAllText(appDir / "TransitionCheckApp.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{RuntimeLane.Framework}-{c.Platform}</TargetFramework>
                <RuntimeIdentifier>{c.Rid}</RuntimeIdentifier>
                {MixedConsumerRuntimeProperties(RuntimeLane, device: false)}
                <Nullable>enable</Nullable>
                <ApplicationId>com.swiftbindings.transitioncheck</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>{minimum}</SupportedOSPlatformVersion>
                <MtouchLink>None</MtouchLink>
                <!-- The check runs before CoreCompile and keys on the configuration and engine, not on
                     code generation; LLVM would only add many minutes to each Mono Release build. -->
                <MtouchUseLlvm>false</MtouchUseLlvm>
                {(c.Device ? "<!-- Built, never installed: no signing identity or provisioning profile is needed. -->\n<EnableCodeSigning>false</EnableCodeSigning>" : "")}
                <NoWarn>$(NoWarn);CA1416;CA1422</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{c.PackageId}" Version="{BcfLaneVersion}" />
              </ItemGroup>
              {(c.Suppress ? $"""<ItemGroup><SwiftBindingsTransitionSuppress Include="{c.PackageId}" /></ItemGroup>""" : "")}
            </Project>
            """);
        File.WriteAllText(appDir / "Program.cs", """
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            public static class Program
            {
                public static void Main() { }
            }
            """);
        if (c.Platform != "macos")
        {
            // The iOS, tvOS and Mac Catalyst 27 SDKs fail the build (MT7199) without a scene
            // manifest. The app is only built, never launched, so it names no scene configuration.
            File.WriteAllText(appDir / "Info.plist",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\">\n<dict>\n" +
                "    <key>UIApplicationSceneManifest</key>\n" +
                "    <dict>\n" +
                "        <key>UIApplicationSupportsMultipleScenes</key>\n" +
                "        <false/>\n" +
                "    </dict>\n" +
                "</dict>\n</plist>\n");
        }
        File.WriteAllText(appDir / "NuGet.config", c.PackageId == BcfPackageId
            ? MixedPackNuGetConfig(nupkgDir, bindingFeed, "BridgeConsumerFixture.*")
            : MixedPackNuGetConfig(nupkgDir, fixtureFeed, "TransitionCheck.*"));
    }

    /// <summary>What is wrong with one consumer build, against what it must produce.</summary>
    static List<string> TransitionCheckOutcome(TcCase c, int exitCode, IReadOnlyList<string> output, AbsolutePath appDir)
    {
        var problems = new List<string>();
        var errors = output.Where(l => l.Contains($"error {TcDiagnostic}", StringComparison.Ordinal)).Distinct().ToList();
        var warnings = output.Where(l => l.Contains($"warning {TcDiagnostic}", StringComparison.Ordinal)).Distinct().ToList();
        var otherErrors = output.Where(l => l.Contains(": error ", StringComparison.Ordinal) && !l.Contains(TcDiagnostic, StringComparison.Ordinal)).Distinct().ToList();
        var verdicts = TransitionCheckVerdicts(appDir);

        switch (c.Expect)
        {
            case TcExpect.Refused:
                if (exitCode == 0)
                    problems.Add($"the build succeeded; it must fail with {TcDiagnostic}");
                if (errors.Count == 0)
                    problems.Add($"no error {TcDiagnostic}");
                else
                {
                    var text = errors[0];
                    foreach (var required in new[] { $"{c.Assembly}.dll", $"package {c.PackageId}", "no Swift transition manifest", "CountOf", "R1",
                                 "NativeAOT is not affected", $"<SwiftBindingsTransitionSuppress Include=\"{c.PackageId}\" />" })
                        if (!text.Contains(required, StringComparison.Ordinal))
                            problems.Add($"the {TcDiagnostic} error does not name '{required}': {text}");
                }
                if (otherErrors.Count > 0)
                    problems.Add($"other errors besides {TcDiagnostic}: {otherErrors[0]}");
                break;
            case TcExpect.Suppressed:
                if (exitCode != 0)
                    problems.Add($"the build failed (exit {exitCode}); the suppression must downgrade {TcDiagnostic} to a warning: {errors.Concat(otherErrors).FirstOrDefault()}");
                if (warnings.Count == 0)
                    problems.Add($"no warning {TcDiagnostic}: a suppression must stay loud");
                if (errors.Count > 0)
                    problems.Add($"error {TcDiagnostic} despite the suppression: {errors[0]}");
                break;
            default:
                if (exitCode != 0)
                    problems.Add($"the build failed (exit {exitCode}): {errors.Concat(otherErrors).FirstOrDefault() ?? "see the log"}");
                if (errors.Count + warnings.Count > 0)
                    problems.Add($"unexpected {TcDiagnostic}: {errors.Concat(warnings).First()}");
                break;
        }

        // The check's own record: an accepted build must show it ran and what it concluded.
        switch (c.Expect)
        {
            case TcExpect.NotChecked:
                if (verdicts is not null)
                    problems.Add("the check ran on an engine it must leave alone (its cache exists under obj/)");
                break;
            case TcExpect.Verified or TcExpect.PrimitiveOnly or TcExpect.Refused or TcExpect.Suppressed:
                var expected = c.Expect switch
                {
                    TcExpect.Verified => "Verified",
                    TcExpect.PrimitiveOnly => "PrimitiveOnly",
                    _ => "Refused",
                };
                if (verdicts is null)
                    problems.Add("the check left no cache under obj/: it did not run");
                else if (!verdicts.TryGetValue(c.Assembly, out var verdict))
                    problems.Add($"the check did not inspect {c.Assembly} (it inspected {string.Join(", ", verdicts.Keys)})");
                else if (verdict.Verdict != expected)
                    problems.Add($"the check classed {c.Assembly} {verdict.Verdict}; expected {expected}");
                else if (!verdicts.TryGetValue("Swift.Runtime", out var runtime))
                    problems.Add("the check did not inspect Swift.Runtime, which every consumer resolves");
                else if (c.Expect == TcExpect.Verified)
                {
                    // Verified is also the verdict when a routed library's image is absent; the packed
                    // binding and the runtime ship theirs, so the hash comparison must have run on both.
                    foreach (var (name, record) in new[] { (c.Assembly, verdict), ("Swift.Runtime", runtime) })
                        if (record.Unverified.Count > 0)
                            problems.Add($"the check found no native image of {string.Join(", ", record.Unverified)} for {name}, so it compared no manifest hash");
                }
                break;
        }
        return problems;
    }

    /// <summary>The verdicts (and routed libraries with no native image) the check's cache under <paramref name="appDir"/>/obj records, by assembly; null when there is none.</summary>
    static Dictionary<string, (string Verdict, List<string> Unverified)>? TransitionCheckVerdicts(AbsolutePath appDir)
    {
        var obj = appDir / "obj";
        var cache = Directory.Exists(obj)
            ? Directory.GetFiles(obj, "swiftbindings-transition-check.json", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (cache is null)
            return null;
        using var document = JsonDocument.Parse(File.ReadAllText(cache));
        return document.RootElement.GetProperty("Results").EnumerateObject()
            .Select(r => (Name: r.Value.GetProperty("Name").GetString()!, Verdict: r.Value.GetProperty("Verdict").GetString()!,
                Unverified: r.Value.GetProperty("Unverified").EnumerateArray().Select(u => u.GetString()!).ToList()))
            .GroupBy(r => r.Name)
            .ToDictionary(g => g.Key, g => (g.First().Verdict, g.First().Unverified));
    }
}
