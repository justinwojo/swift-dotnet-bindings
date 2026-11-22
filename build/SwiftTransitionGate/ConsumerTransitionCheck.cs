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

/// <summary>What a refused assembly's diagnostic should tell its author to do, from what refused it.</summary>
public enum ConsumerTransitionRemedy
{
    /// <summary>Nothing: the assembly is not refused.</summary>
    None,

    /// <summary>Build the binding with the current SDK: a missing or tampered manifest, or a transition check that fails.</summary>
    Regenerate,

    /// <summary>
    /// A native image carries bridges or a manifest hash the assembly's manifests do not account for,
    /// which is what a wrapper left from an earlier routing ships: a clean rebuild replaces it.
    /// </summary>
    CleanRebuild,

    /// <summary>
    /// The module's bridges were withdrawn because the relinked wrapper contradicted the lowering
    /// receipt; such a withdrawal is never accepted. The wrapper's inputs must be made stable.
    /// </summary>
    ReceiptMismatch,

    /// <summary>
    /// The module records a bridge withdrawal, but the compiled assembly or its images do not hold up
    /// as that withdrawal (a dangling bridge, a missing guard, a record that disagrees): a defect a
    /// clean rebuild does not fix. The relink failure the record names is what to fix, so the binding
    /// routes its bridges.
    /// </summary>
    WithdrawalIncoherent,

    /// <summary>
    /// A native wrapper is not the one the assembly was generated with
    /// (<see cref="SwiftWrapperIdentityCheck"/>): a wrapper of another copy of the package, of another
    /// build, or from before wrappers carried their identity. The assembly and its natives must come
    /// from one build of the binding.
    /// </summary>
    WrapperMismatch,
}

