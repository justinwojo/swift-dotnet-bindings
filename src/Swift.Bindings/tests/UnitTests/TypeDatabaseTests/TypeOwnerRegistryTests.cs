// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Exercises the 6-level resolver order: per-type overrides, per-conformance overrides,
/// per-module policy, per-framework policy, per-package defaults, and global fallback.
/// Each level shadows the ones below it; a miss at one level falls through to the next.
/// </summary>
/// <remarks>
/// The registry is process-global static state. Tests run inside a dedicated collection so
/// they do not race against one another, and every test resets the registry to its seeded
/// defaults before exercising additional registrations.
/// </remarks>
[Collection(nameof(TypeOwnerRegistryCollection))]
public class TypeOwnerRegistryTests
{
    public TypeOwnerRegistryTests()
    {
        TypeOwnerRegistry.ResetForTests();
    }

    // ---- Level 1 — Per-type overrides (stdlib pins + SwiftUI.Text) ----------------

    [Theory]
    [InlineData("Swift.String")]
    [InlineData("Swift.AnyHashable")]
    [InlineData("Swift.Hasher")]
    [InlineData("Swift.DispatchQueue")]
    public void Resolve_StdlibCanonical_ResolvesToRuntime(string swiftIdentity)
    {
        var owner = TypeOwnerRegistry.Resolve(swiftIdentity);

        Assert.Equal(TypeOwnerKind.Runtime, owner.Kind);
        Assert.Equal(TypeOwnerRegistry.RuntimePackageId, owner.PackageId);
    }

    [Theory]
    [InlineData("Foundation.Date")]
    [InlineData("Foundation.Data")]
    [InlineData("Foundation.URL")]
    [InlineData("Foundation.Decimal")]
    [InlineData("Foundation.AnyError")]
    [InlineData("Foundation.Measurement")]
    [InlineData("Foundation.Measurement<UnitType>")]
    [InlineData("Foundation.Measurement<Foundation.UnitLength>")]
    [InlineData("ManagedSettings.Token")]
    [InlineData("ManagedSettings.Token<Application>")]
    // Matter framework — pure ObjC, ships no .swiftinterface. Owner resolution lands on
    // AppleSupplement (Matter is in s_defaultAppleModules); the actual TypeRecord is then
    // synthesized by ObjCBridgingStrategy (class types) or DatabaseLookupStrategy (the
    // two WiFi value types in MatterDatabase.xml). MatterSupport's cross-module fix
    // depends on this owner classification.
    [InlineData("Matter.MTRSetupPayload")]
    [InlineData("Matter.MTRNetworkCommissioningWiFiBand")]
    [InlineData("Matter.MTRNetworkCommissioningWiFiSecurity")]
    public void Resolve_AppleSupplementCanonical_ResolvesToAppleSupplement(string swiftIdentity)
    {
        // Previously pinned to Runtime (legacy canonical list). Now resolve via the Apple
        // module default — Foundation and ManagedSettings are both in s_defaultAppleModules.
        var owner = TypeOwnerRegistry.Resolve(swiftIdentity);

        Assert.Equal(TypeOwnerKind.AppleSupplement, owner.Kind);
        Assert.Equal(TypeOwnerRegistry.AppleSupplementPackageId, owner.PackageId);
    }

    [Fact]
    public void Resolve_SwiftUIText_ResolvesToAppleSupplementViaOverride()
    {
        // SwiftUI is deliberately excluded from s_defaultAppleModules (SwiftUI types are
        // suppressed at generation time). SwiftUI.Text is the one hand-rolled exception and
        // is pinned via s_appleSupplementOverrides — without that pin it would fall through
        // to Unsupported.
        var owner = TypeOwnerRegistry.Resolve("SwiftUI.Text");

        Assert.Equal(TypeOwnerKind.AppleSupplement, owner.Kind);
        Assert.Equal(TypeOwnerRegistry.AppleSupplementPackageId, owner.PackageId);
        Assert.Equal("SwiftUI", owner.ModuleName);
    }

    [Fact]
    public void Resolve_StdlibOverride_WinsOverModuleDefault()
    {
        // "Swift" module is not in s_defaultAppleModules so this is partly structural, but
        // the intent of the stdlib pin is that the override layer applies regardless.
        var owner = TypeOwnerRegistry.Resolve("Swift.String");

        Assert.Equal(TypeOwnerKind.Runtime, owner.Kind);
    }

