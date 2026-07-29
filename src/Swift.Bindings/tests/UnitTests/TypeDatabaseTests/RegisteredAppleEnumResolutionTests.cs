// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Coverage for the value-type record built for Apple framework types the registry lists as value
/// types AND describes as integer-backed enums.
/// <para>
/// Listing a name as a value type withholds the synthetic ObjC bridged-class record. That is right
/// for a shape no Handle-bearing class can stand in for, but on its own it leaves the name with no
/// record at all, so every member mentioning it is skipped as unresolvable — even a plain NS_ENUM
/// that crosses the boundary as its raw integer. These tests pin both halves: a described name
/// resolves as a raw-value enum, and an undescribed one (or one the platform binding does not
/// declare as an enum) stays exactly as fail-closed as before.
/// </para>
/// The platform surface is always hand-built here, so the assertions never depend on which Apple
/// workload happens to be installed.
/// </summary>
public class RegisteredAppleEnumResolutionTests
{
    // The registered Apple value types that carry a "kind": "enum" description, with the .NET
    // identity the platform binding actually declares for each. The nested Swift spellings resolve
    // through the registry's hand-authored remap (UIAccessibilityCustomRotor.Direction is flattened
    // to UIAccessibilityCustomRotorDirection; WKWebView.FullscreenState is spelled WKFullscreenState),
    // which is why the remapped name — not the Swift leaf — is what the surface must declare.
    public static TheoryData<string, string, string, string> DescribedEnums() => new()
    {
        { "PassKit.PKPaymentButtonType", "PassKit", "PKPaymentButtonType", "Int" },
        { "PassKit.PKPaymentButtonStyle", "PassKit", "PKPaymentButtonStyle", "Int" },
        { "HealthKit.HKWorkoutActivityType", "HealthKit", "HKWorkoutActivityType", "UInt" },
        { "HealthKit.HKWorkoutSessionLocationType", "HealthKit", "HKWorkoutSessionLocationType", "Int" },
        { "HealthKit.HKWorkoutSwimmingLocationType", "HealthKit", "HKWorkoutSwimmingLocationType", "Int" },
        { "UIKit.UIAccessibilityCustomRotor.Direction", "UIKit", "UIAccessibilityCustomRotorDirection", "Int" },
        { "WebKit.WKWebView.FullscreenState", "WebKit", "WKFullscreenState", "Int" },
    };

    [Theory]
    [MemberData(nameof(DescribedEnums))]
    public void DescribedEnum_ResolvesAsRawValueEnumValueType(
        string swiftName, string netNamespace, string netName, string rawValueType)
    {
        using var surface = Surface((netNamespace, netName, AppleTypeSurfaceKind.Enum, rawValueType, false));

        var resolved = new TypeDatabase().TryGetTypeRecord(new NamedTypeSpec(swiftName), out var record);

        Assert.True(resolved, $"'{swiftName}' must resolve once the registry describes it as an enum");
        Assert.Equal(TypeRecordKind.Enum, record!.Kind);
        Assert.True((record.Flags & TypeRecordFlags.SimpleEnum) != 0);
        Assert.Equal(0, (int)(record.Flags & TypeRecordFlags.ObjCBridged));
        Assert.Equal(netNamespace, record.CSharpTypeName.Namespace);
        Assert.Equal(netName, record.CSharpTypeName.Name);
        Assert.Equal(rawValueType, record.RawValueTypeName);
    }

    [Fact]
    public void DescribedEnum_FlagsBinding_ProjectsAsOptionSet()
    {
        // An NS_OPTIONS bitmask reaches .NET as a [Flags] enum; the record must carry OptionSet so
        // reconstruction uses the non-failable init. The flag is read off the reflected binding
        // rather than declared in the registry, so it cannot drift from the shipped surface.
        using var surface = Surface(("PassKit", "PKPaymentButtonType", AppleTypeSurfaceKind.Enum, "UInt", true));

        Assert.True(new TypeDatabase().TryGetTypeRecord(
            new NamedTypeSpec("PassKit.PKPaymentButtonType"), out var record));
        Assert.True((record!.Flags & TypeRecordFlags.OptionSet) != 0);
    }

