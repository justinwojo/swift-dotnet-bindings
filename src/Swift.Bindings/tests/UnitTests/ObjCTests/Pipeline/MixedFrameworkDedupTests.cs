// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class MixedFrameworkDedupTests
{

    private static ObjCModule CreateTestModule(
        List<ObjCClassDecl>? classes = null,
        List<ObjCProtocolDecl>? protocols = null,
        List<ObjCEnumDecl>? enums = null,
        List<ObjCStructDecl>? structs = null,
        List<ObjCFunctionDecl>? functions = null,
        List<ObjCConstantDecl>? constants = null,
        List<ObjCCategoryDecl>? categories = null)
    {
        return new ObjCModule
        {
            ModuleName = "TestModule",
            Classes = classes ?? [],
            Protocols = protocols ?? [],
            Enums = enums ?? [],
            Structs = structs ?? [],
            Functions = functions ?? [],
            Constants = constants ?? [],
            Categories = categories ?? [],
        };
    }

    [Fact]
    public void FilterForMixedFramework_ObjCOnlyTypes_KeptFully()
    {
        var module = CreateTestModule(
            classes: [new() { Name = "ObjCOnlyManager" }],
            protocols: [new() { Name = "ObjCOnlyDelegate" }]);

        var swiftNames = new HashSet<string> { "SwiftViewModel", "SwiftService" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Single(filtered.Classes);
        Assert.Equal("ObjCOnlyManager", filtered.Classes[0].Name);
        Assert.Single(filtered.Protocols);
        Assert.Equal("ObjCOnlyDelegate", filtered.Protocols[0].Name);
    }

    [Fact]
    public void FilterForMixedFramework_SharedClass_Dropped()
    {
        var module = CreateTestModule(
            classes: [new() { Name = "SharedClass" }, new() { Name = "ObjCOnly" }]);

        var swiftNames = new HashSet<string> { "SharedClass" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Single(filtered.Classes);
        Assert.Equal("ObjCOnly", filtered.Classes[0].Name);
    }

    [Fact]
    public void FilterForMixedFramework_SharedProtocol_Dropped()
    {
        var module = CreateTestModule(
            protocols: [new() { Name = "SharedProto" }, new() { Name = "ObjCProto" }]);

        var swiftNames = new HashSet<string> { "SharedProto" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Single(filtered.Protocols);
        Assert.Equal("ObjCProto", filtered.Protocols[0].Name);
    }

    [Fact]
    public void FilterForMixedFramework_EnumsStructsFunctions_NeverFiltered()
    {
        var module = CreateTestModule(
            enums: [new() { Name = "SharedEnum" }],
            structs: [new() { Name = "SharedStruct" }],
            functions: [new()
            {
                Name = "SharedFunc",
                ReturnType = new ObjCTypeRef { Name = "void" }
            }],
            constants: [new()
            {
                Name = "SharedConst",
                Type = new ObjCTypeRef { Name = "NSString" }
            }]);

        // Even if names match Swift types, enums/structs/functions/constants are never filtered
        var swiftNames = new HashSet<string>
            { "SharedEnum", "SharedStruct", "SharedFunc", "SharedConst" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Single(filtered.Enums);
        Assert.Single(filtered.Structs);
        Assert.Single(filtered.Functions);
        Assert.Single(filtered.Constants);
    }

    [Fact]
    public void FilterForMixedFramework_EmptyExcludeSet_NoChanges()
    {
        var module = CreateTestModule(
            classes: [new() { Name = "Foo" }],
            protocols: [new() { Name = "Bar" }]);

        var swiftNames = new HashSet<string>();
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Single(filtered.Classes);
        Assert.Single(filtered.Protocols);
    }

    [Fact]
    public void FilterForMixedFramework_AllFiltered_ReturnsZeroDecls()
    {
        var module = CreateTestModule(
            classes: [new() { Name = "Alpha" }],
            protocols: [new() { Name = "Beta" }]);

        var swiftNames = new HashSet<string> { "Alpha", "Beta" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Empty(filtered.Classes);
        Assert.Empty(filtered.Protocols);
        Assert.Equal(0, filtered.TotalDeclarations);
    }

    // ──────────────────────────────────────────────
    // Category extraction tests
    // ──────────────────────────────────────────────

    [Fact]
    public void FilterForMixedFramework_SharedClassWithCategoryMembers_ExtractsCategories()
    {
        var module = CreateTestModule(
            classes: [new()
            {
                Name = "SharedClass",
                Methods =
                [
                    new() { Selector = "init", ReturnType = new ObjCTypeRef { Name = "instancetype" }, IsInstanceMethod = true },
                    new() { Selector = "doExtra", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true, IsFromCategory = true, CategoryName = "Extras" }
                ]
            }],
            categories: [new()
            {
                CategoryName = "Extras",
                ClassName = "SharedClass",
                Methods = [new() { Selector = "doExtra", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true }]
            }]);

        var swiftNames = new HashSet<string> { "SharedClass" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Empty(filtered.Classes);
        Assert.Single(filtered.Categories);
        Assert.Equal("Extras", filtered.Categories[0].CategoryName);
        Assert.Equal("SharedClass", filtered.Categories[0].ClassName);
    }

    [Fact]
    public void FilterForMixedFramework_SharedClassNoCategoryMembers_DroppedCleanly()
    {
        var module = CreateTestModule(
            classes: [new()
            {
                Name = "SharedClass",
                Methods = [new() { Selector = "init", ReturnType = new ObjCTypeRef { Name = "instancetype" }, IsInstanceMethod = true }]
            }]);

        var swiftNames = new HashSet<string> { "SharedClass" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Empty(filtered.Classes);
        Assert.Empty(filtered.Categories);
    }

    [Fact]
    public void FilterForMixedFramework_ObjCOnlyClassWithCategories_KeepsMerged()
    {
        var module = CreateTestModule(
            classes: [new()
            {
                Name = "ObjCOnly",
                Methods =
                [
                    new() { Selector = "init", ReturnType = new ObjCTypeRef { Name = "instancetype" }, IsInstanceMethod = true },
                    new() { Selector = "catMethod", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true, IsFromCategory = true, CategoryName = "Cat" }
                ]
            }],
            categories: [new()
            {
                CategoryName = "Cat",
                ClassName = "ObjCOnly",
                Methods = [new() { Selector = "catMethod", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true }]
            }]);

        var swiftNames = new HashSet<string> { "SwiftThing" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        // ObjC-only class is kept, its category is NOT extracted (stays merged inline)
        Assert.Single(filtered.Classes);
        Assert.Equal("ObjCOnly", filtered.Classes[0].Name);
        Assert.Empty(filtered.Categories);
    }

    [Fact]
    public void FilterForMixedFramework_MultipleCategoriesOnSharedClass_GroupedSeparately()
    {
        var module = CreateTestModule(
            classes: [new()
            {
                Name = "SharedClass",
                GenericTypeParamNames = ["ObjectType"]
            }],
            categories:
            [
                new()
                {
                    CategoryName = "Alpha",
                    ClassName = "SharedClass",
                    Methods = [new() { Selector = "alphaMethod", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true }]
                },
                new()
                {
                    CategoryName = "Beta",
                    ClassName = "SharedClass",
                    Methods = [new() { Selector = "betaMethod", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true }]
                }
            ]);

        var swiftNames = new HashSet<string> { "SharedClass" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Equal(2, filtered.Categories.Count);
        Assert.Contains(filtered.Categories, c => c.CategoryName == "Alpha");
        Assert.Contains(filtered.Categories, c => c.CategoryName == "Beta");
        // GenericTypeParamNames should be copied from owning class
        Assert.All(filtered.Categories, c => Assert.Contains("ObjectType", c.GenericTypeParamNames));
    }

    [Fact]
    public void FilterForMixedFramework_SharedClassMixedMembers_OnlyCategoryMembersExtracted()
    {
        var module = CreateTestModule(
            classes: [new()
            {
                Name = "SharedClass",
                Methods =
                [
                    new() { Selector = "normalMethod", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true },
                    new() { Selector = "catMethod", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true, IsFromCategory = true, CategoryName = "Extras" }
                ]
            }],
            categories: [new()
            {
                CategoryName = "Extras",
                ClassName = "SharedClass",
                Methods = [new() { Selector = "catMethod", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true }]
            }]);

        var swiftNames = new HashSet<string> { "SharedClass" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Empty(filtered.Classes);
        Assert.Single(filtered.Categories);
        // Only the category method is in the extracted category
        Assert.Single(filtered.Categories[0].Methods);
        Assert.Equal("catMethod", filtered.Categories[0].Methods[0].Selector);
    }

    [Fact]
    public void FilterForMixedFramework_SharedProtocol_StillDroppedEntirely()
    {
        var module = CreateTestModule(
            protocols: [new() { Name = "SharedProto" }, new() { Name = "ObjCProto" }]);

        var swiftNames = new HashSet<string> { "SharedProto" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        Assert.Single(filtered.Protocols);
        Assert.Equal("ObjCProto", filtered.Protocols[0].Name);
        Assert.Empty(filtered.Categories);
    }

    [Fact]
    public void FilterForMixedFramework_CategoriesOnly_NotSkippedByPostHocGate()
    {
        // A module with only categories (all classes/protocols shared with Swift).
        // The post-hoc gate should NOT skip emission when Categories.Count > 0.
        var module = CreateTestModule(
            classes: [new() { Name = "SharedClass" }],
            categories: [new()
            {
                CategoryName = "Extras",
                ClassName = "SharedClass",
                Methods = [new() { Selector = "doExtra", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true }]
            }]);

        var swiftNames = new HashSet<string> { "SharedClass" };
        var filtered = ObjCPipeline.FilterForMixedFramework(module, swiftNames, Logger);

        // After filtering: no classes, no protocols, but one category
        Assert.Empty(filtered.Classes);
        Assert.Empty(filtered.Protocols);
        Assert.Single(filtered.Categories);

        // The post-hoc gate condition: classes == 0 && protocols == 0 && categories == 0
        // This should be FALSE since we have categories
        bool wouldSkip = filtered.Classes.Count == 0 && filtered.Protocols.Count == 0 && filtered.Categories.Count == 0;
        Assert.False(wouldSkip, "Post-hoc gate should NOT skip when categories are present");
    }
}
