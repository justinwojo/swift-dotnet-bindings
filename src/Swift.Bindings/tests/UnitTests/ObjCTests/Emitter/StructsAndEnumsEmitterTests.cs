// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

public class StructsAndEnumsEmitterTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static ObjCTypeRef SimpleType(string name, bool isPointer = false) =>
        new() { Name = name, IsPointer = isPointer };

    [Fact]
    public void EmitEnum_WithPrefixStripping()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLStatus",
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "TLStatusIdle" },
                        new ObjCEnumCaseDecl { Name = "TLStatusActive" },
                        new ObjCEnumCaseDecl { Name = "TLStatusError" },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("Idle,", output);
        Assert.Contains("Active,", output);
        Assert.Contains("Error,", output);
        Assert.DoesNotContain("TLStatusIdle", output);
    }

    [Fact]
    public void EmitEnum_WithoutPrefixStripping_PartialMatch()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLStatus",
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "TLStatusIdle" },
                        new ObjCEnumCaseDecl { Name = "Unknown" },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("TLStatusIdle,", output);
        Assert.Contains("Unknown,", output);
    }

    [Fact]
    public void EmitEnum_NSOptions_HasFlagsAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLOptions",
                    IsOptions = true,
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "TLOptionsNone", Value = 0 },
                        new ObjCEnumCaseDecl { Name = "TLOptionsBold", Value = 1 },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Flags]", output);
        Assert.Contains(": ulong", output);
    }

    [Fact]
    public void EmitEnum_NSENUM_UsesLongBase()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLMode",
                    IsOptions = false,
                    Cases = [new ObjCEnumCaseDecl { Name = "TLModeDefault" }]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains(": long", output);
        Assert.DoesNotContain("[Flags]", output);
    }

    [Fact]
    public void EmitEnum_WithExplicitValues()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLPriority",
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "TLPriorityLow", Value = 0 },
                        new ObjCEnumCaseDecl { Name = "TLPriorityMedium", Value = 5 },
                        new ObjCEnumCaseDecl { Name = "TLPriorityHigh", Value = 10 },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("Low = 0,", output);
        Assert.Contains("Medium = 5,", output);
        Assert.Contains("High = 10,", output);
    }

    [Fact]
    public void EmitStruct_WithFields()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "TLPoint",
                    Fields =
                    [
                        new ObjCStructField { Name = "x", Type = SimpleType("CGFloat") },
                        new ObjCStructField { Name = "y", Type = SimpleType("CGFloat") },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[StructLayout(LayoutKind.Sequential)]", output);
        Assert.Contains("public struct TLPoint", output);
        Assert.Contains("public nfloat X;", output);
        Assert.Contains("public nfloat Y;", output);
    }

    [Fact]
    public void EmitConstant_NSString_AsField()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLErrorDomain",
                    Type = SimpleType("NSString", isPointer: true),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"TLErrorDomain\", \"__Internal\")]", output);
        Assert.Contains("public NSString TLErrorDomain { get; }", output);
    }

    [Fact]
    public void EmitConstant_Int_AsField()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "maxRetries",
                    Type = SimpleType("int"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"maxRetries\", \"__Internal\")]", output);
        Assert.Contains("public int MaxRetries { get; }", output);
    }

    [Fact]
    public void EmitConstant_UnsupportedType_EmitsTodoComment()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "someStruct",
                    Type = SimpleType("TLCustomStruct"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("// TODO: someStruct (TLCustomStruct)", output);
        Assert.Contains("[Field] not supported for this type", output);
    }

    [Fact]
    public void EmitFunction_AsDllImport()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Functions =
            [
                new ObjCFunctionDecl
                {
                    Name = "TLComputeDistance",
                    ReturnType = SimpleType("double"),
                    Parameters =
                    [
                        new ObjCParameterDecl { Name = "x", Type = SimpleType("double") },
                        new ObjCParameterDecl { Name = "y", Type = SimpleType("double") },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[DllImport(\"__Internal\")]", output);
        Assert.Contains("public static extern double TLComputeDistance(double x, double y);", output);
    }

    [Fact]
    public void ReturnsNull_WhenModuleHasNoRelevantDeclarations()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Classes = [new ObjCClassDecl { Name = "SomeClass" }],
        };

        var tempDir = Path.Combine(Path.GetTempPath(), $"structs_enums_test_{Guid.NewGuid():N}");
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, tempDir, "TestLib.Binding", Logger);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FullModule_EmitsMixedDeclarations()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLMode",
                    Cases = [new ObjCEnumCaseDecl { Name = "TLModeAuto", Value = 0 }]
                }
            ],
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "TLSize",
                    Fields = [new ObjCStructField { Name = "width", Type = SimpleType("CGFloat") }]
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLVersion",
                    Type = SimpleType("NSString", isPointer: true),
                    IsExtern = true
                }
            ],
            Functions =
            [
                new ObjCFunctionDecl
                {
                    Name = "TLInit",
                    ReturnType = SimpleType("void"),
                    Parameters = []
                }
            ]
        };

        var output = EmitAndRead(module);

        // Enum
        Assert.Contains("public enum TLMode : long", output);
        Assert.Contains("Auto = 0,", output);

        // Struct
        Assert.Contains("[StructLayout(LayoutKind.Sequential)]", output);
        Assert.Contains("public struct TLSize", output);
        Assert.Contains("public nfloat Width;", output);

        // Constants class
        Assert.Contains("public static partial class TestLibConstants", output);
        Assert.Contains("[Field(\"TLVersion\", \"__Internal\")]", output);

        // Function
        Assert.Contains("[DllImport(\"__Internal\")]", output);
        Assert.Contains("public static extern void TLInit();", output);
    }

    [Fact]
    public void Namespace_AppearsInOutput()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLFoo",
                    Cases = [new ObjCEnumCaseDecl { Name = "TLFooBar" }]
                }
            ]
        };

        var output = EmitAndRead(module, "My.Custom.Namespace");
        Assert.Contains("namespace My.Custom.Namespace", output);
    }

    [Fact]
    public void ConstantsClassName_MatchesModuleName()
    {
        var module = new ObjCModule
        {
            ModuleName = "MyFramework",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "version",
                    Type = SimpleType("NSInteger"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("public static partial class MyFrameworkConstants", output);
    }

    [Fact]
    public void EmitEnum_NoCases_EmitsEmptyBody()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums = [new ObjCEnumDecl { Name = "TLEmpty", Cases = [] }]
        };

        var output = EmitAndRead(module);
        Assert.Contains("public enum TLEmpty : long", output);
    }

    [Fact]
    public void EmitConstant_Nfloat_AsField()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "defaultScale",
                    Type = SimpleType("CGFloat"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"defaultScale\", \"__Internal\")]", output);
        Assert.Contains("public nfloat DefaultScale { get; }", output);
    }

    [Fact]
    public void EmitConstant_NonExtern_Skipped()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "kLocalConst",
                    Type = SimpleType("int"),
                    IsExtern = false
                }
            ]
        };

        var tempDir = Path.Combine(Path.GetTempPath(), $"structs_enums_test_{Guid.NewGuid():N}");
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, tempDir, "TestLib.Binding", Logger);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Emit_EnumCaseDigitPrefix_PrependedWithUnderscore()
    {
        var module = new ObjCModule
        {
            ModuleName = "Printer",
            Enums =
            [
                new()
                {
                    Name = "SpeedRate",
                    Cases =
                    [
                        new() { Name = "SpeedRate1ips" },
                        new() { Name = "SpeedRate2ips" },
                    ]
                }
            ]
        };
        var content = EmitAndRead(module, "Printer");
        Assert.Contains("_1ips", content);
        Assert.Contains("_2ips", content);
        Assert.DoesNotContain(" 1ips", content);
    }

    private static string EmitAndRead(ObjCModule module, string ns = "TestLib.Binding")
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"structs_enums_test_{Guid.NewGuid():N}");
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, tempDir, ns, Logger);
            Assert.NotNull(result);
            return File.ReadAllText(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
