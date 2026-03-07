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
        Assert.Contains("public static class TestLibConstants", output);
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
                    Name = "RLMNotificationBlock",
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
        Assert.Contains("public delegate void RLMNotificationBlock(nint arg0, NSError arg1);", output);
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
        Assert.Contains("[MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]", output);
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

    // --- Fix: StructsAndEnums.cs must include using UIKit and CoreAnimation ---

    [Fact]
    public void Emit_IncludesUsingUIKit()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums = [new ObjCEnumDecl { Name = "TLFoo", Cases = [new ObjCEnumCaseDecl { Name = "TLFooBar" }] }]
        };

        var output = EmitAndRead(module);
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

        var output = EmitAndRead(module);
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

    private static string EmitAndRead(ObjCModule module, string ns = "TestLib.Binding")
    {
        var (content, _) = EmitBothFiles(module, ns);
        return content;
    }

    #nullable enable
    private static (string main, string? bgenDelegates) EmitBothFiles(ObjCModule module, string ns = "TestLib.Binding")
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"structs_enums_test_{Guid.NewGuid():N}");
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, tempDir, ns, Logger);
            Assert.NotNull(result);
            var main = File.ReadAllText(result!.FilePath);
            var bgen = result.BgenDelegatesFilePath != null
                ? File.ReadAllText(result.BgenDelegatesFilePath)
                : null;
            return (main, bgen);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
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
}
