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
// Load-safety P2 makes the binding refuse such a member before native code touches it: the generator
// floors every declaration whose symbol the shipped OS symbol inventory's OS lacks
// (OsSymbolFloorInference). The BindingTests dependency is generated with a supplied inventory that
// lists every Swift symbol it exports except a few fixture members (WriteDependencyTestInventory), so
// the floors, the weak imports and the managed throws are exercised against a real binary.
//
// The gate runs in every `--compile-only` invocation after the thunk weak-link gate. For every slice
// of both BindingTests wrappers it requires every `/System/` dylib load to be LC_LOAD_WEAK_DYLIB, and
// requires the wrapper to load at least one OS framework so the check cannot pass on an empty read.
// One strong load is sound and accepted: an umbrella that re-exports a library only its allowed
// clients may link (SwiftUI re-exports SwiftUICore). A symbol of that library binds through the
// umbrella, and ld then keeps the umbrella's load strong whatever -weak_framework asks, while every
// import from it stays weak, so a missing symbol still binds to null. The umbrella itself ships on
// every supported OS. The SDK's stubs decide which loads qualify.
//
// P2 is then judged from the artifacts alone, never from what a report says was guarded. Every slice's
// imports (`dyld_info -imports`) of a Swift symbol a covered provider (an OS-resident dylib, by install
// name, or a supplied provider) does not ship must be weak, and each must be reached from an export of
// the wrapper's arm64 code (WrapperImportReach: calls, tail branches, GOT loads, data that holds the
// symbol). Every managed method of CompileCheck.dll and SwiftBindings.Apple.dll that holds a P/Invoke
// of such an export, or a direct P/Invoke of such a symbol, must throw PlatformNotSupportedException
// at entry on the inventory's OS (OsFloorGuardCheck, which runs the method's IL with the OS queries
// answered), or be private with every caller refusing. A negative control compiles an unguarded and a
// guarded caller of a real floored export and requires exactly the unguarded one to fail, and positive
// controls require the dependency's omitted fixture symbols to be reached and judged. Fail-closed and
// unconditional, with no `--permissive` arm: a soundness property, not a baseline.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BindingsGeneration;
using SwiftBindings.TransitionGate;
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

    /// <summary>
    /// The generator's shipped OS symbol inventory, read from its source file: the build compiles the
    /// inventory reader but does not embed the inventory, so every check here passes it explicitly.
    /// </summary>
    static OsSymbolInventory ShippedOsSymbolInventory => s_shippedOsSymbolInventory.Value;

    static readonly Lazy<OsSymbolInventory> s_shippedOsSymbolInventory = new(() =>
        OsSymbolInventory.LoadFile(RootDirectory / "src" / "Swift.Bindings" / "src" / "Data" / "os-symbol-inventory.bin"));

    /// <summary>The path of the SDK <paramref name="binary"/> was built for, from its <c>LC_BUILD_VERSION</c>.</summary>
    string SdkPathOf(AbsolutePath binary)
    {
        var sdk = OsFrameworkWeakLinking.BuildPlatformSdk(ReadOtoolLoadCommands(binary))
            ?? throw new Exception($"{binary}: otool -l shows no LC_BUILD_VERSION platform, so its SDK is unknown.");
        return SdkPath(sdk);
    }

    /// <summary>The path of the SDK named <paramref name="sdk"/> (<c>iphonesimulator</c>, …).</summary>
    string SdkPath(string sdk)
    {
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
        strong.Count == 0 ? strong : OsFrameworkWeakLinking.UnsafeStrongLoads(strong, ReadDylibImports(binary), SdkPathOf(binary), ShippedOsSymbolInventory);

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

    /// <summary>The supplied inventory the BindingTests dependency is generated with.</summary>
    AbsolutePath DependencyTestInventoryPath => BtOutputDir / "os-symbol-inventory.dependency.json";

    /// <summary>
    /// The dependency fixture members (<c>OsSymbolInventoryProbes.swift</c>) the supplied inventory leaves
    /// out, so the generator floors them; every rule must match an exported symbol. The rules are mangled:
    /// Swift's word substitution spells a word the symbol already used ("Inventory", from the type name)
    /// as a back-reference, so <c>absentOnInventoryOs</c> is mangled <c>absentOnF2Os</c>.
    /// </summary>
    static readonly string[] DependencyInventoryOmissions =
    {
        "14InventoryProbeV08absentOnF2Os", "14InventoryProbeV014absentMethodOnF2Os", "20InventoryAbsentValueV",
        // init(rawValue:), the (Self, Self) `==` (not the (Self, Int) one declared before it), and a struct's `==`.
        "17InventoryRawProbeO8rawValueACSgSS_tcfC", "22InventoryEqualityProbeO2eeoiySbAC_ACtFZ", "23InventoryEquatableValueV2eeoiySbAC_ACtFZ",
    };

    /// <summary>The fixture declarations the dependency's binding report must record as raised.</summary>
    static readonly string[] DependencyInventoryFlooredDeclarations =
    {
        "InventoryProbe.absentOnInventoryOs", "InventoryProbe.absentMethodOnInventoryOs", "InventoryAbsentValue",
        "InventoryRawProbe.init", "InventoryEqualityProbe.==", "InventoryEquatableValue.==",
    };

    /// <summary>
    /// Writes the dependency's supplied inventory: every Swift symbol each of its slices exports, less
    /// <see cref="DependencyInventoryOmissions"/>, at OS 26.0 with floor 99.0, so its omitted members are
    /// floored above every OS a test runs on.
    /// </summary>
    AbsolutePath WriteDependencyTestInventory()
    {
        var binaries = Directory.GetDirectories(BtDepXcframeworkDir)
            .Select(slice => Path.Combine(slice, $"{DepModuleName}.framework", DepModuleName))
            .Where(File.Exists)
            .ToList();
        if (binaries.Count == 0)
            throw new Exception($"OS symbol inventory: no {DepModuleName} slice under {BtDepXcframeworkDir}.");
        var lines = binaries.SelectMany(b => XcRunTool($"nm -gUj \"{b}\"", logOutput: false, logInvocation: false)
            .Where(o => o.Type == OutputType.Std).Select(o => o.Text)).ToList();
        var json = OsLoadSafetyAudit.BuildSuppliedInventory(
            lines, $"@rpath/{DepModuleName}.framework/{DepModuleName}", DepModuleName, "26.0", "99.0",
            DependencyInventoryOmissions, out var omitted);
        File.WriteAllText(DependencyTestInventoryPath, json);
        Log.Information("OS symbol inventory for {Module}: omitted {Omitted}",
            DepModuleName, string.Join(", ", omitted.Select(o => $"{o.Key} ({o.Value})")));
        return DependencyTestInventoryPath;
    }

    /// <summary>The lines a tool printed to standard output, unlogged.</summary>
    IReadOnlyList<string> XcRunLines(ArgumentStringHandler arguments) =>
        XcRunTool(arguments, logOutput: false, logInvocation: false).Where(o => o.Type == OutputType.Std).Select(o => o.Text).ToList();

    /// <summary>Which imports each export of <paramref name="binary"/>'s arm64 code reaches, read from the binary.</summary>
    WrapperImportReach ReadArm64Reach(AbsolutePath binary) =>
        WrapperImportReach.Parse(
            XcRunLines($"objdump --macho -h --arch=arm64 \"{binary}\""),
            XcRunLines($"nm -n -arch arm64 \"{binary}\""),
            XcRunLines($"objdump --macho -d --no-show-raw-insn --arch=arm64 \"{binary}\""),
            XcRunLines($"dyld_info -arch arm64 -fixups \"{binary}\""));

    /// <summary>
    /// One native image a compiled assembly P/Invokes into by <see cref="Library"/>: its binaries, one per
    /// xcframework slice (or framework), and whether the gate also holds its loads to P1.
    /// </summary>
    sealed record OsFloorImage(string Library, string Label, IReadOnlyList<AbsolutePath> Binaries, bool CheckLoads);

    static IReadOnlyList<AbsolutePath> XcframeworkBinaries(AbsolutePath xcframework, string module) =>
        Directory.Exists(xcframework)
            ? Directory.GetDirectories(xcframework)
                .Select(slice => (AbsolutePath)Path.Combine(slice, $"{module}.framework", module))
                .Where(b => File.Exists(b))
                .OrderBy(b => b.ToString(), StringComparer.Ordinal)
                .ToList()
            : new List<AbsolutePath>();

    /// <summary>
    /// Reads every slice of both BindingTests wrappers (and the bridge image) back and requires every
    /// OS-resident dylib load to be weak (P1). Then P2, from the artifacts alone: every import of a symbol
    /// the inventory's OS lacks must be weak and reached from an export (read from the arm64 code), and
    /// every managed method holding a P/Invoke of such an export, or a direct P/Invoke of such a symbol,
    /// must throw PlatformNotSupportedException at entry on that OS (read from the compiled IL). A
    /// negative control compiles a guarded and an unguarded caller of a real floored export and requires
    /// the check to flag exactly the unguarded one. Invoked from the --compile-only path once both
    /// wrappers are built.
    /// </summary>
    void RunOsLoadSafetyGate()
    {
        Log.Information("=========================================");
        Log.Information(" OS load-safety gate");
        Log.Information("=========================================");

        var depWrapper = $"{DepModuleName}SwiftBindings";
        var bridgeBinaries = new[] { BindingTestsDir / "SwiftBridge" / $"{BridgeModule}.framework" / BridgeModule,
                                     BindingTestsDir / "SwiftBridge" / "device" / $"{BridgeModule}.framework" / BridgeModule }
            .Where(b => File.Exists(b)).ToList();
        var images = new[]
        {
            new OsFloorImage(WrapperModule, WrapperModule, XcframeworkBinaries(BtOutputDir / $"{WrapperModule}.xcframework", WrapperModule), CheckLoads: true),
            new OsFloorImage(depWrapper, depWrapper, XcframeworkBinaries(BtOutputDir / $"{depWrapper}.xcframework", depWrapper), CheckLoads: true),
            new OsFloorImage(BridgeModule, BridgeModule, bridgeBinaries, CheckLoads: false),
        };
        var appleImages = new[]
        {
            // SwiftBindings.Apple.dll is judged for iOS, so its wrapper's iOS slices are the ones read.
            new OsFloorImage("SBApple", "SBApple", XcframeworkBinaries(RootDirectory / "src" / "Swift.Bindings.Apple" / "native" / "SBApple.xcframework", "SBApple")
                .Where(b => OsLoadSafetyAudit.SliceVariant(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(b)))!) is "ios" or "ios-simulator").ToList(), CheckLoads: false),
        };

        var failures = new List<string>();
        var inventory = ShippedOsSymbolInventory;
        if (!File.Exists(DependencyTestInventoryPath))
            throw new Exception($"OS load-safety gate: {DependencyTestInventoryPath} not found; the regen did not write the dependency's inventory.");
        var supplied = OsSymbolInventory.Load(DependencyTestInventoryPath, inventory).Providers;

        // Positive control on generation: the dependency's binding records the floors its supplied
        // inventory implies. The gate takes nothing else from the reports.
        var dependencyReport = BtOutputDir / "withdrawal-evidence" / DepModuleName / "binding-report.json";
        if (!File.Exists(dependencyReport))
            throw new Exception($"OS load-safety gate: {dependencyReport} not found.");
        var dependencyRaised = OsLoadSafetyAudit.ReadRaisedDeclarations(File.ReadAllText(dependencyReport));
        foreach (var declaration in DependencyInventoryFlooredDeclarations)
        {
            if (!dependencyRaised.Any(d => d.EndsWith(declaration, StringComparison.Ordinal)))
                failures.Add($"{DepModuleName}: its binding report records no inferred floor for {declaration}, which its supplied inventory omits.");
        }

        // P1 and the weak-import half of P2, slice by slice; then which exports reach each absent import.
        int coveredImports = 0;
        var suppliedAbsentReached = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, List<AbsentSymbol>>> ReadImages(IEnumerable<OsFloorImage> set)
        {
            var reach = new Dictionary<string, Dictionary<string, List<AbsentSymbol>>>(StringComparer.Ordinal);
            foreach (var image in set)
            {
                var byExport = reach[image.Library] = new Dictionary<string, List<AbsentSymbol>>(StringComparer.Ordinal);
                if (image.Binaries.Count == 0)
                {
                    failures.Add($"{image.Label}: no binary found to read.");
                    continue;
                }
                foreach (var binary in image.Binaries)
                {
                    var loads = ReadDylibLoads(binary);
                    var slice = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(binary)))!;
                    var imports = ReadDylibImports(binary);
                    var label = $"{image.Label} {slice}";
                    if (image.CheckLoads)
                    {
                        var verdict = OsLoadSafetyAudit.EvaluateLoads(image.Label, binary, loads, imports, SdkPathOf(binary), inventory);
                        failures.AddRange(verdict.Failures);
                        Log.Information("{Label}: {Os} OS-resident load(s), {Weak} weak, {Kept} kept strong by the linker with every import weak ({KeptLoads})",
                            label, verdict.OsResidentLoadCount, verdict.WeakOsResidentLoadCount, verdict.SafeStrongLoads.Count,
                            string.Join(", ", verdict.SafeStrongLoads.Select(OsFrameworkWeakLinking.ImportLeaf)));
                    }
                    var platform = PlatformOfBinary(binary, inventory, out var platformProblem);
                    if (platform is null)
                    {
                        failures.Add($"{label}: {platformProblem}");
                        continue;
                    }
                    if (imports.Count == 0)
                        failures.Add($"{label}: dyld_info read no imports.");
                    var variants = new[] { platform };
                    var importVerdict = OsLoadSafetyAudit.EvaluateImports(label, imports, loads,
                        library => OsLoadSafetyAudit.Resolve(library, supplied, symbol => inventory.ShipsOnAll(variants, symbol), shippedOsVersion: platform.OsVersion));
                    failures.AddRange(importVerdict.Failures);
                    coveredImports += importVerdict.CoveredSwiftImports;
                    var archs = imports.Select(i => i.Arch).Distinct(StringComparer.Ordinal).ToList();
                    if (!archs.Contains("arm64"))
                    {
                        if (importVerdict.Absent.Count > 0)
                            failures.Add($"{label}: imports {importVerdict.Absent.Count} symbol(s) the inventory's OS lacks and has no arm64 code to attribute them with.");
                        continue;
                    }
                    var reachRead = ReadArm64Reach(binary);
                    var attributed = OsLoadSafetyAudit.AttributeAbsentImports(label, importVerdict.Absent, reachRead);
                    failures.AddRange(attributed.Failures);
                    foreach (var (export, absent) in attributed.ByExport)
                    {
                        if (!byExport.TryGetValue(export, out var list))
                            byExport[export] = list = new List<AbsentSymbol>();
                        list.AddRange(absent.Where(a => !list.Contains(a)));
                    }
                    suppliedAbsentReached.UnionWith(attributed.ByExport.Values.SelectMany(v => v).Select(a => a.Symbol));
                    Log.Information("{Label}: {Covered} covered Swift import(s), {Absent} absent from the inventory's OS (all weak); " +
                                    "arm64 reach: {Nodes} node(s), {Exports} export(s), {Reaching} reaching an absent import, {Unresolved} unresolved reference(s)",
                        label, importVerdict.CoveredSwiftImports, importVerdict.Absent.Count,
                        reachRead.NodeCount, reachRead.Exports.Count, attributed.ByExport.Count, reachRead.UnresolvedReferences);
                }
            }
            return reach;
        }
        var bindingReach = ReadImages(images);
        var appleReach = ReadImages(appleImages);

        // The refusal half of P2, from the compiled IL: both BindingTests modules compile into
        // CompileCheck.dll, and the Apple supplement's P/Invokes are held to the shipped inventory.
        var iosVariants = inventory.Platforms.Where(p => p.Variant is "ios" or "ios-simulator").ToList();
        var iosOsVersion = iosVariants.Select(p => p.OsVersion).Distinct().SingleOrDefault()
            ?? throw new Exception("OS load-safety gate: the shipped inventory's iOS variants disagree on their OS version.");
        // A system-framework binding names its library bare ("CryptoKit"), which the runtime's resolver
        // maps to /System/Library/Frameworks.
        var simulatorFrameworks = (AbsolutePath)SdkPath("iphonesimulator") / "System" / "Library" / "Frameworks";
        bool IsOsFramework(string name) => Directory.Exists(simulatorFrameworks / $"{name}.framework");
        CoveredProvider? Direct(string library) => OsLoadSafetyAudit.Resolve(
            library, supplied, symbol => inventory.ShipsOnAll(iosVariants, symbol), IsOsFramework, iosOsVersion);

        OsFloorHazard? HazardOf(Dictionary<string, Dictionary<string, List<AbsentSymbol>>> reach, string library, string entryPoint)
        {
            List<AbsentSymbol> absent;
            string how;
            if (reach.TryGetValue(library, out var byExport))
            {
                if (!byExport.TryGetValue("_" + entryPoint.TrimStart('_'), out var reached) || reached.Count == 0)
                    return null;
                absent = reached;
                how = "reaches " + string.Join(", ", reached.Select(a => a.Symbol).Distinct().Take(3));
            }
            else if (OsLoadSafetyAudit.DirectAbsentSymbol(library, entryPoint, Direct) is { } direct)
            {
                absent = new List<AbsentSymbol> { direct };
                how = $"{direct.InstallName} lacks it";
            }
            else
                return null;
            var points = absent.Select(a => a.OsVersion ?? iosOsVersion).Distinct(StringComparer.Ordinal)
                .Select(v => new OsFloorPoint("ios", System.Version.Parse(v.Contains('.') ? v : v + ".0"))).ToList();
            return new OsFloorHazard(library, entryPoint, points, how);
        }

        OsFloorGuardResult? CompileCheckResult = null;
        foreach (var (name, reach) in new[] { ("CompileCheck.dll", bindingReach), ("SwiftBindings.Apple.dll", appleReach) })
        {
            var assembly = CompileCheckOutputDir / name;
            if (!File.Exists(assembly))
            {
                failures.Add($"{name}: not found under {CompileCheckOutputDir}.");
                continue;
            }
            var result = OsFloorGuardCheck.Check(assembly, (library, entryPoint) => HazardOf(reach, library, entryPoint));
            failures.AddRange(result.Errors.Select(e => $"{name}: {e}"));
            failures.AddRange(result.Violations.Select(v => $"{name}: [{v.Rule}] {v.Member}: {v.Reason}"));
            Log.Information("{Assembly}: {PInvokes} P/Invoke(s), {Hazards} reaching a Swift symbol the inventory's OS lacks " +
                            "({Wrapper} through a wrapper export), {Leaves} leaf method(s): {Refusing} refuse at entry, {Covered} private and covered by refusing callers",
                name, result.PInvokes, result.Hazards.Length, result.Hazards.Count(h => reach.ContainsKey(h.Hazard.Library)),
                result.Hazards.Sum(h => h.Leaves.Length), result.RefusingLeaves, result.CoveredPrivateLeaves);
            if (name == "CompileCheck.dll")
                CompileCheckResult = result;
        }

        if (coveredImports == 0)
            failures.Add("No wrapper slice imports a Swift symbol from a covered provider, so the import check read nothing.");
        var suppliedProvider = supplied.FirstOrDefault(p => p.ModuleName == DepModuleName);
        // Only a wrapper import proves the weak-import path; a direct P/Invoke bypasses it.
        if (suppliedProvider is null || !suppliedAbsentReached.Any(s => !suppliedProvider.Symbols.Contains(s.TrimStart('_'))))
            failures.Add($"Positive control: no wrapper export reaches a {DepModuleName} symbol its supplied inventory omits.");
        var wrapperHazard = CompileCheckResult?.Hazards.FirstOrDefault(h => bindingReach.ContainsKey(h.Hazard.Library) && h.Leaves.Length > 0);
        if (CompileCheckResult is not null && wrapperHazard is null)
            failures.Add("Positive control: no method of CompileCheck.dll calls a wrapper export that reaches a symbol the inventory's OS lacks, so the refusal check judged none.");
        if (wrapperHazard is not null)
            failures.AddRange(RunOsFloorNegativeControl(wrapperHazard.Hazard));

        if (failures.Count > 0)
        {
            foreach (var f in failures)
                Log.Error("  {Failure}", f);
            throw new Exception(
                $"OS load-safety gate: {failures.Count} failure(s). A strong load of an OS framework makes dyld refuse " +
                "the whole wrapper, and the app with it, on an OS that lacks one of the symbols it imports; a managed path " +
                "that reaches such a symbol without refusing first calls a null import — see the lines above.");
        }

        Log.Information("OS load-safety gate: PASSED");
    }

    /// <summary>The inventory platform of <paramref name="binary"/>'s xcframework slice (or, for a bare framework, its build platform).</summary>
    static OsSymbolInventory.Platform? PlatformOfBinary(AbsolutePath binary, OsSymbolInventory inventory, out string problem)
    {
        var slice = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(binary)))!;
        var variant = slice.Contains('-') ? OsLoadSafetyAudit.SliceVariant(slice)
            : slice == "device" ? "ios" : "ios-simulator";
        var platform = inventory.Platforms.FirstOrDefault(p => p.Variant == variant);
        problem = platform is null ? $"the shipped inventory has no '{variant}' variant." : "";
        return platform;
    }

    /// <summary>
    /// The gate's self-test: compiles, in memory, an assembly that P/Invokes <paramref name="hazard"/>'s
    /// export from one method that refuses first (at the inventory's floor for that OS) and one that does
    /// not, and requires the refusal check to flag exactly the second. A check that stopped reading
    /// guards, or stopped finding the export's reach, fails here.
    /// </summary>
    static IEnumerable<string> RunOsFloorNegativeControl(OsFloorHazard hazard)
    {
        var floor = hazard.RefuseOn.Max(p => p.Version)!;
        var source = $$"""
            using System;
            using System.Runtime.InteropServices;
            public static class OsFloorNegativeControl
            {
                [DllImport("{{hazard.Library}}", EntryPoint = "{{hazard.EntryPoint}}")] private static extern void Floored();
                public static void Guarded()
                {
                    if (OperatingSystem.IsOSPlatform("ios") && !OperatingSystem.IsOSPlatformVersionAtLeast("ios", {{floor.Major}}, {{floor.Minor + 1}}, 0, 0))
                        throw new PlatformNotSupportedException("control");
                    Floored();
                }
                public static void Unguarded() => Floored();
            }
            """;
        var frameworkDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p) && p.StartsWith(frameworkDir, StringComparison.Ordinal))
            .Select(p => (Microsoft.CodeAnalysis.MetadataReference)Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p));
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("OsFloorNegativeControl",
            new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source) }, references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        if (!emitted.Success)
        {
            yield return "Negative control: the control assembly does not compile: " +
                         string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
            yield break;
        }
        var result = OsFloorGuardCheck.Check(stream.ToArray(), (library, entryPoint) =>
            library == hazard.Library && entryPoint == hazard.EntryPoint ? hazard : null);
        var flagged = result.Violations.Select(v => v.Member).Distinct().ToList();
        if (flagged.Count != 1 || flagged[0] != "OsFloorNegativeControl::Unguarded" || result.Violations.Any(v => v.Rule != OsFloorGuardCheck.UnguardedRule))
            yield return $"Negative control: a guarded and an unguarded caller of {hazard.EntryPoint} in {hazard.Library} must flag exactly " +
                         $"OsFloorNegativeControl::Unguarded, and the check flagged [{string.Join(", ", result.Violations.Select(v => $"{v.Rule} {v.Member}"))}].";
        else
            Log.Information("Negative control: an unguarded caller of {Export} ({Library}) fails the refusal check; its guarded sibling passes", hazard.EntryPoint, hazard.Library);
    }
}
