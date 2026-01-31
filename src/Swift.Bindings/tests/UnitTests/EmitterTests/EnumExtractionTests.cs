// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for enum associated value extraction functionality.
/// </summary>
public class EnumExtractionTests
{
    #region EnumDecl.GetCaseTag Tests

    [Fact]
    public void GetCaseTag_PayloadCaseFirst_ReturnsZero()
    {
        var enumDecl = CreateEnumDeclWithMixedCases();
        var payloadCase = enumDecl.Cases.First(c => c.HasAssociatedValues);

        var tag = enumDecl.GetCaseTag(payloadCase);

        Assert.Equal(0, tag);
    }

    [Fact]
    public void GetCaseTag_NoPayloadCaseAfterPayload_ReturnsPayloadCount()
    {
        var enumDecl = CreateEnumDeclWithMixedCases();
        var payloadCases = enumDecl.Cases.Where(c => c.HasAssociatedValues).ToList();
        var noPayloadCase = enumDecl.Cases.First(c => !c.HasAssociatedValues);

        var tag = enumDecl.GetCaseTag(noPayloadCase);

        Assert.Equal(payloadCases.Count, tag);
    }

    [Fact]
    public void GetCaseTag_MultiplePayloadCases_ReturnsDeclarationOrder()
    {
        var enumDecl = CreateEnumDeclWithMixedCases();
        var payloadCases = enumDecl.Cases.Where(c => c.HasAssociatedValues).ToList();

        for (int i = 0; i < payloadCases.Count; i++)
        {
            var tag = enumDecl.GetCaseTag(payloadCases[i]);
            Assert.Equal(i, tag);
        }
    }

    [Fact]
    public void GetCaseTag_MultipleNoPayloadCases_ReturnsCorrectOffsets()
    {
        var enumDecl = CreateEnumDeclWithMixedCases();
        var payloadCount = enumDecl.Cases.Count(c => c.HasAssociatedValues);
        var noPayloadCases = enumDecl.Cases.Where(c => !c.HasAssociatedValues).ToList();

        for (int i = 0; i < noPayloadCases.Count; i++)
        {
            var tag = enumDecl.GetCaseTag(noPayloadCases[i]);
            Assert.Equal(payloadCount + i, tag);
        }
    }

    [Fact]
    public void GetCaseTag_AllPayloadCases_SequentialTags()
    {
        var enumDecl = CreateEnumWithOnlyPayloadCases();

        for (int i = 0; i < enumDecl.Cases.Count; i++)
        {
            var tag = enumDecl.GetCaseTag(enumDecl.Cases[i]);
            Assert.Equal(i, tag);
        }
    }

    [Fact]
    public void GetCaseTag_AllNoPayloadCases_SequentialTags()
    {
        var enumDecl = CreateEnumWithOnlyNoPayloadCases();

        for (int i = 0; i < enumDecl.Cases.Count; i++)
        {
            var tag = enumDecl.GetCaseTag(enumDecl.Cases[i]);
            Assert.Equal(i, tag);
        }
    }

    [Fact]
    public void GetCaseTag_CaseNotInEnum_ReturnsMinusOne()
    {
        var enumDecl = CreateEnumWithOnlyNoPayloadCases();
        var foreignCase = CreateEnumCaseDecl("foreignCase");

        var tag = enumDecl.GetCaseTag(foreignCase);

        Assert.Equal(-1, tag);
    }

    #endregion

    #region EnumDecl Helper Properties Tests

    [Fact]
    public void PayloadCases_ReturnsOnlyCasesWithAssociatedValues()
    {
        var enumDecl = CreateEnumDeclWithMixedCases();

        var payloadCases = enumDecl.PayloadCases.ToList();

        Assert.All(payloadCases, c => Assert.True(c.HasAssociatedValues));
        Assert.Equal(2, payloadCases.Count);
    }

    [Fact]
    public void NoPayloadCases_ReturnsOnlyCasesWithoutAssociatedValues()
    {
        var enumDecl = CreateEnumDeclWithMixedCases();

        var noPayloadCases = enumDecl.NoPayloadCases.ToList();

        Assert.All(noPayloadCases, c => Assert.False(c.HasAssociatedValues));
        Assert.Equal(2, noPayloadCases.Count);
    }

    [Fact]
    public void PayloadCases_EmptyWhenNoPayloadCases()
    {
        var enumDecl = CreateEnumWithOnlyNoPayloadCases();

        var payloadCases = enumDecl.PayloadCases.ToList();

        Assert.Empty(payloadCases);
    }

