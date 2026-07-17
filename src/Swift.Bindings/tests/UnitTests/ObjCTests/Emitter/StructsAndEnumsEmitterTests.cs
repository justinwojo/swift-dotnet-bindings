// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration;
using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class StructsAndEnumsEmitterTests
{

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
    public void EmitEnum_PerCaseAvailability_EmittedOnMemberIndependentOfEnumLevel()
    {
        // An NS_ENUM whose TYPE is introduced in ios 13 but where one CASE was deprecated later in
        // ios 15. Per-case [ObsoletedOSPlatform] must land on that member only — the enum body still
        // emits all members, and the unannotated case carries no attribute.
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLMode",
                    Availability = [new ObjCAvailability { Platform = "ios", IntroducedVersion = "13.0" }],
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "TLModeClassic", Value = 0 },
                        new ObjCEnumCaseDecl
                        {
                            Name = "TLModeLegacy",
                            Value = 1,
                            Availability =
                            [
                                new ObjCAvailability
                                {
                                    Platform = "ios",
                                    IntroducedVersion = "13.0",
                                    DeprecatedVersion = "15.0",
                                    Message = "use Classic"
                                }
                            ]
                        },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        // Enum-level attribute present on the type.
        Assert.Contains("SupportedOSPlatform(\"ios13.0\")", output);
        // Per-case deprecation attribute present.
        Assert.Contains("ObsoletedOSPlatform(\"ios15.0\", \"use Classic\")", output);
        // Both members still emit.
        Assert.Contains("Classic = 0,", output);
        Assert.Contains("Legacy = 1,", output);
        // The deprecation attribute attaches to the deprecated member, not the clean one.
        var attrIdx = output.IndexOf("ObsoletedOSPlatform(\"ios15.0\"", StringComparison.Ordinal);
        var legacyIdx = output.IndexOf("Legacy = 1,", StringComparison.Ordinal);
        var classicIdx = output.IndexOf("Classic = 0,", StringComparison.Ordinal);
        Assert.True(attrIdx >= 0 && attrIdx < legacyIdx);
        Assert.True(attrIdx > classicIdx, "per-case attribute must attach to the legacy member, not the classic one");
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
        Assert.Contains("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Sequential)]", output);
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
        Assert.Contains("public static NSString TLErrorDomain { get; }", output);
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
        Assert.Contains("public static int MaxRetries { get; }", output);
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
        Assert.Contains("[global::System.Runtime.InteropServices.DllImport(\"__Internal\")]", output);
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
        Assert.Contains("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Sequential)]", output);
        Assert.Contains("public struct TLSize", output);
        Assert.Contains("public nfloat Width;", output);

        // Constants class
        Assert.Contains("public static class TestLibConstants", output);
        Assert.Contains("[Field(\"TLVersion\", \"__Internal\")]", output);

        // Function
        Assert.Contains("[global::System.Runtime.InteropServices.DllImport(\"__Internal\")]", output);
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
    public void EmitStruct_FieldWithTypedefAlias_Resolved()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "MyFloat",
                    UnderlyingType = SimpleType("CGFloat")
                }
            ],
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "TLRect",
                    Fields =
                    [
                        new ObjCStructField { Name = "width", Type = SimpleType("MyFloat") }
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        // MyFloat should resolve to CGFloat → nfloat via typedefMap
        Assert.Contains("public nfloat Width;", output);
        Assert.DoesNotContain("MyFloat", output);
    }

    [Fact]
    public void EmitConstant_WithTypedefAlias_Resolved()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "MyInt",
                    UnderlyingType = SimpleType("NSInteger")
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "maxCount",
                    Type = SimpleType("MyInt"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        // MyInt should resolve to NSInteger → nint via typedefMap
        Assert.Contains("public static nint MaxCount { get; }", output);
        Assert.DoesNotContain("MyInt", output);
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
        Assert.Contains("public static class MyFrameworkConstants", output);
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

    // --- Enum backing type tests ---

    [Theory]
    [InlineData("NSInteger", "long")]
    [InlineData("NSUInteger", "ulong")]
    [InlineData("CFIndex", "long")]
    [InlineData("unsigned long", "ulong")]
    [InlineData("long", "long")]
    public void EmitEnum_NativeWidthTypes_EmitNativeAttribute(string underlyingType, string expectedBase)
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLNative",
                    UnderlyingType = new ObjCTypeRef { Name = underlyingType },
                    Cases = [new ObjCEnumCaseDecl { Name = "TLNativeA", Value = 0 }]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains($": {expectedBase}", output);
        Assert.Contains("[Native]", output);
    }

    [Theory]
    [InlineData("uint8_t", "byte")]
    [InlineData("unsigned char", "byte")]
    [InlineData("int8_t", "sbyte")]
    [InlineData("signed char", "sbyte")]
    [InlineData("int16_t", "short")]
    [InlineData("short", "short")]
    [InlineData("uint16_t", "ushort")]
    [InlineData("unsigned short", "ushort")]
    [InlineData("int32_t", "int")]
    [InlineData("int", "int")]
    [InlineData("uint32_t", "uint")]
    [InlineData("unsigned int", "uint")]
    [InlineData("int64_t", "long")]
    [InlineData("long long", "long")]
    [InlineData("uint64_t", "ulong")]
    [InlineData("unsigned long long", "ulong")]
    public void EmitEnum_FixedWidthTypes_NoNativeAttribute(string underlyingType, string expectedBase)
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLFixed",
                    UnderlyingType = new ObjCTypeRef { Name = underlyingType },
                    Cases = [new ObjCEnumCaseDecl { Name = "TLFixedA", Value = 0 }]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains($": {expectedBase}", output);
        Assert.DoesNotContain("[Native]", output);
    }

    [Fact]
    public void EmitEnum_FlagsWithFixedWidth_OrthogonalConcerns()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLMask",
                    IsOptions = true,
                    UnderlyingType = new ObjCTypeRef { Name = "uint32_t" },
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "TLMaskNone", Value = 0 },
                        new ObjCEnumCaseDecl { Name = "TLMaskBold", Value = 1 },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Flags]", output);
        Assert.Contains(": uint", output);
        Assert.DoesNotContain("[Native]", output);
    }

    [Fact]
    public void EmitEnum_NoUnderlyingType_DefaultsToLongWithNative()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLDefault",
                    Cases = [new ObjCEnumCaseDecl { Name = "TLDefaultA" }]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains(": long", output);
        Assert.Contains("[Native]", output);
    }

    [Fact]
    public void EmitEnum_UnknownUnderlyingType_FallsBackToLongWithNative()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLUnknown",
                    UnderlyingType = new ObjCTypeRef { Name = "some_custom_type_t" },
                    Cases = [new ObjCEnumCaseDecl { Name = "TLUnknownA" }]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains(": long", output);
        Assert.Contains("[Native]", output);
    }

    [Fact]
    public void EmitEnum_UnknownUnderlyingType_Flags_FallsBackToUlongWithNative()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLUnknownOpts",
                    IsOptions = true,
                    UnderlyingType = new ObjCTypeRef { Name = "some_custom_type_t" },
                    Cases = [new ObjCEnumCaseDecl { Name = "TLUnknownOptsNone", Value = 0 }]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains(": ulong", output);
        Assert.Contains("[Native]", output);
        Assert.Contains("[Flags]", output);
    }

    [Fact]
    public void EmitEnum_FlagsNoUnderlyingType_DefaultsToUlongWithNative()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLOpts",
                    IsOptions = true,
                    Cases = [new ObjCEnumCaseDecl { Name = "TLOptsNone", Value = 0 }]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains(": ulong", output);
        Assert.Contains("[Flags]", output);
        Assert.Contains("[Native]", output);
    }

    [Fact]
    public void EmitEnum_TypedefAliasedBackingType_ResolvesCorrectly()
    {
        // When clang reports a typedef alias (e.g., MyEnumBase) as the underlying type,
        // the emitter should resolve through the typedef map to find the real C type.
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "MyEnumBase", UnderlyingType = new ObjCTypeRef { Name = "uint32_t" } }
            ],
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLResult",
                    UnderlyingType = new ObjCTypeRef { Name = "MyEnumBase" },
                    Cases = [new ObjCEnumCaseDecl { Name = "TLResultOk", Value = 0 }]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains(": uint", output);
        Assert.DoesNotContain("[Native]", output);
    }

    [Fact]
    public void EmitEnum_TypedefAliasedNativeWidth_ResolvesWithNative()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "PBType", UnderlyingType = new ObjCTypeRef { Name = "NSInteger" } }
            ],
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLMode",
                    UnderlyingType = new ObjCTypeRef { Name = "PBType" },
                    Cases = [new ObjCEnumCaseDecl { Name = "TLModeDefault", Value = 0 }]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains(": long", output);
        Assert.Contains("[Native]", output);
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
        Assert.Contains("public static nfloat DefaultScale { get; }", output);
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

    [Fact]
    public void Emit_BlockTypedef_EmitsDelegateDeclaration()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "MOSNotificationBlock",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams =
                        [
                            new ObjCTypeRef { Name = "NSInteger" },
                            new ObjCTypeRef { Name = "NSError", IsPointer = true },
                        ]
                    }
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("public delegate void MOSNotificationBlock(nint arg0, NSError arg1);", output);
    }

    [Fact]
    public void Emit_BlockTypedef_WithReturnType_EmitsDelegate()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "MyPredicate",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "BOOL" },
                        BlockParams =
                        [
                            new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        ]
                    }
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("public delegate bool MyPredicate(string arg0);", output);
    }

    [Fact]
    public void Emit_ModuleWithOnlyBlockTypedefs_EmitsFile()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "CompletionHandler",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                    }
                }
            ]
        };

        var tempDir = Path.Combine(Path.GetTempPath(), $"structs_enums_test_{Guid.NewGuid():N}");
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, tempDir, "TestLib.Binding", Logger);
            Assert.NotNull(result);
            var content = File.ReadAllText(result!.FilePath);
            Assert.Contains("public delegate void CompletionHandler();", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EmitStruct_WithFixedSizeArray_EmitsMarshalAs()
    {
        // Fixed-size arrays come from the parser via FixedArraySize on ObjCTypeRef
        // (clang qualType "uint8_t [4]" → Name="uint8_t", FixedArraySize=4)
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "TLColor",
                    Fields =
                    [
                        new ObjCStructField
                        {
                            Name = "components",
                            Type = new ObjCTypeRef { Name = "uint8_t", FixedArraySize = 4 }
                        }
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains(
            "[global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 4)]",
            output);
        Assert.Contains("public byte[] Components;", output);
    }

    [Fact]
    public void EmitFunction_ReferencingModuleLocalType_Skipped()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Classes = [new ObjCClassDecl { Name = "MyClient" }],
            // Include a safe function alongside the one referencing a module-local type
            Functions =
            [
                new ObjCFunctionDecl
                {
                    Name = "TLCreateClient",
                    ReturnType = new ObjCTypeRef { Name = "MyClient", IsPointer = true },
                    Parameters = []
                },
                new ObjCFunctionDecl
                {
                    Name = "TLVersion",
                    ReturnType = SimpleType("int"),
                    Parameters = []
                }
            ]
        };

        var output = EmitAndRead(module);
        // Safe function is emitted
        Assert.Contains("TLVersion", output);
        // Function referencing module-local type (MyClient) is skipped
        Assert.DoesNotContain("TLCreateClient", output);
    }

    [Fact]
    public void EmitBlockDelegate_ReferencingModuleLocalType_Skipped()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Classes = [new ObjCClassDecl { Name = "MyResponse" }],
            Typedefs =
            [
                // This one references MyResponse (module-local) — should be skipped
                new ObjCTypedefDecl
                {
                    Name = "ResponseHandler",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams =
                        [
                            new ObjCTypeRef { Name = "MyResponse", IsPointer = true }
                        ]
                    }
                },
                // This one is safe — should be emitted
                new ObjCTypedefDecl
                {
                    Name = "CompletionHandler",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                    }
                }
            ]
        };

        var output = EmitAndRead(module);
        // Safe delegate is emitted
        Assert.Contains("public delegate void CompletionHandler();", output);
        // Delegate referencing module-local type is skipped
        Assert.DoesNotContain("ResponseHandler", output);
    }

    [Fact]
    public void EmitConstantsClass_NotPartial_NoStaticAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "kVersion",
                    Type = SimpleType("int"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        // Constants class should be "public static class" (not partial, no [Static] attribute)
        Assert.Contains("public static class TestLibConstants", output);
        Assert.DoesNotContain("partial class TestLibConstants", output);
        Assert.DoesNotContain("[Static]", output);
    }

    // --- StructsAndEnums.cs must include using UIKit and CoreAnimation when the
    // target platform supports them (here: explicit iOS PlatformInfo, which is the
    // only platform that exercises UIKit availability without relying on the
    // null-PlatformInfo legacy fallback). Platform-conditional filtering of these
    // usings is covered separately in ObjCUsingsEmitterTests. ---

    [Fact]
    public void Emit_IncludesUsingUIKit()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums = [new ObjCEnumDecl { Name = "TLFoo", Cases = [new ObjCEnumCaseDecl { Name = "TLFooBar" }] }]
        };

        var output = EmitStructsAndEnums(module, platformInfo: PlatformInfoFactory.Create(ApplePlatform.iOS));
        Assert.Contains("using UIKit;", output);
    }

    [Fact]
    public void Emit_IncludesUsingCoreAnimation()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums = [new ObjCEnumDecl { Name = "TLFoo", Cases = [new ObjCEnumCaseDecl { Name = "TLFooBar" }] }]
        };

        var output = EmitStructsAndEnums(module, platformInfo: PlatformInfoFactory.Create(ApplePlatform.iOS));
        Assert.Contains("using CoreAnimation;", output);
    }

    // --- Fix: void* function params emit as IntPtr ---

    [Fact]
    public void EmitFunction_VoidPointerParam_EmitsIntPtr()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Functions =
            [
                new ObjCFunctionDecl
                {
                    Name = "TLProcess",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    Parameters =
                    [
                        new ObjCParameterDecl { Name = "data", Type = new ObjCTypeRef { Name = "void", IsPointer = true } },
                        new ObjCParameterDecl { Name = "size", Type = new ObjCTypeRef { Name = "uint32_t" } },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("public static extern void TLProcess(IntPtr data, uint size);", output);
    }

    private static string EmitAndRead(ObjCModule module, string ns = "TestLib.Binding") =>
        EmitStructsAndEnums(module, ns);

    #nullable enable
    private static (string main, string? bgenDelegates) EmitBothFiles(ObjCModule module, string ns = "TestLib.Binding") =>
        EmitStructsAndEnumsBoth(module, ns);

    /// <summary>Emit through StructsAndEnumsEmitter passing the cross-boundary excluded Swift type
    /// names (the Swift-side types not re-emitted into the ObjC binding).</summary>
    private static string EmitWithExclusions(ObjCModule module, HashSet<string> excludeTypeNames, string ns = "TestLib.Binding")
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"structs_enums_test_{Guid.NewGuid():N}");
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, tempDir, ns, Logger, diagnostics: null, platformInfo: null, excludeTypeNames: excludeTypeNames);
            Assert.NotNull(result);
            return File.ReadAllText(result!.FilePath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Emit_CrossBoundaryDelegateAndDependentFunction_DroppedWhenExcluded()
    {
        // A mixed (ObjC+Swift) binding passes the Swift-emitted type names as exclusions so the ObjC
        // side doesn't re-emit them. A block-typedef delegate whose signature references such a type
        // (here ExampleMetadata) has no C# definition in the ObjC assembly, so it — and any C
        // function that takes/returns it, or that references the excluded type directly — must be
        // dropped (else CS0246). Clean delegates/functions are unaffected.
        var module = new ObjCModule
        {
            ModuleName = "Quick",
            Typedefs =
            [
                // Tainted: a block param references the excluded Swift type.
                new ObjCTypedefDecl
                {
                    Name = "ExampleMetadataBlock",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams = [new ObjCTypeRef { Name = "ExampleMetadata", IsPointer = true }]
                    }
                },
                // Clean: references only resolvable types.
                new ObjCTypedefDecl
                {
                    Name = "CleanBlock",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams = [new ObjCTypeRef { Name = "NSInteger" }]
                    }
                }
            ],
            Functions =
            [
                // Dropped: takes the tainted (dropped) delegate.
                new ObjCFunctionDecl
                {
                    Name = "qck_beforeEachWithMetadata",
                    ReturnType = SimpleType("void"),
                    Parameters = [new ObjCParameterDecl { Name = "block", Type = SimpleType("ExampleMetadataBlock") }]
                },
                // Dropped: references the excluded Swift type directly.
                new ObjCFunctionDecl
                {
                    Name = "qck_directMetadata",
                    ReturnType = SimpleType("void"),
                    Parameters = [new ObjCParameterDecl { Name = "meta", Type = SimpleType("ExampleMetadata", isPointer: true) }]
                },
                // Kept: takes the clean delegate.
                new ObjCFunctionDecl
                {
                    Name = "qck_clean",
                    ReturnType = SimpleType("void"),
                    Parameters = [new ObjCParameterDecl { Name = "block", Type = SimpleType("CleanBlock") }]
                }
            ]
        };

        // Baseline (no exclusions): everything emits, including the cross-boundary references.
        var baseline = EmitAndRead(module);
        Assert.Contains("ExampleMetadataBlock", baseline);
        Assert.Contains("qck_beforeEachWithMetadata", baseline);
        Assert.Contains("qck_directMetadata", baseline);

        // With the Swift-side type excluded: tainted delegate + dependent functions drop, clean kept.
        var output = EmitWithExclusions(module, new HashSet<string> { "ExampleMetadata" });
        Assert.DoesNotContain("ExampleMetadata", output);
        Assert.DoesNotContain("ExampleMetadataBlock", output);
        Assert.DoesNotContain("qck_beforeEachWithMetadata", output);
        Assert.DoesNotContain("qck_directMetadata", output);
        Assert.Contains("public delegate void CleanBlock(nint arg0);", output);
        Assert.Contains("qck_clean", output);
    }

    [Fact]
    public void Emit_TransitiveCrossBoundaryDelegate_DroppedViaFixpoint()
    {
        // A delegate that references ANOTHER (already-dropped) cross-boundary delegate must also be
        // dropped — the fixpoint pass catches the transitive case. WrapperBlock takes a parameter
        // typed as the tainted ExampleMetadataBlock, so it drops on the second iteration.
        var module = new ObjCModule
        {
            ModuleName = "Quick",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "ExampleMetadataBlock",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams = [new ObjCTypeRef { Name = "ExampleMetadata", IsPointer = true }]
                    }
                },
                new ObjCTypedefDecl
                {
                    Name = "WrapperBlock",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams = [new ObjCTypeRef { Name = "ExampleMetadataBlock" }]
                    }
                }
            ]
        };

        var output = EmitWithExclusions(module, new HashSet<string> { "ExampleMetadata" });
        Assert.DoesNotContain("ExampleMetadataBlock", output);
        Assert.DoesNotContain("WrapperBlock", output);
    }

    [Fact]
    public void Emit_TransitiveDelegateViaAliasOfDroppedDelegate_AlsoDropped()
    {
        // The transitive drop must also resolve typedef aliases: a second delegate whose param is a
        // typedef alias of an already-dropped block-typedef delegate is itself unbindable. The alias
        // name passes a raw membership check, but ObjCTypeMapper.MapType resolves it back to the
        // dropped delegate at emit time, so the fixpoint's dropped-delegate test must walk the alias
        // chain. (ExampleMetadataBlock drops for its excluded param; AliasToDroppedBlock aliases it;
        // WrapperViaAliasBlock takes the alias and must drop too — otherwise it emits a reference to
        // the missing delegate, CS0246.)
        var module = new ObjCModule
        {
            ModuleName = "Quick",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "ExampleMetadataBlock",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams = [new ObjCTypeRef { Name = "ExampleMetadata", IsPointer = true }]
                    }
                },
                // Non-block typedef alias of the (to-be-dropped) block-typedef delegate.
                new ObjCTypedefDecl
                {
                    Name = "AliasToDroppedBlock",
                    UnderlyingType = new ObjCTypeRef { Name = "ExampleMetadataBlock" }
                },
                // Second delegate whose param is the ALIAS, not the dropped delegate's raw name.
                new ObjCTypedefDecl
                {
                    Name = "WrapperViaAliasBlock",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams = [new ObjCTypeRef { Name = "AliasToDroppedBlock" }]
                    }
                }
            ]
        };

        // Baseline (no exclusions): both delegates emit; the alias resolves cleanly.
        var baseline = EmitAndRead(module);
        Assert.Contains("ExampleMetadataBlock", baseline);
        Assert.Contains("WrapperViaAliasBlock", baseline);

        // With the Swift-side type excluded: the dropped delegate AND the alias-dependent delegate
        // both drop — the alias never re-introduces a reference to the missing delegate.
        var output = EmitWithExclusions(module, new HashSet<string> { "ExampleMetadata" });
        Assert.DoesNotContain("ExampleMetadata", output);
        Assert.DoesNotContain("WrapperViaAliasBlock", output);
    }

    [Fact]
    public void Emit_TypedefAliasOfExcludedType_DropsDependentDelegateAndFunction()
    {
        // A typedef alias of an excluded Swift type (typedef ExampleMetadata *ExampleMetadataAlias;)
        // must not slip past the cross-boundary exclusion. A raw-name check sees only the alias name
        // and lets the delegate/function through, but ObjCTypeMapper.MapType resolves the alias at
        // emit time and re-emits the excluded ExampleMetadata — producing a reference to an undefined
        // type (CS0246). Both the delegate-drop fixpoint and the function taint check must resolve
        // the typedef chain.
        var module = new ObjCModule
        {
            ModuleName = "Quick",
            Typedefs =
            [
                // Non-block alias of the excluded Swift type.
                new ObjCTypedefDecl
                {
                    Name = "ExampleMetadataAlias",
                    UnderlyingType = new ObjCTypeRef { Name = "ExampleMetadata", IsPointer = true }
                },
                // A block whose param is the ALIAS (not the raw excluded name) → exercises the
                // delegate-drop fixpoint's alias resolution.
                new ObjCTypedefDecl
                {
                    Name = "AliasMetadataBlock",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams = [new ObjCTypeRef { Name = "ExampleMetadataAlias" }]
                    }
                }
            ],
            Functions =
            [
                // A function param typed on the alias → exercises EmitFunction's taint resolution.
                new ObjCFunctionDecl
                {
                    Name = "qck_aliasMetadata",
                    ReturnType = SimpleType("void"),
                    Parameters = [new ObjCParameterDecl { Name = "meta", Type = SimpleType("ExampleMetadataAlias") }]
                }
            ]
        };

        // Baseline (no exclusions): both the alias-typed delegate and function emit.
        var baseline = EmitAndRead(module);
        Assert.Contains("AliasMetadataBlock", baseline);
        Assert.Contains("qck_aliasMetadata", baseline);

        // With the Swift-side type excluded: alias resolution catches both — neither the dropped
        // delegate nor the dependent function survives, and the resolved excluded name never appears.
        var output = EmitWithExclusions(module, new HashSet<string> { "ExampleMetadata" });
        Assert.DoesNotContain("ExampleMetadata", output);
        Assert.DoesNotContain("AliasMetadataBlock", output);
        Assert.DoesNotContain("qck_aliasMetadata", output);
    }

    [Fact]
    public void EmitBlockDelegate_SkipsProtocolMethodBlockParams()
    {
        // Block typedef used as a parameter in a protocol method should NOT be emitted
        // because MAUI bgen auto-generates the delegate type from the protocol binding.
        var blockType = new ObjCTypeRef
        {
            Name = "",
            IsBlock = true,
            BlockReturnType = SimpleType("void"),
        };
        blockType.BlockParams.Add(SimpleType("NSData", isPointer: true));

        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "MyCompletionBlock", UnderlyingType = blockType },
                new ObjCTypedefDecl { Name = "MyOtherBlock", UnderlyingType = blockType },
            ],
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "MyProtocol",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doSomething:",
                            ReturnType = SimpleType("void"),
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "completion", Type = SimpleType("MyCompletionBlock") }],
                        }
                    ]
                }
            ]
        };

        var content = EmitAndRead(module);
        // MyCompletionBlock is used in a protocol method — bgen will auto-generate it
        Assert.DoesNotContain("MyCompletionBlock", content);
        // MyOtherBlock is NOT used in any protocol method — we should emit it
        Assert.Contains("MyOtherBlock", content);
    }

    [Fact]
    public void EmitBlockDelegate_KeepsProtocolBlockWhenAlsoUsedByFunction()
    {
        // If a block typedef is used in both a protocol method AND a C function,
        // we must keep emitting the delegate — the function signature needs it.
        var blockType = new ObjCTypeRef
        {
            Name = "",
            IsBlock = true,
            BlockReturnType = SimpleType("void"),
        };
        blockType.BlockParams.Add(SimpleType("NSData", isPointer: true));

        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "SharedBlock", UnderlyingType = blockType },
            ],
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "MyProtocol",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doSomething:",
                            ReturnType = SimpleType("void"),
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "completion", Type = SimpleType("SharedBlock") }],
                        }
                    ]
                }
            ],
            Functions =
            [
                new ObjCFunctionDecl
                {
                    Name = "RegisterHandler",
                    ReturnType = SimpleType("void"),
                    Parameters = [new ObjCParameterDecl { Name = "handler", Type = SimpleType("SharedBlock") }],
                }
            ]
        };

        var content = EmitAndRead(module);
        // SharedBlock is used by a function — must be emitted even though protocol also uses it
        Assert.Contains("SharedBlock", content);
    }

    [Fact]
    public void EmitBlockDelegate_KeepsProtocolBlockWhenFunctionUsesAlias()
    {
        // typedef void (^OriginalBlock)(NSData *);
        // typedef OriginalBlock AliasBlock;
        // Protocol uses OriginalBlock, function uses AliasBlock.
        // EmitFunction resolves AliasBlock → OriginalBlock via typedefMap,
        // so the delegate must be preserved.
        var blockType = new ObjCTypeRef
        {
            Name = "",
            IsBlock = true,
            BlockReturnType = SimpleType("void"),
        };
        blockType.BlockParams.Add(SimpleType("NSData", isPointer: true));

        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "OriginalBlock", UnderlyingType = blockType },
                // AliasBlock is a non-block typedef alias → resolved by typedefMap
                new ObjCTypedefDecl { Name = "AliasBlock", UnderlyingType = SimpleType("OriginalBlock") },
            ],
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "MyProtocol",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doSomething:",
                            ReturnType = SimpleType("void"),
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "completion", Type = SimpleType("OriginalBlock") }],
                        }
                    ]
                }
            ],
            Functions =
            [
                new ObjCFunctionDecl
                {
                    Name = "RegisterHandler",
                    ReturnType = SimpleType("void"),
                    // Function uses the ALIAS, not the block typedef directly
                    Parameters = [new ObjCParameterDecl { Name = "handler", Type = SimpleType("AliasBlock") }],
                }
            ]
        };

        var content = EmitAndRead(module);
        // OriginalBlock must be emitted — function uses AliasBlock which resolves to it
        Assert.Contains("OriginalBlock", content);
    }

    [Fact]
    public void BgenDelegates_NestedBlockTypedefs_EmittedInSeparateFile()
    {
        // When a block typedef is used as a parameter of another block typedef
        // (which is a property type), bgen auto-generates the inner delegate in
        // SupportDelegates.g.cs. We must emit it in BgenDelegates.cs (for bgen to
        // resolve during ApiDefinition parsing) but NOT in StructsAndEnums.cs.
        var innerBlock = new ObjCTypeRef
        {
            Name = "Block",
            IsBlock = true,
            BlockReturnType = SimpleType("void"),
            BlockParams = [SimpleType("NSInputStream", isPointer: true)],
        };
        var outerBlock = new ObjCTypeRef
        {
            Name = "Block",
            IsBlock = true,
            BlockReturnType = SimpleType("void"),
            BlockParams = [SimpleType("InnerHandler")], // references inner typedef by name
        };

        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "InnerHandler", UnderlyingType = innerBlock },
                new ObjCTypedefDecl { Name = "OuterHandler", UnderlyingType = outerBlock },
            ],
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "handler",
                            Type = SimpleType("OuterHandler"), // property type is the outer typedef
                        }
                    ]
                }
            ]
        };

        var (main, bgenDelegates) = EmitBothFiles(module);

        // InnerHandler should be in BgenDelegates.cs, not StructsAndEnums.cs
        Assert.DoesNotContain("InnerHandler", main);
        Assert.NotNull(bgenDelegates);
        Assert.Contains("InnerHandler", bgenDelegates);

        // OuterHandler is also bgen-used (direct property type) — also in BgenDelegates.cs
        Assert.DoesNotContain("OuterHandler", main);
        Assert.Contains("OuterHandler", bgenDelegates);
    }

    [Fact]
    public void BgenDelegates_FunctionUsedDelegate_StaysInStructsAndEnums()
    {
        // When a block typedef is used by both a C function and as a nested block param,
        // it must remain in StructsAndEnums.cs (function signatures reference it).
        var innerBlock = new ObjCTypeRef
        {
            Name = "Block",
            IsBlock = true,
            BlockReturnType = SimpleType("void"),
            BlockParams = [SimpleType("bool")],
        };
        var outerBlock = new ObjCTypeRef
        {
            Name = "Block",
            IsBlock = true,
            BlockReturnType = SimpleType("void"),
            BlockParams = [SimpleType("CompletionBlock")],
        };

        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "CompletionBlock", UnderlyingType = innerBlock },
                new ObjCTypedefDecl { Name = "WrapperBlock", UnderlyingType = outerBlock },
            ],
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "wrapper",
                            Type = SimpleType("WrapperBlock"),
                        }
                    ]
                }
            ],
            Functions =
            [
                new ObjCFunctionDecl
                {
                    Name = "RunCompletion",
                    ReturnType = SimpleType("void"),
                    Parameters = [new ObjCParameterDecl { Name = "cb", Type = SimpleType("CompletionBlock") }],
                }
            ]
        };

        var (main, _) = EmitBothFiles(module);

        // CompletionBlock is used by function → must be in StructsAndEnums.cs
        Assert.Contains("CompletionBlock", main);
    }

    [Fact]
    public void BgenDelegates_NoBgenUsage_NoBgenDelegatesFile()
    {
        // When no block typedefs are used in binding members, no BgenDelegates.cs is emitted.
        var block = new ObjCTypeRef
        {
            Name = "Block",
            IsBlock = true,
            BlockReturnType = SimpleType("void"),
        };

        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "SimpleCallback", UnderlyingType = block },
            ],
        };

        var (main, bgenDelegates) = EmitBothFiles(module);

        // No bgen usage → delegate emitted in main file, no BgenDelegates.cs
        Assert.Contains("SimpleCallback", main);
        Assert.Null(bgenDelegates);
    }

    [Fact]
    public void EmitStruct_FunctionPointerField_MappedToIntPtr()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "Callbacks",
                    Fields =
                    [
                        new ObjCStructField
                        {
                            Name = "handler",
                            Type = new ObjCTypeRef
                            {
                                Name = "FunctionPointer",
                                IsFunctionPointer = true,
                                RawQualType = "bool (*)(int, float)",
                            }
                        }
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("public IntPtr Handler;", output);
    }

    [Fact]
    public void EmitStruct_SelfReferentialField_MappedToIntPtr()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "LinkedNode",
                    Fields =
                    [
                        new ObjCStructField
                        {
                            Name = "value",
                            Type = SimpleType("int"),
                        },
                        new ObjCStructField
                        {
                            Name = "next",
                            Type = SimpleType("LinkedNode"),
                        }
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        // Self-referential field should be IntPtr to avoid CS0523
        Assert.Contains("public IntPtr Next;", output);
        Assert.Contains("public int Value;", output);
    }

    // ──────────────────────────────────────────────
    // Function filtering — unresolvable C-type params
    // ──────────────────────────────────────────────

    [Fact]
    public void EmitFunction_SkipsUnresolvableCTypeParam()
    {
        // Function with a snake_case C-internal type parameter should be skipped
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Functions =
            [
                new ObjCFunctionDecl
                {
                    Name = "pb_encode_tag",
                    ReturnType = SimpleType("bool"),
                    Parameters = [new ObjCParameterDecl { Name = "wire_type", Type = SimpleType("pb_wire_type_t") }]
                },
                new ObjCFunctionDecl
                {
                    Name = "good_function",
                    ReturnType = SimpleType("int"),
                    Parameters = [new ObjCParameterDecl { Name = "value", Type = SimpleType("int") }]
                }
            ]
        };

        var output = EmitAndRead(module);
        // Function with unresolvable C type should be skipped
        Assert.DoesNotContain("pb_encode_tag", output);
        // Function with known types should be emitted
        Assert.Contains("good_function", output);
    }

    [Fact]
    public void EmitStruct_WithAppleFrameworkFieldType_Emits()
    {
        // Struct with an Apple framework type field (CamelCase) should NOT be skipped
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "PixelFormat",
                    Fields =
                    [
                        new ObjCStructField { Name = "bitmapInfo", Type = SimpleType("CGBitmapInfo") },
                        new ObjCStructField { Name = "alignment", Type = SimpleType("size_t") }
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        // Struct should be emitted — CGBitmapInfo is an Apple framework type (uppercase)
        Assert.Contains("public struct PixelFormat", output);
        Assert.Contains("public CGBitmapInfo BitmapInfo;", output);
        Assert.Contains("public nuint Alignment;", output);
    }

    [Fact]
    public void EmitStruct_SignedCharField_MapsToSbyte()
    {
        // Struct with signed char field should emit correctly with sbyte mapping
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "CompactField",
                    Fields =
                    [
                        new ObjCStructField { Name = "size_offset", Type = SimpleType("signed char") },
                        new ObjCStructField { Name = "tag", Type = SimpleType("int") }
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("public struct CompactField", output);
        Assert.Contains("public sbyte Size_offset;", output);
    }

    [Fact]
    public void EmitStruct_WithUnsafeLayout_SkipsEmission()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "BitfieldStruct",
                    HasUnsafeLayout = true,
                    UnsafeLayoutReason = "contains bitfield",
                    Fields =
                    [
                        new ObjCStructField { Name = "value", Type = SimpleType("int") }
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.DoesNotContain("public struct BitfieldStruct", output);
    }

    [Fact]
    public void EmitStruct_WithUnsafeLayout_RecordsDiagnostic()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "UnionStruct",
                    HasUnsafeLayout = true,
                    UnsafeLayoutReason = "contains anonymous union/struct",
                    Fields = []
                }
            ]
        };

        var diag = new ObjCBindingDiagnostics();
        var tempDir = Path.Combine(Path.GetTempPath(), $"structs_enums_test_{Guid.NewGuid():N}");
        try
        {
            StructsAndEnumsEmitter.Emit(module, tempDir, "TestLib.Binding", Logger, diag);
            Assert.Contains(diag.SkippedSymbols, s => s.SymbolName == "UnionStruct" && s.Reason == ObjCSkipReason.UnsupportedConstruct);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    // ──────────────────────────────────────────────
    // Non-sequential enum value emission tests
    // ──────────────────────────────────────────────

    [Fact]
    public void EmitEnum_NonSequentialValues_EmitsExplicitValues()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "STDSErrorCode",
                    UnderlyingType = SimpleType("NSInteger"),
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "STDSErrorCodeAssertionFailed", Value = 204 },
                        new ObjCEnumCaseDecl { Name = "STDSErrorCodeUnrecognizedID", Value = 203 },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("AssertionFailed = 204,", output);
        Assert.Contains("UnrecognizedID = 203,", output);
    }

    [Fact]
    public void EmitEnum_HighStartValue_EmitsExplicitValues()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "HighEnum",
                    UnderlyingType = SimpleType("NSInteger"),
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "HighEnumBase", Value = 20000 },
                        new ObjCEnumCaseDecl { Name = "HighEnumNext", Value = 20001 },
                        new ObjCEnumCaseDecl { Name = "HighEnumGap", Value = 20100 },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("Base = 20000,", output);
        Assert.Contains("Next = 20001,", output);
        Assert.Contains("Gap = 20100,", output);
    }

    [Fact]
    public void EmitEnum_WithGaps_EmitsAllExplicitValues()
    {
        // HTTP-like status codes with gaps
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "HTTPStatus",
                    UnderlyingType = SimpleType("NSInteger"),
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "HTTPStatusOK", Value = 200 },
                        new ObjCEnumCaseDecl { Name = "HTTPStatusCreated", Value = 201 },
                        new ObjCEnumCaseDecl { Name = "HTTPStatusNoContent", Value = 204 },
                        new ObjCEnumCaseDecl { Name = "HTTPStatusFound", Value = 302 },
                        new ObjCEnumCaseDecl { Name = "HTTPStatusNotFound", Value = 404 },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("OK = 200,", output);
        Assert.Contains("Created = 201,", output);
        Assert.Contains("NoContent = 204,", output);
        Assert.Contains("Found = 302,", output);
        Assert.Contains("NotFound = 404,", output);
    }

    [Fact]
    public void EmitEnum_NegativeValues_EmitsExplicitValues()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "SignedEnum",
                    UnderlyingType = SimpleType("NSInteger"),
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "SignedEnumError", Value = -1 },
                        new ObjCEnumCaseDecl { Name = "SignedEnumNone", Value = 0 },
                        new ObjCEnumCaseDecl { Name = "SignedEnumOK", Value = 1 },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("Error = -1,", output);
        Assert.Contains("None = 0,", output);
        Assert.Contains("OK = 1,", output);
    }

    [Fact]
    public void EmitEnum_NoExplicitValues_OmitsValueAssignment()
    {
        // When values are null (not extracted), emit without assignment
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "SimpleEnum",
                    UnderlyingType = SimpleType("NSInteger"),
                    Cases =
                    [
                        new ObjCEnumCaseDecl { Name = "SimpleEnumA" },
                        new ObjCEnumCaseDecl { Name = "SimpleEnumB" },
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        // Should emit without value assignments
        Assert.Contains("A,", output);
        Assert.Contains("B,", output);
        Assert.DoesNotContain("= ", output.Substring(output.IndexOf("public enum")));
    }

    // ──────────────────────────────────────────────
    // Typedef'd NSString constant [Field] emission
    // ──────────────────────────────────────────────

    [Fact]
    public void EmitConstant_TypedefNSString_EmitsFieldProperty()
    {
        // e.g., typedef NSString *MOSNotification; extern MOSNotification const MOSStoreDidChange;
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "MOSNotification",
                    UnderlyingType = SimpleType("NSString", isPointer: true)
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "MOSStoreDidChange",
                    Type = SimpleType("MOSNotification"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"MOSStoreDidChange\", \"__Internal\")]", output);
        Assert.Contains("public static NSString MOSStoreDidChange { get; }", output);
        Assert.DoesNotContain("TODO", output);
    }

    [Fact]
    public void EmitConstant_TypedefNSStringPointerUsage_EmitsFieldProperty()
    {
        // typedef NSString MOSNotification; usage: MOSNotification *
        // (typedef drops pointer, usage adds it)
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "TLNotification",
                    UnderlyingType = SimpleType("NSString")
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLDidComplete",
                    Type = SimpleType("TLNotification", isPointer: true),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"TLDidComplete\", \"__Internal\")]", output);
        Assert.Contains("public static NSString TLDidComplete { get; }", output);
        Assert.DoesNotContain("TODO", output);
    }

    [Fact]
    public void EmitConstant_ChainedTypedefNSString_EmitsFieldProperty()
    {
        // typedef NSString *BaseNotification; typedef BaseNotification DerivedNotification;
        // Chain: DerivedNotification → BaseNotification → NSString*
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "BaseNotification",
                    UnderlyingType = SimpleType("NSString", isPointer: true)
                },
                new ObjCTypedefDecl
                {
                    Name = "DerivedNotification",
                    UnderlyingType = SimpleType("BaseNotification")
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLSomeEvent",
                    Type = SimpleType("DerivedNotification"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        // The typedef chain resolves DerivedNotification → NSString*
        Assert.Contains("[Field(\"TLSomeEvent\", \"__Internal\")]", output);
        Assert.Contains("public static NSString TLSomeEvent { get; }", output);
        Assert.DoesNotContain("TODO", output);
    }

    [Fact]
    public void EmitConstant_DirectNSString_StillWorks()
    {
        // Verify that the existing direct NSString* path still works after the typedef fix
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLDirectString",
                    Type = SimpleType("NSString", isPointer: true),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"TLDirectString\", \"__Internal\")]", output);
        Assert.Contains("public static NSString TLDirectString { get; }", output);
    }

    [Fact]
    public void EmitConstant_NonNSStringTypedef_StillEmitsTodo()
    {
        // Typedef to a non-NSString type should still emit TODO
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "CustomType",
                    UnderlyingType = SimpleType("SomeStruct")
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLCustomConst",
                    Type = SimpleType("CustomType"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("TODO", output);
    }

    [Fact]
    public void EmitStruct_ReferencingSkippedStruct_IsAlsoSkipped()
    {
        // When struct A has a union (unsafe layout) and struct B references struct A
        // as a field type, struct B should also be skipped because A won't be emitted.
        // This prevents CS0246 errors for undefined struct types.
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "InnerInfo",
                    HasUnsafeLayout = true,
                    UnsafeLayoutReason = "contains anonymous union",
                    Fields =
                    [
                        new ObjCStructField { Name = "value", Type = SimpleType("int") }
                    ]
                },
                new ObjCStructDecl
                {
                    Name = "OuterEvent",
                    Fields =
                    [
                        new ObjCStructField { Name = "event_type", Type = SimpleType("int") },
                        new ObjCStructField { Name = "info", Type = SimpleType("InnerInfo") }
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        // InnerInfo has unsafe layout → skipped
        Assert.DoesNotContain("public struct InnerInfo", output);
        // OuterEvent references InnerInfo which is skipped → also skipped
        Assert.DoesNotContain("public struct OuterEvent", output);
    }

    [Fact]
    public void EmitStruct_ReferencingEmittableStruct_IsEmitted()
    {
        // When struct A is emittable and struct B references A, both should be emitted.
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Structs =
            [
                new ObjCStructDecl
                {
                    Name = "Point",
                    Fields =
                    [
                        new ObjCStructField { Name = "x", Type = SimpleType("float") },
                        new ObjCStructField { Name = "y", Type = SimpleType("float") }
                    ]
                },
                new ObjCStructDecl
                {
                    Name = "Rect",
                    Fields =
                    [
                        new ObjCStructField { Name = "origin", Type = SimpleType("Point") },
                        new ObjCStructField { Name = "width", Type = SimpleType("float") },
                        new ObjCStructField { Name = "height", Type = SimpleType("float") }
                    ]
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("public struct Point", output);
        Assert.Contains("public struct Rect", output);
        Assert.Contains("public Point Origin;", output);
    }
}
