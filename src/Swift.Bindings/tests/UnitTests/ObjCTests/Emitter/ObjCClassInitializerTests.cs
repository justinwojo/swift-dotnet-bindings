// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCClassInitializerTests
{
    [Theory]
    [InlineData("class", "init", "InitClass")]
    [InlineData("class", "initWithRequest:", "InitWithRequest")]
    [InlineData("class", "initWithCoder:", "InitWithCoder")]
    [InlineData("category", "init", "InitClass")]
    [InlineData("category", "initWithRequest:", "InitWithRequest")]
    [InlineData("protocol", "init", "InitClass")]
    [InlineData("protocol", "initWithRequest:", "InitWithRequest")]
    public void ClassInitSelectors_AreStaticMethods(string owner, string selector, string expectedName)
    {
        var method = new ObjCMethodDecl
        {
            Selector = selector, IsInstanceMethod = false, ReturnType = SimpleType("void"),
            Parameters = selector.Contains(':') ? [new() { Name = "request", Type = SimpleType("NSInteger") }] : []
        };
        var module = new ObjCModule { ModuleName = "Test" };
        if (owner == "class")
            module.Classes.Add(new() { Name = "Service", ProtocolNames = ["NSCoding"], Methods = [method] });
        else if (owner == "category")
        {
            module.Classes.Add(new() { Name = "Service" });
            module.Categories.Add(new() { ClassName = "Service", CategoryName = "Setup", Methods = [method] });
        }
        else
            module.Protocols.Add(new() { Name = "Service", Methods = [method] });

        var result = EmitApiDefinition(module, "TestNamespace").ReplaceLineEndings("\n");
        Assert.Contains($"[Static]\n        [Export(\"{selector}\")]\n        void {expectedName}(", result);
        Assert.DoesNotContain("NativeHandle Constructor", result);
        Assert.DoesNotContain("[DisableDefaultCtor]", result);
    }

    [Fact]
    public void ClassAndInstanceInit_KeepSeparateDispatch()
    {
        var result = EmitApiDefinition(new ObjCModule
        {
            ModuleName = "Test", Classes = [new()
            {
                Name = "Service", Methods = [
                    new() { Selector = "init", IsInstanceMethod = true, ReturnType = SimpleType("instancetype") },
                    new() { Selector = "init", IsInstanceMethod = false, ReturnType = SimpleType("void") },
                    new() { Selector = "initWithValue:", IsInstanceMethod = true, ReturnType = SimpleType("instancetype"),
                        Parameters = [new() { Name = "value", Type = SimpleType("NSInteger") }] }
                ]
            }]
        }, "TestNamespace").ReplaceLineEndings("\n");
        Assert.Contains("[DisableDefaultCtor]", result);
        Assert.Contains("[Static]\n        [Export(\"init\")]\n        void InitClass()", result);
        Assert.Contains("NativeHandle Constructor(nint value)", result);
    }
}
