// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCProtocolCollisionTests
{
    static ObjCMethodDecl Callback(string selector, string type = "NSInteger") => new()
    {
        Selector = selector, ReturnType = SimpleType("void"), IsInstanceMethod = true,
        Parameters = [new() { Name = "value", Type = SimpleType(type) }, new() { Name = "info", Type = SimpleType("NSInteger") }]
    };

    static ObjCProtocolDecl Parent(string name, string selector, string type = "NSInteger") => new()
    {
        Name = name, IsDelegateProtocol = true, Methods = [Callback(selector, type)]
    };

    static string Emit(params ObjCProtocolDecl[] protocols) => EmitApiDefinition(new ObjCModule
    {
        ModuleName = "Test", Protocols = protocols.ToList()
    }, "TestNamespace").ReplaceLineEndings("\n");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CoAdoptedParents_KeepBothSelectorsWithDistinctNames(bool reverse, bool classAdopter)
    {
        var parents = new[] { Parent("LoadDelegate", "didLoad:withAdInfo:"), Parent("InteractionDelegate", "didRecordImpression:withAdInfo:") };
        if (reverse) Array.Reverse(parents);
        var module = new ObjCModule { ModuleName = "Test", Protocols = parents.ToList() };
        var adopted = parents.Select(p => p.Name).ToList();
        if (classAdopter)
            module.Classes.Add(new() { Name = "Adopter", ProtocolNames = adopted });
        else
        {
            module.Protocols.Add(new() { Name = "CombinedDelegate", InheritedProtocolNames = adopted });
            module.Protocols.Add(new() { Name = "DescendantDelegate", InheritedProtocolNames = ["CombinedDelegate"] });
        }
        var result = EmitApiDefinition(module, "TestNamespace").ReplaceLineEndings("\n");
        Assert.Contains("[Export(\"didLoad:withAdInfo:\")]\n        void WithAdInfo(", result);
        Assert.Contains("[Export(\"didRecordImpression:withAdInfo:\")]\n        void DidRecordImpressionWithAdInfo(", result);
        Assert.DoesNotContain("WithAdInfo2(", result);
    }

    [Fact]
    public void UnrelatedParents_KeepExistingNames()
    {
        var result = Emit(Parent("LoadDelegate", "didLoad:withAdInfo:"), Parent("InteractionDelegate", "didRecordImpression:withAdInfo:"));
        Assert.Equal(2, result.Split("void WithAdInfo(").Length - 1);
        Assert.DoesNotContain("void DidRecordImpressionWithAdInfo(", result);
    }

    [Fact]
    public void CommonAncestorWithoutCoAdoption_DoesNotConnectSiblingSurfaces()
    {
        var result = Emit(new() { Name = "Root" },
            Parent("LoadDelegate", "didLoad:withAdInfo:") with { InheritedProtocolNames = ["Root"] },
            Parent("InteractionDelegate", "didRecordImpression:withAdInfo:") with { InheritedProtocolNames = ["Root"] });
        Assert.Equal(2, result.Split("void WithAdInfo(").Length - 1);
    }

    [Fact]
    public void Diamond_DoesNotRenameSharedDeclaration()
    {
        var result = Emit(Parent("LoadDelegate", "didLoad:withAdInfo:"),
            new() { Name = "Left", InheritedProtocolNames = ["LoadDelegate"] },
            new() { Name = "Right", InheritedProtocolNames = ["LoadDelegate"] },
            new() { Name = "Combined", InheritedProtocolNames = ["Left", "Right"] });
        Assert.Contains("void WithAdInfo(", result);
        Assert.DoesNotContain("void DidLoadWithAdInfo(", result);
    }

    [Theory]
    [InlineData("NSString")] // Legal overload.
    [InlineData("MissingThirdPartyType")] // Skipped declaration must not reserve a name.
    public void NonCollidingOrSkippedParent_DoesNotRename(string type)
    {
        var result = Emit(Parent("LoadDelegate", "didLoad:withAdInfo:"),
            Parent("InteractionDelegate", "didRecordImpression:withAdInfo:", type),
            new() { Name = "Combined", InheritedProtocolNames = ["LoadDelegate", "InteractionDelegate"] });
        Assert.Contains("void WithAdInfo(", result);
        Assert.DoesNotContain("void DidRecordImpressionWithAdInfo(", result);
    }

    [Fact]
    public void ReplacementName_ReservesExistingFullSelectorMember()
    {
        var result = Emit(Parent("LoadDelegate", "didLoad:withAdInfo:"),
            Parent("InteractionDelegate", "didRecordImpression:withAdInfo:"),
            Parent("OtherDelegate", "other:didRecordImpressionWithAdInfo:"),
            new() { Name = "Combined", InheritedProtocolNames = ["LoadDelegate", "InteractionDelegate", "OtherDelegate"] });
        Assert.Contains("void DidRecordImpressionWithAdInfo2(", result);
        Assert.Contains("void DidRecordImpressionWithAdInfo(", result);
    }

    [Fact]
    public void RenamingParent_DoesNotLetSiblingStealFreedShortName()
    {
        var interaction = Parent("InteractionDelegate", "didRecordImpression:withAdInfo:") with
        {
            Methods = [Callback("didRecordImpression:withAdInfo:"), Callback("didClick:withAdInfo:")]
        };
        var result = Emit(Parent("LoadDelegate", "didLoad:withAdInfo:"), interaction,
            new() { Name = "Combined", InheritedProtocolNames = ["LoadDelegate", "InteractionDelegate"] });
        Assert.Contains("[Export(\"didClick:withAdInfo:\")]\n        void DidClickWithAdInfo(", result);
        Assert.Contains("[Export(\"didLoad:withAdInfo:\")]\n        void WithAdInfo(", result);
    }

    [Fact]
    public void IdenticalSelectorsInIndependentParents_KeepOneManagedName()
    {
        var result = Emit(Parent("FirstDelegate", "didLoad:withAdInfo:"), Parent("SecondDelegate", "didLoad:withAdInfo:"),
            new() { Name = "Combined", InheritedProtocolNames = ["FirstDelegate", "SecondDelegate"] });
        Assert.Equal(2, result.Split("void WithAdInfo(").Length - 1);
        Assert.DoesNotContain("WithAdInfo2(", result);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtocolParameterSpellings_UseTheEmittedInterfaceForDedup(bool classClash)
    {
        var direct = new ObjCTypeRef { Name = "Payload", IsPointer = true };
        var qualified = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["Payload"] };
        var first = Parent("LoadDelegate", "didLoad:withInfo:") with
        {
            Methods = [Callback("didLoad:withInfo:") with
            {
                Parameters = [new() { Name = "value", Type = direct }, new() { Name = "info", Type = SimpleType("NSInteger") }]
            }]
        };
        var second = Parent("RecordDelegate", "didRecord:withInfo:") with
        {
            Methods = [Callback("didRecord:withInfo:") with
            {
                Parameters = [new() { Name = "value", Type = qualified }, new() { Name = "info", Type = SimpleType("NSInteger") }]
            }]
        };
        var module = new ObjCModule
        {
            ModuleName = "Test", Protocols = [new() { Name = "Payload" }, first, second,
                new() { Name = "Combined", InheritedProtocolNames = [first.Name, second.Name] }],
            Classes = classClash ? [new() { Name = "Payload" }] : []
        };
        var result = EmitApiDefinition(module, "TestNamespace").ReplaceLineEndings("\n");
        var type = classClash ? "IPayloadProtocol" : "IPayload";
        Assert.Contains($"void WithInfo({type} value", result);
        Assert.Contains($"void DidRecordWithInfo({type} value", result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacementName_ReservesAdoptingClassMembers(bool property)
    {
        var cls = new ObjCClassDecl { Name = "Host", ProtocolNames = ["LoadDelegate", "RecordDelegate"] };
        if (property)
            cls.Properties.Add(new() { Name = "didRecordWithInfo", Type = SimpleType("NSInteger") });
        else
            cls.Methods.Add(Callback("didRecordWithInfo:other:"));
        var module = new ObjCModule
        {
            ModuleName = "Test", Classes = [cls],
            Protocols = [Parent("LoadDelegate", "didLoad:withInfo:"), Parent("RecordDelegate", "didRecord:withInfo:")]
        };
        var result = EmitApiDefinition(module, "TestNamespace").ReplaceLineEndings("\n");
        Assert.Contains("void DidRecordWithInfo2(", result);
        Assert.Contains(property ? "nint DidRecordWithInfo {" : "void DidRecordWithInfo(", result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonFlattenedClassSurface_DoesNotRenameProtocols(bool optional)
    {
        var first = Parent("LoadDelegate", "didLoad:withInfo:");
        var second = Parent("RecordDelegate", "didRecord:withInfo:");
        if (optional)
        {
            first = first with { Methods = [first.Methods[0] with { IsOptional = true }] };
            second = second with { Methods = [second.Methods[0] with { IsOptional = true }] };
        }
        var result = EmitApiDefinition(new ObjCModule
        {
            ModuleName = "Test", Protocols = [first, second],
            Classes = [new() { Name = "Host", SuperclassName = optional ? "NSObject" : "MissingBase",
                ProtocolNames = [first.Name, second.Name] }]
        }, "TestNamespace");
        Assert.Equal(2, result.Split("void WithInfo(").Length - 1);
        Assert.DoesNotContain("void DidRecordWithInfo(", result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EquivalentCSharpParameterShapes_Collide(bool byRef)
    {
        var firstType = byRef ? new ObjCTypeRef { Name = "int", IsPointer = true } : SimpleType("NSInteger");
        var secondType = byRef ? firstType : new ObjCTypeRef { Name = "void", IsPointer = true };
        ObjCProtocolDecl Make(string name, string selector, ObjCTypeRef type, ObjCParameterDirection direction) =>
            Parent(name, selector) with { Methods = [Callback(selector) with
            {
                Parameters = [new() { Name = "value", Type = type, Direction = direction },
                    new() { Name = "info", Type = SimpleType("NSInteger") }]
            }] };
        var result = Emit(Make("LoadDelegate", "didLoad:withInfo:", firstType, ObjCParameterDirection.Out),
            Make("RecordDelegate", "didRecord:withInfo:", secondType, ObjCParameterDirection.InOut),
            new() { Name = "Combined", InheritedProtocolNames = ["LoadDelegate", "RecordDelegate"] });
        Assert.Contains("void WithInfo(", result);
        Assert.Contains("void DidRecordWithInfo(", result);
    }

}
