// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

public class WrapperEmitterHelpersTests
{
    // The production `RawGenericSig` (api-digester form) names the SUBJECT of every requirement
    // by its RAW token (`τ_0_0`), while the emitted `extension Parent` line refers to the param
    // by its SUGARED name (`DonationInfo`). Every test below feeds the raw-token shape — the only
    // shape the generator ever sees — so the suite would have caught the latent raw-vs-sugared
    // mismatch that a sugared `<DonationInfo where DonationInfo == …>` input masked.

    [Fact]
    public void BuildParentSameTypeExtensionWhere_EmitsSameTypeConstraint_MatchesRawTokenEmitsSugared()
    {
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        var method = CreateMethodWithRawSig(
            "<τ_0_0 where τ_0_0 == TipKit.Tips.EmptyDonation>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

        // Subject matched on the raw token `τ_0_0`, emitted under the sugared param name.
        Assert.Equal(" where DonationInfo == TipKit.Tips.EmptyDonation", result);
    }

    [Fact]
    public void BuildParentSameTypeExtensionWhere_ReturnsEmpty_WhenSigHasNoWhereClause()
    {
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        var method = CreateMethodWithRawSig("<τ_0_0>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildParentSameTypeExtensionWhere_ReturnsEmpty_WhenParentHasNoGenerics()
    {
        var parent = CreateStructDecl("Plain", isGeneric: false);
        var method = CreateMethodWithRawSig(
            "<τ_0_0 where τ_0_0 == Swift.Int>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildParentSameTypeExtensionWhere_IgnoresConstraint_OnMethodOwnGenericParam()
    {
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        // `τ_1_0` is a method-own param (depth 1), not the parent param `τ_0_0`.
        var method = CreateMethodWithRawSig(
            "<τ_0_0, τ_1_0 where τ_1_0 == Swift.Int>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildParentSameTypeExtensionWhere_SkipsConformanceConstraints_UnderDefaultFlag()
    {
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        var method = CreateMethodWithRawSig(
            "<τ_0_0 where τ_0_0 : Swift.Decodable>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildParentSameTypeExtensionWhere_EmitsRealConformanceConstraint_WhenIncluded()
    {
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        var method = CreateMethodWithRawSig(
            "<τ_0_0 where τ_0_0 : Swift.Decodable>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(
            method, parent, includeConformanceConstraints: true);

        // Non-marker conformance: raw token matched, sugared param name emitted.
        Assert.Equal(" where DonationInfo : Swift.Decodable", result);
    }

    [Theory]
    [InlineData("Swift.Sendable")]
    [InlineData("Swift.Copyable")]
    [InlineData("Swift.Escapable")]
    [InlineData("Swift.BitwiseCopyable")]
    [InlineData("Swift.SendableMetatype")]
    public void BuildParentSameTypeExtensionWhere_DropsStdlibMarkerConformance_EvenWhenIncluded(string marker)
    {
        // A non-marker protocol's conditional conformance may not depend on a marker protocol
        // (Swift rejects it), so a marker conformance MUST be dropped — leaving the GSF
        // conformance unconditional. Without the drop the emitted Swift fails to compile.
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        var method = CreateMethodWithRawSig($"<τ_0_0 where τ_0_0 : {marker}>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(
            method, parent, includeConformanceConstraints: true);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildParentSameTypeExtensionWhere_ReturnsEmpty_WhenRawGenericSigIsNull()
    {
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        var method = CreateMethodWithRawSig(null);

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

        Assert.Equal(string.Empty, result);
    }

    private static StructDecl CreateStructDecl(string name, bool isGeneric)
    {
        return new StructDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = isGeneric
                ? new List<GenericArgumentDecl> { new("τ_0_0", "T", new(), new()) }
                : new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };
    }

    private static StructDecl CreateGenericStructDecl(string name, string genericParamName)
    {
        return new StructDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", genericParamName, new(), new())
            },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };
    }

    private static MethodDecl CreateMethodWithRawSig(string? rawGenericSig)
    {
        return new MethodDecl
        {
            Name = "donate",
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = "",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null,
            RawGenericSig = rawGenericSig
        };
    }
}
