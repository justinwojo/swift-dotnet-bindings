// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// How the in-process Roslyn probe reproduces (or deliberately cannot reproduce) each element
/// the binding csproj emitter writes.
/// </summary>
public enum ProbeParityDisposition
{
    /// <summary>The probe reproduces this in its <c>CSharpCompilation</c> (parse option,
    /// compilation option, or a metadata reference it resolves in-process).</summary>
    MirroredInProbe,

    /// <summary>The probe cannot reproduce this in-process because it is resolved by the MSBuild
    /// build/restore graph, not available at generation time (a package the consumer restores, a
    /// workload-injected reference/symbol, or a packaging-only item that a managed compile ignores).
    /// Each such element is a reason the probe is a heuristic and the real build is the gate.</summary>
    NotReproducibleInProcess,
}

/// <summary>
/// The single declared registry of every element the binding csproj emitter can write, each
/// classified by how the in-process probe treats it. This IS the parity checklist: a unit test
/// emits a real csproj, reads back every property/item element name, and asserts each is present
/// here — so a newly emitted csproj property cannot silently diverge the probe's compilation from
/// the real build without failing that test and forcing a deliberate classification.
///
/// The <see cref="ProbeParityDisposition.NotReproducibleInProcess"/> entries are the recorded
/// evidence behind the parity verdict: reference packs, dependency-binding packages, the Apple
/// supplement, and the interop analyzers are resolved by the MSBuild restore graph, so an
/// in-process probe can only approximate them.
/// </summary>
public static class CSharpProbeParityChecklist
{
    /// <summary>
    /// Every csproj element (PropertyGroup child, ItemGroup item, or AssemblyAttribute) the
    /// emitter can write, keyed by its XML element name, with the probe's disposition.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ProbeParityDisposition> KnownCsprojElements =
        new Dictionary<string, ProbeParityDisposition>
        {
            // --- PropertyGroup: compilation-shaping properties the probe mirrors ---
            ["OutputType"] = ProbeParityDisposition.MirroredInProbe,
            ["TargetFramework"] = ProbeParityDisposition.MirroredInProbe,
            ["ImplicitUsings"] = ProbeParityDisposition.MirroredInProbe,
            ["Nullable"] = ProbeParityDisposition.MirroredInProbe,
            ["AllowUnsafeBlocks"] = ProbeParityDisposition.MirroredInProbe,
            ["NoWarn"] = ProbeParityDisposition.MirroredInProbe,
            // The generated csproj disables default Compile items and lists every emitted file
            // explicitly; the probe compiles exactly the emitted .cs file set, so this maps.
            ["EnableDefaultCompileItems"] = ProbeParityDisposition.MirroredInProbe,

            // --- PropertyGroup: packaging / metadata properties a managed compile ignores ---
            ["IsPackable"] = ProbeParityDisposition.NotReproducibleInProcess,
            ["PackageId"] = ProbeParityDisposition.NotReproducibleInProcess,
            ["PackageVersion"] = ProbeParityDisposition.NotReproducibleInProcess,
            // Drives the workload's platform DefineConstants and OS-version attributes at build
            // time; the in-process probe has no workload logic to reproduce that mapping.
            ["SupportedOSPlatformVersion"] = ProbeParityDisposition.NotReproducibleInProcess,
            ["GenerateDocumentationFile"] = ProbeParityDisposition.NotReproducibleInProcess,

            // --- ItemGroup: AssemblyAttribute (DisableRuntimeMarshalling) ---
            // Affects runtime marshalling, not the C# compile; the probe does not need it to
            // reproduce compile diagnostics.
            ["AssemblyAttribute"] = ProbeParityDisposition.MirroredInProbe,

            // --- ItemGroup: references ---
            // Swift.Runtime resolves via ProjectReference against the in-tree build the probe can
            // locate on disk.
            ["ProjectReference"] = ProbeParityDisposition.MirroredInProbe,
            // Dependency-binding packages and the Apple supplement are restored by NuGet at build
            // time; their managed assemblies do not exist at generation time, so the probe cannot
            // reference them (a dependency-bearing binding false-reds under the probe).
            ["PackageReference"] = ProbeParityDisposition.NotReproducibleInProcess,

            // --- ItemGroup: compile / native / packaging items ---
            ["Compile"] = ProbeParityDisposition.MirroredInProbe,
            // Native + packaging items do not participate in a managed library compile.
            ["NativeReference"] = ProbeParityDisposition.NotReproducibleInProcess,
            ["None"] = ProbeParityDisposition.NotReproducibleInProcess,
            ["EmbeddedResource"] = ProbeParityDisposition.NotReproducibleInProcess,
            ["TrimmerRootDescriptor"] = ProbeParityDisposition.NotReproducibleInProcess,
            // SPM resource bundle — an app-bundle runtime resource, copied/packed but never seen by
            // the managed compile.
            ["BundleResource"] = ProbeParityDisposition.NotReproducibleInProcess,

            // --- PropertyGroup: pack-time output wiring (mixed-ObjC companion embedding) ---
            // Extends the pack pipeline (TargetsForTfmSpecificBuildOutput) to embed the ObjC
            // companion assembly; a pack-time concern, not a compilation input the probe reproduces.
            ["TargetsForTfmSpecificBuildOutput"] = ProbeParityDisposition.NotReproducibleInProcess,
        };
}
