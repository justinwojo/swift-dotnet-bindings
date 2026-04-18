// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration.AppleTypesManifest;

// Live-SDK CI validation for the Apple types manifest (Phase 2 / M10).
//
// For every entry whose metadata accessor is available on the host platform, this
// validator dlsym-probes the accessor symbol in the resolved Apple framework dylib,
// invokes it via a CallConvSwift function pointer to obtain the runtime TypeMetadata,
// and reads the value witness table for size/alignment/stride. The probed values are
// either populated into the manifest (first-run write-back) or compared against the
// existing manifest entry (drift detection).
//
// The probe deliberately mirrors the no-argument CallConvSwift accessor pattern that
// `AppleTypesCsEmitter.EmitVwtOpaque` emits — same library path, same symbol, same
// calling convention. A regression in either side is caught here before a downstream
// consumer crashes at runtime.
public static class AppleTypesManifestValidator
{
    public sealed record ValidationResult(
        string SwiftIdentity,
        string ModuleName,
        ValidationOutcome Outcome,
        int? ProbedSize,
        int? ProbedAlignment,
        int? ProbedStride,
        bool? ProbedIsNonPOD,
        string? Detail);

    public enum ValidationOutcome
    {
        // Probed cleanly; size/align/stride read; no drift vs. manifest (or manifest was empty).
        Probed,
        // Probed cleanly AND existing non-null manifest fields match.
        ProbedMatchesManifest,
        // Probed cleanly but disagrees with non-null manifest fields. Hard failure.
        Drift,
        // Skipped because the entry is not advertised as available on the current host platform.
        SkippedUnavailableOnHost,
        // Could not load the framework dylib. Hard failure when the platform claims availability.
        LibraryLoadFailure,
        // Library loaded but the accessor symbol is missing. Hard failure when claimed available.
        SymbolMissing,
        // Accessor returned a null metadata handle. Hard failure when claimed available.
        AccessorReturnedNull,
    }

    public static IReadOnlyList<ValidationResult> Validate(
        Manifest manifest,
        bool writeBack,
        ILogger logger)
    {
        var results = new List<ValidationResult>();

        foreach (var (moduleName, module) in manifest.Modules)
        {
            foreach (var entry in module.Types)
            {
                var result = ValidateEntry(moduleName, entry, writeBack, logger);
                results.Add(result);
            }
        }

        return results;
    }