    [Fact]
    public void RegisterPerTypeOverride_TakesPrecedenceOverAllOtherLevels()
    {
        var custom = new TypeOwner
        {
            Kind = TypeOwnerKind.ThirdPartyPackage,
            PackageId = "Contoso.Foundation.Overlay",
        };
        TypeOwnerRegistry.RegisterPerTypeOverride("Foundation.CustomLocale", custom);

        var owner = TypeOwnerRegistry.Resolve("Foundation.CustomLocale");

        Assert.Equal(TypeOwnerKind.ThirdPartyPackage, owner.Kind);
        Assert.Equal("Contoso.Foundation.Overlay", owner.PackageId);
    }

    // ---- Level 2 — Swift stdlib ---------------------------------------------------

    [Fact]
    public void RegisterSwiftStdlibType_ResolvesToSwiftStdlibKind()
    {
        // Swift.Int (not Swift.String) — Swift.String is pinned via s_legacyRuntimeCanonicals
        // so it resolves at Level 1 as TypeOwnerKind.Runtime and would never reach the Level 2
        // stdlib path. Pick an identity that's actually a pure stdlib registration to exercise
        // the Level 2 resolver.
        TypeOwnerRegistry.RegisterSwiftStdlibType("Swift.Int");

        var owner = TypeOwnerRegistry.Resolve("Swift.Int");

        Assert.Equal(TypeOwnerKind.SwiftStdlib, owner.Kind);
        Assert.Equal(TypeOwnerRegistry.RuntimePackageId, owner.PackageId);
    }

    // ---- Level 3 — ObjC workload projection --------------------------------------

    [Fact]
    public void RegisterObjCWorkloadProjection_ResolvesToObjCWorkloadKind()
    {
        // NSDate is NOT in the per-type override table for Foundation.Date, and Foundation
        // is in the default Apple module set. A generator-time registration must still
        // project an ObjC workload type instead of the supplement.
        TypeOwnerRegistry.RegisterObjCWorkloadProjection(
            swiftIdentity: "Foundation.NSLocale",
            projectedTypeName: "global::Foundation.NSLocale",
            moduleName: "Foundation");

        var owner = TypeOwnerRegistry.Resolve("Foundation.NSLocale");

        Assert.Equal(TypeOwnerKind.ObjCWorkload, owner.Kind);
        Assert.Equal("global::Foundation.NSLocale", owner.ProjectedTypeName);
    }

    [Fact]
    public void RegisterObjCWorkloadProjection_AppliesToGenericInstantiations()
    {
        // ObjC projections registered for the unbound stem must also cover bound forms, to
        // stay consistent with the exact-then-stripped pattern used for overrides and stdlib.
        TypeOwnerRegistry.RegisterObjCWorkloadProjection(
            swiftIdentity: "Foundation.NSArray",
            projectedTypeName: "global::Foundation.NSArray",
            moduleName: "Foundation");

        var owner = TypeOwnerRegistry.Resolve("Foundation.NSArray<NSString>");

        Assert.Equal(TypeOwnerKind.ObjCWorkload, owner.Kind);
        Assert.Equal("global::Foundation.NSArray", owner.ProjectedTypeName);
    }

    // ---- Level 4 — Module default -------------------------------------------------

    [Fact]
    public void Resolve_NewAppleModuleType_ResolvesToAppleSupplement()
    {
        // Brand-new Foundation type the registry has never heard of — must still land on
        // the supplement by the Apple-module default.
        var owner = TypeOwnerRegistry.Resolve("Foundation.Locale.Language");

        Assert.Equal(TypeOwnerKind.AppleSupplement, owner.Kind);
        Assert.Equal(TypeOwnerRegistry.AppleSupplementPackageId, owner.PackageId);
        Assert.Equal("Foundation", owner.ModuleName);
    }

    [Theory]
    [InlineData("CryptoKit.P256.Signing.ECDSASignature", "CryptoKit")]
    [InlineData("ManagedSettings.Application", "ManagedSettings")]
    [InlineData("FamilyControls.FamilyActivitySelection", "FamilyControls")]
    [InlineData("WeatherKit.HourWeather", "WeatherKit")]
    [InlineData("TipKit.Tip", "TipKit")]
    public void Resolve_AppleModuleTypes_ResolveToAppleSupplement(string swiftIdentity, string expectedModule)
    {
        var owner = TypeOwnerRegistry.Resolve(swiftIdentity);

        Assert.Equal(TypeOwnerKind.AppleSupplement, owner.Kind);
        Assert.Equal(expectedModule, owner.ModuleName);
    }

