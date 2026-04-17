// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Swift.Runtime;

/// <summary>
/// Identifies which distribution channel owns the managed projection of a Swift type.
/// </summary>
/// <remarks>
/// Drives the generator's decision of whether to emit a reference to an already-shipped
/// package, pull a type from the ObjC workload, or emit the type locally in the consumer
/// assembly. See <c>src/docs/apple-swift-types-architecture.md</c> §"Resolved questions"
/// Q5 for the full resolution model.
/// </remarks>
public enum TypeOwnerKind
{
    /// <summary>Owned by the <c>SwiftBindings.Runtime</c> package (legacy canonical types).</summary>
    Runtime,

    /// <summary>Owned by the Apple supplement package (<c>SwiftBindings.Apple</c>).</summary>
    AppleSupplement,

    /// <summary>Owned by a generated third-party binding package (e.g. <c>Stripe.Swift.iOS</c>).</summary>
    ThirdPartyPackage,

    /// <summary>Swift standard-library type; lives inside <c>SwiftBindings.Runtime</c> today.</summary>
    SwiftStdlib,

    /// <summary>Projected onto a type exposed by the .NET Apple ObjC workload (e.g. <c>global::Foundation.NSDate</c>).</summary>
    ObjCWorkload,

    /// <summary>Type is emitted in the currently-generating consumer assembly (no external package needed).</summary>
    LocalModule,

    /// <summary>Type has no known owner; callers should skip members that reference it.</summary>
    Unsupported,
}

/// <summary>
/// Describes the package (and, when relevant, managed projection target) that owns a Swift type.
/// </summary>
public readonly record struct TypeOwner
{
    /// <summary>The kind of owner.</summary>
    public TypeOwnerKind Kind { get; init; }

    /// <summary>
    /// NuGet package id that owns the managed projection. Empty string for
    /// <see cref="TypeOwnerKind.LocalModule"/> and <see cref="TypeOwnerKind.Unsupported"/>.
    /// </summary>
    public string PackageId { get; init; }

    /// <summary>
    /// Swift module the type originates from, if known (e.g. <c>Foundation</c>).
    /// Informational only; resolution keys off the full Swift identity, not this field.
    /// </summary>
    public string? ModuleName { get; init; }

    /// <summary>
    /// Fully-qualified managed projection target (e.g. <c>global::Foundation.NSDate</c>).
    /// Populated for <see cref="TypeOwnerKind.ObjCWorkload"/> and optionally for per-type
    /// overrides that pin a specific projection. Null for owners whose projection is
    /// synthesised by the generator from the Swift identity alone.
    /// </summary>
    public string? ProjectedTypeName { get; init; }

    /// <summary>Pre-built owner value for the Runtime package.</summary>
    public static TypeOwner Runtime { get; } = new()
    {
        Kind = TypeOwnerKind.Runtime,
        PackageId = TypeOwnerRegistry.RuntimePackageId,
    };

    /// <summary>Pre-built owner value for the Apple supplement package.</summary>
    public static TypeOwner AppleSupplement { get; } = new()
    {
        Kind = TypeOwnerKind.AppleSupplement,
        PackageId = TypeOwnerRegistry.AppleSupplementPackageId,
    };

    /// <summary>Pre-built owner value indicating the type is emitted locally.</summary>
    public static TypeOwner Local { get; } = new()
    {
        Kind = TypeOwnerKind.LocalModule,
        PackageId = string.Empty,
    };

    /// <summary>Pre-built owner value indicating the type is not supported by any owner.</summary>
    public static TypeOwner Unsupported { get; } = new()
    {
        Kind = TypeOwnerKind.Unsupported,
        PackageId = string.Empty,
    };
}

