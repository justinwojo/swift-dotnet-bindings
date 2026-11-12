// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging.Abstractions;

namespace BindingsGeneration.Tests;

/// <summary>A target the oracle compiles for: an LLVM triple and the SDK it builds against.</summary>
public sealed record SwiftOracleTarget(string Triple, string Sdk)
{
    public static readonly SwiftOracleTarget DeviceArm64 = new("arm64-apple-ios15.0", "iphoneos");
    public static readonly SwiftOracleTarget SimulatorArm64 = new("arm64-apple-ios15.0-simulator", "iphonesimulator");
    public static readonly SwiftOracleTarget SimulatorX64 = new("x86_64-apple-ios15.0-simulator", "iphonesimulator");

    public static readonly ImmutableArray<SwiftOracleTarget> All = ImmutableArray.Create(DeviceArm64, SimulatorArm64, SimulatorX64);

    public static SwiftOracleTarget ByTriple(string triple) => All.Single(t => t.Triple == triple);

    public override string ToString() => Triple;
}

/// <summary>A module compiled by swiftc: its ABI descriptor and its LLVM IR.</summary>
/// <param name="TbdPath">The exported symbols, which the generator reads from the library's text stub.</param>
public sealed record SwiftOracleModule(string Name, string AbiJsonPath, string TbdPath, SwiftIrModule Ir);

public enum SwiftOracleOutcome
{
    /// <summary>The predicted signature is the one swiftc defined.</summary>
    Match,

    /// <summary>The predicted signature differs from the one swiftc defined: a lowering defect.</summary>
    Mismatch,

    /// <summary>The predictor refused, with a reason.</summary>
    Refused,

    /// <summary>swiftc defined no function under the callee's symbol (nothing to compare).</summary>
    NotDefined,
}

/// <summary>One callee's prediction against swiftc.</summary>
public sealed record SwiftOracleEntry(
    SwiftCallFacts Facts,
    SwiftOracleOutcome Outcome,
    string? Predicted,
    SwiftIrFunction? Actual,
    string? Refusal,
    SwiftSelfPassing? Self)
{
    public string Symbol => Facts.Target.Symbol ?? Facts.Id;

    public override string ToString() => Outcome switch
    {
        SwiftOracleOutcome.Match => $"match     {Symbol}: {Predicted}",
        SwiftOracleOutcome.Mismatch => $"MISMATCH  {Symbol}\n    predicted {Predicted}\n    swiftc    {Actual!.CallingConvention} {Actual.Signature} ({Actual.Symbol})",
        SwiftOracleOutcome.Refused => $"refused   {Symbol}: {Refusal}" + (Actual is null ? "" : $" [swiftc {Actual.CallingConvention} {Actual.Signature}]"),
        _ => $"undefined {Symbol}",
    };
}

/// <summary>
/// A carrier type the facts builder cannot resolve (no type database entry) lowered straight through
/// the layout resolver, against the function swiftc defined to take and return it.
/// </summary>
public sealed record SwiftOracleCarrierEntry(string Symbol, string Predicted, SwiftIrFunction Actual)
{
    public bool Agrees => Actual.CallingConvention == "swiftcc" && Actual.Signature == Predicted;

    public override string ToString() => Agrees
        ? $"carrier ok  {Symbol}: {Predicted}"
        : $"CARRIER     {Symbol}\n    predicted {Predicted}\n    swiftc    {Actual.CallingConvention} {Actual.Signature}";
}

/// <summary>One frozen struct's resolved layout against the IR type swiftc gave it.</summary>
public sealed record SwiftOracleLayoutEntry(string TypeName, SwiftLeafLayout? Predicted, SwiftIrLayout? Actual, string? Mismatch)
{
    public override string ToString() => Mismatch is null
        ? $"layout ok   {TypeName}: {Predicted}"
        : $"LAYOUT      {TypeName}: {Mismatch}\n    predicted {Predicted}\n    swiftc    {Actual}";
}