    [Fact]
    public void RegisterThirdPartyModule_ResolvesToThirdPartyPackage()
    {
        TypeOwnerRegistry.RegisterThirdPartyModule("Stripe", "Stripe.Swift.iOS");

        var owner = TypeOwnerRegistry.Resolve("Stripe.PaymentIntent");

        Assert.Equal(TypeOwnerKind.ThirdPartyPackage, owner.Kind);
        Assert.Equal("Stripe.Swift.iOS", owner.PackageId);
        Assert.Equal("Stripe", owner.ModuleName);
    }

    // ---- Level 5 — Same-module-being-generated ------------------------------------

    [Fact]
    public void Resolve_SameModuleBeingGenerated_Unregistered_ResolvesToLocal()
    {
        // Module is neither Apple nor third-party. When the current generation target
        // matches the declaring module, fall through to Local.
        var owner = TypeOwnerRegistry.Resolve("MyApp.MyType", currentlyGeneratingModule: "MyApp");

        Assert.Equal(TypeOwnerKind.LocalModule, owner.Kind);
        Assert.Equal("MyApp", owner.ModuleName);
    }

    [Fact]
    public void Resolve_ThirdPartyModule_EvenWhenCurrentlyGenerating_StillResolvesToPackage()
    {
        // The third-party package IS the canonical owner of its own types; registering the
        // owner and then generating that same package should keep module-default semantics.
        // Treating it as "local" during self-generation is an emitter concern, not a
        // registry concern — the registry always reports the canonical owner.
        TypeOwnerRegistry.RegisterThirdPartyModule("Stripe", "Stripe.Swift.iOS");

        var owner = TypeOwnerRegistry.Resolve("Stripe.PaymentIntent", currentlyGeneratingModule: "Stripe");

        Assert.Equal(TypeOwnerKind.ThirdPartyPackage, owner.Kind);
        Assert.Equal("Stripe.Swift.iOS", owner.PackageId);
    }

    // ---- Level 6 — Unsupported ----------------------------------------------------

    [Fact]
    public void Resolve_UnknownModule_ResolvesToUnsupported()
    {
        var owner = TypeOwnerRegistry.Resolve("NotARealModule.NotARealType");

        Assert.Equal(TypeOwnerKind.Unsupported, owner.Kind);
        Assert.Equal("NotARealModule", owner.ModuleName);
    }

    [Fact]
    public void Resolve_RootIdentifierWithNoModule_ResolvesToUnsupported()
    {
        var owner = TypeOwnerRegistry.Resolve("RootOnly");

        Assert.Equal(TypeOwnerKind.Unsupported, owner.Kind);
        Assert.Null(owner.ModuleName);
    }

    // ---- Precedence gauntlet — every level layered on one identity ----------------

    [Fact]
    public void Resolve_PrecedenceGauntlet_OverrideBeatsEverythingElse()
    {
        // Start with a Foundation type that would normally resolve to the Apple supplement,
        // layer a stdlib and ObjC workload entry on top, then add a per-type override.
        // Level 1 must win.
        TypeOwnerRegistry.RegisterSwiftStdlibType("Foundation.Token");
        TypeOwnerRegistry.RegisterObjCWorkloadProjection("Foundation.Token", "global::Foundation.NSToken");
        TypeOwnerRegistry.RegisterPerTypeOverride("Foundation.Token", TypeOwner.Runtime);

        var owner = TypeOwnerRegistry.Resolve("Foundation.Token");

        Assert.Equal(TypeOwnerKind.Runtime, owner.Kind);
    }

    [Fact]
    public void Resolve_PrecedenceGauntlet_StdlibBeatsObjCWorkload()
    {
        TypeOwnerRegistry.RegisterObjCWorkloadProjection("Swift.Optional", "global::System.Nullable");
        TypeOwnerRegistry.RegisterSwiftStdlibType("Swift.Optional");

        var owner = TypeOwnerRegistry.Resolve("Swift.Optional");

        Assert.Equal(TypeOwnerKind.SwiftStdlib, owner.Kind);
    }

