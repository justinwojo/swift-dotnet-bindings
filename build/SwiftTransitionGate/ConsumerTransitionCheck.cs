// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SwiftBindings.TransitionGate;

/// <summary>What the consumer check concluded about one compiled assembly.</summary>
public enum ConsumerTransitionVerdict
{
    /// <summary>No manifest, and every Swift transition is a primitive-only forward call.</summary>
    PrimitiveOnly,

    /// <summary>Valid manifests, and the assembly's transitions satisfy the transition gate's rules against them.</summary>
    Verified,

    /// <summary>The assembly carries a transition its consumer cannot be shown to run safely.</summary>
    Refused,
}

/// <summary>The consumer check's result for one assembly.</summary>
/// <param name="AssemblyName">The assembly's name.</param>
/// <param name="Verdict">What the check concluded.</param>
/// <param name="State">The reader's evidence state.</param>
/// <param name="Problems">Why the assembly is refused, one per member or check; empty otherwise.</param>
/// <param name="UnverifiedLibraries">Native libraries the manifests route bridges through that no image was given for, so their hash and exports went unchecked.</param>
public sealed record ConsumerTransitionResult(
    string AssemblyName,
    ConsumerTransitionVerdict Verdict,
    TransitionEvidenceState State,
    ImmutableArray<string> Problems,
    ImmutableArray<string> UnverifiedLibraries);

