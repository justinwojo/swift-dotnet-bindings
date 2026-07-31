// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ClangAstParserTests
{
    private const string HeadersPath = DefaultHeadersPath;

    [Fact]
    public void Parse_ObjCInterfaceDecl_CreatesClass()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "protocols": [{ "name": "NSCoding" }],
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Single(module.Classes);
        var cls = module.Classes[0];
        Assert.Equal("MyClass", cls.Name);
        Assert.Equal("NSObject", cls.SuperclassName);
        Assert.Single(cls.ProtocolNames);
        Assert.Equal("NSCoding", cls.ProtocolNames[0]);
    }

    [Fact]
    public void Parse_ObjCInterfaceDecl_WithObjCRuntimeNameAttr_SetsHasCustomRuntimeName()
    {
        // Clang emits an ObjCRuntimeNameAttr child for __attribute__((objc_runtime_name("...")))
        // but omits the string argument in JSON; we record only its presence.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "PublicName",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                { "kind": "ObjCRuntimeNameAttr" }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Single(module.Classes);
        Assert.True(module.Classes[0].HasCustomRuntimeName);
    }

    [Fact]
    public void Parse_ObjCInterfaceDecl_WithoutRuntimeNameAttr_HasCustomRuntimeNameFalse()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "PlainClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Single(module.Classes);
        Assert.False(module.Classes[0].HasCustomRuntimeName);
    }

    [Fact]
    public void Parse_ObjCInterfaceDecl_WithNestedMethods()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doSomething",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "count",
                    "instance": false,
                    "returnType": { "qualType": "NSInteger" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        Assert.Equal(2, module.Classes[0].Methods.Count);

        var instanceMethod = module.Classes[0].Methods[0];
        Assert.Equal("doSomething", instanceMethod.Selector);
        Assert.True(instanceMethod.IsInstanceMethod);

        var classMethod = module.Classes[0].Methods[1];
        Assert.Equal("count", classMethod.Selector);
        Assert.False(classMethod.IsInstanceMethod);
    }

    [Fact]
    public void Parse_ObjCInterfaceDecl_WithNestedProperties()
    {
        // Real clang AST includes implicit getter/setter ObjCMethodDecl nodes
        // alongside properties. The parser must filter these out.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "title",
                    "type": { "qualType": "NSString *" },
                    "readwrite": true
                },
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "isEnabled",
                    "type": { "qualType": "BOOL" },
                    "readonly": true
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "explicitMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCMethodDecl",
                    "isImplicit": true,
                    "name": "title",
                    "instance": true,
                    "returnType": { "qualType": "NSString *" },
                    "inner": []
                },
                {
                    "kind": "ObjCMethodDecl",
                    "isImplicit": true,
                    "name": "setTitle:",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCMethodDecl",
                    "isImplicit": true,
                    "name": "isEnabled",
                    "instance": true,
                    "returnType": { "qualType": "BOOL" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        Assert.Equal(2, module.Classes[0].Properties.Count);
        Assert.Equal("title", module.Classes[0].Properties[0].Name);
        Assert.False(module.Classes[0].Properties[0].IsReadonly);
        Assert.Equal("isEnabled", module.Classes[0].Properties[1].Name);
        Assert.True(module.Classes[0].Properties[1].IsReadonly);
        // Only the explicit method should be present — implicit accessors filtered out
        Assert.Single(module.Classes[0].Methods);
        Assert.Equal("explicitMethod", module.Classes[0].Methods[0].Selector);
    }

    [Fact]
    public void Parse_ObjCMethodDecl_WithNestedParmVarDecls()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Manager",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "initWithDelegate:queue:",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": [
                        {
                            "kind": "ParmVarDecl",
                            "name": "delegate",
                            "type": { "qualType": "id<NSCoding> _Nullable" }
                        },
                        {
                            "kind": "ParmVarDecl",
                            "name": "queue",
                            "type": { "qualType": "dispatch_queue_t _Nullable" }
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        var method = module.Classes[0].Methods[0];
        Assert.Equal("initWithDelegate:queue:", method.Selector);
        Assert.Equal(2, method.Parameters.Count);
        Assert.Equal("delegate", method.Parameters[0].Name);
        Assert.Equal("queue", method.Parameters[1].Name);
    }

    [Fact]
    public void Parse_ObjCProtocolDecl_WithOptionalProperty()
    {
        // In real clang AST, properties carry control:"optional" but methods do NOT.
        // This test verifies the property's control field is handled correctly.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCProtocolDecl",
            "name": "MyDelegate",
            {{MakeLoc()}},
            "protocols": [{ "name": "NSObject" }],
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "willStart",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "status",
                    "type": { "qualType": "NSInteger" },
                    "control": "optional",
                    "readonly": true
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Protocols);
        var proto = module.Protocols[0];
        Assert.Equal("MyDelegate", proto.Name);
        Assert.Single(proto.InheritedProtocolNames);
        Assert.Single(proto.Methods);
        // Method has no control field -> required (source file not available for unit test)
        Assert.False(proto.Methods[0].IsOptional);
        // Property has control:"optional" -> optional
        Assert.Single(proto.Properties);
        Assert.True(proto.Properties[0].IsOptional);
    }

    [Fact]
    public void Parse_ObjCProtocolDecl_OptionalMethodsFromSourceFile()
    {
        // Clang JSON does not mark methods as optional — we must read the source file
        // and find @optional/@required section boundaries to infer method optionality.
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_parser_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var headerPath = Path.Combine(tempDir, "TestLib.h");
            File.WriteAllText(headerPath, """
                #import <Foundation/Foundation.h>
                @protocol MyDelegate <NSObject>
                @required
                - (void)willStart;
                @optional
                - (void)didFinish;
                - (void)didCancel;
                @required
                - (void)mustImplement;
                @end
                """);

            // Build AST JSON with loc pointing to the real file + lines
            var json = $$"""
            {
                "kind": "TranslationUnitDecl",
                "inner": [
                    {
                        "kind": "ObjCProtocolDecl",
                        "name": "MyDelegate",
                        "loc": { "file": "{{headerPath.Replace("\\", "\\\\")}}", "line": 2 },
                        "range": { "begin": { "line": 2 }, "end": { "line": 10 } },
                        "protocols": [{ "name": "NSObject" }],
                        "inner": [
                            {
                                "kind": "ObjCMethodDecl",
                                "name": "willStart",
                                "loc": { "line": 4 },
                                "instance": true,
                                "returnType": { "qualType": "void" },
                                "inner": []
                            },
                            {
                                "kind": "ObjCMethodDecl",
                                "name": "didFinish",
                                "loc": { "line": 6 },
                                "instance": true,
                                "returnType": { "qualType": "void" },
                                "inner": []
                            },
                            {
                                "kind": "ObjCMethodDecl",
                                "name": "didCancel",
                                "loc": { "line": 7 },
                                "instance": true,
                                "returnType": { "qualType": "void" },
                                "inner": []
                            },
                            {
                                "kind": "ObjCMethodDecl",
                                "name": "mustImplement",
                                "loc": { "line": 9 },
                                "instance": true,
                                "returnType": { "qualType": "void" },
                                "inner": []
                            }
                        ]
                    }
                ]
            }
            """;

            var module = ClangAstParser.Parse(json, "TestLib", tempDir);
            Assert.Single(module.Protocols);
            var proto = module.Protocols[0];
            Assert.Equal(4, proto.Methods.Count);
            Assert.False(proto.Methods[0].IsOptional, "willStart should be required (@required section)");
            Assert.True(proto.Methods[1].IsOptional, "didFinish should be optional (@optional section)");
            Assert.True(proto.Methods[2].IsOptional, "didCancel should be optional (@optional section)");
            Assert.False(proto.Methods[3].IsOptional, "mustImplement should be required (@required section)");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void Parse_ObjCProtocolDecl_OptionalMethodsFallbackCurrentFile()
    {
        // When clang omits loc.file from a protocol (because it's defined in an included header
        // and the file context is inherited from a previous declaration), the parser must fall back
        // to the currentFile tracked in the main parsing loop. This simulates the umbrella header
        // scenario: umbrella.h includes a header that defines a protocol with no loc.file.
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_parser_fallback_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            // The included header that contains the protocol
            var includedHeader = Path.Combine(tempDir, "LogFramework.h");
            File.WriteAllText(includedHeader, """
                #import <Foundation/Foundation.h>
                @protocol LogFrameworkLogger <NSObject>
                @required
                - (void)logMessage:(id)logMessage;
                @optional
                - (void)didAddLogger;
                - (void)willRemoveLogger;
                @end
                """);

            // Build AST JSON simulating umbrella-include behavior:
            // - First node has loc.file pointing to LogFramework.h (sets currentFile)
            // - Protocol node has loc WITHOUT file (only line) — simulates inherited file context
            var json = $$"""
            {
                "kind": "TranslationUnitDecl",
                "inner": [
                    {
                        "kind": "ObjCInterfaceDecl",
                        "name": "LogFrameworkAbstractLogger",
                        "loc": { "file": "{{includedHeader.Replace("\\", "\\\\")}}", "line": 1 },
                        "super": { "name": "NSObject" },
                        "inner": []
                    },
                    {
                        "kind": "ObjCProtocolDecl",
                        "name": "LogFrameworkLogger",
                        "loc": { "line": 2 },
                        "range": { "begin": { "line": 2 }, "end": { "line": 8 } },
                        "protocols": [{ "name": "NSObject" }],
                        "inner": [
                            {
                                "kind": "ObjCMethodDecl",
                                "name": "logMessage:",
                                "loc": { "line": 4 },
                                "instance": true,
                                "returnType": { "qualType": "void" },
                                "inner": [
                                    { "kind": "ParmVarDecl", "name": "logMessage", "type": { "qualType": "id" } }
                                ]
                            },
                            {
                                "kind": "ObjCMethodDecl",
                                "name": "didAddLogger",
                                "loc": { "line": 6 },
                                "instance": true,
                                "returnType": { "qualType": "void" },
                                "inner": []
                            },
                            {
                                "kind": "ObjCMethodDecl",
                                "name": "willRemoveLogger",
                                "loc": { "line": 7 },
                                "instance": true,
                                "returnType": { "qualType": "void" },
                                "inner": []
                            }
                        ]
                    }
                ]
            }
            """;

            var module = ClangAstParser.Parse(json, "TestLib", tempDir);
            Assert.Single(module.Protocols);
            var proto = module.Protocols[0];
            Assert.Equal(3, proto.Methods.Count);
            Assert.False(proto.Methods[0].IsOptional, "logMessage: should be required (@required section)");
            Assert.True(proto.Methods[1].IsOptional, "didAddLogger should be optional (@optional section)");
            Assert.True(proto.Methods[2].IsOptional, "willRemoveLogger should be optional (@optional section)");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void Parse_ObjCCategoryDecl_MergesOntoClass()
    {
        // In real clang AST, the category's owning class is in "interface.name",
        // and "name" is the category name (e.g., "Extras" in Widget(Extras)).
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "id": "0x1", "kind": "ObjCInterfaceDecl", "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "categoryMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        Assert.Equal(2, module.Classes[0].Methods.Count);
        Assert.Contains(module.Classes[0].Methods, m => m.Selector == "categoryMethod");
    }

    [Fact]
    public void CategoryMerge_MethodsTaggedAsFromCategory()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "id": "0x1", "kind": "ObjCInterfaceDecl", "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "extraMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var cls = module.Classes[0];
        var catMethod = cls.Methods.First(m => m.Selector == "extraMethod");
        Assert.True(catMethod.IsFromCategory);
    }

    [Fact]
    public void CategoryMerge_OriginalMethodsNotTagged()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "id": "0x1", "kind": "ObjCInterfaceDecl", "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "extraMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var cls = module.Classes[0];
        var originalMethod = cls.Methods.First(m => m.Selector == "init");
        Assert.False(originalMethod.IsFromCategory);
    }

    [Fact]
    public void CategoryMerge_PropertiesTaggedAsFromCategory()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "title",
                    "type": { "qualType": "NSString *" }
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "id": "0x1", "kind": "ObjCInterfaceDecl", "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "subtitle",
                    "type": { "qualType": "NSString *" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var cls = module.Classes[0];
        var catProp = cls.Properties.First(p => p.Name == "subtitle");
        Assert.True(catProp.IsFromCategory);
        var origProp = cls.Properties.First(p => p.Name == "title");
        Assert.False(origProp.IsFromCategory);
    }

    [Fact]
    public void Parse_EnumDecl_NSEnum_WithNestedConstants()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "MyEnum",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "MyEnumFirst",
                    "inner": [
                        {
                            "kind": "ConstantExpr",
                            "value": "0"
                        }
                    ]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "MyEnumSecond",
                    "inner": [
                        {
                            "kind": "ConstantExpr",
                            "value": "1"
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Enums);
        var e = module.Enums[0];
        Assert.Equal("MyEnum", e.Name);
        Assert.False(e.IsOptions);
        Assert.NotNull(e.UnderlyingType);
        Assert.Equal("NSInteger", e.UnderlyingType!.Name);
        Assert.Equal(2, e.Cases.Count);
        Assert.Equal("MyEnumFirst", e.Cases[0].Name);
        Assert.Equal(0L, e.Cases[0].Value);
        Assert.Equal("MyEnumSecond", e.Cases[1].Name);
        Assert.Equal(1L, e.Cases[1].Value);
    }

    [Fact]
    public void Parse_EnumDecl_NSOptions_HasFlagEnumAttr()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "MyOptions",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "NSUInteger" },
            "inner": [
                { "kind": "FlagEnumAttr" },
                {
                    "kind": "EnumConstantDecl",
                    "name": "MyOptionsNone",
                    "inner": [{ "kind": "ConstantExpr", "value": "0" }]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "MyOptionsBold",
                    "inner": [{ "kind": "ConstantExpr", "value": "1" }]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Enums);
        Assert.True(module.Enums[0].IsOptions);
        Assert.Equal(2, module.Enums[0].Cases.Count);
    }

    [Fact]
    public void Parse_RecordDecl_WithNestedFieldDecls()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "RecordDecl",
            "name": "MyPoint",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "FieldDecl",
                    "name": "x",
                    "type": { "qualType": "float" }
                },
                {
                    "kind": "FieldDecl",
                    "name": "y",
                    "type": { "qualType": "float" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Structs);
        Assert.Equal("MyPoint", module.Structs[0].Name);
        Assert.Equal(2, module.Structs[0].Fields.Count);
        Assert.Equal("x", module.Structs[0].Fields[0].Name);
        Assert.Equal("y", module.Structs[0].Fields[1].Name);
    }

    [Fact]
    public void Parse_RecordDecl_WithBitfield_FlagsUnsafeLayout()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "RecordDecl",
            "name": "BitfieldStruct",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "FieldDecl",
                    "name": "flags",
                    "type": { "qualType": "unsigned int" },
                    "isBitfield": true
                },
                {
                    "kind": "FieldDecl",
                    "name": "value",
                    "type": { "qualType": "int" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Structs);
        var s = module.Structs[0];
        Assert.True(s.HasUnsafeLayout);
        Assert.Contains("bitfield", s.UnsafeLayoutReason!);
        // Only non-bitfield fields are captured
        Assert.Single(s.Fields);
        Assert.Equal("value", s.Fields[0].Name);
    }

    [Fact]
    public void Parse_RecordDecl_WithAnonymousUnion_FlagsUnsafeLayout()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "RecordDecl",
            "name": "UnionStruct",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "FieldDecl",
                    "name": "tag",
                    "type": { "qualType": "int" }
                },
                {
                    "kind": "RecordDecl",
                    "inner": [
                        {
                            "kind": "FieldDecl",
                            "name": "intVal",
                            "type": { "qualType": "int" }
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Structs);
        var s = module.Structs[0];
        Assert.True(s.HasUnsafeLayout);
        Assert.Contains("anonymous", s.UnsafeLayoutReason!);
    }

    [Fact]
    public void Parse_TypedefPromotedAnonymousStruct_WithBitfield_FlagsUnsafeLayout()
    {
        // Clang emits anonymous struct as sibling, then typedef referencing it
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "RecordDecl",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "FieldDecl",
                    "name": "flags",
                    "type": { "qualType": "unsigned int" },
                    "isBitfield": true
                }
            ]
        },
        {
            "kind": "TypedefDecl",
            "name": "BitfieldAlias",
            {{MakeLoc()}},
            "type": { "qualType": "struct (unnamed)" }
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var promoted = module.Structs.FirstOrDefault(s => s.Name == "BitfieldAlias");
        Assert.NotNull(promoted);
        Assert.True(promoted!.HasUnsafeLayout);
        Assert.Contains("bitfield", promoted.UnsafeLayoutReason!);
    }

    [Fact]
    public void Parse_RecordDecl_WithoutUnsafeLayout_NoFlag()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "RecordDecl",
            "name": "SafeStruct",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "FieldDecl",
                    "name": "x",
                    "type": { "qualType": "float" }
                },
                {
                    "kind": "FieldDecl",
                    "name": "y",
                    "type": { "qualType": "float" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Structs);
        Assert.False(module.Structs[0].HasUnsafeLayout);
        Assert.Null(module.Structs[0].UnsafeLayoutReason);
    }

    [Fact]
    public void Parse_FunctionDecl_WithNestedParmVarDecls()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "FunctionDecl",
            "name": "MyFunction",
            {{MakeLoc()}},
            "type": { "qualType": "int (float, float)" },
            "inner": [
                {
                    "kind": "ParmVarDecl",
                    "name": "x",
                    "type": { "qualType": "float" }
                },
                {
                    "kind": "ParmVarDecl",
                    "name": "y",
                    "type": { "qualType": "float" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Functions);
        Assert.Equal("MyFunction", module.Functions[0].Name);
        Assert.Equal("int", module.Functions[0].ReturnType.Name);
        Assert.Equal(2, module.Functions[0].Parameters.Count);
    }

    [Fact]
    public void Parse_VarDecl_ExternConst_CreatesConstant()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "VarDecl",
            "name": "MyConstant",
            {{MakeLoc()}},
            "type": { "qualType": "NSString *" },
            "storageClass": "extern"
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Constants);
        Assert.Equal("MyConstant", module.Constants[0].Name);
        Assert.True(module.Constants[0].IsExtern);
        Assert.Equal("NSString", module.Constants[0].Type.Name);
    }

    [Fact]
    public void Parse_TypedefDecl_CreatesTypedef()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "TypedefDecl",
            "name": "MyCallback",
            {{MakeLoc()}},
            "type": { "qualType": "void (^)(NSString *)" },
            "inner": [
                {
                    "kind": "BlockPointerType",
                    "qualType": "void (^)(NSString *)"
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Typedefs);
        Assert.Equal("MyCallback", module.Typedefs[0].Name);
    }

    [Fact]
    public void Parse_AvailabilityAttr_NoSourceOffset_RecoversNothing()
    {
        // clang's -ast-dump=json emits AvailabilityAttr nodes that carry ONLY {id, kind, range}
        // — never the platform/introduced/deprecated fields. (Verified against a live clang probe,
        // Xcode 26.3.) Finding 22 (a2) recovers the data by reading the consumer header at the
        // attribute's range.begin source offset (see ClangAstParserAvailabilityTests). This node
        // carries only a `loc` with no usable range.begin offset, so there is nothing to read back:
        // recovery degrades cleanly to no availability while the class and its method still parse.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "NewWidget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    {{MakeLoc()}}
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doStuff",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": [
                        {
                            "kind": "AvailabilityAttr",
                            {{MakeLoc()}}
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // The AvailabilityAttr nodes recover nothing (no source offset); the class and its method
        // parse normally and carry no availability.
        Assert.Single(module.Classes);
        Assert.Equal("NewWidget", module.Classes[0].Name);
        Assert.Empty(module.Classes[0].Availability);
        var method = module.Classes[0].Methods[0];
        Assert.Equal("doStuff", method.Selector);
        Assert.Empty(method.Availability);
    }

    [Fact]
    public void Parse_FiltersTransitiveIncludes_MultiFieldFallback()
    {
        // Declaration from framework headers (should be included)
        var publicDecl = $$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "PublicClass",
            "loc": { "file": "{{HeadersPath}}/TestLib.h" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        }
        """;

        // Declaration from system headers (should be excluded)
        var systemDecl = """
        {
            "kind": "ObjCInterfaceDecl",
            "name": "NSObject",
            "loc": { "file": "/usr/include/objc/NSObject.h" },
            "super": {},
            "inner": []
        }
        """;

        // Declaration with expansionLoc (macro-expanded, should be included)
        var macroDecl = $$"""
        {
            "kind": "EnumDecl",
            "name": "MacroEnum",
            "loc": { "expansionLoc": { "file": "{{HeadersPath}}/TestLib.h" } },
            "inner": [
                { "kind": "EnumConstantDecl", "name": "MacroEnumA" }
            ]
        }
        """;

        // Declaration with spellingLoc (should be included)
        var spellingDecl = $$"""
        {
            "kind": "VarDecl",
            "name": "SpellingConst",
            "loc": { "spellingLoc": { "file": "{{HeadersPath}}/Const.h" } },
            "type": { "qualType": "int" }
        }
        """;

        // Declaration with includedFrom pointing to our framework headers (sub-header case — should be included)
        var includedFromDecl = $$"""
        {
            "kind": "VarDecl",
            "name": "IncludedConst",
            "loc": { "includedFrom": { "file": "{{HeadersPath}}/TestLib.h" } },
            "type": { "qualType": "int" }
        }
        """;

        var json = WrapInTranslationUnit($"{publicDecl},{systemDecl},{macroDecl},{spellingDecl},{includedFromDecl}");
        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // PublicClass included
        Assert.Single(module.Classes);
        Assert.Equal("PublicClass", module.Classes[0].Name);

        // MacroEnum included
        Assert.Single(module.Enums);
        Assert.Equal("MacroEnum", module.Enums[0].Name);

        // Both loc-variant constants included (includedFrom points to our headers)
        Assert.Equal(2, module.Constants.Count);
    }

    [Fact]
    public void Parse_ExternalHeaderDecls_AreNotInheritedAsPublic()
    {
        // A declaration in an external dependency header must not be misclassified as public
        // just because an earlier declaration set currentFile to the framework's header path.
        //
        // This uses the shape clang actually emits. Verified directly against
        // `clang -Xclang -ast-dump=json`: loc.file is present on the FIRST declaration of every
        // file and re-emitted the moment the file changes (both entering an include and returning
        // from one), and omitted only while the file is unchanged; loc.includedFrom names the file
        // that included the declaration's file, never the declaration's own file. So a declaration
        // in Dep.h carries file = Dep.h, and only its FOLLOWING siblings omit it.
        var publicDecl = $$"""
        {
            "kind": "VarDecl",
            "name": "PublicConst",
            {{MakeLoc()}},
            "type": { "qualType": "int" }
        }
        """;

        // First declaration inside the dependency header: clang emits its file because the file
        // changed, and includedFrom = our framework header (the includer).
        var dependencyFirst = $$"""
        {
            "kind": "VarDecl",
            "name": "DependencyConstFirst",
            "loc": {
                "file": "/usr/include/external/Dep.h",
                "includedFrom": { "file": "{{HeadersPath}}/TestLib.h" }
            },
            "type": { "qualType": "int" }
        }
        """;

        // Second declaration in that same header: file omitted (unchanged), so it inherits
        // currentFile — which the first declaration just set to the external path.
        var dependencySecond = $$"""
        {
            "kind": "VarDecl",
            "name": "DependencyConstSecond",
            "loc": { "includedFrom": { "file": "{{HeadersPath}}/TestLib.h" } },
            "type": { "qualType": "int" }
        }
        """;

        var json = WrapInTranslationUnit($"{publicDecl},{dependencyFirst},{dependencySecond}");
        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Single(module.Constants);
        Assert.Equal("PublicConst", module.Constants[0].Name);
    }

    [Fact]
    public void Parse_IncludedFromOurHeaders_Accepted()
    {
        // A declaration with includedFrom pointing to our framework's headers
        // is a sub-header declaration and should be included.
        var subHeaderDecl = $$"""
        {
            "kind": "VarDecl",
            "name": "SubHeaderConst",
            "loc": { "includedFrom": { "file": "{{HeadersPath}}/TestLib.h" } },
            "type": { "qualType": "int" }
        }
        """;

        var json = WrapInTranslationUnit(subHeaderDecl);
        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Single(module.Constants);
        Assert.Equal("SubHeaderConst", module.Constants[0].Name);
    }

    [Fact]
    public void Parse_ForwardDeclaration_Skipped()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "ForwardOnly",
            {{MakeLoc()}}
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Empty(module.Classes);
    }

    // ──────────────────────────────────────────────
    // Pass 3: Dedup tests
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_DuplicateEnums_KeepsRichest()
    {
        // First has 0 cases (forward-like), second has 3 cases → keep second
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "MyEnum",
            {{MakeLoc()}}
        },
        {
            "kind": "EnumDecl",
            "name": "MyEnum",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                { "kind": "EnumConstantDecl", "name": "MyEnumA", "inner": [{ "kind": "ConstantExpr", "value": "0" }] },
                { "kind": "EnumConstantDecl", "name": "MyEnumB", "inner": [{ "kind": "ConstantExpr", "value": "1" }] },
                { "kind": "EnumConstantDecl", "name": "MyEnumC", "inner": [{ "kind": "ConstantExpr", "value": "2" }] }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Enums);
        Assert.Equal("MyEnum", module.Enums[0].Name);
        Assert.Equal(3, module.Enums[0].Cases.Count);
    }

    [Fact]
    public void Parse_DuplicateEnums_BothEmpty_KeepsFirst()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "EmptyEnum",
            {{MakeLoc()}}
        },
        {
            "kind": "EnumDecl",
            "name": "EmptyEnum",
            {{MakeLoc()}}
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Enums);
        Assert.Equal("EmptyEnum", module.Enums[0].Name);
    }

    [Fact]
    public void Parse_DuplicateStructs_KeepsRichest()
    {
        // First empty, second has 2 fields → keep second
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "RecordDecl",
            "name": "MyPoint",
            {{MakeLoc()}}
        },
        {
            "kind": "RecordDecl",
            "name": "MyPoint",
            {{MakeLoc()}},
            "inner": [
                { "kind": "FieldDecl", "name": "x", "type": { "qualType": "float" } },
                { "kind": "FieldDecl", "name": "y", "type": { "qualType": "float" } }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Structs);
        Assert.Equal("MyPoint", module.Structs[0].Name);
        Assert.Equal(2, module.Structs[0].Fields.Count);
    }

    [Fact]
    public void Parse_DuplicateConstants_KeepsFirst()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "VarDecl",
            "name": "MyConst",
            {{MakeLoc()}},
            "type": { "qualType": "NSString *" },
            "storageClass": "extern"
        },
        {
            "kind": "VarDecl",
            "name": "MyConst",
            {{MakeLoc()}},
            "type": { "qualType": "NSString *" },
            "storageClass": "extern"
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Constants);
        Assert.Equal("MyConst", module.Constants[0].Name);
    }

    [Fact]
    public void Parse_DuplicateFunctions_KeepsFirst()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "FunctionDecl",
            "name": "MyFunc",
            {{MakeLoc()}},
            "type": { "qualType": "void ()" },
            "inner": []
        },
        {
            "kind": "FunctionDecl",
            "name": "MyFunc",
            {{MakeLoc()}},
            "type": { "qualType": "void ()" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Functions);
        Assert.Equal("MyFunc", module.Functions[0].Name);
    }

    [Fact]
    public void Parse_DuplicateTypedefs_KeepsFirst()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "TypedefDecl",
            "name": "MyType",
            {{MakeLoc()}},
            "type": { "qualType": "int" },
            "inner": [{ "kind": "BuiltinType", "qualType": "int" }]
        },
        {
            "kind": "TypedefDecl",
            "name": "MyType",
            {{MakeLoc()}},
            "type": { "qualType": "int" },
            "inner": [{ "kind": "BuiltinType", "qualType": "int" }]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Typedefs);
        Assert.Equal("MyType", module.Typedefs[0].Name);
    }

    [Fact]
    public void Parse_DuplicateClasses_KeepsRichest()
    {
        // Same class in two headers — first empty, second has methods → keep second
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": []
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doStuff",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "title",
                    "type": { "qualType": "NSString *" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        Assert.Equal("Widget", module.Classes[0].Name);
        Assert.Single(module.Classes[0].Methods);
        Assert.Single(module.Classes[0].Properties);
    }

    [Fact]
    public void Parse_DuplicateProtocols_KeepsRichest()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCProtocolDecl",
            "name": "MyProto",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doIt",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCProtocolDecl",
            "name": "MyProto",
            {{MakeLoc()}},
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Protocols);
        Assert.Equal("MyProto", module.Protocols[0].Name);
        Assert.Single(module.Protocols[0].Methods);
    }

    [Fact]
    public void Parse_DuplicateClasses_CategoryMembersPreserved_WhenLaterDuplicateIsRicher()
    {
        // Regression: if category methods were only merged onto the first duplicate,
        // and a later duplicate had more inherent members, the "richest wins" dedup
        // would discard the category-bearing instance and lose category members.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doA",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doB",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doC",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "title",
                    "type": { "qualType": "NSString *" }
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "id": "0x1", "kind": "ObjCInterfaceDecl", "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "categoryMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "subtitle",
                    "type": { "qualType": "NSString *" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        var cls = module.Classes[0];
        // The winner must have the category members regardless of which duplicate won
        Assert.Contains(cls.Methods, m => m.Selector == "categoryMethod");
        Assert.Contains(cls.Properties, p => p.Name == "subtitle");
        Assert.True(cls.Methods.First(m => m.Selector == "categoryMethod").IsFromCategory);
        Assert.True(cls.Properties.First(p => p.Name == "subtitle").IsFromCategory);
    }

    [Fact]
    public void Parse_ObjCTypeParamDecl_ExtractsGenericTypeParamNames()
    {
        // Clang AST emits ObjCTypeParamDecl nodes for lightweight generics like
        // @interface MOSResults<MOSObjectType>
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MOSResults",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCTypeParamDecl",
                    "name": "MOSObjectType"
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "objectAtIndex:",
                    "instance": true,
                    "returnType": { "qualType": "MOSObjectType" },
                    "inner": [
                        {
                            "kind": "ParmVarDecl",
                            "name": "index",
                            "type": { "qualType": "NSUInteger" }
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        var cls = module.Classes[0];
        Assert.Single(cls.GenericTypeParamNames);
        Assert.Equal("MOSObjectType", cls.GenericTypeParamNames[0]);
    }

    [Fact]
    public void Parse_ObjCTypeParamDecl_MultipleGenericParams()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MOSDictionary",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                { "kind": "ObjCTypeParamDecl", "name": "MOSKeyType" },
                { "kind": "ObjCTypeParamDecl", "name": "MOSObjectType" },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var cls = module.Classes[0];
        Assert.Equal(2, cls.GenericTypeParamNames.Count);
        Assert.Contains("MOSKeyType", cls.GenericTypeParamNames);
        Assert.Contains("MOSObjectType", cls.GenericTypeParamNames);
    }

    [Fact]
    public void Parse_DuplicateClasses_MergesMetadata()
    {
        // Two duplicates of Widget: first has superclass and protocol A, second has protocol B
        // (and an AvailabilityAttr node with no recoverable source offset — its inline
        // platform/introduced JSON fields are NOT read; availability is recovered from header bytes,
        // see ClangAstParserAvailabilityTests.Merges_AvailabilityFromSparserDuplicate). Merge should
        // combine all the structural metadata regardless.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "UIView" },
            "protocols": [{ "name": "NSCoding" }],
            "inner": [
                { "kind": "ObjCTypeParamDecl", "name": "WidgetType" },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "UIView" },
            "protocols": [{ "name": "NSCopying" }],
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doA",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doB",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "AvailabilityAttr",
                    "platform": "ios",
                    "introduced": "15.0"
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        var cls = module.Classes[0];
        // Richest by member count (2 methods vs 1) wins as base
        Assert.Equal(2, cls.Methods.Count);
        // Superclass preserved
        Assert.Equal("UIView", cls.SuperclassName);
        // Both protocols merged
        Assert.Contains("NSCoding", cls.ProtocolNames);
        Assert.Contains("NSCopying", cls.ProtocolNames);
        // Generic type params merged from first duplicate
        Assert.Contains("WidgetType", cls.GenericTypeParamNames);
    }

    [Fact]
    public void Parse_DuplicateProtocols_MergesMetadata()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCProtocolDecl",
            "name": "MyProto",
            {{MakeLoc()}},
            "protocols": [{ "name": "NSCoding" }],
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doIt",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCProtocolDecl",
            "name": "MyProto",
            {{MakeLoc()}},
            "protocols": [{ "name": "NSCopying" }],
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "platform": "ios",
                    "introduced": "14.0"
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Protocols);
        var proto = module.Protocols[0];
        // Richest (1 method vs 0) wins as base
        Assert.Single(proto.Methods);
        // Both inherited protocols merged
        Assert.Contains("NSCoding", proto.InheritedProtocolNames);
        Assert.Contains("NSCopying", proto.InheritedProtocolNames);
    }

    [Fact]
    public void Parse_DuplicateClasses_SuperclassFromNonRichest_Preserved()
    {
        // Edge case: first duplicate has a superclass, second (richer) doesn't
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "UIView" },
            "inner": []
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doStuff",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        var cls = module.Classes[0];
        Assert.Single(cls.Methods);
        // Superclass from the non-richest duplicate is preserved
        Assert.Equal("UIView", cls.SuperclassName);
    }

    [Fact]
    public void Parse_NoDuplicates_Unchanged()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "EnumA",
            {{MakeLoc()}},
            "inner": [{ "kind": "EnumConstantDecl", "name": "A1" }]
        },
        {
            "kind": "EnumDecl",
            "name": "EnumB",
            {{MakeLoc()}},
            "inner": [{ "kind": "EnumConstantDecl", "name": "B1" }]
        },
        {
            "kind": "VarDecl",
            "name": "ConstA",
            {{MakeLoc()}},
            "type": { "qualType": "int" }
        },
        {
            "kind": "VarDecl",
            "name": "ConstB",
            {{MakeLoc()}},
            "type": { "qualType": "int" }
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Equal(2, module.Enums.Count);
        Assert.Equal(2, module.Constants.Count);
    }

    [Fact]
    public void Parse_FullModule_AllDeclTypes()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCProtocolDecl",
            "name": "MyProto",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doIt",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "EnumDecl",
            "name": "MyEnum",
            {{MakeLoc()}},
            "inner": [
                { "kind": "EnumConstantDecl", "name": "A" }
            ]
        },
        {
            "kind": "RecordDecl",
            "name": "MyStruct",
            {{MakeLoc()}},
            "inner": [
                { "kind": "FieldDecl", "name": "val", "type": { "qualType": "int" } }
            ]
        },
        {
            "kind": "FunctionDecl",
            "name": "MyFunc",
            {{MakeLoc()}},
            "type": { "qualType": "void ()" },
            "inner": []
        },
        {
            "kind": "VarDecl",
            "name": "MyConst",
            {{MakeLoc()}},
            "type": { "qualType": "int" },
            "storageClass": "extern"
        },
        {
            "kind": "TypedefDecl",
            "name": "MyType",
            {{MakeLoc()}},
            "type": { "qualType": "int" },
            "inner": [{ "kind": "BuiltinType", "qualType": "int" }]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Equal("TestLib", module.ModuleName);
        Assert.Single(module.Classes);
        Assert.Single(module.Protocols);
        Assert.Single(module.Enums);
        Assert.Single(module.Structs);
        Assert.Single(module.Functions);
        Assert.Single(module.Constants);
        Assert.Single(module.Typedefs);
        Assert.Equal(7, module.TotalDeclarations);
    }

    [Fact]
    public void Parse_TypedefDecl_AnonymousStruct_PromotesToStructs()
    {
        // typedef struct { CGFloat top; CGFloat left; } LabelPrinterMargins;
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "TypedefDecl",
            "name": "LabelPrinterMargins",
            {{MakeLoc()}},
            "type": { "qualType": "struct LabelPrinterMargins" },
            "inner": [
                {
                    "kind": "RecordDecl",
                    "inner": [
                        {
                            "kind": "FieldDecl",
                            "name": "top",
                            "type": { "qualType": "CGFloat" }
                        },
                        {
                            "kind": "FieldDecl",
                            "name": "left",
                            "type": { "qualType": "CGFloat" }
                        }
                    ]
                },
                {
                    "kind": "ElaboratedType",
                    "qualType": "struct LabelPrinterMargins"
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Structs);
        var s = module.Structs[0];
        Assert.Equal("LabelPrinterMargins", s.Name);
        Assert.Equal(2, s.Fields.Count);
        Assert.Equal("top", s.Fields[0].Name);
        Assert.Equal("left", s.Fields[1].Name);
    }

    [Fact]
    public void Parse_TypedefDecl_NoRecordDecl_NoPromotion()
    {
        // typedef NSString * LabelPrinterSerialNumber;
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "TypedefDecl",
            "name": "LabelPrinterSerialNumber",
            {{MakeLoc()}},
            "type": { "qualType": "NSString *" },
            "inner": [
                {
                    "kind": "ObjCObjectPointerType",
                    "qualType": "NSString *"
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Empty(module.Structs);
        Assert.Single(module.Typedefs);
        Assert.Equal("LabelPrinterSerialNumber", module.Typedefs[0].Name);
        // A plain NSString typedef (no NS_TYPED_ENUM) must NOT be flagged as a Swift newtype.
        Assert.False(module.Typedefs[0].IsSwiftNewType);
    }

    [Fact]
    public void Parse_TypedefDecl_WithSwiftNewTypeAttr_FlagsSwiftNewType()
    {
        // typedef NSString *MyAuthType NS_TYPED_EXTENSIBLE_ENUM NS_SWIFT_NAME(AuthType);
        // clang lowers NS_TYPED_ENUM / NS_TYPED_EXTENSIBLE_ENUM to the swift_wrapper attribute,
        // which -ast-dump=json emits as a SwiftNewTypeAttr child alongside the type node (and a
        // SwiftNameAttr for the NS_SWIFT_NAME). The parser must set IsSwiftNewType so the bridge
        // factory routes it to an ObjCBridgeable record.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "TypedefDecl",
            "name": "MyAuthType",
            {{MakeLoc()}},
            "type": { "qualType": "NSString *" },
            "inner": [
                { "kind": "ObjCObjectPointerType", "qualType": "NSString *" },
                { "kind": "SwiftNewTypeAttr" },
                { "kind": "SwiftNameAttr" }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var typedef = Assert.Single(module.Typedefs);
        Assert.Equal("MyAuthType", typedef.Name);
        Assert.True(typedef.IsSwiftNewType);
        Assert.Equal("NSString", typedef.UnderlyingType.Name);
        Assert.True(typedef.UnderlyingType.IsPointer);
    }

    // ──────────────────────────────────────────────
    // Category parsing tests
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_ObjCCategoryDecl_PreservesCategoryName()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doExtra",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // Category should be preserved on module
        Assert.Single(module.Categories);
        Assert.Equal("Extras", module.Categories[0].CategoryName);
        Assert.Equal("Widget", module.Categories[0].ClassName);
        Assert.Single(module.Categories[0].Methods);
        Assert.Equal("doExtra", module.Categories[0].Methods[0].Selector);

        // Category method should also be merged onto the class with IsFromCategory + CategoryName
        var cls = Assert.Single(module.Classes);
        var catMethod = cls.Methods.FirstOrDefault(m => m.Selector == "doExtra");
        Assert.NotNull(catMethod);
        Assert.True(catMethod.IsFromCategory);
        Assert.Equal("Extras", catMethod.CategoryName);
    }

    [Fact]
    public void Parse_UnnamedCategory_HasEmptyCategoryName()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            {{MakeLoc()}},
            "interface": { "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "version",
                    "type": { "qualType": "NSString *" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Single(module.Categories);
        Assert.Equal("", module.Categories[0].CategoryName);
        Assert.Equal("Widget", module.Categories[0].ClassName);
        Assert.Single(module.Categories[0].Properties);
    }

    [Fact]
    public void Parse_MultipleCategoriesOnSameClass_PreservesDistinctNames()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Alpha",
            {{MakeLoc()}},
            "interface": { "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "alphaMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Beta",
            {{MakeLoc()}},
            "interface": { "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "betaMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Equal(2, module.Categories.Count);
        Assert.Contains(module.Categories, c => c.CategoryName == "Alpha");
        Assert.Contains(module.Categories, c => c.CategoryName == "Beta");

        // Both methods should be merged onto the class
        var cls = Assert.Single(module.Classes);
        Assert.Equal(3, cls.Methods.Count); // init + alphaMethod + betaMethod
    }

    [Fact]
    public void Parse_CategoryWithProtocols_MergesOntoClass()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "protocols": [{ "name": "NSCoding" }],
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "name": "Widget" },
            "protocols": [{ "name": "NSSecureCoding" }],
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doExtra",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // Category protocols should be merged onto the class
        var cls = Assert.Single(module.Classes);
        Assert.Contains("NSCoding", cls.ProtocolNames);
        Assert.Contains("NSSecureCoding", cls.ProtocolNames);

        // Category should also preserve its own protocols
        var cat = Assert.Single(module.Categories);
        Assert.Contains("NSSecureCoding", cat.ProtocolNames);
    }

    [Fact]
    public void Parse_CategoryAvailabilityAttr_NoSourceOffset_RecoversNothing()
    {
        // A category's AvailabilityAttr node carrying no source offset (here: a bare loc, no
        // range.begin offset) recovers nothing — availability is recovered from the byte offset when
        // present (see ClangAstParserAvailabilityTests), but degrades cleanly when it isn't. The
        // category and its member still parse, and the category carries no availability.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "NewStuff",
            {{MakeLoc()}},
            "interface": { "name": "Widget" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    {{MakeLoc()}}
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "newMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        var cat = Assert.Single(module.Categories);
        Assert.Equal("NewStuff", cat.CategoryName);
        Assert.Contains(cat.Methods, m => m.Selector == "newMethod");
        // No source offset on the attr → no availability recovered.
        Assert.Empty(cat.Availability);
    }

    [Fact]
    public void Parse_DuplicateCategories_Merged()
    {
        // Same category appearing twice (e.g., via umbrella + direct header include)
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doExtra",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doMore",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doExtra",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // Duplicates should be merged — richest wins (2 methods)
        Assert.Single(module.Categories);
        Assert.Equal("Extras", module.Categories[0].CategoryName);
        Assert.Equal(2, module.Categories[0].Methods.Count);
    }

    [Fact]
    public void Parse_DuplicateCategories_DisjointMembers_AllMerged()
    {
        // Two duplicate categories with disjoint methods — both sets must survive merge
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "init",
                    "instance": true,
                    "returnType": { "qualType": "instancetype" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "methodA",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Extras",
            {{MakeLoc()}},
            "interface": { "name": "Widget" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "methodB",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Single(module.Categories);
        // Both disjoint methods must be present
        Assert.Equal(2, module.Categories[0].Methods.Count);
        Assert.Contains(module.Categories[0].Methods, m => m.Selector == "methodA");
        Assert.Contains(module.Categories[0].Methods, m => m.Selector == "methodB");
    }

    // ──────────────────────────────────────────────
    // ResolutionTypedefs — non-framework typedefs for resolution
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_TypedefFromSystemHeader_InResolutionTypedefsOnly()
    {
        // A typedef from a non-framework header should be in ResolutionTypedefs
        // but NOT in Typedefs (which are framework-local only).
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "TypedefDecl",
            "name": "system_alias_t",
            "loc": { "file": "/usr/include/sys/types.h" },
            "inner": [
                {
                    "kind": "BuiltinType",
                    "type": { "qualType": "unsigned int" }
                }
            ]
        },
        {
            "kind": "TypedefDecl",
            "name": "MyLocalAlias",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "BuiltinType",
                    "type": { "qualType": "int" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // Framework-local typedef should be in both lists
        Assert.Single(module.Typedefs);
        Assert.Equal("MyLocalAlias", module.Typedefs[0].Name);

        // ResolutionTypedefs should contain both framework-local and system typedefs
        Assert.NotNull(module.ResolutionTypedefs);
        Assert.Equal(2, module.ResolutionTypedefs.Count);
        Assert.Contains(module.ResolutionTypedefs, t => t.Name == "system_alias_t");
        Assert.Contains(module.ResolutionTypedefs, t => t.Name == "MyLocalAlias");

        // Framework-local typedef should come AFTER system typedef (last-write-wins precedence)
        var sysIdx = module.ResolutionTypedefs.FindIndex(t => t.Name == "system_alias_t");
        var localIdx = module.ResolutionTypedefs.FindIndex(t => t.Name == "MyLocalAlias");
        Assert.True(localIdx > sysIdx, "Framework-local typedefs must come after system typedefs for last-write-wins precedence");
    }

    [Fact]
    public void Parse_SystemTypedefDoesNotStealAnonymousStructFields()
    {
        // Scenario: framework-local anonymous RecordDecl, then a system-header TypedefDecl
        // intervenes, then the framework-local TypedefDecl that should promote the struct.
        // The system typedef must NOT consume lastAnonymousStructFields.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "RecordDecl",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "FieldDecl",
                    "name": "x",
                    "type": { "qualType": "int" }
                },
                {
                    "kind": "FieldDecl",
                    "name": "y",
                    "type": { "qualType": "int" }
                }
            ]
        },
        {
            "kind": "TypedefDecl",
            "name": "unrelated_system_type",
            "loc": { "file": "/usr/include/sys/types.h" },
            "inner": [
                {
                    "kind": "BuiltinType",
                    "type": { "qualType": "unsigned long" }
                }
            ]
        },
        {
            "kind": "TypedefDecl",
            "name": "MyPoint",
            {{MakeLoc()}},
            "type": { "qualType": "struct MyPoint" },
            "inner": [
                {
                    "kind": "ElaboratedType",
                    "type": { "qualType": "struct (unnamed)" },
                    "inner": [
                        {
                            "kind": "RecordType",
                            "type": { "qualType": "struct (unnamed)" }
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // The anonymous struct should be promoted via the framework-local typedef
        Assert.Single(module.Structs);
        Assert.Equal("MyPoint", module.Structs[0].Name);
        Assert.Equal(2, module.Structs[0].Fields.Count);
        Assert.Contains(module.Structs[0].Fields, f => f.Name == "x");
        Assert.Contains(module.Structs[0].Fields, f => f.Name == "y");
    }

    [Fact]
    public void Parse_FrameworkTypedefTakesPrecedenceOverSystemTypedef()
    {
        // When both system and framework headers define the same typedef name,
        // the framework-local definition should take precedence in resolution.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "TypedefDecl",
            "name": "MyAlias",
            "loc": { "file": "/usr/include/sys/types.h" },
            "inner": [
                {
                    "kind": "BuiltinType",
                    "type": { "qualType": "unsigned int" }
                }
            ]
        },
        {
            "kind": "TypedefDecl",
            "name": "MyAlias",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "BuiltinType",
                    "type": { "qualType": "int" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // ResolutionTypedefs: system first, framework-local second
        Assert.NotNull(module.ResolutionTypedefs);
        Assert.Equal(2, module.ResolutionTypedefs.Count);
        // The framework-local one (int) should be last, so it wins in dict assignment
        Assert.Equal("int", module.ResolutionTypedefs[1].UnderlyingType.Name);

        // BuildResolvedTypedefMap should resolve to the framework-local definition
        var typedefMap = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        var result = ObjCTypeMapper.MapType(
            new ObjCTypeRef { Name = "MyAlias" }, typedefMap: typedefMap);
        Assert.Equal("int", result);
    }

    [Fact]
    public void Parse_SdkHeaderIncludedByFrameworkHeader_NotFrameworkLocal()
    {
        // When a framework header #imports an SDK header, the SDK declarations have
        // includedFrom pointing to the framework header. But they should NOT be classified
        // as framework-local because currentFile was set to the SDK path by the first
        // declaration from that header.
        var sdkFirstDecl = """
        {
            "kind": "ObjCProtocolDecl",
            "name": "SdkDelegate",
            "loc": { "file": "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/SDKs/iPhoneSimulator.sdk/System/Library/Frameworks/SomeSDK.framework/Headers/SomeSDK.h" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "didFinish",
                    "returnType": { "qualType": "void" },
                    "instance": true
                }
            ]
        }
        """;

        // Second declaration from same SDK header — no loc.file, only includedFrom
        // pointing to our framework's header (the includer). Should NOT be framework-local.
        var sdkSecondDecl = $$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "SdkManager",
            "loc": { "includedFrom": { "file": "{{HeadersPath}}/TestLib.h" } },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "start",
                    "returnType": { "qualType": "void" },
                    "instance": true
                }
            ]
        }
        """;

        var json = WrapInTranslationUnit($"{sdkFirstDecl},{sdkSecondDecl}");
        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // Neither SDK declaration should be parsed as framework-local
        Assert.Empty(module.Classes);
        Assert.Empty(module.Protocols);
    }

    [Fact]
    public void Parse_AppleSdkTypeNamespaces_CollectsFromSdkHeaders()
    {
        // ObjC classes and protocols from Apple SDK headers should be collected into
        // AppleSdkTypeNamespaces for ApiDefinition type resolvability, each mapped to the .NET
        // namespace derived from its <Framework>.framework header provenance (UIKit here).
        var sdkClass = """
        {
            "kind": "ObjCInterfaceDecl",
            "name": "UIViewController",
            "loc": { "file": "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/SDKs/iPhoneSimulator.sdk/System/Library/Frameworks/UIKit.framework/Headers/UIViewController.h" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "viewDidLoad",
                    "returnType": { "qualType": "void" },
                    "instance": true
                }
            ]
        }
        """;

        var sdkProtocol = """
        {
            "kind": "ObjCProtocolDecl",
            "name": "UITableViewDelegate",
            "loc": { "file": "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/SDKs/iPhoneSimulator.sdk/System/Library/Frameworks/UIKit.framework/Headers/UITableView.h" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "didSelectRow",
                    "returnType": { "qualType": "void" },
                    "instance": true
                }
            ]
        }
        """;

        // Framework-local class that's NOT from SDK
        var localClass = $$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyWidget",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doStuff",
                    "returnType": { "qualType": "void" },
                    "instance": true
                }
            ]
        }
        """;

        var json = WrapInTranslationUnit($"{sdkClass},{sdkProtocol},{localClass}");
        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // SDK types NOT in framework classes/protocols (they're not framework-local)
        Assert.Single(module.Classes);
        Assert.Equal("MyWidget", module.Classes[0].Name);
        Assert.Empty(module.Protocols);

        // But they ARE in AppleSdkTypeNamespaces, mapped to their UIKit.framework provenance.
        Assert.NotNull(module.AppleSdkTypeNamespaces);
        Assert.True(module.AppleSdkTypeNamespaces!.TryGetValue("UIViewController", out var vcNs));
        Assert.Equal("UIKit", vcNs);
        Assert.True(module.AppleSdkTypeNamespaces.TryGetValue("UITableViewDelegate", out var delNs));
        Assert.Equal("UIKit", delNs);
        // Framework-local types are NOT in AppleSdkTypeNamespaces
        Assert.False(module.AppleSdkTypeNamespaces.ContainsKey("MyWidget"));
    }

    [Fact]
    public void Parse_AppleSdkTypeNamespaces_NotCollectedFromNonSdkPaths()
    {
        // ObjC types from non-SDK, non-framework paths should NOT be collected.
        var thirdPartyDecl = """
        {
            "kind": "ObjCInterfaceDecl",
            "name": "ThirdPartyWidget",
            "loc": { "file": "/Users/dev/libs/ThirdParty.framework/Headers/ThirdParty.h" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "show",
                    "returnType": { "qualType": "void" },
                    "instance": true
                }
            ]
        }
        """;

        var json = WrapInTranslationUnit(thirdPartyDecl);
        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Empty(module.Classes);
        // ThirdPartyWidget is NOT from an SDK path → not collected
        Assert.Null(module.AppleSdkTypeNamespaces);
    }

    [Fact]
    public void Parse_AppleSdkEnumNamespaces_CollectsFromSdkHeaders_SeparateFromTypeNamespaces()
    {
        // Apple SDK EnumDecl (NS_ENUM) names must land in AppleSdkEnumNamespaces (usings-only),
        // NOT AppleSdkTypeNamespaces (resolvability keys). MTLPixelFormat from Metal is the
        // rive-ios CS0246 canary: a struct field typed MTLPixelFormat needs `using Metal;`
        // without flipping ApiDefinition resolvability for the enum name.
        var sdkEnum = """
        {
            "kind": "EnumDecl",
            "name": "MTLPixelFormat",
            "loc": { "file": "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/SDKs/iPhoneSimulator.sdk/System/Library/Frameworks/Metal.framework/Headers/MTLPixelFormat.h" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "MTLPixelFormatInvalid",
                    "type": { "qualType": "MTLPixelFormat" }
                }
            ]
        }
        """;

        // Framework-local struct so the TU is not empty of local surface.
        var localStruct = $$"""
        {
            "kind": "RecordDecl",
            "name": "RiveRenderConfig",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "FieldDecl",
                    "name": "pixelFormat",
                    "type": { "qualType": "MTLPixelFormat" }
                }
            ]
        }
        """;

        var json = WrapInTranslationUnit($"{sdkEnum},{localStruct}");
        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Single(module.Structs);
        Assert.Equal("RiveRenderConfig", module.Structs[0].Name);

        // Enum channel: MTLPixelFormat → Metal
        Assert.NotNull(module.AppleSdkEnumNamespaces);
        Assert.True(module.AppleSdkEnumNamespaces!.TryGetValue("MTLPixelFormat", out var metalNs));
        Assert.Equal("Metal", metalNs);

        // Resolvability map must NOT contain the enum name (separate channel).
        Assert.True(
            module.AppleSdkTypeNamespaces is null
            || !module.AppleSdkTypeNamespaces.ContainsKey("MTLPixelFormat"),
            "MTLPixelFormat must not enter AppleSdkTypeNamespaces (resolvability).");
    }

    // --- NS_SWIFT_NAME capture ---

    [Fact]
    public void Parse_ClassWithSwiftNameAttr_CapturesSwiftName()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "TLMyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                { "kind": "SwiftNameAttr", "name": "MyClass" }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        Assert.Equal("MyClass", module.Classes[0].SwiftName);
    }

    [Fact]
    public void Parse_ClassWithoutSwiftNameAttr_HasNullSwiftName()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "TLMyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        Assert.Null(module.Classes[0].SwiftName);
    }

    [Fact]
    public void Parse_MethodWithSwiftNameAttr_CapturesSwiftName()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "performOperationWithData:completion:",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": [
                        { "kind": "SwiftNameAttr", "name": "perform(data:completion:)" }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        Assert.Single(module.Classes[0].Methods);
        Assert.Equal("perform(data:completion:)", module.Classes[0].Methods[0].SwiftName);
    }

    [Fact]
    public void Parse_PropertyWithSwiftNameAttr_CapturesSwiftName()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "isEnabled",
                    "type": { "qualType": "BOOL" },
                    "inner": [
                        { "kind": "SwiftNameAttr", "name": "isActive" }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        Assert.Single(module.Classes[0].Properties);
        Assert.Equal("isActive", module.Classes[0].Properties[0].SwiftName);
    }

    [Fact]
    public void Parse_EnumWithSwiftNameAttr_CapturesSwiftName()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "TLErrorCode",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                { "kind": "SwiftNameAttr", "name": "ErrorCode" },
                {
                    "kind": "EnumConstantDecl",
                    "name": "TLErrorCodeUnknown",
                    "inner": [{ "kind": "ConstantExpr", "value": "0" }]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Enums);
        Assert.Equal("ErrorCode", module.Enums[0].SwiftName);
    }

    // --- NS_REFINED_FOR_SWIFT capture ---

    [Fact]
    public void Parse_MethodWithSwiftPrivateAttr_SetsIsRefinedForSwift()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "internalMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": [
                        { "kind": "SwiftPrivateAttr" }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes[0].Methods);
        Assert.True(module.Classes[0].Methods[0].IsRefinedForSwift);
    }

    [Fact]
    public void Parse_MethodWithoutSwiftPrivateAttr_IsNotRefined()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "publicMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes[0].Methods);
        Assert.False(module.Classes[0].Methods[0].IsRefinedForSwift);
    }

    [Fact]
    public void Parse_PropertyWithSwiftPrivateAttr_SetsIsRefinedForSwift()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "internalProp",
                    "type": { "qualType": "NSString *" },
                    "inner": [
                        { "kind": "SwiftPrivateAttr" }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes[0].Properties);
        Assert.True(module.Classes[0].Properties[0].IsRefinedForSwift);
    }

    [Fact]
    public void Parse_PropertyWithoutSwiftPrivateAttr_IsNotRefined()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "publicProp",
                    "type": { "qualType": "NSString *" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes[0].Properties);
        Assert.False(module.Classes[0].Properties[0].IsRefinedForSwift);
    }

    // --- Doc comment capture ---

    [Fact]
    public void Parse_ClassWithFullComment_CapturesDocComment()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "DocumentedClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "FullComment",
                    "inner": [
                        {
                            "kind": "ParagraphComment",
                            "inner": [
                                { "kind": "TextComment", "text": " A well-documented class." }
                            ]
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        Assert.Equal("A well-documented class.", module.Classes[0].DocComment);
    }

    [Fact]
    public void Parse_MethodWithParamComments_CapturesDocParams()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doThingWithName:age:",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": [
                        {
                            "kind": "ParmVarDecl",
                            "name": "name",
                            "type": { "qualType": "NSString *" }
                        },
                        {
                            "kind": "ParmVarDecl",
                            "name": "age",
                            "type": { "qualType": "NSInteger" }
                        },
                        {
                            "kind": "FullComment",
                            "inner": [
                                {
                                    "kind": "ParagraphComment",
                                    "inner": [
                                        { "kind": "TextComment", "text": " Does a thing." }
                                    ]
                                },
                                {
                                    "kind": "ParamCommandComment",
                                    "param": "name",
                                    "inner": [
                                        {
                                            "kind": "ParagraphComment",
                                            "inner": [
                                                { "kind": "TextComment", "text": " The name to use." }
                                            ]
                                        }
                                    ]
                                },
                                {
                                    "kind": "ParamCommandComment",
                                    "param": "age",
                                    "inner": [
                                        {
                                            "kind": "ParagraphComment",
                                            "inner": [
                                                { "kind": "TextComment", "text": " The person's age." }
                                            ]
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var method = module.Classes[0].Methods[0];
        Assert.Equal("Does a thing.", method.DocComment);
        Assert.Equal(2, method.DocParams.Count);
        Assert.Equal("name", method.DocParams[0].Name);
        Assert.Equal("The name to use.", method.DocParams[0].Description);
        Assert.Equal("age", method.DocParams[1].Name);
    }

    [Fact]
    public void Parse_ClassWithoutComment_HasNullDocComment()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "PlainClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Null(module.Classes[0].DocComment);
    }

    [Fact]
    public void Parse_MethodWithBothSwiftNameAndSwiftPrivate_CapturesBoth()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "rawMethod",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": [
                        { "kind": "SwiftNameAttr", "name": "refinedMethod()" },
                        { "kind": "SwiftPrivateAttr" }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var method = module.Classes[0].Methods[0];
        Assert.Equal("refinedMethod()", method.SwiftName);
        Assert.True(method.IsRefinedForSwift);
    }

    // ──────────────────────────────────────────────
    // Enum value extraction tests
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_EnumDecl_NonSequentialValues_ExtractsAllValues()
    {
        // Simulates an enum like STDSErrorCode where values are non-sequential (204, 203)
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "ErrorCode",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "ErrorCodeAssertionFailed",
                    "inner": [
                        {
                            "kind": "ConstantExpr",
                            "value": "204",
                            "inner": [
                                { "kind": "IntegerLiteral", "value": "204" }
                            ]
                        }
                    ]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "ErrorCodeUnrecognizedID",
                    "inner": [
                        {
                            "kind": "ConstantExpr",
                            "value": "203",
                            "inner": [
                                { "kind": "IntegerLiteral", "value": "203" }
                            ]
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Enums);
        var e = module.Enums[0];
        Assert.Equal(2, e.Cases.Count);
        Assert.Equal(204L, e.Cases[0].Value);
        Assert.Equal(203L, e.Cases[1].Value);
    }

    [Fact]
    public void Parse_EnumDecl_HighValues_ExtractsCorrectly()
    {
        // Enum values starting at 20000
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "HighEnum",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "HighEnumBase",
                    "inner": [
                        { "kind": "ConstantExpr", "value": "20000" }
                    ]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "HighEnumNext",
                    "inner": [
                        { "kind": "ConstantExpr", "value": "20001" }
                    ]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "HighEnumGap",
                    "inner": [
                        { "kind": "ConstantExpr", "value": "20100" }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var e = module.Enums[0];
        Assert.Equal(3, e.Cases.Count);
        Assert.Equal(20000L, e.Cases[0].Value);
        Assert.Equal(20001L, e.Cases[1].Value);
        Assert.Equal(20100L, e.Cases[2].Value);
    }

    [Fact]
    public void Parse_EnumDecl_WithImplicitCastExpr_ExtractsValue()
    {
        // Some clang versions wrap ConstantExpr inside ImplicitCastExpr
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "CastEnum",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "CastEnumValue",
                    "inner": [
                        {
                            "kind": "ImplicitCastExpr",
                            "inner": [
                                {
                                    "kind": "ConstantExpr",
                                    "value": "42",
                                    "inner": [
                                        { "kind": "IntegerLiteral", "value": "42" }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var e = module.Enums[0];
        Assert.Single(e.Cases);
        Assert.Equal(42L, e.Cases[0].Value);
    }

    [Fact]
    public void Parse_EnumDecl_WithParenExpr_ExtractsValue()
    {
        // ParenExpr wrapping — e.g., from macro expansion: #define MY_VALUE (100)
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "ParenEnum",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "int" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "ParenEnumValue",
                    "inner": [
                        {
                            "kind": "ConstantExpr",
                            "value": "100",
                            "inner": [
                                {
                                    "kind": "ParenExpr",
                                    "inner": [
                                        { "kind": "IntegerLiteral", "value": "100" }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var e = module.Enums[0];
        Assert.Single(e.Cases);
        Assert.Equal(100L, e.Cases[0].Value);
    }

    [Fact]
    public void Parse_EnumDecl_WithGaps_ExtractsAllValues()
    {
        // HTTP-like status codes with gaps: 200, 201, 204, 302, 404
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "HTTPStatus",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "HTTPStatusOK",
                    "inner": [{ "kind": "ConstantExpr", "value": "200" }]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "HTTPStatusCreated",
                    "inner": [{ "kind": "ConstantExpr", "value": "201" }]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "HTTPStatusNoContent",
                    "inner": [{ "kind": "ConstantExpr", "value": "204" }]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "HTTPStatusFound",
                    "inner": [{ "kind": "ConstantExpr", "value": "302" }]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "HTTPStatusNotFound",
                    "inner": [{ "kind": "ConstantExpr", "value": "404" }]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var e = module.Enums[0];
        Assert.Equal(5, e.Cases.Count);
        Assert.Equal(200L, e.Cases[0].Value);
        Assert.Equal(201L, e.Cases[1].Value);
        Assert.Equal(204L, e.Cases[2].Value);
        Assert.Equal(302L, e.Cases[3].Value);
        Assert.Equal(404L, e.Cases[4].Value);
    }

    [Fact]
    public void Parse_EnumDecl_NegativeValues_Extracted()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "SignedEnum",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "SignedEnumError",
                    "inner": [{ "kind": "ConstantExpr", "value": "-1" }]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "SignedEnumNone",
                    "inner": [{ "kind": "ConstantExpr", "value": "0" }]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "SignedEnumOK",
                    "inner": [{ "kind": "ConstantExpr", "value": "1" }]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var e = module.Enums[0];
        Assert.Equal(3, e.Cases.Count);
        Assert.Equal(-1L, e.Cases[0].Value);
        Assert.Equal(0L, e.Cases[1].Value);
        Assert.Equal(1L, e.Cases[2].Value);
    }

    [Fact]
    public void Parse_EnumDecl_CStyleCastExpr_ExtractsValue()
    {
        // C-style casts: (int)42
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "CastStyleEnum",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "int" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "CastStyleEnumValue",
                    "inner": [
                        {
                            "kind": "ConstantExpr",
                            "value": "99",
                            "inner": [
                                {
                                    "kind": "CStyleCastExpr",
                                    "inner": [
                                        { "kind": "IntegerLiteral", "value": "99" }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var e = module.Enums[0];
        Assert.Single(e.Cases);
        Assert.Equal(99L, e.Cases[0].Value);
    }

    [Fact]
    public void Parse_EnumDecl_HighBitHexValue_PreservesBitPattern()
    {
        // High-bit hex value (exceeds long.MaxValue) — e.g., 0xFFFFFFFF80000000
        // Should preserve the bit pattern via unchecked cast to long.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "HighBitEnum",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "unsigned long long" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "HighBitEnumAll",
                    "inner": [
                        {
                            "kind": "ConstantExpr",
                            "value": "0xFFFFFFFFFFFFFFFF"
                        }
                    ]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "HighBitEnumHigh",
                    "inner": [
                        {
                            "kind": "ConstantExpr",
                            "value": "0x8000000000000000"
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var e = module.Enums[0];
        Assert.Equal(2, e.Cases.Count);
        // 0xFFFFFFFFFFFFFFFF as long is -1
        Assert.Equal(-1L, e.Cases[0].Value);
        // 0x8000000000000000 as long is long.MinValue
        Assert.Equal(long.MinValue, e.Cases[1].Value);
    }

    [Fact]
    public void Parse_EnumDecl_HexValue_NormalRange_StillWorks()
    {
        // Normal hex values (within long range) should still parse correctly
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "NormalHexEnum",
            {{MakeLoc()}},
            "fixedUnderlyingType": { "qualType": "unsigned int" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "NormalHexEnumA",
                    "inner": [
                        { "kind": "ConstantExpr", "value": "0xFF" }
                    ]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "NormalHexEnumB",
                    "inner": [
                        { "kind": "ConstantExpr", "value": "0x1A" }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var e = module.Enums[0];
        Assert.Equal(255L, e.Cases[0].Value);
        Assert.Equal(26L, e.Cases[1].Value);
    }

    // --- Fix #9a: Property memory semantic extraction ---

    [Theory]
    [InlineData("copy", ObjCMemorySemantic.Copy)]
    [InlineData("weak", ObjCMemorySemantic.Weak)]
    [InlineData("strong", ObjCMemorySemantic.Strong)]
    [InlineData("retain", ObjCMemorySemantic.Retain)]
    [InlineData("assign", ObjCMemorySemantic.Assign)]
    [InlineData("unsafe_unretained", ObjCMemorySemantic.UnsafeUnretained)]
    public void Parse_PropertyDecl_MemorySemantic_Extracted(string jsonAttr, ObjCMemorySemantic expected)
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "data",
                    "type": { "qualType": "NSData *" },
                    "{{jsonAttr}}": true
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes[0].Properties);
        Assert.Equal(expected, module.Classes[0].Properties[0].MemorySemantic);
    }

    [Fact]
    public void Parse_PropertyDecl_NoMemorySemantic_DefaultsToNone()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "count",
                    "type": { "qualType": "NSInteger" },
                    "readonly": true
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Equal(ObjCMemorySemantic.None, module.Classes[0].Properties[0].MemorySemantic);
    }

    [Fact]
    public void Parse_PropertyDecl_CopyWithCustomGetter_BothExtracted()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCPropertyDecl",
                    "name": "title",
                    "type": { "qualType": "NSString *" },
                    "copy": true,
                    "getter": "title"
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var prop = module.Classes[0].Properties[0];
        Assert.Equal(ObjCMemorySemantic.Copy, prop.MemorySemantic);
        Assert.Equal("title", prop.GetterSelector);
    }

    // --- DesignatedInitializer detection ---

    [Fact]
    public void Parse_MethodDecl_WithDesignatedInitializerAttr_SetsFlag()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "initWithName:age:",
                    {{MakeLoc()}},
                    "returnType": { "qualType": "instancetype" },
                    "instance": true,
                    "inner": [
                        {
                            "kind": "ParmVarDecl",
                            "name": "name",
                            "type": { "qualType": "NSString *" }
                        },
                        {
                            "kind": "ParmVarDecl",
                            "name": "age",
                            "type": { "qualType": "int" }
                        },
                        {
                            "kind": "ObjCDesignatedInitializerAttr"
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var method = module.Classes[0].Methods[0];
        Assert.True(method.IsDesignatedInitializer);
        Assert.Equal("initWithName:age:", method.Selector);
    }

    [Fact]
    public void Parse_MethodDecl_WithoutDesignatedInitializerAttr_FlagIsFalse()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MyClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "initWithFrame:",
                    {{MakeLoc()}},
                    "returnType": { "qualType": "instancetype" },
                    "instance": true,
                    "inner": [
                        {
                            "kind": "ParmVarDecl",
                            "name": "frame",
                            "type": { "qualType": "CGRect" }
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var method = module.Classes[0].Methods[0];
        Assert.False(method.IsDesignatedInitializer);
    }

    [Fact]
    public void Parse_DeprecatedSubclassAlias_DroppedFromClasses()
    {
        // Apple's MTR_DEPRECATED rename pattern: legacy spelling subclasses canonical, names differ
        // only by letter case. Both clang AST nodes look identical to the parser because
        // -ast-dump=json omits availability platform/deprecated fields entirely.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MTROTAFooParams",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": []
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MTROtaFooParams",
            {{MakeLoc()}},
            "super": { "name": "MTROTAFooParams" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);
        Assert.Equal("MTROTAFooParams", module.Classes[0].Name);
    }

    [Fact]
    public void Parse_DeprecatedSubclassAlias_CategoryOnDroppedClass_AlsoDropped()
    {
        // Categories targeting a dropped alias would emit [BaseType(typeof(MissingClass))] —
        // filter them alongside the class itself.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MTROTAFooParams",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": []
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MTROtaFooParams",
            {{MakeLoc()}},
            "super": { "name": "MTROTAFooParams" },
            "inner": []
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Deprecated",
            {{MakeLoc()}},
            "interface": { "name": "MTROtaFooParams" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Empty(module.Categories);
    }

    [Fact]
    public void Parse_SubclassWithDifferentName_NotDropped()
    {
        // Guard against false positives — a normal subclass whose name doesn't match the parent
        // case-insensitively must be preserved.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MTRBaseDevice",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": []
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MTRDevice",
            {{MakeLoc()}},
            "super": { "name": "MTRBaseDevice" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Equal(2, module.Classes.Count);
    }

    // --- IsAppleSdkPath: which header paths count as bindable Apple SDK types ---

    [Theory]
    // The real SDK lives under <Platform>.platform/Developer/SDKs/<Platform>.sdk — bindable.
    [InlineData("/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk/System/Library/Frameworks/Foundation.framework/Headers/NSObject.h", true)]
    [InlineData("/usr/include/objc/objc.h", true)]
    // The platform's Developer-tools frameworks (XCTest/Testing/XCUIAutomation) are NOT bindable:
    // Microsoft.iOS binds none of them. A binding that treated XCTestCase as resolvable would emit
    // [BaseType(typeof(XCTestCase))] → CS0246. These sit under Developer/Library/Frameworks.
    [InlineData("/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/Library/Frameworks/XCTest.framework/Headers/XCTest.h", false)]
    [InlineData("/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/Library/Frameworks/Testing.framework/Headers/Testing.h", false)]
    // The platform's clang builtins under Developer/usr are tooling, not bindable SDK types.
    [InlineData("/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/usr/lib/clang/include/stdint.h", false)]
    // A third-party header (not under any SDK path) is never an Apple SDK type.
    [InlineData("/Users/me/Build/Quick.framework/Headers/Quick.h", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsAppleSdkPath_ClassifiesPathsCorrectly(string filePath, bool expected)
    {
        Assert.Equal(expected, ClangAstParser.IsAppleSdkPath(filePath));
    }

    // ─────────────────────────────────────────────
    // Free-function linkage: only externally-linked functions are bindable.
    // A `static inline` (NS_INLINE) / non-extern `inline` definition emits no
    // standalone symbol, so a P/Invoke to it cannot link. The parser skips them.
    // ─────────────────────────────────────────────

    [Fact]
    public void Parse_StaticInlineFunction_IsSkipped()
    {
        // NS_INLINE expands to `static inline`: clang emits storageClass "static" + inline true.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "FunctionDecl",
            {{MakeLoc()}},
            "name": "MLNDegreesFromRadians",
            "storageClass": "static",
            "inline": true,
            "type": { "qualType": "double (double)" },
            "inner": [
                { "kind": "ParmVarDecl", "name": "radians", "type": { "qualType": "double" } }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Empty(module.Functions);
    }

    [Fact]
    public void Parse_PlainInlineFunction_IsSkipped()
    {
        // A non-`extern` `inline` definition (storageClass absent) likewise emits no standalone
        // symbol in this translation unit — there is nothing to bind.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "FunctionDecl",
            {{MakeLoc()}},
            "name": "TLPlainInline",
            "inline": true,
            "type": { "qualType": "int (int)" },
            "inner": [
                { "kind": "ParmVarDecl", "name": "x", "type": { "qualType": "int" } }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Empty(module.Functions);
    }

    [Fact]
    public void Parse_StaticNonInlineFunction_IsSkipped()
    {
        // A `static` (internal-linkage) function is file-local with no external symbol.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "FunctionDecl",
            {{MakeLoc()}},
            "name": "TLStaticHelper",
            "storageClass": "static",
            "type": { "qualType": "int (void)" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Empty(module.Functions);
    }

    [Fact]
    public void Parse_ExternFunction_IsKept()
    {
        // A normal extern (externally-linked) function has a real symbol and must still be bound.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "FunctionDecl",
            {{MakeLoc()}},
            "name": "MLNRealExport",
            "type": { "qualType": "double (double)" },
            "inner": [
                { "kind": "ParmVarDecl", "name": "x", "type": { "qualType": "double" } }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var func = Assert.Single(module.Functions);
        Assert.Equal("MLNRealExport", func.Name);
    }

    [Fact]
    public void Parse_ExternInlineFunction_IsKept()
    {
        // `extern inline` provides an external definition — it does emit a symbol, so it is kept.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "FunctionDecl",
            {{MakeLoc()}},
            "name": "TLExternInline",
            "storageClass": "extern",
            "inline": true,
            "type": { "qualType": "int (int)" },
            "inner": [
                { "kind": "ParmVarDecl", "name": "x", "type": { "qualType": "int" } }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var func = Assert.Single(module.Functions);
        Assert.Equal("TLExternInline", func.Name);
    }

    // ─────────────────────────────────────────────
    // @objc / Swift enum raw values: a Swift `@objc enum Foo: Int { case bar = 17009 }`
    // surfaces in the generated `-Swift.h` as a SWIFT_ENUM whose enumerator value tree is
    // `EnumConstantDecl → ImplicitCastExpr → ConstantExpr(17009) → IntegerLiteral(17009)`.
    // The explicit value must be preserved verbatim, never sequentially renumbered.
    // ─────────────────────────────────────────────

    [Fact]
    public void Parse_SwiftObjcEnum_PreservesExplicitRawValues()
    {
        // Faithful to a real clang AST dump of a SWIFT_ENUM(NSInteger, AuthErrorCode, closed):
        // each enumerator wraps its literal in an ImplicitCastExpr → ConstantExpr → IntegerLiteral.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            {{MakeLoc()}},
            "name": "AuthErrorCode",
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                { "kind": "EnumExtensibilityAttr" },
                {
                    "kind": "EnumConstantDecl",
                    "name": "AuthErrorCodeInvalidEmail",
                    "inner": [
                        { "kind": "ImplicitCastExpr", "inner": [
                            { "kind": "ConstantExpr", "value": "17008", "inner": [
                                { "kind": "IntegerLiteral", "value": "17008" } ] } ] }
                    ]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "AuthErrorCodeWrongPassword",
                    "inner": [
                        { "kind": "ImplicitCastExpr", "inner": [
                            { "kind": "ConstantExpr", "value": "17009", "inner": [
                                { "kind": "IntegerLiteral", "value": "17009" } ] } ] }
                    ]
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "AuthErrorCodeUserNotFound",
                    "inner": [
                        { "kind": "ImplicitCastExpr", "inner": [
                            { "kind": "ConstantExpr", "value": "17011", "inner": [
                                { "kind": "IntegerLiteral", "value": "17011" } ] } ] }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var enumDecl = Assert.Single(module.Enums);
        Assert.Equal(3, enumDecl.Cases.Count);
        Assert.Equal(17008, enumDecl.Cases[0].Value);
        Assert.Equal(17009, enumDecl.Cases[1].Value);
        Assert.Equal(17011, enumDecl.Cases[2].Value);
    }

    [Fact]
    public void Parse_SwiftObjcEnum_ForwardDeclFirst_KeepsValueBearingDefinition()
    {
        // `SWIFT_ENUM(NSInteger, AuthErrorCode, closed)` macro-expands to a forward declaration
        // (`enum AuthErrorCode : NSInteger AuthErrorCode;` — zero enumerators) immediately followed
        // by the real value-bearing definition. The empty forward decl appears FIRST in the AST;
        // dedup must keep the richer (value-bearing) decl, not collapse onto the empty one and lose
        // every raw value.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            {{MakeLoc()}},
            "name": "AuthErrorCode",
            "fixedUnderlyingType": { "qualType": "NSInteger" }
        },
        {
            "kind": "EnumDecl",
            {{MakeLoc()}},
            "name": "AuthErrorCode",
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                {
                    "kind": "EnumConstantDecl",
                    "name": "AuthErrorCodeWrongPassword",
                    "inner": [
                        { "kind": "ImplicitCastExpr", "inner": [
                            { "kind": "ConstantExpr", "value": "17009", "inner": [
                                { "kind": "IntegerLiteral", "value": "17009" } ] } ] }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        var enumDecl = Assert.Single(module.Enums);
        var wrongPassword = Assert.Single(enumDecl.Cases);
        Assert.Equal("AuthErrorCodeWrongPassword", wrongPassword.Name);
        Assert.Equal(17009, wrongPassword.Value);
    }

    // ──────────────────────────────────────────────
    // Location filtering — currentFile inheritance
    // ──────────────────────────────────────────────

    /// <summary>
    /// Clang omits <c>loc.file</c> when a declaration is in the same file as the previous one, so
    /// the tracked current file IS that declaration's file. The declaration still carries an
    /// <c>includedFrom</c> naming whatever pulled the header in — and when the framework's headers
    /// are read through a synthesized combined header (directory-umbrella and explicit-header
    /// modulemaps), that includer lives in a temp directory OUTSIDE the framework. Treating an
    /// out-of-framework includedFrom as a veto on inheritance drops every declaration in a header
    /// except its first, silently and with a zero exit code.
    /// </summary>
    [Fact]
    public void Parse_SecondDeclInHeader_WithOutOfFrameworkIncludedFrom_IsStillPublic()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "FirstClass",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": []
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "SecondClass",
            "loc": { "includedFrom": { "file": "/tmp/objc_binding_abc/TestLib_combined.h" } },
            "super": { "name": "NSObject" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Contains(module.Classes, c => c.Name == "FirstClass");
        Assert.Contains(module.Classes, c => c.Name == "SecondClass");
    }

    /// <summary>
    /// The case the includedFrom heuristic exists for, which the inheritance above must not
    /// reopen: a framework header #imports an SDK header, so the SDK's later declarations carry
    /// includedFrom = the framework header while the tracked current file is the SDK header.
    /// Those are not the framework's API and must stay dropped.
    /// </summary>
    [Fact]
    public void Parse_SdkDeclIncludedFromFrameworkHeader_IsNotPublic()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "SdkFirstClass",
            "loc": { "file": "/usr/include/Foundation/NSString.h" },
            "super": { "name": "NSObject" },
            "inner": []
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "SdkSecondClass",
            "loc": { "includedFrom": { "file": "/Frameworks/TestLib.framework/Headers/TestLib.h" } },
            "super": { "name": "NSObject" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.DoesNotContain(module.Classes, c => c.Name == "SdkFirstClass");
        Assert.DoesNotContain(module.Classes, c => c.Name == "SdkSecondClass");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Foreign-defined types: a name this framework's headers only RE-declare while
    // another module defines it must not become an empty [Protocol]/[BaseType] shell.
    // Two such shells (ours + the owning module's real binding) register the same
    // native name and the static registrar rejects the app.
    //
    // The shapes below are taken from the real FBSDKCoreKit 18.1.0 clang dump: clang
    // propagates a definition's attributes onto every later re-declaration, so
    // `@protocol FBSDKDataPersisting;` in FBSDKCoreKit's headers arrives as
    // `inner: [SwiftNameAttr]` — non-empty, and therefore a "definition" under the old
    // no-inner-children rule.
    // ──────────────────────────────────────────────────────────────────────────

    private const string ForeignHeader = "/Frameworks/OtherLib.framework/Headers/OtherLib.h";

    [Fact]
    public void Parse_ProtocolDefinedInAnotherFramework_LocalRedeclarationWithAttributeOnly_IsNotEmitted()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCProtocolDecl",
            "name": "DataPersisting",
            "loc": { "file": "{{ForeignHeader}}" },
            "inner": [
                { "kind": "SwiftNameAttr" },
                { "kind": "FullComment" },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "store",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCProtocolDecl",
            "name": "DataPersisting",
            {{MakeLoc()}},
            "inner": [ { "kind": "SwiftNameAttr" } ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        // The definition is foreign (dropped by the headers-path gate) and the local node is only a
        // re-declaration, so this framework declares the type not at all — rather than declaring an
        // empty shell that collides with OtherLib's binding.
        Assert.DoesNotContain(module.Protocols, p => p.Name == "DataPersisting");
    }

    [Fact]
    public void Parse_ClassDefinedInAnotherFramework_LocalForwardDeclaration_IsNotEmitted()
    {
        // A bare `@class CrashHandler;` carries `super: {"id":"0x0"}` — no name. The old rule read
        // the mere presence of a `super` key as proof of a definition.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "CrashHandler",
            "loc": { "file": "{{ForeignHeader}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "disable",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "CrashHandler",
            {{MakeLoc()}},
            "super": { "id": "0x0" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.DoesNotContain(module.Classes, c => c.Name == "CrashHandler");
    }

    [Fact]
    public void Parse_LocalDefinitionPlusAttributeOnlyRedeclaration_KeepsTheDefinition()
    {
        // The same clang shape, but the definition is OURS. Nothing may change: the real
        // declaration survives with all of its members.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCProtocolDecl",
            "name": "LocalProto",
            {{MakeLoc()}},
            "inner": [
                { "kind": "SwiftNameAttr" },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doIt",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCProtocolDecl",
            "name": "LocalProto",
            {{MakeLoc()}},
            "inner": [ { "kind": "SwiftNameAttr" } ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        var proto = Assert.Single(module.Protocols);
        Assert.Equal("LocalProto", proto.Name);
        Assert.Single(proto.Methods);
    }

    [Fact]
    public void Parse_AttributedEmptyMarkerProtocolWithNoDefinitionAnywhere_StillEmits()
    {
        // The byte-identity guarantee: with no richer declaration of the name anywhere in the TU
        // there is no evidence the type belongs to someone else, so the historical answer stands
        // and an attributed-but-memberless protocol is emitted exactly as before.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCProtocolDecl",
            "name": "MarkerProto",
            {{MakeLoc()}},
            "inner": [ { "kind": "SwiftNameAttr" } ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        var proto = Assert.Single(module.Protocols);
        Assert.Equal("MarkerProto", proto.Name);
        Assert.Empty(proto.Methods);
    }

    [Fact]
    public void Parse_EmptyDeclarationCarryingConformances_IsADefinition()
    {
        // `@protocol P <NSCopying> @end` has no members but does state conformances, which only a
        // definition can. It must not be mistaken for a re-declaration even when a richer node for
        // the same name exists elsewhere in the TU.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCProtocolDecl",
            "name": "ConformingProto",
            "loc": { "file": "{{ForeignHeader}}" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "somethingElse",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCProtocolDecl",
            "name": "ConformingProto",
            {{MakeLoc()}},
            "protocols": [{ "name": "NSCopying" }],
            "inner": [ { "kind": "SwiftNameAttr" } ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        var proto = Assert.Single(module.Protocols);
        Assert.Contains("NSCopying", proto.InheritedProtocolNames);
    }

    [Theory]
    // Attribute- and comment-only children are propagated onto re-declarations by clang and are
    // never evidence of a definition.
    [InlineData("""{ "kind": "ObjCProtocolDecl", "name": "P", "inner": [ { "kind": "SwiftNameAttr" } ] }""", false)]
    [InlineData("""{ "kind": "ObjCProtocolDecl", "name": "P", "inner": [ { "kind": "AvailabilityAttr" }, { "kind": "FullComment" } ] }""", false)]
    [InlineData("""{ "kind": "ObjCProtocolDecl", "name": "P", "inner": [] }""", false)]
    [InlineData("""{ "kind": "ObjCInterfaceDecl", "name": "C", "super": { "id": "0x0" } }""", false)]
    // Real definition evidence.
    [InlineData("""{ "kind": "ObjCProtocolDecl", "name": "P", "inner": [ { "kind": "ObjCMethodDecl" } ] }""", true)]
    [InlineData("""{ "kind": "ObjCProtocolDecl", "name": "P", "protocols": [ { "name": "NSCopying" } ] }""", true)]
    [InlineData("""{ "kind": "ObjCInterfaceDecl", "name": "C", "super": { "name": "NSObject" } }""", true)]
    [InlineData("""{ "kind": "ObjCInterfaceDecl", "name": "C", "inner": [ { "kind": "ObjCPropertyDecl" } ] }""", true)]
    public void HasDefinitionBody_DistinguishesDefinitionsFromRedeclarations(string nodeJson, bool expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(nodeJson);
        Assert.Equal(expected, ClangAstParser.HasDefinitionBody(doc.RootElement));
    }

    [Fact]
    public void ScanNamesWithRealDefinition_RecordsTheDefiningFile_AndIgnoresRedeclarations()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCProtocolDecl",
            "name": "Owned",
            "loc": { "file": "{{ForeignHeader}}" },
            "inner": [ { "kind": "ObjCMethodDecl" } ]
        },
        {
            "kind": "ObjCProtocolDecl",
            "name": "Owned",
            {{MakeLoc()}},
            "inner": [ { "kind": "SwiftNameAttr" } ]
        },
        {
            "kind": "ObjCProtocolDecl",
            "name": "NeverDefined",
            {{MakeLoc()}},
            "inner": []
        }
        """);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var files = new Dictionary<string, string>();
        var redeclared = new HashSet<string>();
        var names = ClangAstParser.ScanNamesWithRealDefinition(
            doc.RootElement.GetProperty("inner"), files, redeclared, DefaultHeadersPath);

        Assert.Contains("ObjCProtocolDecl|Owned", names);
        Assert.DoesNotContain("ObjCProtocolDecl|NeverDefined", names);
        Assert.Equal(ForeignHeader, files["ObjCProtocolDecl|Owned"]);

        // Only the re-declarations sitting under THIS framework's headers are collected — the
        // foreign definition itself is not, so a caller reporting "defined elsewhere" stays scoped
        // to what this framework actually mentioned.
        Assert.Contains("ObjCProtocolDecl|Owned", redeclared);
        Assert.Contains("ObjCProtocolDecl|NeverDefined", redeclared);
    }

    [Fact]
    public void Parse_ProtocolDefinitionDoesNotSuppressASameNamedClassForwardDeclaration()
    {
        // ObjC keeps classes and protocols in separate namespaces, and real SDKs use that:
        // FBSDKCoreKit ships `@protocol FBSDKAppLink` (defined) alongside `@class FBSDKAppLink;`
        // (forward only). Matching on the bare name would let the protocol's definition suppress the
        // class, silently deleting a type the old parser emitted.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCProtocolDecl",
            "name": "AppLink",
            {{MakeLoc()}},
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "sourceURL",
                    "instance": true,
                    "returnType": { "qualType": "NSURL *" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "AppLink",
            {{MakeLoc()}},
            "super": { "id": "0x0" },
            "inner": []
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.Contains(module.Protocols, p => p.Name == "AppLink");
        Assert.Contains(module.Classes, c => c.Name == "AppLink");
    }

    [Fact]
    public void Parse_CategoryOnAForeignDefinedClass_SurvivesAsAStandaloneCategory()
    {
        // The obvious worry about dropping the foreign-owned shell is that a category this framework
        // declares on that class loses its anchor and its members vanish. It does not: the category
        // stops being merged into a local class and instead stays in Categories, which
        // ObjCPipeline.FilterToForeignCategories keeps for standalone
        // [Category] [BaseType(typeof(Owner))] emission. Pinned so the routing is a decision.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "ForeignOwned",
            "loc": { "file": "{{ForeignHeader}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "real",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "ForeignOwned",
            {{MakeLoc()}},
            "super": { "id": "0x0" },
            "inner": []
        },
        {
            "kind": "ObjCCategoryDecl",
            "name": "Local",
            {{MakeLoc()}},
            "interface": { "id": "0x1", "kind": "ObjCInterfaceDecl", "name": "ForeignOwned" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "extra",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": []
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);

        Assert.DoesNotContain(module.Classes, c => c.Name == "ForeignOwned");
        var category = Assert.Single(module.Categories, c => c.ClassName == "ForeignOwned");
        Assert.Contains(category.Methods, m => m.Selector == "extra");
    }
}
