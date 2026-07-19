// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BindingsGeneration;

/// <summary>
/// Options controlling an in-process probe run. Defaults match the generated iOS csproj shape.
/// </summary>
public sealed record RoslynCSharpProbeOptions
{
    /// <summary>Language version the probe parses with. The probe's Roslyn is a released package;
    /// the real build's compiler is the SDK's (newer) Roslyn, so the highest the probe supports is
    /// still behind — a recorded parity gap.</summary>
    public LanguageVersion LanguageVersion { get; init; } = LanguageVersion.Preview;

    /// <summary>Preprocessor symbols to define. The generated code platform-gates some bodies with
    /// <c>#if __IOS__ …</c>; the standalone csproj is iOS, so the probe approximates the workload
    /// by defining the iOS symbol. The exact workload symbol set is not reproduced in-process.</summary>
    public IReadOnlyList<string> PreprocessorSymbols { get; init; } = new[] { "__IOS__" };

    /// <summary>Diagnostic ids suppressed by the generated csproj's <c>NoWarn</c>.</summary>
    public IReadOnlyList<string> SuppressedDiagnosticIds { get; init; } =
        new[] { "CS0169", "CS0414", "CA1420", "CS1591" };

    /// <summary>Run the interop source generator so <c>[LibraryImport]</c> externs bind. When it
    /// cannot be loaded/run in-process (version coupling), the probe records that and compiles
    /// without it — every extern then false-reds, which is itself the evidence.</summary>
    public bool RunInteropGenerator { get; init; } = true;
}

/// <summary>
/// The always-on in-process C# verification probe: compiles a settled render with Roslyn and
/// reports structured diagnostics. Deterministic, no network, no writes. It is an
/// <em>acceleration heuristic</em>, not the publication gate — its reference set and compiler
/// version cannot exactly match the real MSBuild build (see <see cref="CSharpProbeReferenceSet"/>
/// and <see cref="CSharpProbeParityChecklist"/>) — so a real-build SARIF leg is the gate and the
/// probe is the fast approximation the recovery loop can lean on.
/// </summary>
public static class RoslynCSharpProbe
{
    /// <summary>
    /// Compile <paramref name="renderFiles"/> (file name → C# text) against
    /// <paramref name="references"/> and return the structured diagnostics.
    /// </summary>
    public static CSharpVerificationResult Probe(
        IReadOnlyDictionary<string, string> renderFiles,
        CSharpProbeReferenceSet references,
        RoslynCSharpProbeOptions? options = null)
    {
        options ??= new RoslynCSharpProbeOptions();

        var parseOptions = new CSharpParseOptions(options.LanguageVersion)
            .WithPreprocessorSymbols(options.PreprocessorSymbols);

        // Sort files by name so the compilation (and any generator ordering) is deterministic.
        var trees = renderFiles
            .Where(kv => kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => CSharpSyntaxTree.ParseText(kv.Value, parseOptions, path: kv.Key))
            .ToList();

        var metadataRefs = references.MetadataReferencePaths
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var suppressions = options.SuppressedDiagnosticIds
            .ToImmutableDictionary(id => id, _ => ReportDiagnostic.Suppress);

        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable,
            specificDiagnosticOptions: suppressions,
            // The binding csproj does not set TreatWarningsAsErrors, so warnings stay warnings.
            generalDiagnosticOption: ReportDiagnostic.Default);

        Compilation compilation = CSharpCompilation.Create(
            "SwiftBindingsProbe",
            trees,
            metadataRefs,
            compilationOptions);

        var diagnostics = new List<Diagnostic>();

        if (options.RunInteropGenerator && references.InteropGeneratorPath is not null)
        {
            var (updated, generatorRan) = TryRunInteropGenerator(
                compilation, references.InteropGeneratorPath, parseOptions, out var genDiagnostics, out _);
            if (generatorRan)
            {
                compilation = updated;
                diagnostics.AddRange(genDiagnostics);
            }
            // On failure we keep the base compilation; the missing extern bodies surface as
            // errors, faithfully reflecting that the probe could not reproduce the interop
            // generator in-process.
        }

        diagnostics.AddRange(compilation.GetDiagnostics());

        var mapped = diagnostics
            .Where(d => d.Severity != DiagnosticSeverity.Hidden)
            .Select(Map)
            .OrderBy(d => d.OrderKey)
            .ToList();

        var anyError = mapped.Any(d => d.Severity == CSharpDiagnosticSeverity.Error);
        return CSharpVerificationResult.FromDiagnostics(mapped, buildSucceeded: !anyError);
    }

    /// <summary>
    /// Load and run the interop source generator reflectively. It ships against the SDK's Roslyn,
    /// which is newer than the probe's package, so loading or running it can throw on version
    /// coupling; every failure path is swallowed and reported via <paramref name="generatorRan"/>
    /// = false (with the base compilation returned unchanged).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The interop generator is an external analyzer assembly loaded by path inside " +
                        "the generator host (never a trimmed application). Any load failure is caught " +
                        "and reported as a parity gap, so no functionality silently breaks.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "The generator type is discovered reflectively from that external analyzer " +
                        "assembly and instantiated via its parameterless constructor; a resolution or " +
                        "activation failure is caught and reported, not propagated.")]
    private static (Compilation Updated, bool Ran) TryRunInteropGenerator(
        Compilation compilation,
        string generatorPath,
        CSharpParseOptions parseOptions,
        out IReadOnlyList<Diagnostic> generatorDiagnostics,
        out string? failureReason)
    {
        generatorDiagnostics = Array.Empty<Diagnostic>();
        failureReason = null;
        try
        {
            var assembly = Assembly.LoadFrom(generatorPath);
            var generatorType = assembly.GetTypes().FirstOrDefault(t =>
                !t.IsAbstract &&
                typeof(IIncrementalGenerator).IsAssignableFrom(t) &&
                t.Name.Contains("LibraryImport", StringComparison.Ordinal));
            if (generatorType is null)
            {
                failureReason = "no IIncrementalGenerator implementing LibraryImport found in the analyzer assembly";
                return (compilation, false);
            }

            var generator = (IIncrementalGenerator)Activator.CreateInstance(generatorType)!;
            var driver = CSharpGeneratorDriver.Create(
                new[] { generator.AsSourceGenerator() },
                parseOptions: parseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diags);
            generatorDiagnostics = diags;
            return (updated, true);
        }
        catch (Exception ex)
        {
            // Version coupling (MissingMethodException / TypeLoadException / ReflectionTypeLoad) or
            // any other load failure: the probe cannot reproduce the interop generator in-process.
            failureReason = ex.GetType().Name + ": " + ex.Message;
            return (compilation, false);
        }
    }

    private static CSharpCompileDiagnostic Map(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        var hasLocation = d.Location.IsInSource && span.IsValid;
        var start = span.StartLinePosition;
        var end = span.EndLinePosition;
        return new CSharpCompileDiagnostic(
            Id: d.Id,
            Severity: MapSeverity(d.Severity),
            FilePath: hasLocation ? span.Path : null,
            Line: hasLocation ? start.Line + 1 : 0,
            Column: hasLocation ? start.Character + 1 : 0,
            EndLine: hasLocation ? end.Line + 1 : 0,
            EndColumn: hasLocation ? end.Character + 1 : 0,
            Message: d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static CSharpDiagnosticSeverity MapSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => CSharpDiagnosticSeverity.Error,
        DiagnosticSeverity.Warning => CSharpDiagnosticSeverity.Warning,
        DiagnosticSeverity.Info => CSharpDiagnosticSeverity.Info,
        _ => CSharpDiagnosticSeverity.Hidden,
    };
}