    private static ValidationResult ValidateEntry(
        string moduleName,
        TypeEntry entry,
        bool writeBack,
        ILogger logger)
    {
        if (entry.MetadataAccessor is null)
        {
            return new ValidationResult(
                entry.SwiftIdentity, moduleName, ValidationOutcome.SkippedUnavailableOnHost,
                null, null, null, null, "no metadata_accessor in manifest");
        }

        var accessor = entry.MetadataAccessor;
        // Distinguish "availability has no annotation at all" (treat as available
        // everywhere — e.g. Swift stdlib types) from "annotation present but the host
        // platform field is null" (explicitly unavailable on this platform). Only the
        // latter is a legitimate skip. Treating the two equivalently would silently
        // bypass VWT probing for types that lack any intro_* data, so their
        // size/alignment drift across SDK trains would go undetected.
        if (!accessor.Availability.IsEmpty)
        {
            var hostAvailability = GetHostAvailability(accessor.Availability);
            if (hostAvailability is null)
            {
                return new ValidationResult(
                    entry.SwiftIdentity, moduleName, ValidationOutcome.SkippedUnavailableOnHost,
                    null, null, null, null,
                    $"not advertised on host platform (host={GetCurrentPlatformToken()})");
            }
        }

        var libraryPath = ResolveLibraryPath(accessor.Library);

        IntPtr libraryHandle;
        try
        {
            libraryHandle = NativeLibrary.Load(libraryPath);
        }
        catch (Exception ex)
        {
            return new ValidationResult(
                entry.SwiftIdentity, moduleName, ValidationOutcome.LibraryLoadFailure,
                null, null, null, null,
                $"NativeLibrary.Load('{libraryPath}') threw: {ex.Message}");
        }

        try
        {
            if (!NativeLibrary.TryGetExport(libraryHandle, accessor.Symbol, out var symbolAddress))
            {
                return new ValidationResult(
                    entry.SwiftIdentity, moduleName, ValidationOutcome.SymbolMissing,
                    null, null, null, null,
                    $"symbol '{accessor.Symbol}' not exported by '{libraryPath}'");
            }

            TypeMetadata metadata;
            unsafe
            {
                var fnPtr = (delegate* unmanaged[Swift]<TypeMetadata>)symbolAddress;
                metadata = fnPtr();
            }

            if (!metadata.IsValid)
            {
                return new ValidationResult(
                    entry.SwiftIdentity, moduleName, ValidationOutcome.AccessorReturnedNull,
                    null, null, null, null,
                    $"accessor '{accessor.Symbol}' returned a null TypeMetadata handle");
            }

            int probedSize;
            int probedAlignment;
            int probedStride;
            bool probedIsNonPOD;
            unsafe
            {
                var vwt = metadata.ValueWitnessTable;
                probedSize = checked((int)vwt->Size);
                probedAlignment = vwt->Alignment;
                probedStride = checked((int)vwt->Stride);
                probedIsNonPOD = vwt->IsNonPOD;
            }

            // VWT sanity checks (cheap, catch wildly bogus reads early).
            if (probedSize < 0 || probedAlignment <= 0 || probedStride < probedSize)
            {
                return new ValidationResult(
                    entry.SwiftIdentity, moduleName, ValidationOutcome.Drift,
                    probedSize, probedAlignment, probedStride, probedIsNonPOD,
                    $"VWT sanity check failed (size={probedSize} align={probedAlignment} stride={probedStride})");
            }
            if ((probedAlignment & (probedAlignment - 1)) != 0)
            {
                return new ValidationResult(
                    entry.SwiftIdentity, moduleName, ValidationOutcome.Drift,
                    probedSize, probedAlignment, probedStride, probedIsNonPOD,
                    $"VWT alignment is not a power of two (align={probedAlignment})");
            }

            // Drift check vs. existing manifest entry.
            var sizeDrift     = entry.Size      is { } s && s != probedSize;
            var alignDrift    = entry.Alignment is { } a && a != probedAlignment;
            var strideDrift   = entry.Stride    is { } st && st != probedStride;
            if (sizeDrift || alignDrift || strideDrift)
            {
                return new ValidationResult(
                    entry.SwiftIdentity, moduleName, ValidationOutcome.Drift,
                    probedSize, probedAlignment, probedStride, probedIsNonPOD,
                    $"manifest disagrees with live SDK: " +
                    $"size {entry.Size?.ToString() ?? "null"}->{probedSize}, " +
                    $"alignment {entry.Alignment?.ToString() ?? "null"}->{probedAlignment}, " +
                    $"stride {entry.Stride?.ToString() ?? "null"}->{probedStride}");
            }

            // VWT copy/destroy smoke. Allocates a stride-sized buffer, calls
            // InitializeWithCopy from a zero source for POD types only (a non-POD
            // copy from arbitrary bytes would invoke ARC/destructors on garbage),
            // then Destroy. Validates the function pointers are non-null and that
            // the round-trip does not crash. Skipped for non-POD because we have
            // no real source value here.
            if (!probedIsNonPOD && probedSize > 0)
            {
                unsafe
                {
                    var vwt = metadata.ValueWitnessTable;
                    if (vwt->InitializeWithCopy == null || vwt->Destroy == null)
                    {
                        return new ValidationResult(
                            entry.SwiftIdentity, moduleName, ValidationOutcome.Drift,
                            probedSize, probedAlignment, probedStride, probedIsNonPOD,
                            "VWT InitializeWithCopy or Destroy function pointer is null");
                    }
                    var src = NativeMemory.AllocZeroed((nuint)probedSize);
                    var dst = NativeMemory.AllocZeroed((nuint)probedSize);
                    try
                    {
                        vwt->InitializeWithCopy(dst, src, metadata);
                        vwt->Destroy(dst, metadata);
                    }
                    finally
                    {
                        NativeMemory.Free(src);
                        NativeMemory.Free(dst);
                    }
                }
            }

            var manifestHadAllFields = entry.Size.HasValue && entry.Alignment.HasValue && entry.Stride.HasValue;

            if (writeBack)
            {
                entry.Size = probedSize;
                entry.Alignment = probedAlignment;
                entry.Stride = probedStride;
                entry.ValueWitness.Trivial = !probedIsNonPOD;
            }

            var outcome = manifestHadAllFields
                ? ValidationOutcome.ProbedMatchesManifest
                : ValidationOutcome.Probed;

            return new ValidationResult(
                entry.SwiftIdentity, moduleName, outcome,
                probedSize, probedAlignment, probedStride, probedIsNonPOD,
                writeBack ? "wrote probed values to manifest" : null);
        }
        finally
        {
            NativeLibrary.Free(libraryHandle);
        }
    }

    // Resolves manifest `library` to an absolute dyld path for host-side validation.
    //
    // INTENTIONALLY DIFFERENT from AppleTypesCsEmitter.ResolveLibraryPath: the emitter
    // emits BARE names (e.g. "CryptoKit") into supplement DllImports so the macios
    // linker's `.framework/` substring scan doesn't force-add `-framework` entries for
    // modules the consumer never references (BlastRadius FINDINGS #9). This validator
    // runs at manifest-validation time on the host (macOS) where `NativeLibrary.Load`
    // needs a concrete dyld path — no macios linker, no blast-radius concern — so we
    // expand bare names to `/System/Library/Frameworks/X.framework/X` here.
    private static string ResolveLibraryPath(string library)
    {
        return library switch
        {
            "Swift" or "SwiftCore" => "/usr/lib/swift/libswiftCore.dylib",
            "Dispatch" => "/usr/lib/swift/libswiftDispatch.dylib",
            _ => $"/System/Library/Frameworks/{library}.framework/{library}",
        };
    }

    // The validator runs on the host (macOS during nuke ValidateAppleTypesManifest),
    // so "host platform" maps to the macos slot of the manifest's availability
    // record. ManagedSettings entries that are iOS-only have macos=null and are
    // legitimately skipped — the SDK simply doesn't ship that framework on macOS.
    private static string? GetHostAvailability(Availability availability)
    {
        var token = GetCurrentPlatformToken();
        return token switch
        {
            "macos" => availability.Macos,
            "ios" => availability.Ios,
            "tvos" => availability.Tvos,
            "maccatalyst" => availability.Maccatalyst,
            _ => null,
        };
    }

    private static string GetCurrentPlatformToken()
    {
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsIOS()) return "ios";
        if (OperatingSystem.IsTvOS()) return "tvos";
        if (OperatingSystem.IsMacCatalyst()) return "maccatalyst";
        return "unknown";
    }
}
