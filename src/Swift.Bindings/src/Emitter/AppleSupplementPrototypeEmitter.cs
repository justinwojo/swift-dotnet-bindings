// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using BindingsGeneration.AppleTypesManifest;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BindingsGeneration;

/// <summary>
/// Emits a trimmed <c>SwiftBindings.Apple.Prototype.csproj</c> plus its .cs sources so a
/// consumer can reference the supplement as a project instead of a NuGet package while
/// iterating on new Apple-supplement types. Prototyping mode keeps canonical identity
/// (<c>SwiftBindings.Apple</c> assembly + <c>Swift.*</c> namespace) intact so flipping
/// between project and package reference is transparent to the generated bindings.
/// </summary>
/// <remarks>
/// <para>The emitter is demand-driven: the caller passes the Swift identities the current
/// generator run actually resolved through <see cref="AppleSupplementResolver"/>, and the
/// prototype project is trimmed to just those modules/types. This keeps the prototype
/// focused on the supplement surface the consumer is debugging rather than pulling in the
/// full Apple-types manifest on every build.</para>
/// <para>Assembly name + root namespace are <b>intentionally pinned</b> to
/// <c>SwiftBindings.Apple</c> / <c>Swift</c> so generated consumer bindings compile against
/// exactly the same symbols they would see from the published package. Changing either
/// would silently fork the symbol graph and defeat the swap-ability invariant.</para>
/// </remarks>
public static class AppleSupplementPrototypeEmitter
{
    private const string ManifestResourceName = "Swift.Bindings.apple-types-manifest.json";
    public const string PrototypeCsprojName = "SwiftBindings.Apple.Prototype.csproj";
    public const string SourcesSubdirectory = "Sources";

    public sealed class Options
    {
        /// <summary>Directory the prototype project + its sources will be materialized into.</summary>
        public required string PrototypeDirectory { get; init; }

        /// <summary>
        /// Swift identities (module-qualified, e.g. <c>Foundation.Locale.Language</c>) the
        /// outer generator resolved through the supplement. Only manifest entries whose
        /// identity is in this set get emitted, keeping the prototype trimmed.
        /// </summary>
        public required IReadOnlyCollection<string> ReferencedIdentities { get; init; }

        /// <summary>
        /// Platform info used to pick the target framework string for the prototype csproj.
        /// Must match the consumer's TFM so dependency unification resolves cleanly.
        /// </summary>
        public required PlatformInfo PlatformInfo { get; init; }

        /// <summary>
        /// SwiftBindings.Runtime version the consumer references. The prototype follows the
        /// same dev-sentinel convention as <see cref="BindingProjectEmitter"/>:
        /// <c>0.0.0-dev</c> means "bind against the in-tree project via SwiftBindingsRepoRoot";
        /// any other value emits a PackageReference with a bounded patch range.
        /// </summary>
        public string? SwiftRuntimeVersion { get; init; }

        /// <summary>
        /// Optional override for the minimum OS version the prototype advertises. Defaults
        /// to 15.0 (the floor SwiftBindings.Apple publishes).
        /// </summary>
        public string? MinimumOSVersion { get; init; }
    }

    public sealed class Result
    {
        public required string CsprojPath { get; init; }
        public required IReadOnlyList<string> EmittedSourceFiles { get; init; }
        public required IReadOnlyList<string> SkippedIdentities { get; init; }
    }

    /// <summary>
    /// Materializes the prototype directory. Returns the absolute csproj path so the caller
    /// can wire a <c>ProjectReference</c> into the consumer's generated csproj.
    /// </summary>
    public static Result Emit(Options options, ILogger logger)
    {
        if (options.ReferencedIdentities.Count == 0)
            throw new InvalidOperationException(
                "AppleSupplementPrototypeEmitter.Emit called with no referenced identities — " +
                "the caller should skip prototype emission when the generator didn't resolve any supplement types.");

        Directory.CreateDirectory(options.PrototypeDirectory);
        var sourcesDir = Path.Combine(options.PrototypeDirectory, SourcesSubdirectory);
        Directory.CreateDirectory(sourcesDir);

        var manifest = LoadEmbeddedManifest();
        var trimmed = TrimManifest(manifest, options.ReferencedIdentities);

        // Fresh-emit: clear stale .cs so a previous run's types don't linger when the set of
        // referenced identities shrinks between invocations. Scoped to the sources dir only.
        if (Directory.Exists(sourcesDir))
        {
            foreach (var file in Directory.EnumerateFiles(sourcesDir, "*.cs", SearchOption.AllDirectories))
                File.Delete(file);
        }

        // Use an empty whitelist — prototypes always take the safe VWT-backed opaque path.
        // Sequential-layout gating still requires live-SDK size/alignment validation that
        // only the canonical supplement build carries; forcing opaque here keeps ABI-correct
        // even when the manifest advertises sequential.
        var whitelist = SequentialLayoutWhitelist.Empty();
        var emitter = new AppleTypesCsEmitter(whitelist, logger);
        emitter.Emit(trimmed, sourcesDir);

        // Fail-closed on structural skips — same policy AppleTypesCsCommand applies to the
        // canonical supplement build. A blank metadata accessor (or any other malformed
        // entry) silently drops the type, and the prototype is meant to mirror the packaged
        // artifact exactly. Swallowing here would let a hand-patched embedded manifest diverge
        // undetected until ship.
        if (emitter.StructuralSkips.Count > 0)
        {
            foreach (var skip in emitter.StructuralSkips)
                logger.LogError("Structural skip: '{Identity}' — {Reason}", skip.SwiftIdentity, skip.Reason);
            throw new InvalidOperationException(
                $"AppleSupplementPrototypeEmitter: manifest contains {emitter.StructuralSkips.Count} " +
                "structural skip(s). Fix the embedded apple-types-manifest before re-running the prototype.");
        }

        var csprojPath = Path.Combine(options.PrototypeDirectory, PrototypeCsprojName);
        WriteCsproj(csprojPath, options, emitter.EmittedFiles);

        logger.LogInformation(
            "Apple-supplement prototype emitted: {Csproj} ({FileCount} source file(s), {SkippedCount} benign skips).",
            csprojPath, emitter.EmittedFiles.Count, emitter.SkippedEntries.Count);

        return new Result
        {
            CsprojPath = csprojPath,
            EmittedSourceFiles = emitter.EmittedFiles.ToList(),
            SkippedIdentities = emitter.SkippedEntries.Select(s => s.SwiftIdentity).ToList(),
        };
    }

