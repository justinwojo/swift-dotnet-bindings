// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

public class MixedFrameworkDedupTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static ObjCModule CreateTestModule(
        List<ObjCClassDecl>? classes = null,
        List<ObjCProtocolDecl>? protocols = null,
        List<ObjCEnumDecl>? enums = null,
        List<ObjCStructDecl>? structs = null,
        List<ObjCFunctionDecl>? functions = null,
        List<ObjCConstantDecl>? constants = null)
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
}