/// <summary>The consumer check's result for one assembly.</summary>
/// <param name="AssemblyName">The assembly's name.</param>
/// <param name="Verdict">What the check concluded.</param>
/// <param name="State">The reader's evidence state.</param>
/// <param name="Problems">Why the assembly is refused, one per member or check; empty otherwise.</param>
/// <param name="UnverifiedLibraries">Native libraries the manifests route bridges through, or a withdrawal withdrew them from, that no image was given for, so their hash and exports went unchecked.</param>
/// <param name="Withdrawals">The recorded bridge withdrawals the assembly carries, accepted or not: which members refuse on which engines, and why.</param>
/// <param name="Remedy">What a refusal's diagnostic should tell the author to do; <see cref="ConsumerTransitionRemedy.None"/> when not refused.</param>
public sealed record ConsumerTransitionResult(
    string AssemblyName,
    ConsumerTransitionVerdict Verdict,
    TransitionEvidenceState State,
    ImmutableArray<string> Problems,
    ImmutableArray<string> UnverifiedLibraries,
    ImmutableArray<TransitionWithdrawal> Withdrawals = default,
    ConsumerTransitionRemedy Remedy = ConsumerTransitionRemedy.None)
{
    /// <summary>The withdrawals, never default.</summary>
    public ImmutableArray<TransitionWithdrawal> Withdrawals { get; init; } = Withdrawals.IsDefault ? ImmutableArray<TransitionWithdrawal>.Empty : Withdrawals;
}

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
/// <para>
/// A routing gap a module's recorded bridge withdrawal explains is accepted on the terms of
/// <see cref="PackageTransitionEvidence.RuleProblems"/>, and every image given of the library the
/// bridges were withdrawn from must carry none of them and no manifest hash; any other gap is
/// refused. The withdrawal is an explanation, never an exemption: every other rule still holds, and
/// each restored member refuses, before Swift runs, on the engines its record names.
/// </para>
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
        // The wrapper the assembly was generated with, whatever its transitions: a wrapper of another
        // copy or build is refused even under a primitive-only assembly.
        // A wrapper library no image provides is not reported: the app's load fails on the missing wrapper.
        var (wrapperProblems, _) = SwiftWrapperIdentityCheck.Check(assembly, natives);
        switch (read.State)
        {
            case TransitionEvidenceState.AbsentPrimitiveOnly when !wrapperProblems.IsEmpty:
                return new(read.AssemblyName, ConsumerTransitionVerdict.Refused, read.State, wrapperProblems, [],
                    Remedy: ConsumerTransitionRemedy.WrapperMismatch);
            case TransitionEvidenceState.AbsentPrimitiveOnly:
                return new(read.AssemblyName, ConsumerTransitionVerdict.PrimitiveOnly, read.State, [], []);
            case TransitionEvidenceState.AbsentNonPrimitive:
                return new(read.AssemblyName, ConsumerTransitionVerdict.Refused, read.State, read.Problems.AddRange(wrapperProblems), [],
                    Remedy: ConsumerTransitionRemedy.Regenerate);
            case TransitionEvidenceState.TamperedOrMismatched:
                // A dangling bridge import shows here: what a withdrawal the manifests record says
                // decides whether a clean rebuild can help.
                var recorded = RecordedWithdrawals(read);
                return new(read.AssemblyName, ConsumerTransitionVerdict.Refused, read.State, read.Problems.AddRange(wrapperProblems), [], recorded,
                    recorded.IsEmpty ? ConsumerTransitionRemedy.Regenerate : RemedyOf(recorded));
        }

        var problems = new List<string>();
        var rules = PackageTransitionEvidence.RuleProblems(assembly, read, scratch, acceptWithdrawals: true);
        // The rule lines lead with the assembly's path, which the consumer's diagnostic already names.
        var prefix = assembly.DisplayPath + ": ";
        problems.AddRange(rules.Problems.Select(p => p.StartsWith(prefix, StringComparison.Ordinal) ? p[prefix.Length..] : p));
        var ruleFailures = problems.Count;

        var unverified = new List<string>();
        var withdrawals = rules.Withdrawals.IsEmpty ? RecordedWithdrawals(read) : rules.Withdrawals;
        var routedLibraries = read.Routing.Select(m => m.Manifest.NativeLibrary!).ToHashSet(StringComparer.Ordinal);
        foreach (var library in routedLibraries.Concat(withdrawals.Select(w => w.Library)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var images = natives.Where(n => n.Library == library).ToList();
            if (images.Count == 0)
            {
                unverified.Add(library);
                if (requireNativeImages)
                    problems.Add(routedLibraries.Contains(library)
                        ? $"its manifests route bridges through {library}, but no image of {library} was found to check"
                        : $"its bridges through {library} were withdrawn, but no image of {library} was found to show they are gone");
                continue;
            }
            foreach (var native in images)
                problems.AddRange(SwiftTransitionManifestReader.CheckNative(read, library, native.Image, native.DisplayPath));
        }
        if (!withdrawals.IsEmpty)
            problems.AddRange(WithdrawnRoutingLeftovers(withdrawals, natives,
                routedLibraries.Concat(withdrawals.Select(w => w.Library)).ToHashSet(StringComparer.Ordinal)));

        problems.AddRange(wrapperProblems);
        var refused = problems.Count > 0;
        var remedy = !refused ? ConsumerTransitionRemedy.None
            : problems.Count == wrapperProblems.Length ? ConsumerTransitionRemedy.WrapperMismatch
            : withdrawals.Any(w => w.Cause == TransitionWithdrawal.ReceiptMismatch) ? ConsumerTransitionRemedy.ReceiptMismatch
            : ruleFailures == 0 ? ConsumerTransitionRemedy.CleanRebuild
            : !withdrawals.IsEmpty ? ConsumerTransitionRemedy.WithdrawalIncoherent
            : ConsumerTransitionRemedy.Regenerate;
        return new(read.AssemblyName, refused ? ConsumerTransitionVerdict.Refused : ConsumerTransitionVerdict.Verified,
            read.State, problems.Distinct(StringComparer.Ordinal).ToImmutableArray(), unverified.ToImmutableArray(), withdrawals, remedy);
    }

    /// <summary>
    /// What is left of a withdrawn routing in images the record does not name. The record's library
    /// says which images must show the bridges gone, so it is not trusted to be the only one: a
    /// wrapper that bridged the module exports the module's manifest hash symbol, and no image of
    /// any other library may export it. Only that symbol is the module's own; bridge names follow
    /// the plan, not the module, so another library's bridges are not taken for leftovers.
    /// </summary>
    private static IEnumerable<string> WithdrawnRoutingLeftovers(
        ImmutableArray<TransitionWithdrawal> withdrawals, IReadOnlyList<PackagedNative> natives, IReadOnlySet<string> checkedLibraries)
    {
        var hashSymbols = withdrawals.Select(w => (w, symbol: BindingsGeneration.SwiftTransitionManifestFormat.NativeSymbol(w.Module))).ToList();
        foreach (var native in natives.Where(n => !checkedLibraries.Contains(n.Library)))
        {
            ImmutableArray<MachOSlice> slices;
            try
            {
                slices = MachOSymbols.Read(native.Image);
            }
            catch (BadImageFormatException)
            {
                continue; // not an image a wrapper of the module could be
            }
            foreach (var slice in slices)
                foreach (var (withdrawal, symbol) in hashSymbols.Where(h => slice.Exports.Contains(h.symbol)))
                    yield return $"{native.DisplayPath} ({slice.Architecture}) exports {symbol}, the manifest hash of {withdrawal.Module}'s withdrawn routing, "
                                 + $"but the withdrawal records its bridges as withdrawn from {withdrawal.Library}";
        }
    }

    /// <summary>The withdrawals the manifests that read record, whether or not they hold up.</summary>
    private static ImmutableArray<TransitionWithdrawal> RecordedWithdrawals(TransitionEvidence read) =>
        BridgeTwinPlans.FromSidecars(read.Manifests.Select(m =>
                (m.Resource[..^BindingsGeneration.SwiftTransitionManifestFormat.FileSuffix.Length] + BridgeTwinPlans.SidecarSuffix, m.Manifest.Plans)))
            .Withdrawals;

    private static ConsumerTransitionRemedy RemedyOf(ImmutableArray<TransitionWithdrawal> withdrawals) =>
        withdrawals.Any(w => w.Cause == TransitionWithdrawal.ReceiptMismatch) ? ConsumerTransitionRemedy.ReceiptMismatch : ConsumerTransitionRemedy.WithdrawalIncoherent;

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