    [Theory]
    // The Stripe/adyen payment-network row: an NS_EXTENSIBLE_STRING_ENUM group of NSString
    // constants. The .NET binding still projects it as a C# enum, so the surface alone would happily
    // "confirm" it — only the absence of a registry description keeps it out.
    [InlineData("PassKit.PKPaymentNetwork", "PassKit", "PKPaymentNetwork")]
    // The same trap in AVFoundation: an NS_TYPED_ENUM whose Swift side is a String-wrapping struct
    // with no raw integer to marshal.
    [InlineData("AVFoundation.AVMetadataObject.ObjectType", "AVFoundation", "AVMetadataObjectType")]
    public void UndescribedValueType_DeclaredAsEnumBySurface_StaysFailClosed(
        string swiftName, string netNamespace, string netName)
    {
        using var surface = Surface((netNamespace, netName, AppleTypeSurfaceKind.Enum, "Int", false));

        var resolved = new TypeDatabase().TryGetTypeRecord(new NamedTypeSpec(swiftName), out _);

        Assert.False(resolved,
            $"'{swiftName}' has no registry description of its Swift shape, so a C# enum on the "
            + "binding side is not evidence the Swift side marshals as a raw integer");
    }

    [Theory]
    // Swift-only nested value types with no platform-binding counterpart at all.
    [InlineData("Foundation.Decimal.FormatStyle.Currency")]
    [InlineData("Foundation.Date.ComponentsFormatStyle")]
    // A real ObjC class the registry lists to keep it off the weak-prefix bridge path.
    [InlineData("PhotosUI.PHPickerResult")]
    public void UndescribedValueType_AbsentFromSurface_StaysFailClosed(string swiftName)
    {
        using var surface = Surface(("PassKit", "PKPaymentButtonType", AppleTypeSurfaceKind.Enum, "Int", false));

        Assert.False(new TypeDatabase().TryGetTypeRecord(new NamedTypeSpec(swiftName), out _));
    }

    [Theory]
    // Not an Apple auto-bridge module at all.
    [InlineData("SwiftBindingsTestLib.SomeType")]
    // An Apple module type the registry does not describe as an integer enum.
    [InlineData("PassKit.PKPaymentNetwork")]
    public void UndescribedName_NeverResolvesThePlatformSurface(string swiftName)
    {
        // Resolving the surface can mean building the reference-pack index on first touch. A name
        // the registry gates already reject must not pay that cost — the provider stays uninvoked.
        var record = TypeDatabaseExtensions.TryCreateRegisteredAppleEnumRecord(
            SwiftTypeName.FromModuleQualifiedName(swiftName),
            usr: null,
            () => throw new InvalidOperationException("surface index resolved for an undescribed name"));

        Assert.Null(record);
    }

    [Fact]
    public void DescribedEnum_SurfaceUnavailable_StaysFailClosed()
    {
        // No installed reference assembly means no way to learn the .NET spelling or raw-value
        // width. Fabricating them would emit a reference that may not compile, so the description
        // alone recovers nothing.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(index: null);

        Assert.False(new TypeDatabase().TryGetTypeRecord(
            new NamedTypeSpec("PassKit.PKPaymentButtonType"), out _));
    }

    [Theory]
    [InlineData(nameof(AppleTypeSurfaceKind.Class))]
    [InlineData(nameof(AppleTypeSurfaceKind.Struct))]
    [InlineData(nameof(AppleTypeSurfaceKind.StaticConstants))]
    [InlineData(nameof(AppleTypeSurfaceKind.Protocol))]
    public void DescribedEnum_SurfaceDisagreesOnShape_StaysFailClosed(string kindName)
    {
        var kind = Enum.Parse<AppleTypeSurfaceKind>(kindName);
        // The binding is the authority on what it ships. A description that the surface contradicts
        // is a stale registry entry, and guessing past it would emit an enum reference to a type
        // that is not one.
        using var surface = Surface(("PassKit", "PKPaymentButtonType", kind, null, false));

        Assert.False(new TypeDatabase().TryGetTypeRecord(
            new NamedTypeSpec("PassKit.PKPaymentButtonType"), out _));
    }