public sealed record SwiftOracleReport(
    string ModuleName,
    string Triple,
    ImmutableArray<SwiftOracleEntry> Entries,
    ImmutableArray<SwiftOracleLayoutEntry> Layouts,
    ImmutableArray<SwiftOracleCarrierEntry> Carriers)
{
    public IEnumerable<SwiftOracleEntry> With(SwiftOracleOutcome outcome) => Entries.Where(e => e.Outcome == outcome);

    public int Count(SwiftOracleOutcome outcome) => With(outcome).Count();

    public IEnumerable<SwiftOracleLayoutEntry> LayoutMismatches => Layouts.Where(l => l.Mismatch is not null);

    public IEnumerable<SwiftOracleCarrierEntry> CarrierMismatches => Carriers.Where(c => !c.Agrees);

    public string Summary =>
        $"{ModuleName} {Triple}: {Count(SwiftOracleOutcome.Match)} match, {Count(SwiftOracleOutcome.Mismatch)} mismatch, " +
        $"{Count(SwiftOracleOutcome.Refused)} refused, {Count(SwiftOracleOutcome.NotDefined)} not defined; " +
        $"layouts {Layouts.Length - LayoutMismatches.Count()}/{Layouts.Length} agree; " +
        $"carriers {Carriers.Length - CarrierMismatches.Count()}/{Carriers.Length} agree";

    public string Describe(bool includeMatches = false)
    {
        var sb = new StringBuilder(Summary).AppendLine();
        foreach (var entry in Entries.Where(e => includeMatches || e.Outcome != SwiftOracleOutcome.Match))
            sb.AppendLine(entry.ToString());
        foreach (var layout in Layouts.Where(l => includeMatches || l.Mismatch is not null))
            sb.AppendLine(layout.ToString());
        foreach (var carrier in Carriers.Where(c => includeMatches || !c.Agrees))
            sb.AppendLine(carrier.ToString());
        return sb.ToString();
    }
}

/// <summary>
/// The swiftc lowering oracle: compiles Swift sources with swiftc for a target, reads back the ABI
/// descriptor through the generator's own parser and facts builder, predicts every callee's physical
/// signature with <see cref="SwiftPhysicalSignaturePredictor"/> and the layout lowering, and compares
/// each prediction with the function swiftc defined in its IR. It also compares every frozen
/// struct's resolved layout with the IR type swiftc stores it as.
/// </summary>
public static class SwiftLoweringOracle
{
    private static readonly Lazy<string?> XcrunPath = new(() =>
        File.Exists("/usr/bin/xcrun") ? "/usr/bin/xcrun" : null);

    public static bool IsAvailable => OperatingSystem.IsMacOS() && XcrunPath.Value is not null && SdkPath("iphonesimulator") is not null;

    /// <summary>The toolchain that produced the IR: <c>swiftc --version</c>'s first line.</summary>
    public static string ToolchainVersion => _toolchain.Value;

    private static readonly Lazy<string> _toolchain = new(() =>
        Run(XcrunPath.Value!, new[] { "swiftc", "--version" }, Directory.GetCurrentDirectory()).Output
            .Split('\n').FirstOrDefault(l => l.Contains("Swift version", StringComparison.Ordinal))?.Trim() ?? "unknown");

    private static readonly Dictionary<string, string?> _sdkPaths = new(StringComparer.Ordinal);

