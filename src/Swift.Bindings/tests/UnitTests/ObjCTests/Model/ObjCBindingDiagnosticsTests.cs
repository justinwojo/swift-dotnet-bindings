// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCBindingDiagnosticsTests
{
    [Fact]
    public void RecordSkip_AccumulatesEntries()
    {
        var diag = new ObjCBindingDiagnostics();
        diag.RecordSkip("Method", "doSomething:", ObjCSkipReason.UnresolvableType, "unresolvable return type 'foo_t'");
        diag.RecordSkip("Property", "Bar", ObjCSkipReason.UnresolvableType, "unresolvable type 'baz_t'");
        diag.RecordSkip("Function", "myFunc", ObjCSkipReason.AccessibilityConflict, "references module-local type");

        Assert.Equal(3, diag.SkippedSymbols.Count);
        Assert.Equal("doSomething:", diag.SkippedSymbols[0].SymbolName);
        Assert.Equal(ObjCSkipReason.UnresolvableType, diag.SkippedSymbols[0].Reason);
    }

    [Fact]
    public void LogSummary_EmptyDiagnostics_LogsNoneSkipped()
    {
        var diag = new ObjCBindingDiagnostics();
        // Should not throw
        diag.LogSummary(Logger);
        Assert.Empty(diag.SkippedSymbols);
    }

    [Fact]
    public void LogSummary_WithEntries_DoesNotThrow()
    {
        var diag = new ObjCBindingDiagnostics();
        diag.RecordSkip("Method", "foo:", ObjCSkipReason.UnresolvableType, "type 'x'");
        diag.RecordSkip("Struct", "Bar", ObjCSkipReason.UnsupportedConstruct, "bitfield");
        diag.LogSummary(Logger);
        Assert.Equal(2, diag.SkippedSymbols.Count);
    }

    [Fact]
    public void ApiDefinitionEmitter_RecordsDiagnostics_ForUnresolvableMethod()
    {
        var diag = new ObjCBindingDiagnostics();
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doThingWithFoo:",
                            ReturnType = new ObjCTypeRef { Name = "some_internal_c_type" },
                            IsInstanceMethod = true,
                        }
                    ]
                }
            ]
        };

        var dir = Path.Combine(Path.GetTempPath(), $"diag_test_{Guid.NewGuid():N}");
        try
        {
            ApiDefinitionEmitter.Emit(module, dir, "TestNamespace", Logger, diag);
            Assert.Single(diag.SkippedSymbols);
            Assert.Equal("doThingWithFoo:", diag.SkippedSymbols[0].SymbolName);
            Assert.Equal(ObjCSkipReason.UnresolvableType, diag.SkippedSymbols[0].Reason);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StructsAndEnumsEmitter_RecordsDiagnostics_ForUnresolvableStruct()
    {
        var diag = new ObjCBindingDiagnostics();
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "MyStruct",
                    Fields =
                    [
                        new ObjCStructField
                        {
                            Name = "data",
                            Type = new ObjCTypeRef { Name = "some_unknown_c_type" }
                        }
                    ]
                }
            ]
        };

        var dir = Path.Combine(Path.GetTempPath(), $"diag_test_{Guid.NewGuid():N}");
        try
        {
            StructsAndEnumsEmitter.Emit(module, dir, "TestNamespace", Logger, diag);
            Assert.Single(diag.SkippedSymbols);
            Assert.Equal("MyStruct", diag.SkippedSymbols[0].SymbolName);
            Assert.Equal(ObjCSkipReason.UnresolvableType, diag.SkippedSymbols[0].Reason);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