/// <summary>
/// Central registry mapping Swift type identities to the package that owns their managed
/// projection. See <c>src/docs/apple-swift-types-architecture.md</c> §"Decision summary"
/// item 6 and §"Implementation specifics" item 7 for the authoritative resolver order.
/// </summary>
/// <remarks>
/// <para>Resolution precedence (first match wins):</para>
/// <list type="number">
///   <item>Per-type owner override (legacy canonical types such as <c>Foundation.Date</c>).</item>
///   <item>Swift standard-library known type.</item>
///   <item>ObjC workload projection (e.g. <c>Foundation.NSDate</c> -&gt; <c>global::Foundation.NSDate</c>).</item>
///   <item>Module default — Apple Swift modules point at the Apple supplement;
///         third-party modules point at their generated binding package.</item>
///   <item>Same-module type currently being generated → emit locally.</item>
///   <item>Unsupported — the member should be skipped.</item>
/// </list>
/// <para>Cross-module protocol conformances (architecture doc §Q10 item 3) are tracked
/// separately via <see cref="RegisterConformanceOwner"/> because a type from module A
/// may conform to a protocol from module B with the conformance itself owned by a
/// third party — type ownership alone cannot answer that question.</para>
/// </remarks>
public static class TypeOwnerRegistry
{
    /// <summary>NuGet package id of the runtime package.</summary>
    public const string RuntimePackageId = "SwiftBindings.Runtime";

    /// <summary>NuGet package id of the Apple supplement package.</summary>
    public const string AppleSupplementPackageId = "SwiftBindings.Apple";

    // Level 1: Per-type overrides. Keyed on generic-stripped Swift identity so that
    // both "Foundation.Measurement" and "Foundation.Measurement<UnitType>" resolve.
    private static readonly ConcurrentDictionary<string, TypeOwner> s_overrides =
        new(StringComparer.Ordinal);

    // Level 2: Swift stdlib. Stored as a set; resolved to a Runtime-kind TypeOwner
    // tagged with TypeOwnerKind.SwiftStdlib.
    private static readonly ConcurrentDictionary<string, bool> s_stdlibTypes =
        new(StringComparer.Ordinal);

    // Level 3: ObjC workload projections. Value holds the projected managed type.
    private static readonly ConcurrentDictionary<string, TypeOwner> s_objcProjections =
        new(StringComparer.Ordinal);

    // Level 4: Module-default resolution. Apple modules resolve to AppleSupplement,
    // third-party modules to their registered package id.
    private static readonly ConcurrentDictionary<string, bool> s_appleModules =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> s_thirdPartyModules =
        new(StringComparer.Ordinal);

    // Cross-module protocol conformance owners. Keyed on (type Swift identity,
    // protocol Swift identity). See architecture doc §Q10 item 3.
    private static readonly ConcurrentDictionary<(string Type, string Protocol), TypeOwner> s_conformanceOwners =
        new();

    static TypeOwnerRegistry()
    {
        SeedLegacyCanonicals();
        SeedDefaultAppleModules();
    }