    [Fact]
    public void Resolve_PrecedenceGauntlet_ObjCWorkloadBeatsModuleDefault()
    {
        // UIKit is in the Apple module default set. An explicit ObjC workload projection
        // must still take precedence over the supplement fallback.
        TypeOwnerRegistry.RegisterObjCWorkloadProjection(
            "UIKit.UIImage",
            "global::UIKit.UIImage",
            "UIKit");

        var owner = TypeOwnerRegistry.Resolve("UIKit.UIImage");

        Assert.Equal(TypeOwnerKind.ObjCWorkload, owner.Kind);
    }

    // ---- Cross-module protocol conformance ----------------------------------------

    [Fact]
    public void ConformanceOwner_UnregisteredPair_ReturnsNull()
    {
        var owner = TypeOwnerRegistry.TryGetConformanceOwner("Foundation.Date", "Swift.Hashable");

        Assert.Null(owner);
    }

    [Fact]
    public void ConformanceOwner_Registered_ReturnsRegisteredOwner()
    {
        // Type from module A conforming to protocol from module B with the conformance
        // itself owned by a third-party package — the conformance carrier, not either
        // endpoint module, decides ownership so ordering cannot cause a split-package.
        var expected = new TypeOwner
        {
            Kind = TypeOwnerKind.ThirdPartyPackage,
            PackageId = "Contoso.DateExtensions",
        };
        TypeOwnerRegistry.RegisterConformanceOwner(
            "Foundation.Date",
            "Contoso.TimestampEncodable",
            expected);

        var owner = TypeOwnerRegistry.TryGetConformanceOwner(
            "Foundation.Date",
            "Contoso.TimestampEncodable");

        Assert.NotNull(owner);
        Assert.Equal(expected, owner!.Value);
    }

    [Fact]
    public void ConformanceOwner_DistinctFromTypeOwner()
    {
        // Swift.String's type owner is pinned to Runtime (legacy stdlib canonical). The
        // conformance owner is a separate registration — they must not collide.
        var conformanceOwner = new TypeOwner
        {
            Kind = TypeOwnerKind.ThirdPartyPackage,
            PackageId = "Contoso.StringExtensions",
        };
        TypeOwnerRegistry.RegisterConformanceOwner(
            "Swift.String",
            "Contoso.SomeProtocol",
            conformanceOwner);

        var typeOwner = TypeOwnerRegistry.Resolve("Swift.String");
        var recordedConformance = TypeOwnerRegistry.TryGetConformanceOwner(
            "Swift.String",
            "Contoso.SomeProtocol");

        Assert.Equal(TypeOwnerKind.Runtime, typeOwner.Kind);
        Assert.NotNull(recordedConformance);
        Assert.Equal(TypeOwnerKind.ThirdPartyPackage, recordedConformance!.Value.Kind);
    }

    [Fact]
    public void ConformanceOwner_StripsGenericArguments()
    {
        var expected = new TypeOwner
        {
            Kind = TypeOwnerKind.ThirdPartyPackage,
            PackageId = "Contoso.Collections",
        };
        TypeOwnerRegistry.RegisterConformanceOwner(
            "Swift.Array",
            "Contoso.BulkEncodable",
            expected);

        var owner = TypeOwnerRegistry.TryGetConformanceOwner(
            "Swift.Array<Int>",
            "Contoso.BulkEncodable");

        Assert.NotNull(owner);
        Assert.Equal(expected, owner!.Value);
    }

    [Fact]
    public void ConformanceOwner_StripsGenericArguments_OnProtocolToo()
    {
        // The registrar strips both tuple elements, so a lookup using a generic-instantiated
        // protocol identity must also resolve to the registered owner.
        var expected = new TypeOwner
        {
            Kind = TypeOwnerKind.ThirdPartyPackage,
            PackageId = "Contoso.Collections",
        };
        TypeOwnerRegistry.RegisterConformanceOwner(
            "Swift.Array",
            "Contoso.BulkEncodable",
            expected);

        var owner = TypeOwnerRegistry.TryGetConformanceOwner(
            "Swift.Array<Int>",
            "Contoso.BulkEncodable<Stripe.Customer>");

        Assert.NotNull(owner);
        Assert.Equal(expected, owner!.Value);
    }

