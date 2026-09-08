// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCNativeContractEligibilityTests
{
    [Theory]
    [InlineData("packed")]
    [InlineData("aligned")]
    [InlineData("bitfield")]
    public void RefusedRecord_ClosesEveryMemberDependency_KeepingOrdinaryControls(string layout)
    {
        var bad = new ObjCStructDecl
        {
            Name = "BadRecord", IsPacked = layout == "packed", HasExplicitAlignment = layout == "aligned",
            HasBitFields = layout == "bitfield", NativeLayout = new ObjCRecordLayout(128, 128, [0]),
            Fields = [new() { Name = "value", Type = SimpleType("int") }]
        };
        var plain = new ObjCStructDecl { Name = "PlainRecord", Fields = [new() { Name = "value", Type = SimpleType("int") }] };
        var alias = new ObjCTypedefDecl { Name = "RecordAlias", UnderlyingType = SimpleType("BadRecord") };
        var block = new ObjCTypedefDecl
        {
            Name = "RecordCallback", UnderlyingType = new ObjCTypeRef
            {
                Name = "", IsBlock = true, BlockReturnType = SimpleType("void"), BlockParams = [SimpleType("RecordAlias")]
            }
        };
        var methods = new List<ObjCMethodDecl>
        {
            new() { Selector = "healthy", ReturnType = SimpleType("PlainRecord"), IsInstanceMethod = true },
            new() { Selector = "bad", ReturnType = SimpleType("RecordAlias"), IsInstanceMethod = true },
            new() { Selector = "consume:", ReturnType = SimpleType("void"), IsInstanceMethod = true,
                Parameters = [new() { Name = "value", Type = SimpleType("RecordAlias", isPointer: true) }] },
            new() { Selector = "callback:", ReturnType = SimpleType("void"), IsInstanceMethod = true,
                Parameters = [new() { Name = "value", Type = SimpleType("RecordCallback") }] }
        };
        var props = new List<ObjCPropertyDecl>
        {
            new() { Name = "badProperty", Type = SimpleType("RecordAlias") },
            new() { Name = "healthyProperty", Type = SimpleType("int") }
        };
        var module = new ObjCModule
        {
            ModuleName = "Contracts", Structs = [
                new() { Name = "OuterRecord", Fields = [new() { Name = "nested", Type = SimpleType("MiddleRecord") }] },
                new() { Name = "MiddleRecord", Fields = [new() { Name = "nested", Type = SimpleType("RecordAlias") }] }, bad, plain],
            Typedefs = [alias, block],
            Classes = [new() { Name = "ContractOwner", SuperclassName = "NSObject", Methods = methods, Properties = props }],
            Protocols = [new() { Name = "ContractProtocol", Methods = methods, Properties = props }],
            Categories = [new() { ClassName = "NSObject", CategoryName = "ContractExtras", Methods = methods, Properties = props }],
            Functions = [new() { Name = "badFunction", ReturnType = SimpleType("RecordAlias") },
                new() { Name = "healthyFunction", ReturnType = SimpleType("int") }],
            Constants = [new() { Name = "BadConstant", Type = SimpleType("RecordAlias"), IsExtern = true }]
        };
        var (api, apiDiagnostics) = EmitApiDefinitionWithDiagnostics(module);
        var (core, coreDiagnostics) = EmitStructsAndEnumsWithDiagnostics(module);
        Assert.Contains("Healthy", api);
        Assert.Contains("HealthyProperty", api);
        Assert.Contains("public struct PlainRecord", core);
        Assert.Contains("healthyFunction", core);
        foreach (var name in new[] { "BadRecord", "RecordAlias", "RecordCallback", "OuterRecord", "MiddleRecord" })
        {
            Assert.DoesNotContain(name, api);
            Assert.DoesNotContain(name, core);
        }
        foreach (var selector in new[] { "bad", "consume:", "callback:", "badProperty" })
            Assert.Equal(3, apiDiagnostics.SkippedSymbols.Count(s => s.SymbolName == selector));
        Assert.Contains(apiDiagnostics.SkippedSymbols, s => s.SymbolName == "BadConstant");
        Assert.Contains(coreDiagnostics.SkippedSymbols, s => s.SymbolName == "badFunction");
        Assert.Equal(ObjCSkipReason.UnsupportedConstruct, coreDiagnostics.SkippedSymbols.Single(s => s.SymbolName == "BadRecord").Reason);
        foreach (var dependent in new[] { "MiddleRecord", "OuterRecord", "badFunction", "RecordCallback" })
            Assert.Equal(ObjCSkipReason.UnresolvableType, coreDiagnostics.SkippedSymbols.Single(s => s.SymbolName == dependent).Reason);
        Assert.All(apiDiagnostics.SkippedSymbols.Where(s => s.SymbolName is "bad" or "consume:" or "callback:" or "badProperty"),
            s => Assert.Equal(ObjCSkipReason.UnresolvableType, s.Reason));
        Assert.Contains(coreDiagnostics.SkippedSymbols, s => s.SymbolName == "BadRecord" && s.Detail.Contains("native") || s.SymbolName == "BadRecord" && s.Detail.Contains("bitfield"));
    }

    [Fact]
    public void UnknownExplicitEnumValue_RefusesEnumAndMembers_NotImplicitControls()
    {
        var module = new ObjCModule
        {
            ModuleName = "Contracts",
            Enums = [new() { Name = "UnknownEnum", Cases = [new() { Name = "UnknownCase", HasExplicitValue = true }] },
                new() { Name = "OrdinaryEnum", Cases = [new() { Name = "OrdinaryZero" }, new() { Name = "OrdinaryOne" }] }],
            Classes = [new() { Name = "ContractOwner", SuperclassName = "NSObject", Methods = [
                new() { Selector = "bad", ReturnType = SimpleType("UnknownEnum"), IsInstanceMethod = true },
                new() { Selector = "healthy", ReturnType = SimpleType("OrdinaryEnum"), IsInstanceMethod = true }] }]
        };
        var (api, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        var core = EmitStructsAndEnums(module);
        Assert.DoesNotContain("UnknownEnum", api + core);
        Assert.Contains("OrdinaryEnum", api);
        Assert.Contains("public enum OrdinaryEnum", core);
        Assert.Contains(diagnostics.SkippedSymbols, s => s.SymbolName == "bad" && s.Detail.Contains("explicit enum initializer"));
        var settledDiagnostics = new ObjCBindingDiagnostics();
        var settled = ObjCEmissionEligibility.Prepare(module, settledDiagnostics);
        var records = ObjCBridgeRecordFactory.CreateRecords(settled, "Contracts", "Contracts", Logger);
        Assert.DoesNotContain(records, r => r.SwiftTypeName.Name == "UnknownEnum");
        Assert.Contains(records, r => r.SwiftTypeName.Name == "OrdinaryEnum");
        var dir = Path.Combine(Path.GetTempPath(), $"settled_contract_{Guid.NewGuid():N}");
        try
        {
            var count = settledDiagnostics.SkippedSymbols.Count;
            ApiDefinitionEmitter.Emit(settled, dir, "Contracts", Logger, settledDiagnostics);
            StructsAndEnumsEmitter.Emit(settled, dir, "Contracts", Logger, settledDiagnostics);
            Assert.Equal(count, settledDiagnostics.SkippedSymbols.Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("unsigned long long", "18446744073709551615UL", ulong.MaxValue, true)]
    [InlineData("unsigned long long", "9223372036854775808UL", 9223372036854775808UL, true)]
    [InlineData("long long", "-9223372036854775808", 9223372036854775808UL, false)]
    [InlineData("unsigned int", "4294967295", 4294967295UL, true)]
    [InlineData("int", "-1", ulong.MaxValue, false)]
    public void EvaluatedEnumBits_UseDeclaredBackingType(string backing, string literal, ulong bits, bool unsigned)
    {
        var module = new ObjCModule
        {
            ModuleName = "Contracts", Enums = [new() { Name = "BitsEnum", UnderlyingType = SimpleType(backing),
                Cases = [new() { Name = "BitsEnumValue", EvaluatedValue = new ObjCIntegerValue(bits, unsigned), HasExplicitValue = true }] }]
        };
        Assert.Contains($"Value = {literal},", EmitStructsAndEnums(module));
    }
}
