// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

public class ClangAstParserTests
{
    private const string HeadersPath = "/Frameworks/TestLib.framework/Headers";

    private static string WrapInTranslationUnit(string innerJson)
    {
        return $$"""
        {
            "kind": "TranslationUnitDecl",
            "inner": [{{innerJson}}]
        }
        """;
    }

    private static string MakeLoc(string file = "/Frameworks/TestLib.framework/Headers/TestLib.h")
    {
        return $"\"loc\": {{ \"file\": \"{file}\" }}";
    }

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
    public void Parse_AvailabilityAttr_OnClass()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "NewWidget",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "platform": "ios",
                    "introduced": "15.0"
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doStuff",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": [
                        {
                            "kind": "AvailabilityAttr",
                            "platform": "ios",
                            "introduced": "16.0",
                            "deprecated": "18.0"
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Classes);

        // Class availability
        Assert.Single(module.Classes[0].Availability);
        Assert.Equal("15.0", module.Classes[0].Availability[0].IntroducedVersion);

        // Method availability (nested inside class→method inner[])
        var method = module.Classes[0].Methods[0];
        Assert.Single(method.Availability);
        Assert.Equal("16.0", method.Availability[0].IntroducedVersion);
        Assert.Equal("18.0", method.Availability[0].DeprecatedVersion);
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

        // Declaration with includedFrom (should be included)
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

        // Both loc-variant constants included
        Assert.Equal(2, module.Constants.Count);
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
        // @interface RLMResults<RLMObjectType>
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "RLMResults",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCTypeParamDecl",
                    "name": "RLMObjectType"
                },
                {
                    "kind": "ObjCMethodDecl",
                    "name": "objectAtIndex:",
                    "instance": true,
                    "returnType": { "qualType": "RLMObjectType" },
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
        Assert.Equal("RLMObjectType", cls.GenericTypeParamNames[0]);
    }

    [Fact]
    public void Parse_ObjCTypeParamDecl_MultipleGenericParams()
    {
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "RLMDictionary",
            {{MakeLoc()}},
            "super": { "name": "NSObject" },
            "inner": [
                { "kind": "ObjCTypeParamDecl", "name": "RLMKeyType" },
                { "kind": "ObjCTypeParamDecl", "name": "RLMObjectType" },
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
        Assert.Contains("RLMKeyType", cls.GenericTypeParamNames);
        Assert.Contains("RLMObjectType", cls.GenericTypeParamNames);
    }

    [Fact]
    public void Parse_DuplicateClasses_MergesMetadata()
    {
        // Two duplicates of Widget: first has superclass and protocol A,
        // second has protocol B and availability. Merge should combine all metadata.
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
        // Availability merged from second duplicate
        Assert.Contains(cls.Availability, a => a.IntroducedVersion == "15.0");
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
        // Availability merged
        Assert.Contains(proto.Availability, a => a.IntroducedVersion == "14.0");
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
        // typedef struct { CGFloat top; CGFloat left; } BRLMMargins;
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "TypedefDecl",
            "name": "BRLMMargins",
            {{MakeLoc()}},
            "type": { "qualType": "struct BRLMMargins" },
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
                    "qualType": "struct BRLMMargins"
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", HeadersPath);
        Assert.Single(module.Structs);
        var s = module.Structs[0];
        Assert.Equal("BRLMMargins", s.Name);
        Assert.Equal(2, s.Fields.Count);
        Assert.Equal("top", s.Fields[0].Name);
        Assert.Equal("left", s.Fields[1].Name);
    }

    [Fact]
    public void Parse_TypedefDecl_NoRecordDecl_NoPromotion()
    {
        // typedef NSString * BRLMSerialNumber;
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "TypedefDecl",
            "name": "BRLMSerialNumber",
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
        Assert.Equal("BRLMSerialNumber", module.Typedefs[0].Name);
    }
}