    // ---- TryGetOverride + input validation ----------------------------------------

    [Fact]
    public void TryGetOverride_ReturnsTrue_ForStdlibCanonical()
    {
        Assert.True(TypeOwnerRegistry.TryGetOverride("Swift.String", out var owner));
        Assert.Equal(TypeOwnerKind.Runtime, owner.Kind);
    }

    [Fact]
    public void TryGetOverride_ReturnsTrue_ForSwiftUIText()
    {
        Assert.True(TypeOwnerRegistry.TryGetOverride("SwiftUI.Text", out var owner));
        Assert.Equal(TypeOwnerKind.AppleSupplement, owner.Kind);
        Assert.Equal(TypeOwnerRegistry.AppleSupplementPackageId, owner.PackageId);
    }

    [Fact]
    public void TryGetOverride_ReturnsFalse_ForUnregisteredType()
    {
        Assert.False(TypeOwnerRegistry.TryGetOverride("Foundation.BrandNewType", out _));
    }

    [Fact]
    public void TryGetOverride_ReturnsFalse_ForMovedCanonical()
    {
        // Foundation.Date and Foundation.Measurement used to be in s_legacyRuntimeCanonicals
        // but are now served by the Foundation module default, not an override.
        Assert.False(TypeOwnerRegistry.TryGetOverride("Foundation.Date", out _));
        Assert.False(TypeOwnerRegistry.TryGetOverride("Foundation.Measurement<Foundation.UnitLength>", out _));
    }

    [Fact]
    public void Resolve_NullOrEmptyIdentity_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TypeOwnerRegistry.Resolve(null!));
        Assert.Throws<ArgumentException>(() => TypeOwnerRegistry.Resolve(""));
    }

    // ---- v1/v2 coexistence guard -------------------------------------------------

    [Fact]
    public void RegisterPerTypeOverride_ConflictingPackageId_Throws()
    {
        // Simulates a consumer graph that resolves both SwiftBindings.Apple and
        // SwiftBindings.Apple.v2 — the second [ModuleInitializer] must not silently overwrite.
        var v1 = new TypeOwner
        {
            Kind = TypeOwnerKind.AppleSupplement,
            PackageId = "SwiftBindings.Apple",
        };
        var v2 = new TypeOwner
        {
            Kind = TypeOwnerKind.AppleSupplement,
            PackageId = "SwiftBindings.Apple.v2",
        };
        TypeOwnerRegistry.RegisterPerTypeOverride("Foundation.SomeNewType", v1);

        var ex = Assert.Throws<InvalidOperationException>(
            () => TypeOwnerRegistry.RegisterPerTypeOverride("Foundation.SomeNewType", v2));

        Assert.Contains("SwiftBindings.Apple", ex.Message);
        Assert.Contains("SwiftBindings.Apple.v2", ex.Message);
        Assert.Contains("Foundation.SomeNewType", ex.Message);
    }

    [Fact]
    public void RegisterPerTypeOverride_SamePackageId_IsIdempotent()
    {
        // Re-registration by the same package (e.g. duplicate module-init path) must not throw.
        var owner = new TypeOwner
        {
            Kind = TypeOwnerKind.AppleSupplement,
            PackageId = "SwiftBindings.Apple",
        };
        TypeOwnerRegistry.RegisterPerTypeOverride("Foundation.SomeNewType", owner);
        TypeOwnerRegistry.RegisterPerTypeOverride("Foundation.SomeNewType", owner);

        var resolved = TypeOwnerRegistry.Resolve("Foundation.SomeNewType");
        Assert.Equal("SwiftBindings.Apple", resolved.PackageId);
    }

    [Fact]
    public void GetRegisteredAppleModules_IncludesKnownFoundationAndCryptoKit()
    {
        var modules = TypeOwnerRegistry.GetRegisteredAppleModules();

        Assert.Contains("Foundation", modules);
        Assert.Contains("CryptoKit", modules);
        Assert.Contains("WeatherKit", modules);
    }
}

[CollectionDefinition(nameof(TypeOwnerRegistryCollection))]
public class TypeOwnerRegistryCollection
{
    // Marker class. Forces TypeOwnerRegistryTests to run in a dedicated xunit collection so
    // the process-global mutable state in TypeOwnerRegistry doesn't race with other tests
    // that (today or in the future) also exercise it.
}