    [Fact]
    public void DescribedEnum_DoesNotOverrideDatabaseEntry()
    {
        // A hand-authored database record still wins: the strategy runs after the database cascade,
        // so an explicit XML entry keeps its projection.
        using var surface = Surface(("PassKit", "PKPaymentButtonType", AppleTypeSurfaceKind.Enum, "Int", false));
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("PassKit", "/fake/path");
        var swiftName = SwiftTypeName.FromModuleQualifiedName("PassKit.PKPaymentButtonType");
        module.RegisterType(swiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("HandRolled", "PaymentButtonKind"),
            SwiftTypeName = swiftName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        db.AddModuleDatabase(module);

        Assert.True(db.TryGetTypeRecord(new NamedTypeSpec("PassKit.PKPaymentButtonType"), out var record));
        Assert.Equal("HandRolled.PaymentButtonKind", record!.CSharpTypeName.FullyQualifiedName);
    }

    [Theory]
    [MemberData(nameof(DescribedEnums))]
    public void DescribedEnum_IsStillWithheldFromObjCBridging(
        string swiftName, string netNamespace, string netName, string rawValueType)
    {
        // The description adds a value-type record; it must not re-open the ObjC class bridge for a
        // name that is not a class.
        _ = netNamespace;
        _ = netName;
        _ = rawValueType;
        Assert.True(AppleFrameworkRegistry.IsKnownValueType(swiftName));
        Assert.True(AppleFrameworkRegistry.IsIntegerEnumValueType(swiftName));
        Assert.False(TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec(swiftName)));
    }

    [Theory]
    [InlineData("PassKit.PKPaymentNetwork")]
    [InlineData("AVFoundation.AVMetadataObject.ObjectType")]
    [InlineData("PhotosUI.PHPickerResult")]
    [InlineData("Foundation.Decimal.FormatStyle.Currency")]
    public void BareValueTypeEntry_IsListedButUndescribed(string swiftName)
    {
        // The bare string form keeps its historical meaning — "not an ObjC class" and nothing more.
        Assert.True(AppleFrameworkRegistry.IsKnownValueType(swiftName));
        Assert.False(AppleFrameworkRegistry.IsIntegerEnumValueType(swiftName));
    }

    [Theory]
    [MemberData(nameof(DescribedEnums))]
    public void DescribedEnum_ResolvesOnTheRawNameSurfaceToo(
        string swiftName, string netNamespace, string netName, string rawValueType)
    {
        // Two lookup surfaces reach the same records: the NamedTypeSpec strategy chain and the raw
        // SwiftTypeName entry point. Leaf lookups inside containers take the raw one, so a record
        // that exists on only one surface still loses every Optional/array position.
        using var surface = Surface((netNamespace, netName, AppleTypeSurfaceKind.Enum, rawValueType, false));
        var db = new TypeDatabase();

        Assert.True(db.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName(swiftName), out var byName));
        Assert.True(db.TryGetTypeRecord(new NamedTypeSpec(swiftName), out var bySpec));

        Assert.Equal(TypeRecordKind.Enum, byName!.Kind);
        Assert.True((byName.Flags & TypeRecordFlags.SimpleEnum) != 0);
        Assert.Equal(bySpec!.CSharpTypeName.FullyQualifiedName, byName.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(bySpec.RawValueTypeName, byName.RawValueTypeName);
        Assert.Equal(bySpec.Flags, byName.Flags);
    }

