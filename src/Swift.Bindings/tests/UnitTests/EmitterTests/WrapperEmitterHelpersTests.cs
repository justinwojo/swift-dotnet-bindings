// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

public class WrapperEmitterHelpersTests
{
    [Fact]
    public void BuildParentSameTypeExtensionWhere_EmitsSameTypeConstraint_ForParentGenericParam()
    {
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        var method = CreateMethodWithRawSig(
            "<DonationInfo where DonationInfo == TipKit.Tips.EmptyDonation>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

        Assert.Equal(" where DonationInfo == TipKit.Tips.EmptyDonation", result);
    }

    [Fact]
    public void BuildParentSameTypeExtensionWhere_ReturnsEmpty_WhenSigHasNoWhereClause()
    {
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        var method = CreateMethodWithRawSig("<DonationInfo>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildParentSameTypeExtensionWhere_ReturnsEmpty_WhenParentHasNoGenerics()
    {
        var parent = CreateStructDecl("Plain", isGeneric: false);
        var method = CreateMethodWithRawSig(
            "<T where T == Swift.Int>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildParentSameTypeExtensionWhere_IgnoresConstraint_OnMethodOwnGenericParam()
    {
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        var method = CreateMethodWithRawSig(
            "<DonationInfo, U where U == Swift.Int>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildParentSameTypeExtensionWhere_SkipsProtocolConformanceConstraints()
    {
        var parent = CreateGenericStructDecl("Event", "DonationInfo");
        var method = CreateMethodWithRawSig(
            "<DonationInfo where DonationInfo : Swift.Decodable>");

        var result = WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere(method, parent);

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