    internal static Manifest LoadEmbeddedManifest()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ManifestResourceName}' not found. Ensure the manifest " +
                "is wired up as EmbeddedResource in Swift.Bindings.csproj.");
        using var reader = new StreamReader(stream);
        return JsonConvert.DeserializeObject<Manifest>(reader.ReadToEnd())
            ?? throw new InvalidOperationException("Failed to deserialize apple-types-manifest.json.");
    }

    internal static Manifest TrimManifest(Manifest source, IReadOnlyCollection<string> identities)
    {
        var wanted = new HashSet<string>(identities, StringComparer.Ordinal);
        var trimmed = new Manifest
        {
            Schema = source.Schema,
            ManifestVersion = source.ManifestVersion,
            GeneratedBy = source.GeneratedBy,
            GeneratedAt = source.GeneratedAt,
            SdkTrain = source.SdkTrain,
        };

        foreach (var (moduleName, module) in source.Modules)
        {
            var keptTypes = module.Types
                .Where(t => wanted.Contains(t.SwiftIdentity))
                .ToList();
            if (keptTypes.Count == 0)
                continue;

            trimmed.Modules[moduleName] = new AppleTypesManifest.Module
            {
                Types = keptTypes,
                // Typealiases are cheap and pass through unchanged — they serve as reference
                // documentation; the emitter does not materialize C# for them so skipping
                // filtering keeps the JSON serializable without extra work. (Prototype mode
                // does not re-serialize the manifest to disk, but preserving completeness
                // here keeps equality-based tests stable.)
                Typealiases = module.Typealiases,
            };
        }

        return trimmed;
    }

    private static void WriteCsproj(
        string csprojPath,
        Options options,
        IReadOnlyList<string> emittedFiles)
    {
        var runtimeVersion = options.SwiftRuntimeVersion ?? BindingProjectEmitter.DefaultSwiftRuntimeVersion;
        var tfm = options.PlatformInfo.PackTfm;
        var minOs = options.MinimumOSVersion ?? options.PlatformInfo.DefaultMinimumOS;

        // Runtime reference mirrors BindingProjectEmitter's dev-sentinel path so the prototype
        // behaves identically to the outer project: in-tree ProjectReference when
        // SwiftBindingsRepoRoot is set, exact-version PackageReference otherwise. Keeping
        // them in lockstep avoids the case where the consumer binds against one Runtime
        // and the prototype binds against another.
        var runtimeReference = runtimeVersion == BindingProjectEmitter.DefaultSwiftRuntimeVersion
            ? $"""
                    <!-- Local-dev wiring: matches BindingProjectEmitter. Set SwiftBindingsRepoRoot to
                         bind against the in-tree Swift.Runtime project; otherwise fall back to a
                         strict-version PackageReference that fails loud if the package is missing. -->
                    <ProjectReference Include="$(SwiftBindingsRepoRoot)/src/Swift.Runtime/src/Swift.Runtime.csproj"
                                      Condition="'$(SwiftBindingsRepoRoot)' != ''" />
                    <PackageReference Include="SwiftBindings.Runtime" Version="[{runtimeVersion}]" Condition="'$(SwiftBindingsRepoRoot)' == ''" />
                """
            : $"""
                    <PackageReference Include="SwiftBindings.Runtime" Version="{BindingProjectEmitter.BuildBoundedRuntimeVersionRange(runtimeVersion)}" />
                """;

        var content = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <!-- Prototype project for SwiftBindings.Apple. Emitted by the generator on demand
                     when --apple-supplement-prototype-dir is passed. Swappable for a
                     PackageReference to SwiftBindings.Apple — assembly name, root namespace, and
                     emitted .cs contents all match the canonical package. -->
                <TargetFramework>{tfm}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <IsPackable>false</IsPackable>
                <AssemblyName>SwiftBindings.Apple</AssemblyName>
                <RootNamespace>Swift</RootNamespace>
                <SupportedOSPlatformVersion>{minOs}</SupportedOSPlatformVersion>
                <NoWarn>$(NoWarn);SB1001;CS1591</NoWarn>
                <!-- Generator lists the Compile items explicitly below; default wildcard would
                     pull in the same files twice and trip NETSDK1022. -->
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>

              <ItemGroup>
                <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
              </ItemGroup>

              <ItemGroup>
            {runtimeReference}
              </ItemGroup>

              <ItemGroup>
            {BuildCompileItems(csprojPath, emittedFiles)}
              </ItemGroup>
            </Project>
            """;

        File.WriteAllText(csprojPath, content);
    }

    private static string BuildCompileItems(string csprojPath, IReadOnlyList<string> emittedFiles)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
        var lines = emittedFiles
            .Select(p => Path.GetRelativePath(projectDir, Path.GetFullPath(p)).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(rel => $"    <Compile Include=\"{XmlEscape(rel)}\" />")
            .ToList();
        return string.Join(Environment.NewLine, lines);
    }

    private static string XmlEscape(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