    [Theory]
    // Undescribed, though the binding does declare a C# enum for it.
    [InlineData("PassKit.PKPaymentNetwork", "PassKit", "PKPaymentNetwork", nameof(AppleTypeSurfaceKind.Enum))]
    // Described, but the binding declares something a raw integer cannot stand for.
    [InlineData("PassKit.PKPaymentButtonType", "PassKit", "PKPaymentButtonType", nameof(AppleTypeSurfaceKind.Class))]
    public void RawNameSurface_AppliesTheSameFailClosedRule(
        string swiftName, string netNamespace, string netName, string kindName)
    {
        using var surface = Surface((netNamespace, netName, Enum.Parse<AppleTypeSurfaceKind>(kindName), "Int", false));

        Assert.False(new TypeDatabase().TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName(swiftName), out _));
    }

    [Theory]
    [InlineData("Swift.Optional")]
    [InlineData("Swift.Array")]
    public void DescribedEnum_InsideAContainer_ProjectsAsARawValueEnum(string container)
    {
        // The container leaf resolves through the raw-name surface, so this is the shape that stayed
        // broken while only the strategy chain knew the record: the projection factory would find no
        // record and hand the whole container back unprojected.
        using var surface = Surface(("PassKit", "PKPaymentButtonType", AppleTypeSurfaceKind.Enum, "Int", false));

        var projection = new TypeProjectionFactory().Project(
            new NamedTypeSpec(container, new NamedTypeSpec("PassKit.PKPaymentButtonType")),
            new ProjectionContext { TypeDatabase = new TypeDatabase(), IsParameter = false });

        var leaf = Assert.IsType<SimpleEnumProjection>(InnerProjection(projection));
        Assert.Contains("PKPaymentButtonType", leaf.PublicType);
    }

    [Fact]
    public void UndescribedValueType_InsideAContainer_StaysUnprojected()
    {
        using var surface = Surface(("PassKit", "PKPaymentNetwork", AppleTypeSurfaceKind.Enum, "Int", false));

        var projection = new TypeProjectionFactory().Project(
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("PassKit.PKPaymentNetwork")),
            new ProjectionContext { TypeDatabase = new TypeDatabase(), IsParameter = false });

        Assert.Null(InnerProjection(projection) as SimpleEnumProjection);
    }

    [Theory]
    // A typo'd property name is the failure this rejects: it would otherwise parse as an object with
    // no kind and degrade to a bare entry, silently withholding the record the entry was added for.
    [InlineData("{ \"name\": \"PKPaymentButtonType\", \"knid\": \"enum\" }")]
    [InlineData("{ \"name\": \"PKPaymentButtonType\" }")]
    [InlineData("{ \"name\": \"PKPaymentButtonType\", \"kind\": \"\" }")]
    [InlineData("{ \"kind\": \"enum\" }")]
    [InlineData("42")]
    // The string form gets the same guard as the object form: an empty name would register an
    // unmatchable entry instead of failing the load.
    [InlineData("\"\"")]
    public void MalformedValueTypeEntry_FailsLoad(string entryJson)
    {
        Assert.ThrowsAny<Newtonsoft.Json.JsonException>(
            () => AppleFrameworkRegistry.ParseValueTypeEntry(entryJson));
    }

    [Theory]
    [InlineData("\"PKPaymentNetwork\"", "PKPaymentNetwork", null)]
    [InlineData("{ \"name\": \"PKPaymentButtonType\", \"kind\": \"enum\" }", "PKPaymentButtonType", "enum")]
    public void WellFormedValueTypeEntry_Loads(string entryJson, string name, string? kind)
    {
        var parsed = AppleFrameworkRegistry.ParseValueTypeEntry(entryJson);

        Assert.Equal(name, parsed.Name);
        Assert.Equal(kind, parsed.Kind);
    }

    // Unwraps whatever container projection the factory produced, so the assertions target the leaf
    // the record actually drives rather than the container spelling.
    private static ITypeProjection? InnerProjection(ITypeProjection? projection) => projection switch
    {
        OptionalProjection optional => optional.InnerProjection,
        ArrayProjection array => array.ElementProjection,
        _ => projection,
    };

    [Fact]
    public void ValueTypeKind_Null_DescribesNothing()
        => Assert.False(AppleFrameworkRegistry.DescribesIntegerEnum("SomeModule.SomeType", null));

    [Fact]
    public void ValueTypeKind_Enum_DescribesIntegerEnum()
        => Assert.True(AppleFrameworkRegistry.DescribesIntegerEnum("SomeModule.SomeType", "enum"));

    [Theory]
    [InlineData("struct")]
    [InlineData("Enum")]
    [InlineData("")]
    public void ValueTypeKind_Unrecognized_FailsLoad(string kind)
    {
        // A typo must not degrade to "described by nothing" — that is indistinguishable from a
        // deliberate bare entry and would silently leave the type unresolvable.
        var ex = Assert.Throws<InvalidOperationException>(
            () => AppleFrameworkRegistry.DescribesIntegerEnum("SomeModule.SomeType", kind));
        Assert.Contains("SomeModule.SomeType", ex.Message);
    }

    // A hand-built platform surface declaring exactly the given types — the stand-in for the
    // installed reference assembly, so resolution is deterministic without the Apple workload.
    private static IDisposable Surface(
        params (string Namespace, string Name, AppleTypeSurfaceKind Kind, string? Underlying, bool IsFlags)[] types)
    {
        var byFullName = new Dictionary<string, AppleTypeSurfaceEntry>(StringComparer.Ordinal);
        var byBareName = new Dictionary<string, AppleTypeSurfaceEntry>(StringComparer.Ordinal);
        foreach (var (ns, name, kind, underlying, isFlags) in types)
        {
            var entry = new AppleTypeSurfaceEntry(name, ns, kind, underlying, isFlags);
            byFullName[$"{ns}.{name}"] = entry;
            byBareName[name] = entry;
        }

        return AppleTypeSurfaceIndex.OverrideDefaultForTest(new AppleTypeSurfaceIndex(byFullName, byBareName));
    }
}
