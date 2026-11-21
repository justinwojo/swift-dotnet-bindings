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
// It then reads every slice's imports (`dyld_info -imports`) and the compiled assembly's direct
// P/Invokes: a Swift symbol a covered provider (an OS-resident dylib, by install name, or a supplied
// provider) does not ship must be imported weakly and belong to a declaration a binding report records
// as floored above the inventory's OS. A positive control requires the dependency's absent fixture
// members to reach the check, floored. Fail-closed and unconditional, with no `--permissive` arm: a
// soundness property, not a baseline.

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
    };

    /// <summary>The fixture declarations the dependency's binding report must record as raised.</summary>
    static readonly string[] DependencyInventoryFlooredDeclarations =
    {
        "InventoryProbe.absentOnInventoryOs", "InventoryProbe.absentMethodOnInventoryOs", "InventoryAbsentValue",
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

    /// <summary>Every P/Invoke of <paramref name="assembly"/>: library, entry point and declaring member.</summary>
    static List<(string Library, string EntryPoint, string Member)> ReadPInvokes(AbsolutePath assembly)
    {
        var result = new List<(string, string, string)>();
        using var stream = File.OpenRead(assembly);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (!pe.HasMetadata)
            return result;
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if ((method.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) == 0)
                continue;
            var import = method.GetImport();
            if (import.Module.IsNil)
                continue;
            var library = md.GetString(md.GetModuleReference(import.Module).Name);
            var name = md.GetString(method.Name);
            var entryPoint = import.Name.IsNil ? name : md.GetString(import.Name);
            var type = md.GetTypeDefinition(method.GetDeclaringType());
            result.Add((library, string.IsNullOrEmpty(entryPoint) ? name : entryPoint, $"{md.GetString(type.Name)}.{name}"));
        }
        return result;
    }

    /// <summary>
    /// Reads every slice of both BindingTests wrappers back and requires every OS-resident dylib load to
    /// be weak, then holds every import and direct P/Invoke of a symbol the inventory's OS lacks to a
    /// floored declaration. Invoked from the --compile-only path once both wrappers are built.
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
        var inventory = ShippedOsSymbolInventory;
        if (!File.Exists(DependencyTestInventoryPath))
            throw new Exception($"OS load-safety gate: {DependencyTestInventoryPath} not found; the regen did not write the dependency's inventory.");
        var supplied = OsSymbolInventory.Load(DependencyTestInventoryPath, inventory).Providers;
        // Each wrapper answers to its own binding's floors; CompileCheck.dll compiles both bindings.
        var guardedByLeg = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var dependencyRaised = new List<string>();
        foreach (var (leg, report, isDependency) in new[]
                 {
                     (WrapperModule, BtOutputDir / "binding-report.json", false),
                     (depWrapper, BtOutputDir / "withdrawal-evidence" / DepModuleName / "binding-report.json", true),
                 })
        {
            if (!File.Exists(report))
                throw new Exception($"OS load-safety gate: {report} not found.");
            var (symbols, raised) = OsLoadSafetyAudit.ReadGuardedSymbols(File.ReadAllText(report));
            guardedByLeg[leg] = symbols;
            if (isDependency)
                dependencyRaised.AddRange(raised);
        }
        var guarded = new HashSet<string>(guardedByLeg.Values.SelectMany(g => g), StringComparer.Ordinal);
        foreach (var declaration in DependencyInventoryFlooredDeclarations)
        {
            if (!dependencyRaised.Any(d => d.EndsWith(declaration, StringComparison.Ordinal)))
                failures.Add($"{DepModuleName}: its binding report records no inferred floor for {declaration}, which its supplied inventory omits.");
        }
        var suppliedAbsentReached = new HashSet<string>(StringComparer.Ordinal);
        int coveredImports = 0;

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
                var loads = ReadDylibLoads(binary);
                var slice = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(binary)))!;
                var imports = ReadDylibImports(binary);
                var verdict = OsLoadSafetyAudit.EvaluateLoads(leg, binary, loads, imports, SdkPathOf(binary), inventory);
                failures.AddRange(verdict.Failures);

                var variant = OsLoadSafetyAudit.SliceVariant(slice);
                var platform = inventory.Platforms.FirstOrDefault(p => p.Variant == variant);
                if (platform is null)
                {
                    failures.Add($"{leg} {slice}: the shipped inventory has no '{variant}' variant.");
                    continue;
                }
                var variants = new[] { platform };
                if (imports.Count == 0)
                    failures.Add($"{leg} {slice}: dyld_info read no imports.");
                var importVerdict = OsLoadSafetyAudit.EvaluateImports(
                    $"{leg} {slice}", imports, loads,
                    library => OsLoadSafetyAudit.Resolve(library, supplied, symbol => inventory.ShipsOnAll(variants, symbol)),
                    guardedByLeg[leg]);
                failures.AddRange(importVerdict.Failures);
                coveredImports += importVerdict.CoveredSwiftImports;
                suppliedAbsentReached.UnionWith(importVerdict.GuardedAbsent);
                Log.Information("{Leg} {Slice}: {Os} OS-resident load(s), {Weak} weak, {Kept} kept strong by the linker with every import weak ({KeptLoads}); {Covered} covered Swift import(s), {Absent} absent from the inventory's OS, all guarded weak",
                    leg, slice, verdict.OsResidentLoadCount, verdict.WeakOsResidentLoadCount, verdict.SafeStrongLoads.Count,
                    string.Join(", ", verdict.SafeStrongLoads.Select(OsFrameworkWeakLinking.ImportLeaf)),
                    importVerdict.CoveredSwiftImports, importVerdict.GuardedAbsent.Count);
            }
        }

        // Direct P/Invokes bypass the wrapper's weak imports: a call into a symbol the OS lacks must be
        // refused by the binding's guard before it binds. Both BindingTests modules compile into
        // CompileCheck.dll; the Apple supplement's direct calls are held to the shipped inventory.
        var iosVariants = inventory.Platforms.Where(p => p.Variant is "ios" or "ios-simulator").ToList();
        // A system-framework binding names its library bare ("CryptoKit"), which the runtime's resolver
        // maps to /System/Library/Frameworks.
        var simulatorFrameworks = (AbsolutePath)SdkPath("iphonesimulator") / "System" / "Library" / "Frameworks";
        bool IsOsFramework(string name) => Directory.Exists(simulatorFrameworks / $"{name}.framework");
        foreach (var (name, assemblyGuarded) in new[]
                 {
                     ("CompileCheck.dll", (IReadOnlySet<string>)guarded),
                     ("SwiftBindings.Apple.dll", new HashSet<string>(StringComparer.Ordinal)),
                 })
        {
            var assembly = CompileCheckOutputDir / name;
            if (!File.Exists(assembly))
            {
                failures.Add($"{name}: not found under {CompileCheckOutputDir}.");
                continue;
            }
            var pinvokeVerdict = OsLoadSafetyAudit.EvaluatePInvokes(
                name, ReadPInvokes(assembly),
                library => OsLoadSafetyAudit.Resolve(library, supplied, symbol => inventory.ShipsOnAll(iosVariants, symbol), IsOsFramework),
                assemblyGuarded);
            failures.AddRange(pinvokeVerdict.Failures);
            Log.Information("{Assembly}: {Covered} direct Swift P/Invoke(s) into covered providers, {Absent} absent from the inventory's OS, all guarded",
                name, pinvokeVerdict.CoveredSwiftImports, pinvokeVerdict.GuardedAbsent.Count);
        }

        if (coveredImports == 0)
            failures.Add("No wrapper slice imports a Swift symbol from a covered provider, so the import check read nothing.");
        var suppliedProvider = supplied.FirstOrDefault(p => p.ModuleName == DepModuleName);
        // Only a wrapper import proves the weak-import path; a direct P/Invoke bypasses it.
        if (suppliedProvider is null || !suppliedAbsentReached.Any(s => !suppliedProvider.Symbols.Contains(s)))
            failures.Add($"Positive control: no wrapper import of a {DepModuleName} symbol its supplied inventory omits reached the check.");

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