    /// <summary>
    /// Resolves the owner of a Swift type by its module-qualified identity.
    /// </summary>
    /// <param name="swiftIdentity">
    /// The module-qualified Swift identity, e.g. <c>Foundation.Locale.Language</c> or
    /// <c>Foundation.Measurement&lt;UnitType&gt;</c>. Generic arguments are stripped before
    /// per-type override lookup so the same record handles both the unbound and bound forms.
    /// </param>
    /// <param name="currentlyGeneratingModule">
    /// Optional Swift module name of the consumer assembly currently being generated.
    /// When non-null and the type's declaring module matches, resolution falls through to
    /// <see cref="TypeOwner.Local"/> after the module-default step.
    /// </param>
    public static TypeOwner Resolve(string swiftIdentity, string? currentlyGeneratingModule = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(swiftIdentity);

        // Level 1: per-type overrides. Try exact first, then generic-stripped form so
        // the same override handles "Foundation.Measurement" and "Foundation.Measurement<T>".
        if (s_overrides.TryGetValue(swiftIdentity, out var exact))
        {
            return exact;
        }
        var stripped = StripGenericArguments(swiftIdentity);
        if (!ReferenceEquals(stripped, swiftIdentity) &&
            s_overrides.TryGetValue(stripped, out var generic))
        {
            return generic;
        }

        // Level 2: Swift stdlib — owned by Runtime but tagged distinctly.
        if (s_stdlibTypes.ContainsKey(swiftIdentity) || s_stdlibTypes.ContainsKey(stripped))
        {
            return new TypeOwner
            {
                Kind = TypeOwnerKind.SwiftStdlib,
                PackageId = RuntimePackageId,
                ModuleName = GetModuleName(swiftIdentity),
            };
        }

        // Level 3: ObjC workload projection. Same exact-then-stripped pattern as levels 1–2
        // so a projection registered for the unbound stem also covers generic instantiations.
        if (s_objcProjections.TryGetValue(swiftIdentity, out var objc))
        {
            return objc;
        }
        if (!ReferenceEquals(stripped, swiftIdentity) &&
            s_objcProjections.TryGetValue(stripped, out var objcStripped))
        {
            return objcStripped;
        }

        // Level 4: module-default resolution.
        var moduleName = GetModuleName(swiftIdentity);
        if (moduleName is not null)
        {
            if (s_appleModules.ContainsKey(moduleName))
            {
                return new TypeOwner
                {
                    Kind = TypeOwnerKind.AppleSupplement,
                    PackageId = AppleSupplementPackageId,
                    ModuleName = moduleName,
                };
            }
            if (s_thirdPartyModules.TryGetValue(moduleName, out var packageId))
            {
                return new TypeOwner
                {
                    Kind = TypeOwnerKind.ThirdPartyPackage,
                    PackageId = packageId,
                    ModuleName = moduleName,
                };
            }
        }

        // Level 5: same-module type being generated → local. Only reached when no
        // module default matched; a type whose module is registered as Apple/third-party
        // resolves to that package even when also being generated (the supplement is
        // itself the canonical owner of its types and keeps identity across consumers).
        if (currentlyGeneratingModule is not null &&
            moduleName is not null &&
            string.Equals(currentlyGeneratingModule, moduleName, StringComparison.Ordinal))
        {
            return TypeOwner.Local with { ModuleName = moduleName };
        }

        // Level 6: unsupported.
        return TypeOwner.Unsupported with { ModuleName = moduleName };
    }

    /// <summary>Attempts a per-type override lookup without running the full resolver.</summary>
    public static bool TryGetOverride(string swiftIdentity, out TypeOwner owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(swiftIdentity);

        if (s_overrides.TryGetValue(swiftIdentity, out owner))
        {
            return true;
        }
        var stripped = StripGenericArguments(swiftIdentity);
        if (!ReferenceEquals(stripped, swiftIdentity) &&
            s_overrides.TryGetValue(stripped, out owner))
        {
            return true;
        }
        owner = default;
        return false;
    }

    /// <summary>Registers a per-type override. Keyed on generic-stripped Swift identity.</summary>
    public static void RegisterPerTypeOverride(string swiftIdentity, TypeOwner owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(swiftIdentity);
        s_overrides[StripGenericArguments(swiftIdentity)] = owner;
    }

    /// <summary>Registers a Swift stdlib type. Stdlib types resolve to <see cref="TypeOwnerKind.SwiftStdlib"/>.</summary>
    public static void RegisterSwiftStdlibType(string swiftIdentity)
    {
        ArgumentException.ThrowIfNullOrEmpty(swiftIdentity);
        s_stdlibTypes[StripGenericArguments(swiftIdentity)] = true;
    }

