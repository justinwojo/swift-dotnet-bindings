// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Coverage for the record built for an Apple framework type the registry describes as an
/// NSString-backed NS_STRING_ENUM / NS_TYPED_ENUM (<c>"kind": "stringEnum"</c>).
/// <para>
/// Swift imports one of these as a String-backed <c>RawRepresentable</c> newtype that bridges to
/// <c>NSString</c>, so it crosses the boundary as an ObjC object pointer and a collection of them
/// crosses as an NSArray/NSSet/NSDictionary. The platform binding, however, projects the constant
/// group as a C# <c>enum</c> with a sibling <c>{Name}Extensions</c> converter — an enum has no
/// <c>Handle</c>, so the two facts have to be reconciled at the marshalling boundary rather than by
/// pretending the enum is an NSObject.
/// </para>
/// The platform surface is always hand-built here, so the assertions never depend on which Apple
/// workload happens to be installed.
/// </summary>
public class RegisteredAppleTypedEnumResolutionTests
{
    private const string DescribedName = "Vision.VNBarcodeSymbology";
    private const string DescribedNamespace = "Vision";
    private const string DescribedLeaf = "VNBarcodeSymbology";

    [Fact]
    public void DescribedStringEnum_ResolvesAsAnNSStringBridgedRecord()
    {
        using var surface = TypedEnumSurface();

        Assert.True(new TypeDatabase().TryGetTypeRecord(new NamedTypeSpec(DescribedName), out var record));

        Assert.Equal(TypeRecordKind.Struct, record!.Kind);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridgeable) != 0);
        Assert.True((record.Flags & TypeRecordFlags.AppleTypedEnum) != 0);
        // The raw-integer arm must NOT be claimed: there is no raw value to truncate or reconstruct.
        Assert.Equal(0, (int)(record.Flags & TypeRecordFlags.SimpleEnum));
        Assert.Equal($"{DescribedNamespace}.{DescribedLeaf}", record.CSharpTypeName.FullyQualifiedName);
        Assert.Equal("Foundation.NSString", record.NativeTypeName!.FullyQualifiedName);
    }

    [Fact]
    public void DescribedStringEnum_ResolvesOnTheRawNameSurfaceToo()
    {
        // Two lookup surfaces reach the same records: the NamedTypeSpec strategy chain (which the
        // Swift-side @_cdecl mapper consults to decide the container bridges as an NSArray) and the
        // raw SwiftTypeName entry point (which the C# projection factory consults for a container's
        // leaf). A record only one of them knows about does not fail to compile — it emits a C#
        // signature and a Swift wrapper that disagree about the wire format.
        using var surface = TypedEnumSurface();
        var db = new TypeDatabase();

        Assert.True(db.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName(DescribedName), out var byName));
        Assert.True(db.TryGetTypeRecord(new NamedTypeSpec(DescribedName), out var bySpec));

        Assert.Equal(bySpec!.Flags, byName!.Flags);
        Assert.Equal(bySpec.Kind, byName.Kind);
        Assert.Equal(bySpec.CSharpTypeName.FullyQualifiedName, byName.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(bySpec.NativeTypeName!.FullyQualifiedName, byName.NativeTypeName!.FullyQualifiedName);
    }

    [Fact]
    public void DescribedStringEnum_WithoutTheExtensionsSibling_StaysFailClosed()
    {
        // The emitted C# reaches the backing NSString only through {Enum}Extensions. Without it
        // there is nothing to call, so a record here would emit code that cannot compile.
        using var surface = Surface((DescribedNamespace, DescribedLeaf, AppleTypeSurfaceKind.Enum, "Int", false));

        Assert.False(new TypeDatabase().TryGetTypeRecord(new NamedTypeSpec(DescribedName), out _));
    }

    [Theory]
    [InlineData(nameof(AppleTypeSurfaceKind.Class))]
    [InlineData(nameof(AppleTypeSurfaceKind.Struct))]
    [InlineData(nameof(AppleTypeSurfaceKind.Protocol))]
    [InlineData(nameof(AppleTypeSurfaceKind.StaticConstants))]
    public void DescribedStringEnum_SurfaceDisagreesOnShape_StaysFailClosed(string kindName)
    {
        // The binding is the authority on what it ships. A description the surface contradicts is a
        // stale registry entry, and guessing past it emits a reference to a type that is not one.
        var kind = Enum.Parse<AppleTypeSurfaceKind>(kindName);
        using var surface = Surface(
            (DescribedNamespace, DescribedLeaf, kind, null, false),
            (DescribedNamespace, DescribedLeaf + "Extensions", AppleTypeSurfaceKind.StaticConstants, null, false));

        Assert.False(new TypeDatabase().TryGetTypeRecord(new NamedTypeSpec(DescribedName), out _));
    }

    [Fact]
    public void DescribedStringEnum_ExtensionsSiblingIsNotAStaticClass_StaysFailClosed()
    {
        // Microsoft's generator emits the converter as a static class, which the index classifies as
        // StaticConstants. Anything else with that name is a different type, not the converter.
        using var surface = Surface(
            (DescribedNamespace, DescribedLeaf, AppleTypeSurfaceKind.Enum, "Int", false),
            (DescribedNamespace, DescribedLeaf + "Extensions", AppleTypeSurfaceKind.Class, null, false));

        Assert.False(new TypeDatabase().TryGetTypeRecord(new NamedTypeSpec(DescribedName), out _));
    }

    [Fact]
    public void DescribedStringEnum_SurfaceUnavailable_StaysFailClosed()
    {
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(index: null);

        Assert.False(new TypeDatabase().TryGetTypeRecord(new NamedTypeSpec(DescribedName), out _));
    }

    [Fact]
    public void UndescribedName_NeverResolvesThePlatformSurface()
    {
        // Building the reference-pack index is expensive; a name the registry gates already reject
        // must not pay for it.
        var record = TypeDatabaseExtensions.TryCreateRegisteredAppleTypedEnumRecord(
            SwiftTypeName.FromModuleQualifiedName("PassKit.PKPaymentButtonType"),
            usr: null,
            () => throw new InvalidOperationException("surface index resolved for an undescribed name"));

        Assert.Null(record);
    }

    [Fact]
    public void DescribedStringEnum_IsWithheldFromTheObjCClassBridge()
    {
        // The reporter's failure in one assertion: an ObjC-prefixed name whose .NET projection is an
        // enum must never take the "weak prefix ⇒ it's an ObjC class" path, which emits `.Handle`.
        Assert.True(AppleFrameworkRegistry.IsKnownValueType(DescribedName));
        Assert.True(AppleFrameworkRegistry.IsStringEnumValueType(DescribedName));
        Assert.False(AppleFrameworkRegistry.IsIntegerEnumValueType(DescribedName));
        Assert.False(TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec(DescribedName)));
        Assert.False(MarshallingHelpers.IsObjCPrefixBridgeCandidate(new NamedTypeSpec(DescribedName)));
    }

    [Theory]
    // Every Vision NS_STRING_ENUM the audit found alongside VNBarcodeSymbology. Each carries the VN
    // prefix, so each would otherwise be bridged as a class it is not.
    [InlineData("Vision.VNBarcodeSymbology")]
    [InlineData("Vision.VNAnimalIdentifier")]
    [InlineData("Vision.VNComputeStage")]
    [InlineData("Vision.VNImageOption")]
    [InlineData("Vision.VNVideoProcessingOption")]
    [InlineData("Vision.VNRecognizedPointKey")]
    [InlineData("Vision.VNRecognizedPointGroupKey")]
    public void RegisteredVisionStringEnums_AreNotObjCBridgeCandidates(string swiftName)
    {
        Assert.True(AppleFrameworkRegistry.IsStringEnumValueType(swiftName));
        Assert.False(MarshallingHelpers.IsObjCPrefixBridgeCandidate(new NamedTypeSpec(swiftName)));
    }

    [Theory]
    // The Vision integer NS_ENUMs and the nested joint-name / float-typedef entries. None is a
    // class, so none may reach the prefix bridge either.
    [InlineData("Vision.VNBarcodeCompositeType")]
    [InlineData("Vision.VNChirality")]
    [InlineData("Vision.VNElementType")]
    [InlineData("Vision.VNErrorCode")]
    [InlineData("Vision.VNImageCropAndScaleOption")]
    [InlineData("Vision.VNPointsClassification")]
    [InlineData("Vision.VNRequestTextRecognitionLevel")]
    [InlineData("Vision.VNRequestTrackingLevel")]
    [InlineData("Vision.VNHumanBodyPoseObservation.JointName")]
    [InlineData("Vision.VNHumanHandPoseObservation.JointsGroupName")]
    [InlineData("Vision.VNAspectRatio")]
    [InlineData("Vision.VNConfidence")]
    [InlineData("Vision.VNDegrees")]
    public void RegisteredVisionValueTypes_AreNotObjCBridgeCandidates(string swiftName)
    {
        Assert.True(AppleFrameworkRegistry.IsKnownValueType(swiftName));
        Assert.False(MarshallingHelpers.IsObjCPrefixBridgeCandidate(new NamedTypeSpec(swiftName)));
    }

    [Fact]
    public void ImageIOOrientation_IsDescribedAsAnIntegerEnum()
    {
        // ImageIO carries no ObjC-bridging flags at all, so before it was described its enum had no
        // record and every ImageAnalyzer.analyze overload mentioning it skipped as unresolvable.
        Assert.True(AppleFrameworkRegistry.IsKnownValueType("ImageIO.CGImagePropertyOrientation"));
        Assert.True(AppleFrameworkRegistry.IsIntegerEnumValueType("ImageIO.CGImagePropertyOrientation"));
    }

    [Fact]
    public void ImageIOOrientation_ResolvesWithoutAutoBridgeMembership()
    {
        using var surface = Surface(
            ("ImageIO", "CGImagePropertyOrientation", AppleTypeSurfaceKind.Enum, "Int32", false));

        Assert.False(AppleFrameworkRegistry.IsAutoBridgeModule("ImageIO"));
        Assert.True(new TypeDatabase().TryGetTypeRecord(
            new NamedTypeSpec("ImageIO.CGImagePropertyOrientation"), out var record));
        Assert.True((record!.Flags & TypeRecordFlags.SimpleEnum) != 0);
        Assert.True((record.Flags & TypeRecordFlags.ExternalAppleEnum) != 0);
    }

    [Fact]
    public void ValueTypeKind_StringEnum_ClassifiesAsStringEnum()
        => Assert.Equal(
            AppleFrameworkRegistry.ValueTypeShape.StringEnum,
            AppleFrameworkRegistry.ClassifyValueTypeShape("SomeModule.SomeType", "stringEnum"));

    [Fact]
    public void ValueTypeKind_StringEnum_IsNotAnIntegerEnum()
        => Assert.False(AppleFrameworkRegistry.DescribesIntegerEnum("SomeModule.SomeType", "stringEnum"));

    [Theory]
    [InlineData("stringenum")]
    [InlineData("StringEnum")]
    [InlineData("string-enum")]
    public void ValueTypeKind_MisspelledStringEnum_FailsLoad(string kind)
    {
        // Degrading a typo to "described by nothing" is indistinguishable from a deliberate bare
        // entry and silently leaves the type unresolvable — the failure the description removes.
        var ex = Assert.Throws<InvalidOperationException>(
            () => AppleFrameworkRegistry.ClassifyValueTypeShape("SomeModule.SomeType", kind));
        Assert.Contains("SomeModule.SomeType", ex.Message);
        Assert.Contains("stringEnum", ex.Message);
    }

    // --- Boundary conversion: the enum must never expose a Handle, in any position ---

    [Fact]
    public void ScalarParameter_ConvertsThroughTheExtensionsConverter()
    {
        using var surface = TypedEnumSurface();
        var projection = Project(new NamedTypeSpec(DescribedName), isParameter: true);

        var plan = projection.GetParameterPlan("symbology");
        var rendered = Render(plan);

        Assert.Contains("VNBarcodeSymbologyExtensions.GetConstant(symbology)", rendered);
        Assert.DoesNotContain("symbology.Handle", rendered);
    }

    [Fact]
    public void ScalarReturn_ConvertsBackThroughTheExtensionsConverter()
    {
        using var surface = TypedEnumSurface();
        var projection = Project(new NamedTypeSpec(DescribedName), isParameter: false);

        var plan = projection.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Contains("VNBarcodeSymbologyExtensions.GetValue(", plan.PInvokeExpression);
        Assert.Contains("Foundation.NSString", plan.PInvokeExpression);
    }

    [Fact]
    public void ArrayParameter_BridgesAsAnNSArrayOfConstants()
    {
        // The reporter's member: `RecognizedDataType.barcode(symbologies: [VNBarcodeSymbology])`.
        // Swift receives an NSArray; each element has to be the backing NSString, not an enum.
        using var surface = TypedEnumSurface();
        var array = Assert.IsType<ArrayProjection>(
            Project(new NamedTypeSpec("Swift.Array", new NamedTypeSpec(DescribedName)), isParameter: true));

        Assert.True(array.UsesObjCContainerBridge);
        Assert.Contains("Vision.VNBarcodeSymbology", array.PublicType);

        var rendered = Render(array.GetParameterPlan("symbologies"));

        Assert.Contains("Foundation.NSArray.FromNSObjects(", rendered);
        Assert.Contains("VNBarcodeSymbologyExtensions.GetConstant(e)", rendered);
        // The pre-fix emission — `.Select(e => (IntPtr)e.Handle)` over a SwiftArray<IntPtr> — is the
        // CS1061 this exists to prevent, and the raw Swift-array pipeline is the silent ABI
        // mismatch that replaced it while only one lookup surface knew the record.
        Assert.DoesNotContain("e.Handle", rendered);
        Assert.DoesNotContain("SwiftArray<", rendered);
    }

    [Fact]
    public void ArrayReturn_ReadsNSStringsBackAsEnumValues()
    {
        using var surface = TypedEnumSurface();
        var array = Assert.IsType<ArrayProjection>(
            Project(new NamedTypeSpec("Swift.Array", new NamedTypeSpec(DescribedName)), isParameter: false));

        var rendered = Render(array.GetReturnPlan("result", ReturnStrategy.Direct))
                       + array.GetReturnContainerConversion("container");

        Assert.Contains("Foundation.NSString", rendered);
        Assert.Contains("VNBarcodeSymbologyExtensions.GetValue(e)", rendered);
    }

    [Fact]
    public void SetParameterAndReturn_ConvertBothWays()
    {
        using var surface = TypedEnumSurface();
        var paramSet = Assert.IsType<SetProjection>(
            Project(new NamedTypeSpec("Swift.Set", new NamedTypeSpec(DescribedName)), isParameter: true));
        var returnSet = Assert.IsType<SetProjection>(
            Project(new NamedTypeSpec("Swift.Set", new NamedTypeSpec(DescribedName)), isParameter: false));

        var paramRendered = Render(paramSet.GetParameterPlan("symbologies"));
        var returnRendered = Render(returnSet.GetReturnPlan("result", ReturnStrategy.Direct));

        Assert.Contains("VNBarcodeSymbologyExtensions.GetConstant(e)", paramRendered);
        Assert.DoesNotContain("e.Handle", paramRendered);
        Assert.Contains("VNBarcodeSymbologyExtensions.GetValue(", returnRendered);
    }

    [Fact]
    public void DictionaryValue_ConvertsOutboundThroughTheExtensionsConverter()
    {
        using var surface = TypedEnumSurface();
        var dictionary = Assert.IsType<DictionaryProjection>(
            Project(DictionaryOfSymbologies(), isParameter: true));

        var rendered = Render(dictionary.GetParameterPlan("labels"));

        Assert.Contains("VNBarcodeSymbologyExtensions.GetConstant(", rendered);
        // The pre-fix emission asked the C# enum in the value slot for a Handle it does not have.
        // Scoped to the value slot on purpose: the NSDictionary container's own `.Handle` is
        // correct and appears in the same plan.
        Assert.DoesNotContain("kvp.Value.Handle", rendered);
    }

    [Fact]
    public void DictionaryValue_ConvertsInboundThroughTheExtensionsConverter()
    {
        // The inbound half is the one an outbound-only assertion cannot fail: the value arrives as
        // a boxed NSString and has to be read back through GetValue, not cast to the C# enum.
        using var surface = TypedEnumSurface();
        var dictionary = Assert.IsType<DictionaryProjection>(
            Project(DictionaryOfSymbologies(), isParameter: false));

        var rendered = Render(dictionary.GetReturnPlan("result", ReturnStrategy.Direct))
                       + dictionary.GetReturnContainerConversion("container");

        Assert.Contains("VNBarcodeSymbologyExtensions.GetValue(", rendered);
        Assert.Contains("Foundation.NSString", rendered);
    }

    [Fact]
    public void OptionalParameter_ConvertsThroughTheExtensionsConverter()
    {
        using var surface = TypedEnumSurface();
        var optional = Assert.IsType<OptionalProjection>(
            Project(new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(DescribedName)), isParameter: true));

        // Asserted on the PLAN alone. Folding in GetParameterElementConversion would make the
        // assertion unfalsifiable: that helper emits GetConstant for any ObjC-bridgeable inner, so
        // a plan that still reached for `.Handle` on the C# enum would pass on the helper's text.
        var rendered = Render(optional.GetParameterPlan("symbology"));

        Assert.Contains("VNBarcodeSymbologyExtensions.GetConstant(", rendered);
        Assert.DoesNotContain("symbology.Handle", rendered);
        Assert.DoesNotContain("symbology?.Handle", rendered);
    }

    [Fact]
    public void OptionalReturn_ReadsTheCarrierBackAsAnEnumValue()
    {
        using var surface = TypedEnumSurface();
        var optional = Assert.IsType<OptionalProjection>(
            Project(new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(DescribedName)), isParameter: false));

        var rendered = Render(optional.GetReturnPlan("result", ReturnStrategy.Direct));

        Assert.Contains("VNBarcodeSymbologyExtensions.GetValue(", rendered);
        Assert.Contains("Foundation.NSString", rendered);
    }

    [Fact]
    public void ClosureCarryingTheTypedEnum_IsRefused()
    {
        // The one shape here that must NOT bind. The member's public signature projects the C# enum
        // while the closure translator renders the NSString carrier, and the two are unrelated
        // generic instantiations: the emitted delegate would compile as Action<TEnum> and the thunk
        // would resolve it as Action<NSString>, throwing InvalidCastException on the first callback.
        // A compile error would be caught by the C#-plane verify-recover loop; this one would not be,
        // which is what the refusal is for.
        using var surface = TypedEnumSurface();
        var handler = new ClosureHandler(new TypeDatabase());

        var closure = new ClosureTypeSpec(new NamedTypeSpec(DescribedName), TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void ClosureCarryingAnIntegerAppleEnum_IsStillAccepted()
    {
        // Falsifiability control for the refusal above. The comparison type is a sibling in the SAME
        // framework that the registry describes as an ordinary integer enum rather than a stringEnum,
        // so it reaches the closure gate through the same registry-resolution path and differs only
        // in carrying no AppleTypedEnum flag. An integer enum's C# projection IS its marshalled type,
        // so there are no two disagreeing faces to reconcile and the closure binds.
        //
        // Without this, the refusal test would pass just as well for a type nothing could resolve at
        // all — which is exactly what an earlier version of this control demonstrated.
        using var surface = Surface(
            (DescribedNamespace, "VNChirality", AppleTypeSurfaceKind.Enum, "Int", false));
        var handler = new ClosureHandler(new TypeDatabase());

        var closure = new ClosureTypeSpec(
            new NamedTypeSpec($"{DescribedNamespace}.VNChirality"), TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(handler.IsSupportedClosure(closure));
    }

    private static NamedTypeSpec DictionaryOfSymbologies() => new(
        "Swift.Dictionary",
        new NamedTypeSpec("Swift.String"),
        new NamedTypeSpec(DescribedName));

    // --- helpers ---

    private static ITypeProjection Project(TypeSpec spec, bool isParameter)
    {
        var projection = new TypeProjectionFactory().Project(
            spec, new ProjectionContext { TypeDatabase = new TypeDatabase(), IsParameter = isParameter });
        Assert.NotNull(projection);
        return projection!;
    }

    private static string Render(MarshalPlan plan)
    {
        var text = new System.Text.StringBuilder();
        foreach (var statement in plan.SetupStatements)
        {
            text.AppendLine(statement switch
            {
                MarshalStatement.Line line => line.Code,
                MarshalStatement.Using u => $"using var {u.Name} = {u.InitExpression};",
                _ => statement.ToString(),
            });
        }

        text.AppendLine(plan.PInvokeExpression);
        return text.ToString();
    }

    // The described enum plus the converter class the platform binding ships beside it.
    private static IDisposable TypedEnumSurface() => Surface(
        (DescribedNamespace, DescribedLeaf, AppleTypeSurfaceKind.Enum, "Int", false),
        (DescribedNamespace, DescribedLeaf + "Extensions", AppleTypeSurfaceKind.StaticConstants, null, false));

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
