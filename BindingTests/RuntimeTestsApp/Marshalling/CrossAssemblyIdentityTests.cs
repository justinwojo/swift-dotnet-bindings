// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;

// CA1416: guarded at runtime by OperatingSystem.IsIOSVersionAtLeast; the analyzer
// does not thread the guard through the discovery-generator invocation shim.
#pragma warning disable CA1416

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Permanent cross-module type identity guardrail.
///
/// Two independent consumer assemblies (AppleIdentity.ConsumerA, .ConsumerB)
/// each reference SwiftBindings.Apple and touch Foundation.Locale.Language.
/// If the generator ever regressed and started duplicating emitted types per
/// consumer (e.g. per-assembly <c>internal sealed</c> copies instead of one
/// shared public type from SwiftBindings.Apple), the <c>typeof</c> handles
/// from the two assemblies would differ and so would the resolved Swift
/// TypeMetadata. This test catches that regression as a failed assertion
/// instead of a subtle later-stage crash.
/// </summary>
public class CrossAssemblyIdentityTests : TestBase
{
    public CrossAssemblyIdentityTests(TestResults results) : base(results) { }

    // Known gap: both assemblies load into the default AssemblyLoadContext in
    // the current test app, so `ReferenceEquals(typeFromA, typeFromB)` and
    // `AssemblyQualifiedName` equality cannot distinguish "same type in one
    // ALC" from "same-named type in two different ALCs." The Assembly.Load
    // cross-check below pins the handle against the default-ALC resolution,
    // which catches the specific case of a consumer privately re-emitting
    // the type into its own assembly — but it does NOT catch the ALC-
    // duplication case. Full ALC-isolation coverage (loading ConsumerA and
    // ConsumerB into separately created AssemblyLoadContexts and asserting
    // the resolved Language types diverge as expected, then re-merge via the
    // shared SwiftBindings.Apple assembly in the default ALC) is deferred to
    // a post-0.8 hardening pass.
    public void TestLanguageTypeReferenceEqualsAcrossAssemblies()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("Foundation.Locale.Language requires iOS 16+; skipping.");
            return;
        }

        var typeFromA = AppleIdentity.ConsumerA.TypeProbe.GetLanguageType();
        var typeFromB = AppleIdentity.ConsumerB.TypeProbe.GetLanguageType();

        AssertTrue(
            ReferenceEquals(typeFromA, typeFromB),
            $"System.Type handle must be reference-equal across assemblies; A={typeFromA.AssemblyQualifiedName} B={typeFromB.AssemblyQualifiedName}");

        AssertEqual(
            typeof(Swift.Foundation.Locale.Language).Assembly.FullName,
            typeFromA.Assembly.FullName,
            "Consumer A resolves Language from the SwiftBindings.Apple assembly (not a local duplicate)");

        AssertEqual(
            typeof(Swift.Foundation.Locale.Language).Assembly.FullName,
            typeFromB.Assembly.FullName,
            "Consumer B resolves Language from the SwiftBindings.Apple assembly (not a local duplicate)");

        // Default-ALC pin: resolve the supplement assembly explicitly by the
        // name typeof reports and look up the type using typeof's FullName
        // (which emits nested-type delimiters correctly — Language is nested
        // inside Locale, so the reflection name is "Swift.Foundation.Locale+Language",
        // not the Swift-style dotted form). Verifies the returned Type is
        // reference-equal to what each consumer observed, catching a consumer
        // privately re-emitting the type even if both observed it uniformly.
        var canonicalAssemblyName = typeof(Swift.Foundation.Locale.Language).Assembly.GetName().Name!;
        var canonicalTypeName = typeof(Swift.Foundation.Locale.Language).FullName!;
        var canonicalAssembly = Assembly.Load(canonicalAssemblyName);
        var canonicalType = canonicalAssembly.GetType(canonicalTypeName, throwOnError: true)!;
        AssertTrue(
            ReferenceEquals(canonicalType, typeFromA),
            $"ConsumerA's Language handle matches SwiftBindings.Apple's canonical type; got {typeFromA.AssemblyQualifiedName}");
        AssertTrue(
            ReferenceEquals(canonicalType, typeFromB),
            $"ConsumerB's Language handle matches SwiftBindings.Apple's canonical type; got {typeFromB.AssemblyQualifiedName}");
    }

    public void TestLanguageMetadataHandleMatchesAcrossAssemblies()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("Foundation.Locale.Language requires iOS 16+; skipping.");
            return;
        }

        var metadataFromA = AppleIdentity.ConsumerA.TypeProbe.GetLanguageMetadata();
        var metadataFromB = AppleIdentity.ConsumerB.TypeProbe.GetLanguageMetadata();

        AssertTrue(metadataFromA.IsValid, "Consumer A resolves a valid TypeMetadata");
        AssertTrue(metadataFromB.IsValid, "Consumer B resolves a valid TypeMetadata");
        AssertEqual(metadataFromA, metadataFromB,
            "Both assemblies observe the same Swift runtime metadata handle for Language");
    }

    /// <summary>
    /// Constructs a live Foundation.Locale.Language value in ConsumerA via a
    /// SwiftBindingsTestLib factory, passes the instance to ConsumerB, and
    /// asserts that (1) ConsumerB observes the same <c>System.Type</c> handle
    /// for the value and (2) a MarshalToSwift + NewFromPayload round-trip in
    /// ConsumerB yields the same type. Exercises payload copy/destroy ABI and
    /// cross-assembly value flow on top of the existing type-identity probes.
    /// </summary>
    public void TestLanguageValueRoundTripsAcrossAssemblies()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("Foundation.Locale.Language requires iOS 16+; skipping.");
            return;
        }

        using var lang = AppleIdentity.ConsumerA.TypeProbe.CreateDefaultLanguage();

        var typeInB = AppleIdentity.ConsumerB.TypeProbe.AcceptLanguageTyped(lang);
        AssertTrue(
            ReferenceEquals(typeof(Swift.Foundation.Locale.Language), typeInB),
            $"ConsumerB observes typeof(Language) reference-equal to the SwiftBindings.Apple type; got {typeInB.AssemblyQualifiedName}");

        var roundTrippedType = AppleIdentity.ConsumerB.TypeProbe.RoundTripLanguage(lang);
        AssertTrue(
            ReferenceEquals(typeof(Swift.Foundation.Locale.Language), roundTrippedType),
            $"ConsumerB round-tripped Language via MarshalToSwift + NewFromPayload and observed the same typeof; got {roundTrippedType.AssemblyQualifiedName}");
    }
}