    public static string? SdkPath(string sdk)
    {
        lock (_sdkPaths)
        {
            if (_sdkPaths.TryGetValue(sdk, out var cached))
                return cached;
            string? path = null;
            if (XcrunPath.Value is { } xcrun)
            {
                var result = Run(xcrun, new[] { "--sdk", sdk, "--show-sdk-path" }, Directory.GetCurrentDirectory());
                path = result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } p && Directory.Exists(p) ? p : null;
            }
            _sdkPaths[sdk] = path;
            return path;
        }
    }

    /// <summary>
    /// The directory of a module's public textual interface, alone: what a binary framework gives a
    /// consumer, and so what a generated wrapper compiles against (no SPI, no accessor-level
    /// availability).
    /// </summary>
    public static string InterfaceDirectory(string outputDirectory) => Path.Combine(outputDirectory, "interface");

    /// <summary>
    /// Compiles <paramref name="sources"/> into one library-evolution module and its IR, and writes
    /// its public interface under <see cref="InterfaceDirectory"/>.
    /// </summary>
    public static SwiftOracleModule Compile(
        string moduleName, IEnumerable<string> sources, SwiftOracleTarget target, string outputDirectory,
        IEnumerable<string>? extraArguments = null)
    {
        Directory.CreateDirectory(InterfaceDirectory(outputDirectory));
        var sdk = SdkPath(target.Sdk) ?? throw new InvalidOperationException($"no {target.Sdk} SDK");
        var modulePath = Path.Combine(outputDirectory, moduleName + ".swiftmodule");
        var irPath = Path.Combine(outputDirectory, moduleName + ".ll");
        var tbdPath = Path.Combine(outputDirectory, moduleName + ".tbd");
        var arguments = new List<string>
        {
            "swiftc", "-swift-version", "5", "-wmo", "-parse-as-library", "-enable-library-evolution",
            "-target", target.Triple, "-sdk", sdk, "-module-name", moduleName,
            "-emit-module", "-emit-module-path", modulePath, "-emit-ir", "-o", irPath,
            "-emit-tbd", "-emit-tbd-path", tbdPath, "-Xfrontend", "-tbd-install_name", "-Xfrontend", $"@rpath/{moduleName}.framework/{moduleName}",
            // The private interface (SPI included) goes beside the module, never into the public
            // interface's directory, where an importer would prefer it.
            "-emit-module-interface-path", Path.Combine(InterfaceDirectory(outputDirectory), moduleName + ".swiftinterface"),
            "-emit-private-module-interface-path", Path.Combine(outputDirectory, moduleName + ".private.swiftinterface"),
        };
        if (extraArguments is not null)
            arguments.AddRange(extraArguments);
        arguments.AddRange(sources);
        var result = Run(XcrunPath.Value!, arguments, outputDirectory);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"swiftc failed for {moduleName} ({target}):\n{result.Output}");
        var abiJson = Path.Combine(outputDirectory, moduleName + ".abi.json");
        if (!File.Exists(abiJson))
            throw new InvalidOperationException($"swiftc wrote no ABI descriptor for {moduleName} ({target})");
        return new SwiftOracleModule(moduleName, abiJson, tbdPath, SwiftIrModule.Parse(File.ReadAllText(irPath)));
    }

    /// <summary>
    /// Compiles <paramref name="sources"/> to LLVM IR only, the way the generator compiles a wrapper
    /// (no library evolution, minimal concurrency checking, imports through public interfaces): for
    /// the signatures of the functions it defines and of the functions it calls in other modules.
    /// </summary>
    public static SwiftIrModule CompileIr(
        string moduleName, IEnumerable<string> sources, SwiftOracleTarget target, string outputDirectory,
        IEnumerable<string>? extraArguments = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var sdk = SdkPath(target.Sdk) ?? throw new InvalidOperationException($"no {target.Sdk} SDK");
        var irPath = Path.Combine(outputDirectory, moduleName + ".ll");
        var arguments = new List<string>
        {
            "swiftc", "-wmo", "-parse-as-library", "-strict-concurrency=minimal",
            "-target", target.Triple, "-sdk", sdk, "-module-name", moduleName, "-emit-ir", "-o", irPath,
            "-module-cache-path", Path.Combine(outputDirectory, "module-cache"),
        };
        if (extraArguments is not null)
            arguments.AddRange(extraArguments);
        arguments.AddRange(sources);
        var result = Run(XcrunPath.Value!, arguments, outputDirectory);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"swiftc failed for {moduleName} ({target}):\n{result.Output}");
        return SwiftIrModule.Parse(File.ReadAllText(irPath));
    }

    /// <summary>
    /// Parses <paramref name="module"/>'s ABI descriptor the way the generator does (after any
    /// <paramref name="dependencies"/>, whose declarations the layout resolver also sees), and
    /// compares every callee's prediction and every frozen struct's layout with swiftc's IR.
    /// </summary>
    public static SwiftOracleReport Compare(SwiftOracleModule module, string triple, IEnumerable<SwiftOracleModule>? dependencies = null)
    {
        var db = LoadTypeDatabase();
        var decls = new List<ModuleDecl>();
        foreach (var dependency in dependencies ?? Array.Empty<SwiftOracleModule>())
            decls.Add(ParseModule(dependency, db));
        var decl = ParseModule(module, db);
        // As the generator does: a dependency's protocols are what the module's own refine.
        decl.DependencyProtocols = decls.ToDictionary(d => d.Name, d => d.Protocols.ToList(), StringComparer.Ordinal);
        decls.Add(decl);

        var table = SwiftCallFactsTable.Build(decl, db);
        var lowering = new SwiftLayoutValueLowering(decls, db);
        var entries = ImmutableArray.CreateBuilder<SwiftOracleEntry>();
        var carriers = ImmutableArray.CreateBuilder<SwiftOracleCarrierEntry>();
        foreach (var facts in table.All)
        {
            var entry = Judge(facts, module.Ir, lowering);
            entries.Add(entry);
            if (entry is { Outcome: SwiftOracleOutcome.Refused, Actual: { } actual } && CarrierSignature(facts, lowering) is { } carrier)
                carriers.Add(new SwiftOracleCarrierEntry(entry.Symbol, carrier, actual));
        }

        var layouts = ImmutableArray.CreateBuilder<SwiftOracleLayoutEntry>();
        foreach (var type in AllTypes(decl.Types))
        {
            if (type is not StructDecl { IsFrozen: true } s || s.GenericParameters.Count > 0 || !s.MangledName.StartsWith("$s", StringComparison.Ordinal))
                continue;
            var irName = "%T" + s.MangledName[2..];
            var predicted = lowering.Resolver.Resolve(new NamedTypeSpec(s.SwiftTypeName.ModuleQualifiedName));
            SwiftIrLayout? actual;
            try
            {
                actual = module.Ir.LayoutOf(irName);
            }
            catch (FormatException ex)
            {
                // A layout swiftc declares but this reader cannot read is never a silent agreement.
                if (!predicted.IsIndirect)
                {
                    var why = predicted.Layout is null ? $"no layout: {predicted.Reason}" : $"swiftc's IR type {irName} does not parse: {ex.Message}";
                    layouts.Add(new SwiftOracleLayoutEntry(s.SwiftTypeName.ModuleQualifiedName, predicted.Layout, null, why));
                }
                continue;
            }
            if (actual is null || predicted.Layout is null)
            {
                // Either swiftc never needed the type's IR struct, or the resolver gives it no inline
                // layout (it holds a resilient field): only a type both sides describe is compared.
                if (actual is not null && !predicted.IsIndirect)
                    layouts.Add(new SwiftOracleLayoutEntry(s.SwiftTypeName.ModuleQualifiedName, null, actual, $"no layout: {predicted.Reason}"));
                continue;
            }
            layouts.Add(new SwiftOracleLayoutEntry(s.SwiftTypeName.ModuleQualifiedName, predicted.Layout, actual, CompareLayout(predicted.Layout, actual)));
        }
        return new SwiftOracleReport(decl.Name, triple, entries.ToImmutable(), layouts.ToImmutable(), carriers.ToImmutable());
    }

    /// <summary>
    /// For a plain free function whose facts are incomplete only because a parameter or result type
    /// has no type database entry, the signature its types lower to through the layout resolver
    /// alone; null for any other callee. This covers the carrier shapes the resolver knows but the
    /// facts builder cannot categorize.
    /// </summary>
    private static string? CarrierSignature(SwiftCallFacts facts, SwiftLayoutValueLowering lowering)
    {
        if (facts.IsComplete || !facts.Unknowns.All(u => u.Contains("is not known to the module or the type database", StringComparison.Ordinal)))
            return null;
        if (facts.Target.Kind != SwiftCallTargetKind.FreeFunction || facts.Self.Kind != SwiftCallSelfKind.None
            || facts.Effects.Throws || facts.Effects.IsAsync || !facts.Generics.IsEmpty)
            return null;
        var arguments = new List<string>();
        string result = "void";
        if (!facts.Result.IsVoid)
        {
            var answer = lowering.Lower(facts.Result.Type, generics: null, asResult: true);
            if (answer.Outcome == SwiftLoweringOutcome.Indirect)
                arguments.Add("ptr noalias sret");
            else if (answer is { Outcome: SwiftLoweringOutcome.Direct, HasLayout: true })
                result = SwiftPhysicalSignaturePredictor.ResultType(answer.Layout);
            else
                return null;
        }
        foreach (var parameter in facts.Parameters)
        {
            if (parameter.Convention is not (SwiftParameterConvention.Guaranteed or SwiftParameterConvention.Owned))
                return null;
            var answer = lowering.Lower(parameter.Type);
            if (answer.Outcome == SwiftLoweringOutcome.Indirect)
                arguments.Add("ptr noalias");
            else if (answer is { Outcome: SwiftLoweringOutcome.Direct, HasLayout: true })
                arguments.AddRange(answer.Layout.Select(p => p.IrType));
            else
                return null;
        }
        return $"{result} ({string.Join(", ", arguments)})";
    }

    private static SwiftOracleEntry Judge(SwiftCallFacts facts, SwiftIrModule ir, ISwiftValueLowering lowering)
    {
        // The implementation and, for a member dispatched through a vtable or witness table, its
        // dispatch thunk: both take the same arguments. A protocol requirement has only the thunk.
        var defined = new List<SwiftIrFunction>();
        if (facts.Target.Symbol is { } symbol)
        {
            var bare = symbol.StartsWith("_$", StringComparison.Ordinal) ? symbol[1..] : symbol;
            if (ir.Functions.TryGetValue(bare, out var implementation))
                defined.Add(implementation);
            if (facts.Target.HasDispatchThunk && ir.Functions.TryGetValue(bare + "Tj", out var thunk))
                defined.Add(thunk);
        }

        var prediction = SwiftPhysicalSignaturePredictor.Predict(facts, lowering);
        SwiftSelfPassing? self = facts.IsComplete ? SwiftPassingClassifier.SelfConvention(facts, lowering) : null;
        if (!prediction.IsPredicted)
            return new SwiftOracleEntry(facts, SwiftOracleOutcome.Refused, null, defined.FirstOrDefault(), prediction.Refusal, self);
        var predicted = prediction.Signature!.Normalized;
        if (defined.Count == 0)
            return new SwiftOracleEntry(facts, SwiftOracleOutcome.NotDefined, predicted, null, null, self);
        var disagreeing = defined.FirstOrDefault(f => f.CallingConvention != "swiftcc" || f.Signature != predicted);
        return disagreeing is null
            ? new SwiftOracleEntry(facts, SwiftOracleOutcome.Match, predicted, defined[0], null, self)
            : new SwiftOracleEntry(facts, SwiftOracleOutcome.Mismatch, predicted, disagreeing, null, self);
    }

    /// <summary>
    /// Where a resolved layout and swiftc's IR type disagree, or null when they agree: the same size,
    /// every typed leaf at the same offset with the same IR type, and every untyped run inside bytes
    /// swiftc also leaves untyped or types as an integer (an enum's tag is <c>i8</c> in IR).
    /// </summary>
    public static string? CompareLayout(SwiftLeafLayout predicted, SwiftIrLayout actual)
    {
        if (predicted.Size != actual.Size)
            return $"size {predicted.Size} != {actual.Size}";
        foreach (var leaf in predicted.Leaves.Where(l => l.Size > 0))
        {
            if (leaf.Kind == SwiftScalarKind.Opaque)
            {
                if (actual.Leaves.Any(a => a.IrType is "ptr" or "float" or "double" && a.Offset < leaf.End && leaf.Offset < a.End))
                    return $"untyped {leaf} covers a typed IR leaf";
                continue;
            }
            var irType = SwiftAggregateLowering.IrTypeOf(leaf.Kind, leaf.Size);
            if (!actual.Leaves.Any(a => a.Offset == leaf.Offset && a.Size == leaf.Size && a.IrType == irType))
                return $"no IR {irType} at offset {leaf.Offset} for {leaf}";
        }
        foreach (var leaf in actual.Leaves.Where(a => a.IrType is not null))
        {
            if (!predicted.Leaves.Any(p => p.Offset <= leaf.Offset && leaf.End <= p.End))
                return $"IR {leaf} is outside every resolved leaf";
        }
        return null;
    }

    private static IEnumerable<TypeDecl> AllTypes(IEnumerable<TypeDecl> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in AllTypes(type.Types))
                yield return nested;
        }
    }

    // ---- the generator's inputs ----

    private static ModuleDecl ParseModule(SwiftOracleModule module, TypeDatabase db)
    {
        var demangling = DemanglingResults.FromTbd(module.TbdPath, NullLoggerFactory.Instance);
        var parser = new SwiftABIParser(module.AbiJsonPath, db, demangling, NullLogger.Instance, SwiftInterfaceFacts.Empty);
        var (decl, _, _) = parser.ParseModule();
        decl.ExportedSymbols = demangling.AllSymbols;
        return decl;
    }

    /// <summary>A type database with the built-in module databases an iOS generation loads.</summary>
    private static TypeDatabase LoadTypeDatabase()
    {
        var db = new TypeDatabase();
        var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift");
        foreach (var file in Directory.GetFiles(directory, "*Database.xml").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file) == "AppKitDatabase.xml")
                continue;
            db.LoadModuleDatabaseFromFile(file).Wait();
        }
        return db;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _demangled = new(StringComparer.Ordinal);

    /// <summary>A symbol's demangling, from <c>swift-demangle</c>.</summary>
    public static string Demangle(string symbol) => _demangled.GetOrAdd(symbol, s =>
        Run(XcrunPath.Value!, new[] { "swift-demangle", "--compact", s.StartsWith("_$", StringComparison.Ordinal) ? s[1..] : s }, Directory.GetCurrentDirectory())
            .Output.Trim());

    // ---- processes ----

    private static (int ExitCode, string Output) Run(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} {string.Join(' ', arguments)} did not finish");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