/// <summary>
/// The transition check of a compiled binding that something consumes: a reference assembly an app
/// resolves, or an assembly a binding package carries. It reads the assembly's transition evidence
/// (<see cref="SwiftTransitionManifestReader"/>) and, where the manifests are valid, runs the
/// transition gate's rules against the plans and guards they embed
/// (<see cref="PackageTransitionEvidence.RuleProblems"/>), then checks each native image given for a
/// library the manifests route through (<see cref="SwiftTransitionManifestReader.CheckNative"/>).
/// </summary>
/// <remarks>
/// <para>The verdicts:</para>
/// <list type="bullet">
/// <item><c>AbsentPrimitiveOnly</c> is accepted: a binding whose Swift calls are all primitive-only
/// forward calls needs no manifest.</item>
/// <item><c>PresentValid</c> is accepted when the rules hold and every native image given matches;
/// a valid manifest proves authenticity, not compliance.</item>
/// <item><c>AbsentNonPrimitive</c> and <c>TamperedOrMismatched</c> are refused.</item>
/// </list>
/// <para>Base class library, <c>System.Reflection.Metadata</c> and <c>System.Text.Json</c> only, so
/// the MSBuild tasks the Runtime and SDK packages ship link-compile it.</para>
/// </remarks>
public static class ConsumerTransitionCheck
{
    /// <summary>
    /// Whether <paramref name="image"/> can make a Swift transition at all, from its metadata tables
    /// alone, so an app's other references are not scanned. True when the assembly is
    /// <c>Swift.Runtime</c> or references it; references a type of
    /// <c>System.Runtime.InteropServices.Swift</c>, <c>CallConvSwift</c> (a function-pointer
    /// convention), <c>UnmanagedCallConvAttribute</c> (how a P/Invoke names the Swift convention, whose
    /// <c>typeof</c> argument is a string in the attribute blob, not a type reference) or
    /// <c>UnmanagedCallersOnlyAttribute</c>; or imports a bridge (<c>SBSR_</c>). An image that does
    /// not read as an assembly is reported as a candidate, so the scan reports why.
    /// </summary>
    public static bool MayHaveSwiftTransitions(byte[] image)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (!pe.HasMetadata)
                return false;
            var md = pe.GetMetadataReader();
            if (md.IsAssembly && md.GetString(md.GetAssemblyDefinition().Name) == "Swift.Runtime")
                return true;
            foreach (var handle in md.AssemblyReferences)
                if (md.GetString(md.GetAssemblyReference(handle).Name) == "Swift.Runtime")
                    return true;
            foreach (var handle in md.TypeReferences)
            {
                var type = md.GetTypeReference(handle);
                var ns = md.GetString(type.Namespace);
                var name = md.GetString(type.Name);
                if (ns == "System.Runtime.InteropServices.Swift"
                    || (ns == "System.Runtime.CompilerServices" && name == "CallConvSwift")
                    || (ns == "System.Runtime.InteropServices" && name is "UnmanagedCallConvAttribute" or "UnmanagedCallersOnlyAttribute"))
                    return true;
            }
            foreach (var handle in md.MethodDefinitions)
            {
                var import = md.GetMethodDefinition(handle).GetImport();
                if (!import.Name.IsNil && SwiftTransitionScanner.IsForwardBridge(md.GetString(import.Name)))
                    return true;
            }
            return false;
        }
        catch (BadImageFormatException)
        {
            return true;
        }
    }

    /// <summary>
    /// Checks <paramref name="assembly"/>. <paramref name="natives"/> are the native images the
    /// consumer embeds; those of a library the assembly's manifests route through are checked
    /// against them, and a routed library with no image is listed in
    /// <see cref="ConsumerTransitionResult.UnverifiedLibraries"/>, a refusal only when
    /// <paramref name="requireNativeImages"/>. <paramref name="scratch"/> receives the manifests'
    /// records for the plan-dependent rules; it is created if missing and left for the caller.
    /// </summary>
    public static ConsumerTransitionResult Check(
        PackagedAssembly assembly, IReadOnlyList<PackagedNative> natives, bool requireNativeImages, string scratch)
    {
        var read = SwiftTransitionManifestReader.Read(assembly.Image, assembly.DisplayPath,
            new SwiftTransitionScanOptions { ReferenceDirectories = assembly.ReferenceDirectories });
        switch (read.State)
        {
            case TransitionEvidenceState.AbsentPrimitiveOnly:
                return new(read.AssemblyName, ConsumerTransitionVerdict.PrimitiveOnly, read.State, [], []);
            case TransitionEvidenceState.AbsentNonPrimitive:
            case TransitionEvidenceState.TamperedOrMismatched:
                return new(read.AssemblyName, ConsumerTransitionVerdict.Refused, read.State, read.Problems, []);
        }

        var problems = new List<string>();
        var (rules, _) = PackageTransitionEvidence.RuleProblems(assembly, read, scratch);
        // The rule lines lead with the assembly's path, which the consumer's diagnostic already names.
        var prefix = assembly.DisplayPath + ": ";
        problems.AddRange(rules.Select(p => p.StartsWith(prefix, StringComparison.Ordinal) ? p[prefix.Length..] : p));

        var unverified = new List<string>();
        foreach (var library in read.Routing.Select(m => m.Manifest.NativeLibrary!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var images = natives.Where(n => n.Library == library).ToList();
            if (images.Count == 0)
            {
                unverified.Add(library);
                if (requireNativeImages)
                    problems.Add($"its manifests route bridges through {library}, but no image of {library} was found to check");
                continue;
            }
            foreach (var native in images)
                problems.AddRange(SwiftTransitionManifestReader.CheckNative(read, library, native.Image, native.DisplayPath));
        }

        return new(read.AssemblyName, problems.Count == 0 ? ConsumerTransitionVerdict.Verified : ConsumerTransitionVerdict.Refused,
            read.State, problems.Distinct(StringComparer.Ordinal).ToImmutableArray(), unverified.ToImmutableArray());
    }

    /// <summary>
    /// The binaries of every <c>{library}.framework</c> under <paramref name="root"/>: a shallow
    /// bundle's at its root, a versioned (Mac) bundle's under <c>Versions/</c>, symbolic links skipped
    /// so a versioned bundle's binary is read once.
    /// </summary>
    public static IEnumerable<string> FrameworkBinaries(string root, string library) =>
        Directory.GetFiles(root, library, SearchOption.AllDirectories)
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}{library}.framework{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !new FileInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint))
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// The native images of <paramref name="library"/> a native reference provides: every slice
    /// binary of an <c>.xcframework</c> or <c>.framework</c> directory named after it, or a
    /// <c>{library}.dylib</c> / <c>lib{library}.dylib</c> file.
    /// </summary>
    public static IEnumerable<string> NativeReferenceImages(string reference, string library)
    {
        var trimmed = reference.TrimEnd('/', '\\');
        var name = Path.GetFileNameWithoutExtension(trimmed);
        var extension = Path.GetExtension(trimmed);
        if (Directory.Exists(trimmed) && name == library && extension is ".xcframework" or ".framework")
            return FrameworkBinaries(trimmed, library);
        if (File.Exists(trimmed) && extension == ".dylib" && (name == library || name == "lib" + library))
            return [trimmed];
        return [];
    }
}
