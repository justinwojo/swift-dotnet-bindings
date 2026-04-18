// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.AppleTypesManifest;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class AppleTypesManifestValidatorTests
{
    // Shared builders so every test runs the validator against the same shape of
    // entry and only the availability record varies.
    private static TypeEntry MakeEntry(Availability availability)
    {
        return new TypeEntry
        {
            SwiftIdentity = "TestModule.TestType",
            ManagedProjection = new ManagedRef { Namespace = "Swift.TestModule", DeclarationPath = new List<string> { "TestType" } },
            AbiCarrier = new ManagedRef { Namespace = "Swift.TestModule", DeclarationPath = new List<string> { "TestType" } },
            Kind = "struct",
            MetadataAccessor = new MetadataAccessor
            {
                Symbol = "$s10TestModule0A4TypeVMa_DOES_NOT_EXIST",
                Library = "TestModule",
                Availability = availability,
                WeakLink = true,
            },
        };
    }

    private static Manifest MakeManifest(TypeEntry entry)
    {
        var manifest = new Manifest { SdkTrain = new SdkTrain { Major = 26 } };
        var module = new Module();
        module.Types.Add(entry);
        manifest.Modules["TestModule"] = module;
        return manifest;
    }

    [Fact]
    public void Empty_availability_is_not_treated_as_unavailable_on_host()
    {
        // No intro_* data at all -> "available everywhere" per the distinction introduced
        // to stop silent skips from hiding VWT drift for unannotated types. The validator
        // must attempt the probe (and fail later because the library/symbol is fake) rather
        // than skipping with SkippedUnavailableOnHost.
        var entry = MakeEntry(new Availability());
        var manifest = MakeManifest(entry);

        var results = AppleTypesManifestValidator.Validate(manifest, writeBack: false, NullLogger.Instance);
        var result = Assert.Single(results);

        Assert.NotEqual(AppleTypesManifestValidator.ValidationOutcome.SkippedUnavailableOnHost, result.Outcome);
        Assert.True(
            result.Outcome is AppleTypesManifestValidator.ValidationOutcome.LibraryLoadFailure
                              or AppleTypesManifestValidator.ValidationOutcome.SymbolMissing,
            $"expected validator to proceed past availability gate and fail at library/symbol resolution, got: {result.Outcome} ({result.Detail})");
    }

    [Fact]
    public void Annotated_availability_with_null_host_field_is_skipped()
    {
        // Annotation is present (iOS-only) but the host platform slot is null — this IS
        // the legitimate "explicitly unavailable on this platform" skip case. Populate
        // only the non-host platform so the host field stays null regardless of which
        // OS the unit-test runner is on (macOS/Linux/Windows-as-CI).
        var availability = new Availability { Ios = "15.0" }; // only iOS, host macOS slot remains null
        if (OperatingSystem.IsIOS())
        {
            // If somehow running on iOS, swap to a non-host-populated platform.
            availability = new Availability { Macos = "14.0" };
        }
        var entry = MakeEntry(availability);
        var manifest = MakeManifest(entry);

        var results = AppleTypesManifestValidator.Validate(manifest, writeBack: false, NullLogger.Instance);
        var result = Assert.Single(results);

        Assert.Equal(AppleTypesManifestValidator.ValidationOutcome.SkippedUnavailableOnHost, result.Outcome);
    }
}