    [Fact]
    public void NoPayloadCases_EmptyWhenAllPayloadCases()
    {
        var enumDecl = CreateEnumWithOnlyPayloadCases();

        var noPayloadCases = enumDecl.NoPayloadCases.ToList();

        Assert.Empty(noPayloadCases);
    }

    #endregion

    #region Tag Ordering Tests

    [Fact]
    public void TagOrdering_MatchesSwiftConvention()
    {
        // Swift orders tags as: payload cases (declaration order) then no-payload cases (declaration order)
        // Given: payload1, nopayload1, payload2, nopayload2
        // Expected tags: payload1=0, payload2=1, nopayload1=2, nopayload2=3

        var enumDecl = CreateEnumDeclWithInterleavedCases();

        var payload1 = enumDecl.Cases.First(c => c.Name == "payload1");
        var payload2 = enumDecl.Cases.First(c => c.Name == "payload2");
        var nopayload1 = enumDecl.Cases.First(c => c.Name == "nopayload1");
        var nopayload2 = enumDecl.Cases.First(c => c.Name == "nopayload2");

        Assert.Equal(0, enumDecl.GetCaseTag(payload1));
        Assert.Equal(1, enumDecl.GetCaseTag(payload2));
        Assert.Equal(2, enumDecl.GetCaseTag(nopayload1));
        Assert.Equal(3, enumDecl.GetCaseTag(nopayload2));
    }

    #endregion

    #region Helper Methods

    private static EnumDecl CreateEnumDecl(string name)
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}O",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Cases = new List<EnumCaseDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = string.Empty
        };
    }

    private static EnumCaseDecl CreateEnumCaseDecl(string name)
    {
        return new EnumCaseDecl
        {
            Name = name,
            MangledName = $"$s10TestModule9TestEnumO{name.Length}{name}yA2CmF",
            AssociatedValues = new List<TypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static EnumCaseDecl CreateEnumCaseDeclWithPayload(string name, params string[] typeNames)
    {
        var caseDecl = CreateEnumCaseDecl(name);
        foreach (var typeName in typeNames)
        {
            caseDecl.AssociatedValues.Add(new NamedTypeSpec(typeName));
        }
        return caseDecl;
    }

    /// <summary>
    /// Creates an enum with mixed cases: 2 with associated values, 2 without.
    /// Order: dataLoadingFailed (payload), decoderNotRegistered (payload), dataMissingInCache, dataIsEmpty
    /// </summary>
    private static EnumDecl CreateEnumDeclWithMixedCases()
    {
        var enumDecl = CreateEnumDecl("Error");

        enumDecl.Cases.Add(CreateEnumCaseDeclWithPayload("dataLoadingFailed", "Swift.Error"));
        enumDecl.Cases.Add(CreateEnumCaseDeclWithPayload("decoderNotRegistered", "Swift.String"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("dataMissingInCache"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("dataIsEmpty"));

        return enumDecl;
    }

    /// <summary>
    /// Creates an enum with only payload cases.
    /// </summary>
    private static EnumDecl CreateEnumWithOnlyPayloadCases()
    {
        var enumDecl = CreateEnumDecl("Result");

        enumDecl.Cases.Add(CreateEnumCaseDeclWithPayload("success", "T"));
        enumDecl.Cases.Add(CreateEnumCaseDeclWithPayload("failure", "E"));

        return enumDecl;
    }

    /// <summary>
    /// Creates an enum with only no-payload cases.
    /// </summary>
    private static EnumDecl CreateEnumWithOnlyNoPayloadCases()
    {
        var enumDecl = CreateEnumDecl("Direction");

        enumDecl.Cases.Add(CreateEnumCaseDecl("north"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("south"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("east"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("west"));

        return enumDecl;
    }

    /// <summary>
    /// Creates an enum with interleaved payload and no-payload cases to test ordering.
    /// Declaration order: payload1, nopayload1, payload2, nopayload2
    /// </summary>
    private static EnumDecl CreateEnumDeclWithInterleavedCases()
    {
        var enumDecl = CreateEnumDecl("Mixed");

        enumDecl.Cases.Add(CreateEnumCaseDeclWithPayload("payload1", "Swift.Int"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("nopayload1"));
        enumDecl.Cases.Add(CreateEnumCaseDeclWithPayload("payload2", "Swift.String"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("nopayload2"));

        return enumDecl;
    }

    #endregion
}