    /// <summary>
    /// Registers an ObjC workload projection — when looked up, the given Swift identity
    /// resolves to <paramref name="projectedTypeName"/> in the Apple ObjC workload.
    /// </summary>
    public static void RegisterObjCWorkloadProjection(
        string swiftIdentity,
        string projectedTypeName,
        string? moduleName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(swiftIdentity);
        ArgumentException.ThrowIfNullOrEmpty(projectedTypeName);
        s_objcProjections[swiftIdentity] = new TypeOwner
        {
            Kind = TypeOwnerKind.ObjCWorkload,
            PackageId = string.Empty,
            ModuleName = moduleName ?? GetModuleName(swiftIdentity),
            ProjectedTypeName = projectedTypeName,
        };
    }

    /// <summary>Marks a Swift module as an Apple SDK module — its types default to the Apple supplement.</summary>
    public static void RegisterAppleModule(string module)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        s_appleModules[module] = true;
    }

    /// <summary>
    /// Marks a Swift module as a third-party binding module, owned by the given generated package.
    /// </summary>
    public static void RegisterThirdPartyModule(string module, string packageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        s_thirdPartyModules[module] = packageId;
    }

    /// <summary>
    /// Records the owner of a specific type→protocol conformance. Conformance ownership is
    /// distinct from type ownership: a type from module A may conform to a protocol from
    /// module B with the conformance itself published by a third party (architecture doc §Q10 item 3).
    /// </summary>
    public static void RegisterConformanceOwner(
        string typeSwiftIdentity,
        string protocolSwiftIdentity,
        TypeOwner owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeSwiftIdentity);
        ArgumentException.ThrowIfNullOrEmpty(protocolSwiftIdentity);
        var key = (StripGenericArguments(typeSwiftIdentity), StripGenericArguments(protocolSwiftIdentity));
        s_conformanceOwners[key] = owner;
    }

    /// <summary>Looks up the owner of a specific type→protocol conformance, or returns null.</summary>
    public static TypeOwner? TryGetConformanceOwner(string typeSwiftIdentity, string protocolSwiftIdentity)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeSwiftIdentity);
        ArgumentException.ThrowIfNullOrEmpty(protocolSwiftIdentity);
        var key = (StripGenericArguments(typeSwiftIdentity), StripGenericArguments(protocolSwiftIdentity));
        return s_conformanceOwners.TryGetValue(key, out var owner) ? owner : null;
    }

    /// <summary>
    /// Returns every registered Apple module name. Intended for tooling introspection and tests.
    /// </summary>
    public static IReadOnlyCollection<string> GetRegisteredAppleModules() =>
        s_appleModules.Keys.ToArray();

    /// <summary>
    /// Test-only reset of all mutable state back to the seeded defaults. Keeps tests isolated
    /// when they mutate the registry (e.g. registering a third-party module for one case).
    /// </summary>
    internal static void ResetForTests()
    {
        s_overrides.Clear();
        s_stdlibTypes.Clear();
        s_objcProjections.Clear();
        s_appleModules.Clear();
        s_thirdPartyModules.Clear();
        s_conformanceOwners.Clear();
        SeedLegacyCanonicals();
        SeedDefaultAppleModules();
    }

    private static void SeedLegacyCanonicals()
    {
        // Pinned to SwiftBindings.Runtime regardless of their declaring Swift module.
        // See apple-swift-types-architecture.md §"Decision summary" item 2: these hand-rolled
        // canonical types stay in Runtime even though their modules (Foundation, ManagedSettings,
        // SwiftUI) default to the Apple supplement.
        foreach (var identity in s_legacyRuntimeCanonicals)
        {
            s_overrides[identity] = TypeOwner.Runtime with { ModuleName = GetModuleName(identity) };
        }
    }

    private static void SeedDefaultAppleModules()
    {
        foreach (var module in s_defaultAppleModules)
        {
            s_appleModules[module] = true;
        }
    }

    // Legacy canonical types (architecture doc §"Why hand-rolling won't scale" table + §"Decision summary" item 6).
    // Generic types are listed by stem only — the resolver strips "<...>" before lookup.
    private static readonly string[] s_legacyRuntimeCanonicals =
    {
        "Foundation.Date",
        "Foundation.Data",
        "Foundation.URL",
        "Foundation.Decimal",
        "Foundation.Measurement",
        "Foundation.AnyError",
        "ManagedSettings.Token",
        "SwiftUI.Text",
    };

    // Default set of Apple SDK Swift module names. Generators can extend this at startup
    // via RegisterAppleModule(...) — e.g. loading from apple-frameworks.json on the
    // generator side. Kept intentionally broad: any module here defaults to the Apple
    // supplement unless an override, stdlib, or ObjC projection claims the type first.
    private static readonly string[] s_defaultAppleModules =
    {
        "Accessibility",
        "AppKit",
        "AppIntents",
        "ARKit",
        "AuthenticationServices",
        "AVFAudio",
        "AVFoundation",
        "AVKit",
        "BackgroundTasks",
        "CallKit",
        "CarPlay",
        "CloudKit",
        "Combine",
        "Contacts",
        "ContactsUI",
        "CoreBluetooth",
        "CoreData",
        "CoreFoundation",
        "CoreGraphics",
        "CoreHaptics",
        "CoreImage",
        "CoreLocation",
        "CoreML",
        "CoreMedia",
        "CoreMIDI",
        "CoreMotion",
        "CoreServices",
        "CoreTelephony",
        "CoreText",
        "CoreVideo",
        "CryptoKit",
        "DeveloperToolsSupport",
        "DeviceActivity",
        "DeviceDiscoveryExtension",
        "EventKit",
        "ExternalAccessory",
        "FamilyControls",
        "FileProvider",
        "Foundation",
        "GameController",
        "GameKit",
        "HealthKit",
        "HomeKit",
        "IdentityLookup",
        "IOKit",
        "LiveCommunicationKit",
        "LocalAuthentication",
        "ManagedSettings",
        "MapKit",
        "Matter",
        "MediaPlayer",
        "MessageUI",
        "Metal",
        "MetalKit",
        "MetricKit",
        "MusicKit",
        "NaturalLanguage",
        "NearbyInteraction",
        "Network",
        "NetworkExtension",
        "NotificationCenter",
        "Observation",
        "OSLog",
        "PassKit",
        "PDFKit",
        "PhotosUI",
        "Photos",
        "PreviewsObservation",
        "ProximityReader",
        "PushKit",
        "QuartzCore",
        "ReplayKit",
        "SafariServices",
        "SceneKit",
        "ScreenTime",
        "SensorKit",
        "Social",
        "SoundAnalysis",
        "Speech",
        "SpriteKit",
        "StoreKit",
        "Symbols",
        "SwiftData",
        "SwiftUI",
        "SystemConfiguration",
        "TipKit",
        "Translation",
        "UIKit",
        "UniformTypeIdentifiers",
        "UserNotifications",
        "UserNotificationsUI",
        "VideoToolbox",
        "Vision",
        "VisionKit",
        "WatchConnectivity",
        "WatchKit",
        "WeatherKit",
        "WebKit",
        "WidgetKit",
        "WorkoutKit",
    };

    private static string? GetModuleName(string swiftIdentity)
    {
        if (string.IsNullOrEmpty(swiftIdentity))
        {
            return null;
        }
        var dot = swiftIdentity.IndexOf('.');
        return dot < 0 ? null : swiftIdentity[..dot];
    }

    // Callers pass canonical module-qualified Swift identities (e.g. "Foundation.Measurement<T>").
    // Anything past the first '<' is treated as generic arguments and dropped; malformed inputs
    // are tolerated and resolve downstream as unsupported rather than throwing.
    private static string StripGenericArguments(string swiftIdentity)
    {
        var angle = swiftIdentity.IndexOf('<');
        return angle < 0 ? swiftIdentity : swiftIdentity[..angle];
    }
}
