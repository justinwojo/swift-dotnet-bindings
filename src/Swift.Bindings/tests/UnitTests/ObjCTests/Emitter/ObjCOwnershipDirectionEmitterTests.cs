// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCOwnershipDirectionEmitterTests
{
    [Theory]
    [InlineData(ObjCParameterDirection.InOut, "ref")]
    [InlineData(ObjCParameterDirection.In, "ref")]
    [InlineData(ObjCParameterDirection.Out, "out")]
    [InlineData(ObjCParameterDirection.Unspecified, "out")]
    public void Direction_AgreesAcrossApiAndCategoryForwarder(ObjCParameterDirection direction, string modifier)
    {
        var module = new ObjCModule { ModuleName = "Contracts", Categories = [new() {
            ClassName = "NSObject", CategoryName = "Extras", Methods = [Method(direction)] }] };
        var (api, forwarders, diagnostics) = EmitApiDefinitionWithCategoryStatics(module);
        Assert.Contains($"{modifier} int value", api);
        Assert.Contains($"{modifier} int value", forwarders);
        Assert.Contains($"{modifier} value", forwarders);
        Assert.Empty(diagnostics.SkippedSymbols);
    }

    [Fact]
    public void UnknownDirection_RefusesCompleteMember_WhileOutErrorControlSurvives()
    {
        var unknown = Method(ObjCParameterDirection.Unknown);
        var model = new ObjCModule { ModuleName = "Contracts", Classes = [new() {
            Name = "Owner", SuperclassName = "NSObject", Methods = [unknown, Method(ObjCParameterDirection.Out) with { Selector = "write:" }] }] };
        var (api, diagnostics) = EmitApiDefinitionWithDiagnostics(model);
        Assert.DoesNotContain("increment:", api);
        Assert.Contains("write:", api);
        Assert.Contains(diagnostics.SkippedSymbols, s => s.SymbolName == "increment:" && s.Detail.Contains("direction"));
    }

    [Fact]
    public void OwnedMethod_UsesProvenReturnRelease_AndConsumedMemberClosesAllContainers()
    {
        var owned = new ObjCMethodDecl { Selector = "factory", ReturnType = SimpleType("NSObject", isPointer: true), ReturnOwnership = ObjCReturnOwnership.Retained };
        var consumed = new ObjCMethodDecl { Selector = "consume:", ReturnType = SimpleType("void"), Parameters = [new() { Name = "value", Type = SimpleType("NSObject", isPointer: true), IsConsumed = true }] };
        var model = new ObjCModule { ModuleName = "Contracts", Classes = [new() { Name = "Owner", SuperclassName = "NSObject", Methods = [owned, consumed] }],
            Protocols = [new() { Name = "ContractProtocol", Methods = [consumed] }],
            Categories = [new() { ClassName = "NSObject", CategoryName = "Extras", Methods = [consumed] }],
            Functions = [new() { Name = "ConsumeFunction", ReturnType = SimpleType("void"), Parameters = consumed.Parameters }] };
        var (api, diagnostics) = EmitApiDefinitionWithDiagnostics(model);
        var (core, coreDiagnostics) = EmitStructsAndEnumsWithDiagnostics(model);
        Assert.Contains("[return: Release]", api);
        Assert.Contains("factory", api);
        Assert.DoesNotContain("consume:", api);
        Assert.DoesNotContain("ConsumeFunction", core);
        Assert.Equal(3, diagnostics.SkippedSymbols.Count(s => s.SymbolName == "consume:"));
        Assert.Contains(coreDiagnostics.SkippedSymbols, s => s.SymbolName == "ConsumeFunction");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedOrConsumingProperty_IsCompletelyRefused_KeepingBorrowedNeighbor(bool consumed)
    {
        var properties = new List<ObjCPropertyDecl> {
            new() { Name = "unsafeValue", Type = SimpleType("NSObject", isPointer: true),
                GetterOwnership = consumed ? ObjCReturnOwnership.Borrowed : ObjCReturnOwnership.Retained,
                HasConsumedAccessor = consumed },
            new() { Name = "borrowedValue", Type = SimpleType("NSObject", isPointer: true), GetterOwnership = ObjCReturnOwnership.Borrowed }
        };
        var model = new ObjCModule { ModuleName = "Contracts", Classes = [new() {
            Name = "Owner", SuperclassName = "NSObject", Properties = properties }],
            Categories = [new() { ClassName = "NSObject", CategoryName = "Extras", Properties = properties }] };
        var (api, diagnostics) = EmitApiDefinitionWithDiagnostics(model);
        Assert.DoesNotContain("unsafeValue", api);
        Assert.DoesNotContain("UnsafeValue", api);
        Assert.Contains("BorrowedValue", api);
        Assert.Equal(2, diagnostics.SkippedSymbols.Count(s => s.SymbolName == "unsafeValue"));
    }

    [Fact]
    public void NSError_ExplicitInputIsRefused_WhileConventionalOutputRemains()
    {
        var method = new ObjCMethodDecl { Selector = "error:", ReturnType = SimpleType("void"),
            Parameters = [new() { Name = "error", Type = ObjCTypeRefParser.Parse("NSError **"), Direction = ObjCParameterDirection.InOut }] };
        var model = new ObjCModule { ModuleName = "Contracts", Classes = [new() { Name = "Owner", SuperclassName = "NSObject", Methods = [method,
            method with { Selector = "output:", Parameters = [method.Parameters[0] with { Direction = ObjCParameterDirection.Unspecified }] }] }] };
        var (api, diagnostics) = EmitApiDefinitionWithDiagnostics(model);
        Assert.DoesNotContain("error:", api);
        Assert.Contains("[NullAllowed] out NSError error", api);
        Assert.Single(diagnostics.SkippedSymbols);
    }

    [Theory]
    [InlineData("int *", ObjCParameterDirection.Unknown)]
    [InlineData("NSError **", ObjCParameterDirection.Unknown)]
    [InlineData("NSError **", ObjCParameterDirection.In)]
    [InlineData("NSError **", ObjCParameterDirection.InOut)]
    public void RefusedAncestorPointer_DoesNotReserveDescendantPropertyName(string type, ObjCParameterDirection direction)
    {
        // Exercise the public emitter and its transitive inherited-name replay, not a private
        // predictor directly. The absent ancestor method must not erase the child's real property.
        var ancestor = new ObjCProtocolDecl { Name = "Ancestor", Methods = [new() {
            Selector = "increment:", ReturnType = SimpleType("void"), IsInstanceMethod = true,
            Parameters = [new() { Name = "value", Type = ObjCTypeRefParser.Parse(type), Direction = direction }] }] };
        var middle = new ObjCProtocolDecl { Name = "Middle", InheritedProtocolNames = ["Ancestor"] };
        var child = new ObjCProtocolDecl { Name = "Child", InheritedProtocolNames = ["Middle"],
            Properties = [new() { Name = "increment", Type = SimpleType("int"), IsReadonly = true }] };
        foreach (var protocols in new[] { new[] { ancestor, middle, child }, new[] { child, middle, ancestor } })
        {
            var (api, diagnostics) = EmitApiDefinitionWithDiagnostics(new ObjCModule {
                ModuleName = "Contracts", Protocols = protocols.ToList() });
            Assert.DoesNotContain("increment:", api);
            Assert.Contains("int Increment { get; }", api);
            var skip = Assert.Single(diagnostics.SkippedSymbols);
            Assert.Equal("increment:", skip.SymbolName);
            Assert.Equal(ObjCSkipReason.UnsupportedConstruct, skip.Reason);
        }
    }

    static ObjCMethodDecl Method(ObjCParameterDirection direction) => new() {
        Selector = "increment:", ReturnType = SimpleType("void"), IsInstanceMethod = false,
        Parameters = [new() { Name = "value", Type = ObjCTypeRefParser.Parse("int *"), Direction = direction }] };
}
